using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Overlay.Pricing;

/// <summary>
/// Result of a price lookup. <see cref="Exalted"/> is the item's value in Exalted Orbs (PoE2's base
/// economy unit on poe2scout). <see cref="Quantity"/> is the listing volume — a confidence signal:
/// low-volume rows are often mislisted (a 1-listing leveling unique priced at 100k ex).
/// </summary>
public readonly record struct PriceResult(string Name, double Exalted, int Quantity, string Category)
{
    public bool LowConfidence(int minQty) => Quantity < minQty;
}

/// <summary>
/// Centralized price source, ported in spirit from the user's PoE1 NinjaPriceService but built around
/// the validated poe2scout PoE2 schema. Fetches the current league's currency + unique prices (all in
/// Exalted), indexes them by normalized name AND by 2D-art basename (the bridge to in-game item reads:
/// a dropped item's RenderItem .dds basename equals poe2scout's IconUrl basename), caches to disk, and
/// refreshes on a TTL. Reads are lock-free off volatile snapshots; the whole index is swapped atomically.
///
/// <para>This is the recyclable data source: ground-loot valuation, expedition choices, ritual/vendor
/// overlays all consume <see cref="TryByArt"/> / <see cref="TryByName"/>.</para>
/// </summary>
public sealed class PriceBook
{
    // poe2scout unique categories (have IconUrl → art basename) and flat currency categories (by name).
    private static readonly string[] UniqueCategories =
        { "weapon", "armour", "accessory", "flask", "jewel", "sanctum" };
    private static readonly string[] CurrencyCategories =
        { "currency", "runes", "expedition", "breach", "ritual", "essences", "fragments", "delirium", "ultimatum", "abyss", "expedition" };

    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _cachePath;
    private readonly object _gate = new();

    // Atomically-swapped snapshots (volatile; lock-free reads on the tick/render thread).
    private volatile Dictionary<string, PricedItem> _byArt = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, PricedItem> _byName = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _fetching;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private string _league = "";
    private string? _leagueOverride;

    public double ExPerDivine { get; private set; } = 1;
    public double ExPerChaos { get; private set; } = 1;
    public bool IsLoaded => _byName.Count > 0 || _byArt.Count > 0;
    public int ItemCount => _byArt.Count + _byName.Count;
    public string League => _league;
    public string Status { get; private set; } = "not started";
    public DateTime LastFetchUtc => _lastFetchUtc;
    public int RefreshIntervalMinutes { get; set; } = 30;

    private sealed record PricedItem(string Name, double Exalted, int Quantity, string Category);

    public PriceBook(string cachePath, string? leagueOverride = null)
    {
        _cachePath = cachePath;
        _leagueOverride = string.IsNullOrWhiteSpace(leagueOverride) ? null : leagueOverride.Trim();
        TryLoadCache();
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("POE2Radar-PriceBook");
        return c;
    }

    /// <summary>Set/clear a manual league (e.g. from settings); triggers a refresh if it changed.</summary>
    public void SetLeagueOverride(string? league)
    {
        var v = string.IsNullOrWhiteSpace(league) ? null : league.Trim();
        if (v == _leagueOverride) return;
        _leagueOverride = v;
        _lastFetchUtc = DateTime.MinValue; // force the next RefreshIfDue to re-fetch
    }

    /// <summary>Call periodically (cheap when not due). Kicks a background fetch when stale and not already running.</summary>
    public void RefreshIfDue()
    {
        if (_fetching) return;
        if (DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromMinutes(RefreshIntervalMinutes)) return;
        _fetching = true;
        _ = Task.Run(FetchAsync);
    }

    public void ForceRefresh()
    {
        if (_fetching) return;
        _fetching = true;
        _ = Task.Run(FetchAsync);
    }

    /// <summary>Look up a unique by its 2D-art basename (e.g. "Earthbound") — the in-game item read key.</summary>
    public PriceResult? TryByArt(string? artBasename)
    {
        if (string.IsNullOrWhiteSpace(artBasename)) return null;
        return _byArt.TryGetValue(artBasename.Trim(), out var p)
            ? new PriceResult(p.Name, p.Exalted, p.Quantity, p.Category) : null;
    }

