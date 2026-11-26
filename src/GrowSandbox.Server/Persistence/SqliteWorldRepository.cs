using System.Buffers.Binary;
using System.Text.Json;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Server.Persistence;

public readonly record struct PersistedWorldData(
    Tile[] Tiles,
    DroppedItem[] Drops,
    uint NextDropId,
    WorldLockData LockData,
    TreeData[] Trees);

/// <summary>
/// Codec biner snapshot world versi 3 (<c>GSW3</c>) yang menyimpan blob Brotli <c>Tile[6000]</c>,
/// seluruh <see cref="DroppedItem"/> aktif di lantai, <c>NextDropId</c> monotonik,
/// serta seluruh <see cref="TreeData"/> aktif (UTC timestamp), dengan fallback otomatis aman
/// untuk blob v2 (<c>GSW2</c>) dan blob v1 (Brotli tile mentah lama).
/// </summary>
public static class WorldSnapshotBlobCodec
{
    private const byte MagicG = (byte)'G';
    private const byte MagicS = (byte)'S';
    private const byte MagicW = (byte)'W';
    private const byte Magic2 = (byte)'2';
    private const byte Magic3 = (byte)'3';
    private const byte FormatVersion2 = 2;
    private const byte FormatVersion3 = 3;
    private const int HeaderBytes = 9;            // 4 magic + 1 version + 4 compressedLen
    private const int DropSectionHeaderBytes = 8; // 4 nextDropId + 4 dropCount
    private const int DropEntryBytes = 20;        // 4 DropId + 2 ItemId + 2 Count + 4 X + 4 Y + 4 SpawnTime
    private const int TreeEntryBytes = 23;        // 4 TileIndex + 2 SeedId + 8 PlantedTicks + 8 ReadyTicks + 1 FruitCount
    private const int MaxPersistedDrops = 16384;
    private const int MaxPersistedTrees = WorldConstants.TotalTiles;

    public static byte[] EncodeV2(
        byte[] compressedTiles,
        IReadOnlyCollection<DroppedItem>? drops,
        uint nextDropId)
    {
        ArgumentNullException.ThrowIfNull(compressedTiles);
        if (compressedTiles.Length == 0)
        {
            throw new ArgumentException("Payload tile terkompresi Brotli tidak boleh kosong.", nameof(compressedTiles));
        }

        int dropCount = drops?.Count ?? 0;
        if (dropCount > MaxPersistedDrops)
        {
            throw new ArgumentException($"Jumlah drop persisten melebihi batas {MaxPersistedDrops}.", nameof(drops));
        }

        int totalLength = HeaderBytes + compressedTiles.Length + DropSectionHeaderBytes + (dropCount * DropEntryBytes);
        var buffer = new byte[totalLength];
        Span<byte> span = buffer;

        span[0] = MagicG;
        span[1] = MagicS;
        span[2] = MagicW;
        span[3] = Magic2;
        span[4] = FormatVersion2;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(5, 4), compressedTiles.Length);
        compressedTiles.AsSpan().CopyTo(span.Slice(HeaderBytes, compressedTiles.Length));

