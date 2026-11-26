using GrowSandbox.Server.Network;
using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.Security;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Networking.Packets;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Tests;

public class AuthenticationSystemTests
{
    [Fact]
    public void PasswordHasher_CorrectPasswordPasses_WrongPasswordFails_AndIdenticalPasswordsUseDifferentSalts()
    {
        const string password = "MySecurePassword!99";

        (byte[] hash1, byte[] salt1) = PasswordHasher.HashPassword(password);
        (byte[] hash2, byte[] salt2) = PasswordHasher.HashPassword(password);

        Assert.Equal(PasswordHasher.HashSizeBytes, hash1.Length);
        Assert.Equal(PasswordHasher.SaltSizeBytes, salt1.Length);
        Assert.Equal(PasswordHasher.HashSizeBytes, hash2.Length);
        Assert.Equal(PasswordHasher.SaltSizeBytes, salt2.Length);

        // Dua password identik wajib menghasilkan salt acak 16-byte & hash 32-byte yang berbeda.
        Assert.False(salt1.SequenceEqual(salt2));
        Assert.False(hash1.SequenceEqual(hash2));

        // Password benar lulus verifikasi untuk kedua pasangan salt+hash.
        Assert.True(PasswordHasher.VerifyPassword(password, hash1, salt1));
        Assert.True(PasswordHasher.VerifyPassword(password, hash2, salt2));

        // Password salah, kosong, atau pasangan hash/salt tertukar wajib ditolak.
        Assert.False(PasswordHasher.VerifyPassword("WrongPassword!99", hash1, salt1));
        Assert.False(PasswordHasher.VerifyPassword("mysecurepassword!99", hash1, salt1));
        Assert.False(PasswordHasher.VerifyPassword(string.Empty, hash1, salt1));
        Assert.False(PasswordHasher.VerifyPassword(password, hash1, salt2));
    }

    [Fact]
    public void SqliteAccountRepository_RegisterSuccess_RejectsCaseInsensitiveDuplicate_AndVerifiesLogin()
    {
        using var repo = new SqliteAccountRepository(":memory:");

        // Validasi input registrasi tidak sah
        Assert.False(repo.Register("ab", "password123").Success);                // < 3 karakter
        Assert.False(repo.Register("TooLongUsername17", "password123").Success); // > 16 karakter
        Assert.False(repo.Register("Bad Name!", "password123").Success);         // karakter ilegal
        Assert.False(repo.Register("ValidUser", "12345").Success);               // password < 6 karakter

        // Register sukses
        AccountAuthResult reg1 = repo.Register("Budi_99", "Rahasia123");
        Assert.True(reg1.Success);
        Assert.NotNull(reg1.Account);
        Assert.True(reg1.Account.AccountId > 0);
        Assert.Equal("Budi_99", reg1.Account.Username);
        Assert.Equal("budi_99", reg1.Account.NormalizedUsername);
        Assert.Equal("START", reg1.Account.LastWorld);

        // Register username duplikat beda kapitalisasi wajib ditolak ("Budi_99" vs "budi_99" / "BUDI_99")
        AccountAuthResult dupLower = repo.Register("budi_99", "PasswordLain99");
        AccountAuthResult dupUpper = repo.Register("BUDI_99", "PasswordLain99");
        Assert.False(dupLower.Success);
        Assert.False(dupUpper.Success);

        // Login benar (case-insensitive username) -> sukses
        AccountAuthResult loginOk = repo.Authenticate("budi_99", "Rahasia123");
        Assert.True(loginOk.Success);
        Assert.NotNull(loginOk.Account);
        Assert.Equal(reg1.Account.AccountId, loginOk.Account.AccountId);
        Assert.Equal("Budi_99", loginOk.Account.Username);

        // Login password salah & akun tidak ada -> gagal
        Assert.False(repo.Authenticate("Budi_99", "SalahPassword").Success);
        Assert.False(repo.Authenticate("Budi_99", "rahasia123").Success);
        Assert.False(repo.Authenticate("TidakAda", "Rahasia123").Success);
    }

