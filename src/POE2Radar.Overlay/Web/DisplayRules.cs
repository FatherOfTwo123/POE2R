using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// One row of the unified display ruleset. A rule is a MATCHER (every set condition must hold;
/// an empty list / null field means "any") plus an ACTION (hide, or draw with a style/label/HP-bar).
/// The ruleset is an ORDERED list evaluated top-down per entity — the first enabled rule that matches
/// decides the entity's fate, so precedence is explicit (list order) and the old watched-vs-mechanic-
/// vs-category conflicts are impossible by construction. Mutable + JSON-serialized (object-initializer
/// shape, like <see cref="MechanicStyle"/>); treated as immutable once in a snapshot.
/// </summary>
public sealed class DisplayRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";

    // ── Matcher (unset = "any") ──
    public List<string> Categories { get; set; } = new();   // EntityCategory names; empty = any
    public List<string> Match { get; set; } = new();        // metadata terms (substring, or glob if it has * / ?); ANY-of; empty = any
    public List<string> Mods { get; set; } = new();         // monster affix-mod terms (e.g. "Aura"); ANY-of vs the entity's mod ids; empty = any
    public string? Rarity { get; set; }                     // Normal | Magic | Rare | Unique
    public string? Reaction { get; set; }                   // Hostile | Friendly
    public string? Life { get; set; }                       // Alive | Dead
    public string? Chest { get; set; }                      // Opened | Unopened
    public string? Poi { get; set; }                        // Yes | No   (game MinimapIcon present)
    public string? Encounter { get; set; }                  // Active | Complete   (IconComplete faded state)

    // ── Action ──
    public bool Hide { get; set; }                          // match → stop, don't draw
    public string Shape { get; set; } = "Circle";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1f;
    public float Size { get; set; } = 3f;
    public string? Label { get; set; }                      // optional text label drawn next to the dot
    public bool Navigable { get; set; }                     // reserved (Phase 2): qualifies as a nav target
}

