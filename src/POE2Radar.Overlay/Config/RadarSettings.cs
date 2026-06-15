using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Overlay.Config;

/// <summary>
/// User-tweakable overlay settings, persisted as JSON next to the executable
/// (<c>config/radar_settings.json</c>). Defaults reproduce the original hardcoded behavior exactly,
/// so a missing/partial file changes nothing. Calibration is saved live as hotkeys adjust it.
/// </summary>
public sealed class RadarSettings
{
    // ── Feature flags (reserved for later phases; no behavior wired yet). ──
    public bool HideJunk { get; set; } = false;
    public bool ShowPath { get; set; } = false;
    public bool UseCuratedLandmarks { get; set; } = true;
    public bool DrawAllLandmarkPaths { get; set; } = false;

    // ── Landmark clustering. A reusable tile (e.g. a "stairs up" wall piece) recurs in several
    //    disjoint spots — a multi-level dungeon has several stair-up/stair-down sections — so the
    //    scanner groups a tile path's cells into spatial clusters and emits one marker per cluster.
    //    This is the MAX GAP (in TILES; 1 tile ≈ 23 grid units) between cells still considered the
    //    same cluster: larger = merges nearby spots (fewer markers, less map spam), smaller = splits
    //    them (more markers). 0 disables bridging (only directly-touching tiles group). ──
    public int LandmarkClusterGap { get; set; } = 2;

    // ── Entity persistence (anti-flicker): how many world-ticks (~30 Hz) an entity keeps showing at its
    //    last-known position after it leaves the network bubble, before it's dropped. Smooths monsters/
    //    POIs that flicker in and out (Breach, Delirium, fast rares). 0 = off (only entities present this
    //    tick). Default 20 ≈ 0.66 s. Higher = steadier but shows staler ghosts; lower = snappier. ──
    public int EntityLingerFrames { get; set; } = 20;

    // ── Radar display toggles. ──
    public bool ShowMonsters { get; set; } = true;
    public bool ShowTerrain { get; set; } = true;
    // The player position blip at map-center. Default on (prior behavior); some prefer it off.
    public bool ShowPlayerBlip { get; set; } = true;

    // ── Overlay render/present rate (Hz). The overlay redraws + UpdateLayeredWindow-blits at this
    //    rate; lower = less CPU/GPU tax on the game (the blit cost is proportional to resolution).
    //    60 is plenty smooth for a radar; raise toward your monitor's refresh if you prefer. The
    //    heavier entity/terrain walk stays fixed at ~30 Hz regardless. ──
    public int FpsCap { get; set; } = 60;

    // ── Navigation-menu widget: which screen corner it is pinned to.
    //    One of "TopLeft", "TopRight", "BottomLeft", "BottomRight". ──
    public string NavMenuCorner { get; set; } = "TopLeft";

    // ── Persistent auto-nav: substrings matched (case-insensitive Contains) against a navigation
    //    target's MatchKey (tile path / entity metadata). On every zone change, every target whose
    //    MatchKey matches ANY pattern is auto-selected (up to the 8-color cap), so entering a new
    //    zone auto-draws a path to e.g. the expedition encounter. Seeded with one example so the
    //    feature is visible out of the box; clear the list to disable. ──
    // Dir-qualified so it matches the real marker ("Expedition2/Expedition2Encounter") and not the
    // transient ".../Objects/Expedition2EncounterCrack" effects. (Plain "ExpeditionEncounter" matched
    // nothing — the live path is "Expedition2Encounter" with a digit.)
    public List<string> AutoNavPatterns { get; set; } = new() { "Expedition2/Expedition2Encounter" };

    // ── Monster HP bars (world-space nameplates) by rarity.
    //    Defaults preserve prior behavior: Magic/Rare/Unique shown, Normal hidden. ──
    public bool HpBarNormal { get; set; } = false;
    public bool HpBarMagic { get; set; } = true;
    public bool HpBarRare { get; set; } = true;
    public bool HpBarUnique { get; set; } = true;

