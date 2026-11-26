using System.IO.Compression;
using System.Threading.Channels;
using GrowSandbox.Server.Network;
using GrowSandbox.Server.Persistence;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using LiteNetLib;
using LiteNetLib.Utils;

namespace GrowSandbox.Server.World;

public readonly record struct DirtyWorldSnapshot(
    string WorldName,
    byte[] CompressedData,
    long Version,
    WorldLockData LockData,
    int DropCount = 0,
    int TreeCount = 0);

public abstract record WorldCommand;

public sealed record JoinWorldCommand(
    NetPeer Peer,
    string PlayerName,
    ushort ProtocolVersion,
    Dictionary<ushort, ushort>? ExistingInventory = null,
    int ExistingGems = PlayerSession.StarterGems,
    int AccountId = 0) : WorldCommand;

public sealed record SimulatedJoinPlayerCommand(
    int PlayerId,
    string PlayerName,
    float PositionX,
    float PositionY,
    Dictionary<ushort, ushort>? Inventory = null,
    int Gems = 0,
    bool FacingLeft = false,
    int AccountId = 0) : WorldCommand;

public sealed record LeaveWorldCommand(
    int PlayerId,
    string Reason,
    Action<Dictionary<ushort, ushort>, int>? OnPlayerLeft = null) : WorldCommand;

public sealed record PlayerStateCommand(int PlayerId, PlayerStatePacket State) : WorldCommand;

public sealed record TileActionCommand(int PlayerId, TileActionPacket Action) : WorldCommand;

public sealed record DropItemRequestCommand(int PlayerId, DropItemRequestPacket Request) : WorldCommand;

public sealed record WrenchTileCommand(
    int PlayerId,
    WrenchTileRequestPacket Request,
    Action<bool>? OnLockConfigModalDecision = null) : WorldCommand;

public sealed record WorldLockConfigCommand(int PlayerId, WorldLockConfigRequestPacket Request) : WorldCommand;

public sealed record TradeRequestCommand(int PlayerId, TradeRequestPacket Request) : WorldCommand;

public sealed record TradeInviteResponseCommand(int PlayerId, TradeInviteResponsePacket Response) : WorldCommand;

public sealed record TradeUpdateOfferCommand(int PlayerId, TradeUpdateOfferPacket Update) : WorldCommand;

public sealed record TradeActionCommand(int PlayerId, TradeActionPacket Action) : WorldCommand;

public sealed record SimulatedSpawnDropCommand(
    ushort ItemId,
    ushort Count,
    float X,
    float Y,
    int DropperPlayerId = -1) : WorldCommand;

public sealed record SimulatedTileActionCommand(
    float PlayerX,
    float PlayerY,
    TileActionPacket Action,
    string? ActorName = null) : WorldCommand;

public sealed record SetTileCommand(int X, int Y, Tile Tile) : WorldCommand;

public sealed record ChatMessageCommand(int PlayerId, string Message) : WorldCommand;

public sealed record CaptureDirtySnapshotCommand(TaskCompletionSource<DirtyWorldSnapshot?> Completion) : WorldCommand;

public sealed record MarkWorldSavedCommand(long SavedVersion) : WorldCommand;

public sealed class PlayerSession
{
    public const int StarterGems = 10;

    public int PlayerId { get; }
    public int AccountId { get; }
    public NetPeer? Peer { get; }
    public string Name { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float VelocityX { get; set; }
    public float VelocityY { get; set; }
    public bool IsGrounded { get; set; }
    public bool FacingLeft { get; set; }
    public bool IsProfileDirty { get; set; }
    public double LastStateReceivedSeconds { get; set; }
    public double LastLockDenyToastSeconds { get; set; } = -10.0;
    public uint LastStateTick { get; set; }
    public Dictionary<ushort, ushort> Inventory { get; }
    public int Gems { get; set; }

    public PlayerSession(
        int playerId,
        NetPeer? peer,
        string name,
        float spawnX,
        float spawnY,
        double joinedAtSeconds,
        Dictionary<ushort, ushort> inventory,
        int gems = StarterGems,
        bool facingLeft = false,
        int accountId = 0)
    {
        PlayerId = playerId;
        AccountId = accountId;
        Peer = peer;
        Name = name;
        PositionX = spawnX;
        PositionY = spawnY;
        IsGrounded = true;
        FacingLeft = facingLeft;
        LastStateReceivedSeconds = joinedAtSeconds;
        Inventory = SanitizeInventory(inventory, ref gems);
        Gems = Math.Clamp(gems, 0, ItemCatalog.MaxGemBalance);
    }

    private static Dictionary<ushort, ushort> SanitizeInventory(Dictionary<ushort, ushort> source, ref int gems)
    {
        var sanitized = new Dictionary<ushort, ushort>(source.Count);
        foreach ((ushort itemId, ushort count) in source)
        {
            if (count == 0 || itemId is TileIds.Air or TileIds.Fist or TileIds.Wrench)
            {
                continue;
            }

            if (itemId == TileIds.Gem)
            {
                gems = Math.Min(ItemCatalog.MaxGemBalance, gems + count);
                continue;
            }

            sanitized[itemId] = Math.Min(count, ItemCatalog.MaxInventoryStack);
        }

        return sanitized;
    }

    public static Dictionary<ushort, ushort> CreateStarterInventory() => new()
    {
        [TileIds.WorldLock] = 2,
        [TileIds.Dirt] = 30,
        [TileIds.Wood] = 15,
        [TileIds.Rock] = 15,
        [TileIds.Brick] = 15,
        [TileIds.Glass] = 10,
        [TileIds.WoodBackground] = 15,
        [TileIds.Torch] = 10,
        [TileIds.DirtSeed] = 5,
        [TileIds.WoodSeed] = 5,
        [TileIds.RockSeed] = 3,
        [TileIds.BrickSeed] = 2,
        [TileIds.GlassSeed] = 2,
    };

    public static InventorySlot[] BuildInventorySlots(IReadOnlyDictionary<ushort, ushort> inventory)
    {
        // Urutkan slot agar susunan hotbar & laci inventory rapi dan stabil.
        ushort[] preferredOrder =
        [
            TileIds.Dirt,
            TileIds.Wood,
            TileIds.Rock,
            TileIds.Brick,
            TileIds.Glass,
            TileIds.DirtSeed,
            TileIds.WoodSeed,
            TileIds.WorldLock,
            TileIds.RockSeed,
            TileIds.BrickSeed,
            TileIds.GlassSeed,
            TileIds.WoodBackground,
            TileIds.Torch,
            TileIds.DirtBackground,
        ];

        var slots = new List<InventorySlot>(inventory.Count);
        var added = new HashSet<ushort>();

        foreach (ushort id in preferredOrder)
        {
            if (inventory.TryGetValue(id, out ushort count) && count > 0)
            {
                slots.Add(new InventorySlot(id, count));
                added.Add(id);
            }
        }

        foreach ((ushort id, ushort count) in inventory)
        {
            if (count > 0 && id is not (TileIds.Air or TileIds.Gem or TileIds.Fist or TileIds.Wrench) && added.Add(id))
            {
                slots.Add(new InventorySlot(id, count));
            }
        }

        return slots.ToArray();
    }

    public InventorySlot[] ToInventorySlots() => BuildInventorySlots(Inventory);
}

/// <summary>
/// Satu instansi dunia otoritatif berukuran 100x60.
/// Seluruh mutasi tile, pemain, item drop anti-duplikasi, sistem Seed &amp; Farming berbasis UTC,
/// World Lock penuh, dan chat diantrekan lewat <see cref="Channel{T}"/> dan dieksekusi serial di dalam <see cref="Tick(float)"/>.
/// </summary>
public sealed class WorldInstance
{
    public const int SystemSenderId = -1;

    private const float PositionToleranceMarginPx = 6f;
    private const float MaxValidationWindowSeconds = 0.5f;
    private const double ToastThrottleCooldownSeconds = 1.0;

    private readonly Tile[] _tiles;
    private readonly Dictionary<int, PlayerSession> _players = new();
    private readonly Dictionary<uint, DroppedItem> _drops = new();
    private readonly Dictionary<uint, (int DropperPlayerId, double CooldownUntilSeconds)> _dropperCooldowns = new();
    private readonly Dictionary<int, TreeData> _trees = new();
    private readonly List<uint> _dropIdScratch = new();
    private readonly List<PlayerSession> _playerScratch = new();
    private readonly List<PendingTradeInvite> _pendingTradeInvites = new();
    private readonly List<TradeSession> _activeTradeSessions = new();
    private readonly Dictionary<int, TradeSession> _activeTradesByPlayerId = new();
    private readonly NetDataWriter _writer = new();
    private readonly WorldLockData _lockData;
    private readonly Func<DateTime> _utcNowProvider;
    private IAccountRepository? _accountRepository;
    private Action<int, Dictionary<ushort, ushort>, int, string, bool>? _onPlayerProfileChanged;
    private Action<int, Dictionary<ushort, ushort>, int, string>? _onPlayerProfilePersistedAtomically;
    private readonly Channel<WorldCommand> _commandChannel = Channel.CreateUnbounded<WorldCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private byte[]? _cachedBrotliTiles;
    private int _playerCount;
    private int _isDirty;
    private long _mutationVersion;
    private uint _nextDropId = 1;

    public string Name { get; }
    public int SpawnTileX { get; }
    public int SpawnTileY { get; }
    public float SpawnX { get; }
    public float SpawnY { get; }
    public ulong TickCount { get; private set; }
    public double ElapsedSeconds { get; private set; }
    public int PlayerCount => Volatile.Read(ref _playerCount);
    public bool IsDirty => Volatile.Read(ref _isDirty) != 0;

    public WorldLockData LockData => _lockData;
    public string LockOwner => _lockData.IsLocked ? _lockData.OwnerName : string.Empty;
    public int LockTileX => _lockData.IsLocked ? _lockData.LockX : -1;
    public int LockTileY => _lockData.IsLocked ? _lockData.LockY : -1;
    public bool IsLocked => _lockData.IsLocked;
    public bool IsPublicLock => _lockData.IsLocked && _lockData.IsPublic;
    public int ActiveDropCount => _drops.Count;
    public int ActiveTreeCount => _trees.Count;
    public int ActiveTradeCount => _activeTradeSessions.Count;
    public int PendingTradeInviteCount => _pendingTradeInvites.Count;
    public uint NextDropId => _nextDropId;

    public event Action<int, TradeInvitePacket>? TradeInviteSent;
    public event Action<int, TradeStateSyncPacket>? TradeStateSynced;
    public event Action<int, TradeEndedPacket>? TradeEndedSent;
    public event Action<int, string, ToastKind>? PlayerToastSent;
    public event Action<int, string>? SystemChatSent;

    private long UtcNowTicks => _utcNowProvider().Ticks;

    public WorldInstance(
        string name,
        Tile[]? initialTiles = null,
        WorldLockData? initialLockData = null,
        IEnumerable<DroppedItem>? initialDrops = null,
        uint initialNextDropId = 1,
        IEnumerable<TreeData>? initialTrees = null,
        Func<DateTime>? utcNowProvider = null,
        IAccountRepository? accountRepository = null,
        Action<int, Dictionary<ushort, ushort>, int, string, bool>? onPlayerProfileChanged = null,
        Action<int, Dictionary<ushort, ushort>, int, string>? onPlayerProfilePersistedAtomically = null)
    {
        Name = name;
        _tiles = initialTiles ?? WorldGenerator.Generate(name);
        _utcNowProvider = utcNowProvider ?? (static () => DateTime.UtcNow);
        _accountRepository = accountRepository;
        _onPlayerProfileChanged = onPlayerProfileChanged;
        _onPlayerProfilePersistedAtomically = onPlayerProfilePersistedAtomically;

        if (_tiles.Length != WorldConstants.TotalTiles)
        {
            throw new ArgumentException(
                $"Array tile wajib berukuran {WorldConstants.TotalTiles} elemen.",
                nameof(initialTiles));
        }

        SpawnTileX = WorldGenerator.SpawnTileX;
        SpawnTileY = WorldGenerator.SpawnTileY;
        (SpawnX, SpawnY) = TileCollision.GetSpawnPosition(SpawnTileX, SpawnTileY);

        _lockData = initialLockData is { IsLocked: true } && !string.IsNullOrWhiteSpace(initialLockData.OwnerName)
            ? initialLockData.Clone()
            : WorldLockData.Unlocked();

        uint maxLoadedDropId = 0;
        if (initialDrops is not null)
        {
            foreach (DroppedItem drop in initialDrops)
            {
                if (drop.DropId == 0 || drop.ItemId == TileIds.Air || drop.Count == 0)
                {
                    continue;
                }

                ushort clampedCount = Math.Min(drop.Count, ItemCatalog.MaxDropStack);
                _drops[drop.DropId] = drop with { Count = clampedCount };
                if (drop.DropId > maxLoadedDropId)
                {
                    maxLoadedDropId = drop.DropId;
                }
            }
        }

        _nextDropId = Math.Max(1u, Math.Max(initialNextDropId, maxLoadedDropId + 1u));

        if (initialTrees is not null)
        {
            foreach (TreeData tree in initialTrees)
            {
                if ((uint)tree.TileIndex >= WorldConstants.TotalTiles || !ItemCatalog.IsSeed(tree.SeedId))
                {
                    continue;
                }

                _trees[tree.TileIndex] = tree;
            }
        }

        ScanInitialWorldState();
    }

