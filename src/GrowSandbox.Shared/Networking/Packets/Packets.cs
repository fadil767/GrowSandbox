using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;

namespace GrowSandbox.Shared.Networking.Packets;

public enum TileActionKind : byte
{
    Punch = 0,
    PlaceForeground = 1,
    PlaceBackground = 2,
    Interact = 3,
}

public enum WorldLockConfigAction : byte
{
    AddAccess = 0,
    RemoveAccess = 1,
    TogglePublic = 2,
    PickUpLock = 3,
}

public enum ToastKind : byte
{
    Info = 0,
    Success = 1,
    Warning = 2,
}

public enum TradeActionKind : byte
{
    ToggleAccept = 0,
    FinalConfirm = 1,
    Cancel = 2,
}

public enum TradeSessionStatus : byte
{
    Negotiating = 0,
    CooldownLocked = 1,
    ReadyForFinalConfirm = 2,
}

public readonly record struct JoinWorldRequestPacket(
    string WorldName,
    string PlayerName,
    ushort ProtocolVersion = NetworkConstants.ProtocolVersion);

public readonly record struct WorldSnapshotPacket(
    string WorldName,
    int Width,
    int Height,
    Tile[] Tiles,
    DroppedItem[]? Drops = null,
    WorldLockData? LockData = null,
    TreeData[]? Trees = null)
{
    public static WorldSnapshotPacket Create(
        string worldName,
        Tile[] tiles,
        DroppedItem[]? drops = null,
        WorldLockData? lockData = null,
        TreeData[]? trees = null) =>
        new(
            worldName,
            WorldConstants.Width,
            WorldConstants.Height,
            tiles,
            drops ?? Array.Empty<DroppedItem>(),
            lockData?.Clone() ?? WorldLockData.Unlocked(),
            trees ?? Array.Empty<TreeData>());
}

public readonly record struct PlayerJoinedPacket(
    int PlayerId,
    string PlayerName,
    float SpawnX,
    float SpawnY);

public readonly record struct PlayerLeftPacket(
    int PlayerId,
    string Reason);

public readonly record struct PlayerStatePacket(
    int PlayerId,
    uint Tick,
    float PositionX,
    float PositionY,
    float VelocityX,
    float VelocityY,
    bool IsGrounded,
    bool FacingLeft);

public readonly record struct TileActionPacket(
    TileActionKind Action,
    int TileX,
    int TileY,
    ushort ItemId);

public readonly record struct TileUpdateDeltaPacket(
    int TileX,
    int TileY,
    Tile Tile);

public readonly record struct InventorySlot(
    ushort ItemId,
    ushort Count);

public readonly record struct InventorySyncPacket(
    InventorySlot[] Slots,
    int Gems = 0);

public readonly record struct DropSpawnPacket(
    DroppedItem Drop);

public readonly record struct DropRemovePacket(
    uint DropId,
    int CollectorPlayerId = -1);

public readonly record struct DropUpdatePacket(
    uint DropId,
    ushort Count,
    int CollectorPlayerId = -1);

public readonly record struct DropItemRequestPacket(
    ushort ItemId,
    int Count);

public readonly record struct ChatMessagePacket(
    int SenderPlayerId,
    string SenderName,
    string Message);

public readonly record struct WorldLockStatePacket(
    WorldLockData LockData,
    bool OpenConfigModal = false);

public readonly record struct WrenchTileRequestPacket(
    int TileX,
    int TileY);

public readonly record struct WorldLockConfigRequestPacket(
    WorldLockConfigAction Action,
    string TargetPlayerName = "");

public readonly record struct SystemToastPacket(
    string Message,
    ToastKind Kind = ToastKind.Info);

public readonly record struct TreeUpdatePacket(
    int TileIndex,
    bool IsRemoved,
    TreeData Tree);

public readonly record struct RegisterRequestPacket(
    string Username,
    string Password);

public readonly record struct LoginRequestPacket(
    string Username,
    string Password);

public readonly record struct AuthResponsePacket(
    bool Success,
    int AccountId,
    string DisplayName,
    string ErrorMessage,
    int Gems,
    string LastWorld);

public readonly record struct LogoutRequestPacket;

public readonly record struct TradeOfferSlot(
    ushort ItemId,
    ushort Count)
{
    public static readonly TradeOfferSlot Empty = new(TileIds.Air, 0);
    public bool IsEmpty => ItemId == TileIds.Air || Count == 0;
}

public readonly record struct TradeRequestPacket(
    int TargetAccountId = 0,
    string TargetUsername = "");

public readonly record struct TradeInvitePacket(
    int RequesterAccountId,
    string RequesterName);

public readonly record struct TradeInviteResponsePacket(
    int RequesterAccountId,
    bool Accepted);

public readonly record struct TradeUpdateOfferPacket(
    byte SlotIndex,
    ushort ItemId,
    ushort Count);

public readonly record struct TradeActionPacket(
    TradeActionKind Action);

public readonly record struct TradeStateSyncPacket(
    TradeSessionStatus Status,
    int AccountIdA,
    string NameA,
    int AccountIdB,
    string NameB,
    TradeOfferSlot[] OffersA,
    TradeOfferSlot[] OffersB,
    bool AcceptedA,
    bool AcceptedB,
    bool ConfirmedA,
    bool ConfirmedB,
    float LockCooldownRemaining);

public readonly record struct TradeEndedPacket(
    bool Completed,
    string ReasonMessage);
