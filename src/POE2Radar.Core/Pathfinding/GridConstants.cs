namespace POE2Radar.Core.Pathfinding;

/// <summary>
/// Constants for grid ↔ world conversion and network-bubble bounds.
/// All bot/system code uses grid units; world units appear only at two call sites:
/// movement target submission (entity-grid * <see cref="GridToWorld"/>) and screen
/// projection (Camera.WorldToScreen on a world-space vector).
/// </summary>
public static class GridConstants
{
    /// <summary>One grid cell = 250/23 ≈ 10.8696 world units (PoE2: TileToWorld 250 / TileToGrid 23).</summary>
    public const float GridToWorld = 250f / 23f;
    public const float WorldToGrid = 1f / GridToWorld;

    /// <summary>
    /// Conservative network bubble radius in grid units. Entities first appear in the
    /// client entity list at ~200-215 grid distance from the player; 180 is a safe
    /// "we've seen everything that exists here" threshold.
    /// </summary>
    public const int NetworkBubbleGrid = 180;
}
