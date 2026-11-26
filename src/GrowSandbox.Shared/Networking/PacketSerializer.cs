using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using LiteNetLib.Utils;

namespace GrowSandbox.Shared.Networking;

/// <summary>
/// Helper serialisasi dan deserialisasi paket jaringan GrowSandbox
/// menggunakan <see cref="NetDataWriter"/> dan <see cref="NetDataReader"/> milik LiteNetLib.
/// Setiap metode Write menulis 1 byte header <see cref="PacketType"/> diikuti payload paket.
/// Pembacaan dilakukan dengan memanggil <see cref="ReadPacketType"/> lalu metode Read sesuai tipenya.
/// </summary>
public static class PacketSerializer
{
    public const int MaxInventorySlots = 64;
    public const int MaxChatMessageLength = 160;
    public const int MaxWorldSnapshotDrops = 4096;
    public const int MaxWorldSnapshotTrees = WorldConstants.TotalTiles;

    public static PacketType PeekPacketType(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.AvailableBytes < 1)
        {
            throw new InvalidDataException("Buffer paket kosong; header PacketType tidak ditemukan.");
        }

        byte rawType = reader.PeekByte();
        if (!Enum.IsDefined(typeof(PacketType), rawType))
        {
            throw new InvalidDataException($"PacketType tidak dikenal: {rawType}.");
        }