        int offset = HeaderBytes + compressedTiles.Length;
        WriteDropsSection(span, ref offset, drops, dropCount, nextDropId);
        return buffer;
    }

    public static byte[] EncodeV3(
        byte[] compressedTiles,
        IReadOnlyCollection<DroppedItem>? drops,
        uint nextDropId,
        IReadOnlyCollection<TreeData>? trees)
    {
        ArgumentNullException.ThrowIfNull(compressedTiles);
        if (compressedTiles.Length == 0)
        {
            throw new ArgumentException("Payload tile terkompresi Brotli tidak boleh kosong.", nameof(compressedTiles));
        }

        int dropCount = drops?.Count ?? 0;
        if (dropCount > MaxPersistedDrops)
        {
            throw new ArgumentException($"Jumlah drop persisten melebihi batas {MaxPersistedDrops}.", nameof(drops));
        }

        int treeCount = trees?.Count ?? 0;
        if (treeCount > MaxPersistedTrees)
        {
            throw new ArgumentException($"Jumlah pohon persisten melebihi batas {MaxPersistedTrees}.", nameof(trees));
        }

        int totalLength = HeaderBytes
            + compressedTiles.Length
            + DropSectionHeaderBytes + (dropCount * DropEntryBytes)
            + 4 + (treeCount * TreeEntryBytes);
        var buffer = new byte[totalLength];
        Span<byte> span = buffer;

        span[0] = MagicG;
        span[1] = MagicS;
        span[2] = MagicW;
        span[3] = Magic3;
        span[4] = FormatVersion3;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(5, 4), compressedTiles.Length);
        compressedTiles.AsSpan().CopyTo(span.Slice(HeaderBytes, compressedTiles.Length));

        int offset = HeaderBytes + compressedTiles.Length;
        WriteDropsSection(span, ref offset, drops, dropCount, nextDropId);

        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), treeCount);
        offset += 4;

        if (trees is not null && treeCount > 0)
        {
            foreach (TreeData tree in trees)
            {
                BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), tree.TileIndex);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 4, 2), tree.SeedId);
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset + 6, 8), tree.PlantedUtcTicks);
                BinaryPrimitives.WriteInt64LittleEndian(span.Slice(offset + 14, 8), tree.ReadyUtcTicks);
                span[offset + 22] = tree.FruitCount;
                offset += TreeEntryBytes;
            }
        }

        return buffer;
    }

    private static void WriteDropsSection(
        Span<byte> span,
        ref int offset,
        IReadOnlyCollection<DroppedItem>? drops,
        int dropCount,
        uint nextDropId)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), Math.Max(1u, nextDropId));
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(offset, 4), dropCount);
        offset += 4;

        if (drops is not null && dropCount > 0)
        {
            foreach (DroppedItem drop in drops)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), drop.DropId);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 4, 2), drop.ItemId);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(offset + 6, 2), drop.Count);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 8, 4), drop.X);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 12, 4), drop.Y);
                BinaryPrimitives.WriteSingleLittleEndian(span.Slice(offset + 16, 4), drop.SpawnTime);
                offset += DropEntryBytes;
            }
        }
    }

    public static bool IsV2Blob(ReadOnlySpan<byte> blob) =>
        blob.Length >= HeaderBytes + DropSectionHeaderBytes &&
        blob[0] == MagicG &&
        blob[1] == MagicS &&
        blob[2] == MagicW &&
        blob[3] == Magic2 &&
        blob[4] == FormatVersion2;

    public static bool IsV3Blob(ReadOnlySpan<byte> blob) =>
        blob.Length >= HeaderBytes + DropSectionHeaderBytes + 4 &&
        blob[0] == MagicG &&
        blob[1] == MagicS &&
        blob[2] == MagicW &&
        blob[3] == Magic3 &&
        blob[4] == FormatVersion3;

    public static (Tile[] Tiles, DroppedItem[] Drops, uint NextDropId, TreeData[] Trees) Decode(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        bool isV3 = IsV3Blob(blob);
        bool isV2 = !isV3 && IsV2Blob(blob);

        if (!isV3 && !isV2)
        {
            // Fallback aman untuk format snapshot lama (v1 = stream Brotli Tile[6000] langsung).
            Tile[] legacyTiles = TileCompression.DecompressBrotli(blob, WorldConstants.TotalTiles);
            return (legacyTiles, Array.Empty<DroppedItem>(), 1u, Array.Empty<TreeData>());
        }

        ReadOnlySpan<byte> span = blob;
        int compressedLen = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(5, 4));
        if (compressedLen <= 0 || HeaderBytes + compressedLen + DropSectionHeaderBytes > blob.Length)
        {
            throw new InvalidDataException($"Header blob world tidak valid (compressedLen={compressedLen}, total={blob.Length}).");
        }

        var compressedTiles = new byte[compressedLen];
        Buffer.BlockCopy(blob, HeaderBytes, compressedTiles, 0, compressedLen);
        Tile[] tiles = TileCompression.DecompressBrotli(compressedTiles, WorldConstants.TotalTiles);

        int offset = HeaderBytes + compressedLen;
        uint persistedNextId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;
        int dropCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        if (dropCount < 0 || dropCount > MaxPersistedDrops || offset + (dropCount * DropEntryBytes) > blob.Length)
        {
            throw new InvalidDataException($"Bagian DroppedItem pada blob world tidak valid (count={dropCount}).");
        }

        var drops = dropCount == 0 ? Array.Empty<DroppedItem>() : new DroppedItem[dropCount];
        uint maxSeenId = 0;

        for (int i = 0; i < dropCount; i++)
        {
            uint dropId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
            ushort itemId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 4, 2));
            ushort count = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 6, 2));
            float x = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 8, 4));
            float y = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 12, 4));
            float spawnTime = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(offset + 16, 4));
            offset += DropEntryBytes;

            drops[i] = new DroppedItem(dropId, itemId, count, x, y, spawnTime);
            if (dropId > maxSeenId)
            {
                maxSeenId = dropId;
            }
        }

        uint nextDropId = Math.Max(1u, Math.Max(persistedNextId, maxSeenId + 1u));

        if (!isV3)
        {
            return (tiles, drops, nextDropId, Array.Empty<TreeData>());
        }

        if (offset + 4 > blob.Length)
        {
            throw new InvalidDataException("Bagian header TreeData pada blob world v3 terpotong.");
        }

        int treeCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        if (treeCount < 0 || treeCount > MaxPersistedTrees || offset + (treeCount * TreeEntryBytes) > blob.Length)
        {
            throw new InvalidDataException($"Bagian TreeData pada blob world v3 tidak valid (count={treeCount}).");
        }

        var trees = treeCount == 0 ? Array.Empty<TreeData>() : new TreeData[treeCount];
        for (int i = 0; i < treeCount; i++)
        {
            int tileIndex = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
            ushort seedId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(offset + 4, 2));
            long plantedUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 6, 8));
            long readyUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(offset + 14, 8));
            byte fruitCount = span[offset + 22];
            offset += TreeEntryBytes;

            trees[i] = new TreeData(tileIndex, seedId, plantedUtcTicks, readyUtcTicks, fruitCount);
        }

        return (tiles, drops, nextDropId, trees);
    }
}

