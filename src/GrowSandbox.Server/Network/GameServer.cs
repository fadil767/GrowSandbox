using System.Collections.Concurrent;
using System.Diagnostics;
using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.Security;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking;
using GrowSandbox.Shared.Networking.Packets;
using LiteNetLib;
using LiteNetLib.Utils;

namespace GrowSandbox.Server.Network;

/// <summary>
/// Sesi koneksi peer yang dikelola oleh <see cref="GameServer"/> untuk gerbang autentikasi,
/// anti-double-login, serta pelacakan world dan saldo akun aktif.
/// anti-double-login, cache profil akun (<see cref="Inventory"/>, <see cref="Gems"/>, <see cref="LastWorld"/>),
/// serta pelacakan <see cref="IsProfileDirty"/> untuk auto-save anti-rollback.
/// </summary>
public sealed class PeerSession
{
    private readonly object _profileLock = new();

    public int PeerId { get; }
    public NetPeer? Peer { get; }
    public string RemoteAddress { get; }
    public bool IsAuthenticated { get; set; }
    public int AccountId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string CurrentWorldName { get; set; } = string.Empty;
    public int Gems { get; set; }
    public Dictionary<ushort, ushort> Inventory { get; private set; } = new();
    public byte[]? InventoryBlob { get; set; }
    public string LastWorld { get; set; } = "START";
    public bool IsProfileDirty { get; set; }
    public long ProfileVersion { get; private set; }

    public PeerSession(int peerId, NetPeer? peer, string remoteAddress)
    {
        PeerId = peerId;
        Peer = peer;
        RemoteAddress = string.IsNullOrWhiteSpace(remoteAddress) ? $"peer:{peerId}" : remoteAddress;
    }

    public void LoadAccountProfile(AccountRecord account)
    {
        lock (_profileLock)
        {
            Dictionary<ushort, ushort> decoded = AccountInventoryCodec.Decode(account.InventoryBlob)
                ?? PlayerSession.CreateStarterInventory();
            Inventory = decoded;
            InventoryBlob = AccountInventoryCodec.Encode(decoded);
            Gems = Math.Clamp(account.Gems, 0, ItemCatalog.MaxGemBalance);
            LastWorld = WorldManager.NormalizeWorldName(account.LastWorld);
            IsProfileDirty = false;
            ProfileVersion++;
        }
    }

    public long UpdateCachedProfile(Dictionary<ushort, ushort> inventoryCopy, int gems, string worldName)
    {
        lock (_profileLock)
        {
            Inventory = new Dictionary<ushort, ushort>(inventoryCopy);
            InventoryBlob = AccountInventoryCodec.Encode(Inventory);
            Gems = Math.Clamp(gems, 0, ItemCatalog.MaxGemBalance);
            if (!string.IsNullOrWhiteSpace(worldName))
            {
                LastWorld = WorldManager.NormalizeWorldName(worldName);
            }

            IsProfileDirty = true;
            ProfileVersion++;
            return ProfileVersion;
        }
    }

    public (int AccountId, int Gems, byte[] InventoryBlob, string LastWorld, long Version) CaptureProfileForSave()
    {
        lock (_profileLock)
        {
            byte[] blob = InventoryBlob ?? AccountInventoryCodec.Encode(Inventory);
            return (AccountId, Gems, blob, LastWorld, ProfileVersion);
        }
    }

    public Dictionary<ushort, ushort> CloneInventory()
    {
        lock (_profileLock)
        {
            return new Dictionary<ushort, ushort>(Inventory);
        }
    }

    public InventorySlot[] ToInventorySlots()
    {
        lock (_profileLock)
        {
            return PlayerSession.BuildInventorySlots(Inventory);
        }
    }

    public void MarkProfileSaved(long savedVersion)
    {
        lock (_profileLock)
        {
            if (ProfileVersion == savedVersion)
            {
                IsProfileDirty = false;
            }
        }
    }

    public void ApplyPersistedProfile(Dictionary<ushort, ushort> inventoryCopy, int gems, string worldName)
    {
        lock (_profileLock)
        {
            Inventory = new Dictionary<ushort, ushort>(inventoryCopy);
            InventoryBlob = AccountInventoryCodec.Encode(Inventory);
            Gems = Math.Clamp(gems, 0, ItemCatalog.MaxGemBalance);
            if (!string.IsNullOrWhiteSpace(worldName))
            {
                LastWorld = WorldManager.NormalizeWorldName(worldName);
            }

            IsProfileDirty = false;
            ProfileVersion++;
        }
    }
}

