using System.Net;
using System.Net.Sockets;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking;
using GrowSandbox.Shared.Networking.Packets;
using LiteNetLib;
using LiteNetLib.Utils;
using Microsoft.Xna.Framework;

namespace GrowSandbox.Client.Network;

public sealed class SpeechBubble
{
    public string Text { get; }
    public float RemainingSeconds { get; set; }

    public SpeechBubble(string text, float durationSeconds = 5f)
    {
        Text = text;
        RemainingSeconds = durationSeconds;
    }
}

public enum ChatMessageKind : byte
{
    Player = 0,
    System = 1,
    Error = 2,
}

public sealed class ChatLogEntry
{
    public int SenderPlayerId { get; }
    public string SenderName { get; }
    public string Message { get; }
    public ChatMessageKind Kind { get; }
    public float AgeSeconds { get; set; }
    public bool IsSystem => Kind != ChatMessageKind.Player;

    public ChatLogEntry(int senderPlayerId, string senderName, string message, ChatMessageKind kind)
    {
        SenderPlayerId = senderPlayerId;
        SenderName = senderName;
        Message = message;
        Kind = kind;
    }
}

public sealed class WorldDropView
{
    public uint DropId { get; }
    public ushort ItemId { get; }
    public ushort Count { get; set; }
    public Vector2 Position { get; }
    public float SpawnTime { get; }
    public float SpawnPhase { get; }

    public WorldDropView(uint dropId, ushort itemId, ushort count, float x, float y, float spawnTime = 0f)
    {
        DropId = dropId;
        ItemId = itemId;
        Count = count;
        Position = new Vector2(x, y);
        SpawnTime = spawnTime;
        SpawnPhase = (dropId * 0.7f + spawnTime) % (MathF.PI * 2f);
    }

    public static WorldDropView FromDroppedItem(in DroppedItem drop) =>
        new(drop.DropId, drop.ItemId, drop.Count, drop.X, drop.Y, drop.SpawnTime);
}

public sealed class IncomingTradeInviteView
{
    public const float DefaultDurationSeconds = 15.0f;

    public int RequesterAccountId { get; }
    public string RequesterName { get; }
    public float RemainingSeconds { get; set; }
    public float TotalSeconds { get; }

    public IncomingTradeInviteView(int requesterAccountId, string requesterName, float durationSeconds = DefaultDurationSeconds)
    {
        RequesterAccountId = requesterAccountId;
        RequesterName = requesterName;
        TotalSeconds = MathF.Max(1f, durationSeconds);
        RemainingSeconds = TotalSeconds;
    }

    public float Progress => Math.Clamp(RemainingSeconds / TotalSeconds, 0f, 1f);
}

public sealed class ActiveTradeView
{
    public TradeSessionStatus Status { get; set; }
    public int AccountIdA { get; }
    public string NameA { get; }
    public int AccountIdB { get; }
    public string NameB { get; }
    public TradeOfferSlot[] OffersA { get; }
    public TradeOfferSlot[] OffersB { get; }
    public bool AcceptedA { get; set; }
    public bool AcceptedB { get; set; }
    public bool ConfirmedA { get; set; }
    public bool ConfirmedB { get; set; }
    public float LockCooldownRemaining { get; set; }

    public ActiveTradeView(in TradeStateSyncPacket sync)
    {
        Status = sync.Status;
        AccountIdA = sync.AccountIdA;
        NameA = sync.NameA;
        AccountIdB = sync.AccountIdB;
        NameB = sync.NameB;
        OffersA = CloneSlots(sync.OffersA);
        OffersB = CloneSlots(sync.OffersB);
        AcceptedA = sync.AcceptedA;
        AcceptedB = sync.AcceptedB;
        ConfirmedA = sync.ConfirmedA;
        ConfirmedB = sync.ConfirmedB;
        LockCooldownRemaining = MathF.Max(0f, sync.LockCooldownRemaining);
    }

    public void UpdateFromSync(in TradeStateSyncPacket sync)
    {
        Status = sync.Status;
        CopySlots(sync.OffersA, OffersA);
        CopySlots(sync.OffersB, OffersB);
        AcceptedA = sync.AcceptedA;
        AcceptedB = sync.AcceptedB;
        ConfirmedA = sync.ConfirmedA;
        ConfirmedB = sync.ConfirmedB;
        LockCooldownRemaining = MathF.Max(0f, sync.LockCooldownRemaining);
    }

    private static TradeOfferSlot[] CloneSlots(TradeOfferSlot[]? source)
    {
        var result = new TradeOfferSlot[NetworkConstants.TradeOfferSlotCount];
        CopySlots(source, result);
        return result;
    }

