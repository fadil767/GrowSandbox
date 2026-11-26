using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;

namespace GrowSandbox.Tests;

public class TileLayoutTests
{
    [Fact]
    public void TileStruct_MustBeExactly8Bytes()
    {
        Assert.Equal(8, Marshal.SizeOf<Tile>());
        Assert.Equal(8, Unsafe.SizeOf<Tile>());
        Assert.Equal(WorldConstants.TileByteSize, Marshal.SizeOf<Tile>());
    }

    [Fact]
    public void TileStruct_FieldOffsets_MatchSequentialPack1Layout()
    {
        Assert.Equal((nint)0, Marshal.OffsetOf<Tile>(nameof(Tile.ForegroundId)));
        Assert.Equal((nint)2, Marshal.OffsetOf<Tile>(nameof(Tile.BackgroundId)));
        Assert.Equal((nint)4, Marshal.OffsetOf<Tile>(nameof(Tile.Damage)));
        Assert.Equal((nint)5, Marshal.OffsetOf<Tile>(nameof(Tile.Flags)));
        Assert.Equal((nint)6, Marshal.OffsetOf<Tile>(nameof(Tile.ExtraDataId)));
    }

    [Fact]
    public void WorldAndPhysicsConstants_MatchArchitectureSpec()
    {
        Assert.Equal(100, WorldConstants.Width);
        Assert.Equal(60, WorldConstants.Height);
        Assert.Equal(32, WorldConstants.TileSize);
        Assert.Equal(6000, WorldConstants.TotalTiles);

        Assert.True(PhysicsConstants.Gravity > 0f);
        Assert.True(PhysicsConstants.MoveSpeed > 0f);
        Assert.True(PhysicsConstants.JumpForce > 0f);
        Assert.Equal(3, PhysicsConstants.MaxReachTiles);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(99, 0, 99)]
    [InlineData(0, 1, 100)]
    [InlineData(99, 59, 5999)]
    public void GetIndex_Maps2DCoordinatesToFlat1DArray(int x, int y, int expectedIndex)
    {
        Assert.Equal(expectedIndex, Tile.GetIndex(x, y));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(100, 0)]
    [InlineData(0, 60)]
    public void GetIndex_ThrowsWhenOutOfBounds(int x, int y)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Tile.GetIndex(x, y));
    }
}