    // ── Projection calibration (PageUp/Down = scale, arrows = offset, Home = reset). ──
    public float ScaleMul { get; set; } = 1.0f;
    public float OffX { get; set; } = 0f;
    public float OffY { get; set; } = 0f;

    // Draw the overlay even when PoE2 isn't the foreground window (e.g. while tweaking the dashboard).
    // Default off (overlay hides when unfocused).
    public bool AlwaysShowOverlay { get; set; } = false;

    // NOTE: the atlas canvas→screen projection has NO stored settings — it's derived live from the game
    // window height (UIscale = winH/1600 × live zoom) in RadarApp.AtlasProjection, so it's resolution-
    // correct everywhere with no calibration. (The old F10/F11 homography calibration + its AtlasScale/
    // Off/Shear/Pers/CalibZoom settings were removed; F10 now drives atlas point-to-point routing.)

    // Atlas highlight rules: only nodes whose content tags include one of these are drawn in-game (the
    // point is to surface content the game hides by default). Set live from the dashboard Atlas tab.
    // Matched case-insensitively against each node's resolved content tags (e.g. "Breach", "Powerful Map Boss").
    public List<string> AtlasHighlightTags { get; set; } = new();
    // Tags with the off-screen ARROW enabled: when a matching map is outside render distance, an edge
    // arrow points toward it (for hunting high-value maps you can't zoom out to). Independent of tracking.
    public List<string> AtlasArrowTags { get; set; } = new();
    // Per-rule ring colour (tag → "#RRGGBB"), so each highlighted map draws in its filter's category
    // colour in-game (Citadel gold, Boss red, …). Set from the dashboard alongside AtlasHighlightTags.
    public Dictionary<string, string> AtlasHighlightColors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Seeded-defaults guard: false until the atlas rules have been initialized once (either by seeding
    // the Citadel defaults when nodes are first read, or by any dashboard edit). Stops re-seeding.
    public bool AtlasRulesInitialized { get; set; }
    // DEBUG: draw EVERY atlas node (overriding the highlight-only rule) — for offset/coverage diagnostics.
    // Off by default: normally only nodes matching AtlasHighlightTags (or manually selected) are drawn.
    public bool AtlasDrawAll { get; set; } = false;
    // Atlas routing: F10 over a tile sets it as the route destination; the overlay draws the shortest path
    // (through the node connection graph) from the player's current node to it. On by default.
    public bool AtlasShowRoute { get; set; } = true;

    // ── HTTP API. ──
    public int ApiPort { get; set; } = 7777;

    // ── Per-item icon styling (shape / color / opacity / size) + metadata-matched "mechanic"
    //    overrides. Defaults reproduce the original hardcoded look exactly. ──
    public RadarStyles Styles { get; set; } = new();

    // ── Monster HP-bar geometry (the per-rarity ENABLE flags above stay the source of truth;
    //    this adds per-rarity sizing, border thickness, and border color). ──
    public HpBarSettings HpBars { get; set; } = new();

    // ── Walkable-terrain bitmap colors/transparency. Defaults reproduce the old hardcoded wash. ──
    public TerrainSettings Terrain { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Config file path: a "config" directory next to the executable.</summary>
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "radar_settings.json");

    /// <summary>
    /// Load settings from disk. Returns defaults if the file is missing (and writes a default file),
    /// and is tolerant of partial/missing keys. Never throws on IO/parse errors — logs and falls back.
    /// </summary>
    public static RadarSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new RadarSettings();
                fresh.Save();
                return fresh;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<RadarSettings>(json, Json) ?? new RadarSettings();
            // Existing configs are loaded verbatim (never re-seeded from defaults), so repair stale
            // patterns shipped by older builds in place, then persist the upgrade.
            if (loaded.Migrate())
            {
                loaded.Save();
                Console.WriteLine("Settings: migrated stale mechanic rules (Expedition/Strongbox/Ritual/Breach/Shrine gating).");
            }
            return loaded;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings load failed ({ex.Message}); using defaults.");
            return new RadarSettings();
        }
    }

    /// <summary>
    /// One-time, idempotent repair of mechanic rules from older builds (loaded verbatim, so they'd
    /// otherwise keep the bug forever). Every fix addresses an ungated rule that tagged a mechanic's
    /// spawned monsters / NPCs / effect-carriers, not just the object the marker is meant for:
    /// <list type="bullet">
    /// <item>Expedition: bare "Expedition" / dead "ExpeditionEncounter" → precise, Other-gated
    ///   "Expedition2/Expedition2Encounter".</item>
    /// <item>Strongbox: add a Chest category gate (the box's Vaal guards carry "...Strongbox").</item>
    /// <item>Ritual: add an Object/Other gate (the bare term tagged the tribute mobs).</item>
    /// <item>Breach: add a Monster/Other gate (the bare term tagged the neutral Breach NPC, Ailith).</item>
    /// <item>Shrine: rewrite bare "Shrine" → "Metadata/Shrines/" + Other gate (the bare term tagged the
    ///   player-attached shrine buff daemons, which then rode the player around).</item>
    /// </list>
    /// Returns true if anything changed.
    /// </summary>
    public bool Migrate()
    {
        const string precise = "Expedition2/Expedition2Encounter";
        static bool IsStaleExp(string p) =>
            string.Equals(p, "Expedition", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p, "ExpeditionEncounter", StringComparison.OrdinalIgnoreCase);

        var changed = false;

        static bool IsBroadStrongbox(string p) => string.Equals(p, "Strongbox", StringComparison.OrdinalIgnoreCase);
        static bool Has(MechanicStyle m, string term) =>
            m.Match.Exists(p => string.Equals(p, term, StringComparison.OrdinalIgnoreCase));
        static bool Ungated(MechanicStyle m) => m.Categories is null || m.Categories.Count == 0;

        if (Styles?.Mechanics is { } mechanics)
            foreach (var m in mechanics)
            {
                if (m.Match is null) continue;
                // Expedition: drop the stale/over-broad keys → the precise key + an Other category gate
                // (so it can't hijack the Monster-category expedition mobs).
                if (m.Match.RemoveAll(IsStaleExp) > 0)
                {
                    if (!m.Match.Exists(p => string.Equals(p, precise, StringComparison.OrdinalIgnoreCase)))
                        m.Match.Add(precise);
                    m.Categories ??= new List<string>();
                    if (m.Categories.Count == 0) m.Categories.Add("Other");
                    changed = true;
                }
                // Strongbox: the default's bare "Strongbox" term over-matched twice — the box's spawned
                // Vaal guards (…Strongbox monsters) and ordinary area chests named "...Strongbox". Drop
                // it down to the "StrongBoxes" directory term and gate to Chest (the box is a /Chests/
                // entity). Triggers whenever the broad term is still present, regardless of category.
                else if (m.Match.Exists(IsBroadStrongbox))
                {
                    m.Match.RemoveAll(IsBroadStrongbox);
                    if (!m.Match.Exists(p => string.Equals(p, "StrongBoxes", StringComparison.OrdinalIgnoreCase)))
                        m.Match.Add("StrongBoxes");
                    m.Categories ??= new List<string>();
                    if (m.Categories.Count == 0) m.Categories.Add("Chest");
                    changed = true;
                }
                // Ritual: bare "Ritual" tagged the tribute mobs (category Monster). Gate it to the altar's
                // categories (Object/Other). Only touch a still-ungated rule so a user's customization stays.
                else if (Has(m, "Ritual") && Ungated(m))
                {
                    m.Categories = new List<string> { "Object", "Other" }; changed = true;
                }
                // Breach: bare "Breach" tagged the neutral Breach NPC (Ailith, category Npc). Gate to the
                // breach mobs + objects (Monster/Other), which excludes Npc.
                else if (Has(m, "Breach") && Ungated(m))
                {
                    m.Categories = new List<string> { "Monster", "Other" }; changed = true;
                }
                // Shrine: bare "Shrine" tagged the player-attached shrine buff daemons
                // ("Metadata/Monsters/Daemon/Shrines/…"). Rewrite to the top-level "Metadata/Shrines/"
                // prefix (the real pickups) + an Other gate; the daemons live a level deeper and can't match.
                else if (Has(m, "Shrine"))
                {
                    m.Match.RemoveAll(p => string.Equals(p, "Shrine", StringComparison.OrdinalIgnoreCase));
                    if (!Has(m, "Metadata/Shrines/")) m.Match.Add("Metadata/Shrines/");
                    m.Categories ??= new List<string>();
                    if (m.Categories.Count == 0) m.Categories.Add("Other");
                    changed = true;
                }
            }

        // Auto-nav: the seeded "ExpeditionEncounter" matched nothing (digit in the real path).
        if (AutoNavPatterns is not null)
            for (var i = 0; i < AutoNavPatterns.Count; i++)
                if (IsStaleExp(AutoNavPatterns[i])) { AutoNavPatterns[i] = precise; changed = true; }

        return changed;
    }

    /// <summary>Persist current settings to disk. Never throws on IO error — logs and continues.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings save failed: {ex.Message}");
        }
    }
}

