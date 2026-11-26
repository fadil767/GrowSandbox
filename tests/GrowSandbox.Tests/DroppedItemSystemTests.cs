using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Tests;

public class DroppedItemSystemTests
{
    [Fact]
    public void TwoPlayersOnSameDrop_SameTick_OnlyOneReceivesItem_NoDuplication()
    {
        var world = new WorldInstance("ANTIDUP_TEST", WorldGenerator.Generate("ANTIDUP_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);
        float centerX = spawnX + PhysicsConstants.PlayerWidth * 0.5f;
        float centerY = spawnY + PhysicsConstants.PlayerHeight * 0.5f;

        // Dua pemain berdiri pada koordinat yang SAMA PERSIS tepat di atas 1 DroppedItem.
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(1, "PlayerA", spawnX, spawnY));
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(2, "PlayerB", spawnX, spawnY));
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Stone, 5, centerX, centerY));

        // Eksekusi satu tick serial: kedua pemain bersaing di tick yang sama.
        world.Tick(1f / 30f);

        ushort p1Stone = world.GetPlayerInventoryCount(1, TileIds.Stone);
        ushort p2Stone = world.GetPlayerInventoryCount(2, TileIds.Stone);
        int floorStone = world.GetTotalFloorItemCount(TileIds.Stone);

        // HANYA 1 pemain yang menerima item dan tidak ada duplikasi di seluruh dunia.
        Assert.True((p1Stone == 5 && p2Stone == 0) || (p1Stone == 0 && p2Stone == 5));
        Assert.Equal(0, world.ActiveDropCount);
        Assert.Equal(0, floorStone);
        Assert.Equal(5, p1Stone + p2Stone + floorStone);

        // Tick tambahan tidak boleh memunculkan atau menduplikasi item lagi.
        world.Tick(1f / 30f);
        Assert.Equal(5,
            world.GetPlayerInventoryCount(1, TileIds.Stone) +
            world.GetPlayerInventoryCount(2, TileIds.Stone) +
            world.GetTotalFloorItemCount(TileIds.Stone));
    }

    [Fact]
    public void TwoPlayersOnSameDrop_NearInventoryCap200_PartialPickupConservesExactTotal()
    {
        var world = new WorldInstance("PARTIAL_CAP_TEST", WorldGenerator.Generate("PARTIAL_CAP_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);
        float centerX = spawnX + PhysicsConstants.PlayerWidth * 0.5f;
        float centerY = spawnY + PhysicsConstants.PlayerHeight * 0.5f;

        // Player 1 sudah punya 198 Dirt (kapasitas maks 200 -> sisa ruang 2).
        // Player 2 punya 10 Dirt. Keduanya di atas 1 drop berisi 5 Dirt pada tick yang sama.
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "NearFullPlayer",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.Dirt] = 198 }));
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            2,
            "SecondPlayer",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.Dirt] = 10 }));
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Dirt, 5, centerX, centerY));

        world.Tick(1f / 30f);

        ushort p1Dirt = world.GetPlayerInventoryCount(1, TileIds.Dirt);
        ushort p2Dirt = world.GetPlayerInventoryCount(2, TileIds.Dirt);
        int floorDirt = world.GetTotalFloorItemCount(TileIds.Dirt);

        Assert.Equal((ushort)200, p1Dirt);
        Assert.Equal((ushort)13, p2Dirt);
        Assert.Equal(0, floorDirt);
        Assert.Equal(0, world.ActiveDropCount);
        Assert.Equal(213, p1Dirt + p2Dirt + floorDirt);
    }

    [Fact]
    public void SinglePlayerNearInventoryCap200_LeavesRemainderOnFloorWithSameDropId()
    {
        var world = new WorldInstance("SINGLE_CAP_TEST", WorldGenerator.Generate("SINGLE_CAP_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);
        float centerX = spawnX + PhysicsConstants.PlayerWidth * 0.5f;
        float centerY = spawnY + PhysicsConstants.PlayerHeight * 0.5f;

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "Collector",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.Wood] = 197 }));
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Wood, 10, centerX, centerY));

        world.Tick(1f / 30f);

        Assert.Equal((ushort)200, world.GetPlayerInventoryCount(1, TileIds.Wood));
        Assert.Equal(1, world.ActiveDropCount);
        DroppedItem remaining = Assert.Single(world.GetDropsSnapshot());
        Assert.Equal(1u, remaining.DropId);
        Assert.Equal(TileIds.Wood, remaining.ItemId);
        Assert.Equal((ushort)7, remaining.Count);
        Assert.Equal(207, world.GetPlayerInventoryCount(1, TileIds.Wood) + world.GetTotalFloorItemCount(TileIds.Wood));
    }

    [Fact]
    public void AutoStacking_MergesSameItemWithin1Point2Tiles_UpTo200()
    {
        var world = new WorldInstance("AUTOSTACK_TEST", WorldGenerator.Generate("AUTOSTACK_TEST"));

        // Drop 1: 120 Dirt di (500, 944)
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Dirt, 120, 500f, 944f));
        // Drop 2: 60 Dirt berjarak 30 px (<= 38.4 px = 1.2 tile), total 180 <= 200 -> wajib digabung ke Drop 1
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Dirt, 60, 530f, 944f));
        world.Tick(1f / 30f);

        Assert.Equal(1, world.ActiveDropCount);
        DroppedItem merged = Assert.Single(world.GetDropsSnapshot());
        Assert.Equal(1u, merged.DropId);
        Assert.Equal((ushort)180, merged.Count);

        // Drop 3: 50 Dirt berjarak 20 px, namun 180 + 50 = 230 > 200 -> wajib buat drop baru
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Dirt, 50, 520f, 944f));
        // Drop 4: 10 Dirt berjarak 100 px (> 1.2 tile) -> wajib buat drop terpisah
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Dirt, 10, 600f, 944f));
        // Drop 5: 15 Stone di lokasi yang sama -> ItemId berbeda, wajib drop terpisah
        world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Stone, 15, 500f, 944f));
        world.Tick(1f / 30f);

        Assert.Equal(4, world.ActiveDropCount);
        Assert.Equal(240, world.GetTotalFloorItemCount(TileIds.Dirt));
        Assert.Equal(15, world.GetTotalFloorItemCount(TileIds.Stone));
    }

    [Fact]
    public void DropItemRequest_RejectsInvalidItemsAndNegativeCounts_AndEnforces045sDropperCooldown()
    {
        var world = new WorldInstance("DROPREQ_TEST", WorldGenerator.Generate("DROPREQ_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "Dropper",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.Brick] = 10 },
            Gems: 50,
            FacingLeft: false));
        world.Tick(1f / 30f);

        // Tolak Fist, Wrench, Gem, Bedrock, jumlah 0, jumlah negatif, dan jumlah melebihi stok.
        world.EnqueueCommand(new DropItemRequestCommand(1, new DropItemRequestPacket(TileIds.Fist, 1)));
        world.EnqueueCommand(new DropItemRequestCommand(1, new DropItemRequestPacket(TileIds.Wrench, 1)));
        world.EnqueueCommand(new DropItemRequestCommand(1, new DropItemRequestPacket(TileIds.Gem, 5)));
        world.EnqueueCommand(new DropItemRequestCommand(1, new DropItemRequestPacket(TileIds.Bedrock, 1)));
        world.EnqueueCommand(new DropItemRequestCommand(1, new DropItemRequestPacket(TileIds.Brick, 0)));
        world.EnqueueCommand(new DropItemRequestCommand(1, new DropItemRequestPacket(TileIds.Brick, -3)));
        world.EnqueueCommand(new DropItemRequestCommand(1, new DropItemRequestPacket(TileIds.Brick, 99)));
        world.Tick(1f / 30f);

        Assert.Equal(0, world.ActiveDropCount);
        Assert.Equal((ushort)10, world.GetPlayerInventoryCount(1, TileIds.Brick));
        Assert.Equal(50, world.GetPlayerGems(1));

        // Permintaan sah: buang 3 Brick -> stok dipotong dulu jadi 7, lalu muncul 1 tile di depan kanan.
        world.EnqueueCommand(new DropItemRequestCommand(1, new DropItemRequestPacket(TileIds.Brick, 3)));
        world.Tick(1f / 30f);

        Assert.Equal((ushort)7, world.GetPlayerInventoryCount(1, TileIds.Brick));
        Assert.Equal(1, world.ActiveDropCount);
        DroppedItem dropped = Assert.Single(world.GetDropsSnapshot());
        Assert.Equal(TileIds.Brick, dropped.ItemId);
        Assert.Equal((ushort)3, dropped.Count);
        Assert.True(dropped.X > spawnX + PhysicsConstants.PlayerWidth * 0.5f);

        // Gerakkan pemain tepat di atas item yang baru ia buang selama cooldown < 0.45 detik.
        float targetPlayerX = dropped.X - PhysicsConstants.PlayerWidth * 0.5f;
        world.EnqueueCommand(new PlayerStateCommand(
            1,
            new PlayerStatePacket(1, 1u, targetPlayerX, spawnY, 0f, 0f, true, false)));
        world.Tick(0.20f);

        // Belum boleh tersedot kembali karena masih dalam cooldown 0.45 detik.
        Assert.Equal((ushort)7, world.GetPlayerInventoryCount(1, TileIds.Brick));
        Assert.Equal(1, world.ActiveDropCount);

        // Setelah melewati 0.45 detik, pemain dapat memungutnya kembali dengan total konstan = 10.
        world.Tick(0.30f);
        Assert.Equal((ushort)10, world.GetPlayerInventoryCount(1, TileIds.Brick));
        Assert.Equal(0, world.ActiveDropCount);
    }

    [Fact]
    public void BlockBreak_SpawnsDropOnFloor_WithoutDirectlyAddingToPlayerInventory()
    {
        var world = new WorldInstance("BREAK_DROP_TEST", WorldGenerator.Generate("BREAK_DROP_TEST"));
        (float spawnX, float spawnY) = TileCollision.GetSpawnPosition(50, 29);

        // Tempatkan pemain jauh dari lokasi jatuh Dirt (52, 30) namun masih dalam jangkauan pukul 3 tile.
        world.EnqueueCommand(new SimulatedJoinPlayerCommand(
            1,
            "Miner",
            spawnX,
            spawnY,
            new Dictionary<ushort, ushort> { [TileIds.Dirt] = 5 }));

        for (int i = 0; i < 3; i++)
        {
            world.EnqueueCommand(new TileActionCommand(1, new TileActionPacket(TileActionKind.Punch, 52, 30, TileIds.Air)));
        }

        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Air, world.GetTileCopy(52, 30).ForegroundId);
        // Inventory tidak bertambah langsung; Dirt wajib jatuh ke lantai sebagai DroppedItem.
        Assert.Equal((ushort)5, world.GetPlayerInventoryCount(1, TileIds.Dirt));
        Assert.Equal(1, world.GetTotalFloorItemCount(TileIds.Dirt));
    }

    [Fact]
    public async Task SqlitePersistence_BinaryV2_PersistsDroppedItemsAcrossRestart_AndFallsBackSafelyForLegacyV1()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"growsandbox_drops_{Guid.NewGuid():N}.db");

        try
        {
            var repo = new SqliteWorldRepository(tempDbPath);
            var manager1 = new WorldManager(repo);
            WorldInstance world = manager1.GetOrCreateWorld("DROP_PERSIST");

            world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Wood, 14, 1520.25f, 940.5f));
            world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Gem, 7, 1680.75f, 938.0f));
            world.Tick(0.5f);

            Assert.True(world.IsDirty);
            Assert.Equal(2, world.ActiveDropCount);
            uint expectedNextId = world.NextDropId;

            Task<int> saveTask = manager1.SaveDirtyWorldsAsync();
            while (!saveTask.IsCompleted)
            {
                world.Tick(1f / 30f);
                await Task.Yield();
            }

            Assert.Equal(1, await saveTask);
            world.Tick(1f / 30f);
            Assert.False(world.IsDirty);

            // Muat ulang dari SQLite (simulasi server restart)
            var manager2 = new WorldManager(new SqliteWorldRepository(tempDbPath));
            WorldInstance reloaded = manager2.GetOrCreateWorld("DROP_PERSIST");

            Assert.Equal(2, reloaded.ActiveDropCount);
            Assert.Equal(expectedNextId, reloaded.NextDropId);
            Assert.Equal(14, reloaded.GetTotalFloorItemCount(TileIds.Wood));
            Assert.Equal(7, reloaded.GetTotalFloorItemCount(TileIds.Gem));

            DroppedItem[] reloadedDrops = reloaded.GetDropsSnapshot();
            Assert.Contains(reloadedDrops, d => d.ItemId == TileIds.Wood && d.Count == 14 && MathF.Abs(d.X - 1520.25f) < 0.01f);
            Assert.Contains(reloadedDrops, d => d.ItemId == TileIds.Gem && d.Count == 7 && MathF.Abs(d.X - 1680.75f) < 0.01f);

            // Pastikan ID baru setelah restart tetap monotonik tanpa bertabrakan dengan ID lama.
            reloaded.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Brick, 2, 1800f, 940f));
            reloaded.Tick(1f / 30f);
            Assert.Contains(reloaded.GetDropsSnapshot(), d => d.ItemId == TileIds.Brick && d.DropId == expectedNextId);

            // Uji fallback aman untuk data lama (blob v1 = raw Brotli Tile[6000] tanpa header GSW2).
            Tile[] legacyTiles = WorldGenerator.Generate("LEGACY_V1");
            legacyTiles[Tile.GetIndex(42, 29)] = new Tile(TileIds.Brick, TileIds.Air);
            byte[] legacyRawBrotli = TileCompression.CompressBrotli(legacyTiles);
            Assert.False(WorldSnapshotBlobCodec.IsV2Blob(legacyRawBrotli));

            repo.SaveWorldBlob("LEGACY_V1", legacyRawBrotli);
            PersistedWorldData? loadedLegacy = repo.LoadWorldData("LEGACY_V1");
            Assert.NotNull(loadedLegacy);
            Assert.Equal(TileIds.Brick, loadedLegacy.Value.Tiles[Tile.GetIndex(42, 29)].ForegroundId);
            Assert.Empty(loadedLegacy.Value.Drops);
            Assert.Equal(1u, loadedLegacy.Value.NextDropId);
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
