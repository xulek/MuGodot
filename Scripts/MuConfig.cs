namespace MuGodot;

/// <summary>
/// Configuration for MU Online data paths and terrain constants.
/// </summary>
public static class MuConfig
{
    /// <summary>
    /// Path to the MU Online client data folder (containing World1/, Object1/, etc.)
    /// Set this before loading any world data.
    /// </summary>
    public static string DataPath { get; set; } = @"C:\Games\MU_Red_1_20_61_Full\Data";

    // Terrain constants (matching MU Online)
    public const int TerrainSize = 256;
    public const float TerrainScale = 100f;
    public const float HeightMultiplier = 1.5f;

    // Scale factor to convert MU world units to Godot units
    // 1 MU grid cell (100 units) = 1 Godot unit
    public const float WorldToGodot = 1f / TerrainScale;
}
