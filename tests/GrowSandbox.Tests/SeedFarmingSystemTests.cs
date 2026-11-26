using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Tests;

public class SeedFarmingSystemTests
{
    [Fact]
    public void ItemCatalog_RegistersAllFiveFarmableSeedsWithExpectedGrowTimes()
    {
        Assert.True(ItemCatalog.TryGetSeedProperties(TileIds.DirtSeed, out SeedProperties dirt));
        Assert.Equal(TileIds.Dirt, dirt.BlockId);
        Assert.Equal(15, dirt.GrowTimeSeconds);

        Assert.True(ItemCatalog.TryGetSeedProperties(TileIds.WoodSeed, out SeedProperties wood));
        Assert.Equal(TileIds.Wood, wood.BlockId);
        Assert.Equal(25, wood.GrowTimeSeconds);

        Assert.True(ItemCatalog.TryGetSeedProperties(TileIds.RockSeed, out SeedProperties rock));
        Assert.Equal(TileIds.Rock, rock.BlockId);
        Assert.Equal(30, rock.GrowTimeSeconds);

        Assert.True(ItemCatalog.TryGetSeedProperties(TileIds.BrickSeed, out SeedProperties brick));
        Assert.Equal(TileIds.Brick, brick.BlockId);
        Assert.Equal(45, brick.GrowTimeSeconds);

        Assert.True(ItemCatalog.TryGetSeedProperties(TileIds.GlassSeed, out SeedProperties glass));
        Assert.Equal(TileIds.Glass, glass.BlockId);
        Assert.Equal(60, glass.GrowTimeSeconds);

        // Tile pohon wajib non-solid agar pemain dapat berjalan melewatinya.
        Assert.False(ItemCatalog.IsSolid(TileIds.Tree));
    }