/// <summary>
/// Authoritative UDP game server berbasis LiteNetLib pada port 9050
/// dengan fixed tick loop (20-30 tick/detik), gerbang autentikasi akun, anti-double-login,
/// rate limit percobaan login/register gagal, dan auto-save SQLite setiap 30 detik.
/// rate limit percobaan login/register gagal, auto-save world &amp; profil pemain setiap 30 detik,
/// immediate save untuk transaksi World Lock, serta flush graceful saat shutdown.
/// </summary>
public sealed class GameServer : IDisposable
{
    public const int DefaultTickRate = 30;
    public static readonly TimeSpan DefaultAutoSaveInterval = TimeSpan.FromSeconds(30);

    private readonly WorldManager _worldManager;
    private readonly IAccountRepository _accountRepository;
    private readonly AuthRateLimiter _authRateLimiter;
    private readonly EventBasedNetListener _listener;
    private readonly NetManager _netManager;
    private readonly ConcurrentDictionary<int, PeerSession> _peerSessions = new();
    private readonly ConcurrentDictionary<int, PeerSession> _activeAccounts = new();
    private readonly NetDataWriter _authWriter = new();
    private readonly TimeSpan _autoSaveInterval;
    private int _shutdownFlushed;

    public int Port { get; }
    public int TickRate { get; }
    public bool IsRunning => _netManager.IsRunning;
    public int ActiveAuthenticatedAccountCount => _activeAccounts.Count;

    public GameServer(
        WorldManager worldManager,
        IAccountRepository? accountRepository = null,
        int port = NetworkConstants.DefaultPort,
        int tickRate = DefaultTickRate,
        TimeSpan? autoSaveInterval = null,
        Func<DateTime>? utcNowProvider = null)
    {
        ArgumentNullException.ThrowIfNull(worldManager);
        if (tickRate is < 20 or > 30)
        {
            throw new ArgumentOutOfRangeException(
                nameof(tickRate),
                "TickRate server harus berada di rentang 20-30 tick/detik.");
        }

        _worldManager = worldManager;
        _accountRepository = accountRepository ?? new SqliteAccountRepository(":memory:");
        _authRateLimiter = new AuthRateLimiter(utcNowProvider: utcNowProvider);
        Port = port;
        TickRate = tickRate;
        _autoSaveInterval = autoSaveInterval ?? DefaultAutoSaveInterval;

        // Sambungkan repositori akun & hook perubahan profil ke seluruh world aktif maupun baru.
        _worldManager.ConfigureAccountIntegration(
            _accountRepository,
            OnPlayerProfileChanged,
            OnPlayerProfilePersistedAtomically);

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
        _listener.ConnectionRequestEvent += request =>
        {
            request.AcceptIfKey(NetworkConstants.ConnectionKey);
        };

        _listener.PeerConnectedEvent += peer =>
        {
            string remoteAddr = peer.Address?.ToString() ?? $"peer:{peer.Id}";
            _peerSessions[peer.Id] = new PeerSession(peer.Id, peer, remoteAddr);
            Console.WriteLine($"[GameServer] Peer terhubung: Id={peer.Id} ({remoteAddr}) [Menunggu Autentikasi]");
        };

        _listener.PeerDisconnectedEvent += (peer, disconnectInfo) =>
        {
            Console.WriteLine($"[GameServer] Peer terputus: Id={peer.Id} ({disconnectInfo.Reason})");

            if (_peerSessions.TryRemove(peer.Id, out PeerSession? session))
            {
                EndAuthenticatedSession(session, disconnectInfo.Reason.ToString());
            }
        };

        _listener.NetworkReceiveEvent += OnNetworkReceive;
    }

