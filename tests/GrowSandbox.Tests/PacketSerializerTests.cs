using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking;
using GrowSandbox.Shared.Networking.Packets;
using LiteNetLib.Utils;

namespace GrowSandbox.Tests;

public class PacketSerializerTests
{
    private static NetDataReader CreateReader(NetDataWriter writer) =>
        new(writer.Data, 0, writer.Length);

    [Fact]
    public void PacketType_UnderlyingTypeIsByte_AndContainsAllRequiredValues()
    {
        Assert.Equal(typeof(byte), Enum.GetUnderlyingType(typeof(PacketType)));

        var expectedNames = new[]
        {
            nameof(PacketType.JoinWorldRequest),
            nameof(PacketType.WorldSnapshot),
            nameof(PacketType.PlayerJoined),
            nameof(PacketType.PlayerLeft),
            nameof(PacketType.PlayerState),
            nameof(PacketType.TileAction),
            nameof(PacketType.TileUpdateDelta),
            nameof(PacketType.InventorySync),
            nameof(PacketType.DropSpawn),
            nameof(PacketType.DropRemove),
            nameof(PacketType.DropUpdate),
            nameof(PacketType.DropItemRequest),
            nameof(PacketType.ChatMessage),
            nameof(PacketType.WorldLockState),
            nameof(PacketType.WrenchTileRequest),
            nameof(PacketType.WorldLockConfigRequest),
            nameof(PacketType.SystemToast),
            nameof(PacketType.TreeUpdate),
            nameof(PacketType.RegisterRequest),
            nameof(PacketType.LoginRequest),
            nameof(PacketType.AuthResponse),
            nameof(PacketType.LogoutRequest),
            nameof(PacketType.TradeRequest),
            nameof(PacketType.TradeInvite),
            nameof(PacketType.TradeInviteResponse),
            nameof(PacketType.TradeUpdateOffer),
            nameof(PacketType.TradeAction),
            nameof(PacketType.TradeStateSync),
            nameof(PacketType.TradeEnded),
        };

        var actualNames = Enum.GetNames<PacketType>();
        Assert.Equal(expectedNames.Length, actualNames.Length);
        foreach (string expected in expectedNames)
        {
            Assert.Contains(expected, actualNames);
        }
    }

    [Fact]
    public void JoinWorldRequest_RoundTrip_PreservesAllFields()
    {
        var writer = new NetDataWriter();
        var original = new JoinWorldRequestPacket("START", "BuilderOne", NetworkConstants.ProtocolVersion);

        PacketSerializer.WriteJoinWorldRequest(writer, in original);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.JoinWorldRequest, PacketSerializer.PeekPacketType(reader));
        Assert.Equal(PacketType.JoinWorldRequest, PacketSerializer.ReadPacketType(reader));

        var deserialized = PacketSerializer.ReadJoinWorldRequest(reader);

