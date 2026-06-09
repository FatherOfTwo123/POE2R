using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Core.Game;

/// <summary>
/// The curated "important preloads" list for preload alerts (A1): substrings of loaded-file paths
/// that mark notable zone contents (a mechanic, a boss, a reward), each with a friendly name + colour.
/// Baked into the assembly as the embedded <c>important_preloads.json</c> (same convention as
/// <see cref="EntityNameResolver"/>), so the overlay ships self-contained; users can extend it.
///
/// <para>Matching is a case-insensitive substring test against each path the client loaded for the
/// current area (from <see cref="PreloadReader"/>). The seed entries are STARTING GUESSES based on
/// PoE2 mechanic names — capture real per-area preload paths with <c>POE2Radar.Research --preload</c>
/// and refine the list.</para>
/// </summary>
public sealed class PreloadCatalog
{
    /// <summary>One catalog rule: a path substring to look for, and how to present a hit.</summary>
    public readonly record struct Entry(string Match, string Name, string Color);

    /// <summary>A matched preload found in the current area.</summary>
    public readonly record struct Hit(string Name, string Color, string Path);

    private readonly List<Entry> _entries;

    private PreloadCatalog(List<Entry> entries) => _entries = entries;

    /// <summary>The shared catalog, loaded once from the embedded list.</summary>
    public static PreloadCatalog Shared { get; } = LoadEmbedded();

    /// <summary>Number of loaded rules (0 = the list failed to load / is empty).</summary>
    public int Count => _entries.Count;

    private static PreloadCatalog LoadEmbedded()
    {
        var entries = new List<Entry>();
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("important_preloads"));
            if (resName != null)
            {
                using var stream = asm.GetManifestResourceStream(resName)!;
                var doc = JsonDocument.Parse(stream);
                if (doc.RootElement.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in arr.EnumerateArray())
                    {
                        var match = e.TryGetProperty("match", out var m) ? m.GetString() : null;
                        if (string.IsNullOrWhiteSpace(match)) continue;
                        var name = e.TryGetProperty("name", out var n) ? n.GetString() ?? match : match;
                        var color = e.TryGetProperty("color", out var c) ? c.GetString() ?? "#FFFFFF" : "#FFFFFF";
                        entries.Add(new Entry(match, name, color));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"PreloadCatalog load failed: {ex.Message}");
        }
        return new PreloadCatalog(entries);
    }

    /// <summary>
    /// Match the catalog against the set of file paths loaded for the current area. Returns one hit per
    /// catalog entry that matched (deduped by name, first matching path kept), in catalog order.
    /// </summary>
    public List<Hit> Match(IReadOnlyCollection<string> loadedPaths)
    {
        var hits = new List<Hit>();
        if (_entries.Count == 0 || loadedPaths.Count == 0) return hits;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            foreach (var path in loadedPaths)
            {
                if (path.Contains(entry.Match, StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(entry.Name)) hits.Add(new Hit(entry.Name, entry.Color, path));
                    break;
                }
            }
        }
        return hits;
    }
}