    private void OnNetworkReceive(
        NetPeer peer,
        NetPacketReader reader,
        byte channelNumber,
        DeliveryMethod deliveryMethod)
    {
        try
        {
            PeerSession session = _peerSessions.GetOrAdd(
                peer.Id,
                id => new PeerSession(id, peer, peer.Address?.ToString() ?? $"peer:{id}"));

            PacketType packetType = PacketSerializer.ReadPacketType(reader);

            // 1. Paket autentikasi dapat diterima sebelum login (Register / Login / Logout).
            switch (packetType)
            {
                case PacketType.RegisterRequest:
                {
                    RegisterRequestPacket req = PacketSerializer.ReadRegisterRequest(reader);
                    AuthResponsePacket resp = ProcessRegister(session, req);
                    SendAuthResponse(peer, in resp);
                    if (resp.Success)
                    {
                        SendPeerInventorySync(session);
                    }

                    return;
                }

                case PacketType.LoginRequest:
                {
                    LoginRequestPacket req = PacketSerializer.ReadLoginRequest(reader);
                    AuthResponsePacket resp = ProcessLogin(session, req);
                    SendAuthResponse(peer, in resp);
                    if (resp.Success)
                    {
                        SendPeerInventorySync(session);
                    }

                    return;
                }

                case PacketType.LogoutRequest:
                {
                    _ = PacketSerializer.ReadLogoutRequest(reader);
                    AuthResponsePacket resp = ProcessLogout(session);
                    SendAuthResponse(peer, in resp);
                    return;
                }
            }

            // 2. GERBANG AUTENTIKASI: tolak/abaikan seluruh paket gameplay jika belum terautentikasi!
            if (!IsGameplayPacketAllowed(session))
            {
                Console.WriteLine($"[AuthGate] Menolak paket gameplay {packetType} dari Peer {peer.Id} yang belum login.");
                return;
            }

            switch (packetType)
            {
                case PacketType.JoinWorldRequest:
                    HandleJoinWorldRequest(session, PacketSerializer.ReadJoinWorldRequest(reader));
                    break;

                case PacketType.PlayerState:
                    RouteToCurrentWorld(session, new PlayerStateCommand(peer.Id, PacketSerializer.ReadPlayerState(reader)));
                    break;

                case PacketType.TileAction:
                    RouteToCurrentWorld(session, new TileActionCommand(peer.Id, PacketSerializer.ReadTileAction(reader)));
                    break;

                case PacketType.DropItemRequest:
                    RouteToCurrentWorld(session, new DropItemRequestCommand(peer.Id, PacketSerializer.ReadDropItemRequest(reader)));
                    break;

                case PacketType.WrenchTileRequest:
                    RouteToCurrentWorld(session, new WrenchTileCommand(peer.Id, PacketSerializer.ReadWrenchTileRequest(reader)));
                    break;

                case PacketType.WorldLockConfigRequest:
                    RouteToCurrentWorld(session, new WorldLockConfigCommand(peer.Id, PacketSerializer.ReadWorldLockConfigRequest(reader)));
                    break;

                case PacketType.TradeRequest:
                    RouteToCurrentWorld(session, new TradeRequestCommand(peer.Id, PacketSerializer.ReadTradeRequest(reader)));
                    break;

                case PacketType.TradeInviteResponse:
                    RouteToCurrentWorld(session, new TradeInviteResponseCommand(peer.Id, PacketSerializer.ReadTradeInviteResponse(reader)));
                    break;

                case PacketType.TradeUpdateOffer:
                    RouteToCurrentWorld(session, new TradeUpdateOfferCommand(peer.Id, PacketSerializer.ReadTradeUpdateOffer(reader)));
                    break;

                case PacketType.TradeAction:
                    RouteToCurrentWorld(session, new TradeActionCommand(peer.Id, PacketSerializer.ReadTradeAction(reader)));
                    break;

                case PacketType.ChatMessage:
                    HandleIncomingChat(session, PacketSerializer.ReadChatMessage(reader));
                    break;

                default:
                    Console.WriteLine($"[GameServer] Paket tidak ditangani dari Peer {peer.Id}: {packetType}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GameServer] Paket rusak dari Peer {peer.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gerbang autentikasi: paket gameplay hanya diizinkan jika <see cref="PeerSession.IsAuthenticated"/> bernilai <c>true</c>.
    /// </summary>
    public bool IsGameplayPacketAllowed(PeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.IsAuthenticated && session.AccountId > 0;
    }

    public AuthResponsePacket ProcessRegister(PeerSession session, RegisterRequestPacket request)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.IsAuthenticated)
        {
            return new AuthResponsePacket(false, 0, string.Empty, "Sesi sudah login. Logout terlebih dahulu.", 0, string.Empty);
        }

        if (_authRateLimiter.IsRateLimited(session.RemoteAddress))
        {
            return new AuthResponsePacket(false, 0, string.Empty, "Terlalu banyak percobaan gagal. Coba lagi dalam 30 detik.", 0, string.Empty);
        }

        // Starter Kit awal untuk akun baru: 2 World Lock, 30 Dirt, 15 Wood, 10 Gems, dll.
        byte[] starterInventoryBlob = AccountInventoryCodec.Encode(PlayerSession.CreateStarterInventory());
        AccountAuthResult result = _accountRepository.Register(
            request.Username,
            request.Password,
            initialInventoryBlob: starterInventoryBlob,
            initialGems: PlayerSession.StarterGems);

        if (!result.Success || result.Account is null)
        {
            _authRateLimiter.RecordFailure(session.RemoteAddress);
            return new AuthResponsePacket(false, 0, string.Empty, result.ErrorMessage, 0, string.Empty);
        }

        return ActivateAuthenticatedSession(session, result.Account, actionLabel: "Register");
    }

    public AuthResponsePacket ProcessLogin(PeerSession session, LoginRequestPacket request)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (session.IsAuthenticated)
        {
            return new AuthResponsePacket(false, 0, string.Empty, "Sesi sudah login. Logout terlebih dahulu.", 0, string.Empty);
        }

        if (_authRateLimiter.IsRateLimited(session.RemoteAddress))
        {
            return new AuthResponsePacket(false, 0, string.Empty, "Terlalu banyak percobaan gagal. Coba lagi dalam 30 detik.", 0, string.Empty);
        }

        AccountAuthResult result = _accountRepository.Authenticate(request.Username, request.Password);
        if (!result.Success || result.Account is null)
        {
            _authRateLimiter.RecordFailure(session.RemoteAddress);
            return new AuthResponsePacket(false, 0, string.Empty, result.ErrorMessage, 0, string.Empty);
        }

        return ActivateAuthenticatedSession(session, result.Account, actionLabel: "Login");
    }

    public AuthResponsePacket ProcessLogout(PeerSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (!session.IsAuthenticated)
        {
            return new AuthResponsePacket(true, 0, string.Empty, "Tidak ada sesi aktif.", 0, string.Empty);
        }

        string lastWorld = session.LastWorld;
        EndAuthenticatedSession(session, "Logged out");
        return new AuthResponsePacket(true, 0, string.Empty, string.Empty, 0, lastWorld);
    }

    /// <summary>
    /// Mensimulasikan disconnect peer secara bersih (termasuk menyimpan profil terlebih dahulu sebelum menghapus AccountId aktif).
    /// </summary>
    public void DisconnectSession(PeerSession session, string reason = "Disconnected")
    {
        ArgumentNullException.ThrowIfNull(session);
        _peerSessions.TryRemove(session.PeerId, out _);
        EndAuthenticatedSession(session, reason);
    }

    /// <summary>
    /// Memasukkan sesi terautentikasi ke world tertentu secara terpadu (dapat dipakai juga oleh unit test tanpa soket UDP).
    /// </summary>
    public WorldInstance? JoinSessionToWorld(PeerSession session, string? worldName)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!IsGameplayPacketAllowed(session))
        {
            return null;
        }