/// <summary>
/// A single drawable radar icon: shape, RGB color, opacity, pixel size, and an enable toggle.
/// <see cref="Shape"/> is one of Circle/Triangle/Star/Diamond/Plus/Square (anything else falls back
/// to Circle when rendered); <see cref="Color"/> is <c>#RRGGBB</c>; <see cref="Opacity"/> is 0..1.
/// </summary>
public sealed class IconStyle
{
    public bool Enabled { get; set; } = true;
    public string Shape { get; set; } = "Circle";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1.0f;
    public float Size { get; set; } = 3.0f;

    public IconStyle() { }
    public IconStyle(string shape, string color, float opacity, float size)
    {
        Shape = shape; Color = color; Opacity = opacity; Size = size;
    }
}

/// <summary>
/// A user-defined "mechanic" highlight: when an entity's metadata contains ANY of <see cref="Match"/>
/// (case-insensitive) AND its category is in <see cref="Categories"/> (if any are listed), it draws
/// this icon instead of its generic category dot — so e.g. an Expedition marker or a Strongbox stands
/// out. The first enabled matching rule wins.
/// </summary>
public sealed class MechanicStyle
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    public List<string> Match { get; set; } = new();
    /// <summary>Entity-category gate by <c>Poe2Live.EntityCategory</c> name (e.g. "Monster", "Chest",
    /// "Other"). A rule applies only to these categories; empty = all categories. This stops a broad
    /// match term (e.g. "Expedition") from hijacking the wrong entities — the league POI marker
    /// (category Other) vs. the monsters that spawn during the event (category Monster).</summary>
    public List<string> Categories { get; set; } = new();
    public string Shape { get; set; } = "Star";
    public string Color { get; set; } = "#FFFFFF";
    public float Opacity { get; set; } = 1.0f;
    public float Size { get; set; } = 6.0f;
}

