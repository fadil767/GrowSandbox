namespace GrowSandbox.Shared.Constants;

/// <summary>
/// Konstanta dimensi grid dunia dan ukuran tile yang menjadi sumber kebenaran tunggal.
/// </summary>
public static class WorldConstants
{
    public const int Width = 100;
    public const int Height = 60;
    public const int TotalTiles = Width * Height;

    /// <summary>Baris permukaan tanah standar dunia (baris 0..29 langit, 30..54 tanah, 55..59 bedrock).</summary>
    public const int SurfaceY = 30;

    public const int TileSize = 32;
    public const int TileByteSize = 8;

    public const int WorldPixelWidth = Width * TileSize;
    public const int WorldPixelHeight = Height * TileSize;
}
