using System.Buffers.Binary;
using System.IO.Compression;
using GrowSandbox.Shared.Constants;

namespace GrowSandbox.Shared.Models;

/// <summary>
/// Utilitas serialisasi biner dan kompresi Brotli untuk flat 1D Tile[] array (100x60).
/// </summary>
public static class TileCompression
{
    public static byte[] SerializeRaw(ReadOnlySpan<Tile> tiles)
    {
        var raw = new byte[tiles.Length * WorldConstants.TileByteSize];
        Span<byte> span = raw;

        for (int i = 0; i < tiles.Length; i++)
        {
            int offset = i * WorldConstants.TileByteSize;
            ref readonly Tile tile = ref tiles[i];
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset, 2), tile.ForegroundId);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 2, 2), tile.BackgroundId);
            span[offset + 4] = tile.Damage;
            span[offset + 5] = (byte)tile.Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 6, 2), tile.ExtraDataId);
        }

        return raw;
    }

    public static Tile[] DeserializeRaw(ReadOnlySpan<byte> raw, int expectedTileCount = WorldConstants.TotalTiles)
    {
        int expectedBytes = expectedTileCount * WorldConstants.TileByteSize;
        if (raw.Length != expectedBytes)
        {
            throw new InvalidDataException(
                $"Ukuran data tile mentah tidak valid: {raw.Length} byte (wajib {expectedBytes} byte).");
        }

        var tiles = new Tile[expectedTileCount];
        for (int i = 0; i < expectedTileCount; i++)
        {
            int offset = i * WorldConstants.TileByteSize;
            tiles[i] = new Tile(
                foregroundId: BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(offset, 2)),
                backgroundId: BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(offset + 2, 2)),
                damage: raw[offset + 4],
                flags: (TileFlags)raw[offset + 5],
                extraDataId: BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(offset + 6, 2)));
        }

        return tiles;
    }

    public static byte[] CompressBrotli(
        ReadOnlySpan<Tile> tiles,
        CompressionLevel compressionLevel = CompressionLevel.Fastest)
    {
        byte[] raw = SerializeRaw(tiles);

        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, compressionLevel, leaveOpen: true))
        {
            brotli.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    public static Tile[] DecompressBrotli(
        byte[] compressedData,
        int expectedTileCount = WorldConstants.TotalTiles)
    {
        ArgumentNullException.ThrowIfNull(compressedData);

        int expectedBytes = expectedTileCount * WorldConstants.TileByteSize;
        using var input = new MemoryStream(compressedData, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(expectedBytes);
        brotli.CopyTo(output);

        if (!output.TryGetBuffer(out ArraySegment<byte> buffer))
        {
            return DeserializeRaw(output.ToArray(), expectedTileCount);
        }

        return DeserializeRaw(buffer.AsSpan(), expectedTileCount);
    }
}
