namespace POE2Radar.Core.Game;

/// <summary>
/// Stores byte patterns for AOB-scanning PoE.exe's .text section to locate global pointer slots.
/// Each pattern targets a REX.W MOV reg,[RIP+rel32] instruction that loads a root game object.
///
/// How to populate after a PoE patch:
///   1. Run: POE2Radar.Research --discover-aob
///      (requires PoE running + POEMCP active to supply the ground-truth addresses)
///   2. The tool prints pattern literals like:
///         new byte?[] { 0x48, 0x8B, 0x0D, null, null, null, null, 0xE8, ... }
///         // DispOffset=3, InstrLen=7
///   3. Paste into the appropriate array below.
///
/// How patterns are used at runtime:
///   AobScanner.ScanForResolvedAddresses finds .text matches, resolves RIP+disp â†’ global slot address.
///   ReadPointer(slotAddress) = the live heap address of the game object.
///   If no patterns are stored (arrays empty), startup falls back to value-scan (--hp N argument).
/// </summary>
public static class AobPatterns
{
    public sealed record Pattern(
        byte?[] Bytes,
        int DispOffset,
        int InstrLen,
        string Description);

    /// <summary>
    /// Patterns that RIP-reference the global pointer slot for IngameState.
    /// The resolved slot address, when dereferenced, gives the IngameState heap object.
    ///
    /// Populate by running: POE2Radar.Research --discover-aob
    /// </summary>
    public static readonly Pattern[] IngameStateRefs =
    [
        new Pattern(
            Bytes: new byte?[] {
                0x8B, 0xC7, 0x48, 0x83, 0xC4, 0x40, 0x5F, 0xC3,
                0x48, 0x8B, 0x05, null, null, null, null,
                0x48, 0x89, 0x07, 0x48
            },
            // Pattern includes 8 prefix bytes before the MOV — adjust offsets accordingly.
            // Instruction-relative DispOffset=3, InstrLen=7 → pattern-relative 8+3=11, 8+7=15.
            DispOffset:  11,
            InstrLen:    15,
            Description: "IngameState global slot — PoE build 2026-05-05 (validated by --discover-aob)"),
    ];

    /// <summary>
    /// PoE2 "Game States" pattern (GameHelper2 StaticOffsetsPatterns.cs):
    ///   <c>48 39 2D ^ ?? ?? ?? ?? 0F 85 16 01 00 00</c>
    /// The instruction is <c>cmp [rip+rel32], rbp</c> (48 39 2D + rel32 = 7 bytes); the rel32
    /// resolves to the GameStates global pointer slot. Deref the slot → GameState root.
    /// The trailing <c>0F 85 …</c> (jnz) is included for uniqueness.
    /// </summary>
    public static readonly Pattern[] GameStateRefs =
    [
        new Pattern(
            Bytes: new byte?[] {
                0x48, 0x39, 0x2D, null, null, null, null,
                0x0F, 0x85, 0x16, 0x01, 0x00, 0x00
            },
            DispOffset:  3,   // rel32 starts after 48 39 2D
            InstrLen:    7,   // cmp [rip+rel32], rbp
            Description: "PoE2 GameStates global slot (GameHelper2 'Game States')"),
    ];

