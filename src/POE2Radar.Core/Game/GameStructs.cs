using System.Runtime.InteropServices;

namespace POE2Radar.Core.Game;

/// <summary>
/// Blittable structs mirroring PoE2 memory layout for direct <c>ReadStruct&lt;T&gt;</c> reads.
/// </summary>

/// <summary>std::vector — 3 pointers (first/last/end). Count = (last-first)/elementSize.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StdVector
{
    public nint First;
    public nint Last;
    public nint End;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector2
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
public struct Vector3
{
    public float X;
    public float Y;
    public float Z;
}

/// <summary>
/// Health / Mana / EnergyShield pool. ReservedFlat@0x10, ReservedFraction@0x14 (e.g. 2023 = 20.23%),
/// Regen@0x28, Max@0x2C, Current@0x30. Layout validated live (PoE1-lineage; unchanged in PoE2).
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x34)]
public struct VitalStruct
{
    [FieldOffset(0x10)] public int ReservedFlat;
    [FieldOffset(0x14)] public int ReservedFraction;
    [FieldOffset(0x28)] public float Regen;
    [FieldOffset(0x2C)] public int Max;
    [FieldOffset(0x30)] public int Current;

    public readonly bool LooksValid()
    {
        if (Max <= 0 || Max > 10_000_000) return false;
        if (Current < -Max || Current > Max + 1) return false;
        return ReservedFlat >= 0 && ReservedFlat <= Max;
    }
}

/// <summary>
/// One active buff/debuff (status effect) on an entity, mirroring GameHelper2's StatusEffectStruct.
/// <c>BuffDefinitionPtr</c> → a BuffDefinitions.dat row whose +0x00 field is a pointer to the buff's
/// (icon/internal) name. <c>TimeLeft</c>/<c>TotalTime</c> are seconds (∞ = permanent aura). Layout is
/// GH2-sourced (PoE1 lineage) and UNVALIDATED on PoE2 — the field offsets may have drifted; a wrong
/// layout yields implausible values, which <see cref="LooksValid"/> rejects. Validate via
/// <c>POE2Radar.Research --buffs</c>.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x50)]
public struct StatusEffectStruct
{
    [FieldOffset(0x08)] public nint BuffDefinitionPtr; // → BuffDefinitions.dat row; row +0x00 → name ptr
    [FieldOffset(0x18)] public float TotalTime;        // seconds (may be +Infinity for permanent)
    [FieldOffset(0x1C)] public float TimeLeft;         // seconds remaining
    [FieldOffset(0x40)] public short Charges;          // stack count (e.g. wither / ailment stacks)

    /// <summary>Cheap sanity gate so a drifted/garbage layout doesn't surface nonsense buffs: a real
    /// status effect has a user-mode definition pointer and non-negative, finite-ish timers.</summary>
    public readonly bool LooksValid()
    {
        var p = (ulong)BuffDefinitionPtr;
        if (p < 0x10000 || p > 0x7FFFFFFFFFFF) return false;
        // TimeLeft can be +Infinity (permanent auras) — accept that, reject NaN / wildly negative.
        if (float.IsNaN(TimeLeft) || float.IsNaN(TotalTime)) return false;
        if (TimeLeft < -1f || TotalTime < -1f) return false;
        return Charges is >= 0 and < 10000;
    }
}
