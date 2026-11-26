using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Physics;

namespace GrowSandbox.Tests;

public class TileCollisionTests
{
    private static Tile[] CreateFlatWorld()
    {
        var tiles = new Tile[WorldConstants.TotalTiles];
        for (int y = 30; y < WorldConstants.Height; y++)
        {
            int rowOffset = y * WorldConstants.Width;
            for (int x = 0; x < WorldConstants.Width; x++)
            {
                tiles[rowOffset + x] = new Tile(TileIds.Dirt, TileIds.DirtBackground);
            }
        }

        tiles[Tile.GetIndex(50, 29)] = new Tile(TileIds.Spawn, TileIds.Air, flags: TileFlags.Locked);
        return tiles;
    }

    [Fact]
    public void MoveAndSlide_FallingPlayer_LandsFlushOnSolidGround()
    {
        Tile[] tiles = CreateFlatWorld();
        float x = 50 * WorldConstants.TileSize + 4f;
        float y = 30 * WorldConstants.TileSize - PhysicsConstants.PlayerHeight - 5f;

        CollisionResolutionResult result = TileCollision.MoveAndSlide(
            tiles,
            x,
            y,
            PhysicsConstants.PlayerWidth,
            PhysicsConstants.PlayerHeight,
            velocityX: 0f,
            velocityY: 600f,
            dt: 1f / 60f);

        Assert.True(result.IsGrounded);
        Assert.Equal(0f, result.VelocityY);
        Assert.Equal(30 * WorldConstants.TileSize - PhysicsConstants.PlayerHeight, result.Y);
    }

    [Fact]
    public void MoveAndSlide_WalkingIntoSolidWall_StopsFlushAtWall()
    {
        Tile[] tiles = CreateFlatWorld();
        // Buat dinding di kolom x=52, baris y=28..29
        tiles[Tile.GetIndex(52, 28)] = new Tile(TileIds.Dirt);
        tiles[Tile.GetIndex(52, 29)] = new Tile(TileIds.Dirt);

        (float startX, float startY) = TileCollision.GetSpawnPosition(51, 29);

        CollisionResolutionResult result = TileCollision.MoveAndSlide(
            tiles,
            startX,
            startY,
            PhysicsConstants.PlayerWidth,
            PhysicsConstants.PlayerHeight,
            velocityX: PhysicsConstants.MoveSpeed,
            velocityY: 0f,
            dt: 0.1f);

        Assert.True(result.HitWallRight);
        Assert.Equal(0f, result.VelocityX);
        Assert.Equal(52 * WorldConstants.TileSize - PhysicsConstants.PlayerWidth, result.X);
    }

    [Fact]
    public void MoveAndSlide_JumpingIntoCeiling_StopsBelowCeiling()
    {
        Tile[] tiles = CreateFlatWorld();
        // Buat langit-langit tepat di baris y=27
        tiles[Tile.GetIndex(50, 27)] = new Tile(TileIds.Dirt);

        (float startX, float startY) = TileCollision.GetSpawnPosition(50, 29);

        CollisionResolutionResult result = TileCollision.MoveAndSlide(
            tiles,
            startX,
            startY,
            PhysicsConstants.PlayerWidth,
            PhysicsConstants.PlayerHeight,
            velocityX: 0f,
            velocityY: -PhysicsConstants.JumpForce,
            dt: 0.1f);

        Assert.True(result.HitCeiling);
        Assert.Equal(0f, result.VelocityY);
        Assert.Equal(28 * WorldConstants.TileSize, result.Y);
    }

    [Fact]
    public void MoveAndSlide_WhiteDoorAndSpawnTile_ArePassable()
    {
        Tile[] tiles = CreateFlatWorld();
        tiles[Tile.GetIndex(50, 29)] = new Tile(TileIds.WhiteDoor, TileIds.Air, flags: TileFlags.Locked);
        (float startX, float startY) = TileCollision.GetSpawnPosition(49, 29);

        CollisionResolutionResult result = TileCollision.MoveAndSlide(
            tiles,
            startX,
            startY,
            PhysicsConstants.PlayerWidth,
            PhysicsConstants.PlayerHeight,
            velocityX: PhysicsConstants.MoveSpeed,
            velocityY: 0f,
            dt: 0.1f);

        Assert.False(TileCollision.IsSolidTile(in tiles[Tile.GetIndex(50, 29)]));
        Assert.False(result.HitWallRight);
        Assert.True(result.X > startX);
    }

    [Fact]
    public void MoveAndSlide_ClampsToWorldBoundaries()
    {
        Tile[] tiles = CreateFlatWorld();

        CollisionResolutionResult leftResult = TileCollision.MoveAndSlide(
            tiles,
            x: 2f,
            y: 100f,
            PhysicsConstants.PlayerWidth,
            PhysicsConstants.PlayerHeight,
            velocityX: -PhysicsConstants.MoveSpeed,
            velocityY: 0f,
            dt: 0.1f);

        Assert.True(leftResult.HitWallLeft);
        Assert.Equal(0f, leftResult.X);
    }
}