    private static void CopySlots(TradeOfferSlot[]? source, TradeOfferSlot[] destination)
    {
        for (int i = 0; i < destination.Length; i++)
        {
            destination[i] = source is not null && i < source.Length ? source[i] : TradeOfferSlot.Empty;
        }
    }
}

/// <summary>
/// Representasi pemain jarak jauh dengan Entity Interpolation (linear interpolation / lerp)
/// serta animasi jalan dan speech bubble.
/// </summary>
public sealed class RemotePlayerState
{
    private const float TeleportSnapDistanceSq = 256f * 256f;
    private const float MinInterpolationDuration = 0.03f;
    private const float MaxInterpolationDuration = 0.20f;

    public int PlayerId { get; }
    public string Name { get; set; }

    public Vector2 PreviousPosition { get; private set; }
    public Vector2 TargetPosition { get; private set; }
    public Vector2 RenderPosition { get; private set; }

    public float VelocityX { get; private set; }
    public float VelocityY { get; private set; }
    public bool IsGrounded { get; private set; }
    public bool FacingLeft { get; private set; }
    public float WalkAnimTime { get; private set; }
    public SpeechBubble? ActiveBubble { get; set; }

    private float _interpolationElapsed;
    private float _interpolationDuration = PhysicsConstants.ClientStateSendInterval;

    public RemotePlayerState(int playerId, string name, float spawnX, float spawnY)
    {
        PlayerId = playerId;
        Name = name;

        var initial = new Vector2(spawnX, spawnY);
        PreviousPosition = initial;
        TargetPosition = initial;
        RenderPosition = initial;
        IsGrounded = true;
    }

    public void ApplyNetworkState(in PlayerStatePacket packet)
    {
        var incoming = new Vector2(packet.PositionX, packet.PositionY);
        float distSq = Vector2.DistanceSquared(RenderPosition, incoming);

        if (distSq >= TeleportSnapDistanceSq)
        {
            PreviousPosition = incoming;
            TargetPosition = incoming;
            RenderPosition = incoming;
            _interpolationElapsed = _interpolationDuration;
        }
        else
        {
            float measuredInterval = _interpolationElapsed > 0.001f
                ? _interpolationElapsed
                : PhysicsConstants.ClientStateSendInterval;

            _interpolationDuration = Math.Clamp(
                measuredInterval,
                MinInterpolationDuration,
                MaxInterpolationDuration);

            PreviousPosition = RenderPosition;
            TargetPosition = incoming;
            _interpolationElapsed = 0f;
        }

        VelocityX = packet.VelocityX;
        VelocityY = packet.VelocityY;
        IsGrounded = packet.IsGrounded;
        FacingLeft = packet.FacingLeft;
    }

    public void UpdateInterpolation(float dt)
    {
        Vector2 previousRender = RenderPosition;
        if (_interpolationDuration <= 0f)
        {
            RenderPosition = TargetPosition;
        }
        else
        {
            _interpolationElapsed = MathF.Min(_interpolationElapsed + dt, _interpolationDuration);
            float t = Math.Clamp(_interpolationElapsed / _interpolationDuration, 0f, 1f);
            RenderPosition = Vector2.Lerp(PreviousPosition, TargetPosition, t);
        }

        // Gunakan kecepatan hasil interpolasi atau kecepatan jaringan agar animasi Walk/Jump/Fall mulus.
        float derivedVx = dt > 0.0001f ? (RenderPosition.X - previousRender.X) / dt : VelocityX;
        float effectiveVx = MathF.Abs(derivedVx) > 10f ? derivedVx : VelocityX;
        if (effectiveVx < -10f) FacingLeft = true;
        else if (effectiveVx > 10f) FacingLeft = false;

        if (IsGrounded && MathF.Abs(effectiveVx) > 10f)
        {
            WalkAnimTime += dt * 10f;
        }
        else
        {
            WalkAnimTime = 0f;
        }

        if (ActiveBubble is not null)
        {
            ActiveBubble.RemainingSeconds -= dt;
            if (ActiveBubble.RemainingSeconds <= 0f)
            {
                ActiveBubble = null;
            }
        }
    }
}

/// <summary>
/// Klien jaringan UDP berbasis LiteNetLib untuk world snapshot, sinkronisasi blok,
/// inventory, item drop, chat/speech bubble, dan World Lock.
/// </summary>
public sealed class GameClient : IDisposable
{
    public const string DefaultHost = "127.0.0.1";
    public const string DefaultWorldName = "START";
    private const int MaxChatHistory = 24;

