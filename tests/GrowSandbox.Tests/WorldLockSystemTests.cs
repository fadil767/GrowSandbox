using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Tests;

public class WorldLockSystemTests
{
    [Fact]
    public void DoubleWorldLockPlacement_AndPlacementAboveWhiteDoorOrBedrock_AreRejected()
    {
        var world = new WorldInstance("DOUBLE_LOCK_TEST", WorldGenerator.Generate("DOUBLE_LOCK_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "Alice",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.WorldLock] = 3 }));
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            2,
            "Bob",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.WorldLock] = 2 }));

        // 1. Tolak pemasangan World Lock di atas WhiteDoor (50, 29) atau tepat di atasnya (50, 28).
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 50, 29, TileIds.WorldLock)));
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 50, 28, TileIds.WorldLock)));
        world.Tick(1f / 30f);

        Assert.False(world.IsLocked);
        Assert.Equal(TileIds.WhiteDoor, world.GetTileCopy(50, 29).ForegroundId);
        Assert.Equal(TileIds.Air, world.GetTileCopy(50, 28).ForegroundId);
        Assert.Equal((ushort)3, world.GetPlayerInventoryCount(1, TileIds.WorldLock));

        // 2. Tolak pemasangan World Lock di atas Bedrock (baris 54 tepat di atas bedrock baris 55).
        (float deepX, float deepY) = TileCollision.GetSpawnPosition(52, 54);
        world.EnqueueCommand(new SetTileCommand(52, 54, new Tile(TileIds.Air, TileIds.CaveBackground)));
        world.EnqueueCommand(new SetTileCommand(53, 54, new Tile(TileIds.Air, TileIds.CaveBackground)));
        world.EnqueueCommand(new SimulatedTileActionCommand(
            deepX,
            deepY,
            new TileActionPacket(TileActionKind.PlaceForeground, 53, 54, TileIds.WorldLock),
            ActorName: "Alice"));
        world.Tick(1f / 30f);

        Assert.False(world.IsLocked);
        Assert.Equal(TileIds.Air, world.GetTileCopy(53, 54).ForegroundId);

        // 3. Pemasangan World Lock pertama yang sah di (52, 29) oleh Alice -> berhasil & kurangi inventory 1.
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock)));
        world.Tick(1f / 30f);

        Assert.True(world.IsLocked);
        Assert.Equal("Alice", world.LockOwner);
        Assert.Equal(52, world.LockTileX);
        Assert.Equal(29, world.LockTileY);
        Assert.Equal(TileIds.WorldLock, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal((ushort)2, world.GetPlayerInventoryCount(1, TileIds.WorldLock));

        // 4. Pemasangan World Lock ke-2 oleh Alice maupun Bob -> ditolak dan inventory tidak berkurang.
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 51, 29, TileIds.WorldLock)));
        world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.PlaceForeground, 49, 29, TileIds.WorldLock)));
        world.Tick(1f / 30f);

        Assert.Equal("Alice", world.LockOwner);
        Assert.Equal(TileIds.Air, world.GetTileCopy(51, 29).ForegroundId);
        Assert.Equal(TileIds.Air, world.GetTileCopy(49, 29).ForegroundId);
        Assert.Equal((ushort)2, world.GetPlayerInventoryCount(1, TileIds.WorldLock));
        Assert.Equal((ushort)2, world.GetPlayerInventoryCount(2, TileIds.WorldLock));
    }

    [Fact]
    public void NonAccessPlayer_CannotBreakOrPlace_WhileAccessListPlayerCanBuild_CaseInsensitive()
    {
        var world = new WorldInstance("ACCESS_TEST", WorldGenerator.Generate("ACCESS_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "Alice",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.WorldLock] = 1 }));
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            2,
            "BuilderBob",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.Brick] = 10 }));

        // Alice mengunci world di (52, 29).
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock)));
        world.Tick(1f / 30f);

        // BuilderBob (belum punya akses) gagal menghancurkan blok Dirt (51, 30) dan gagal memasang Brick (51, 29).
        for (int i = 0; i < 3; i++)
        {
            world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.Punch, 51, 30, TileIds.Air)));
        }

        world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.PlaceForeground, 51, 29, TileIds.Brick)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Dirt, world.GetTileCopy(51, 30).ForegroundId);
        Assert.Equal((byte)0, world.GetTileCopy(51, 30).Damage);
        Assert.Equal(TileIds.Air, world.GetTileCopy(51, 29).ForegroundId);
        Assert.Equal((ushort)10, world.GetPlayerInventoryCount(2, TileIds.Brick));

        // Alice menambahkan "builderbob" (huruf kecil berbeda -> case-insensitive!) ke AccessList.
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "builderbob")));
        world.Tick(1f / 30f);

        Assert.True(world.LockData.HasAccess("BuilderBob"));

        // BuilderBob sekarang BERHASIL membangun Brick di (51, 29) dan menghancurkan Dirt di (51, 30).
        world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.PlaceForeground, 51, 29, TileIds.Brick)));
        for (int i = 0; i < 3; i++)
        {
            world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.Punch, 51, 30, TileIds.Air)));
        }

        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Brick, world.GetTileCopy(51, 29).ForegroundId);
        Assert.Equal(TileIds.Air, world.GetTileCopy(51, 30).ForegroundId);
        Assert.Equal((ushort)9, world.GetPlayerInventoryCount(2, TileIds.Brick));

        // Meskipun masuk AccessList, BuilderBob TETAP TIDAK BOLEH menghancurkan blok World Lock milik Alice!
        for (int i = 0; i < 4; i++)
        {
            world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.Punch, 52, 29, TileIds.Air)));
        }

        world.Tick(1f / 30f);

        Assert.True(world.IsLocked);
        Assert.Equal(TileIds.WorldLock, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal((byte)0, world.GetTileCopy(52, 29).Damage);
    }

    [Fact]
    public void PublicMode_AllowsAnyoneToBuild_ButProtectsWorldLockBlock()
    {
        var world = new WorldInstance("PUBLIC_LOCK_TEST", WorldGenerator.Generate("PUBLIC_LOCK_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "OwnerAlice",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.WorldLock] = 1 }));
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            2,
            "GuestCharlie",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.Wood] = 5 }));

        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock)));
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.TogglePublic)));
        world.Tick(1f / 30f);

        Assert.True(world.IsPublicLock);

        // Tamu tanpa AccessList boleh memasang blok biasa saat Public Mode aktif.
        world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.PlaceForeground, 51, 29, TileIds.Wood)));
        // Namun tamu TETAP dilarang menghancurkan World Lock.
        for (int i = 0; i < 4; i++)
        {
            world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.Punch, 52, 29, TileIds.Air)));
        }

        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Wood, world.GetTileCopy(51, 29).ForegroundId);
        Assert.True(world.IsLocked);
        Assert.Equal(TileIds.WorldLock, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal((byte)0, world.GetTileCopy(52, 29).Damage);
    }

    [Fact]
    public void NonOwner_CannotModifyLockConfig_OrPickUpLock_WhileOwnerCanPickUpLock()
    {
        var world = new WorldInstance("CONFIG_AUTH_TEST", WorldGenerator.Generate("CONFIG_AUTH_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "Alice",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.WorldLock] = 1 }));
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            2,
            "Bob",
            spawnX,
            spawnY));

        // Alice pasang lock dan beri akses bangun ke Bob.
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock)));
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "Bob")));
        world.Tick(1f / 30f);

        Assert.Equal((ushort)0, world.GetPlayerInventoryCount(1, TileIds.WorldLock));

        // Bob (non-owner, meski ada di AccessList) gagal mengubah konfigurasi lock sama sekali!
        world.EnqueueCommand(new WorldLockConfigCommand(2, new WorldLockConfigRequestPacket(WorldLockConfigAction.TogglePublic)));
        world.EnqueueCommand(new WorldLockConfigCommand(2, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "Hacker")));
        world.EnqueueCommand(new WorldLockConfigCommand(2, new WorldLockConfigRequestPacket(WorldLockConfigAction.RemoveAccess, "Bob")));
        world.EnqueueCommand(new WorldLockConfigCommand(2, new WorldLockConfigRequestPacket(WorldLockConfigAction.PickUpLock)));
        world.Tick(1f / 30f);

        Assert.True(world.IsLocked);
        Assert.False(world.IsPublicLock);
        Assert.Equal("Alice", world.LockOwner);
        Assert.Single(world.LockData.AccessList);
        Assert.True(world.LockData.HasAccess("Bob"));
        Assert.Equal(TileIds.WorldLock, world.GetTileCopy(52, 29).ForegroundId);

        // Alice (owner) mengambil kembali World Lock lewat PickUpLock -> tile bersih, lock terbuka, +1 World Lock kembali ke inventory!
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.PickUpLock)));
        world.Tick(1f / 30f);

        Assert.False(world.IsLocked);
        Assert.Equal(string.Empty, world.LockOwner);
        Assert.Empty(world.LockData.AccessList);
        Assert.Equal(TileIds.Air, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal((ushort)1, world.GetPlayerInventoryCount(1, TileIds.WorldLock));
    }

    [Fact]
    public void AccessList_EnforcesMax20Players_AndCaseInsensitiveUniqueness()
    {
        var world = new WorldInstance("MAX20_TEST", WorldGenerator.Generate("MAX20_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "OwnerOne",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.WorldLock] = 1 }));
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock)));

        // Tambah 20 nama unik + duplikat beda kapitalisasi + nama ke-21.
        for (int i = 1; i <= 20; i++)
        {
            world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, $"Friend_{i}")));
        }

        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "FRIEND_1")));
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "OwnerOne")));
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "OverflowPlayer_21")));
        world.Tick(1f / 30f);

        Assert.Equal(WorldLockData.MaxAccessListCount, world.LockData.AccessList.Count);
        Assert.False(world.LockData.HasAccess("OverflowPlayer_21"));

        // Cabut 1 nama (case-insensitive) -> slot terbuka kembali untuk nama baru.
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.RemoveAccess, "friend_5")));
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "NewFriend_21")));
        world.Tick(1f / 30f);

        Assert.Equal(WorldLockData.MaxAccessListCount, world.LockData.AccessList.Count);
        Assert.False(world.LockData.HasAccess("Friend_5"));
        Assert.True(world.LockData.HasAccess("NewFriend_21"));
    }

    [Fact]
    public async Task SqlitePersistence_PreservesFullWorldLockData_IsPublic_AndAccessListAcrossRestart()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"growsandbox_wlock_{Guid.NewGuid():N}.db");

        try
        {
            var repo = new SqliteWorldRepository(tempDbPath);
            var manager1 = new WorldManager(repo);
            WorldInstance world = manager1.GetOrCreateWorld("LOCK_PERSIST_FULL");

            (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);
            world.EnqueueCommand(new SimulatedJoinPlayerCommand(
                1,
                "KingOwner",
                spawnX,
                spawnY,
                new Dictionary<ushort, ushort> { [TileIds.WorldLock] = 1 }));
            world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock)));
            world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.TogglePublic)));
            world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "HelperOne")));
            world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "HelperTwo")));
            world.Tick(1f / 30f);

            Task<int> saveTask = manager1.SaveDirtyWorldsAsync();
            while (!saveTask.IsCompleted)
            {
                world.Tick(1f / 30f);
                await Task.Yield();
            }

            Assert.Equal(1, await saveTask);
            world.Tick(1f / 30f);
            Assert.False(world.IsDirty);

            // Restart simulasi: buat SqliteWorldRepository & WorldManager baru.
            var manager2 = new WorldManager(new SqliteWorldRepository(tempDbPath));
            WorldInstance reloaded = manager2.GetOrCreateWorld("LOCK_PERSIST_FULL");

            Assert.True(reloaded.IsLocked);
            Assert.Equal("KingOwner", reloaded.LockOwner);
            Assert.Equal(52, reloaded.LockTileX);
            Assert.Equal(29, reloaded.LockTileY);
            Assert.True(reloaded.IsPublicLock);
            Assert.Equal(2, reloaded.LockData.AccessList.Count);
            Assert.True(reloaded.LockData.HasAccess("HelperOne"));
            Assert.True(reloaded.LockData.HasAccess("HelperTwo"));
            Assert.True(reloaded.CanModifyWorld("helperone"));
            Assert.True(reloaded.CanModifyWorld("Stranger")); // karena IsPublic == true
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(tempDbPath)) File.Delete(tempDbPath);
            if (File.Exists(tempDbPath + "-wal")) File.Delete(tempDbPath + "-wal");
            if (File.Exists(tempDbPath + "-shm")) File.Delete(tempDbPath + "-shm");
        }
    }
}
