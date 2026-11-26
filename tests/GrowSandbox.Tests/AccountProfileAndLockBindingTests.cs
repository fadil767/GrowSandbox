using GrowSandbox.Server.Network;
using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Tests;

public class AccountProfileAndLockBindingTests
{
    [Fact]
    public void FullAccountLifecycle_LoginReceiveItemsAndGems_Disconnect_Relogin_PreservesExactInventoryAndGems()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"growsandbox_profile_{Guid.NewGuid():N}.db");

        try
        {
            // Sesi 1: Register (mendapat Starter Kit: 2 WorldLock, 30 Dirt, 15 Wood, 10 Gems) -> masuk world -> pungut item & gems -> disconnect.
            using (var accountRepo1 = new SqliteAccountRepository(tempDbPath))
            {
                var worldRepo1 = new SqliteWorldRepository(tempDbPath);
                var worldManager1 = new WorldManager(worldRepo1, accountRepo1);
                using var server1 = new GameServer(worldManager1, accountRepo1);

                var peer1 = new PeerSession(peerId: 1, peer: null, remoteAddress: "127.0.0.10");
                AuthResponsePacket regResp = server1.ProcessRegister(peer1, new RegisterRequestPacket("PlayerAlpha", "AlphaPass99"));
                Assert.True(regResp.Success);
                Assert.Equal(PlayerSession.StarterGems, regResp.Gems);
                Assert.Equal("START", regResp.LastWorld);
                Assert.Equal((ushort)2, peer1.Inventory[TileIds.WorldLock]);
                Assert.Equal((ushort)30, peer1.Inventory[TileIds.Dirt]);
                Assert.Equal((ushort)15, peer1.Inventory[TileIds.Wood]);
                Assert.False(peer1.Inventory.ContainsKey(TileIds.Fist));
                Assert.False(peer1.Inventory.ContainsKey(TileIds.Wrench));

                WorldInstance? world = server1.JoinSessionToWorld(peer1, "START");
                Assert.NotNull(world);
                world.Tick(1f / 30f);

                // Spawn drop Stone x12 dan Gem x35 tepat di posisi pemain lalu tick agar dipungut.
                float cx = world.SpawnX + PhysicsConstants.PlayerWidth * 0.5f;
                float cy = world.SpawnY + PhysicsConstants.PlayerHeight * 0.5f;
                world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Rock, 12, cx, cy));
                world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Gem, 35, cx, cy));
                world.Tick(1f / 30f);

                Assert.True(peer1.IsProfileDirty);
                Assert.Equal(PlayerSession.StarterGems + 35, peer1.Gems);
                Assert.Equal((ushort)27, peer1.Inventory[TileIds.Rock]); // 15 starter + 12 pickup

                // Disconnect -> wajib menyimpan profil SEBELUM melepas AccountId aktif.
                server1.DisconnectSession(peer1, "ClientDisconnect");
                Assert.Equal(0, server1.ActiveAuthenticatedAccountCount);
                Assert.False(peer1.IsProfileDirty);
            }

            // Sesi 2 (setelah restart / login ulang): seluruh saldo inventory & gems wajib persis sama!
            using (var accountRepo2 = new SqliteAccountRepository(tempDbPath))
            {
                var worldRepo2 = new SqliteWorldRepository(tempDbPath);
                var worldManager2 = new WorldManager(worldRepo2, accountRepo2);
                using var server2 = new GameServer(worldManager2, accountRepo2);

                var peer2 = new PeerSession(peerId: 2, peer: null, remoteAddress: "127.0.0.11");
                AuthResponsePacket loginResp = server2.ProcessLogin(peer2, new LoginRequestPacket("playeralpha", "AlphaPass99"));

                Assert.True(loginResp.Success);
                Assert.Equal("PlayerAlpha", loginResp.DisplayName);
                Assert.Equal(PlayerSession.StarterGems + 35, loginResp.Gems);
                Assert.Equal(PlayerSession.StarterGems + 35, peer2.Gems);
                Assert.Equal("START", loginResp.LastWorld);
                Assert.Equal((ushort)2, peer2.Inventory[TileIds.WorldLock]);
                Assert.Equal((ushort)30, peer2.Inventory[TileIds.Dirt]);
                Assert.Equal((ushort)15, peer2.Inventory[TileIds.Wood]);
                Assert.Equal((ushort)27, peer2.Inventory[TileIds.Rock]);
                Assert.False(peer2.Inventory.ContainsKey(TileIds.Fist));
                Assert.False(peer2.Inventory.ContainsKey(TileIds.Wrench));
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
    public void WorldLockAccountBinding_ProtectsByAccountId_ValidatesRegisteredAccess_AndPersistsOwnerAccountId()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"growsandbox_acctlock_{Guid.NewGuid():N}.db");

        try
        {
            int accountIdA;
            using (var accountRepo = new SqliteAccountRepository(tempDbPath))
            {
                var worldRepo = new SqliteWorldRepository(tempDbPath);
                var worldManager = new WorldManager(worldRepo, accountRepo);
                using var server = new GameServer(worldManager, accountRepo);

                var peerA = new PeerSession(peerId: 1, peer: null, remoteAddress: "127.0.0.1");
                var peerB = new PeerSession(peerId: 2, peer: null, remoteAddress: "127.0.0.2");

                AuthResponsePacket regA = server.ProcessRegister(peerA, new RegisterRequestPacket("AccountA", "SecretA123"));
                AuthResponsePacket regB = server.ProcessRegister(peerB, new RegisterRequestPacket("AccountB", "SecretB123"));
                Assert.True(regA.Success);
                Assert.True(regB.Success);
                accountIdA = regA.AccountId;

                WorldInstance? world = server.JoinSessionToWorld(peerA, "LOCK_ACCT_WORLD");
                Assert.NotNull(world);
                server.JoinSessionToWorld(peerB, "LOCK_ACCT_WORLD");
                world.Tick(1f / 30f);

                // 1. Akun A memasang World Lock di (52, 29) -> terikat ke OwnerAccountId A & segera tersimpan ke SQLite!
                world.EnqueueCommand(new TileActionCommand(peerA.PeerId, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock)));
                world.Tick(1f / 30f);

                Assert.True(world.IsLocked);
                Assert.Equal(accountIdA, world.LockData.OwnerAccountId);
                Assert.Equal("AccountA", world.LockData.OwnerName);
                Assert.True(world.CanModifyWorld(peerA));
                Assert.True(world.CanConfigureWorldLock(peerA));

                // Transaksi bernilai tinggi (pasang World Lock) wajib langsung tersimpan ke SQLite (2 -> 1).
                AccountRecord? persistedA = accountRepo.GetById(accountIdA);
                Assert.NotNull(persistedA);
                Dictionary<ushort, ushort>? persistedInvA = AccountInventoryCodec.Decode(persistedA.InventoryBlob);
                Assert.NotNull(persistedInvA);
                Assert.Equal((ushort)1, persistedInvA[TileIds.WorldLock]);

                // 2. Sesi penyamar dengan nama sama ("AccountA") namun AccountId berbeda tetap DITOLAK!
                var impersonator = new PeerSession(peerId: 99, peer: null, remoteAddress: "127.0.0.99")
                {
                    IsAuthenticated = true,
                    AccountId = regB.AccountId + 999,
                    Username = "AccountA",
                };
                Assert.False(world.CanModifyWorld(impersonator));
                Assert.False(world.CanConfigureWorldLock(impersonator));

                // 3. Akun B (non-owner & tidak di AccessList) gagal merusak blok dan gagal membuka menu konfigurasi lock milik A.
                Assert.False(world.CanModifyWorld(peerB));
                Assert.False(world.CanConfigureWorldLock(peerB));

                bool? bModalAllowed = null;
                world.EnqueueCommand(new WrenchTileCommand(
                    peerB.PeerId,
                    new WrenchTileRequestPacket(52, 29),
                    decision => bModalAllowed = decision));
                for (int i = 0; i < 3; i++)
                {
                    world.EnqueueCommand(new TileActionCommand(peerB.PeerId, new TileActionPacket(TileActionKind.Punch, 51, 30, TileIds.Air)));
                }

                world.EnqueueCommand(new WorldLockConfigCommand(peerB.PeerId, new WorldLockConfigRequestPacket(WorldLockConfigAction.TogglePublic)));
                world.Tick(1f / 30f);

                Assert.False(bModalAllowed);
                Assert.Equal(TileIds.Dirt, world.GetTileCopy(51, 30).ForegroundId);
                Assert.Equal((byte)0, world.GetTileCopy(51, 30).Damage);
                Assert.False(world.IsPublicLock);

                // 4. Akun A mencoba menambahkan username yang BELUM terdaftar di SQLite -> ditolak!
                world.EnqueueCommand(new WorldLockConfigCommand(peerA.PeerId, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "NotRegistered99")));
                world.Tick(1f / 30f);
                Assert.Empty(world.LockData.AccessList);

                // 5. Akun A menambahkan "accountb" (terdaftar di DB) ke AccessList -> berhasil, lalu B bisa membangun!
                world.EnqueueCommand(new WorldLockConfigCommand(peerA.PeerId, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "accountb")));
                world.Tick(1f / 30f);

                Assert.True(world.LockData.HasAccess("AccountB"));
                Assert.True(world.CanModifyWorld(peerB));
                Assert.False(world.CanConfigureWorldLock(peerB)); // Tetap bukan owner!

                world.EnqueueCommand(new TileActionCommand(peerB.PeerId, new TileActionPacket(TileActionKind.PlaceForeground, 51, 29, TileIds.Brick)));
                world.Tick(1f / 30f);
                Assert.Equal(TileIds.Brick, world.GetTileCopy(51, 29).ForegroundId);

                // 6. Graceful shutdown flush menyimpan world (termasuk OwnerAccountId & AccessList) dan seluruh profil aktif.
                server.FlushAllStateOnShutdown();
            }

            // 7. Muat ulang dari SQLite -> verifikasi OwnerAccountId & AccessList persisten setelah restart.
            using (var accountRepoReload = new SqliteAccountRepository(tempDbPath))
            {
                var worldRepoReload = new SqliteWorldRepository(tempDbPath);
                var managerReload = new WorldManager(worldRepoReload, accountRepoReload);
                WorldInstance reloaded = managerReload.GetOrCreateWorld("LOCK_ACCT_WORLD");

                Assert.True(reloaded.IsLocked);
                Assert.Equal(accountIdA, reloaded.LockData.OwnerAccountId);
                Assert.Equal("AccountA", reloaded.LockData.OwnerName);
                Assert.True(reloaded.LockData.HasAccess("AccountB"));
                Assert.Equal(TileIds.Brick, reloaded.GetTileCopy(51, 29).ForegroundId);
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
    public void PeriodicAutoSave_PersistsDirtyProfiles_AndClearsDirtyFlag()
    {
        using var accountRepo = new SqliteAccountRepository(":memory:");
        var worldManager = new WorldManager(accountRepository: accountRepo);
        using var server = new GameServer(worldManager, accountRepo);

        var peer = new PeerSession(peerId: 1, peer: null, remoteAddress: "127.0.0.1");
        AuthResponsePacket reg = server.ProcessRegister(peer, new RegisterRequestPacket("AutoSaver", "SavePass123"));
        Assert.True(reg.Success);

        WorldInstance? world = server.JoinSessionToWorld(peer, "START");
        Assert.NotNull(world);
        world.Tick(1f / 30f);

        // Pungut Gem x20 -> menandai IsProfileDirty = true
        float cx = world.SpawnX + PhysicsConstants.PlayerWidth * 0.5f;
        float cy = world.SpawnY + PhysicsConstants.PlayerHeight * 0.5f;
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Gem, 20, cx, cy));
        world.Tick(1f / 30f);

        Assert.True(peer.IsProfileDirty);

        // Jalankan siklus auto-save profil periodik
        int savedProfiles = server.SaveDirtyProfiles();
        Assert.Equal(1, savedProfiles);
        Assert.False(peer.IsProfileDirty);

        AccountRecord? dbRecord = accountRepo.GetById(reg.AccountId);
        Assert.NotNull(dbRecord);
        Assert.Equal(PlayerSession.StarterGems + 20, dbRecord.Gems);
    }
}