public interface IWorldRepository
{
    byte[]? LoadWorldBlob(string worldName);
    Tile[]? LoadWorldTiles(string worldName);
    PersistedWorldData? LoadWorldData(string worldName);
    WorldLockData? LoadWorldLock(string worldName);
    void SaveWorldBlob(string worldName, byte[] compressedData, WorldLockData? lockData = null);
    Task SaveWorldBlobAsync(
        string worldName,
        byte[] compressedData,
        WorldLockData? lockData = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Repositori persistensi world berbasis SQLite (<c>Microsoft.Data.Sqlite</c>)
/// yang menyimpan blob snapshot biner v3 (tile Brotli + DroppedItem lantai + TreeData UTC) pada tabel
/// <c>Worlds(Name TEXT PRIMARY KEY, Data BLOB)</c> serta data penuh <see cref="WorldLockData"/>
/// pada tabel <c>WorldMeta</c>.
/// </summary>
public sealed class SqliteWorldRepository : IWorldRepository
{
    private readonly string _connectionString;
    private readonly bool _isInMemory;

    public string DatabasePath { get; }

    public SqliteWorldRepository(string databasePath = "growsandbox.db")
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Path database SQLite tidak boleh kosong.", nameof(databasePath));
        }

        DatabasePath = databasePath;
        _isInMemory = string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        };