    [Fact]
    public void PlantSeed_InEmptyAirOrAboveAnotherTree_IsRejected_AndSucceedsOnSolidFloor()
    {
        DateTime fakeNow = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        var world = new WorldInstance("PLANT_RULE_TEST", WorldGenerator.Generate("PLANT_RULE_TEST"), utcNowProvider: () => fakeNow);
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "Farmer",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.DirtSeed] = 3 }));

        // 1. Tolak penanaman seed di udara kosong (52, 27) karena (52, 28) adalah Air (bukan lantai solid).
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 27, TileIds.DirtSeed)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Air, world.GetTileCopy(52, 27).ForegroundId);
        Assert.Equal(0, world.ActiveTreeCount);
        Assert.Equal((ushort)3, world.GetPlayerInventoryCount(1, TileIds.DirtSeed));

        // 2. Tanam seed di (52, 29) di atas blok Dirt solid (52, 30) -> berhasil!
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.DirtSeed)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Tree, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal(1, world.ActiveTreeCount);
        Assert.Equal((ushort)2, world.GetPlayerInventoryCount(1, TileIds.DirtSeed));
        Assert.True(world.TryGetTree(52, 29, out TreeData tree));
        Assert.Equal(fakeNow.Ticks, tree.PlantedUtcTicks);
        Assert.Equal(fakeNow.AddSeconds(15).Ticks, tree.ReadyUtcTicks);
        Assert.InRange(tree.FruitCount, (byte)2, (byte)4);

        // 3. Tolak penanaman seed tepat di atas pohon lain (52, 28), karena (52, 29) adalah Tree (non-solid).
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 28, TileIds.DirtSeed)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Air, world.GetTileCopy(52, 28).ForegroundId);
        Assert.Equal(1, world.ActiveTreeCount);
        Assert.Equal((ushort)2, world.GetPlayerInventoryCount(1, TileIds.DirtSeed));
    }

    [Fact]
    public void Tree_CannotBeHarvestedBeforeReadyUtcTicks_AndYieldsFruitDropsAfterReady()
    {
        DateTime fakeNow = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        var world = new WorldInstance("HARVEST_TIME_TEST", WorldGenerator.Generate("HARVEST_TIME_TEST"), utcNowProvider: () => fakeNow);
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "Harvester",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.RockSeed] = 1 }));

        // Tanam Rock Seed (waktu tumbuh 30 detik) di (52, 29).
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.RockSeed)));
        world.Tick(1f / 30f);

        Assert.True(world.TryGetTree(52, 29, out TreeData planted));
        Assert.Equal(0, planted.GetGrowthStage(fakeNow.Ticks));

        // Coba pukul/panen saat baru 15 detik (Tahap 1, BELUM mencapai ReadyUtcTicks 30 detik).
        fakeNow = fakeNow.AddSeconds(15);
        Assert.Equal(1, planted.GetGrowthStage(fakeNow.Ticks));
        Assert.False(planted.IsReady(fakeNow.Ticks));

        for (int i = 0; i < 3; i++)
        {
            world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.Punch, 52, 29, TileIds.Air)));
        }

        world.Tick(1f / 30f);

        // Pohon tidak boleh hancur dan belum menghasilkan buah sebelum matang.
        Assert.Equal(TileIds.Tree, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal(1, world.ActiveTreeCount);
        Assert.Equal(0, world.ActiveDropCount);
        Assert.Equal(0, world.GetTotalFloorItemCount(TileIds.Rock));

        // Cek Tahap 2 pada 25 detik (66-99%).
        fakeNow = fakeNow.AddSeconds(10);
        Assert.Equal(2, planted.GetGrowthStage(fakeNow.Ticks));
        Assert.False(planted.IsReady(fakeNow.Ticks));

        // Lewati ReadyUtcTicks (30+ detik -> Tahap 3 / Ready) lalu pukul untuk panen!
        fakeNow = fakeNow.AddSeconds(6);
        Assert.Equal(3, planted.GetGrowthStage(fakeNow.Ticks));
        Assert.True(planted.IsReady(fakeNow.Ticks));

        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.Punch, 52, 29, TileIds.Air)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Air, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal(0, world.ActiveTreeCount);
        Assert.Equal(planted.FruitCount, world.GetTotalFloorItemCount(TileIds.Rock));
        Assert.True(world.ActiveDropCount >= 1);
    }

    [Fact]
    public void WorldLock_BlocksUnauthorizedPlantingAndHarvesting_AllowsAccessList()
    {
        DateTime fakeNow = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
        var world = new WorldInstance("FARM_LOCK_TEST", WorldGenerator.Generate("FARM_LOCK_TEST"), utcNowProvider: () => fakeNow);
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "OwnerAlice",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.WorldLock] = 1, [TileIds.DirtSeed] = 2 }));
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            2,
            "IntruderBob",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.DirtSeed] = 2 }));

        // Alice kunci world dan tanam Dirt Tree di (51, 29).
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock)));
        world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 51, 29, TileIds.DirtSeed)));
        world.Tick(1f / 30f);

        // Lewati 20 detik agar pohon matang, lalu Bob tanpa akses coba tanam & panen -> harus ditolak!
        fakeNow = fakeNow.AddSeconds(20);
        world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.PlaceForeground, 49, 29, TileIds.DirtSeed)));
        world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.Punch, 51, 29, TileIds.Air)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Air, world.GetTileCopy(49, 29).ForegroundId);
        Assert.Equal(TileIds.Tree, world.GetTileCopy(51, 29).ForegroundId);
        Assert.Equal((ushort)2, world.GetPlayerInventoryCount(2, TileIds.DirtSeed));

        // Setelah diberi akses oleh Alice, Bob berhasil menanam dan memanen.
        world.EnqueueCommand(new WorldLockConfigCommand(1, new WorldLockConfigRequestPacket(WorldLockConfigAction.AddAccess, "IntruderBob")));
        world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.PlaceForeground, 49, 29, TileIds.DirtSeed)));
        world.EnqueueCommand(new TileActionCommand(2, new TileActionPacket(TileActionKind.Punch, 51, 29, TileIds.Air)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Tree, world.GetTileCopy(49, 29).ForegroundId);
        Assert.Equal(TileIds.Air, world.GetTileCopy(51, 29).ForegroundId);
        int totalDirtHarvested =
            world.GetPlayerInventoryCount(1, TileIds.Dirt) +
            world.GetPlayerInventoryCount(2, TileIds.Dirt) +
            world.GetTotalFloorItemCount(TileIds.Dirt);
        Assert.InRange(totalDirtHarvested, 2, 4);
    }

    [Fact]
    public async Task SqlitePersistence_BinaryV3_PersistsTreesAcrossRestart_AndAutoMaturesByUtcTimestamp()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"growsandbox_trees_{Guid.NewGuid():N}.db");

        try
        {
            DateTime serverClock = new(2026, 11, 25, 10, 0, 0, DateTimeKind.Utc);
            var repo = new SqliteWorldRepository(tempDbPath);
            var manager1 = new WorldManager(repo, utcNowProvider: () => serverClock);
            WorldInstance world = manager1.GetOrCreateWorld("FARM_PERSIST");

            (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);
            world.EnqueueCommand(new SimulatedJoinPlayerCommand(
                1,
                "FarmerOne",
                spawnX,
                spawnY,
                new Dictionary<ushort, ushort> { [TileIds.DirtSeed] = 1, [TileIds.GlassSeed] = 1 }));

            world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 51, 29, TileIds.DirtSeed)));
            world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.GlassSeed)));
            world.Tick(1f / 30f);

            Assert.Equal(2, world.ActiveTreeCount);
            Assert.True(world.IsDirty);

            Task<int> saveTask = manager1.SaveDirtyWorldsAsync();
            while (!saveTask.IsCompleted)
            {
                world.Tick(1f / 30f);
                await Task.Yield();
            }

            Assert.Equal(1, await saveTask);
            world.Tick(1f / 30f);
            Assert.False(world.IsDirty);

            // Simulasikan server mati selama 25 detik: Dirt Tree (15s) otomatis MATANG, Glass Tree (60s) masih tumbuh.
            DateTime restartClock = serverClock.AddSeconds(25);
            var manager2 = new WorldManager(new SqliteWorldRepository(tempDbPath), utcNowProvider: () => restartClock);
            WorldInstance reloaded = manager2.GetOrCreateWorld("FARM_PERSIST");

            Assert.Equal(2, reloaded.ActiveTreeCount);
            Assert.True(reloaded.TryGetTree(51, 29, out TreeData dirtTree));
            Assert.True(reloaded.TryGetTree(52, 29, out TreeData glassTree));
            Assert.True(dirtTree.IsReady(restartClock.Ticks));
            Assert.False(glassTree.IsReady(restartClock.Ticks));

            // Pukul Dirt Tree setelah restart -> langsung panen 2..4 Dirt!
            reloaded.EnqueueCommand(new SimulatedTileActionCommand(
                spawnX,
                spawnY,
                new TileActionPacket(TileActionKind.Punch, 51, 29, TileIds.Air)));
            reloaded.Tick(1f / 30f);

            Assert.Equal(TileIds.Air, reloaded.GetTileCopy(51, 29).ForegroundId);
            Assert.Equal(1, reloaded.ActiveTreeCount);
            Assert.Equal(dirtTree.FruitCount, reloaded.GetTotalFloorItemCount(TileIds.Dirt));
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