/// <summary>
/// Monster HP-bar geometry. Width, border thickness, and border color are per-rarity; height + X/Y
/// offset are shared. The per-rarity enable flags live on <see cref="RadarSettings"/>
/// (HpBarNormal/Magic/Rare/Unique). The bar *fill* color is taken from the matching monster icon
/// color (so "rare = gold" stays one setting); the border is configured independently below. Border
/// defaults reproduce the old weight-by-rarity cue (Normal undecorated, Magic 1px, Rare/Unique 2px)
/// with borders tinted to match each rarity's icon color.
/// </summary>
public sealed class HpBarSettings
{
    public float Height { get; set; } = 5f;
    public float OffsetX { get; set; } = 0f;
    public float OffsetY { get; set; } = -30f; // px relative to the mob's screen position (neg = up)
    public float WidthNormal { get; set; } = 30f;
    public float WidthMagic { get; set; } = 38f;
    public float WidthRare { get; set; } = 50f;
    public float WidthUnique { get; set; } = 64f;
    // Border thickness in px (0 = no border).
    public float BorderNormal { get; set; } = 0f;
    public float BorderMagic { get; set; } = 1f;
    public float BorderRare { get; set; } = 2f;
    public float BorderUnique { get; set; } = 2f;
    // Border color (#RRGGBB); defaults mirror the per-rarity monster icon colors.
    public string BorderColorNormal { get; set; } = "#FF3333";
    public string BorderColorMagic { get; set; } = "#73A6FF";
    public string BorderColorRare { get; set; } = "#FFD926";
    public string BorderColorUnique { get; set; } = "#FF7300";
}

