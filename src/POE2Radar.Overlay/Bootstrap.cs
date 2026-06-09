using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay;

/// <summary>
/// Resolves the PoE2 GameState global-pointer slot via the "Game States" AOB pattern, validated
/// by confirming the full chain resolves to a real local player. Returns the slot address (the
/// thing the RIP-relative instruction points at); deref it each tick to get the live GameState.
/// </summary>
internal static class Bootstrap
{
    public static nint ResolveGameStateSlot(ProcessHandle process, MemoryReader reader)
    {
        if (AobPatterns.GameStateRefs.Length == 0)
        {
            Console.Error.WriteLine("No GameState AOB patterns committed.");
            return 0;
        }

        Console.WriteLine("Scanning for GameState via 'Game States' AOB pattern...");
        foreach (var pattern in AobPatterns.GameStateRefs)
        {
            foreach (var slot in AobScanner.ScanForResolvedAddresses(process, reader, pattern).Distinct())
            {
                var live = new Poe2Live(reader, slot);
                if (live.TryResolve(out _, out _, out var localPlayer))
                {
                    Console.WriteLine($"  GameState slot: 0x{slot:X16}  (LocalPlayer 0x{localPlayer:X16})");
                    return slot;
                }
            }
        }

        Console.Error.WriteLine("Pattern matched but no slot resolved to an in-game chain.");
        Console.Error.WriteLine("Make sure you're loaded into a zone (not at login / character select).");
        return 0;
    }

    /// <summary>
    /// Best-effort resolve of the two globals the preload-alert reader needs (A1): the File Root slot
    /// and the AreaChangeCounter slot, via their AOB patterns. Returns (0, 0) for whichever doesn't
    /// resolve — the preload feature then stays inert. Non-fatal: never throws, never blocks startup.
    ///
    /// <para>⚠ The seeded patterns are PoE1 and will almost certainly NOT match PoE2 yet; rediscover
    /// them (see <see cref="AobPatterns.FileRootRefs"/> / <see cref="AobPatterns.AreaChangeCounterRefs"/>)
    /// or validate with <c>POE2Radar.Research --preload</c>.</para>
    /// </summary>
    public static (nint FileRoot, nint AreaChange) ResolvePreloadSlots(ProcessHandle process, MemoryReader reader)
    {
        nint fileRoot = 0, areaChange = 0;
        try
        {
            // File Root: pick the first match whose deref looks like a user-mode pointer (the table root).
            foreach (var pat in AobPatterns.FileRootRefs)
            {
                foreach (var slot in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                    if (reader.TryReadStruct<nint>(slot, out var p) && (ulong)p is > 0x10000 and < 0x7FFFFFFFFFFF)
                    { fileRoot = slot; break; }
                if (fileRoot != 0) break;
            }

            // AreaChangeCounter: pick the first match holding a small positive int (the area-load index).
            foreach (var pat in AobPatterns.AreaChangeCounterRefs)
            {
                foreach (var slot in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                    if (reader.TryReadStruct<int>(slot, out var v) && v is > 0 and < 10_000_000)
                    { areaChange = slot; break; }
                if (areaChange != 0) break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Preload slot resolve skipped: {ex.Message}");
        }
        return (fileRoot, areaChange);
    }
}