        Assert.Equal(original, deserialized);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void WorldSnapshot_RoundTrip_PreservesAll6000Tiles()
    {
        var tiles = new Tile[WorldConstants.TotalTiles];
        for (int y = 0; y < WorldConstants.Height; y++)
        {
            for (int x = 0; x < WorldConstants.Width; x++)
            {
                int index = Tile.GetIndex(x, y);
                tiles[index] = new Tile(
                    foregroundId: (ushort)(y == 30 ? TileIds.GrassDirt : y > 30 ? TileIds.Dirt : TileIds.Air),
                    backgroundId: (ushort)(y >= 25 ? TileIds.DirtBackground : TileIds.Air),
                    damage: (byte)((x + y) % 5),
                    flags: (x % 2 == 0 ? TileFlags.Water : TileFlags.None) |
                           (y == 59 ? TileFlags.Locked : TileFlags.None),
                    extraDataId: (ushort)(index % 1024));
            }
        }

        tiles[Tile.GetIndex(0, 0)] = new Tile(1, 2, 3, TileFlags.Flipped | TileFlags.HasExtraData, 65535);
        tiles[Tile.GetIndex(99, 59)] = new Tile(65535, 32768, 255, TileFlags.Locked | TileFlags.Water, 42);

        var writer = new NetDataWriter();
        var drops = new[]
        {
            new DroppedItem(1u, TileIds.Dirt, 12, 1616.5f, 944.0f, 3.25f),
            new DroppedItem(2u, TileIds.Gem, 5, 1648.0f, 940.5f, 4.50f),
        };
        var lockData = new WorldLockData
        {
            IsLocked = true,
            OwnerAccountId = 77,
            OwnerName = "OwnerOne",
            LockX = 52,
            LockY = 29,
            IsPublic = true,
            AccessList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alice", "bob" },
        };
        var trees = new[]
        {
            new TreeData(Tile.GetIndex(52, 29), TileIds.DirtSeed, 639000000000000000L, 639000000150000000L, 3),
            new TreeData(Tile.GetIndex(54, 29), TileIds.GlassSeed, 639000000000000000L, 639000000600000000L, 4),
        };
        var original = WorldSnapshotPacket.Create("SANDBOX_MAIN", tiles, drops, lockData, trees);

        PacketSerializer.WriteWorldSnapshot(writer, in original);
        Assert.True(writer.Length < WorldConstants.TotalTiles * WorldConstants.TileByteSize);

        var reader = CreateReader(writer);

        Assert.Equal(PacketType.WorldSnapshot, PacketSerializer.ReadPacketType(reader));
        var deserialized = PacketSerializer.ReadWorldSnapshot(reader);

        Assert.Equal(original.WorldName, deserialized.WorldName);
        Assert.Equal(WorldConstants.Width, deserialized.Width);
        Assert.Equal(WorldConstants.Height, deserialized.Height);
        Assert.Equal(WorldConstants.TotalTiles, deserialized.Tiles.Length);
        Assert.Equal(original.Tiles, deserialized.Tiles);
        Assert.NotNull(deserialized.Drops);
        Assert.Equal(drops, deserialized.Drops);
        Assert.NotNull(deserialized.LockData);
        Assert.True(deserialized.LockData.IsLocked);
        Assert.Equal(77, deserialized.LockData.OwnerAccountId);
        Assert.Equal("OwnerOne", deserialized.LockData.OwnerName);
        Assert.Equal(52, deserialized.LockData.LockX);
        Assert.Equal(29, deserialized.LockData.LockY);
        Assert.True(deserialized.LockData.IsPublic);
        Assert.True(deserialized.LockData.HasAccess("Alice"));
        Assert.True(deserialized.LockData.HasAccess("Bob"));
        Assert.Equal(2, deserialized.LockData.AccessList.Count);
        Assert.NotNull(deserialized.Trees);
        Assert.Equal(trees, deserialized.Trees);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void PlayerJoined_RoundTrip_PreservesAllFields()
    {
        var writer = new NetDataWriter();
        var original = new PlayerJoinedPacket(42, "GrowPlayer", 512.5f, 960.25f);

        PacketSerializer.WritePlayerJoined(writer, in original);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.PlayerJoined, PacketSerializer.ReadPacketType(reader));
        var deserialized = PacketSerializer.ReadPlayerJoined(reader);

        Assert.Equal(original, deserialized);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void PlayerLeft_RoundTrip_PreservesAllFields()
    {
        var writer = new NetDataWriter();
        var original = new PlayerLeftPacket(42, "Disconnected by user");

        PacketSerializer.WritePlayerLeft(writer, in original);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.PlayerLeft, PacketSerializer.ReadPacketType(reader));
        var deserialized = PacketSerializer.ReadPlayerLeft(reader);

        Assert.Equal(original, deserialized);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void PlayerState_RoundTrip_PreservesAllFields()
    {
        var writer = new NetDataWriter();
        var original = new PlayerStatePacket(
            PlayerId: 7,
            Tick: 123456u,
            PositionX: 1024.75f,
            PositionY: 736.125f,
            VelocityX: -240f,
            VelocityY: 312.5f,
            IsGrounded: true,
            FacingLeft: true);

        PacketSerializer.WritePlayerState(writer, in original);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.PlayerState, PacketSerializer.ReadPacketType(reader));
        var deserialized = PacketSerializer.ReadPlayerState(reader);

        Assert.Equal(original, deserialized);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Theory]
    [InlineData(TileActionKind.Punch, 12, 34, 0)]
    [InlineData(TileActionKind.PlaceForeground, 50, 29, 2)]
    [InlineData(TileActionKind.PlaceBackground, 99, 59, 10)]
    [InlineData(TileActionKind.Interact, 0, 0, 500)]
    public void TileAction_RoundTrip_PreservesAllFields(
        TileActionKind kind,
        int tileX,
        int tileY,
        ushort itemId)
    {
        var writer = new NetDataWriter();
        var original = new TileActionPacket(kind, tileX, tileY, itemId);

        PacketSerializer.WriteTileAction(writer, in original);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.TileAction, PacketSerializer.ReadPacketType(reader));
        var deserialized = PacketSerializer.ReadTileAction(reader);