/// <summary>
/// Ordered display ruleset — the single source of truth for per-entity visibility/icon/label/HP-bar.
/// Modeled on <see cref="WatchedEntities"/> / <see cref="LandmarkPatterns"/>: JSON-persisted, mutated
/// under a lock, read lock-free on the render thread via a volatile precompiled snapshot, with a
/// <see cref="Generation"/> counter so the tick loop notices live edits. <see cref="Resolve"/> is the
/// hot path (called per entity per frame) and must stay allocation-free.
/// </summary>
public sealed class DisplayRules
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private List<DisplayRule> _rules = new();           // under _gate (authoritative, ordered)
    private volatile Compiled[] _snapshot = Array.Empty<Compiled>(); // immutable; lock-free reads
    private volatile int _generation;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // keep the file tidy (omit "any" fields)
    };

    public DisplayRules(string filePath)
    {
        _filePath = filePath;
        Load();
        Rebuild();
    }

    /// <summary>Bumped on every mutation so the tick loop can detect a live edit (same pattern as
    /// <see cref="LandmarkPatterns.Generation"/>). The snapshot itself is already live; this is a marker.</summary>
    public int Generation => _generation;

    /// <summary>Whether any rules are loaded (used to decide if a migration must seed defaults).</summary>
    public int Count { get { lock (_gate) return _rules.Count; } }

    /// <summary>All rules in order (snapshot copy; safe to enumerate off-thread / serialize for the API).</summary>
    public IReadOnlyList<DisplayRule> All { get { lock (_gate) return _rules.ToList(); } }

    /// <summary>
    /// The first ENABLED rule that matches the entity, or null if none (→ not drawn). Lock-free hot path:
    /// reads the volatile precompiled snapshot. The returned rule's action fields tell the caller whether
    /// to hide or how to draw.
    /// </summary>
    public DisplayRule? Resolve(Poe2Live.EntityDot e)
    {
        var snap = _snapshot;
        foreach (var c in snap)
            if (c.Matches(in e)) return c.Rule;
        return null;
    }

    /// <summary>
    /// Resolve a terrain TILE path to its first matching enabled rule of the special "Tile" category,
    /// or null. Tiles aren't entities, so a Tile rule matches purely on its <see cref="DisplayRule.Match"/>
    /// terms (path substrings/globs); the other conditions are ignored. <paramref name="requireMatch"/>
    /// distinguishes the two passes: the SURFACING pass (true) only lets a Tile rule with explicit match
    /// terms pull a NEW tile onto the map; the STYLING pass (false) lets an empty-match Tile rule restyle
    /// any already-surfaced landmark. The matched rule's shape/color/size/label/hide then style it.
    /// </summary>
    public DisplayRule? ResolveTile(string path, bool requireMatch)
    {
        var snap = _snapshot;
        foreach (var c in snap)
            if (c.MatchesTile(path, requireMatch)) return c.Rule;
        return null;
    }

    /// <summary>Replace the entire ordered ruleset (used by the API "set whole list" path and by
    /// migration). Persists + recompiles + bumps the generation.</summary>
    public void Replace(IEnumerable<DisplayRule> rules)
    {
        lock (_gate)
        {
            _rules = rules.ToList();
            Rebuild(); Save();
        }
    }

    /// <summary>Append a rule (to the end = lowest precedence). Persists + recompiles.</summary>
    public void Add(DisplayRule rule)
    {
        lock (_gate) { _rules.Add(rule); Rebuild(); Save(); }
    }

    /// <summary>Remove the rule at <paramref name="index"/> (no-op if out of range).</summary>
    public void RemoveAt(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _rules.Count) return;
            _rules.RemoveAt(index); Rebuild(); Save();
        }
    }

    /// <summary>Move the rule at <paramref name="from"/> to <paramref name="to"/> (reorder = change
    /// precedence). Indices are clamped; no-op if equal/out of range.</summary>
    public void Move(int from, int to)
    {
        lock (_gate)
        {
            if (from < 0 || from >= _rules.Count) return;
            to = Math.Clamp(to, 0, _rules.Count - 1);
            if (from == to) return;
            var r = _rules[from];
            _rules.RemoveAt(from);
            _rules.Insert(to, r);
            Rebuild(); Save();
        }
    }

    /// <summary>Replace the rule at <paramref name="index"/> with <paramref name="rule"/>.</summary>
    public void Update(int index, DisplayRule rule)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _rules.Count) return;
            _rules[index] = rule; Rebuild(); Save();
        }
    }

    /// <summary>
    /// One-time, idempotent repair of over-broad "event marker" rules seeded from older watched/mechanic
    /// defaults — the same class of bug the migration fixed for Expedition/Strongbox. A bare metadata
    /// term is a substring of far more than the marker it was meant for, so before this fix the mechanic
    /// painted unrelated entities with its icon. Each fix restricts the rule to the categories the real
    /// marker object falls into; where a category gate can't separate marker from noise, it ALSO rewrites
    /// the term to a precise path prefix. Covered:
    /// <list type="bullet">
    /// <item><c>"LeagueRitual"</c>/<c>"Ritual"</c> → Object/Other gate: the bare term hit every tribute
    ///   mob (<c>Metadata/Monsters/LeagueRitual/…</c>, category Monster), so the whole pack inherited the
    ///   red RITUAL marker; the altar the game flags is Object/Other.</item>
    /// <item><c>"Breach"</c> → Monster/Other gate: the bare term hit the neutral Breach NPC
    ///   (<c>Metadata/Monsters/Breach/NPC/ChayulaFarmer</c> = Ailith, category Npc), drawing it as a
    ///   purple breach icon; the gate keeps the marker on breach mobs + the breach hand, off the NPC.</item>
    /// <item><c>"Shrine"</c> → rewrite to <c>"Metadata/Shrines/"</c> + Other gate: the bare term hit the
    ///   shrine BUFF daemons (<c>Metadata/Monsters/Daemon/Shrines/Shrine*DaemonPlayer</c>) that attach to
    ///   the player while a shrine buff is up, so a green shrine star rode the player around. Those daemons
    ///   are category Other just like the real shrine object, so a category gate alone can't exclude them —
    ///   only the top-level path prefix (the daemons sit a level deeper, under …/Monsters/Daemon/…).</item>
    /// </list>
    /// Only touches a rule still in the unfixed shape (the exact term present, no category gate yet),
    /// leaving any rule the user has customized alone. Returns true if it changed anything.
    /// </summary>
    public bool RepairEventMarkerRules()
    {
        // over-broad term → (precise replacement or null to keep the term, categories that isolate the marker)
        (string term, string? replaceWith, string[] cats)[] fixes =
        {
            ("LeagueRitual", null,                new[] { "Object", "Other" }),
            ("Ritual",       null,                new[] { "Object", "Other" }),
            ("Breach",       null,                new[] { "Monster", "Other" }),
            ("Shrine",       "Metadata/Shrines/", new[] { "Other" }),
        };
        var changed = false;
        lock (_gate)
        {
            foreach (var r in _rules)
            {
                if (r.Categories.Count > 0) continue;            // already category-gated → leave it
                foreach (var (term, replaceWith, cats) in fixes)
                {
                    var idx = r.Match.FindIndex(m => string.Equals(m, term, StringComparison.OrdinalIgnoreCase));
                    if (idx < 0) continue;
                    if (replaceWith != null)
                    {
                        if (r.Match.Any(m => string.Equals(m, replaceWith, StringComparison.OrdinalIgnoreCase)))
                            r.Match.RemoveAt(idx);               // precise term already present → drop the dup
                        else
                            r.Match[idx] = replaceWith;          // swap the over-broad term for the precise one
                    }
                    r.Categories = new List<string>(cats);
                    changed = true;
                    break;
                }
            }
            if (changed) { Rebuild(); Save(); }
        }
        return changed;
    }

    /// <summary>
    /// One-time, idempotent: add the "Chest · Magic" rule to rulesets seeded before it existed.
    /// PoE2 breakables (crates/urns/barrels) can roll magic rarity and are real loot the game shows
    /// on its own minimap, but older default rulesets only drew Rare/Unique chests — so magic chests
    /// never appeared. Skipped if ANY rule already gates on Chest + Magic (the user has one, possibly
    /// customized). Inserted right after the "Chest · Rare" rule so precedence mirrors BuildDefault;
    /// appended if no such rule exists. Returns true if it changed anything.
    /// </summary>
    public bool EnsureChestMagicRule(IconStyle style)
    {
        lock (_gate)
        {
            if (_rules.Any(r => r.Categories.Contains("Chest")
                                && string.Equals(r.Rarity, "Magic", StringComparison.OrdinalIgnoreCase)))
                return false;
            var rule = new DisplayRule
            {
                Name = "Chest · Magic", Enabled = style.Enabled,
                Categories = new() { "Chest" }, Rarity = "Magic",
                Shape = style.Shape, Color = style.Color, Opacity = style.Opacity, Size = style.Size,
            };
            var after = _rules.FindIndex(r => r.Categories.Contains("Chest")
                                              && string.Equals(r.Rarity, "Rare", StringComparison.OrdinalIgnoreCase));
            if (after >= 0) _rules.Insert(after + 1, rule);
            else _rules.Add(rule);
            Rebuild(); Save();
        }
        return true;
    }

    /// <summary>
    /// One-time, idempotent: add the "Azmerian Wisp" rule to rulesets seeded before it existed. The
    /// Wildwood/Azmeri wisps are hostile, killable monsters (<c>Metadata/Monsters/TormentedSpirits/…</c>)
    /// that flee and possess enemies — worth spotting/chasing — but older rulesets drew them as plain red
    /// "common enemy" dots. Skipped if ANY rule is named "Azmerian Wisp" or already matches the
    /// TormentedSpirits family (the user may have one, possibly customized). Inserted just ABOVE the first
    /// hostile-Monster category default so it overrides the generic monster styling (mirroring how the
    /// seeded mechanics sit above the category defaults in BuildDefault). Returns true if it changed anything.
    /// </summary>
    public bool EnsureAzmerianWispRule()
    {
        lock (_gate)
        {
            if (_rules.Any(r => string.Equals(r.Name, "Azmerian Wisp", StringComparison.OrdinalIgnoreCase)
                                || r.Match.Any(m => m.Contains("TormentedSpirits", StringComparison.OrdinalIgnoreCase))))
                return false;
            var rule = new DisplayRule
            {
                Name = "Azmerian Wisp", Enabled = true,
                Categories = new() { "Monster" }, Match = new() { "TormentedSpirits" },
                Shape = "Droplet", Color = "#C8FF3C", Opacity = 1f, Size = 7f,
            };
            var at = _rules.FindIndex(r => r.Categories.Contains("Monster")
                                           && string.Equals(r.Reaction, "Hostile", StringComparison.OrdinalIgnoreCase));
            if (at >= 0) _rules.Insert(at, rule);
            else _rules.Add(rule);
            Rebuild(); Save();
        }
        return true;
    }

    /// <summary>
    /// One-time, idempotent: add the "Hide neutral ambient units" rule to rulesets seeded before it
    /// existed. Neutral/unkillable ambient critters carry a reaction that's neither hostile (0) nor
    /// friendly (1), so the "not friendly ⇒ hostile" draw rules paint them as red common enemies. This
    /// inserts a Normal-rarity-gated hide at the TOP (state-hides band) so it precedes the monster draw
    /// rules and can never hide a rare/unique/boss. Skipped if ANY rule already uses Reaction "Neutral"
    /// (the user may have one, possibly disabled/customized). Returns true if it changed anything.
    /// </summary>
    public bool EnsureHideNeutralRule()
    {
        lock (_gate)
        {
            if (_rules.Any(r => string.Equals(r.Reaction, "Neutral", StringComparison.OrdinalIgnoreCase)))
                return false;
            _rules.Insert(0, new DisplayRule
            {
                Name = "Hide neutral ambient units", Enabled = true,
                Categories = new() { "Monster" }, Reaction = "Neutral", Rarity = "Normal", Hide = true,
            });
            Rebuild(); Save();
        }
        return true;
    }

    /// <summary>
    /// Build the default ordered ruleset that REPRODUCES the legacy three-system behavior, used to
    /// seed <c>display_rules.json</c> on first run. Order encodes the old precedence:
    /// <list type="number">
    /// <item>state hides (dead monster / opened chest / completed encounter) — the old IsDrawable gate;</item>
    /// <item>watched highlights (force-draw + label, any category) — wins over mechanics, as before;</item>
    /// <item>mechanic overrides (force-draw, category-gated);</item>
    /// <item>category defaults (hostile monsters by rarity; player; npc; rare/unique chests; transition;
    ///   Object/Other POIs).</item>
    /// </list>
    /// Disabled categories / ShowMonsters-off seed the corresponding rule as <c>Enabled=false</c>.
    /// (Phase 1 leaves the hidden/junk pre-filters and nav qualification external. HP bars are NOT a rule
    /// concern — they're monster-only and gated entirely by the per-rarity toggles in Settings.)
    /// </summary>
    public static List<DisplayRule> BuildDefault(
        RadarStyles st, bool showMonsters, IEnumerable<WatchedEntry> watched)
    {
        var rules = new List<DisplayRule>();

        // 1) State hides (precede everything — mirror the old IsDrawable corpse/opened/complete gate).
        rules.Add(new DisplayRule { Name = "Hide dead monsters",        Categories = new() { "Monster" }, Life = "Dead",     Hide = true });
        rules.Add(new DisplayRule { Name = "Hide opened chests",        Categories = new() { "Chest" },   Chest = "Opened",  Hide = true });
        rules.Add(new DisplayRule { Name = "Hide completed encounters", Encounter = "Complete",                              Hide = true });
        // Neutral, unkillable ambient critters carry a reaction that's neither hostile (0) nor friendly
        // (1); without this they fall into the "not friendly ⇒ hostile" bucket and draw as red common
        // enemies (and get HP bars). Gated to Normal rarity so it can NEVER hide a rare/unique/boss —
        // those are hostile anyway, but the gate is belt-and-suspenders against any enemy with a
        // non-standard reaction value (only hostile=0 is validated).
        rules.Add(new DisplayRule { Name = "Hide neutral ambient units", Categories = new() { "Monster" }, Reaction = "Neutral", Rarity = "Normal", Hide = true });

        // 2) Watched highlights (force-draw + label; substring, any category) — before mechanics so
        //    watched still wins, matching the old DrawMap precedence.
        foreach (var w in watched)
            rules.Add(new DisplayRule
            {
                Name = string.IsNullOrWhiteSpace(w.Label) ? w.Pattern : w.Label,
                Enabled = w.Enabled, Match = new() { w.Pattern },
                Shape = w.Shape, Color = w.Color, Opacity = 1f, Size = w.Size, Label = w.Label,
            });

        // 3) Mechanic overrides (force-draw, category-gated).
        foreach (var m in st.Mechanics ?? new())
            rules.Add(new DisplayRule
            {
                Name = m.Name, Enabled = m.Enabled,
                Categories = new(m.Categories ?? new()), Match = new(m.Match ?? new()),
                Shape = m.Shape, Color = m.Color, Opacity = m.Opacity, Size = m.Size,
            });

        // 4) Category defaults.
        void Mon(string rarity, IconStyle s) => rules.Add(new DisplayRule
        {
            Name = $"Monster · {rarity}", Enabled = s.Enabled && showMonsters,
            Categories = new() { "Monster" }, Reaction = "Hostile", Rarity = rarity,
            Shape = s.Shape, Color = s.Color, Opacity = s.Opacity, Size = s.Size,
        });
        Mon("Unique", st.MonsterUnique);
        Mon("Rare",   st.MonsterRare);
        Mon("Magic",  st.MonsterMagic);
        Mon("Normal", st.MonsterNormal);

        void Cat(string name, string category, string? rarity, IconStyle s) => rules.Add(new DisplayRule
        {
            Name = name, Enabled = s.Enabled, Categories = new() { category }, Rarity = rarity,
            Shape = s.Shape, Color = s.Color, Opacity = s.Opacity, Size = s.Size,
        });
        Cat("Player",        "Player",     null,     st.Player);
        Cat("NPC",           "Npc",        null,     st.Npc);
        Cat("Chest · Unique", "Chest", "Unique", st.ChestUnique);
        Cat("Chest · Rare",   "Chest", "Rare",   st.ChestRare);
        Cat("Chest · Magic",  "Chest", "Magic",  st.ChestMagic);
        Cat("Transition",    "Transition", null,     st.Transition);

        // Object/Other entities the game flags as POIs (waypoints, checkpoints, shrines…).
        rules.Add(new DisplayRule
        {
            Name = "Point of Interest", Enabled = st.Poi.Enabled,
            Categories = new() { "Object", "Other" }, Poi = "Yes",
            Shape = st.Poi.Shape, Color = st.Poi.Color, Opacity = st.Poi.Opacity, Size = st.Poi.Size,
        });

        return rules;
    }

    // ── internals ───────────────────────────────────────────────────────────

    /// <summary>Rebuild the immutable precompiled snapshot + bump generation. Call under <see cref="_gate"/>.</summary>
    private void Rebuild()
    {
        var compiled = new Compiled[_rules.Count];
        for (var i = 0; i < _rules.Count; i++) compiled[i] = new Compiled(_rules[i]);
        _snapshot = compiled;
        _generation++;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<DisplayRule>>(File.ReadAllText(_filePath), Json);
            if (list != null) _rules = list;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Display rules load failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_rules, Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Display rules save failed: {ex.Message}"); }
    }

    /// <summary>A rule precompiled for fast matching: category set + metadata matchers (substring/glob)
    /// + condition codes (0 = any). Built once per Rebuild; immutable thereafter.</summary>
    private sealed class Compiled
    {
        public readonly DisplayRule Rule;
        private readonly bool _enabled;
        private readonly bool _isTile;                     // Categories contains "Tile" → matches terrain tiles
        private readonly bool _hasCats;                    // a category filter is present (Categories non-empty)
        private readonly uint _catMask;                    // bit (1u<<(int)EntityCategory) per included category
        private readonly (string sub, Regex? glob)[]? _match; // null = any
        private readonly (string sub, Regex? glob)[]? _mods;  // null = any (matched vs the entity's mod-id list)
        private readonly bool _raritySet;                  // a rarity filter is present
        private readonly bool _rarityOk;                   // the rarity string parsed to a known enum value
        private readonly Poe2Live.Rarity _rarity;          // required rarity (valid when _raritySet && _rarityOk)
        private readonly int _reaction; // 0 any / 1 Friendly / 2 Hostile (= not friendly) / 3 Neutral
        private readonly int _life, _chest, _poi, _enc; // 0 any / 1 / 2

        public Compiled(DisplayRule r)
        {
            Rule = r;
            _enabled = r.Enabled;
            // Precompile the category filter to a BITMASK over EntityCategory so Matches can test it with a
            // single bit-AND — NOT `e.Category.ToString()`, which allocates a string on every entity, every
            // rule, every frame (the dominant render-path GC pressure in dense packs). "Tile" isn't an entity
            // category (it's the terrain-tile marker, captured in _isTile); _hasCats records that a filter
            // exists so a Tile-only rule (mask 0) correctly matches NO entity rather than being read as "any".
            _hasCats = r.Categories is { Count: > 0 };
            if (_hasCats)
            {
                uint mask = 0;
                foreach (var name in r.Categories)
                {
                    if (string.Equals(name, "Tile", StringComparison.OrdinalIgnoreCase)) { _isTile = true; continue; }
                    if (Enum.TryParse<Poe2Live.EntityCategory>(name, ignoreCase: true, out var cat))
                        mask |= 1u << (int)cat;
                }
                _catMask = mask;
            }
            _match = r.Match is { Count: > 0 }
                ? r.Match.Where(m => !string.IsNullOrEmpty(m)).Select(CompileTerm).ToArray() : null;
            if (_match is { Length: 0 }) _match = null;
            // Precompile the mod matcher (same substring/glob form as Match), allocation-free at match time.
            _mods = r.Mods is { Count: > 0 }
                ? r.Mods.Where(m => !string.IsNullOrEmpty(m)).Select(CompileTerm).ToArray() : null;
            if (_mods is { Length: 0 }) _mods = null;
            // Precompile rarity to the enum value too (same per-frame allocation-avoidance reason).
            _raritySet = !string.IsNullOrEmpty(r.Rarity);
            _rarityOk = _raritySet && Enum.TryParse<Poe2Live.Rarity>(r.Rarity, ignoreCase: true, out _rarity);
            _reaction = string.IsNullOrEmpty(r.Reaction) ? 0
                      : string.Equals(r.Reaction, "Friendly", StringComparison.OrdinalIgnoreCase) ? 1
                      : string.Equals(r.Reaction, "Hostile",  StringComparison.OrdinalIgnoreCase) ? 2
                      : string.Equals(r.Reaction, "Neutral",  StringComparison.OrdinalIgnoreCase) ? 3 : 0;
            _life     = Code(r.Life, "Alive", "Dead");
            _chest    = Code(r.Chest, "Opened", "Unopened");
            _poi      = Code(r.Poi, "Yes", "No");
            _enc      = Code(r.Encounter, "Active", "Complete");
        }

        public bool Matches(in Poe2Live.EntityDot e)
        {
            if (!_enabled) return false;
            if (_hasCats && (_catMask & (1u << (int)e.Category)) == 0) return false;
            if (_match != null && !AnyMatch(e.Metadata)) return false;
            if (_mods != null && !AnyMatchMods(e.Mods)) return false;
            if (_raritySet && (!_rarityOk || e.Rarity != _rarity)) return false;
            if (_reaction == 1 && !e.IsFriendly) return false;
            if (_reaction == 2 && e.IsFriendly) return false;        // Hostile = not friendly (incl. neutral)
            if (_reaction == 3 && !e.IsNeutral) return false;        // Neutral = neither hostile nor friendly
            if (_life == 1 && !e.IsAlive) return false;
            if (_life == 2 && e.IsAlive) return false;
            if (_chest == 1 && !e.Opened) return false;
            if (_chest == 2 && e.Opened) return false;
            if (_poi == 1 && !e.Poi) return false;
            if (_poi == 2 && e.Poi) return false;
            if (_enc == 1 && e.IconComplete) return false;   // "Active" requires not-complete
            if (_enc == 2 && !e.IconComplete) return false;  // "Complete" requires complete
            return true;
        }

        public bool MatchesTile(string path, bool requireMatch)
        {
            if (!_enabled || !_isTile) return false;
            if (_match == null) return !requireMatch; // no terms: styles any tile, but never surfaces one
            return AnyMatch(path);
        }

        private bool AnyMatch(string metadata)
        {
            foreach (var (sub, glob) in _match!)
            {
                if (glob != null) { if (glob.IsMatch(metadata)) return true; }
                else if (metadata.Contains(sub, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True if any of this rule's mod terms matches any of the entity's mod ids (substring or
        /// glob). Allocation-free hot path: no LINQ, null/empty list short-circuits.</summary>
        private bool AnyMatchMods(IReadOnlyList<string>? mods)
        {
            if (mods is not { Count: > 0 }) return false;
            foreach (var (sub, glob) in _mods!)
                for (var i = 0; i < mods.Count; i++)
                {
                    var m = mods[i];
                    if (glob != null) { if (glob.IsMatch(m)) return true; }
                    else if (m.Contains(sub, StringComparison.OrdinalIgnoreCase)) return true;
                }
            return false;
        }

        /// <summary>A term with <c>*</c>/<c>?</c> compiles to an anchored glob regex (mirrors
        /// <see cref="HiddenEntities"/>); otherwise it's a case-insensitive substring.</summary>
        private static (string, Regex?) CompileTerm(string term)
        {
            if (term.IndexOf('*') < 0 && term.IndexOf('?') < 0) return (term, null);
            var rx = "^" + Regex.Escape(term).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return (term, new Regex(rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        private static int Code(string? v, string one, string two)
            => string.IsNullOrEmpty(v) ? 0
             : string.Equals(v, one, StringComparison.OrdinalIgnoreCase) ? 1
             : string.Equals(v, two, StringComparison.OrdinalIgnoreCase) ? 2 : 0;
    }
}