    /// <summary>
    /// Pattern that RIP-references the <b>File Root</b> global slot (the loaded-files table root, for
    /// preload alerts — A1). Seeded from GameHelper2's PoE1 "File Root" pattern:
    ///   <c>48 8D 0D ?? ?? ?? ?? E8 ?? ?? ?? ?? 48 8B 05 ^ ?? ?? ?? ?? 48 83 C4 28</c>
    /// The captured instruction is the <c>mov rax,[rip+rel32]</c> at pattern index 12 (rel32 at 15,
    /// instruction ends at 19); deref the resolved slot → the file-table root.
    ///
    /// <para>✓ validated live 2026-06-09 — the GH2/PoE1 signature matches PoE2 unchanged and resolves the
    /// File Root slot (deref → a table whose buckets hold readable file paths). Re-verify with <c>--preload</c>
    /// after a patch.</para>
    /// </summary>
    public static readonly Pattern[] FileRootRefs =
    [
        new Pattern(
            Bytes: new byte?[] {
                0x48, 0x8D, 0x0D, null, null, null, null,
                0xE8, null, null, null, null,
                0x48, 0x8B, 0x05, null, null, null, null,
                0x48, 0x83, 0xC4, 0x28
            },
            DispOffset:  15,  // rel32 of the captured mov, pattern-relative
            InstrLen:    19,  // captured mov ends at pattern index 19
            Description: "File Root global slot (GameHelper2 'File Root' — ✓ matches PoE2 2026-06-09)"),
    ];

    /// <summary>
    /// Pattern that RIP-references the <b>AreaChangeCounter</b> global (incremented on every area load;
    /// the preload reader keeps only files whose load-index equals its live value). Seeded from
    /// GameHelper2's PoE1 pattern:
    ///   <c>FF 05 ^ ?? ?? ?? ?? 4D 8B 06</c>  (<c>inc dword [rip+rel32]</c>, 6 bytes; rel32 at index 2).
    ///
    /// <para>✓ validated live 2026-06-09 — matches PoE2 unchanged; the resolved slot reads a small positive
    /// int that increments on area load.</para>
    /// </summary>
    public static readonly Pattern[] AreaChangeCounterRefs =
    [
        new Pattern(
            Bytes: new byte?[] {
                0xFF, 0x05, null, null, null, null,
                0x4D, 0x8B, 0x06
            },
            DispOffset:  2,   // rel32 starts right after FF 05
            InstrLen:    6,   // inc dword [rip+rel32] is 6 bytes
            Description: "AreaChangeCounter global slot (GameHelper2 'AreaChangeCounter' — ✓ matches PoE2 2026-06-09)"),
    ];

    /// <summary>
    /// Pattern that locates a <b>field-offset</b> within a struct, by matching an instruction
    /// whose displacement IS the field offset. Example: <c>mov rax, [rdx+0x218]</c> bytes
    /// <c>48 8B 82 18 02 00 00</c> — the four <c>18 02 00 00</c> bytes encode the offset.
    ///
    /// <para>To extract the offset after a patch:</para>
    /// <list type="number">
    ///   <item>AOB-scan for the surrounding pattern (the wildcards mark the disp bytes).</item>
    ///   <item>Read <see cref="DispWidth"/> bytes at <see cref="DispOffsetInMatch"/> within the
    ///         matched bytes — that's the new field offset (sign-extended for disp8).</item>
    /// </list>
    ///
    /// <para><b>Stability requires a unique signature.</b> The bare instruction shape
    /// (<c>48 8B 82 ?? ?? ?? ??</c>) repeats throughout PoE.exe — without surrounding context
    /// the pattern matches many unrelated accesses. Each catalog entry needs enough prefix or
    /// suffix bytes that exactly one match exists in <c>.text</c>. Generating those signatures
    /// is currently manual disassembly work; <see cref="AobScanner.FindFieldAccessHits"/>
    /// helps by listing every candidate hit with surrounding context.</para>
    /// </summary>
    public sealed record FieldOffsetPattern(
        string FieldName,
        byte?[] Bytes,
        int     DispOffsetInMatch,   // byte position WITHIN Bytes where the displacement starts
        int     DispWidth,           // 1 (disp8 sign-extended) or 4 (disp32)
        string  Description);

    /// <summary>
    /// Catalog of field-offset patterns. Empty for now — populating requires manual
    /// disassembly to find unique-enough surrounding bytes per offset. Once an entry exists,
    /// <see cref="AobScanner.ExtractFieldOffset"/> recovers the new value automatically per
    /// patch.
    /// </summary>
    public static readonly FieldOffsetPattern[] FieldPatterns = Array.Empty<FieldOffsetPattern>();
}