    public void ConfigureAccountIntegration(
        IAccountRepository? accountRepository,
        Action<int, Dictionary<ushort, ushort>, int, string, bool>? onPlayerProfileChanged,
        Action<int, Dictionary<ushort, ushort>, int, string>? onPlayerProfilePersistedAtomically = null)
    {
        if (accountRepository is not null)
        {
            _accountRepository = accountRepository;
        }

        if (onPlayerProfileChanged is not null)
        {
            _onPlayerProfileChanged = onPlayerProfileChanged;
        }

        if (onPlayerProfilePersistedAtomically is not null)
        {
            _onPlayerProfilePersistedAtomically = onPlayerProfilePersistedAtomically;
        }
    }

    public bool IsPlayerInActiveTrade(int playerId) => _activeTradesByPlayerId.ContainsKey(playerId);

    public bool TryGetActiveTradeState(int playerId, out TradeStateSyncPacket state)
    {
        if (_activeTradesByPlayerId.TryGetValue(playerId, out TradeSession? session))
        {
            state = session.ToSyncPacket(ElapsedSeconds);
            return true;
        }

        state = default;
        return false;
    }

    public bool HasPendingTradeInvite(int requesterPlayerId, int targetPlayerId)
    {
        for (int i = 0; i < _pendingTradeInvites.Count; i++)
        {
            PendingTradeInvite invite = _pendingTradeInvites[i];
            if (!invite.IsExpired(ElapsedSeconds) &&
                invite.RequesterPlayerId == requesterPlayerId &&
                invite.TargetPlayerId == targetPlayerId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Helper otoritatif hak modifikasi blok di world berbasis <see cref="PeerSession.AccountId"/>:
    /// - Terbuka (!IsLocked) -> true
    /// - Mode Public (IsPublic) -> true (penghancuran tile World Lock tetap khusus OwnerAccountId)
    /// - Pemilik (AccountId == OwnerAccountId) atau anggota AccessList (NormalizedUsername) -> true
    /// </summary>
    public bool CanModifyWorld(PeerSession player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return CanModifyWorld(player.AccountId, player.Username);
    }

    public bool CanModifyWorld(PlayerSession player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return CanModifyWorld(player.AccountId, player.Name);
    }

    public bool CanModifyWorld(string? actorName) => CanModifyWorld(accountId: 0, actorName);

    public bool CanModifyWorld(int accountId, string? actorName)
    {
        if (!_lockData.IsLocked)
        {
            return true;
        }

        if (_lockData.IsPublic)
        {
            return true;
        }

        return _lockData.IsOwner(accountId, actorName) || _lockData.HasAccess(actorName);
    }

    public bool CanConfigureWorldLock(PeerSession player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return _lockData.IsLocked && _lockData.IsOwner(player.AccountId, player.Username);
    }

    public bool CanConfigureWorldLock(PlayerSession player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return _lockData.IsLocked && _lockData.IsOwner(player.AccountId, player.Name);
    }

    private void ScanInitialWorldState()
    {
        bool foundLockTile = false;
        int foundX = -1;
        int foundY = -1;
        var validTreeIndices = new HashSet<int>();

        for (int y = 0; y < WorldConstants.Height; y++)
        {
            int rowOffset = y * WorldConstants.Width;
            for (int x = 0; x < WorldConstants.Width; x++)
            {
                int index = rowOffset + x;
                ref readonly Tile tile = ref _tiles[index];

                if (tile.ForegroundId == TileIds.Tree)
                {
                    validTreeIndices.Add(index);
                    if (!_trees.ContainsKey(index))
                    {
                        ushort fallbackSeed = ItemCatalog.IsSeed(tile.ExtraDataId) ? tile.ExtraDataId : TileIds.WoodSeed;
                        ItemCatalog.TryGetSeedProperties(fallbackSeed, out SeedProperties props);
                        long nowTicks = UtcNowTicks;
                        long readyTicks = nowTicks + TimeSpan.FromSeconds(props.GrowTimeSeconds > 0 ? props.GrowTimeSeconds : 25).Ticks;
                        _trees[index] = new TreeData(index, fallbackSeed, nowTicks, readyTicks, 3);
                    }
                }
                else if (tile.ForegroundId == TileIds.WorldLock)
                {
                    foundLockTile = true;
                    foundX = x;
                    foundY = y;
                }
            }
        }

        // Bersihkan entri TreeData yatim jika tile-nya sudah tidak lagi berupa pohon.
        if (_trees.Count != validTreeIndices.Count)
        {
            var toRemove = new List<int>();
            foreach (int idx in _trees.Keys)
            {
                if (!validTreeIndices.Contains(idx))
                {
                    toRemove.Add(idx);
                }
            }

            for (int i = 0; i < toRemove.Count; i++)
            {
                _trees.Remove(toRemove[i]);
            }
        }

        if (!foundLockTile)
        {
            _lockData.Reset();
        }
        else if (_lockData.IsLocked)
        {
            _lockData.LockX = foundX;
            _lockData.LockY = foundY;
        }
    }

    public bool EnqueueCommand(WorldCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return _commandChannel.Writer.TryWrite(command);
    }

    /// <summary>
    /// Memproses seluruh perintah yang masuk secara serial dan mengecek pungutan item drop
    /// secara atomik anti-duplikasi pada setiap tick dunia.
    /// </summary>
    public void Tick(float dt)
    {
        ElapsedSeconds += dt;

        while (_commandChannel.Reader.TryRead(out WorldCommand? command))
        {
            ProcessCommand(command);
        }

        UpdateTradeTimers();

        if (dt > 0f)
        {
            CheckDropPickups();
        }

        TickCount++;
    }

    public Tile GetTileCopy(int x, int y) => _tiles[Tile.GetIndex(x, y)];

    public WorldLockData GetLockDataSnapshot() => _lockData.Clone();

    public bool TryGetTree(int x, int y, out TreeData tree)
    {
        if (!Tile.IsInBounds(x, y))
        {
            tree = default;
            return false;
        }

        return _trees.TryGetValue(Tile.GetIndex(x, y), out tree);
    }

    public TreeData[] GetTreesSnapshot()
    {
        if (_trees.Count == 0)
        {
            return Array.Empty<TreeData>();
        }

        var snapshot = new TreeData[_trees.Count];
        int i = 0;
        foreach (TreeData tree in _trees.Values)
        {
            snapshot[i++] = tree;
        }

        return snapshot;
    }

    public DroppedItem[] GetDropsSnapshot()
    {
        if (_drops.Count == 0)
        {
            return Array.Empty<DroppedItem>();
        }

        var snapshot = new DroppedItem[_drops.Count];
        int i = 0;
        foreach (DroppedItem drop in _drops.Values)
        {
            snapshot[i++] = drop;
        }

        return snapshot;
    }

    public ushort GetPlayerInventoryCount(int playerId, ushort itemId)
    {
        if (!_players.TryGetValue(playerId, out PlayerSession? session))
        {
            return 0;
        }

        return session.Inventory.TryGetValue(itemId, out ushort count) ? count : (ushort)0;
    }

    public int GetPlayerGems(int playerId) =>
        _players.TryGetValue(playerId, out PlayerSession? session) ? session.Gems : 0;

    public int GetTotalFloorItemCount(ushort itemId)
    {
        int total = 0;
        foreach (DroppedItem drop in _drops.Values)
        {
            if (drop.ItemId == itemId)
            {
                total += drop.Count;
            }
        }

        return total;
    }

    public byte[] SerializeTilesBrotli(CompressionLevel compressionLevel = CompressionLevel.Fastest)
    {
        if (compressionLevel == CompressionLevel.Fastest && _cachedBrotliTiles is not null)
        {
            return _cachedBrotliTiles;
        }

        byte[] compressed = TileCompression.CompressBrotli(_tiles, compressionLevel);
        if (compressionLevel == CompressionLevel.Fastest)
        {
            _cachedBrotliTiles = compressed;
        }

        return compressed;
    }

    public byte[] SerializePersistenceBlobV3() =>
        WorldSnapshotBlobCodec.EncodeV3(SerializeTilesBrotli(), _drops.Values, _nextDropId, _trees.Values);

    public bool TryCaptureDirtySnapshotForShutdown(out DirtyWorldSnapshot snapshot)
    {
        Tick(0f);

        if (!IsDirty)
        {
            snapshot = default;
            return false;
        }

        snapshot = new DirtyWorldSnapshot(
            Name,
            SerializePersistenceBlobV3(),
            _mutationVersion,
            _lockData.Clone(),
            _drops.Count,
            _trees.Count);
        return true;
    }

    public void MarkSavedOnShutdown(long savedVersion)
    {
        if (_mutationVersion == savedVersion)
        {
            Volatile.Write(ref _isDirty, 0);
        }
    }

    private void ProcessCommand(WorldCommand command)
    {
        switch (command)
        {
            case JoinWorldCommand join:
                HandleJoinWorld(join);
                break;

            case SimulatedJoinPlayerCommand simJoin:
                HandleSimulatedJoinPlayer(simJoin);
                break;

            case LeaveWorldCommand leave:
                HandleLeaveWorld(leave);
                break;

            case PlayerStateCommand state:
                HandlePlayerState(state);
                break;

            case TileActionCommand tileAction:
                HandleTileAction(tileAction);
                break;

            case DropItemRequestCommand dropRequest:
                HandleDropItemRequest(dropRequest);
                break;

            case WrenchTileCommand wrench:
                HandleWrenchTileRequest(wrench);
                break;

            case WorldLockConfigCommand lockConfig:
                HandleWorldLockConfig(lockConfig);
                break;

            case TradeRequestCommand tradeRequest:
                HandleTradeRequest(tradeRequest);
                break;

            case TradeInviteResponseCommand tradeInviteResponse:
                HandleTradeInviteResponse(tradeInviteResponse);
                break;

            case TradeUpdateOfferCommand tradeUpdateOffer:
                HandleTradeUpdateOffer(tradeUpdateOffer);
                break;

            case TradeActionCommand tradeAction:
                HandleTradeAction(tradeAction);
                break;

            case SimulatedSpawnDropCommand simDrop:
                SpawnDrop(simDrop.ItemId, simDrop.Count, simDrop.X, simDrop.Y, simDrop.DropperPlayerId);
                break;

            case SimulatedTileActionCommand simulatedAction:
                ApplyTileActionCore(
                    simulatedAction.PlayerX,
                    simulatedAction.PlayerY,
                    simulatedAction.Action,
                    session: null,
                    actorName: simulatedAction.ActorName ?? "Tester");
                break;

            case SetTileCommand setTile:
                HandleSetTile(setTile);
                break;

            case ChatMessageCommand chat:
                HandleChatMessage(chat);
                break;

            case CaptureDirtySnapshotCommand capture:
                HandleCaptureDirtySnapshot(capture);
                break;

            case MarkWorldSavedCommand markSaved:
                HandleMarkWorldSaved(markSaved);
                break;
        }
    }

    private void HandleCaptureDirtySnapshot(CaptureDirtySnapshotCommand command)
    {
        if (!IsDirty)
        {
            command.Completion.TrySetResult(null);
            return;
        }

        byte[] blobV3 = SerializePersistenceBlobV3();
        command.Completion.TrySetResult(
            new DirtyWorldSnapshot(
                Name,
                blobV3,
                _mutationVersion,
                _lockData.Clone(),
                _drops.Count,
                _trees.Count));
    }

    private void HandleMarkWorldSaved(MarkWorldSavedCommand command)
    {
        if (_mutationVersion == command.SavedVersion)
        {
            Volatile.Write(ref _isDirty, 0);
        }
    }

    private void HandleSimulatedJoinPlayer(SimulatedJoinPlayerCommand command)
    {
        string name = string.IsNullOrWhiteSpace(command.PlayerName)
            ? $"SimPlayer_{command.PlayerId}"
            : command.PlayerName.Trim();

        Dictionary<ushort, ushort> inv = command.Inventory is not null
            ? new Dictionary<ushort, ushort>(command.Inventory)
            : new Dictionary<ushort, ushort>();

        var session = new PlayerSession(
            command.PlayerId,
            peer: null,
            name,
            command.PositionX,
            command.PositionY,
            ElapsedSeconds,
            inv,
            gems: command.Gems,
            facingLeft: command.FacingLeft,
            accountId: command.AccountId);

        _players[session.PlayerId] = session;
        Volatile.Write(ref _playerCount, _players.Count);
    }

    private void HandleJoinWorld(JoinWorldCommand command)
    {
        string sanitizedName = string.IsNullOrWhiteSpace(command.PlayerName)
            ? $"Player_{command.Peer.Id}"
            : WorldLockData.SanitizeName(command.PlayerName);

        Dictionary<ushort, ushort> inventory = command.ExistingInventory is not null
            ? new Dictionary<ushort, ushort>(command.ExistingInventory)
            : PlayerSession.CreateStarterInventory();

        var session = new PlayerSession(
            command.Peer.Id,
            command.Peer,
            sanitizedName,
            SpawnX,
            SpawnY,
            ElapsedSeconds,
            inventory,
            gems: command.ExistingGems,
            accountId: command.AccountId);
        _players[session.PlayerId] = session;
        Volatile.Write(ref _playerCount, _players.Count);

        // 1. Kirim WorldSnapshot (Brotli tile + DroppedItem aktif + WorldLockData + TreeData aktif) via ReliableOrdered.
        byte[] compressedTiles = SerializeTilesBrotli();
        _writer.Reset();
        PacketSerializer.WriteWorldSnapshotCompressed(
            _writer,
            Name,
            WorldConstants.Width,
            WorldConstants.Height,
            compressedTiles,
            _drops.Values,
            _lockData,
            _trees.Values);
        command.Peer.Send(_writer, DeliveryMethod.ReliableOrdered);

        // 2. Sinkronkan inventory & saldo gems pemain secara authoritative.
        SendInventorySync(session);

        // 3. Sinkronkan daftar pemain eksisting ke pemain yang baru bergabung.
        foreach (PlayerSession existing in _players.Values)
        {
            if (existing.PlayerId == session.PlayerId)
            {
                continue;
            }

            _writer.Reset();
            var existingJoined = new PlayerJoinedPacket(
                existing.PlayerId,
                existing.Name,
                existing.PositionX,
                existing.PositionY);
            PacketSerializer.WritePlayerJoined(_writer, in existingJoined);
            command.Peer.Send(_writer, DeliveryMethod.ReliableOrdered);
        }

        // 4. Broadcast PlayerJoined ke pemain lain di world yang sama.
        _writer.Reset();
        var joinedPacket = new PlayerJoinedPacket(
            session.PlayerId,
            session.Name,
            session.PositionX,
            session.PositionY);
        PacketSerializer.WritePlayerJoined(_writer, in joinedPacket);
        BroadcastToOthers(session.PlayerId, _writer, DeliveryMethod.ReliableOrdered);

        // 5. Sambutan & info world via chat sistem.
        string lockStatus = _lockData.IsLocked
            ? (_lockData.IsPublic ? $"Public Lock by {_lockData.OwnerName}" : $"Locked by {_lockData.OwnerName}")
            : "Open (unlocked)";
        SendSystemChat(session.Peer, $"Entered {Name} [{lockStatus}]. Type /join <WORLD> to travel, /help for tips.");

        Console.WriteLine(
            $"[World:{Name}] '{session.Name}' (Id={session.PlayerId}) bergabung. Snapshot={compressedTiles.Length} B, Drops={_drops.Count}, Trees={_trees.Count}, Locked={_lockData.IsLocked}. Total pemain={_players.Count}.");
    }

    private void HandleLeaveWorld(LeaveWorldCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? leaving))
        {
            return;
        }

        // Batalkan otomatis sesi trade aktif & undangan tertunda sebelum pemain meninggalkan world.
        CancelTradesAndInvitesForLeavingPlayer(leaving, command.Reason);

        if (!_players.Remove(command.PlayerId, out PlayerSession? removed))
        {
            return;
        }

        Volatile.Write(ref _playerCount, _players.Count);
        command.OnPlayerLeft?.Invoke(new Dictionary<ushort, ushort>(removed.Inventory), removed.Gems);

        _writer.Reset();
        var leftPacket = new PlayerLeftPacket(removed.PlayerId, command.Reason);
        PacketSerializer.WritePlayerLeft(_writer, in leftPacket);
        BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);

        Console.WriteLine(
            $"[World:{Name}] '{removed.Name}' (Id={removed.PlayerId}) keluar ({command.Reason}). Total pemain={_players.Count}.");
    }

