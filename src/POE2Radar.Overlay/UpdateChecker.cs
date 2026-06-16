using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Overlay;

/// <summary>
/// One-shot "am I out of date?" check against GitHub. Best-effort and non-blocking: never throws into the
/// caller — on any failure (offline, rate-limited, parse error) it just reports "no update known".
///
/// Checks TWO repos:
///   • <see cref="Repo"/> — THIS fork's own releases. Drives <see cref="Result.UpdateAvailable"/> (the
///     primary "grab a newer build" banner), since that's where our release artifacts live.
///   • <see cref="UpstreamRepo"/> — the parent we forked from. We ALSO compare its latest version so we
///     know when upstream has pulled ahead (<see cref="Result.UpstreamAhead"/>) and the fork is behind —
///     a cue to run the upstream-sync integration again. Informational only (no download artifact for us).
/// Each repo is fetched independently, so one being down/rate-limited never suppresses the other.
/// Both are surfaced in the console banner + the dashboard.
/// </summary>
internal static class UpdateChecker
{
    // PRIMARY: our fork's own releases (where our build artifacts are published).
    private const string Repo = "FatherOfTwo123/POE2R";
    // UPSTREAM: the parent fork — checked so we're warned when Sikaka ships something we haven't merged.
    private const string UpstreamRepo = "Sikaka/POE2Radar";
    public static readonly string ReleasesPage = $"https://github.com/{Repo}/releases";
    public static readonly string UpstreamReleasesPage = $"https://github.com/{UpstreamRepo}/releases";

    /// <summary>This build's version ("0.7.0"), from the assembly version baked in by the csproj.</summary>
    public static string Current
    {
        get { var v = Assembly.GetExecutingAssembly().GetName().Version; return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}"; }
    }

    /// <param name="UpstreamLatest">Upstream's latest version, or null if unknown.</param>
    /// <param name="UpstreamAhead">True when upstream's latest is newer than this build (fork is behind upstream).</param>
    /// <param name="UpstreamUrl">Link to upstream's releases page (for the "behind upstream" notice).</param>
    public sealed record Result(
        string Current, string? Latest, bool UpdateAvailable, string Url,
        string? UpstreamLatest = null, bool UpstreamAhead = false, string UpstreamUrl = "");

    /// <summary>Check GitHub for a newer version (fork) + whether upstream has pulled ahead. Always returns
    /// a Result (never throws).</summary>
    public static async Task<Result> CheckAsync()
    {
        var current = Current;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("POE2Radar-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            // Primary: the fork's own latest release/tag drives "update available".
            var (latest, url) = await LatestVersion(http, Repo, ReleasesPage);
            var available = Parse(latest) is { } lv && Cmp(lv, Parse(current)!) > 0;

            // Informational: upstream's latest. "Behind upstream" = upstream newer than THIS build's version
            // (the fork tracks upstream's version line at parity, so a direct compare to Current is right).
            var (upLatest, upUrl) = await LatestVersion(http, UpstreamRepo, UpstreamReleasesPage);
            var upstreamAhead = Parse(upLatest) is { } uv && Cmp(uv, Parse(current)!) > 0;

            return new Result(current, latest, available, url, upLatest, upstreamAhead, upUrl);
        }
        catch
        {
            return new Result(current, null, false, ReleasesPage);
        }
    }

    /// <summary>The latest published version for <paramref name="repo"/> (the latest Release's tag, falling
    /// back to the highest semver tag if there are no formal Releases) plus a link. Best-effort: returns
    /// (null, <paramref name="fallbackUrl"/>) on any failure, so one repo being down/rate-limited never
    /// breaks the other check or throws.</summary>
    private static async Task<(string? latest, string url)> LatestVersion(HttpClient http, string repo, string fallbackUrl)
    {
        try
        {
            var rel = await http.GetAsync($"https://api.github.com/repos/{repo}/releases/latest");
            if (rel.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await rel.Content.ReadAsStringAsync());
                var latest = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                var url = doc.RootElement.TryGetProperty("html_url", out var h) && h.GetString() is { Length: > 0 } hu ? hu : fallbackUrl;
                return (latest, url);
            }
            // No formal Releases — fall back to the tag list and pick the highest semver.
            using var tdoc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{repo}/tags"));
            int[] best = { -1, 0, 0 };
            string? bestName = null;
            foreach (var tag in tdoc.RootElement.EnumerateArray())
            {
                var name = tag.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (Parse(name) is { } v && Cmp(v, best) > 0) { best = v; bestName = name; }
            }
            return (bestName, fallbackUrl);
        }
        catch { return (null, fallbackUrl); }
    }

    /// <summary>Parse "vX.Y.Z" / "X.Y.Z" → [major,minor,patch]; null if not a version string.</summary>
    private static int[]? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().TrimStart('v', 'V');
        var parts = s.Split('.', '-');
        var v = new int[3];
        for (var i = 0; i < 3; i++) { if (i >= parts.Length || !int.TryParse(parts[i], out v[i])) { if (i == 0) return null; v[i] = 0; } }
        return v;
    }

    private static int Cmp(int[] a, int[] b)
    {
        for (var i = 0; i < 3; i++) if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }
}