        string targetWorldName = WorldManager.NormalizeWorldName(
            string.IsNullOrWhiteSpace(worldName) ? session.LastWorld : worldName);

        int accountId = session.AccountId;
        if (!string.IsNullOrWhiteSpace(session.CurrentWorldName) &&
            !string.Equals(session.CurrentWorldName, targetWorldName, StringComparison.OrdinalIgnoreCase) &&
            _worldManager.TryGetWorld(session.CurrentWorldName, out WorldInstance? previousWorld) &&
            previousWorld is not null)
        {
            previousWorld.EnqueueCommand(new LeaveWorldCommand(
                session.PeerId,
                "Switched world",
                OnPlayerLeft: (inventory, gems) =>
                {
                    long version = session.UpdateCachedProfile(inventory, gems, targetWorldName);
                    byte[] invBlob = AccountInventoryCodec.Encode(inventory);
                    _accountRepository.SaveProgress(accountId, gems, invBlob, targetWorldName);
                    session.MarkProfileSaved(version);
                }));
            previousWorld.Tick(0f);
        }

        WorldInstance targetWorld = _worldManager.GetOrCreateWorld(targetWorldName);
        session.CurrentWorldName = targetWorld.Name;
        session.LastWorld = targetWorld.Name;

        Dictionary<ushort, ushort> accountInventory = session.CloneInventory();
        int accountGems = session.Gems;
        _accountRepository.SaveProgress(accountId, accountGems, AccountInventoryCodec.Encode(accountInventory), targetWorld.Name);

