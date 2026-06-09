namespace POE2Radar.Core.Game;

/// <summary>
/// Reads the client's loaded-files table to support <b>preload alerts</b> (A1): when you enter an
/// area the game preloads the files for everything in it (boss/unique/mechanic art + metadata), so
/// the set of files loaded <i>for the current area</i> is a fingerprint of what the zone contains —
/// known the instant you arrive, before any monster is in view. This mirrors GameHelper2's
/// <c>LoadedFiles</c>: a static root pointer → an array of std::buckets, each a StdVector of file
/// nodes; a node's file-info struct carries the file's path and the area-change index it loaded in.
/// Keeping only files whose index == the live <c>AreaChangeCounter</c> isolates the current area.
///
/// <para>Both the root pointer and the counter come from AOB patterns (<see cref="AobPatterns.FileRootRefs"/>
/// / <see cref="AobPatterns.AreaChangeCounterRefs"/>) and the struct offsets live in <see cref="Poe2.Preload"/>;
/// all are GH2/PoE1 and UNVALIDATED on PoE2. When the slots don't resolve (or the layout is wrong),
/// every read here degrades to "no files", so the feature is inert rather than crashing. Discover +
/// validate with <c>POE2Radar.Research --preload</c>.</para>
/// </summary>
public sealed class PreloadReader
{
    private readonly MemoryReader _reader;

    /// <summary>The resolved File Root global slot (deref → file-table root). 0 = unresolved (inert).</summary>
    public nint FileRootSlot { get; }

    /// <summary>The resolved AreaChangeCounter global slot (an int). 0 = unresolved (inert).</summary>
    public nint AreaChangeSlot { get; }

    /// <summary>True only when BOTH slots resolved — i.e. preload reads can produce data.</summary>
    public bool Available => FileRootSlot != 0 && AreaChangeSlot != 0;

    public PreloadReader(MemoryReader reader, nint fileRootSlot, nint areaChangeSlot)
    {
        _reader = reader;
        FileRootSlot = fileRootSlot;
        AreaChangeSlot = areaChangeSlot;
    }

    /// <summary>The live area-change counter value (0 when unavailable). Changes on each area load —
    /// the caller can use it to detect "new area, re-scan".</summary>
    public int AreaChangeValue =>
        AreaChangeSlot != 0 && _reader.TryReadStruct<int>(AreaChangeSlot, out var v) ? v : 0;