        Assert.Equal(original, deserialized);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void TileUpdateDelta_RoundTrip_PreservesAllFields()
    {
        var writer = new NetDataWriter();
        var updatedTile = new Tile(
            foregroundId: TileIds.TreeSapling,
            backgroundId: TileIds.WoodBackground,
            damage: 1,
            flags: TileFlags.Flipped,
            extraDataId: 100);
        var original = new TileUpdateDeltaPacket(45, 31, updatedTile);

        PacketSerializer.WriteTileUpdateDelta(writer, in original);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.TileUpdateDelta, PacketSerializer.ReadPacketType(reader));
        var deserialized = PacketSerializer.ReadTileUpdateDelta(reader);

        Assert.Equal(original, deserialized);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void InventorySync_RoundTrip_PreservesAllSlotsAndGems()
    {
        var writer = new NetDataWriter();
        var slots = new[]
        {
            new InventorySlot(TileIds.Dirt, 40),
            new InventorySlot(TileIds.Torch, 15),
            new InventorySlot(TileIds.WorldLock, 2),
        };
        var original = new InventorySyncPacket(slots, Gems: 1337);

        PacketSerializer.WriteInventorySync(writer, in original);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.InventorySync, PacketSerializer.ReadPacketType(reader));
        var deserialized = PacketSerializer.ReadInventorySync(reader);