        _connectionString = builder.ToString();
        EnsureSchema();
    }

    private void EnsureSchema()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        if (!_isInMemory)
        {
            using var walCmd = connection.CreateCommand();
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            walCmd.ExecuteNonQuery();
        }

        using (var schemaCmd = connection.CreateCommand())
        {
            schemaCmd.CommandText =
                """
                CREATE TABLE IF NOT EXISTS Worlds (
                    Name TEXT PRIMARY KEY,
                    Data BLOB NOT NULL
                );
                CREATE TABLE IF NOT EXISTS WorldMeta (
                    Name TEXT PRIMARY KEY,
                    IsLocked INTEGER NOT NULL DEFAULT 0,
                    OwnerAccountId INTEGER NOT NULL DEFAULT 0,
                    LockOwner TEXT NOT NULL DEFAULT '',
                    LockX INTEGER NOT NULL DEFAULT -1,
                    LockY INTEGER NOT NULL DEFAULT -1,
                    IsPublic INTEGER NOT NULL DEFAULT 0,
                    AccessListJson TEXT NOT NULL DEFAULT '[]'
                );
                """;
            schemaCmd.ExecuteNonQuery();
        }

        // Migrasi otomatis aman untuk database WorldMeta versi lama.
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var infoCmd = connection.CreateCommand())
        {
            infoCmd.CommandText = "PRAGMA table_info(WorldMeta);";
            using SqliteDataReader reader = infoCmd.ExecuteReader();
            while (reader.Read())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        if (!existingColumns.Contains("IsLocked"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE WorldMeta ADD COLUMN IsLocked INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }

        if (!existingColumns.Contains("OwnerAccountId"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE WorldMeta ADD COLUMN OwnerAccountId INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }

        if (!existingColumns.Contains("IsPublic"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE WorldMeta ADD COLUMN IsPublic INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }

        if (!existingColumns.Contains("AccessListJson"))
        {
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE WorldMeta ADD COLUMN AccessListJson TEXT NOT NULL DEFAULT '[]';";
            alter.ExecuteNonQuery();
        }
    }

    public byte[]? LoadWorldBlob(string worldName)
    {
        string normalized = WorldManager.NormalizeWorldName(worldName);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Data FROM Worlds WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", normalized);

        object? result = command.ExecuteScalar();
        return result is byte[] blob && blob.Length > 0 ? blob : null;
    }

    public Tile[]? LoadWorldTiles(string worldName)
    {
        byte[]? blob = LoadWorldBlob(worldName);
        if (blob is null)
        {
            return null;
        }

        return WorldSnapshotBlobCodec.Decode(blob).Tiles;
    }

    public PersistedWorldData? LoadWorldData(string worldName)
    {
        byte[]? blob = LoadWorldBlob(worldName);
        if (blob is null)
        {
            return null;
        }

        (Tile[] tiles, DroppedItem[] drops, uint nextDropId, TreeData[] trees) = WorldSnapshotBlobCodec.Decode(blob);
        WorldLockData lockData = LoadWorldLock(worldName) ?? WorldLockData.Unlocked();
        return new PersistedWorldData(tiles, drops, nextDropId, lockData, trees);
    }

    public WorldLockData? LoadWorldLock(string worldName)
    {
        string normalized = WorldManager.NormalizeWorldName(worldName);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT IsLocked, OwnerAccountId, LockOwner, LockX, LockY, IsPublic, AccessListJson FROM WorldMeta WHERE Name = $name;";
        command.Parameters.AddWithValue("$name", normalized);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        bool isLocked = reader.GetInt32(0) != 0;
        int ownerAccountId = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        string owner = reader.GetString(2);
        int x = reader.GetInt32(3);
        int y = reader.GetInt32(4);
        bool isPublic = reader.GetInt32(5) != 0;
        string accessJson = reader.IsDBNull(6) ? "[]" : reader.GetString(6);

        if (!isLocked && !string.IsNullOrWhiteSpace(owner) && x >= 0 && y >= 0)
        {
            isLocked = true;
        }

        if (!isLocked || string.IsNullOrWhiteSpace(owner))
        {
            return WorldLockData.Unlocked();
        }

        var lockData = new WorldLockData
        {
            IsLocked = true,
            OwnerAccountId = Math.Max(0, ownerAccountId),
            OwnerName = WorldLockData.SanitizeName(owner),
            LockX = x,
            LockY = y,
            IsPublic = isPublic,
        };

        try
        {
            List<string>? parsed = JsonSerializer.Deserialize<List<string>>(accessJson);
            if (parsed is not null)
            {
                for (int i = 0; i < parsed.Count; i++)
                {
                    lockData.TryAddAccess(parsed[i], out _);
                }
            }
        }
        catch
        {
            // Abaikan JSON korup agar world tetap dapat dimuat aman.
        }

        return lockData;
    }

    public void SaveWorldBlob(string worldName, byte[] compressedData, WorldLockData? lockData = null)
    {
        ArgumentNullException.ThrowIfNull(compressedData);
        string normalized = WorldManager.NormalizeWorldName(worldName);

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteTransaction tx = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText =
                """
                INSERT INTO Worlds (Name, Data)
                VALUES ($name, $data)
                ON CONFLICT(Name) DO UPDATE SET Data = excluded.Data;
                """;
            command.Parameters.AddWithValue("$name", normalized);
            command.Parameters.Add("$data", SqliteType.Blob).Value = compressedData;
            command.ExecuteNonQuery();
        }

        if (lockData is not null)
        {
            using var metaCmd = connection.CreateCommand();
            metaCmd.Transaction = tx;
            ConfigureWorldMetaUpsert(metaCmd, normalized, lockData);
            metaCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    public async Task SaveWorldBlobAsync(
        string worldName,
        byte[] compressedData,
        WorldLockData? lockData = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compressedData);
        string normalized = WorldManager.NormalizeWorldName(worldName);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var tx = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = tx;
            command.CommandText =
                """
                INSERT INTO Worlds (Name, Data)
                VALUES ($name, $data)
                ON CONFLICT(Name) DO UPDATE SET Data = excluded.Data;
                """;
            command.Parameters.AddWithValue("$name", normalized);
            command.Parameters.Add("$data", SqliteType.Blob).Value = compressedData;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        if (lockData is not null)
        {
            await using var metaCmd = connection.CreateCommand();
            metaCmd.Transaction = tx;
            ConfigureWorldMetaUpsert(metaCmd, normalized, lockData);
            await metaCmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static void ConfigureWorldMetaUpsert(SqliteCommand metaCmd, string normalizedWorldName, WorldLockData lockData)
    {
        bool isLocked = lockData.IsLocked && !string.IsNullOrWhiteSpace(lockData.OwnerName);
        int ownerAccountId = isLocked ? Math.Max(0, lockData.OwnerAccountId) : 0;
        string owner = isLocked ? WorldLockData.SanitizeName(lockData.OwnerName) : string.Empty;
        int lockX = isLocked ? lockData.LockX : -1;
        int lockY = isLocked ? lockData.LockY : -1;
        bool isPublic = isLocked && lockData.IsPublic;
        string accessJson = isLocked && lockData.AccessList.Count > 0
            ? JsonSerializer.Serialize(lockData.AccessList)
            : "[]";

        metaCmd.CommandText =
            """
            INSERT INTO WorldMeta (Name, IsLocked, OwnerAccountId, LockOwner, LockX, LockY, IsPublic, AccessListJson)
            VALUES ($name, $isLocked, $ownerAccountId, $owner, $x, $y, $isPublic, $accessJson)
            ON CONFLICT(Name) DO UPDATE SET
                IsLocked = excluded.IsLocked,
                OwnerAccountId = excluded.OwnerAccountId,
                LockOwner = excluded.LockOwner,
                LockX = excluded.LockX,
                LockY = excluded.LockY,
                IsPublic = excluded.IsPublic,
                AccessListJson = excluded.AccessListJson;
            """;
        metaCmd.Parameters.AddWithValue("$name", normalizedWorldName);
        metaCmd.Parameters.AddWithValue("$isLocked", isLocked ? 1 : 0);
        metaCmd.Parameters.AddWithValue("$ownerAccountId", ownerAccountId);
        metaCmd.Parameters.AddWithValue("$owner", owner);
        metaCmd.Parameters.AddWithValue("$x", lockX);
        metaCmd.Parameters.AddWithValue("$y", lockY);
        metaCmd.Parameters.AddWithValue("$isPublic", isPublic ? 1 : 0);
        metaCmd.Parameters.AddWithValue("$accessJson", accessJson);
    }
}