    private void HandleChatMessage(ChatMessageCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? session))
        {
            return;
        }

        string text = (command.Message ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > PacketSerializer.MaxChatMessageLength)
        {
            text = text[..PacketSerializer.MaxChatMessageLength];
        }

        if (text.Equals("/help", StringComparison.OrdinalIgnoreCase))
        {
            SendSystemChat(session.Peer, "Commands: /join <WORLD>, /trade <PLAYER>, /world | LMB:Punch/Harvest RMB:Place/Plant Q:Drop 2:Wrench");
            return;
        }

        if (text.StartsWith("/trade ", StringComparison.OrdinalIgnoreCase))
        {
            string targetName = text[7..].Trim();
            if (!string.IsNullOrWhiteSpace(targetName))
            {
                HandleTradeRequest(new TradeRequestCommand(session.PlayerId, new TradeRequestPacket(0, targetName)));
                return;
            }
        }

        if (text.Equals("/world", StringComparison.OrdinalIgnoreCase))
        {
            string lockInfo = _lockData.IsLocked
                ? $"{(_lockData.IsPublic ? "Public Lock" : "Locked")} by {_lockData.OwnerName} at ({_lockData.LockX},{_lockData.LockY}) | Access={_lockData.AccessList.Count}"
                : "Open";
            SendSystemChat(session.Peer, $"World {Name} | Players: {_players.Count} | Drops: {_drops.Count} | Trees: {_trees.Count} | {lockInfo}");
            return;
        }