    /// <summary>Look up any priced item (unique or currency) by display name.</summary>
    public PriceResult? TryByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _byName.TryGetValue(Normalize(name), out var p)
            ? new PriceResult(p.Name, p.Exalted, p.Quantity, p.Category) : null;
    }

    /// <summary>Convert an Exalted value to a short display string in the largest sensible unit.</summary>
    public string Format(double ex)
    {
        if (ExPerDivine > 1 && ex >= ExPerDivine) return $"{ex / ExPerDivine:0.##} div";
        return $"{ex:0.##} ex";
    }

    // ── fetch ────────────────────────────────────────────────────────────────

    private async Task FetchAsync()
    {
        try
        {
            Status = "fetching…";
            var league = await ResolveLeagueAndRatesAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(league)) { Status = "no league"; return; }

            var byArt = new Dictionary<string, PricedItem>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, PricedItem>(StringComparer.OrdinalIgnoreCase);
            var lg = Uri.EscapeDataString(league);

            foreach (var cat in CurrencyCategories.Distinct())
                await FetchCurrencyCategoryAsync(lg, cat, byName).ConfigureAwait(false);
            foreach (var cat in UniqueCategories.Distinct())
                await FetchUniqueCategoryAsync(lg, cat, byArt, byName).ConfigureAwait(false);

            _byArt = byArt;
            _byName = byName;
            _league = league;
            _lastFetchUtc = DateTime.UtcNow;
            Status = $"loaded {byArt.Count} uniques (by art) + {byName.Count} by name for '{league}'";
            SaveCache();
        }
        catch (Exception ex) { Status = $"fetch failed: {ex.Message}"; }
        finally { _fetching = false; }
    }

    private async Task<string> ResolveLeagueAndRatesAsync()
    {
        try
        {
            var json = await Http.GetStringAsync("https://poe2scout.com/api/poe2/Leagues").ConfigureAwait(false);
            var leagues = JsonSerializer.Deserialize<List<ScoutLeague>>(json, Json) ?? new();
            ScoutLeague? pick = _leagueOverride != null
                ? leagues.FirstOrDefault(l => string.Equals(l.Value, _leagueOverride, StringComparison.OrdinalIgnoreCase))
                : leagues.FirstOrDefault(l => l.IsCurrent && !l.Value.StartsWith("HC", StringComparison.OrdinalIgnoreCase))
                  ?? leagues.FirstOrDefault(l => l.IsCurrent);
            pick ??= leagues.FirstOrDefault();
            if (pick == null) return "";
            if (pick.DivinePrice > 0) ExPerDivine = pick.DivinePrice;          // ex per 1 divine
            if (pick.ChaosDivinePrice > 0 && pick.DivinePrice > 0)
                ExPerChaos = pick.DivinePrice / pick.ChaosDivinePrice;          // ex per 1 chaos
            return pick.Value;
        }
        catch { return _leagueOverride ?? ""; }
    }

    private async Task FetchUniqueCategoryAsync(string leagueEscaped, string category,
        Dictionary<string, PricedItem> byArt, Dictionary<string, PricedItem> byName)
    {
        var page = 1; var pages = 1;
        while (page <= pages && page <= 20)
        {
            try
            {
                var url = $"https://poe2scout.com/api/poe2/Leagues/{leagueEscaped}/Uniques/ByCategory" +
                          $"?Category={category}&ReferenceCurrency=exalted&PerPage=250&Page={page}";
                var json = await Http.GetStringAsync(url).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<ScoutPage<ScoutUnique>>(json, Json);
                if (data?.Items == null) break;
                pages = data.Pages <= 0 ? 1 : data.Pages;

                foreach (var it in data.Items)
                {
                    var price = it.CurrentPrice ?? 0;
                    if (price <= 0 || string.IsNullOrWhiteSpace(it.Name)) continue;
                    var item = new PricedItem(it.Name.Trim(), price, it.CurrentQuantity ?? 0, category);
                    var art = ArtBasenameFromIcon(it.IconUrl);
                    if (art != null) Upsert(byArt, art, item);     // key = art basename
                    Upsert(byName, Normalize(it.Name), item);      // key = normalized name
                }
            }
            catch { break; } // a 404 / missing category is fine — skip it
            page++;
        }
    }

    private async Task FetchCurrencyCategoryAsync(string leagueEscaped, string category, Dictionary<string, PricedItem> byName)
    {
        var page = 1; var pages = 1;
        while (page <= pages && page <= 20)
        {
            try
            {
                var url = $"https://poe2scout.com/api/poe2/Leagues/{leagueEscaped}/Currencies/ByCategory" +
                          $"?Category={category}&ReferenceCurrency=exalted&PerPage=250&Page={page}";
                var json = await Http.GetStringAsync(url).ConfigureAwait(false);
                var data = JsonSerializer.Deserialize<ScoutPage<ScoutCurrency>>(json, Json);
                if (data?.Items == null) break;
                pages = data.Pages <= 0 ? 1 : data.Pages;

                foreach (var it in data.Items)
                {
                    var price = it.CurrentPrice ?? 0;
                    var name = it.Text;
                    if (price <= 0 || string.IsNullOrWhiteSpace(name)) continue;
                    Upsert(byName, Normalize(name), new PricedItem(name.Trim(), price, it.CurrentQuantity ?? 0, category));
                }
            }
            catch { break; }
            page++;
        }
    }

    // Keep the higher-value listing on a key collision (shared art across variants → show the best).
    private static void Upsert(Dictionary<string, PricedItem> map, string key, PricedItem item)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!map.TryGetValue(key, out var cur) || item.Exalted > cur.Exalted) map[key] = item;
    }

    /// <summary>poe2scout IconUrl → basename. ".../<hash>/Earthbound.png" → "Earthbound".</summary>
    private static string? ArtBasenameFromIcon(string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return null;
        var noQuery = iconUrl.Split('?')[0];
        var seg = noQuery.Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(seg)) return null;
        var dot = seg.LastIndexOf('.');
        var name = dot > 0 ? seg[..dot] : seg;
        return name.Length >= 2 ? name : null;
    }

    private static string Normalize(string s) => s.Trim();

    // ── disk cache ─────────────────────────────────────────────────────────────

    private sealed class CacheDto
    {
        public string League { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public double ExPerDivine { get; set; }
        public double ExPerChaos { get; set; }
        public Dictionary<string, PricedItem> ByArt { get; set; } = new();
        public Dictionary<string, PricedItem> ByName { get; set; } = new();
    }

    private void TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) { Status = "no cache; will fetch"; return; }
            var dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(_cachePath), Json);
            if (dto == null) return;
            // Honor a configured league: a cache for a different league shouldn't be used.
            if (_leagueOverride != null && !string.Equals(dto.League, _leagueOverride, StringComparison.OrdinalIgnoreCase)) return;
            _byArt = new Dictionary<string, PricedItem>(dto.ByArt, StringComparer.OrdinalIgnoreCase);
            _byName = new Dictionary<string, PricedItem>(dto.ByName, StringComparer.OrdinalIgnoreCase);
            _league = dto.League;
            _lastFetchUtc = dto.FetchedUtc;
            if (dto.ExPerDivine > 0) ExPerDivine = dto.ExPerDivine;
            if (dto.ExPerChaos > 0) ExPerChaos = dto.ExPerChaos;
            Status = $"cache: {ItemCount} entries for '{_league}'";
        }
        catch (Exception ex) { Status = $"cache load failed: {ex.Message}"; }
    }

    private void SaveCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var dto = new CacheDto
            {
                League = _league, FetchedUtc = _lastFetchUtc, ExPerDivine = ExPerDivine, ExPerChaos = ExPerChaos,
                ByArt = new(_byArt), ByName = new(_byName),
            };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(dto, Json));
        }
        catch { }
    }

    // ── poe2scout DTOs ──────────────────────────────────────────────────────────
    private sealed class ScoutLeague
    {
        public string Value { get; set; } = "";
        public bool IsCurrent { get; set; }
        public double DivinePrice { get; set; }       // ex per divine
        public double ChaosDivinePrice { get; set; }   // chaos per divine
    }
    private sealed class ScoutPage<T> { public int Pages { get; set; } public List<T> Items { get; set; } = new(); }
    private sealed class ScoutUnique
    {
        public string Name { get; set; } = "";
        public string? Type { get; set; }
        public double? CurrentPrice { get; set; }
        public int? CurrentQuantity { get; set; }
        public string? IconUrl { get; set; }
    }
    private sealed class ScoutCurrency
    {
        public string Text { get; set; } = "";
        public double? CurrentPrice { get; set; }
        public int? CurrentQuantity { get; set; }
        public string? IconUrl { get; set; }
    }
}