/// <summary>
/// Walkable-terrain bitmap styling: the interior "wash" over walkable cells and the brighter
/// outline drawn on walkable cells bordering a wall/edge. Color is <c>#RRGGBB</c>; opacity is 0..1
/// (baked into the per-pixel alpha). Defaults reproduce the formerly hardcoded look exactly:
/// interior <c>#506482</c> @ ~30/255, edge <c>#3CDCFF</c> @ ~180/255. The per-area terrain bitmap
/// is rebuilt when any of these change.
/// </summary>
public sealed class TerrainSettings
{
    public string InteriorColor { get; set; } = "#506482";
    public float InteriorOpacity { get; set; } = 0.118f; // → 30/255
    public string EdgeColor { get; set; } = "#3CDCFF";
    public float EdgeOpacity { get; set; } = 0.706f;      // → 180/255
}

/// <summary>
/// The full radar icon style table. Every default mirrors the formerly hardcoded values in
/// <c>OverlayRenderer</c>, so a missing/partial config renders identically to before.
/// </summary>
public sealed class RadarStyles
{
    // Monster dots by rarity.
    public IconStyle MonsterNormal { get; set; } = new("Circle",   "#FF3333", 0.95f, 2.6f);
    public IconStyle MonsterMagic  { get; set; } = new("Diamond",  "#73A6FF", 0.97f, 3.4f);
    public IconStyle MonsterRare   { get; set; } = new("Triangle", "#FFD926", 1.00f, 5.5f);
    public IconStyle MonsterUnique { get; set; } = new("Star",     "#FF7300", 1.00f, 6.5f);

    // Other entity categories.
    public IconStyle Player        { get; set; } = new("Circle",  "#4DF2FF", 1.00f, 3.0f);
    public IconStyle Npc           { get; set; } = new("Plus",    "#FFD933", 0.95f, 4.0f);
    public IconStyle ChestMagic    { get; set; } = new("Square",  "#73A6FF", 0.95f, 4.5f);
    public IconStyle ChestRare     { get; set; } = new("Square",  "#FFD926", 0.95f, 5.0f);
    public IconStyle ChestUnique   { get; set; } = new("Square",  "#FF7300", 0.95f, 5.0f);
    public IconStyle Transition    { get; set; } = new("Diamond", "#66FF99", 0.95f, 4.5f);
    public IconStyle Poi           { get; set; } = new("Circle",  "#8CBFFF", 0.70f, 3.0f);

    // Tile landmarks (shape marker + text label at the group centroid).
    public IconStyle Landmark      { get; set; } = new("Diamond", "#F259F2", 1.00f, 5.0f);

