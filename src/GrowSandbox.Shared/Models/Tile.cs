using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using GrowSandbox.Shared.Constants;

namespace GrowSandbox.Shared.Models;

[Flags]
public enum TileFlags : byte
{
    None = 0,
    Water = 1 << 0,
    Flipped = 1 << 1,
    Locked = 1 << 2,
    HasExtraData = 1 << 3,
}

/// <summary>
/// Representasi memori kompak berukuran tepat 8 byte untuk satu tile
/// di dalam array flat 1D berukuran 100x60.
/// Layout sequential (Pack = 1):
/// - ForegroundId (2 byte, offset 0)
/// - BackgroundId (2 byte, offset 2)
/// - Damage       (1 byte, offset 4)
/// - Flags        (1 byte, offset 5)
/// - ExtraDataId  (2 byte, offset 6)
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct Tile : IEquatable<Tile>
{
    public ushort ForegroundId;
    public ushort BackgroundId;
    public byte Damage;
    public TileFlags Flags;
    public ushort ExtraDataId;

    public Tile(
        ushort foregroundId,
        ushort backgroundId = 0,
        byte damage = 0,
        TileFlags flags = TileFlags.None,
        ushort extraDataId = 0)
    {
        ForegroundId = foregroundId;
        BackgroundId = backgroundId;
        Damage = damage;
        Flags = flags;
        ExtraDataId = extraDataId;
    }

    public readonly bool IsEmpty => ForegroundId == 0 && BackgroundId == 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetIndex(int x, int y)
    {
        if ((uint)x >= WorldConstants.Width || (uint)y >= WorldConstants.Height)
        {
            throw new ArgumentOutOfRangeException(
                $"Koordinat tile ({x}, {y}) di luar batas dunia ({WorldConstants.Width}x{WorldConstants.Height}).");
        }

        return y * WorldConstants.Width + x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInBounds(int x, int y)
    {
        return (uint)x < WorldConstants.Width && (uint)y < WorldConstants.Height;
    }

    public readonly bool Equals(Tile other) =>
        ForegroundId == other.ForegroundId &&
        BackgroundId == other.BackgroundId &&
        Damage == other.Damage &&
        Flags == other.Flags &&
        ExtraDataId == other.ExtraDataId;

    public override readonly bool Equals(object? obj) => obj is Tile other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(ForegroundId, BackgroundId, Damage, Flags, ExtraDataId);

    public static bool operator ==(Tile left, Tile right) => left.Equals(right);

    public static bool operator !=(Tile left, Tile right) => !left.Equals(right);
}