        _writer.Reset();
        var broadcast = new ChatMessagePacket(session.PlayerId, session.Name, text);
        PacketSerializer.WriteChatMessage(_writer, in broadcast);
        BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void HandlePlayerState(PlayerStateCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? session))
        {
            return;
        }

        PlayerStatePacket incoming = command.State;

        if (!IsFiniteState(in incoming))
        {
            SendAuthoritativeCorrection(session);
            return;
        }

        if (incoming.Tick < session.LastStateTick)
        {
            return;
        }

        float maxX = WorldConstants.WorldPixelWidth - PhysicsConstants.PlayerWidth;
        float maxY = WorldConstants.WorldPixelHeight - PhysicsConstants.PlayerHeight;
        if (incoming.PositionX < 0f || incoming.PositionX > maxX ||
            incoming.PositionY < 0f || incoming.PositionY > maxY)
        {
            SendAuthoritativeCorrection(session);
            return;
        }

        double rawElapsed = ElapsedSeconds - session.LastStateReceivedSeconds;
        float effectiveDt = (float)Math.Clamp(
            rawElapsed,
            PhysicsConstants.ClientStateSendInterval,
            MaxValidationWindowSeconds);

        float maxAllowedDx =
            PhysicsConstants.MoveSpeed * effectiveDt * PhysicsConstants.SpeedValidationTolerance +
            PositionToleranceMarginPx;
        float maxAllowedDy =
            PhysicsConstants.MaxFallSpeed * effectiveDt * PhysicsConstants.SpeedValidationTolerance +
            PositionToleranceMarginPx;

        float dx = MathF.Abs(incoming.PositionX - session.PositionX);
        float dy = MathF.Abs(incoming.PositionY - session.PositionY);

        bool velocityWithinBounds =
            MathF.Abs(incoming.VelocityX) <= PhysicsConstants.MoveSpeed * PhysicsConstants.SpeedValidationTolerance &&
            MathF.Abs(incoming.VelocityY) <= PhysicsConstants.MaxFallSpeed * PhysicsConstants.SpeedValidationTolerance;

        bool embeddedInSolid = TileCollision.OverlapsSolidTile(
            _tiles,
            incoming.PositionX,
            incoming.PositionY,
            PhysicsConstants.PlayerWidth,
            PhysicsConstants.PlayerHeight,
            inset: 1.0f);

        if (dx > maxAllowedDx || dy > maxAllowedDy || !velocityWithinBounds || embeddedInSolid)
        {
            SendAuthoritativeCorrection(session);
            return;
        }

        session.PositionX = incoming.PositionX;
        session.PositionY = incoming.PositionY;
        session.VelocityX = incoming.VelocityX;
        session.VelocityY = incoming.VelocityY;
        session.IsGrounded = incoming.IsGrounded;
        session.FacingLeft = incoming.FacingLeft;
        session.LastStateReceivedSeconds = ElapsedSeconds;
        session.LastStateTick = incoming.Tick;

        var authoritativeState = incoming with { PlayerId = session.PlayerId };
        _writer.Reset();
        PacketSerializer.WritePlayerState(_writer, in authoritativeState);
        BroadcastToOthers(session.PlayerId, _writer, DeliveryMethod.Sequenced);
    }

    private void SendAuthoritativeCorrection(PlayerSession session)
    {
        if (session.Peer is null)
        {
            return;
        }

        var correction = new PlayerStatePacket(
            PlayerId: session.PlayerId,
            Tick: session.LastStateTick,
            PositionX: session.PositionX,
            PositionY: session.PositionY,
            VelocityX: session.VelocityX,
            VelocityY: session.VelocityY,
            IsGrounded: session.IsGrounded,
            FacingLeft: session.FacingLeft);

        _writer.Reset();
        PacketSerializer.WritePlayerState(_writer, in correction);
        session.Peer.Send(_writer, DeliveryMethod.Sequenced);
    }

    private static bool IsFiniteState(in PlayerStatePacket state) =>
        float.IsFinite(state.PositionX) &&
        float.IsFinite(state.PositionY) &&
        float.IsFinite(state.VelocityX) &&
        float.IsFinite(state.VelocityY);

    private void HandleTileAction(TileActionCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? session))
        {
            return;
        }

        ApplyTileActionCore(session.PositionX, session.PositionY, command.Action, session, session.Name);
    }

    /// <summary>
    /// Menangani klik Wrench pada tile:
    /// - Pada World Lock milik pemain: kirim <see cref="WorldLockStatePacket"/> dengan <c>OpenConfigModal = true</c>.
    /// - Pada Pohon (<see cref="TileIds.Tree"/>): kirim toast status &amp; hitung mundur waktu matang UTC.
    /// </summary>
    private void HandleWrenchTileRequest(WrenchTileCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? session))
        {
            return;
        }

        int tx = command.Request.TileX;
        int ty = command.Request.TileY;
        if (!TileCollision.IsWithinReach(session.PositionX, session.PositionY, tx, ty))
        {
            return;
        }

        int index = Tile.GetIndex(tx, ty);
        Tile tile = _tiles[index];

        if (tile.ForegroundId == TileIds.WorldLock)
        {
            if (_lockData.IsLocked && _lockData.IsOwner(session.AccountId, session.Name))
            {
                command.OnLockConfigModalDecision?.Invoke(true);
                SendWorldLockStateToPeer(session.Peer, openConfigModal: true);
            }
            else if (_lockData.IsLocked)
            {
                command.OnLockConfigModalDecision?.Invoke(false);
                SendThrottledToast(
                    session,
                    $"World Lock ini milik {_lockData.OwnerName}{(_lockData.IsPublic ? " (Public Build)" : "")}.",
                    ToastKind.Warning);
            }

            return;
        }

        if (tile.ForegroundId == TileIds.Tree && _trees.TryGetValue(index, out TreeData tree))
        {
            string treeName = ItemCatalog.TryGetSeedProperties(tree.SeedId, out SeedProperties props)
                ? props.TreeName
                : "Tree";
            long nowTicks = UtcNowTicks;
            if (tree.IsReady(nowTicks))
            {
                SendToast(session.Peer, $"{treeName}: READY! Pukul untuk panen ({tree.FruitCount} buah).", ToastKind.Success);
            }
            else
            {
                SendToast(session.Peer, $"{treeName}: {FormatRemainingTime(tree.GetRemainingSeconds(nowTicks))} tersisa.", ToastKind.Info);
            }
        }
    }

    /// <summary>
    /// Validasi otoritatif konfigurasi World Lock: HANYA <c>OwnerName</c> yang diizinkan
    /// menambah/mencabut akses, mengubah mode Public, atau mengambil kembali World Lock.
    /// </summary>
    private void HandleWorldLockConfig(WorldLockConfigCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? session))
        {
            return;
        }

        if (!_lockData.IsLocked)
        {
            SendThrottledToast(session, "World ini tidak memiliki World Lock aktif.", ToastKind.Warning);
            return;
        }

        if (!_lockData.IsOwner(session.AccountId, session.Name))
        {
            SendThrottledToast(
                session,
                $"Hanya {_lockData.OwnerName} yang dapat mengatur World Lock ini!",
                ToastKind.Warning);
            return;
        }

        switch (command.Request.Action)
        {
            case WorldLockConfigAction.TogglePublic:
                _lockData.IsPublic = !_lockData.IsPublic;
                MarkMetadataDirty();
                BroadcastWorldLockState();
                string modeLabel = _lockData.IsPublic ? "PUBLIC BUILD AKTIF" : "MODE PRIVATE (KHUSUS AKSES)";
                BroadcastSystemChat($"World Lock {Name}: {modeLabel} (diubah oleh {session.Name}).");
                BroadcastToast($"World Lock: {modeLabel}!", ToastKind.Info);
                break;

            case WorldLockConfigAction.AddAccess:
            {
                string sanitized = WorldLockData.SanitizeName(command.Request.TargetPlayerName);
                if (string.IsNullOrEmpty(sanitized))
                {
                    SendToast(session.Peer, "Masukkan nama pemain terlebih dahulu.", ToastKind.Warning);
                    return;
                }

                string targetToAdd = sanitized;
                string displayAdded = sanitized;

                // Jika AccountRepository tersedia, validasi bahwa target benar-benar akun terdaftar di SQLite!
                if (_accountRepository is not null)
                {
                    AccountRecord? targetAccount = _accountRepository.GetByUsername(sanitized);
                    if (targetAccount is null)
                    {
                        SendToast(session.Peer, "Pemain tidak ditemukan", ToastKind.Warning);
                        return;
                    }

                    if (_lockData.OwnerAccountId > 0 && targetAccount.AccountId == _lockData.OwnerAccountId)
                    {
                        SendToast(session.Peer, "Pemilik world otomatis memiliki akses penuh.", ToastKind.Info);
                        return;
                    }

                    targetToAdd = targetAccount.NormalizedUsername;
                    displayAdded = targetAccount.Username;
                }
                else if (string.Equals(_lockData.OwnerName, sanitized, StringComparison.OrdinalIgnoreCase))
                {
                    SendToast(session.Peer, "Pemilik world otomatis memiliki akses penuh.", ToastKind.Info);
                    return;
                }

                if (_lockData.HasAccess(targetToAdd))
                {
                    SendToast(session.Peer, $"{displayAdded} sudah berada di daftar akses.", ToastKind.Info);
                    return;
                }

                if (_lockData.AccessList.Count >= WorldLockData.MaxAccessListCount)
                {
                    SendToast(session.Peer, $"Daftar akses penuh (maks {WorldLockData.MaxAccessListCount} pemain).", ToastKind.Warning);
                    return;
                }

                if (_lockData.TryAddAccess(targetToAdd, out _))
                {
                    MarkMetadataDirty();
                    BroadcastWorldLockState();
                    SendToast(session.Peer, $"{displayAdded} ditambahkan ke daftar akses!", ToastKind.Success);
                    SendSystemChat(session.Peer, $"Granted build access in {Name} to {displayAdded}.");
                }

                break;
            }

            case WorldLockConfigAction.RemoveAccess:
            {
                string sanitized = WorldLockData.SanitizeName(command.Request.TargetPlayerName);
                if (_lockData.TryRemoveAccess(sanitized))
                {
                    MarkMetadataDirty();
                    BroadcastWorldLockState();
                    SendToast(session.Peer, $"Akses {sanitized} dicabut.", ToastKind.Info);
                    SendSystemChat(session.Peer, $"Removed build access in {Name} for {sanitized}.");
                }

                break;
            }

            case WorldLockConfigAction.PickUpLock:
            {
                if (IsPlayerInActiveTrade(session.PlayerId))
                {
                    SendThrottledToast(session, "Selesaikan atau batalkan trade terlebih dahulu!", ToastKind.Warning);
                    return;
                }

                int lx = _lockData.LockX;
                int ly = _lockData.LockY;

                if (Tile.IsInBounds(lx, ly))
                {
                    int lockIndex = Tile.GetIndex(lx, ly);
                    Tile lockTile = _tiles[lockIndex];
                    if (lockTile.ForegroundId == TileIds.WorldLock)
                    {
                        lockTile.ForegroundId = TileIds.Air;
                        lockTile.Damage = 0;
                        lockTile.ExtraDataId = 0;
                        lockTile.Flags &= ~TileFlags.Locked;
                        ApplyTileMutation(lx, ly, lockIndex, lockTile);
                    }
                }

                // Reset lock & kembalikan tepat 1 World Lock ke pemilik (inventory jika muat, atau drop lantai).
                _lockData.Reset();
                MarkMetadataDirty();

                session.Inventory.TryGetValue(TileIds.WorldLock, out ushort currentCount);
                if (currentCount < ItemCatalog.MaxInventoryStack)
                {
                    session.Inventory[TileIds.WorldLock] = (ushort)(currentCount + 1);
                    SendInventorySync(session);
                    NotifyPlayerProfileChanged(session, immediatePersist: true);
                }
                else
                {
                    float dropX = Tile.IsInBounds(lx, ly)
                        ? (lx + 0.5f) * WorldConstants.TileSize
                        : session.PositionX + PhysicsConstants.PlayerWidth * 0.5f;
                    float dropY = Tile.IsInBounds(lx, ly)
                        ? (ly + 0.5f) * WorldConstants.TileSize
                        : session.PositionY + PhysicsConstants.PlayerHeight * 0.5f;
                    SpawnDrop(TileIds.WorldLock, 1, dropX, dropY);
                    NotifyPlayerProfileChanged(session, immediatePersist: true);
                }

                BroadcastWorldLockState();
                BroadcastSystemChat($"{session.Name} picked up the World Lock. {Name} is now OPEN!");
                BroadcastToast($"World Lock diambil kembali oleh {session.Name}!", ToastKind.Info);
                break;
            }
        }
    }

    /// <summary>
    /// Menangani permintaan buang item dari klien secara authoritative &amp; anti-duplikasi:
    /// tolak Fist/Wrench/item non-droppable dan jumlah &lt;= 0, potong inventory TERLEBIH DAHULU,
    /// lalu spawn DroppedItem 1 tile di depan arah hadap pemain dengan cooldown 0.45 detik.
    /// </summary>
    private void HandleDropItemRequest(DropItemRequestCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? session))
        {
            return;
        }

        if (IsPlayerInActiveTrade(session.PlayerId))
        {
            SendThrottledToast(session, "Tidak dapat membuang item saat sedang trade!", ToastKind.Warning);
            return;
        }

        ushort itemId = command.Request.ItemId;
        int requestedCount = command.Request.Count;

        if (requestedCount <= 0 || requestedCount > ItemCatalog.MaxDropStack || !ItemCatalog.IsDroppable(itemId))
        {
            return;
        }

        ushort dropCount = (ushort)requestedCount;

        // Eksekusi atomik: kurangi stok inventory TERLEBIH DAHULU sebelum memunculkan objek di lantai.
        if (!TryConsumeInventoryItem(session, itemId, dropCount))
        {
            return;
        }

        float centerX = session.PositionX + PhysicsConstants.PlayerWidth * 0.5f;
        float centerY = session.PositionY + PhysicsConstants.PlayerHeight * 0.5f;
        float dirX = session.FacingLeft ? -1f : 1f;
        float targetX = Math.Clamp(centerX + dirX * WorldConstants.TileSize, 12f, WorldConstants.WorldPixelWidth - 12f);
        float targetY = Math.Clamp(centerY, 12f, WorldConstants.WorldPixelHeight - 12f);

        // Jika 1 tile di depan pemain adalah blok solid, jatuhkan tepat di posisi pemain agar tidak tembus dinding.
        int targetTileX = Math.Clamp((int)MathF.Floor(targetX / WorldConstants.TileSize), 0, WorldConstants.Width - 1);
        int targetTileY = Math.Clamp((int)MathF.Floor(targetY / WorldConstants.TileSize), 0, WorldConstants.Height - 1);
        if (TileCollision.IsSolidTile(in _tiles[Tile.GetIndex(targetTileX, targetTileY)]))
        {
            targetX = centerX;
            targetY = centerY;
        }

        SpawnDrop(itemId, dropCount, targetX, targetY, dropperPlayerId: session.PlayerId);
    }

    private void ApplyTileActionCore(
        float playerX,
        float playerY,
        TileActionPacket action,
        PlayerSession? session,
        string actorName)
    {
        int tx = action.TileX;
        int ty = action.TileY;

        if (!TileCollision.IsWithinReach(playerX, playerY, tx, ty))
        {
            return;
        }

        // 1. Validasi World Lock: cek CanModifyWorld (Owner, AccessList case-insensitive, atau Public).
        bool allowed = session is not null ? CanModifyWorld(session) : CanModifyWorld(actorName);
        if (!allowed)
        {
            if (session is not null)
            {
                SendThrottledToast(session, $"Area ini terkunci oleh {_lockData.OwnerName}!", ToastKind.Warning);
            }

            return;
        }

        int index = Tile.GetIndex(tx, ty);
        Tile current = _tiles[index];
        Tile updated = current;

        switch (action.Action)
        {
            case TileActionKind.Punch:
                if (current.ForegroundId == TileIds.Bedrock ||
                    current.ForegroundId == TileIds.Spawn ||
                    current.ForegroundId == TileIds.WhiteDoor ||
                    (current.Flags.HasFlag(TileFlags.Locked) && current.ForegroundId != TileIds.WorldLock))
                {
                    return;
                }

                // Blok World Lock HANYA boleh dihancurkan oleh OwnerAccountId (meskipun IsPublic=true atau masuk AccessList).
                if (current.ForegroundId == TileIds.WorldLock && !_lockData.IsOwner(session?.AccountId ?? 0, actorName))
                {
                    if (session is not null)
                    {
                        SendThrottledToast(
                            session,
                            $"Hanya {_lockData.OwnerName} yang dapat menghancurkan World Lock!",
                            ToastKind.Warning);
                    }

                    return;
                }

                // Memanen Pohon berbasis UTC timestamp:
                // - Jika sudah matang (UtcNowTicks >= ReadyUtcTicks) -> hapus pohon, spawn buah + peluang seed + gem.
                // - Jika BELUM matang -> kirim notifikasi sisa waktu tumbuh tanpa menghancurkan pohon.
                if (current.ForegroundId == TileIds.Tree)
                {
                    HandlePunchTree(tx, ty, index, current, session);
                    return;
                }

                if (updated.ForegroundId != TileIds.Air)
                {
                    ushort targetFg = updated.ForegroundId;
                    byte breakHits = ItemCatalog.GetBreakHits(targetFg);
                    updated.Damage++;

                    if (updated.Damage >= breakHits)
                    {
                        updated.ForegroundId = TileIds.Air;
                        updated.Damage = 0;
                        updated.ExtraDataId = 0;
                        updated.Flags &= ~TileFlags.Locked;

                        ApplyTileMutation(tx, ty, index, updated);

                        if (targetFg == TileIds.WorldLock)
                        {
                            _lockData.Reset();
                            MarkMetadataDirty();
                            BroadcastWorldLockState();
                            BroadcastSystemChat($"{actorName} broke the World Lock. {Name} is now OPEN!");
                            BroadcastToast($"World {Name} dibuka oleh {actorName}!", ToastKind.Info);
                        }

                        // Jangan langsung tambah ke inventory; spawn DroppedItem (blok + RNG seed/gem).
                        SpawnDropsForBrokenTile(tx, ty, targetFg);
                        return;
                    }
                }
                else if (updated.BackgroundId != TileIds.Air)
                {
                    ushort targetBg = updated.BackgroundId;
                    byte breakHits = ItemCatalog.GetBreakHits(targetBg);
                    updated.Damage++;

                    if (updated.Damage >= breakHits)
                    {
                        updated.BackgroundId = TileIds.Air;
                        updated.Damage = 0;
                        ApplyTileMutation(tx, ty, index, updated);
                        SpawnDropsForBrokenTile(tx, ty, targetBg);
                        return;
                    }
                }
                break;

            case TileActionKind.PlaceForeground:
                if (session is not null && IsPlayerInActiveTrade(session.PlayerId))
                {
                    SendThrottledToast(session, "Tidak dapat memasang blok saat sedang trade!", ToastKind.Warning);
                    return;
                }

                if (current.Flags.HasFlag(TileFlags.Locked) ||
                    updated.ForegroundId != TileIds.Air ||
                    !ItemCatalog.IsPlaceable(action.ItemId) ||
                    ItemCatalog.IsBackgroundItem(action.ItemId))
                {
                    return;
                }

                // Menanam Seed (blok farmable): butuh lantai solid di (x, y + 1), non-solid tile Tree, simpan TreeData UTC.
                if (ItemCatalog.TryGetSeedProperties(action.ItemId, out SeedProperties seedProps))
                {
                    if (ty + 1 >= WorldConstants.Height ||
                        !TileCollision.IsSolidTile(in _tiles[Tile.GetIndex(tx, ty + 1)]))
                    {
                        if (session is not null)
                        {
                            SendThrottledToast(session, "Seed harus ditanam di atas blok lantai yang solid!", ToastKind.Warning);
                        }

                        return;
                    }

                    if (session is not null && !TryConsumeInventoryItem(session, seedProps.SeedId, 1))
                    {
                        return;
                    }

                    updated.ForegroundId = TileIds.Tree;
                    updated.Damage = 0;
                    updated.ExtraDataId = seedProps.SeedId;

                    long plantedTicks = UtcNowTicks;
                    long readyTicks = plantedTicks + TimeSpan.FromSeconds(seedProps.GrowTimeSeconds).Ticks;
                    byte fruitCount = (byte)Random.Shared.Next(seedProps.HarvestMinFruit, seedProps.HarvestMaxFruit + 1);
                    var tree = new TreeData(index, seedProps.SeedId, plantedTicks, readyTicks, fruitCount);
                    _trees[index] = tree;

                    ApplyTileMutation(tx, ty, index, updated);
                    BroadcastTreeUpdate(index, isRemoved: false, in tree);
                    return;
                }

                if (action.ItemId == TileIds.WorldLock)
                {
                    // Hanya boleh 1 World Lock aktif per world.
                    if (_lockData.IsLocked)
                    {
                        if (session is not null)
                        {
                            SendThrottledToast(
                                session,
                                $"World {Name} sudah dikunci oleh {_lockData.OwnerName}!",
                                ToastKind.Warning);
                        }

                        return;
                    }

                    // Tolak jika dipasang di atas WhiteDoor / Spawn / Bedrock.
                    if (ty + 1 < WorldConstants.Height)
                    {
                        ushort belowFg = _tiles[Tile.GetIndex(tx, ty + 1)].ForegroundId;
                        if (belowFg is TileIds.WhiteDoor or TileIds.Spawn or TileIds.Bedrock)
                        {
                            if (session is not null)
                            {
                                SendThrottledToast(
                                    session,
                                    "Tidak bisa memasang World Lock di atas White Door / Bedrock!",
                                    ToastKind.Warning);
                            }

                            return;
                        }
                    }
                }

                if (ItemCatalog.IsSolid(action.ItemId) && IntersectsAnyPlayer(tx, ty, playerX, playerY))
                {
                    return;
                }

                if (session is not null && !TryConsumeInventoryItem(session, action.ItemId, 1))
                {
                    return;
                }

                updated.ForegroundId = action.ItemId;
                updated.Damage = 0;
                updated.ExtraDataId = 0;

                if (action.ItemId == TileIds.WorldLock)
                {
                    updated.Flags |= TileFlags.Locked;
                    _lockData.Reset();
                    _lockData.IsLocked = true;
                    _lockData.OwnerAccountId = session?.AccountId ?? 0;
                    _lockData.OwnerName = WorldLockData.SanitizeName(actorName);
                    _lockData.LockX = tx;
                    _lockData.LockY = ty;
                    _lockData.IsPublic = false;
                }

                ApplyTileMutation(tx, ty, index, updated);

                if (action.ItemId == TileIds.WorldLock)
                {
                    if (session is not null)
                    {
                        // Transaksi bernilai tinggi (pasang World Lock): simpan profil akun segera!
                        NotifyPlayerProfileChanged(session, immediatePersist: true);
                    }

                    BroadcastWorldLockState();
                    BroadcastSystemChat($"{_lockData.OwnerName} placed a World Lock and claimed {Name}!");
                    BroadcastToast($"World locked by {_lockData.OwnerName}!", ToastKind.Success);
                }

                return;

            case TileActionKind.PlaceBackground:
                if (session is not null && IsPlayerInActiveTrade(session.PlayerId))
                {
                    SendThrottledToast(session, "Tidak dapat memasang blok saat sedang trade!", ToastKind.Warning);
                    return;
                }

                if (current.Flags.HasFlag(TileFlags.Locked) ||
                    updated.BackgroundId != TileIds.Air ||
                    !ItemCatalog.IsPlaceable(action.ItemId) ||
                    !ItemCatalog.IsBackgroundItem(action.ItemId))
                {
                    return;
                }

                if (session is not null && !TryConsumeInventoryItem(session, action.ItemId, 1))
                {
                    return;
                }

                updated.BackgroundId = action.ItemId;
                updated.Damage = 0;
                break;

            default:
                return;
        }

        if (updated == current)
        {
            return;
        }

        ApplyTileMutation(tx, ty, index, updated);
    }

    private void HandlePunchTree(int tx, int ty, int index, Tile current, PlayerSession? session)
    {
        if (!_trees.TryGetValue(index, out TreeData tree))
        {
            // Bersihkan tile pohon yatim secara aman.
            Tile cleared = current;
            cleared.ForegroundId = TileIds.Air;
            cleared.Damage = 0;
            cleared.ExtraDataId = 0;
            ApplyTileMutation(tx, ty, index, cleared);
            BroadcastTreeUpdate(index, isRemoved: true, default);
            return;
        }

        long nowTicks = UtcNowTicks;
        ItemCatalog.TryGetSeedProperties(tree.SeedId, out SeedProperties seedProps);
        string treeName = !string.IsNullOrEmpty(seedProps.TreeName) ? seedProps.TreeName : "Tree";

        if (!tree.IsReady(nowTicks))
        {
            if (session is not null)
            {
                string remain = FormatRemainingTime(tree.GetRemainingSeconds(nowTicks));
                SendThrottledToast(session, $"{treeName}: {remain} tersisa", ToastKind.Info);
            }

            return;
        }

        // Pohon sudah matang -> panen secara atomik (hapus dari _trees & tile dulu, lalu spawn hasil panen).
        _trees.Remove(index);

        Tile emptyTile = current;
        emptyTile.ForegroundId = TileIds.Air;
        emptyTile.Damage = 0;
        emptyTile.ExtraDataId = 0;
        ApplyTileMutation(tx, ty, index, emptyTile);
        BroadcastTreeUpdate(index, isRemoved: true, in tree);

        SpawnDropsForHarvestedTree(tx, ty, in tree, in seedProps);
    }

    private void SpawnDropsForHarvestedTree(int tx, int ty, in TreeData tree, in SeedProperties seedProps)
    {
        float baseX = (tx + 0.5f) * WorldConstants.TileSize;
        float baseY = (ty + 0.5f) * WorldConstants.TileSize;
        static float Jitter(float range) => (float)((Random.Shared.NextDouble() * 2.0 - 1.0) * range);

        ushort fruitBlockId = seedProps.BlockId != TileIds.Air ? seedProps.BlockId : TileIds.Wood;
        ushort fruitCount = Math.Max((ushort)1, (ushort)tree.FruitCount);
        SpawnDrop(fruitBlockId, fruitCount, baseX - 4f + Jitter(3f), baseY + Jitter(2f));

        // Peluang 35% menjatuhkan 1-2 Seed tambahan saat panen pohon matang.
        if (seedProps.SeedId != TileIds.Air && Random.Shared.NextDouble() < ItemCatalog.HarvestBonusSeedChance)
        {
            ushort bonusSeeds = (ushort)Random.Shared.Next(1, 3);
            SpawnDrop(seedProps.SeedId, bonusSeeds, baseX + 6f + Jitter(3f), baseY - 2f + Jitter(2f));
        }

        // Gem drop sesuai rentang GemDropMin..GemDropMax dari seed pohon tersebut.
        if (seedProps.GemDropMax > 0)
        {
            int minGems = Math.Max(1, (int)seedProps.GemDropMin);
            int maxGems = Math.Max(minGems, (int)seedProps.GemDropMax);
            ushort gemCount = (ushort)Random.Shared.Next(minGems, maxGems + 1);
            SpawnDrop(TileIds.Gem, gemCount, baseX + Jitter(4f), baseY - 6f + Jitter(2f));
        }
    }

    private void SpawnDropsForBrokenTile(int tx, int ty, ushort brokenItemId)
    {
        float baseX = (tx + 0.5f) * WorldConstants.TileSize;
        float baseY = (ty + 0.5f) * WorldConstants.TileSize;
        static float Jitter(float range) => (float)((Random.Shared.NextDouble() * 2.0 - 1.0) * range);

        ItemDefinition def = ItemCatalog.Get(brokenItemId);
        if (def.DropItemId != TileIds.Air)
        {
            SpawnDrop(def.DropItemId, 1, baseX + Jitter(5f), baseY + Jitter(3f));
        }

        // Tabel loot RNG untuk blok farmable: peluang drop Seed pasangan blok tersebut + Gem drop.
        if (ItemCatalog.TryGetSeedForBlock(brokenItemId, out SeedProperties seedProps))
        {
            if (Random.Shared.NextDouble() < seedProps.SeedDropChance)
            {
                SpawnDrop(seedProps.SeedId, 1, baseX + 5f + Jitter(3f), baseY - 3f + Jitter(2f));
            }

            if (seedProps.GemDropMax > 0 && Random.Shared.Next(100) < 40)
            {
                int minG = Math.Max(1, (int)seedProps.GemDropMin);
                int maxG = Math.Max(minG, (int)seedProps.GemDropMax);
                ushort gems = (ushort)Random.Shared.Next(minG, maxG + 1);
                SpawnDrop(TileIds.Gem, gems, baseX - 5f + Jitter(3f), baseY - 4f + Jitter(2f));
            }
        }
        else if (brokenItemId != TileIds.WorldLock && Random.Shared.Next(100) < 25)
        {
            SpawnDrop(TileIds.Gem, 1, baseX - 5f + Jitter(3f), baseY - 4f + Jitter(2f));
        }
    }

    /// <summary>
    /// Memunculkan <see cref="DroppedItem"/> dengan Auto-Stacking (radius &lt;= 1.2 tile &amp; total &lt;= 200)
    /// dan generator ID monotonik <c>uint _nextDropId</c>.
    /// </summary>
    private void SpawnDrop(ushort itemId, ushort count, float x, float y, int dropperPlayerId = -1)
    {
        if (itemId == TileIds.Air || count == 0)
        {
            return;
        }

        float clampedX = Math.Clamp(x, 8f, WorldConstants.WorldPixelWidth - 8f);
        float clampedY = Math.Clamp(y, 8f, WorldConstants.WorldPixelHeight - 8f);
        const float autoStackRadiusSq = PhysicsConstants.DropAutoStackRadiusPx * PhysicsConstants.DropAutoStackRadiusPx;

        int remaining = count;
        while (remaining > 0)
        {
            ushort chunk = (ushort)Math.Min(remaining, ItemCatalog.MaxDropStack);
            remaining -= chunk;

            uint mergeTargetId = 0;
            float bestDistSq = float.MaxValue;
            foreach (DroppedItem existing in _drops.Values)
            {
                if (existing.ItemId != itemId || existing.Count + chunk > ItemCatalog.MaxDropStack)
                {
                    continue;
                }

                float dx = existing.X - clampedX;
                float dy = existing.Y - clampedY;
                float distSq = (dx * dx) + (dy * dy);
                if (distSq <= autoStackRadiusSq && distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    mergeTargetId = existing.DropId;
                }
            }

            if (mergeTargetId != 0 && _drops.TryGetValue(mergeTargetId, out DroppedItem existingDrop))
            {
                ushort mergedCount = (ushort)(existingDrop.Count + chunk);
                _drops[mergeTargetId] = existingDrop with { Count = mergedCount };
                if (dropperPlayerId >= 0)
                {
                    _dropperCooldowns[mergeTargetId] = (dropperPlayerId, ElapsedSeconds + PhysicsConstants.DropperPickupCooldownSeconds);
                }

                MarkMetadataDirty();

                _writer.Reset();
                var updatePacket = new DropUpdatePacket(mergeTargetId, mergedCount, CollectorPlayerId: -1);
                PacketSerializer.WriteDropUpdate(_writer, in updatePacket);
                BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
            }
            else
            {
                if (_nextDropId == 0)
                {
                    _nextDropId = 1;
                }

                uint id = _nextDropId++;
                var drop = new DroppedItem(id, itemId, chunk, clampedX, clampedY, (float)ElapsedSeconds);
                _drops[id] = drop;
                if (dropperPlayerId >= 0)
                {
                    _dropperCooldowns[id] = (dropperPlayerId, ElapsedSeconds + PhysicsConstants.DropperPickupCooldownSeconds);
                }

                MarkMetadataDirty();

                _writer.Reset();
                var spawnPacket = new DropSpawnPacket(drop);
                PacketSerializer.WriteDropSpawn(_writer, in spawnPacket);
                BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
            }
        }
    }

    /// <summary>
    /// Authoritative Pickup (berjalan serial di dalam <see cref="Tick"/>):
    /// memeriksa jarak (~0.9 tile) &amp; cooldown pembuang (0.45 detik), memverifikasi DropId masih ada,
    /// mengurangi/menghapus dari dictionary TERLEBIH DAHULU sebelum menambah inventory (maks 200/item),
    /// lalu menyiarkan <see cref="DropRemovePacket"/>/<see cref="DropUpdatePacket"/> dan <see cref="InventorySyncPacket"/>.
    /// </summary>
    private void CheckDropPickups()
    {
        if (_drops.Count == 0 || _players.Count == 0)
        {
            return;
        }

        const float radiusSq = PhysicsConstants.DropPickupRadiusPx * PhysicsConstants.DropPickupRadiusPx;

        _dropIdScratch.Clear();
        foreach (uint dropId in _drops.Keys)
        {
            _dropIdScratch.Add(dropId);
        }

        _playerScratch.Clear();
        foreach (PlayerSession player in _players.Values)
        {
            _playerScratch.Add(player);
        }

        _playerScratch.Sort((a, b) => a.PlayerId.CompareTo(b.PlayerId));

        for (int d = 0; d < _dropIdScratch.Count; d++)
        {
            uint dropId = _dropIdScratch[d];

            for (int p = 0; p < _playerScratch.Count; p++)
            {
                if (!_drops.TryGetValue(dropId, out DroppedItem drop))
                {
                    break;
                }

                PlayerSession player = _playerScratch[p];

                if (IsPlayerInActiveTrade(player.PlayerId))
                {
                    continue;
                }

                if (_dropperCooldowns.TryGetValue(dropId, out (int DropperPlayerId, double CooldownUntilSeconds) cd))
                {
                    if (ElapsedSeconds >= cd.CooldownUntilSeconds)
                    {
                        _dropperCooldowns.Remove(dropId);
                    }
                    else if (cd.DropperPlayerId == player.PlayerId)
                    {
                        continue;
                    }
                }

                float playerCenterX = player.PositionX + PhysicsConstants.PlayerWidth * 0.5f;
                float playerCenterY = player.PositionY + PhysicsConstants.PlayerHeight * 0.5f;
                float dx = drop.X - playerCenterX;
                float dy = drop.Y - playerCenterY;
                if ((dx * dx) + (dy * dy) > radiusSq)
                {
                    continue;
                }

                ushort take;
                ushort currentItemCount = 0;
                if (drop.ItemId == TileIds.Gem)
                {
                    int room = Math.Max(0, ItemCatalog.MaxGemBalance - player.Gems);
                    take = (ushort)Math.Min(drop.Count, room);
                }
                else
                {
                    player.Inventory.TryGetValue(drop.ItemId, out currentItemCount);
                    int room = Math.Max(0, ItemCatalog.MaxInventoryStack - currentItemCount);
                    take = (ushort)Math.Min(drop.Count, room);
                }

                if (take == 0)
                {
                    continue;
                }

                bool fullyCollected = take == drop.Count;
                ushort leftover = (ushort)(drop.Count - take);

                if (fullyCollected)
                {
                    _drops.Remove(dropId);
                    _dropperCooldowns.Remove(dropId);
                }
                else
                {
                    _drops[dropId] = drop with { Count = leftover };
                }

                MarkMetadataDirty();

                if (drop.ItemId == TileIds.Gem)
                {
                    player.Gems += take;
                }
                else
                {
                    player.Inventory[drop.ItemId] = (ushort)(currentItemCount + take);
                }

                _writer.Reset();
                if (fullyCollected)
                {
                    var removePacket = new DropRemovePacket(dropId, player.PlayerId);
                    PacketSerializer.WriteDropRemove(_writer, in removePacket);
                }
                else
                {
                    var updatePacket = new DropUpdatePacket(dropId, leftover, player.PlayerId);
                    PacketSerializer.WriteDropUpdate(_writer, in updatePacket);
                }

                BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
                SendInventorySync(player);
                NotifyPlayerProfileChanged(player, immediatePersist: false);

                if (fullyCollected)
                {
                    break;
                }
            }
        }
    }

    private static string FormatRemainingTime(double seconds)
    {
        int totalSecs = Math.Max(1, (int)Math.Ceiling(seconds));
        if (totalSecs >= 60)
        {
            int mins = totalSecs / 60;
            int secs = totalSecs % 60;
            return $"{mins}m {secs:D2}s";
        }

        return $"{totalSecs}s";
    }

    private bool TryConsumeInventoryItem(PlayerSession session, ushort itemId, ushort amount)
    {
        if (amount == 0 || !session.Inventory.TryGetValue(itemId, out ushort current) || current < amount)
        {
            return false;
        }

        ushort remaining = (ushort)(current - amount);
        if (remaining == 0)
        {
            session.Inventory.Remove(itemId);
        }
        else
        {
            session.Inventory[itemId] = remaining;
        }

        SendInventorySync(session);
        NotifyPlayerProfileChanged(session, immediatePersist: false);
        return true;
    }

    private void NotifyPlayerProfileChanged(PlayerSession session, bool immediatePersist)
    {
        session.IsProfileDirty = true;
        if (session.AccountId > 0 && _onPlayerProfileChanged is not null)
        {
            _onPlayerProfileChanged(
                session.AccountId,
                new Dictionary<ushort, ushort>(session.Inventory),
                session.Gems,
                Name,
                immediatePersist);
        }
    }

    private void SendInventorySync(PlayerSession session)
    {
        if (session.Peer is null)
        {
            return;
        }

        _writer.Reset();
        var sync = new InventorySyncPacket(session.ToInventorySlots(), session.Gems);
        PacketSerializer.WriteInventorySync(_writer, in sync);
        session.Peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastTreeUpdate(int tileIndex, bool isRemoved, in TreeData tree)
    {
        _writer.Reset();
        var packet = new TreeUpdatePacket(tileIndex, isRemoved, tree);
        PacketSerializer.WriteTreeUpdate(_writer, in packet);
        BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastWorldLockState()
    {
        _writer.Reset();
        var packet = new WorldLockStatePacket(_lockData, OpenConfigModal: false);
        PacketSerializer.WriteWorldLockState(_writer, in packet);
        BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void SendWorldLockStateToPeer(NetPeer? peer, bool openConfigModal)
    {
        if (peer is null)
        {
            return;
        }

        _writer.Reset();
        var packet = new WorldLockStatePacket(_lockData, openConfigModal);
        PacketSerializer.WriteWorldLockState(_writer, in packet);
        peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void SendThrottledToast(PlayerSession session, string message, ToastKind kind)
    {
        if (ElapsedSeconds - session.LastLockDenyToastSeconds < ToastThrottleCooldownSeconds)
        {
            return;
        }

        session.LastLockDenyToastSeconds = ElapsedSeconds;
        SendToastToPlayer(session, message, kind);
    }

    private void SendToastToPlayer(PlayerSession player, string message, ToastKind kind)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        PlayerToastSent?.Invoke(player.PlayerId, message, kind);
        SendToast(player.Peer, message, kind);
    }

    private void SendSystemChatToPlayer(PlayerSession player, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        SystemChatSent?.Invoke(player.PlayerId, message);
        SendSystemChat(player.Peer, message);
    }

    // =========================================================================
    // SISTEM TRADE 2 ARAH (AUTHORITATIVE, ANTI-SCAM, ATOMIC SQLITE COMMIT)
    // =========================================================================

    private void UpdateTradeTimers()
    {
        PruneExpiredTradeInvites();

        for (int i = 0; i < _activeTradeSessions.Count; i++)
        {
            TradeSession session = _activeTradeSessions[i];
            if (session.CooldownSyncPending && !session.IsEditCooldownActive(ElapsedSeconds))
            {
                session.CooldownSyncPending = false;
                SyncTradeState(session);
            }
        }
    }

    private void PruneExpiredTradeInvites()
    {
        for (int i = _pendingTradeInvites.Count - 1; i >= 0; i--)
        {
            PendingTradeInvite invite = _pendingTradeInvites[i];
            if (!invite.IsExpired(ElapsedSeconds))
            {
                continue;
            }

            _pendingTradeInvites.RemoveAt(i);
            if (_players.TryGetValue(invite.RequesterPlayerId, out PlayerSession? requester))
            {
                SendToastToPlayer(requester, $"Undangan trade ke {invite.TargetName} kedaluwarsa.", ToastKind.Info);
            }
        }
    }

    private void CancelTradesAndInvitesForLeavingPlayer(PlayerSession leaving, string leaveReason)
    {
        if (_activeTradesByPlayerId.TryGetValue(leaving.PlayerId, out TradeSession? activeTrade))
        {
            string reason = $"Trade dibatalkan karena {leaving.Name} meninggalkan world ({leaveReason}).";
            EndTradeSession(activeTrade, completed: false, reason: reason);
        }

        for (int i = _pendingTradeInvites.Count - 1; i >= 0; i--)
        {
            PendingTradeInvite invite = _pendingTradeInvites[i];
            if (invite.RequesterPlayerId != leaving.PlayerId && invite.TargetPlayerId != leaving.PlayerId)
            {
                continue;
            }

            _pendingTradeInvites.RemoveAt(i);
            int otherId = invite.RequesterPlayerId == leaving.PlayerId
                ? invite.TargetPlayerId
                : invite.RequesterPlayerId;
            if (_players.TryGetValue(otherId, out PlayerSession? other))
            {
                SendToastToPlayer(other, $"Undangan trade dengan {leaving.Name} dibatalkan.", ToastKind.Info);
            }
        }
    }

    private PlayerSession? ResolveTradeTarget(TradeRequestPacket request)
    {
        if (request.TargetAccountId > 0)
        {
            foreach (PlayerSession candidate in _players.Values)
            {
                if (candidate.AccountId == request.TargetAccountId)
                {
                    return candidate;
                }
            }
        }

        string targetName = (request.TargetUsername ?? string.Empty).Trim();
        if (targetName.Length > 0)
        {
            foreach (PlayerSession candidate in _players.Values)
            {
                if (string.Equals(candidate.Name, targetName, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private void RemovePendingInvitesForPlayer(int playerId)
    {
        for (int i = _pendingTradeInvites.Count - 1; i >= 0; i--)
        {
            PendingTradeInvite invite = _pendingTradeInvites[i];
            if (invite.RequesterPlayerId == playerId || invite.TargetPlayerId == playerId)
            {
                _pendingTradeInvites.RemoveAt(i);
            }
        }
    }

    private void HandleTradeRequest(TradeRequestCommand command)
    {
        PruneExpiredTradeInvites();

        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? requester))
        {
            return;
        }

        if (IsPlayerInActiveTrade(requester.PlayerId))
        {
            SendToastToPlayer(requester, "Kamu sudah berada di dalam sesi trade aktif.", ToastKind.Warning);
            return;
        }

        PlayerSession? target = ResolveTradeTarget(command.Request);
        if (target is null)
        {
            SendToastToPlayer(requester, "Pemain target tidak berada di world ini.", ToastKind.Warning);
            return;
        }

        if (target.PlayerId == requester.PlayerId ||
            (requester.AccountId > 0 && requester.AccountId == target.AccountId))
        {
            SendToastToPlayer(requester, "Tidak dapat melakukan trade dengan diri sendiri.", ToastKind.Warning);
            return;
        }

        if (IsPlayerInActiveTrade(target.PlayerId))
        {
            SendToastToPlayer(requester, $"{target.Name} sedang berada di dalam sesi trade lain.", ToastKind.Warning);
            return;
        }

        for (int i = 0; i < _pendingTradeInvites.Count; i++)
        {
            PendingTradeInvite existing = _pendingTradeInvites[i];
            if (existing.RequesterPlayerId == requester.PlayerId && existing.TargetPlayerId == target.PlayerId)
            {
                SendToastToPlayer(requester, $"Undangan trade ke {target.Name} masih menunggu respons.", ToastKind.Info);
                return;
            }
        }

        var invite = new PendingTradeInvite(requester, target, ElapsedSeconds);
        _pendingTradeInvites.Add(invite);

        var invitePacket = new TradeInvitePacket(requester.AccountId, requester.Name);
        SendTradeInvite(target, in invitePacket);
        SendToastToPlayer(requester, $"Undangan trade dikirim ke {target.Name}.", ToastKind.Info);
    }

    private void HandleTradeInviteResponse(TradeInviteResponseCommand command)
    {
        PruneExpiredTradeInvites();

        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? responder))
        {
            return;
        }

        int inviteIndex = -1;
        for (int i = 0; i < _pendingTradeInvites.Count; i++)
        {
            PendingTradeInvite candidate = _pendingTradeInvites[i];
            if (candidate.TargetPlayerId == responder.PlayerId &&
                candidate.RequesterAccountId == command.Response.RequesterAccountId)
            {
                inviteIndex = i;
                break;
            }
        }

        if (inviteIndex < 0)
        {
            SendToastToPlayer(responder, "Undangan trade sudah kedaluwarsa atau tidak valid.", ToastKind.Warning);
            return;
        }

        PendingTradeInvite invite = _pendingTradeInvites[inviteIndex];
        _pendingTradeInvites.RemoveAt(inviteIndex);

        if (!_players.TryGetValue(invite.RequesterPlayerId, out PlayerSession? requester))
        {
            SendToastToPlayer(responder, "Pengirim undangan trade sudah meninggalkan world.", ToastKind.Warning);
            return;
        }

        if (!command.Response.Accepted)
        {
            SendToastToPlayer(requester, $"{responder.Name} menolak undangan trade.", ToastKind.Info);
            SendToastToPlayer(responder, $"Undangan trade dari {requester.Name} ditolak.", ToastKind.Info);
            return;
        }

        if (IsPlayerInActiveTrade(requester.PlayerId) || IsPlayerInActiveTrade(responder.PlayerId))
        {
            SendToastToPlayer(responder, "Salah satu pemain sedang berada dalam sesi trade lain.", ToastKind.Warning);
            return;
        }

        RemovePendingInvitesForPlayer(requester.PlayerId);
        RemovePendingInvitesForPlayer(responder.PlayerId);

        var session = new TradeSession(requester, responder, ElapsedSeconds);
        _activeTradeSessions.Add(session);
        _activeTradesByPlayerId[requester.PlayerId] = session;
        _activeTradesByPlayerId[responder.PlayerId] = session;

        SyncTradeState(session);
    }

    private void HandleTradeUpdateOffer(TradeUpdateOfferCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? player) ||
            !_activeTradesByPlayerId.TryGetValue(command.PlayerId, out TradeSession? trade))
        {
            return;
        }

        byte slotIndex = command.Update.SlotIndex;
        if (slotIndex >= TradeSession.SlotCount)
        {
            SendToastToPlayer(player, "Slot trade tidak valid (harus 0 sampai 3).", ToastKind.Warning);
            return;
        }

        TradeOfferSlot[] myOffers = trade.GetOffersFor(player.PlayerId);
        ushort itemId = command.Update.ItemId;
        ushort count = command.Update.Count;

        // Count == 0 berarti mengosongkan slot penawaran.
        if (count == 0)
        {
            if (myOffers[slotIndex].IsEmpty)
            {
                return;
            }

            myOffers[slotIndex] = TradeOfferSlot.Empty;
            trade.OnOfferEdited(ElapsedSeconds);
            SyncTradeState(trade);
            return;
        }

        // Larang Fist, Wrench, Air, Gem, item non-tradeable, atau item tidak dikenal di katalog.
        if (!ItemCatalog.IsTradeable(itemId))
        {
            SendToastToPlayer(player, "Item tersebut tidak dapat diperdagangkan!", ToastKind.Warning);
            return;
        }

        if (count > ItemCatalog.MaxInventoryStack)
        {
            SendToastToPlayer(player, $"Jumlah penawaran melebihi batas maksimal ({ItemCatalog.MaxInventoryStack}).", ToastKind.Warning);
            return;
        }

        // Larang menaruh ItemId yang sama di dua slot berbeda agar validasi stok selalu tepat.
        for (int i = 0; i < TradeSession.SlotCount; i++)
        {
            if (i != slotIndex && !myOffers[i].IsEmpty && myOffers[i].ItemId == itemId)
            {
                SendToastToPlayer(player, "Item yang sama sudah ada di slot lain. Ubah jumlah di slot tersebut.", ToastKind.Warning);
                return;
            }
        }

        // Validasi stok aktual inventory pemain saat ini.
        ushort owned = player.Inventory.TryGetValue(itemId, out ushort currentCount) ? currentCount : (ushort)0;
        if (count > owned)
        {
            SendToastToPlayer(player, $"Stok {ItemCatalog.GetName(itemId)} tidak mencukupi (tersedia {owned}).", ToastKind.Warning);
            return;
        }

        // Tidak ada perubahan aktual -> abaikan.
        if (myOffers[slotIndex].ItemId == itemId && myOffers[slotIndex].Count == count)
        {
            return;
        }

        myOffers[slotIndex] = new TradeOfferSlot(itemId, count);
        trade.OnOfferEdited(ElapsedSeconds);
        SyncTradeState(trade);
    }

    private void HandleTradeAction(TradeActionCommand command)
    {
        if (!_players.TryGetValue(command.PlayerId, out PlayerSession? player) ||
            !_activeTradesByPlayerId.TryGetValue(command.PlayerId, out TradeSession? trade))
        {
            return;
        }

        switch (command.Action.Action)
        {
            case TradeActionKind.Cancel:
                EndTradeSession(trade, completed: false, reason: $"Trade dibatalkan oleh {player.Name}.");
                return;

            case TradeActionKind.ToggleAccept:
                if (trade.IsEditCooldownActive(ElapsedSeconds))
                {
                    float remaining = trade.GetLockCooldownRemaining(ElapsedSeconds);
                    SendToastToPlayer(
                        player,
                        $"Tunggu {remaining:0.0} detik setelah perubahan penawaran sebelum menekan Accept.",
                        ToastKind.Warning);
                    SyncTradeState(trade);
                    return;
                }

                if (trade.IsPlayerA(player.PlayerId))
                {
                    trade.AcceptedA = !trade.AcceptedA;
                    if (!trade.AcceptedA)
                    {
                        trade.ResetConfirmations();
                    }
                }
                else
                {
                    trade.AcceptedB = !trade.AcceptedB;
                    if (!trade.AcceptedB)
                    {
                        trade.ResetConfirmations();
                    }
                }

                SyncTradeState(trade);
                return;

            case TradeActionKind.FinalConfirm:
                if (!trade.AcceptedA || !trade.AcceptedB)
                {
                    SendToastToPlayer(player, "Kedua pihak wajib menekan Accept terlebih dahulu.", ToastKind.Warning);
                    return;
                }

                if (trade.IsEditCooldownActive(ElapsedSeconds))
                {
                    SendToastToPlayer(player, "Tunggu jeda keamanan selesai sebelum konfirmasi akhir.", ToastKind.Warning);
                    SyncTradeState(trade);
                    return;
                }

                if (trade.IsPlayerA(player.PlayerId))
                {
                    trade.ConfirmedA = true;
                }
                else
                {
                    trade.ConfirmedB = true;
                }

                if (!(trade.ConfirmedA && trade.ConfirmedB))
                {
                    SyncTradeState(trade);
                    return;
                }

                CommitTradeAtomic(trade);
                return;
        }
    }

    private void CommitTradeAtomic(TradeSession trade)
    {
        if (!_players.TryGetValue(trade.PlayerIdA, out PlayerSession? playerA) ||
            !_players.TryGetValue(trade.PlayerIdB, out PlayerSession? playerB))
        {
            EndTradeSession(trade, completed: false, reason: "Trade dibatalkan karena salah satu pemain sudah keluar.");
            return;
        }

        Dictionary<ushort, ushort> outgoingA = trade.AggregateOffers(forPlayerA: true);
        Dictionary<ushort, ushort> outgoingB = trade.AggregateOffers(forPlayerA: false);

        // 1. Validasi ulang stok akhir kedua pemain di RAM.
        if (!ValidateTradeOutgoingStock(playerA, outgoingA, out string stockErrorA))
        {
            AbortTradeConfirmation(trade, playerA, playerB, stockErrorA);
            return;
        }

        if (!ValidateTradeOutgoingStock(playerB, outgoingB, out string stockErrorB))
        {
            AbortTradeConfirmation(trade, playerA, playerB, stockErrorB);
            return;
        }

        // 2. Hitung inventory kandidat dan validasi kapasitas tas penerima (maks 200 per jenis item).
        var candidateInvA = new Dictionary<ushort, ushort>(playerA.Inventory);
        var candidateInvB = new Dictionary<ushort, ushort>(playerB.Inventory);

        DeductTradeOffers(candidateInvA, outgoingA);
        DeductTradeOffers(candidateInvB, outgoingB);

        if (!TryAddTradeOffersWithCapacityCheck(candidateInvA, outgoingB, playerA.Name, out string capErrorA))
        {
            AbortTradeConfirmation(trade, playerA, playerB, capErrorA);
            return;
        }

        if (!TryAddTradeOffersWithCapacityCheck(candidateInvB, outgoingA, playerB.Name, out string capErrorB))
        {
            AbortTradeConfirmation(trade, playerA, playerB, capErrorB);
            return;
        }

        // 3. Persistensi transaksional SQLite (1 transaksi atomik) SEBELUM memutasi RAM agar kebal partial-failure.
        bool persistedAtomically = false;
        if (_accountRepository is not null && playerA.AccountId > 0 && playerB.AccountId > 0)
        {
            try
            {
                byte[] blobA = AccountInventoryCodec.Encode(candidateInvA);
                byte[] blobB = AccountInventoryCodec.Encode(candidateInvB);
                _accountRepository.SaveTradeProfilesAtomic(
                    new AccountProfileSaveEntry(playerA.AccountId, playerA.Gems, blobA, Name),
                    new AccountProfileSaveEntry(playerB.AccountId, playerB.Gems, blobB, Name));
                persistedAtomically = true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Trade] Gagal menyimpan transaksi SQLite atomik: {ex.Message}");
                AbortTradeConfirmation(trade, playerA, playerB, "Gagal menyimpan transaksi trade ke database. Konfirmasi dibatalkan.");
                return;
            }
        }

        // 4. Lakukan pertukaran item di RAM untuk kedua sesi pemain.
        ReplaceInventory(playerA.Inventory, candidateInvA);
        ReplaceInventory(playerB.Inventory, candidateInvB);

        if (persistedAtomically)
        {
            playerA.IsProfileDirty = false;
            playerB.IsProfileDirty = false;
            _onPlayerProfilePersistedAtomically?.Invoke(playerA.AccountId, new Dictionary<ushort, ushort>(playerA.Inventory), playerA.Gems, Name);
            _onPlayerProfilePersistedAtomically?.Invoke(playerB.AccountId, new Dictionary<ushort, ushort>(playerB.Inventory), playerB.Gems, Name);
        }
        else
        {
            NotifyPlayerProfileChanged(playerA, immediatePersist: false);
            NotifyPlayerProfileChanged(playerB, immediatePersist: false);
        }

        // 5. Tutup sesi trade, sinkronkan inventory, kirim TradeEnded(Completed: true), dan log chat sistem.
        RemoveActiveTradeSession(trade);

        SendInventorySync(playerA);
        SendInventorySync(playerB);

        SendTradeEnded(playerA, new TradeEndedPacket(Completed: true, ReasonMessage: $"Trade dengan {playerB.Name} berhasil!"));
        SendTradeEnded(playerB, new TradeEndedPacket(Completed: true, ReasonMessage: $"Trade dengan {playerA.Name} berhasil!"));

        SendSystemChatToPlayer(
            playerA,
            $"[TRADE] Selesai dengan {playerB.Name} | Diberikan: {FormatTradeSummary(outgoingA)} | Diterima: {FormatTradeSummary(outgoingB)}.");
        SendSystemChatToPlayer(
            playerB,
            $"[TRADE] Selesai dengan {playerA.Name} | Diberikan: {FormatTradeSummary(outgoingB)} | Diterima: {FormatTradeSummary(outgoingA)}.");

        SendToastToPlayer(playerA, "Trade berhasil diselesaikan!", ToastKind.Success);
        SendToastToPlayer(playerB, "Trade berhasil diselesaikan!", ToastKind.Success);
    }

    private static bool ValidateTradeOutgoingStock(
        PlayerSession player,
        Dictionary<ushort, ushort> outgoing,
        out string errorMessage)
    {
        foreach ((ushort itemId, ushort count) in outgoing)
        {
            if (count == 0 || !ItemCatalog.IsTradeable(itemId))
            {
                errorMessage = $"Item penawaran milik {player.Name} tidak valid. Konfirmasi dibatalkan.";
                return false;
            }

            if (!player.Inventory.TryGetValue(itemId, out ushort owned) || owned < count)
            {
                errorMessage = $"Stok {ItemCatalog.GetName(itemId)} milik {player.Name} tidak mencukupi. Konfirmasi dibatalkan.";
                return false;
            }
        }

        errorMessage = string.Empty;
        return true;
    }

    private static void DeductTradeOffers(
        Dictionary<ushort, ushort> inventory,
        Dictionary<ushort, ushort> outgoing)
    {
        foreach ((ushort itemId, ushort count) in outgoing)
        {
            ushort current = inventory[itemId];
            ushort remaining = (ushort)(current - count);
            if (remaining == 0)
            {
                inventory.Remove(itemId);
            }
            else
            {
                inventory[itemId] = remaining;
            }
        }
    }

    private static bool TryAddTradeOffersWithCapacityCheck(
        Dictionary<ushort, ushort> recipientInventory,
        Dictionary<ushort, ushort> incoming,
        string recipientName,
        out string errorMessage)
    {
        foreach ((ushort itemId, ushort addCount) in incoming)
        {
            recipientInventory.TryGetValue(itemId, out ushort existing);
            int total = existing + addCount;
            if (total > ItemCatalog.MaxInventoryStack)
            {
                errorMessage =
                    $"Tas {recipientName} penuh untuk {ItemCatalog.GetName(itemId)} (maksimal {ItemCatalog.MaxInventoryStack}). Konfirmasi dibatalkan.";
                return false;
            }

            recipientInventory[itemId] = (ushort)total;
        }

        errorMessage = string.Empty;
        return true;
    }

    private void AbortTradeConfirmation(
        TradeSession trade,
        PlayerSession playerA,
        PlayerSession playerB,
        string errorMessage)
    {
        trade.ResetApprovals();
        SendToastToPlayer(playerA, errorMessage, ToastKind.Warning);
        SendToastToPlayer(playerB, errorMessage, ToastKind.Warning);
        SendSystemChatToPlayer(playerA, $"[TRADE] {errorMessage}");
        SendSystemChatToPlayer(playerB, $"[TRADE] {errorMessage}");
        SyncTradeState(trade);
    }

    private void EndTradeSession(TradeSession trade, bool completed, string reason)
    {
        RemoveActiveTradeSession(trade);

        var ended = new TradeEndedPacket(completed, reason);
        if (_players.TryGetValue(trade.PlayerIdA, out PlayerSession? playerA))
        {
            SendTradeEnded(playerA, in ended);
        }

        if (_players.TryGetValue(trade.PlayerIdB, out PlayerSession? playerB))
        {
            SendTradeEnded(playerB, in ended);
        }
    }

    private void RemoveActiveTradeSession(TradeSession trade)
    {
        _activeTradeSessions.Remove(trade);
        _activeTradesByPlayerId.Remove(trade.PlayerIdA);
        _activeTradesByPlayerId.Remove(trade.PlayerIdB);
    }

    private void SyncTradeState(TradeSession trade)
    {
        TradeStateSyncPacket sync = trade.ToSyncPacket(ElapsedSeconds);
        if (_players.TryGetValue(trade.PlayerIdA, out PlayerSession? playerA))
        {
            SendTradeStateSync(playerA, in sync);
        }

        if (_players.TryGetValue(trade.PlayerIdB, out PlayerSession? playerB))
        {
            SendTradeStateSync(playerB, in sync);
        }
    }

    private void SendTradeInvite(PlayerSession target, in TradeInvitePacket packet)
    {
        TradeInviteSent?.Invoke(target.PlayerId, packet);
        if (target.Peer is null)
        {
            return;
        }

        _writer.Reset();
        PacketSerializer.WriteTradeInvite(_writer, in packet);
        target.Peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void SendTradeStateSync(PlayerSession player, in TradeStateSyncPacket packet)
    {
        TradeStateSynced?.Invoke(player.PlayerId, packet);
        if (player.Peer is null)
        {
            return;
        }

        _writer.Reset();
        PacketSerializer.WriteTradeStateSync(_writer, in packet);
        player.Peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void SendTradeEnded(PlayerSession player, in TradeEndedPacket packet)
    {
        TradeEndedSent?.Invoke(player.PlayerId, packet);
        if (player.Peer is null)
        {
            return;
        }

        _writer.Reset();
        PacketSerializer.WriteTradeEnded(_writer, in packet);
        player.Peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private static void ReplaceInventory(Dictionary<ushort, ushort> target, Dictionary<ushort, ushort> source)
    {
        target.Clear();
        foreach ((ushort itemId, ushort count) in source)
        {
            if (count > 0)
            {
                target[itemId] = count;
            }
        }
    }

    private static string FormatTradeSummary(IReadOnlyDictionary<ushort, ushort> items)
    {
        if (items.Count == 0)
        {
            return "(tidak ada)";
        }

        var parts = new List<string>(items.Count);
        foreach ((ushort itemId, ushort count) in items)
        {
            parts.Add($"{count}x {ItemCatalog.GetName(itemId)}");
        }

        return string.Join(", ", parts);
    }

    private void SendToast(NetPeer? peer, string message, ToastKind kind)
    {
        if (peer is null || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _writer.Reset();
        var toast = new SystemToastPacket(message, kind);
        PacketSerializer.WriteSystemToast(_writer, in toast);
        peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastToast(string message, ToastKind kind)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _writer.Reset();
        var toast = new SystemToastPacket(message, kind);
        PacketSerializer.WriteSystemToast(_writer, in toast);
        BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void SendSystemChat(NetPeer? peer, string message)
    {
        if (peer is null)
        {
            return;
        }

        _writer.Reset();
        var chat = new ChatMessagePacket(SystemSenderId, "SERVER", message);
        PacketSerializer.WriteChatMessage(_writer, in chat);
        peer.Send(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastSystemChat(string message)
    {
        _writer.Reset();
        var chat = new ChatMessagePacket(SystemSenderId, "SERVER", message);
        PacketSerializer.WriteChatMessage(_writer, in chat);
        BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
    }

    private bool IntersectsAnyPlayer(int tileX, int tileY, float actorX, float actorY)
    {
        if (TileCollision.IntersectsTile(
                actorX,
                actorY,
                PhysicsConstants.PlayerWidth,
                PhysicsConstants.PlayerHeight,
                tileX,
                tileY))
        {
            return true;
        }

        foreach (PlayerSession player in _players.Values)
        {
            if (TileCollision.IntersectsTile(
                    player.PositionX,
                    player.PositionY,
                    PhysicsConstants.PlayerWidth,
                    PhysicsConstants.PlayerHeight,
                    tileX,
                    tileY))
            {
                return true;
            }
        }

        return false;
    }

    private void HandleSetTile(SetTileCommand command)
    {
        if (!Tile.IsInBounds(command.X, command.Y))
        {
            return;
        }

        int index = Tile.GetIndex(command.X, command.Y);
        if (command.Tile.ForegroundId != TileIds.Tree && _trees.Remove(index))
        {
            BroadcastTreeUpdate(index, isRemoved: true, default);
        }

        ApplyTileMutation(command.X, command.Y, index, command.Tile);
    }

    private void MarkMetadataDirty()
    {
        _mutationVersion++;
        Volatile.Write(ref _isDirty, 1);
    }

    private void ApplyTileMutation(int x, int y, int index, Tile newTile)
    {
        _tiles[index] = newTile;
        _cachedBrotliTiles = null;
        _mutationVersion++;
        Volatile.Write(ref _isDirty, 1);

        _writer.Reset();
        var delta = new TileUpdateDeltaPacket(x, y, newTile);
        PacketSerializer.WriteTileUpdateDelta(_writer, in delta);
        BroadcastToAll(_writer, DeliveryMethod.ReliableOrdered);
    }

    private void BroadcastToAll(NetDataWriter writer, DeliveryMethod deliveryMethod)
    {
        foreach (PlayerSession player in _players.Values)
        {
            player.Peer?.Send(writer, deliveryMethod);
        }
    }

    private void BroadcastToOthers(int excludePlayerId, NetDataWriter writer, DeliveryMethod deliveryMethod)
    {
        foreach (PlayerSession player in _players.Values)
        {
            if (player.PlayerId != excludePlayerId)
            {
                player.Peer?.Send(writer, deliveryMethod);
            }
        }
    }
}