    private readonly EventBasedNetListener _listener;
    private readonly NetManager _netManager;
    private readonly NetDataWriter _writer = new();
    private readonly Tile[] _tiles = new Tile[WorldConstants.TotalTiles];
    private readonly Dictionary<int, RemotePlayerState> _remotePlayers = new();
    private readonly Dictionary<uint, WorldDropView> _drops = new();
    private readonly Dictionary<int, TreeData> _trees = new();
    private readonly List<InventorySlot> _inventory = new();
    private readonly List<ChatLogEntry> _chatLog = new();

    private NetPeer? _serverPeer;
    private string _connectedHost = string.Empty;
    private int _connectedPort;
    private bool _hasPendingAuth;
    private bool _pendingAuthIsRegister;
    private string _pendingAuthUsername = string.Empty;
    private string _pendingAuthPassword = string.Empty;
    private float _connectTimeoutTimer;

    private const float ConnectTimeoutSeconds = 10.0f;

    public Tile[] Tiles => _tiles;
    public IReadOnlyDictionary<int, RemotePlayerState> RemotePlayers => _remotePlayers;
    public IReadOnlyDictionary<uint, WorldDropView> Drops => _drops;
    public IReadOnlyDictionary<int, TreeData> Trees => _trees;
    public IReadOnlyList<InventorySlot> Inventory => _inventory;
    public IReadOnlyList<ChatLogEntry> ChatLog => _chatLog;