    /// <summary>
    /// All DISTINCT file paths the client loaded for the CURRENT area (those whose area-change index
    /// equals the live counter and is past the engine warm-up indices). Empty when unavailable or
    /// during a load. <paramref name="maxFiles"/> caps the result so a bad layout can't run away.
    /// </summary>
    public HashSet<string> CurrentAreaFiles(int maxFiles = 200_000)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Available) return result;

        // Read the live area-change counter. We do NOT early-return when it's small (the old bug:
        // gating the whole scan on `counter <= IgnoreFirstAreas` returned empty in a fresh hideout at
        // counter 2). The base-vs-current-area separation is handled PER FILE in the loop below.
        if (!_reader.TryReadStruct<int>(AreaChangeSlot, out var areaCount) || areaCount <= 0) return result;

        var rootPtr = Ptr(FileRootSlot);
        if (rootPtr == 0) return result;

        for (var r = 0; r < Poe2.Preload.RootObjectCount && result.Count < maxFiles; r++)
        {
            // Each root entry is a std::bucket; its first 24 bytes are the StdVector of file nodes.
            var bucket = rootPtr + (nint)(r * Poe2.Preload.RootObjectStride);
            if (!_reader.TryReadStruct<StdVector>(bucket, out var vec)) continue;
            if (vec.First == 0) continue;
            var count = ((long)vec.Last - (long)vec.First) / Poe2.Preload.FileNodeStride;
            if (count is <= 0 or > 5_000_000) continue;

            for (long i = 0; i < count && result.Count < maxFiles; i++)
            {
                var node = vec.First + (nint)(i * Poe2.Preload.FileNodeStride);
                var info = Ptr(node + Poe2.Preload.FileInfoPtr);
                if (info == 0) continue;
                if (!_reader.TryReadStruct<int>(info + Poe2.Preload.FileAreaChangeId, out var ac)) continue;
                // Keep only files loaded FOR THIS AREA: load-index == live counter AND past the engine
                // warm-up indices. The base-game asset set (loaded in the first couple of area-changes)
                // keeps its low original index, so this per-file `> IgnoreFirstAreas` test is what
                // separates the zone's distinctive content from the always-loaded base set. (Verified
                // live: in a fresh hideout the counter is 2 and ALL 6.7k base files carry index 2 — only
                // once you enter a real zone do its new files get a higher index.)
                if (ac != areaCount || ac <= Poe2.Preload.IgnoreFirstAreas) continue;
                var name = ReadStdWString(info + Poe2.Preload.FileNameStr);
                if (string.IsNullOrEmpty(name)) continue;
                // Paths can carry a trailing "@<n>" variant tag — strip it (GH2 does the same).
                var at = name.IndexOf('@');
                result.Add(at >= 0 ? name[..at] : name);
            }
        }
        return result;
    }

    /// <summary>
    /// DIAGNOSTIC: sample raw (areaChangeId, path) pairs across all buckets WITHOUT the current-area
    /// filter, plus the per-bucket node counts. Lets the Research probe confirm the struct layout
    /// (are real paths read?) and see which area-change indices the files actually carry — independent
    /// of whether any match the live counter. Read-only; bounded by <paramref name="maxSamples"/>.
    /// </summary>
    public (List<(int AreaChangeId, string Path)> Samples, List<long> BucketCounts) RawSample(int maxSamples = 60)
    {
        var samples = new List<(int, string)>();
        var bucketCounts = new List<long>();
        if (!Available) return (samples, bucketCounts);

        var rootPtr = Ptr(FileRootSlot);
        if (rootPtr == 0) return (samples, bucketCounts);

        for (var r = 0; r < Poe2.Preload.RootObjectCount; r++)
        {
            var bucket = rootPtr + (nint)(r * Poe2.Preload.RootObjectStride);
            if (!_reader.TryReadStruct<StdVector>(bucket, out var vec) || vec.First == 0) { bucketCounts.Add(-1); continue; }
            var count = ((long)vec.Last - (long)vec.First) / Poe2.Preload.FileNodeStride;
            bucketCounts.Add(count);
            if (count is <= 0 or > 5_000_000) continue;
            // Count every bucket (cheap StdVector read above), but only collect sample rows up to the cap.
            for (long i = 0; i < count && samples.Count < maxSamples; i++)
            {
                var info = Ptr(vec.First + (nint)(i * Poe2.Preload.FileNodeStride) + Poe2.Preload.FileInfoPtr);
                if (info == 0) continue;
                _reader.TryReadStruct<int>(info + Poe2.Preload.FileAreaChangeId, out var ac);
                var name = ReadStdWString(info + Poe2.Preload.FileNameStr);
                if (!string.IsNullOrEmpty(name)) samples.Add((ac, name));
            }
        }
        return (samples, bucketCounts);
    }

    /// <summary>Capacity-aware std::wstring read (Length@+0x10, Capacity@+0x18): inline buffer when
    /// capacity ≤ 8 wchars, else the heap pointer at +0x00. (More correct than a length heuristic —
    /// matters for the long file paths here.)</summary>
    private string ReadStdWString(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 4096) return string.Empty;
        var hasCap = _reader.TryReadStruct<int>(addr + 0x18, out var cap);
        if (hasCap && cap <= 8) return _reader.ReadStringUtf16(addr, len); // small-string-optimized, stored inline
        var ptr = Ptr(addr);
        return ptr == 0 ? string.Empty : _reader.ReadStringUtf16(ptr, len);
    }

    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : p;
    }
}