        if (session.Peer is not null)
        {
            targetWorld.EnqueueCommand(new JoinWorldCommand(
                session.Peer,
                session.Username,
                NetworkConstants.ProtocolVersion,
                ExistingInventory: accountInventory,
                ExistingGems: accountGems,
                AccountId: accountId));
        }
        else
        {
            targetWorld.EnqueueCommand(new SimulatedJoinPlayerCommand(
                session.PeerId,
                session.Username,
                targetWorld.SpawnX,
                targetWorld.SpawnY,
                Inventory: accountInventory,
                Gems: accountGems,
                FacingLeft: false,
                AccountId: accountId));
        }

        return targetWorld;
    }

    private AuthResponsePacket ActivateAuthenticatedSession(PeerSession session, AccountRecord account, string actionLabel)
    {
        // Anti-Double-Login: tolak login kedua jika AccountId masih aktif online di sesi lain.
        if (!_activeAccounts.TryAdd(account.AccountId, session))
        {
            return new AuthResponsePacket(
                Success: false,
                AccountId: 0,
                DisplayName: string.Empty,
                ErrorMessage: "Akun sedang digunakan di perangkat lain",
                Gems: 0,
                LastWorld: string.Empty);
        }

        _authRateLimiter.Reset(session.RemoteAddress);
        _peerSessions[session.PeerId] = session;

        session.IsAuthenticated = true;
        session.AccountId = account.AccountId;
        session.Username = account.Username;
        session.Gems = account.Gems;
        session.InventoryBlob = account.InventoryBlob;
        session.LastWorld = WorldManager.NormalizeWorldName(account.LastWorld);
        session.CurrentWorldName = string.Empty;
        session.LoadAccountProfile(account);

        // Jangan pernah menulis password ke log; hanya catat AccountId dan Username.
        Console.WriteLine($"[Auth] {actionLabel} sukses: AccountId={account.AccountId}, Username='{account.Username}', Peer={session.PeerId}.");

        return new AuthResponsePacket(
            Success: true,
            AccountId: account.AccountId,
            DisplayName: account.Username,
            ErrorMessage: string.Empty,
            Gems: session.Gems,
            LastWorld: session.LastWorld);
    }

    /// <summary>
    /// Hook dari <see cref="WorldInstance"/> setiap kali inventory/gems pemain berubah:
    /// menandai <c>IsProfileDirty = true</c>, dan langsung menyimpan ke SQLite bila transaksi bernilai tinggi (World Lock).
    /// </summary>
    private void OnPlayerProfileChanged(
        int accountId,
        Dictionary<ushort, ushort> inventoryCopy,
        int gems,
        string worldName,
        bool immediatePersist)
    {
        if (!_activeAccounts.TryGetValue(accountId, out PeerSession? session))
        {
            return;
        }

        long version = session.UpdateCachedProfile(inventoryCopy, gems, worldName);
        if (immediatePersist)
        {
            (int accId, int safeGems, byte[] invBlob, string lastWorld, long capturedVersion) = session.CaptureProfileForSave();
            _accountRepository.SaveProgress(accId, safeGems, invBlob, lastWorld);
            session.MarkProfileSaved(capturedVersion);
        }
        else if (version > 0)
        {
            // Profil akan disimpan secara periodik (30 detik), saat logout/disconnect, atau saat shutdown.
        }
    }

    /// <summary>
    /// Hook dari <see cref="WorldInstance"/> setelah transaksi trade atomik berhasil di-commit ke SQLite:
    /// menyelaraskan cache <see cref="PeerSession"/> di RAM tanpa menandai ulang <c>IsProfileDirty</c>.
    /// </summary>
    private void OnPlayerProfilePersistedAtomically(
        int accountId,
        Dictionary<ushort, ushort> inventoryCopy,
        int gems,
        string worldName)
    {
        if (_activeAccounts.TryGetValue(accountId, out PeerSession? session))
        {
            session.ApplyPersistedProfile(inventoryCopy, gems, worldName);
        }
    }

    /// <summary>
    /// Momen penyimpanan periodik (tiap 30 detik bersama auto-save world):
    /// menyimpan seluruh profil aktif yang memiliki <c>IsProfileDirty == true</c>.
    /// </summary>
    public int SaveDirtyProfiles()
    {
        int savedCount = 0;

        foreach (PeerSession session in _activeAccounts.Values)
        {
            if (!session.IsAuthenticated || !session.IsProfileDirty)
            {
                continue;
            }

            (int accountId, int gems, byte[] invBlob, string lastWorld, long version) = session.CaptureProfileForSave();
            if (accountId <= 0)
            {
                continue;
            }

            _accountRepository.SaveProgress(accountId, gems, invBlob, lastWorld);
            session.MarkProfileSaved(version);
            savedCount++;
        }

        if (savedCount > 0)
        {
            Console.WriteLine($"[Persistence] Auto-save {savedCount} profil pemain dirty ke SQLite.");
        }

        return savedCount;
    }

    /// <summary>
    /// Menyimpan profil akun secara sinkron SEBELUM menghapus <c>AccountId</c> dari registry aktif
    /// untuk mencegah rollback maupun duplikasi item jika akun langsung login kembali.
    /// </summary>
    private void EndAuthenticatedSession(PeerSession session, string reason)
    {
        if (!session.IsAuthenticated)
        {
            return;
        }

        int accountId = session.AccountId;
        string username = session.Username;
        string worldName = session.CurrentWorldName;
        string lastWorld = string.IsNullOrWhiteSpace(worldName) ? session.LastWorld : worldName;
        bool savedViaWorldLeave = false;

        if (!string.IsNullOrWhiteSpace(worldName) &&
            _worldManager.TryGetWorld(worldName, out WorldInstance? currentWorld) &&
            currentWorld is not null)
        {
            currentWorld.EnqueueCommand(new LeaveWorldCommand(
                session.PeerId,
                reason,
                OnPlayerLeft: (inventory, gems) =>
                {
                    long version = session.UpdateCachedProfile(inventory, gems, lastWorld);
                    byte[] invBlob = AccountInventoryCodec.Encode(inventory);
                    _accountRepository.SaveProgress(accountId, gems, invBlob, lastWorld);
                    session.MarkProfileSaved(version);
                    savedViaWorldLeave = true;
                }));
            currentWorld.Tick(0f);
        }

        if (!savedViaWorldLeave)
        {
            (int accId, int gems, byte[] invBlob, string savedWorld, long version) = session.CaptureProfileForSave();
            if (accId > 0)
            {
                _accountRepository.SaveProgress(accId, gems, invBlob, string.IsNullOrWhiteSpace(lastWorld) ? savedWorld : lastWorld);
                session.MarkProfileSaved(version);
            }
        }

        // Hapus dari registry AccountId aktif HANYA SETELAH penyimpanan SQLite selesai!
        session.IsAuthenticated = false;
        session.AccountId = 0;
        session.Username = string.Empty;
        session.CurrentWorldName = string.Empty;
        _activeAccounts.TryRemove(new KeyValuePair<int, PeerSession>(accountId, session));

        Console.WriteLine($"[Auth] Sesi berakhir & profil tersimpan: AccountId={accountId}, Username='{username}' ({reason}).");
    }

    /// <summary>
    /// Flush menyeluruh saat server dimatikan secara graceful (Ctrl+C / ProcessExit):
    /// menyimpan seluruh profil pemain aktif dan seluruh world dirty ke SQLite (idempoten).
    /// </summary>
    public void FlushAllStateOnShutdown()
    {
        if (Interlocked.Exchange(ref _shutdownFlushed, 1) != 0)
        {
            return;
        }

        foreach (PeerSession session in _activeAccounts.Values.ToArray())
        {
            EndAuthenticatedSession(session, "Server shutdown");
        }

        _worldManager.TickAll(0f);
        _worldManager.FlushDirtyWorldsOnShutdown();
    }

    private void SendAuthResponse(NetPeer peer, in AuthResponsePacket response)
    {
        _authWriter.Reset();
        PacketSerializer.WriteAuthResponse(_authWriter, in response);
        peer.Send(_authWriter, DeliveryMethod.ReliableOrdered);
    }

    private void SendPeerInventorySync(PeerSession session)
    {
        if (session.Peer is null || !session.IsAuthenticated)
        {
            return;
        }

        _authWriter.Reset();
        var sync = new InventorySyncPacket(session.ToInventorySlots(), session.Gems);
        PacketSerializer.WriteInventorySync(_authWriter, in sync);
        session.Peer.Send(_authWriter, DeliveryMethod.ReliableOrdered);
    }

    private void HandleIncomingChat(PeerSession session, ChatMessagePacket packet)
    {
        string text = (packet.Message ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        // Tangani perintah pindah/buat world: /join <WORLD> atau /warp <WORLD>
        if (text.StartsWith("/join ", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("/warp ", StringComparison.OrdinalIgnoreCase))
        {
            string targetWorld = text[6..].Trim();
            if (!string.IsNullOrWhiteSpace(targetWorld))
            {
                HandleJoinWorldRequest(session, new JoinWorldRequestPacket(targetWorld, session.Username, NetworkConstants.ProtocolVersion));
                return;
            }
        }

        RouteToCurrentWorld(session, new ChatMessageCommand(session.PeerId, text));
    }

    private void HandleJoinWorldRequest(PeerSession session, JoinWorldRequestPacket request)
    {
        if (session.Peer is null)
        {
            return;
        }

        JoinSessionToWorld(session, request.WorldName);
    }

    private void RouteToCurrentWorld(PeerSession session, WorldCommand command)
    {
        if (!string.IsNullOrWhiteSpace(session.CurrentWorldName) &&
            _worldManager.TryGetWorld(session.CurrentWorldName, out WorldInstance? world) &&
            world is not null)
        {
            world.EnqueueCommand(command);
        }
    }

    public bool Start()
    {
        return _netManager.Start(Port);
    }

    public void Step(float dt)
    {
        _netManager.PollEvents();
        _worldManager.TickAll(dt);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!Start())
        {
            throw new InvalidOperationException($"Gagal mendengarkan UDP port {Port}.");
        }

        double targetStepSeconds = 1.0 / TickRate;
        var stopwatch = Stopwatch.StartNew();
        double previousTime = stopwatch.Elapsed.TotalSeconds;

        using var autoSaveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task autoSaveTask = Task.Run(() => RunAutoSaveLoopAsync(autoSaveCts.Token), CancellationToken.None);

        Console.WriteLine(
            $"[GameServer] Berjalan pada UDP port {Port} ({TickRate} tick/detik, AuthGate aktif, auto-save world & profil tiap {_autoSaveInterval.TotalSeconds:F0} detik).");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                double frameStart = stopwatch.Elapsed.TotalSeconds;
                float dt = (float)(frameStart - previousTime);
                previousTime = frameStart;

                Step(dt);

                double elapsedThisTick = stopwatch.Elapsed.TotalSeconds - frameStart;
                double remainingMs = (targetStepSeconds - elapsedThisTick) * 1000.0;
                if (remainingMs >= 1.0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(remainingMs), cancellationToken);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Penghentian normal.
        }
        finally
        {
            await autoSaveCts.CancelAsync();
            try
            {
                await autoSaveTask;
            }
            catch (OperationCanceledException)
            {
                // Auto-save timer dihentikan normal.
            }

            foreach (PeerSession session in _peerSessions.Values)
            {
                EndAuthenticatedSession(session, "Server shutdown");
            }

            _worldManager.TickAll(0f);
            Stop();
            _worldManager.FlushDirtyWorldsOnShutdown();
            FlushAllStateOnShutdown();
        }
    }

    private async Task RunAutoSaveLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_autoSaveInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await _worldManager.SaveDirtyWorldsAsync(cancellationToken);
                    SaveDirtyProfiles();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[Persistence] Auto-save gagal: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Penghentian normal timer auto-save.
        }
    }

    public void Stop()
    {
        if (_netManager.IsRunning)
        {
            _netManager.Stop();
            Console.WriteLine("[GameServer] Dihentikan.");
        }
    }

    public void Dispose()
    {
        Stop();
        FlushAllStateOnShutdown();
    }
}
