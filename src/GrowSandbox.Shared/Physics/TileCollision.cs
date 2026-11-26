using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;

namespace GrowSandbox.Shared.Physics;

/// <summary>
/// Kotak batas AABB 2D dalam koordinat piksel dunia.
/// </summary>
public readonly record struct Aabb(float X, float Y, float Width, float Height)
{
    public float Left => X;
    public float Right => X + Width;
    public float Top => Y;
    public float Bottom => Y + Height;

    public bool Intersects(in Aabb other) =>
        Left < other.Right &&
        Right > other.Left &&
        Top < other.Bottom &&
        Bottom > other.Top;
}

/// <summary>
/// Hasil resolusi pergerakan dan tabrakan AABB 2D pada satu langkah simulasi.
/// </summary>
public readonly record struct CollisionResolutionResult(
    float X,
    float Y,
    float VelocityX,
    float VelocityY,
    bool IsGrounded,
    bool HitCeiling,
    bool HitWallLeft,
    bool HitWallRight);

/// <summary>
/// Resolusi tabrakan AABB 2D deterministik antara hitbox pemain dan array flat 1D Tile[] (100x60).
/// Digunakan bersama oleh Client-Side Prediction dan validasi Server Otoritatif.
/// </summary>
public static class TileCollision
{
    private const float SkinEpsilon = 0.001f;

    public static bool IsSolidTile(in Tile tile) =>
        ItemCatalog.IsSolid(tile.ForegroundId);

    public static (float X, float Y) GetSpawnPosition(int spawnTileX, int spawnTileY)
    {
        float x = spawnTileX * WorldConstants.TileSize + (WorldConstants.TileSize - PhysicsConstants.PlayerWidth) * 0.5f;
        float y = (spawnTileY + 1) * WorldConstants.TileSize - PhysicsConstants.PlayerHeight;
        return (x, y);
    }

    /// <summary>
    /// Memeriksa apakah target tile berada dalam jarak jangkauan maksimum pemain (<see cref="PhysicsConstants.MaxReachTiles"/>).
    /// Jarak diukur dari pusat hitbox pemain ke titik terdekat pada AABB tile target.
    /// </summary>
    public static bool IsWithinReach(float playerX, float playerY, int tileX, int tileY)
    {
        if (!Tile.IsInBounds(tileX, tileY))
        {
            return false;
        }

        float centerX = playerX + PhysicsConstants.PlayerWidth * 0.5f;
        float centerY = playerY + PhysicsConstants.PlayerHeight * 0.5f;

        float tileLeft = tileX * WorldConstants.TileSize;
        float tileTop = tileY * WorldConstants.TileSize;
        float tileRight = tileLeft + WorldConstants.TileSize;
        float tileBottom = tileTop + WorldConstants.TileSize;

        float closestX = Math.Clamp(centerX, tileLeft, tileRight);
        float closestY = Math.Clamp(centerY, tileTop, tileBottom);

        float dx = centerX - closestX;
        float dy = centerY - closestY;
        float maxReachPx = PhysicsConstants.MaxReachPixels;

        return (dx * dx) + (dy * dy) <= maxReachPx * maxReachPx;
    }

    /// <summary>
    /// Memeriksa apakah AABB pemain bertumpukan dengan kotak tile tertentu.
    /// Digunakan untuk mencegah penempatan blok padat menimpa tubuh pemain.
    /// </summary>
    public static bool IntersectsTile(
        float playerX,
        float playerY,
        float playerWidth,
        float playerHeight,
        int tileX,
        int tileY)
    {
        var playerBox = new Aabb(playerX, playerY, playerWidth, playerHeight);
        var tileBox = new Aabb(
            tileX * WorldConstants.TileSize,
            tileY * WorldConstants.TileSize,
            WorldConstants.TileSize,
            WorldConstants.TileSize);

        return playerBox.Intersects(in tileBox);
    }