    [Fact]
    public void SqliteAccountRepository_PersistsGemsInventoryAndLastWorldAcrossReopen()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"growsandbox_accounts_{Guid.NewGuid():N}.db");

        try
        {
            int accountId;
            using (var repo1 = new SqliteAccountRepository(tempDbPath))
            {
                AccountAuthResult reg = repo1.Register("BuilderOne", "SecretPass42");
                Assert.True(reg.Success);
                accountId = reg.Account!.AccountId;

                byte[] invBlob = AccountInventoryCodec.Encode(new Dictionary<ushort, ushort>
                {
                    [TileIds.Dirt] = 88,
                    [TileIds.WorldLock] = 3,
                    [TileIds.GlassSeed] = 7,
                });

                repo1.SaveProgress(accountId, gems: 450, invBlob, lastWorld: "TRADE");
            }

            using (var repo2 = new SqliteAccountRepository(tempDbPath))
            {
                AccountAuthResult login = repo2.Authenticate("builderone", "SecretPass42");
                Assert.True(login.Success);
                Assert.NotNull(login.Account);
                Assert.Equal(accountId, login.Account.AccountId);
                Assert.Equal(450, login.Account.Gems);
                Assert.Equal("TRADE", login.Account.LastWorld);

                Dictionary<ushort, ushort>? decoded = AccountInventoryCodec.Decode(login.Account.InventoryBlob);
                Assert.NotNull(decoded);
                Assert.Equal((ushort)88, decoded[TileIds.Dirt]);
                Assert.Equal((ushort)3, decoded[TileIds.WorldLock]);
                Assert.Equal((ushort)7, decoded[TileIds.GlassSeed]);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath)) File.Delete(tempDbPath);
            if (File.Exists(tempDbPath + "-wal")) File.Delete(tempDbPath + "-wal");
            if (File.Exists(tempDbPath + "-shm")) File.Delete(tempDbPath + "-shm");
        }
    }

    [Fact]
    public void GameServer_AuthGate_RejectsUnauthenticatedGameplay_AndEnforcesAntiDoubleLogin()
    {
        using var accountRepo = new SqliteAccountRepository(":memory:");
        var worldManager = new WorldManager();
        using var server = new GameServer(worldManager, accountRepo);

        var session1 = new PeerSession(peerId: 1, peer: null, remoteAddress: "127.0.0.1");
        var session2 = new PeerSession(peerId: 2, peer: null, remoteAddress: "127.0.0.2");

        // 1. Sebelum login, Auth Gate menolak seluruh paket gameplay.
        Assert.False(server.IsGameplayPacketAllowed(session1));

        // 2. Peer 1 register & langsung terautentikasi.
        AuthResponsePacket regResp = server.ProcessRegister(session1, new RegisterRequestPacket("Alice_01", "Pass1234"));
        Assert.True(regResp.Success);
        Assert.True(session1.IsAuthenticated);
        Assert.Equal("Alice_01", session1.Username);
        Assert.True(server.IsGameplayPacketAllowed(session1));
        Assert.Equal(1, server.ActiveAuthenticatedAccountCount);

        // 3. Anti-Double-Login: Peer 2 mencoba login ke akun yang sama saat Peer 1 masih online -> ditolak!
        AuthResponsePacket doubleLogin = server.ProcessLogin(session2, new LoginRequestPacket("alice_01", "Pass1234"));
        Assert.False(doubleLogin.Success);
        Assert.Equal("Akun sedang digunakan di perangkat lain", doubleLogin.ErrorMessage);
        Assert.False(session2.IsAuthenticated);
        Assert.False(server.IsGameplayPacketAllowed(session2));

        // 4. Peer 1 logout bersih -> sesi 1 kembali unauthenticated, lalu Peer 2 dapat login normal.
        AuthResponsePacket logoutResp = server.ProcessLogout(session1);
        Assert.True(logoutResp.Success);
        Assert.False(session1.IsAuthenticated);
        Assert.False(server.IsGameplayPacketAllowed(session1));
        Assert.Equal(0, server.ActiveAuthenticatedAccountCount);

        AuthResponsePacket relogin = server.ProcessLogin(session2, new LoginRequestPacket("alice_01", "Pass1234"));
        Assert.True(relogin.Success);
        Assert.True(session2.IsAuthenticated);
        Assert.Equal(regResp.AccountId, relogin.AccountId);
        Assert.Equal(1, server.ActiveAuthenticatedAccountCount);
    }

    [Fact]
    public void GameServer_AuthRateLimiter_BlocksAfterFiveFailuresIn30Seconds_AndUnblocksAfterWindow()
    {
        DateTime fakeClock = new(2026, 11, 25, 14, 0, 0, DateTimeKind.Utc);
        using var accountRepo = new SqliteAccountRepository(":memory:");
        Assert.True(accountRepo.Register("TargetUser", "CorrectPass1").Success);

        var worldManager = new WorldManager();
        using var server = new GameServer(worldManager, accountRepo, utcNowProvider: () => fakeClock);

        var attacker = new PeerSession(peerId: 10, peer: null, remoteAddress: "192.168.1.55");

        // 5 percobaan login gagal dalam < 30 detik.
        for (int i = 0; i < 5; i++)
        {
            AuthResponsePacket failed = server.ProcessLogin(attacker, new LoginRequestPacket("TargetUser", $"Wrong{i}99"));
            Assert.False(failed.Success);
            fakeClock = fakeClock.AddSeconds(2);
        }

        // Percobaan ke-6 langsung diblokir rate limiter meskipun password benar!
        AuthResponsePacket blocked = server.ProcessLogin(attacker, new LoginRequestPacket("TargetUser", "CorrectPass1"));
        Assert.False(blocked.Success);
        Assert.Contains("30 detik", blocked.ErrorMessage);
        Assert.False(attacker.IsAuthenticated);

        // Setelah melewati jendela 30 detik, login benar kembali diizinkan.
        fakeClock = fakeClock.AddSeconds(31);
        AuthResponsePacket allowed = server.ProcessLogin(attacker, new LoginRequestPacket("TargetUser", "CorrectPass1"));
        Assert.True(allowed.Success);
        Assert.True(attacker.IsAuthenticated);
    }
}