    // Metadata-matched overrides (first enabled match wins). Seeded with common PoE2 mechanics.
    public List<MechanicStyle> Mechanics { get; set; } = new()
    {
        // Match the actual league POI marker ONLY. The old bare "Expedition" substring (with an empty
        // category gate) hijacked EVERY entity carrying "Expedition" in its path — the combat mobs
        // (".../...CrabExpedition", category Monster) and the transient detonation effects
        // (".../Objects/Expedition2EncounterCrack", category Other) all got the marker icon. The
        // dir-qualified key hits only "Expedition2/Expedition2Encounter" (NOT the "/Objects/...Crack"
        // path), and the Other gate keeps it off the monsters. ("ExpeditionEncounter" was also dead —
        // the real path is "Expedition2Encounter" with a digit, so that key matched nothing.)
        new() { Name = "Expedition", Match = new() { "Expedition2/Expedition2Encounter" }, Categories = new() { "Other" }, Shape = "Plus", Color = "#26E6D9", Opacity = 1f, Size = 7f },
        // Same over-tagging trap as Expedition: "Ritual" is a substring of every tribute mob's path
        // ("Metadata/Monsters/LeagueRitual/…", category Monster) and the wendigo/voodoo ritual mobs, so
        // an ungated rule paints the whole pack with the RITUAL marker. Gate to Object/Other — the only
        // categories the ritual ALTAR (the thing the game actually flags) falls into — so the monsters
        // drop through to normal monster styling. Mirrors the v0.9.1 watched-rule repair.
        new() { Name = "Ritual",     Match = new() { "Ritual" },            Categories = new() { "Object", "Other" },  Shape = "Star",     Color = "#FF3355", Opacity = 1f, Size = 7f },
        // "Breach" matches the Breach NPC's path too ("Metadata/Monsters/Breach/NPC/ChayulaFarmer" =
        // Ailith, category Npc), so an ungated rule drew the neutral Breach NPC as a purple breach icon.
        // Gate to Monster+Other (breach mobs + the breach hand/portal objects) — everything legitimately
        // "breach" EXCEPT the NPC. (Player/Npc/Transition are excluded by omission.)
        new() { Name = "Breach",     Match = new() { "Breach" },            Categories = new() { "Monster", "Other" }, Shape = "Diamond",  Color = "#A64DFF", Opacity = 1f, Size = 7f },
        // Match the league-strongbox DIRECTORY only ("Metadata/Chests/StrongBoxes/…") and gate to
        // Chest. The bare "Strongbox" term was too broad twice over: it tagged the box's spawned Vaal
        // guards (…Strongbox monsters — now excluded by the Chest gate) AND ordinary area chests that
        // merely carry "Strongbox" in their name (e.g. Chests/KedgeBayChests/KedgeBayChestStrongbox).
        // "StrongBoxes" hits the real boxes (BasicStrongboxLow lives under it) but not those.
        new() { Name = "Strongbox",  Match = new() { "StrongBoxes" }, Categories = new() { "Chest" }, Shape = "Square", Color = "#FFB300", Opacity = 1f, Size = 6f },
        new() { Name = "Essence",    Match = new() { "Essence" },                           Shape = "Triangle", Color = "#33E0FF", Opacity = 1f, Size = 7f },
        // Azmerian Wisps (Wild/Vivid/Primal/Sacred — Bear/Boar/Ox/Cat/Stag/Wolf/Owl/Primate/Serpent/
        // Fox/Rabbit) are hostile, killable monsters that FLEE and possess enemies — you want to spot
        // and chase them, but they render as plain red "common enemy" dots. In PoE2 they're implemented
        // as "Metadata/Monsters/TormentedSpirits/…" entities; the Monster gate keeps the marker on the
        // chaseable wisps and off the invisible possession/effect daemons (category Other). NOTE: the
        // JunkFilter no longer hides this family (it used to, which also hid these real monsters).
        new() { Name = "Azmerian Wisp", Match = new() { "TormentedSpirits" }, Categories = new() { "Monster" }, Shape = "Droplet", Color = "#C8FF3C", Opacity = 1f, Size = 7f },
        // The real shrine pickups live at "Metadata/Shrines/…" (Shrine, Darkshrine — category Other). The
        // bare "Shrine" term ALSO hit the shrine BUFF effect-carriers ("Metadata/Monsters/Daemon/Shrines/
        // Shrine*DaemonPlayer") that attach to the player while a shrine buff is active — so after grabbing
        // a shrine, a green shrine star rode the player around. The "Metadata/Shrines/" prefix matches only
        // the top-level shrine objects (the daemons sit under …/Monsters/Daemon/Shrines/, never reachable
        // by that prefix), and the Other gate keeps it on the object.
        new() { Name = "Shrine",     Match = new() { "Metadata/Shrines/" }, Categories = new() { "Other" },            Shape = "Star",     Color = "#7DFF7D", Opacity = 1f, Size = 6f },
    };
}