        return (PacketType)rawType;
    }

    public static PacketType ReadPacketType(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.AvailableBytes < 1)
        {
            throw new InvalidDataException("Buffer paket kosong; header PacketType tidak ditemukan.");
        }

        byte rawType = reader.GetByte();
        if (!Enum.IsDefined(typeof(PacketType), rawType))
        {
            throw new InvalidDataException($"PacketType tidak dikenal: {rawType}.");
        }

        return (PacketType)rawType;
    }

    public static void WriteTile(NetDataWriter writer, in Tile tile)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put(tile.ForegroundId);
        writer.Put(tile.BackgroundId);
        writer.Put(tile.Damage);
        writer.Put((byte)tile.Flags);
        writer.Put(tile.ExtraDataId);
    }

    public static Tile ReadTile(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new Tile(
            foregroundId: reader.GetUShort(),
            backgroundId: reader.GetUShort(),
            damage: reader.GetByte(),
            flags: (TileFlags)reader.GetByte(),
            extraDataId: reader.GetUShort());
    }

    public static void WriteJoinWorldRequest(NetDataWriter writer, in JoinWorldRequestPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.JoinWorldRequest);
        writer.Put(packet.WorldName ?? string.Empty);
        writer.Put(packet.PlayerName ?? string.Empty);
        writer.Put(packet.ProtocolVersion);
    }

    public static JoinWorldRequestPacket ReadJoinWorldRequest(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new JoinWorldRequestPacket(
            WorldName: reader.GetString(),
            PlayerName: reader.GetString(),
            ProtocolVersion: reader.GetUShort());
    }

    public static void WriteWorldSnapshot(NetDataWriter writer, in WorldSnapshotPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(packet.Tiles);

        if (packet.Width != WorldConstants.Width || packet.Height != WorldConstants.Height)
        {
            throw new ArgumentException(
                $"Dimensi WorldSnapshot wajib {WorldConstants.Width}x{WorldConstants.Height}, ditemukan {packet.Width}x{packet.Height}.",
                nameof(packet));
        }

        if (packet.Tiles.Length != WorldConstants.TotalTiles)
        {
            throw new ArgumentException(
                $"Jumlah tile WorldSnapshot wajib tepat {WorldConstants.TotalTiles}, ditemukan {packet.Tiles.Length}.",
                nameof(packet));
        }

        byte[] compressedTiles = TileCompression.CompressBrotli(packet.Tiles);
        WriteWorldSnapshotCompressed(
            writer,
            packet.WorldName,
            packet.Width,
            packet.Height,
            compressedTiles,
            packet.Drops,
            packet.LockData,
            packet.Trees);
    }

    public static void WriteWorldSnapshotCompressed(
        NetDataWriter writer,
        string worldName,
        int width,
        int height,
        byte[] compressedTiles,
        IReadOnlyCollection<DroppedItem>? drops = null,
        WorldLockData? lockData = null,
        IReadOnlyCollection<TreeData>? trees = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(compressedTiles);

        if (width != WorldConstants.Width || height != WorldConstants.Height)
        {
            throw new ArgumentException(
                $"Dimensi WorldSnapshot wajib {WorldConstants.Width}x{WorldConstants.Height}, ditemukan {width}x{height}.");
        }

        if (compressedTiles.Length == 0)
        {
            throw new ArgumentException("Payload tile terkompresi Brotli tidak boleh kosong.", nameof(compressedTiles));
        }

        int dropCount = drops?.Count ?? 0;
        if (dropCount > MaxWorldSnapshotDrops)
        {
            throw new ArgumentException(
                $"Jumlah DroppedItem dalam WorldSnapshot melebihi batas {MaxWorldSnapshotDrops}.",
                nameof(drops));
        }

        int treeCount = trees?.Count ?? 0;
        if (treeCount > MaxWorldSnapshotTrees)
        {
            throw new ArgumentException(
                $"Jumlah TreeData dalam WorldSnapshot melebihi batas {MaxWorldSnapshotTrees}.",
                nameof(trees));
        }

        writer.Put((byte)PacketType.WorldSnapshot);
        writer.Put(worldName ?? string.Empty);
        writer.Put(width);
        writer.Put(height);
        writer.Put(compressedTiles.Length);
        writer.Put(compressedTiles, 0, compressedTiles.Length);

        writer.Put(dropCount);
        if (drops is not null && dropCount > 0)
        {
            foreach (DroppedItem drop in drops)
            {
                WriteDroppedItem(writer, in drop);
            }
        }

        WriteWorldLockData(writer, lockData);

        writer.Put(treeCount);
        if (trees is not null && treeCount > 0)
        {
            foreach (TreeData tree in trees)
            {
                WriteTreeData(writer, in tree);
            }
        }
    }

    public static WorldSnapshotPacket ReadWorldSnapshot(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string worldName = reader.GetString();
        int width = reader.GetInt();
        int height = reader.GetInt();

        if (width != WorldConstants.Width || height != WorldConstants.Height)
        {
            throw new InvalidDataException(
                $"Dimensi WorldSnapshot tidak valid: {width}x{height} (wajib {WorldConstants.Width}x{WorldConstants.Height}).");
        }

        int compressedLength = reader.GetInt();
        if (compressedLength <= 0 || compressedLength > reader.AvailableBytes)
        {
            throw new InvalidDataException(
                $"Panjang payload Brotli WorldSnapshot tidak valid: {compressedLength} byte (tersedia {reader.AvailableBytes} byte).");
        }

        var compressedBytes = new byte[compressedLength];
        reader.GetBytes(compressedBytes, compressedLength);

        Tile[] tiles = TileCompression.DecompressBrotli(compressedBytes, WorldConstants.TotalTiles);

        int dropCount = reader.GetInt();
        if (dropCount < 0 || dropCount > MaxWorldSnapshotDrops)
        {
            throw new InvalidDataException($"Jumlah DroppedItem dalam WorldSnapshot tidak valid: {dropCount}.");
        }

        var drops = dropCount == 0 ? Array.Empty<DroppedItem>() : new DroppedItem[dropCount];
        for (int i = 0; i < dropCount; i++)
        {
            drops[i] = ReadDroppedItem(reader);
        }

        WorldLockData lockData = ReadWorldLockData(reader);

        int treeCount = reader.GetInt();
        if (treeCount < 0 || treeCount > MaxWorldSnapshotTrees)
        {
            throw new InvalidDataException($"Jumlah TreeData dalam WorldSnapshot tidak valid: {treeCount}.");
        }

        var trees = treeCount == 0 ? Array.Empty<TreeData>() : new TreeData[treeCount];
        for (int i = 0; i < treeCount; i++)
        {
            trees[i] = ReadTreeData(reader);
        }

        return new WorldSnapshotPacket(worldName, width, height, tiles, drops, lockData, trees);
    }

    public static void WriteTreeData(NetDataWriter writer, in TreeData tree)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put(tree.TileIndex);
        writer.Put(tree.SeedId);
        writer.Put(tree.PlantedUtcTicks);
        writer.Put(tree.ReadyUtcTicks);
        writer.Put(tree.FruitCount);
    }

    public static TreeData ReadTreeData(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new TreeData(
            TileIndex: reader.GetInt(),
            SeedId: reader.GetUShort(),
            PlantedUtcTicks: reader.GetLong(),
            ReadyUtcTicks: reader.GetLong(),
            FruitCount: reader.GetByte());
    }

    public static void WriteWorldLockData(NetDataWriter writer, WorldLockData? lockData)
    {
        ArgumentNullException.ThrowIfNull(writer);

        if (lockData is null || !lockData.IsLocked)
        {
            writer.Put(false);
            writer.Put(0);
            writer.Put(string.Empty);
            writer.Put(-1);
            writer.Put(-1);
            writer.Put(false);
            writer.Put((byte)0);
            return;
        }

        writer.Put(true);
        writer.Put(lockData.OwnerAccountId);
        writer.Put(lockData.OwnerName ?? string.Empty);
        writer.Put(lockData.LockX);
        writer.Put(lockData.LockY);
        writer.Put(lockData.IsPublic);

        int count = Math.Min(lockData.AccessList?.Count ?? 0, WorldLockData.MaxAccessListCount);
        writer.Put((byte)count);
        if (lockData.AccessList is not null && count > 0)
        {
            int written = 0;
            foreach (string entry in lockData.AccessList)
            {
                if (written >= count)
                {
                    break;
                }

                writer.Put(entry ?? string.Empty);
                written++;
            }
        }
    }

    public static WorldLockData ReadWorldLockData(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        bool isLocked = reader.GetBool();
        int ownerAccountId = reader.GetInt();
        string ownerName = reader.GetString();
        int lockX = reader.GetInt();
        int lockY = reader.GetInt();
        bool isPublic = reader.GetBool();
        int accessCount = reader.GetByte();
        if (accessCount > WorldLockData.MaxAccessListCount)
        {
            throw new InvalidDataException($"Jumlah AccessList World Lock tidak valid: {accessCount}.");
        }

        var accessList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < accessCount; i++)
        {
            string entry = WorldLockData.NormalizeUsername(reader.GetString());
            if (!string.IsNullOrEmpty(entry))
            {
                accessList.Add(entry);
            }
        }

        return new WorldLockData
        {
            IsLocked = isLocked,
            OwnerAccountId = ownerAccountId,
            OwnerName = ownerName,
            LockX = lockX,
            LockY = lockY,
            IsPublic = isPublic,
            AccessList = accessList,
        };
    }

    public static void WriteDroppedItem(NetDataWriter writer, in DroppedItem drop)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put(drop.DropId);
        writer.Put(drop.ItemId);
        writer.Put(drop.Count);
        writer.Put(drop.X);
        writer.Put(drop.Y);
        writer.Put(drop.SpawnTime);
    }

    public static DroppedItem ReadDroppedItem(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new DroppedItem(
            DropId: reader.GetUInt(),
            ItemId: reader.GetUShort(),
            Count: reader.GetUShort(),
            X: reader.GetFloat(),
            Y: reader.GetFloat(),
            SpawnTime: reader.GetFloat());
    }

    public static void WritePlayerJoined(NetDataWriter writer, in PlayerJoinedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.PlayerJoined);
        writer.Put(packet.PlayerId);
        writer.Put(packet.PlayerName ?? string.Empty);
        writer.Put(packet.SpawnX);
        writer.Put(packet.SpawnY);
    }

    public static PlayerJoinedPacket ReadPlayerJoined(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new PlayerJoinedPacket(
            PlayerId: reader.GetInt(),
            PlayerName: reader.GetString(),
            SpawnX: reader.GetFloat(),
            SpawnY: reader.GetFloat());
    }

    public static void WritePlayerLeft(NetDataWriter writer, in PlayerLeftPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.PlayerLeft);
        writer.Put(packet.PlayerId);
        writer.Put(packet.Reason ?? string.Empty);
    }

    public static PlayerLeftPacket ReadPlayerLeft(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new PlayerLeftPacket(
            PlayerId: reader.GetInt(),
            Reason: reader.GetString());
    }

    public static void WritePlayerState(NetDataWriter writer, in PlayerStatePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.PlayerState);
        writer.Put(packet.PlayerId);
        writer.Put(packet.Tick);
        writer.Put(packet.PositionX);
        writer.Put(packet.PositionY);
        writer.Put(packet.VelocityX);
        writer.Put(packet.VelocityY);
        writer.Put(packet.IsGrounded);
        writer.Put(packet.FacingLeft);
    }

    public static PlayerStatePacket ReadPlayerState(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new PlayerStatePacket(
            PlayerId: reader.GetInt(),
            Tick: reader.GetUInt(),
            PositionX: reader.GetFloat(),
            PositionY: reader.GetFloat(),
            VelocityX: reader.GetFloat(),
            VelocityY: reader.GetFloat(),
            IsGrounded: reader.GetBool(),
            FacingLeft: reader.GetBool());
    }

    public static void WriteTileAction(NetDataWriter writer, in TileActionPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.TileAction);
        writer.Put((byte)packet.Action);
        writer.Put(packet.TileX);
        writer.Put(packet.TileY);
        writer.Put(packet.ItemId);
    }

    public static TileActionPacket ReadTileAction(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        byte rawAction = reader.GetByte();
        if (!Enum.IsDefined(typeof(TileActionKind), rawAction))
        {
            throw new InvalidDataException($"TileActionKind tidak dikenal: {rawAction}.");
        }

        return new TileActionPacket(
            Action: (TileActionKind)rawAction,
            TileX: reader.GetInt(),
            TileY: reader.GetInt(),
            ItemId: reader.GetUShort());
    }

    public static void WriteTileUpdateDelta(NetDataWriter writer, in TileUpdateDeltaPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.TileUpdateDelta);
        writer.Put(packet.TileX);
        writer.Put(packet.TileY);
        Tile tile = packet.Tile;
        WriteTile(writer, in tile);
    }

    public static TileUpdateDeltaPacket ReadTileUpdateDelta(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new TileUpdateDeltaPacket(
            TileX: reader.GetInt(),
            TileY: reader.GetInt(),
            Tile: ReadTile(reader));
    }

    public static void WriteInventorySync(NetDataWriter writer, in InventorySyncPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        InventorySlot[] slots = packet.Slots ?? Array.Empty<InventorySlot>();
        if (slots.Length > MaxInventorySlots)
        {
            throw new ArgumentException($"Jumlah slot inventory melebihi batas {MaxInventorySlots}.", nameof(packet));
        }

        writer.Put((byte)PacketType.InventorySync);
        writer.Put((byte)slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            writer.Put(slots[i].ItemId);
            writer.Put(slots[i].Count);
        }

        writer.Put(packet.Gems);
    }

    public static InventorySyncPacket ReadInventorySync(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        int count = reader.GetByte();
        if (count > MaxInventorySlots)
        {
            throw new InvalidDataException($"Jumlah slot inventory tidak valid: {count}.");
        }

        var slots = new InventorySlot[count];
        for (int i = 0; i < count; i++)
        {
            slots[i] = new InventorySlot(reader.GetUShort(), reader.GetUShort());
        }

        int gems = reader.GetInt();
        return new InventorySyncPacket(slots, gems);
    }

    public static void WriteDropSpawn(NetDataWriter writer, in DropSpawnPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.DropSpawn);
        DroppedItem drop = packet.Drop;
        WriteDroppedItem(writer, in drop);
    }

    public static DropSpawnPacket ReadDropSpawn(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new DropSpawnPacket(ReadDroppedItem(reader));
    }

    public static void WriteDropRemove(NetDataWriter writer, in DropRemovePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.DropRemove);
        writer.Put(packet.DropId);
        writer.Put(packet.CollectorPlayerId);
    }

    public static DropRemovePacket ReadDropRemove(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new DropRemovePacket(
            DropId: reader.GetUInt(),
            CollectorPlayerId: reader.GetInt());
    }

    public static void WriteDropUpdate(NetDataWriter writer, in DropUpdatePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.DropUpdate);
        writer.Put(packet.DropId);
        writer.Put(packet.Count);
        writer.Put(packet.CollectorPlayerId);
    }

    public static DropUpdatePacket ReadDropUpdate(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new DropUpdatePacket(
            DropId: reader.GetUInt(),
            Count: reader.GetUShort(),
            CollectorPlayerId: reader.GetInt());
    }

    public static void WriteDropItemRequest(NetDataWriter writer, in DropItemRequestPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.DropItemRequest);
        writer.Put(packet.ItemId);
        writer.Put(packet.Count);
    }

    public static DropItemRequestPacket ReadDropItemRequest(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new DropItemRequestPacket(
            ItemId: reader.GetUShort(),
            Count: reader.GetInt());
    }

    public static void WriteChatMessage(NetDataWriter writer, in ChatMessagePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string message = packet.Message ?? string.Empty;
        if (message.Length > MaxChatMessageLength)
        {
            message = message[..MaxChatMessageLength];
        }

        writer.Put((byte)PacketType.ChatMessage);
        writer.Put(packet.SenderPlayerId);
        writer.Put(packet.SenderName ?? string.Empty);
        writer.Put(message);
    }

    public static ChatMessagePacket ReadChatMessage(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new ChatMessagePacket(
            SenderPlayerId: reader.GetInt(),
            SenderName: reader.GetString(),
            Message: reader.GetString());
    }

    public static void WriteWorldLockState(NetDataWriter writer, in WorldLockStatePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.WorldLockState);
        WriteWorldLockData(writer, packet.LockData);
        writer.Put(packet.OpenConfigModal);
    }

    public static WorldLockStatePacket ReadWorldLockState(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        WorldLockData lockData = ReadWorldLockData(reader);
        bool openConfigModal = reader.GetBool();
        return new WorldLockStatePacket(lockData, openConfigModal);
    }

    public static void WriteWrenchTileRequest(NetDataWriter writer, in WrenchTileRequestPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.WrenchTileRequest);
        writer.Put(packet.TileX);
        writer.Put(packet.TileY);
    }

    public static WrenchTileRequestPacket ReadWrenchTileRequest(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new WrenchTileRequestPacket(
            TileX: reader.GetInt(),
            TileY: reader.GetInt());
    }

    public static void WriteWorldLockConfigRequest(NetDataWriter writer, in WorldLockConfigRequestPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.WorldLockConfigRequest);
        writer.Put((byte)packet.Action);
        writer.Put(packet.TargetPlayerName ?? string.Empty);
    }

    public static WorldLockConfigRequestPacket ReadWorldLockConfigRequest(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        byte rawAction = reader.GetByte();
        if (!Enum.IsDefined(typeof(WorldLockConfigAction), rawAction))
        {
            throw new InvalidDataException($"WorldLockConfigAction tidak dikenal: {rawAction}.");
        }

        return new WorldLockConfigRequestPacket(
            Action: (WorldLockConfigAction)rawAction,
            TargetPlayerName: reader.GetString());
    }

    public static void WriteSystemToast(NetDataWriter writer, in SystemToastPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        string message = packet.Message ?? string.Empty;
        if (message.Length > MaxChatMessageLength)
        {
            message = message[..MaxChatMessageLength];
        }

        writer.Put((byte)PacketType.SystemToast);
        writer.Put(message);
        writer.Put((byte)packet.Kind);
    }

    public static SystemToastPacket ReadSystemToast(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string message = reader.GetString();
        byte rawKind = reader.GetByte();
        if (!Enum.IsDefined(typeof(ToastKind), rawKind))
        {
            throw new InvalidDataException($"ToastKind tidak dikenal: {rawKind}.");
        }

        return new SystemToastPacket(message, (ToastKind)rawKind);
    }

    public static void WriteTreeUpdate(NetDataWriter writer, in TreeUpdatePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.TreeUpdate);
        writer.Put(packet.TileIndex);
        writer.Put(packet.IsRemoved);
        if (!packet.IsRemoved)
        {
            TreeData tree = packet.Tree;
            WriteTreeData(writer, in tree);
        }
    }

    public static TreeUpdatePacket ReadTreeUpdate(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        int tileIndex = reader.GetInt();
        bool isRemoved = reader.GetBool();
        TreeData tree = isRemoved ? default : ReadTreeData(reader);
        return new TreeUpdatePacket(tileIndex, isRemoved, tree);
    }

    public static void WriteRegisterRequest(NetDataWriter writer, in RegisterRequestPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.RegisterRequest);
        writer.Put(packet.Username ?? string.Empty);
        writer.Put(packet.Password ?? string.Empty);
    }

    public static RegisterRequestPacket ReadRegisterRequest(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new RegisterRequestPacket(
            Username: reader.GetString(),
            Password: reader.GetString());
    }

    public static void WriteLoginRequest(NetDataWriter writer, in LoginRequestPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.LoginRequest);
        writer.Put(packet.Username ?? string.Empty);
        writer.Put(packet.Password ?? string.Empty);
    }

    public static LoginRequestPacket ReadLoginRequest(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new LoginRequestPacket(
            Username: reader.GetString(),
            Password: reader.GetString());
    }

    public static void WriteAuthResponse(NetDataWriter writer, in AuthResponsePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.Put((byte)PacketType.AuthResponse);
        writer.Put(packet.Success);
        writer.Put(packet.AccountId);
        writer.Put(packet.DisplayName ?? string.Empty);
        writer.Put(packet.ErrorMessage ?? string.Empty);
        writer.Put(packet.Gems);
        writer.Put(packet.LastWorld ?? string.Empty);
    }

    public static AuthResponsePacket ReadAuthResponse(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        return new AuthResponsePacket(
            Success: reader.GetBool(),
            AccountId: reader.GetInt(),
            DisplayName: reader.GetString(),
            ErrorMessage: reader.GetString(),
            Gems: reader.GetInt(),
            LastWorld: reader.GetString());
    }

    public static void WriteLogoutRequest(NetDataWriter writer, in LogoutRequestPacket _ = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Put((byte)PacketType.LogoutRequest);
    }

    public static LogoutRequestPacket ReadLogoutRequest(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new LogoutRequestPacket();
    }

    public static void WriteTradeOfferSlot(NetDataWriter writer, in TradeOfferSlot slot)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Put(slot.ItemId);
        writer.Put(slot.Count);
    }

    public static TradeOfferSlot ReadTradeOfferSlot(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new TradeOfferSlot(
            ItemId: reader.GetUShort(),
            Count: reader.GetUShort());
    }

    public static void WriteTradeRequest(NetDataWriter writer, in TradeRequestPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Put((byte)PacketType.TradeRequest);
        writer.Put(packet.TargetAccountId);
        writer.Put(packet.TargetUsername ?? string.Empty);
    }

    public static TradeRequestPacket ReadTradeRequest(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new TradeRequestPacket(
            TargetAccountId: reader.GetInt(),
            TargetUsername: reader.GetString());
    }

    public static void WriteTradeInvite(NetDataWriter writer, in TradeInvitePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Put((byte)PacketType.TradeInvite);
        writer.Put(packet.RequesterAccountId);
        writer.Put(packet.RequesterName ?? string.Empty);
    }

    public static TradeInvitePacket ReadTradeInvite(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new TradeInvitePacket(
            RequesterAccountId: reader.GetInt(),
            RequesterName: reader.GetString());
    }

    public static void WriteTradeInviteResponse(NetDataWriter writer, in TradeInviteResponsePacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Put((byte)PacketType.TradeInviteResponse);
        writer.Put(packet.RequesterAccountId);
        writer.Put(packet.Accepted);
    }

    public static TradeInviteResponsePacket ReadTradeInviteResponse(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new TradeInviteResponsePacket(
            RequesterAccountId: reader.GetInt(),
            Accepted: reader.GetBool());
    }

    public static void WriteTradeUpdateOffer(NetDataWriter writer, in TradeUpdateOfferPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Put((byte)PacketType.TradeUpdateOffer);
        writer.Put(packet.SlotIndex);
        writer.Put(packet.ItemId);
        writer.Put(packet.Count);
    }

    public static TradeUpdateOfferPacket ReadTradeUpdateOffer(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new TradeUpdateOfferPacket(
            SlotIndex: reader.GetByte(),
            ItemId: reader.GetUShort(),
            Count: reader.GetUShort());
    }

    public static void WriteTradeAction(NetDataWriter writer, in TradeActionPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Put((byte)PacketType.TradeAction);
        writer.Put((byte)packet.Action);
    }

    public static TradeActionPacket ReadTradeAction(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        byte rawAction = reader.GetByte();
        if (!Enum.IsDefined(typeof(TradeActionKind), rawAction))
        {
            throw new InvalidDataException($"TradeActionKind tidak dikenal: {rawAction}.");
        }

        return new TradeActionPacket((TradeActionKind)rawAction);
    }

    public static void WriteTradeStateSync(NetDataWriter writer, in TradeStateSyncPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);

        TradeOfferSlot[] offersA = packet.OffersA ?? Array.Empty<TradeOfferSlot>();
        TradeOfferSlot[] offersB = packet.OffersB ?? Array.Empty<TradeOfferSlot>();
        if (offersA.Length > NetworkConstants.TradeOfferSlotCount || offersB.Length > NetworkConstants.TradeOfferSlotCount)
        {
            throw new ArgumentException(
                $"Jumlah slot penawaran Trade maksimal {NetworkConstants.TradeOfferSlotCount}.",
                nameof(packet));
        }

        writer.Put((byte)PacketType.TradeStateSync);
        writer.Put((byte)packet.Status);
        writer.Put(packet.AccountIdA);
        writer.Put(packet.NameA ?? string.Empty);
        writer.Put(packet.AccountIdB);
        writer.Put(packet.NameB ?? string.Empty);

        for (int i = 0; i < NetworkConstants.TradeOfferSlotCount; i++)
        {
            TradeOfferSlot slot = i < offersA.Length ? offersA[i] : TradeOfferSlot.Empty;
            WriteTradeOfferSlot(writer, in slot);
        }

        for (int i = 0; i < NetworkConstants.TradeOfferSlotCount; i++)
        {
            TradeOfferSlot slot = i < offersB.Length ? offersB[i] : TradeOfferSlot.Empty;
            WriteTradeOfferSlot(writer, in slot);
        }

        writer.Put(packet.AcceptedA);
        writer.Put(packet.AcceptedB);
        writer.Put(packet.ConfirmedA);
        writer.Put(packet.ConfirmedB);
        float safeCooldown = float.IsFinite(packet.LockCooldownRemaining) && packet.LockCooldownRemaining > 0f
            ? packet.LockCooldownRemaining
            : 0f;
        writer.Put(safeCooldown);
    }

    public static TradeStateSyncPacket ReadTradeStateSync(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        byte rawStatus = reader.GetByte();
        if (!Enum.IsDefined(typeof(TradeSessionStatus), rawStatus))
        {
            throw new InvalidDataException($"TradeSessionStatus tidak dikenal: {rawStatus}.");
        }

        int accountIdA = reader.GetInt();
        string nameA = reader.GetString();
        int accountIdB = reader.GetInt();
        string nameB = reader.GetString();

        var offersA = new TradeOfferSlot[NetworkConstants.TradeOfferSlotCount];
        for (int i = 0; i < NetworkConstants.TradeOfferSlotCount; i++)
        {
            offersA[i] = ReadTradeOfferSlot(reader);
        }

        var offersB = new TradeOfferSlot[NetworkConstants.TradeOfferSlotCount];
        for (int i = 0; i < NetworkConstants.TradeOfferSlotCount; i++)
        {
            offersB[i] = ReadTradeOfferSlot(reader);
        }

        bool acceptedA = reader.GetBool();
        bool acceptedB = reader.GetBool();
        bool confirmedA = reader.GetBool();
        bool confirmedB = reader.GetBool();
        float cooldown = reader.GetFloat();

        return new TradeStateSyncPacket(
            Status: (TradeSessionStatus)rawStatus,
            AccountIdA: accountIdA,
            NameA: nameA,
            AccountIdB: accountIdB,
            NameB: nameB,
            OffersA: offersA,
            OffersB: offersB,
            AcceptedA: acceptedA,
            AcceptedB: acceptedB,
            ConfirmedA: confirmedA,
            ConfirmedB: confirmedB,
            LockCooldownRemaining: cooldown);
    }

    public static void WriteTradeEnded(NetDataWriter writer, in TradeEndedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.Put((byte)PacketType.TradeEnded);
        writer.Put(packet.Completed);
        writer.Put(packet.ReasonMessage ?? string.Empty);
    }

    public static TradeEndedPacket ReadTradeEnded(NetDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return new TradeEndedPacket(
            Completed: reader.GetBool(),
            ReasonMessage: reader.GetString());
    }

    // Overload generik praktis untuk konsistensi pemanggilan
    public static void Write(NetDataWriter writer, in JoinWorldRequestPacket packet) => WriteJoinWorldRequest(writer, in packet);
    public static void Write(NetDataWriter writer, in WorldSnapshotPacket packet) => WriteWorldSnapshot(writer, in packet);
    public static void Write(NetDataWriter writer, in PlayerJoinedPacket packet) => WritePlayerJoined(writer, in packet);
    public static void Write(NetDataWriter writer, in PlayerLeftPacket packet) => WritePlayerLeft(writer, in packet);
    public static void Write(NetDataWriter writer, in PlayerStatePacket packet) => WritePlayerState(writer, in packet);
    public static void Write(NetDataWriter writer, in TileActionPacket packet) => WriteTileAction(writer, in packet);
    public static void Write(NetDataWriter writer, in TileUpdateDeltaPacket packet) => WriteTileUpdateDelta(writer, in packet);
    public static void Write(NetDataWriter writer, in InventorySyncPacket packet) => WriteInventorySync(writer, in packet);
    public static void Write(NetDataWriter writer, in DropSpawnPacket packet) => WriteDropSpawn(writer, in packet);
    public static void Write(NetDataWriter writer, in DropRemovePacket packet) => WriteDropRemove(writer, in packet);
    public static void Write(NetDataWriter writer, in DropUpdatePacket packet) => WriteDropUpdate(writer, in packet);
    public static void Write(NetDataWriter writer, in DropItemRequestPacket packet) => WriteDropItemRequest(writer, in packet);
    public static void Write(NetDataWriter writer, in ChatMessagePacket packet) => WriteChatMessage(writer, in packet);
    public static void Write(NetDataWriter writer, in WorldLockStatePacket packet) => WriteWorldLockState(writer, in packet);
    public static void Write(NetDataWriter writer, in WrenchTileRequestPacket packet) => WriteWrenchTileRequest(writer, in packet);
    public static void Write(NetDataWriter writer, in WorldLockConfigRequestPacket packet) => WriteWorldLockConfigRequest(writer, in packet);
    public static void Write(NetDataWriter writer, in SystemToastPacket packet) => WriteSystemToast(writer, in packet);
    public static void Write(NetDataWriter writer, in TreeUpdatePacket packet) => WriteTreeUpdate(writer, in packet);
    public static void Write(NetDataWriter writer, in RegisterRequestPacket packet) => WriteRegisterRequest(writer, in packet);
    public static void Write(NetDataWriter writer, in LoginRequestPacket packet) => WriteLoginRequest(writer, in packet);
    public static void Write(NetDataWriter writer, in AuthResponsePacket packet) => WriteAuthResponse(writer, in packet);
    public static void Write(NetDataWriter writer, in LogoutRequestPacket packet) => WriteLogoutRequest(writer, in packet);
    public static void Write(NetDataWriter writer, in TradeRequestPacket packet) => WriteTradeRequest(writer, in packet);
    public static void Write(NetDataWriter writer, in TradeInvitePacket packet) => WriteTradeInvite(writer, in packet);
    public static void Write(NetDataWriter writer, in TradeInviteResponsePacket packet) => WriteTradeInviteResponse(writer, in packet);
    public static void Write(NetDataWriter writer, in TradeUpdateOfferPacket packet) => WriteTradeUpdateOffer(writer, in packet);
    public static void Write(NetDataWriter writer, in TradeActionPacket packet) => WriteTradeAction(writer, in packet);
    public static void Write(NetDataWriter writer, in TradeStateSyncPacket packet) => WriteTradeStateSync(writer, in packet);
    public static void Write(NetDataWriter writer, in TradeEndedPacket packet) => WriteTradeEnded(writer, in packet);
}
