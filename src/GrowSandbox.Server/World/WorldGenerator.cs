using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;

namespace GrowSandbox.Server.World;

/// <summary>
/// Generator dunia standar 100x60:
/// - Baris 0-29  : Udara (Air) dengan satu WhiteDoor (Main Door) di titik spawn (50, 29)
/// - Baris 30-54 : Tanah (Dirt) dengan kantong batu (Stone) di kedalaman; seluruhnya terisi CaveBackground
/// - Baris 55-59 : Bedrock terkunci dengan CaveBackground
/// - Tepat di bawah WhiteDoor (50, 30) diletakkan 1 blok Bedrock agar lantai spawn tidak bisa digali.
/// </summary>
public static class WorldGenerator
{
    public const int AirStartRow = 0;
    public const int AirEndRow = WorldConstants.SurfaceY - 1;
    public const int SurfaceRow = WorldConstants.SurfaceY;
    public const int DirtStartRow = SurfaceRow;
    public const int DirtEndRow = 54;
    public const int BedrockStartRow = 55;
    public const int BedrockEndRow = 59;

    public const int SpawnTileX = WorldConstants.Width / 2;
    public const int SpawnTileY = SurfaceRow - 1;

    public static Tile[] Generate(string? worldName = null)
    {
        var tiles = new Tile[WorldConstants.TotalTiles];
        int seed = string.IsNullOrEmpty(worldName)
            ? 1337
            : StringComparer.OrdinalIgnoreCase.GetHashCode(worldName);

        // Baris 0-29 dibiarkan kosong (ForegroundId = 0, BackgroundId = 0) agar terpapar langit terbuka.

        // Baris 30-54: Tanah (Dirt) + kantong batu (Stone) di kedalaman, seluruhnya memiliki CaveBackground.
        // Lapisan rumput pada baris permukaan digambar otomatis oleh renderer hanya bila tile di atasnya terbuka ke langit.
        for (int y = DirtStartRow; y <= DirtEndRow; y++)
        {
            int rowOffset = y * WorldConstants.Width;
            for (int x = 0; x < WorldConstants.Width; x++)
            {
                ushort fg = TileIds.Dirt;
                if (y >= 36 && y <= 53)
                {
                    int hash = HashCoords(x, y, seed);
                    int stoneChance = y >= 45 ? 18 : 10;
                    if ((hash & 0xFF) < stoneChance)
                    {
                        fg = TileIds.Stone;
                    }
                }

                tiles[rowOffset + x] = new Tile(
                    foregroundId: fg,
                    backgroundId: TileIds.CaveBackground);
            }
        }

        // Baris 55-59: Bedrock terkunci (tidak dapat dihancurkan) dengan CaveBackground konsisten.
        for (int y = BedrockStartRow; y <= BedrockEndRow; y++)
        {
            int rowOffset = y * WorldConstants.Width;
            for (int x = 0; x < WorldConstants.Width; x++)
            {
                tiles[rowOffset + x] = new Tile(
                    foregroundId: TileIds.Bedrock,
                    backgroundId: TileIds.CaveBackground,
                    flags: TileFlags.Locked);
            }
        }

        // WhiteDoor (Main Door) di koordinat spawn (spawnX, surfaceY - 1): non-solid & indestructible.
        tiles[Tile.GetIndex(SpawnTileX, SpawnTileY)] = new Tile(
            foregroundId: TileIds.WhiteDoor,
            backgroundId: TileIds.Air,
            flags: TileFlags.Locked);

        // 1 blok Bedrock tepat di bawah WhiteDoor (spawnX, surfaceY) agar lantai spawn tidak bisa digali.
        tiles[Tile.GetIndex(SpawnTileX, SurfaceRow)] = new Tile(
            foregroundId: TileIds.Bedrock,
            backgroundId: TileIds.CaveBackground,
            flags: TileFlags.Locked);

        return tiles;
    }

    private static int HashCoords(int x, int y, int seed)
    {
        unchecked
        {
            int h = seed;
            h = (h * 397) ^ (x * 73856093);
            h = (h * 397) ^ (y * 19349663);
            h ^= h >> 13;
            h *= 1274126177;
            h ^= h >> 16;
            return h;
        }
    }
}