        Assert.Equal(original.Slots, deserialized.Slots);
        Assert.Equal(1337, deserialized.Gems);
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void DropPackets_RoundTrip_PreserveAllFields()
    {
        var writer = new NetDataWriter();
        var drop = new DroppedItem(99u, TileIds.Wood, 12, 1612.5f, 944.0f, 18.75f);
        var spawn = new DropSpawnPacket(drop);
        PacketSerializer.WriteDropSpawn(writer, in spawn);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.DropSpawn, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(spawn, PacketSerializer.ReadDropSpawn(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var update = new DropUpdatePacket(99u, 25, CollectorPlayerId: -1);
        PacketSerializer.WriteDropUpdate(writer, in update);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.DropUpdate, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(update, PacketSerializer.ReadDropUpdate(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var remove = new DropRemovePacket(99u, CollectorPlayerId: 7);
        PacketSerializer.WriteDropRemove(writer, in remove);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.DropRemove, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(remove, PacketSerializer.ReadDropRemove(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var request = new DropItemRequestPacket(TileIds.Brick, -5);
        PacketSerializer.WriteDropItemRequest(writer, in request);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.DropItemRequest, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(request, PacketSerializer.ReadDropItemRequest(reader));
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void ChatMessage_And_WorldLockPackets_RoundTrip_PreserveAllFields()
    {
        var writer = new NetDataWriter();
        var chat = new ChatMessagePacket(3, "BuilderOne", "Halo semua! /join FARM");
        PacketSerializer.WriteChatMessage(writer, in chat);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.ChatMessage, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(chat, PacketSerializer.ReadChatMessage(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var lockData = new WorldLockData
        {
            IsLocked = true,
            OwnerAccountId = 55,
            OwnerName = "BuilderOne",
            LockX = 52,
            LockY = 29,
            IsPublic = true,
            AccessList = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "frienda", "friendb" },
        };
        var lockState = new WorldLockStatePacket(lockData, OpenConfigModal: true);
        PacketSerializer.WriteWorldLockState(writer, in lockState);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.WorldLockState, PacketSerializer.ReadPacketType(reader));
        WorldLockStatePacket readLockState = PacketSerializer.ReadWorldLockState(reader);
        Assert.True(readLockState.OpenConfigModal);
        Assert.True(readLockState.LockData.IsLocked);
        Assert.Equal(55, readLockState.LockData.OwnerAccountId);
        Assert.Equal("BuilderOne", readLockState.LockData.OwnerName);
        Assert.Equal(52, readLockState.LockData.LockX);
        Assert.Equal(29, readLockState.LockData.LockY);
        Assert.True(readLockState.LockData.IsPublic);
        Assert.True(readLockState.LockData.HasAccess("FriendA"));
        Assert.True(readLockState.LockData.HasAccess("FriendB"));
        Assert.Equal(2, readLockState.LockData.AccessList.Count);
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var wrenchReq = new WrenchTileRequestPacket(52, 29);
        PacketSerializer.WriteWrenchTileRequest(writer, in wrenchReq);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.WrenchTileRequest, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(wrenchReq, PacketSerializer.ReadWrenchTileRequest(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var configReq = new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "Player_332");
        PacketSerializer.WriteWorldLockConfigRequest(writer, in configReq);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.WorldLockConfigRequest, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(configReq, PacketSerializer.ReadWorldLockConfigRequest(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var toast = new SystemToastPacket("Area ini terkunci!", ToastKind.Warning);
        PacketSerializer.WriteSystemToast(writer, in toast);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.SystemToast, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(toast, PacketSerializer.ReadSystemToast(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var tree = new TreeData(Tile.GetIndex(55, 29), TileIds.BrickSeed, 639000000000000000L, 639000000450000000L, 4);
        var treePlanted = new TreeUpdatePacket(tree.TileIndex, IsRemoved: false, tree);
        PacketSerializer.WriteTreeUpdate(writer, in treePlanted);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.TreeUpdate, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(treePlanted, PacketSerializer.ReadTreeUpdate(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var treeRemoved = new TreeUpdatePacket(tree.TileIndex, IsRemoved: true, default);
        PacketSerializer.WriteTreeUpdate(writer, in treeRemoved);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.TreeUpdate, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(treeRemoved, PacketSerializer.ReadTreeUpdate(reader));
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void AuthPackets_RoundTrip_PreserveAllFields()
    {
        var writer = new NetDataWriter();
        var reg = new RegisterRequestPacket("Budi_01", "rahasia123");
        PacketSerializer.WriteRegisterRequest(writer, in reg);
        var reader = CreateReader(writer);

        Assert.Equal(PacketType.RegisterRequest, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(reg, PacketSerializer.ReadRegisterRequest(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var login = new LoginRequestPacket("Budi_01", "rahasia123");
        PacketSerializer.WriteLoginRequest(writer, in login);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.LoginRequest, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(login, PacketSerializer.ReadLoginRequest(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var resp = new AuthResponsePacket(true, 42, "Budi_01", "", 250, "FARM");
        PacketSerializer.WriteAuthResponse(writer, in resp);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.AuthResponse, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(resp, PacketSerializer.ReadAuthResponse(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var logout = new LogoutRequestPacket();
        PacketSerializer.WriteLogoutRequest(writer, in logout);
        reader = CreateReader(writer);

        Assert.Equal(PacketType.LogoutRequest, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(logout, PacketSerializer.ReadLogoutRequest(reader));
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void TradePackets_RoundTrip_PreserveAllFields()
    {
        var writer = new NetDataWriter();

        var req = new TradeRequestPacket(TargetAccountId: 12, TargetUsername: "PartnerB");
        PacketSerializer.WriteTradeRequest(writer, in req);
        var reader = CreateReader(writer);
        Assert.Equal(PacketType.TradeRequest, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(req, PacketSerializer.ReadTradeRequest(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var invite = new TradeInvitePacket(RequesterAccountId: 7, RequesterName: "TraderA");
        PacketSerializer.WriteTradeInvite(writer, in invite);
        reader = CreateReader(writer);
        Assert.Equal(PacketType.TradeInvite, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(invite, PacketSerializer.ReadTradeInvite(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var inviteResp = new TradeInviteResponsePacket(RequesterAccountId: 7, Accepted: true);
        PacketSerializer.WriteTradeInviteResponse(writer, in inviteResp);
        reader = CreateReader(writer);
        Assert.Equal(PacketType.TradeInviteResponse, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(inviteResp, PacketSerializer.ReadTradeInviteResponse(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var updateOffer = new TradeUpdateOfferPacket(SlotIndex: 2, ItemId: TileIds.WorldLock, Count: 1);
        PacketSerializer.WriteTradeUpdateOffer(writer, in updateOffer);
        reader = CreateReader(writer);
        Assert.Equal(PacketType.TradeUpdateOffer, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(updateOffer, PacketSerializer.ReadTradeUpdateOffer(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var action = new TradeActionPacket(TradeActionKind.FinalConfirm);
        PacketSerializer.WriteTradeAction(writer, in action);
        reader = CreateReader(writer);
        Assert.Equal(PacketType.TradeAction, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(action, PacketSerializer.ReadTradeAction(reader));
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var offersA = new[]
        {
            new TradeOfferSlot(TileIds.Dirt, 25),
            new TradeOfferSlot(TileIds.WorldLock, 1),
            TradeOfferSlot.Empty,
            TradeOfferSlot.Empty,
        };
        var offersB = new[]
        {
            new TradeOfferSlot(TileIds.Wood, 15),
            new TradeOfferSlot(TileIds.RockSeed, 3),
            TradeOfferSlot.Empty,
            TradeOfferSlot.Empty,
        };
        var sync = new TradeStateSyncPacket(
            Status: TradeSessionStatus.CooldownLocked,
            AccountIdA: 7,
            NameA: "TraderA",
            AccountIdB: 12,
            NameB: "PartnerB",
            OffersA: offersA,
            OffersB: offersB,
            AcceptedA: false,
            AcceptedB: false,
            ConfirmedA: false,
            ConfirmedB: false,
            LockCooldownRemaining: 1.75f);
        PacketSerializer.WriteTradeStateSync(writer, in sync);
        reader = CreateReader(writer);
        Assert.Equal(PacketType.TradeStateSync, PacketSerializer.ReadPacketType(reader));
        TradeStateSyncPacket readSync = PacketSerializer.ReadTradeStateSync(reader);
        Assert.Equal(sync.Status, readSync.Status);
        Assert.Equal(sync.AccountIdA, readSync.AccountIdA);
        Assert.Equal(sync.NameA, readSync.NameA);
        Assert.Equal(sync.AccountIdB, readSync.AccountIdB);
        Assert.Equal(sync.NameB, readSync.NameB);
        Assert.Equal(offersA, readSync.OffersA);
        Assert.Equal(offersB, readSync.OffersB);
        Assert.Equal(sync.AcceptedA, readSync.AcceptedA);
        Assert.Equal(sync.AcceptedB, readSync.AcceptedB);
        Assert.Equal(sync.ConfirmedA, readSync.ConfirmedA);
        Assert.Equal(sync.ConfirmedB, readSync.ConfirmedB);
        Assert.Equal(1.75f, readSync.LockCooldownRemaining);
        Assert.Equal(0, reader.AvailableBytes);

        writer.Reset();
        var ended = new TradeEndedPacket(Completed: true, ReasonMessage: "Trade selesai!");
        PacketSerializer.WriteTradeEnded(writer, in ended);
        reader = CreateReader(writer);
        Assert.Equal(PacketType.TradeEnded, PacketSerializer.ReadPacketType(reader));
        Assert.Equal(ended, PacketSerializer.ReadTradeEnded(reader));
        Assert.Equal(0, reader.AvailableBytes);
    }

    [Fact]
    public void ReadPacketType_ThrowsOnUnknownPacketType()
    {
        var writer = new NetDataWriter();
        writer.Put((byte)250);
        var reader = CreateReader(writer);

        Assert.Throws<InvalidDataException>(() => PacketSerializer.ReadPacketType(reader));
    }

    [Fact]
    public void WriteWorldSnapshot_ThrowsWhenTileCountDoesNotMatch100By60()
    {
        var writer = new NetDataWriter();
        var invalidSnapshot = new WorldSnapshotPacket("BROKEN", WorldConstants.Width, WorldConstants.Height, new Tile[10]);

        Assert.Throws<ArgumentException>(() => PacketSerializer.WriteWorldSnapshot(writer, in invalidSnapshot));
    }
}
