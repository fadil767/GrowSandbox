using System.Collections.Concurrent;
using GrowSandbox.Server.Persistence;
using GrowSandbox.Shared.Models;

namespace GrowSandbox.Server.World;

/// <summary>
/// Mengelola seluruh instansi world aktif di dalam <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// Saat nama world belum aktif, memuat dari SQLite jika tersimpan atau membuat world baru secara otomatis.
/// </summary>
public sealed class WorldManager
{
    private const string DefaultWorldName = "START";

    private readonly ConcurrentDictionary<string, WorldInstance> _worlds =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IWorldRepository? _repository;
    private readonly Func<DateTime>? _utcNowProvider;
    private IAccountRepository? _accountRepository;
    private Action<int, Dictionary<ushort, ushort>, int, string, bool>? _onPlayerProfileChanged;
    private Action<int, Dictionary<ushort, ushort>, int, string>? _onPlayerProfilePersistedAtomically;

    public int ActiveWorldCount => _worlds.Count;

    public WorldManager(
        IWorldRepository? repository = null,
        IAccountRepository? accountRepository = null,
        Func<DateTime>? utcNowProvider = null)
    {
        _repository = repository;
        _accountRepository = accountRepository;
        _utcNowProvider = utcNowProvider;
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

        foreach (WorldInstance world in _worlds.Values)
        {
            world.ConfigureAccountIntegration(_accountRepository, _onPlayerProfileChanged, _onPlayerProfilePersistedAtomically);
        }
    }

    public static string NormalizeWorldName(string? worldName)
    {
        if (string.IsNullOrWhiteSpace(worldName))
        {
            return DefaultWorldName;
        }

        string trimmed = worldName.Trim().ToUpperInvariant();
        return trimmed.Length > 24 ? trimmed[..24] : trimmed;
    }

    public WorldInstance GetOrCreateWorld(string? worldName)
    {
        string normalized = NormalizeWorldName(worldName);
        return _worlds.GetOrAdd(normalized, key =>
        {
            PersistedWorldData? persisted = _repository?.LoadWorldData(key);
            if (persisted is { } data)
            {
                Console.WriteLine(
                    $"[WorldManager] World '{key}' dimuat dari SQLite (100x60, {data.Drops.Length} drops, {data.Trees.Length} trees, locked={data.LockData.IsLocked}).");
                return new WorldInstance(
                    key,
                    data.Tiles,
                    data.LockData,
                    data.Drops,
                    data.NextDropId,
                    data.Trees,
                    _utcNowProvider,
                    _accountRepository,
                    _onPlayerProfileChanged,
                    _onPlayerProfilePersistedAtomically);
            }

            Console.WriteLine($"[WorldManager] World baru dibuat: '{key}' (100x60).");
            return new WorldInstance(
                key,
                WorldGenerator.Generate(key),
                utcNowProvider: _utcNowProvider,
                accountRepository: _accountRepository,
                onPlayerProfileChanged: _onPlayerProfileChanged,
                onPlayerProfilePersistedAtomically: _onPlayerProfilePersistedAtomically);
        });
    }

    public bool TryGetWorld(string worldName, out WorldInstance? world)
    {
        string normalized = NormalizeWorldName(worldName);
        return _worlds.TryGetValue(normalized, out world);
    }

    public IReadOnlyCollection<WorldInstance> GetActiveWorlds() => _worlds.Values.ToArray();

    public void TickAll(float dt)
    {
        foreach (WorldInstance world in _worlds.Values)
        {
            world.Tick(dt);
        }
    }

    /// <summary>
    /// Mengambil snapshot Brotli dari setiap world dengan <c>IsDirty = true</c> melalui
    /// <c>Channel&lt;WorldCommand&gt;</c>, menyimpannya ke SQLite, lalu menandai world bersih kembali.
    /// </summary>
    public async Task<int> SaveDirtyWorldsAsync(CancellationToken cancellationToken = default)
    {
        if (_repository is null)
        {
            return 0;
        }

        int savedCount = 0;

        foreach (WorldInstance world in _worlds.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!world.IsDirty)
            {
                continue;
            }

            var tcs = new TaskCompletionSource<DirtyWorldSnapshot?>(TaskCreationOptions.RunContinuationsAsynchronously);
            world.EnqueueCommand(new CaptureDirtySnapshotCommand(tcs));

            DirtyWorldSnapshot? snapshot = await tcs.Task.WaitAsync(cancellationToken);
            if (snapshot is null)
            {
                continue;
            }

            DirtyWorldSnapshot value = snapshot.Value;
            await _repository.SaveWorldBlobAsync(
                value.WorldName,
                value.CompressedData,
                value.LockData,
                cancellationToken);
            world.EnqueueCommand(new MarkWorldSavedCommand(value.Version));
            savedCount++;

            Console.WriteLine(
                $"[Persistence] Auto-save world '{value.WorldName}' ke SQLite ({value.CompressedData.Length} B v3, {value.DropCount} drops, {value.TreeCount} trees).");
        }

        return savedCount;
    }

    public int FlushDirtyWorldsOnShutdown()
    {
        if (_repository is null)
        {
            return 0;
        }

        int savedCount = 0;
        foreach (WorldInstance world in _worlds.Values)
        {
            if (world.TryCaptureDirtySnapshotForShutdown(out DirtyWorldSnapshot snapshot))
            {
                _repository.SaveWorldBlob(snapshot.WorldName, snapshot.CompressedData, snapshot.LockData);
                world.MarkSavedOnShutdown(snapshot.Version);
                savedCount++;

                Console.WriteLine(
                    $"[Persistence] Shutdown flush world '{snapshot.WorldName}' ke SQLite ({snapshot.CompressedData.Length} B v3, {snapshot.DropCount} drops, {snapshot.TreeCount} trees).");
            }
        }

        return savedCount;
    }
}
