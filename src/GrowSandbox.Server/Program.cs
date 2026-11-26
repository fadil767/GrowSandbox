using GrowSandbox.Server.Network;
using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Networking;

const string databasePath = "growsandbox.db";
var worldRepository = new SqliteWorldRepository(databasePath);
using var accountRepository = new SqliteAccountRepository(databasePath);
var worldManager = new WorldManager(worldRepository, accountRepository);

// Muat world default dari SQLite jika sudah tersimpan, atau buat baru bila belum ada.
WorldInstance startWorld = worldManager.GetOrCreateWorld("START");
byte[] initialSnapshot = startWorld.SerializeTilesBrotli();

Console.WriteLine(
    $"[Server] GrowSandbox Server dimulai | DB='{Path.GetFullPath(databasePath)}' | World='{startWorld.Name}' ({WorldConstants.Width}x{WorldConstants.Height}) | Brotli snapshot={initialSnapshot.Length} byte | Accounts WAL aktif.");
Console.WriteLine("[Server] Tekan Ctrl+C untuk menghentikan server dengan aman.");

using var shutdownCts = new CancellationTokenSource();
using var gameServer = new GameServer(
    worldManager,
    accountRepository,
    port: NetworkConstants.DefaultPort,
    tickRate: GameServer.DefaultTickRate,
    autoSaveInterval: GameServer.DefaultAutoSaveInterval);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("[Server] Menerima sinyal Ctrl+C, menyimpan seluruh world & profil aktif...");
    shutdownCts.Cancel();
};

AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    gameServer.FlushAllStateOnShutdown();
};

await gameServer.RunAsync(shutdownCts.Token);