    public static bool OverlapsSolidTile(
        ReadOnlySpan<Tile> tiles,
        float x,
        float y,
        float width,
        float height,
        float inset = 0f)
    {
        if (tiles.Length != WorldConstants.TotalTiles)
        {
            return false;
        }

        float left = x + inset;
        float right = x + width - inset;
        float top = y + inset;
        float bottom = y + height - inset;

        if (left >= right || top >= bottom)
        {
            return false;
        }

        int tileSize = WorldConstants.TileSize;
        int minX = Math.Clamp((int)MathF.Floor(left / tileSize), 0, WorldConstants.Width - 1);
        int maxX = Math.Clamp((int)MathF.Floor((right - SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);
        int minY = Math.Clamp((int)MathF.Floor(top / tileSize), 0, WorldConstants.Height - 1);
        int maxY = Math.Clamp((int)MathF.Floor((bottom - SkinEpsilon) / tileSize), 0, WorldConstants.Height - 1);

        for (int ty = minY; ty <= maxY; ty++)
        {
            int rowOffset = ty * WorldConstants.Width;
            for (int tx = minX; tx <= maxX; tx++)
            {
                if (IsSolidTile(in tiles[rowOffset + tx]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Memindahkan hitbox AABB sebesar <c>(velocityX * dt, velocityY * dt)</c> dengan pemisahan sumbu X lalu Y,
    /// serta menyelesaikan tabrakan terhadap tile padat dan batas dunia.
    /// </summary>
    public static CollisionResolutionResult MoveAndSlide(
        ReadOnlySpan<Tile> tiles,
        float x,
        float y,
        float width,
        float height,
        float velocityX,
        float velocityY,
        float dt)
    {
        if (tiles.Length != WorldConstants.TotalTiles)
        {
            throw new ArgumentException(
                $"Array tile wajib berukuran {WorldConstants.TotalTiles} elemen.",
                nameof(tiles));
        }

        if (dt <= 0f)
        {
            bool standing = HasGroundBelow(tiles, x, y, width, height);
            return new CollisionResolutionResult(x, y, velocityX, velocityY, standing, false, false, false);
        }

        int tileSize = WorldConstants.TileSize;
        bool hitWallLeft = false;
        bool hitWallRight = false;
        bool hitCeiling = false;
        bool isGrounded = false;

        // 1. Resolusi sumbu horizontal (X)
        float newX = x + velocityX * dt;

        if (newX < 0f)
        {
            newX = 0f;
            velocityX = 0f;
            hitWallLeft = true;
        }
        else if (newX + width > WorldConstants.WorldPixelWidth)
        {
            newX = WorldConstants.WorldPixelWidth - width;
            velocityX = 0f;
            hitWallRight = true;
        }

        if (velocityX != 0f)
        {
            int minTileY = Math.Clamp((int)MathF.Floor((y + SkinEpsilon) / tileSize), 0, WorldConstants.Height - 1);
            int maxTileY = Math.Clamp((int)MathF.Floor((y + height - SkinEpsilon) / tileSize), 0, WorldConstants.Height - 1);

            if (velocityX > 0f)
            {
                int startTx = Math.Clamp((int)MathF.Floor((x + width - SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);
                int endTx = Math.Clamp((int)MathF.Floor((newX + width - SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);

                for (int tx = startTx; tx <= endTx; tx++)
                {
                    if (HasSolidInColumn(tiles, tx, minTileY, maxTileY))
                    {
                        newX = tx * tileSize - width;
                        velocityX = 0f;
                        hitWallRight = true;
                        break;
                    }
                }
            }
            else
            {
                int startTx = Math.Clamp((int)MathF.Floor(x / tileSize), 0, WorldConstants.Width - 1);
                int endTx = Math.Clamp((int)MathF.Floor(newX / tileSize), 0, WorldConstants.Width - 1);

                for (int tx = startTx; tx >= endTx; tx--)
                {
                    if (HasSolidInColumn(tiles, tx, minTileY, maxTileY))
                    {
                        newX = (tx + 1) * tileSize;
                        velocityX = 0f;
                        hitWallLeft = true;
                        break;
                    }
                }
            }
        }

        // 2. Resolusi sumbu vertikal (Y)
        float newY = y + velocityY * dt;

        if (newY < 0f)
        {
            newY = 0f;
            velocityY = 0f;
            hitCeiling = true;
        }
        else if (newY + height > WorldConstants.WorldPixelHeight)
        {
            newY = WorldConstants.WorldPixelHeight - height;
            velocityY = 0f;
            isGrounded = true;
        }

        if (velocityY > 0f)
        {
            int minTileX = Math.Clamp((int)MathF.Floor((newX + SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);
            int maxTileX = Math.Clamp((int)MathF.Floor((newX + width - SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);
            int startTy = Math.Clamp((int)MathF.Floor((y + height - SkinEpsilon) / tileSize), 0, WorldConstants.Height - 1);
            int endTy = Math.Clamp((int)MathF.Floor((newY + height - SkinEpsilon) / tileSize), 0, WorldConstants.Height - 1);

            for (int ty = startTy; ty <= endTy; ty++)
            {
                if (HasSolidInRow(tiles, ty, minTileX, maxTileX))
                {
                    newY = ty * tileSize - height;
                    velocityY = 0f;
                    isGrounded = true;
                    break;
                }
            }
        }
        else if (velocityY < 0f)
        {
            int minTileX = Math.Clamp((int)MathF.Floor((newX + SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);
            int maxTileX = Math.Clamp((int)MathF.Floor((newX + width - SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);
            int startTy = Math.Clamp((int)MathF.Floor(y / tileSize), 0, WorldConstants.Height - 1);
            int endTy = Math.Clamp((int)MathF.Floor(newY / tileSize), 0, WorldConstants.Height - 1);

            for (int ty = startTy; ty >= endTy; ty--)
            {
                if (HasSolidInRow(tiles, ty, minTileX, maxTileX))
                {
                    newY = (ty + 1) * tileSize;
                    velocityY = 0f;
                    hitCeiling = true;
                    break;
                }
            }
        }
        else if (!isGrounded)
        {
            isGrounded = HasGroundBelow(tiles, newX, newY, width, height);
        }

        return new CollisionResolutionResult(
            newX,
            newY,
            velocityX,
            velocityY,
            isGrounded,
            hitCeiling,
            hitWallLeft,
            hitWallRight);
    }

    private static bool HasGroundBelow(ReadOnlySpan<Tile> tiles, float x, float y, float width, float height)
    {
        float footProbeY = y + height + SkinEpsilon;
        if (footProbeY >= WorldConstants.WorldPixelHeight)
        {
            return true;
        }

        int tileSize = WorldConstants.TileSize;
        int ty = Math.Clamp((int)MathF.Floor(footProbeY / tileSize), 0, WorldConstants.Height - 1);
        int minTileX = Math.Clamp((int)MathF.Floor((x + SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);
        int maxTileX = Math.Clamp((int)MathF.Floor((x + width - SkinEpsilon) / tileSize), 0, WorldConstants.Width - 1);

        return HasSolidInRow(tiles, ty, minTileX, maxTileX);
    }

    private static bool HasSolidInColumn(ReadOnlySpan<Tile> tiles, int tx, int minTy, int maxTy)
    {
        for (int ty = minTy; ty <= maxTy; ty++)
        {
            if (IsSolidTile(in tiles[ty * WorldConstants.Width + tx]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasSolidInRow(ReadOnlySpan<Tile> tiles, int ty, int minTx, int maxTx)
    {
        int rowOffset = ty * WorldConstants.Width;
        for (int tx = minTx; tx <= maxTx; tx++)
        {
            if (IsSolidTile(in tiles[rowOffset + tx]))
            {
                return true;
            }
        }

        return false;
    }
}