    public bool IsConnected => _serverPeer is { ConnectionState: ConnectionState.Connected };
    public bool IsConnecting { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public int LocalAccountId { get; private set; }
    public int LocalPlayerId => _serverPeer?.RemoteId ?? -1;
    public int PingMs => _serverPeer is { ConnectionState: ConnectionState.Connected } ? _serverPeer.RoundTripTime : 0;
    public string LocalPlayerName { get; private set; } = string.Empty;
    public string LastWorldName { get; private set; } = DefaultWorldName;
    public int GemCount { get; private set; }
    public SpeechBubble? LocalSpeechBubble { get; private set; }

    public bool HasWorldSnapshot { get; private set; }
    public string CurrentWorldName { get; private set; } = string.Empty;
    public WorldLockData LockData { get; private set; } = WorldLockData.Unlocked();
    public string WorldLockOwner => LockData.IsLocked ? LockData.OwnerName : string.Empty;
    public int WorldLockTileX => LockData.IsLocked ? LockData.LockX : -1;
    public int WorldLockTileY => LockData.IsLocked ? LockData.LockY : -1;
    public bool IsWorldLocked => LockData.IsLocked;
    public bool IsWorldPublicLock => LockData.IsLocked && LockData.IsPublic;

    public string StatusText { get; private set; } = "Disconnected";
    public int SpawnTileX { get; private set; } = WorldConstants.Width / 2;
    public int SpawnTileY { get; private set; } = 29;
    public IncomingTradeInviteView? PendingTradeInvite { get; private set; }
    public ActiveTradeView? ActiveTrade { get; private set; }
    public bool IsInActiveTrade => ActiveTrade is not null;

    public event Action<AuthResponsePacket>? AuthResponseReceived;
    public event Action<string>? DisconnectedFromServer;
    public event Action<string, int, int>? WorldSnapshotReceived;
    public event Action<PlayerStatePacket>? LocalPlayerStateCorrected;
    public event Action<int, int, Tile, Tile>? TileChanged;
    public event Action<WorldDropView, bool>? DropPickedUp;
    public event Action<WorldLockData, bool>? WorldLockStateChanged;
    public event Action<string, ToastKind>? SystemToastReceived;
    public event Action<TradeInvitePacket>? TradeInviteReceived;
    public event Action<TradeStateSyncPacket>? TradeStateSynced;
    public event Action<TradeEndedPacket>? TradeEndedReceived;

    public GameClient()
    {
        _listener = new EventBasedNetListener();
        _netManager = new NetManager(_listener)
        {
            AutoRecycle = true,
            UpdateTime = 15,
        };

        RegisterEvents();
    }

    private void RegisterEvents()
    {
        _listener.PeerConnectedEvent += peer =>
        {
            _serverPeer = peer;
            IsConnecting = false;
            _connectTimeoutTimer = 0f;
            StatusText = "Connected — Authenticating...";

            // JANGAN otomatis mengirim JoinWorldRequest saat terhubung!
            // Kirim paket Register / Login yang menunggu, lalu tunggu AuthResponsePacket.Success == true.
            if (_hasPendingAuth)
            {
                bool isRegister = _pendingAuthIsRegister;
                string user = _pendingAuthUsername;
                string pass = _pendingAuthPassword;
                ClearPendingAuth();

                if (isRegister)
                {
                    SendRegisterRequest(user, pass);
                }
                else
                {
                    SendLoginRequest(user, pass);
                }
            }
        };

        _listener.PeerDisconnectedEvent += (_, disconnectInfo) =>
        {
            bool wasActive = IsConnected || IsConnecting || IsAuthenticated || HasWorldSnapshot;
            _serverPeer = null;
            IsConnecting = false;
            _connectTimeoutTimer = 0f;
            IsAuthenticated = false;
            LocalAccountId = 0;
            ClearPendingAuth();
            ClearLocalWorldState();
            StatusText = $"Disconnected ({disconnectInfo.Reason})";

            if (wasActive)
            {
                DisconnectedFromServer?.Invoke(disconnectInfo.Reason.ToString());
            }
        };

        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    /// <summary>
    /// Menghubungkan ke server target (bila belum terhubung atau alamat berubah) lalu mengirim
    /// <see cref="RegisterRequestPacket"/> atau <see cref="LoginRequestPacket"/> melalui <c>ReliableOrdered</c>.
    /// </summary>
    public void ConnectAndAuthenticate(string host, int port, bool isRegister, string username, string password)
    {
        bool sameEndpoint = IsConnected &&
                            string.Equals(_connectedHost, host, StringComparison.OrdinalIgnoreCase) &&
                            _connectedPort == port;

        if (sameEndpoint)
        {
            ClearPendingAuth();
            StatusText = isRegister ? "Creating account..." : "Logging in...";
            if (isRegister)
            {
                SendRegisterRequest(username, password);
            }
            else
            {
                SendLoginRequest(username, password);
            }

            return;
        }

        if (_netManager.IsRunning)
        {
            _netManager.DisconnectAll();
        }
        else
        {
            _netManager.Start();
        }

        _serverPeer = null;
        IsAuthenticated = false;
        LocalAccountId = 0;
        _connectedHost = host;
        _connectedPort = port;
        _hasPendingAuth = true;
        _pendingAuthIsRegister = isRegister;
        _pendingAuthUsername = username;
        _pendingAuthPassword = password;
        IsConnecting = true;
        _connectTimeoutTimer = ConnectTimeoutSeconds;

        StatusText = $"Connecting to {host}:{port}...";
        try
        {
            // Prioritaskan IPv4 untuk hostname tunnel seperti Playit.gg (*.tun.ply.gg) yang memiliki record AAAA + A,
            // agar klien di jaringan/ISP IPv4-only tidak gagal karena mencoba rute IPv6 terlebih dahulu.
            IPAddress? resolvedIp = IPAddress.TryParse(host, out IPAddress? literalIp)
                ? literalIp
                : NetUtils.ResolveAddress(host, AddressFamily.InterNetwork)
                  ?? NetUtils.ResolveAddress(host, AddressFamily.InterNetworkV6);

            if (resolvedIp is null)
            {
                IsConnecting = false;
                _connectTimeoutTimer = 0f;
                ClearPendingAuth();
                StatusText = $"Host tidak ditemukan: {host}";
                DisconnectedFromServer?.Invoke("DnsResolveFailed");
                return;
            }

            _netManager.Connect(new IPEndPoint(resolvedIp, port), NetworkConstants.ConnectionKey);
        }
        catch (Exception ex)
        {
            IsConnecting = false;
            _connectTimeoutTimer = 0f;
            ClearPendingAuth();
            StatusText = $"Gagal menghubungkan: {ex.Message}";
            DisconnectedFromServer?.Invoke("ConnectException");
        }
    }

    public void SendRegisterRequest(string username, string password)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var request = new RegisterRequestPacket(username, password);
        PacketSerializer.WriteRegisterRequest(_writer, in request);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendLoginRequest(string username, string password)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var request = new LoginRequestPacket(username, password);
        PacketSerializer.WriteLoginRequest(_writer, in request);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendLogoutRequest()
    {
        if (_serverPeer is { ConnectionState: ConnectionState.Connected })
        {
            _writer.Reset();
            var logout = new LogoutRequestPacket();
            PacketSerializer.WriteLogoutRequest(_writer, in logout);
            _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
        }

        IsAuthenticated = false;
        LocalAccountId = 0;
        ClearPendingAuth();
        ClearLocalWorldState();
        StatusText = "Logged out";
    }

    public void ClearLocalWorldState()
    {
        Array.Clear(_tiles);
        _remotePlayers.Clear();
        _drops.Clear();
        _trees.Clear();
        _inventory.Clear();
        _chatLog.Clear();
        GemCount = 0;
        LocalSpeechBubble = null;
        HasWorldSnapshot = false;
        CurrentWorldName = string.Empty;
        LockData = WorldLockData.Unlocked();
        PendingTradeInvite = null;
        ActiveTrade = null;
    }

    private void ClearPendingAuth()
    {
        _hasPendingAuth = false;
        _pendingAuthIsRegister = false;
        _pendingAuthUsername = string.Empty;
        _pendingAuthPassword = string.Empty;
    }

    public void SendJoinWorldRequest(string worldName)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var request = new JoinWorldRequestPacket(
            WorldName: worldName,
            PlayerName: LocalPlayerName,
            ProtocolVersion: NetworkConstants.ProtocolVersion);

        PacketSerializer.WriteJoinWorldRequest(_writer, in request);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendPlayerState(in PlayerStatePacket state)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        PacketSerializer.WritePlayerState(_writer, in state);
        _serverPeer.Send(_writer, DeliveryMethod.Sequenced);
    }

    public void SendTileAction(in TileActionPacket action)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        PacketSerializer.WriteTileAction(_writer, in action);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendDropItemRequest(ushort itemId, int count)
    {
        if (count <= 0 || _serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var request = new DropItemRequestPacket(itemId, count);
        PacketSerializer.WriteDropItemRequest(_writer, in request);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendWrenchTileRequest(int tileX, int tileY)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var request = new WrenchTileRequestPacket(tileX, tileY);
        PacketSerializer.WriteWrenchTileRequest(_writer, in request);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendWorldLockConfigRequest(WorldLockConfigAction action, string targetPlayerName = "")
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var request = new WorldLockConfigRequestPacket(action, targetPlayerName ?? string.Empty);
        PacketSerializer.WriteWorldLockConfigRequest(_writer, in request);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendTradeRequest(string targetUsername, int targetAccountId = 0)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        string trimmed = (targetUsername ?? string.Empty).Trim();
        if (targetAccountId <= 0 && trimmed.Length == 0)
        {
            return;
        }

        _writer.Reset();
        var request = new TradeRequestPacket(targetAccountId, trimmed);
        PacketSerializer.WriteTradeRequest(_writer, in request);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendTradeInviteResponse(int requesterAccountId, bool accepted)
    {
        PendingTradeInvite = null;

        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var response = new TradeInviteResponsePacket(requesterAccountId, accepted);
        PacketSerializer.WriteTradeInviteResponse(_writer, in response);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendTradeUpdateOffer(byte slotIndex, ushort itemId, ushort count)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var update = new TradeUpdateOfferPacket(slotIndex, itemId, count);
        PacketSerializer.WriteTradeUpdateOffer(_writer, in update);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void SendTradeAction(TradeActionKind action)
    {
        if (_serverPeer is null || _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var packet = new TradeActionPacket(action);
        PacketSerializer.WriteTradeAction(_writer, in packet);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public bool IsLocalWorldLockOwner() =>
        LockData.IsLocked &&
        ((LockData.OwnerAccountId > 0 && LockData.OwnerAccountId == LocalAccountId) ||
         string.Equals(LockData.OwnerName, LocalPlayerName, StringComparison.OrdinalIgnoreCase));

    public void AddLocalSystemMessage(string message, ChatMessageKind kind = ChatMessageKind.System)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _chatLog.Add(new ChatLogEntry(-1, kind == ChatMessageKind.Error ? "WARN" : "INFO", message.Trim(), kind));
        while (_chatLog.Count > MaxChatHistory)
        {
            _chatLog.RemoveAt(0);
        }
    }

    public void SendChatMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message) ||
            _serverPeer is null ||
            _serverPeer.ConnectionState != ConnectionState.Connected)
        {
            return;
        }

        _writer.Reset();
        var packet = new ChatMessagePacket(LocalPlayerId, LocalPlayerName, message.Trim());
        PacketSerializer.WriteChatMessage(_writer, in packet);
        _serverPeer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    public void PollEvents()
    {
        if (_netManager.IsRunning)
        {
            _netManager.PollEvents();
        }
    }

    public void UpdateTimers(float dt)
    {
        if (IsConnecting && _connectTimeoutTimer > 0f)
        {
            _connectTimeoutTimer -= dt;
            if (_connectTimeoutTimer <= 0f)
            {
                IsConnecting = false;
                ClearPendingAuth();
                if (_netManager.IsRunning)
                {
                    _netManager.DisconnectAll();
                }

                StatusText = "Connection timed out";
                DisconnectedFromServer?.Invoke("Timeout");
            }
        }

        foreach (RemotePlayerState player in _remotePlayers.Values)
        {
            player.UpdateInterpolation(dt);
        }

        if (LocalSpeechBubble is not null)
        {
            LocalSpeechBubble.RemainingSeconds -= dt;
            if (LocalSpeechBubble.RemainingSeconds <= 0f)
            {
                LocalSpeechBubble = null;
            }
        }

        for (int i = 0; i < _chatLog.Count; i++)
        {
            _chatLog[i].AgeSeconds += dt;
        }

        if (PendingTradeInvite is not null)
        {
            PendingTradeInvite.RemainingSeconds -= dt;
            if (PendingTradeInvite.RemainingSeconds <= 0f)
            {
                PendingTradeInvite = null;
            }
        }

        if (ActiveTrade is not null && ActiveTrade.LockCooldownRemaining > 0f)
        {
            ActiveTrade.LockCooldownRemaining = MathF.Max(0f, ActiveTrade.LockCooldownRemaining - dt);
            if (ActiveTrade.LockCooldownRemaining <= 0f && ActiveTrade.Status == TradeSessionStatus.CooldownLocked)
            {
                ActiveTrade.Status = ActiveTrade.AcceptedA && ActiveTrade.AcceptedB
                    ? TradeSessionStatus.ReadyForFinalConfirm
                    : TradeSessionStatus.Negotiating;
            }
        }
    }

    private void OnNetworkReceive(
        NetPeer peer,
        NetPacketReader reader,
        byte channelNumber,
        DeliveryMethod deliveryMethod)
    {
        try
        {
            PacketType packetType = PacketSerializer.ReadPacketType(reader);

            switch (packetType)
            {
                case PacketType.WorldSnapshot:
                    HandleWorldSnapshot(PacketSerializer.ReadWorldSnapshot(reader));
                    break;

                case PacketType.TileUpdateDelta:
                    HandleTileUpdateDelta(PacketSerializer.ReadTileUpdateDelta(reader));
                    break;

                case PacketType.PlayerJoined:
                    HandlePlayerJoined(PacketSerializer.ReadPlayerJoined(reader));
                    break;

                case PacketType.PlayerLeft:
                    HandlePlayerLeft(PacketSerializer.ReadPlayerLeft(reader));
                    break;

                case PacketType.PlayerState:
                    HandlePlayerState(PacketSerializer.ReadPlayerState(reader));
                    break;

                case PacketType.InventorySync:
                    HandleInventorySync(PacketSerializer.ReadInventorySync(reader));
                    break;

                case PacketType.DropSpawn:
                    HandleDropSpawn(PacketSerializer.ReadDropSpawn(reader));
                    break;

                case PacketType.DropRemove:
                    HandleDropRemove(PacketSerializer.ReadDropRemove(reader));
                    break;

                case PacketType.DropUpdate:
                    HandleDropUpdate(PacketSerializer.ReadDropUpdate(reader));
                    break;

                case PacketType.ChatMessage:
                    HandleChatMessage(PacketSerializer.ReadChatMessage(reader));
                    break;

                case PacketType.WorldLockState:
                    HandleWorldLockState(PacketSerializer.ReadWorldLockState(reader));
                    break;

                case PacketType.SystemToast:
                    HandleSystemToast(PacketSerializer.ReadSystemToast(reader));
                    break;

                case PacketType.TreeUpdate:
                    HandleTreeUpdate(PacketSerializer.ReadTreeUpdate(reader));
                    break;

                case PacketType.AuthResponse:
                    HandleAuthResponse(PacketSerializer.ReadAuthResponse(reader));
                    break;

                case PacketType.TradeInvite:
                    HandleTradeInvite(PacketSerializer.ReadTradeInvite(reader));
                    break;

                case PacketType.TradeStateSync:
                    HandleTradeStateSync(PacketSerializer.ReadTradeStateSync(reader));
                    break;

                case PacketType.TradeEnded:
                    HandleTradeEnded(PacketSerializer.ReadTradeEnded(reader));
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Packet error: {ex.Message}";
        }
    }

    private void HandleWorldSnapshot(WorldSnapshotPacket snapshot)
    {
        Array.Copy(snapshot.Tiles, _tiles, WorldConstants.TotalTiles);
        CurrentWorldName = snapshot.WorldName;
        HasWorldSnapshot = true;
        _remotePlayers.Clear();
        _drops.Clear();
        _trees.Clear();
        PendingTradeInvite = null;
        ActiveTrade = null;

        if (snapshot.Drops is not null)
        {
            for (int i = 0; i < snapshot.Drops.Length; i++)
            {
                DroppedItem drop = snapshot.Drops[i];
                if (drop.DropId != 0 && drop.Count > 0)
                {
                    _drops[drop.DropId] = WorldDropView.FromDroppedItem(in drop);
                }
            }
        }

        if (snapshot.Trees is not null)
        {
            for (int i = 0; i < snapshot.Trees.Length; i++)
            {
                TreeData tree = snapshot.Trees[i];
                if ((uint)tree.TileIndex < WorldConstants.TotalTiles)
                {
                    _trees[tree.TileIndex] = tree;
                }
            }
        }

        LockData = snapshot.LockData?.Clone() ?? WorldLockData.Unlocked();

        LocateSpawnTile();
        StatusText = $"World: {CurrentWorldName}";
        WorldSnapshotReceived?.Invoke(CurrentWorldName, SpawnTileX, SpawnTileY);
    }

    private void HandleTileUpdateDelta(TileUpdateDeltaPacket delta)
    {
        if (!Tile.IsInBounds(delta.TileX, delta.TileY))
        {
            return;
        }

        int index = Tile.GetIndex(delta.TileX, delta.TileY);
        Tile previous = _tiles[index];
        _tiles[index] = delta.Tile;
        TileChanged?.Invoke(delta.TileX, delta.TileY, previous, delta.Tile);
    }

    private void HandlePlayerJoined(PlayerJoinedPacket packet)
    {
        if (packet.PlayerId == LocalPlayerId)
        {
            return;
        }

        _remotePlayers[packet.PlayerId] = new RemotePlayerState(
            packet.PlayerId,
            packet.PlayerName,
            packet.SpawnX,
            packet.SpawnY);
    }

    private void HandlePlayerLeft(PlayerLeftPacket packet)
    {
        _remotePlayers.Remove(packet.PlayerId);
    }

    private void HandlePlayerState(PlayerStatePacket packet)
    {
        if (packet.PlayerId == LocalPlayerId)
        {
            LocalPlayerStateCorrected?.Invoke(packet);
            return;
        }

        if (_remotePlayers.TryGetValue(packet.PlayerId, out RemotePlayerState? player))
        {
            player.ApplyNetworkState(in packet);
        }
    }

    private void HandleInventorySync(InventorySyncPacket packet)
    {
        _inventory.Clear();
        GemCount = Math.Max(0, packet.Gems);

        if (packet.Slots is null)
        {
            return;
        }

        for (int i = 0; i < packet.Slots.Length; i++)
        {
            InventorySlot slot = packet.Slots[i];
            if (slot.ItemId == TileIds.Gem)
            {
                GemCount = Math.Max(GemCount, slot.Count);
            }
            else if (slot.Count > 0)
            {
                _inventory.Add(slot);
            }
        }
    }

    private void HandleDropSpawn(DropSpawnPacket packet)
    {
        DroppedItem drop = packet.Drop;
        if (drop.DropId == 0 || drop.Count == 0)
        {
            return;
        }

        _drops[drop.DropId] = WorldDropView.FromDroppedItem(in drop);
    }

    private void HandleDropRemove(DropRemovePacket packet)
    {
        if (_drops.Remove(packet.DropId, out WorldDropView? drop) && packet.CollectorPlayerId >= 0)
        {
            bool isLocalCollector = packet.CollectorPlayerId == LocalPlayerId;
            DropPickedUp?.Invoke(drop, isLocalCollector);
        }
    }

    private void HandleDropUpdate(DropUpdatePacket packet)
    {
        if (!_drops.TryGetValue(packet.DropId, out WorldDropView? existing))
        {
            return;
        }

        ushort previousCount = existing.Count;
        if (packet.Count == 0)
        {
            _drops.Remove(packet.DropId);
        }
        else
        {
            existing.Count = packet.Count;
        }

        if (packet.CollectorPlayerId >= 0 && previousCount > packet.Count)
        {
            ushort delta = (ushort)(previousCount - packet.Count);
            var deltaView = new WorldDropView(
                existing.DropId,
                existing.ItemId,
                delta,
                existing.Position.X,
                existing.Position.Y,
                existing.SpawnTime);
            bool isLocalCollector = packet.CollectorPlayerId == LocalPlayerId;
            DropPickedUp?.Invoke(deltaView, isLocalCollector);
        }
    }

    private void HandleChatMessage(ChatMessagePacket packet)
    {
        ChatMessageKind kind = ChatMessageKind.Player;
        if (packet.SenderPlayerId < 0)
        {
            bool isWarning = packet.Message.Contains("locked by", StringComparison.OrdinalIgnoreCase)
                          || packet.Message.Contains("already has", StringComparison.OrdinalIgnoreCase)
                          || packet.Message.Contains("error", StringComparison.OrdinalIgnoreCase)
                          || packet.Message.Contains("gagal", StringComparison.OrdinalIgnoreCase);
            kind = isWarning ? ChatMessageKind.Error : ChatMessageKind.System;
        }

        _chatLog.Add(new ChatLogEntry(packet.SenderPlayerId, packet.SenderName, packet.Message, kind));
        while (_chatLog.Count > MaxChatHistory)
        {
            _chatLog.RemoveAt(0);
        }

        if (packet.SenderPlayerId >= 0)
        {
            if (packet.SenderPlayerId == LocalPlayerId)
            {
                LocalSpeechBubble = new SpeechBubble(packet.Message);
            }
            else if (_remotePlayers.TryGetValue(packet.SenderPlayerId, out RemotePlayerState? remote))
            {
                remote.ActiveBubble = new SpeechBubble(packet.Message);
            }
        }
    }

    private void HandleWorldLockState(WorldLockStatePacket packet)
    {
        LockData = packet.LockData?.Clone() ?? WorldLockData.Unlocked();
        WorldLockStateChanged?.Invoke(LockData, packet.OpenConfigModal);
    }

    private void HandleSystemToast(SystemToastPacket packet)
    {
        if (!string.IsNullOrWhiteSpace(packet.Message))
        {
            SystemToastReceived?.Invoke(packet.Message, packet.Kind);
        }
    }

    private void HandleTreeUpdate(TreeUpdatePacket packet)
    {
        if (packet.IsRemoved)
        {
            _trees.Remove(packet.TileIndex);
        }
        else if ((uint)packet.TileIndex < WorldConstants.TotalTiles)
        {
            _trees[packet.TileIndex] = packet.Tree;
        }
    }

    private void HandleAuthResponse(AuthResponsePacket packet)
    {
        if (packet.Success && packet.AccountId > 0)
        {
            IsAuthenticated = true;
            LocalAccountId = packet.AccountId;
            LocalPlayerName = packet.DisplayName;
            GemCount = Math.Max(0, packet.Gems);
            LastWorldName = string.IsNullOrWhiteSpace(packet.LastWorld) ? DefaultWorldName : packet.LastWorld;
            StatusText = $"Authenticated as {LocalPlayerName}";
        }
        else if (!packet.Success)
        {
            IsAuthenticated = false;
            LocalAccountId = 0;
            StatusText = string.IsNullOrWhiteSpace(packet.ErrorMessage) ? "Authentication failed" : packet.ErrorMessage;
        }

        AuthResponseReceived?.Invoke(packet);
    }

    private void HandleTradeInvite(TradeInvitePacket packet)
    {
        if (string.IsNullOrWhiteSpace(packet.RequesterName))
        {
            return;
        }

        PendingTradeInvite = new IncomingTradeInviteView(packet.RequesterAccountId, packet.RequesterName);
        TradeInviteReceived?.Invoke(packet);
    }

    private void HandleTradeStateSync(TradeStateSyncPacket packet)
    {
        PendingTradeInvite = null;
        if (ActiveTrade is null ||
            ActiveTrade.AccountIdA != packet.AccountIdA ||
            ActiveTrade.AccountIdB != packet.AccountIdB)
        {
            ActiveTrade = new ActiveTradeView(in packet);
        }
        else
        {
            ActiveTrade.UpdateFromSync(in packet);
        }

        TradeStateSynced?.Invoke(packet);
    }

    private void HandleTradeEnded(TradeEndedPacket packet)
    {
        ActiveTrade = null;
        TradeEndedReceived?.Invoke(packet);
    }

    private void LocateSpawnTile()
    {
        for (int y = 0; y < WorldConstants.Height; y++)
        {
            int rowOffset = y * WorldConstants.Width;
            for (int x = 0; x < WorldConstants.Width; x++)
            {
                ushort fg = _tiles[rowOffset + x].ForegroundId;
                if (fg == TileIds.WhiteDoor || fg == TileIds.Spawn)
                {
                    SpawnTileX = x;
                    SpawnTileY = y;
                    return;
                }
            }
        }

        SpawnTileX = WorldConstants.Width / 2;
        SpawnTileY = 29;
    }

    public void Disconnect()
    {
        if (_netManager.IsRunning)
        {
            _netManager.Stop();
        }

        _serverPeer = null;
        IsConnecting = false;
        IsAuthenticated = false;
        LocalAccountId = 0;
        ClearPendingAuth();
        ClearLocalWorldState();
        StatusText = "Disconnected";
    }

    public void Dispose()
    {
        Disconnect();
    }
}
