namespace GrowSandbox.Shared.Networking;

public static class NetworkConstants
{
    public const int DefaultPort = 9050;
    public const string ConnectionKey = "GrowSandbox_v1";
    public const ushort ProtocolVersion = 1;

    public const int MinUsernameLength = 3;
    public const int MaxUsernameLength = 16;
    public const int MinPasswordLength = 6;
    public const int MaxPasswordLength = 128;
    public const int TradeOfferSlotCount = 4;
}

/// <summary>
/// Identifikasi tipe paket jaringan yang ditulis sebagai byte pertama pada setiap payload UDP.
/// </summary>
public enum PacketType : byte
{
    JoinWorldRequest = 1,
    WorldSnapshot = 2,
    PlayerJoined = 3,
    PlayerLeft = 4,
    PlayerState = 5,
    TileAction = 6,
    TileUpdateDelta = 7,
    InventorySync = 8,
    DropSpawn = 9,
    DropRemove = 10,
    DropUpdate = 11,
    DropItemRequest = 12,
    ChatMessage = 13,
    WorldLockState = 14,
    WrenchTileRequest = 15,
    WorldLockConfigRequest = 16,
    SystemToast = 17,
    TreeUpdate = 18,
    RegisterRequest = 19,
    LoginRequest = 20,
    AuthResponse = 21,
    LogoutRequest = 22,
    TradeRequest = 23,
    TradeInvite = 24,
    TradeInviteResponse = 25,
    TradeUpdateOffer = 26,
    TradeAction = 27,
    TradeStateSync = 28,
    TradeEnded = 29,
}
