using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Tests;

public class BlockInteractionAndPersistenceTests
{
    [Fact]
    public void ReachValidation_EnforcesMaxReachTiles()
    {
        (float playerX, float playerY) = TileCollision.GetSpawnPosition(50, 29);

        Assert.True(TileCollision.IsWithinReach(playerX, playerY, 53, 29));
        Assert.True(TileCollision.IsWithinReach(playerX, playerY, 50, 32));

        Assert.False(TileCollision.IsWithinReach(playerX, playerY, 54, 29));
        Assert.False(TileCollision.IsWithinReach(playerX, playerY, 50, 34));
    }

    [Fact]
    public void WorldGenerator_FillsUndergroundWithCaveBackground_AndPlacesWhiteDoorAboveBedrock()
    {
        Tile[] tiles = WorldGenerator.Generate("GEN_CHECK");

        // Seluruh baris bawah tanah (30..59) wajib memiliki CaveBackground konsisten.
        for (int y = WorldGenerator.SurfaceRow; y <= WorldGenerator.BedrockEndRow; y++)
        {
            for (int x = 0; x < WorldConstants.Width; x++)
            {
                Assert.Equal(TileIds.CaveBackground, tiles[Tile.GetIndex(x, y)].BackgroundId);
            }
        }

        // WhiteDoor berada tepat di (spawnX, surfaceY - 1) dan Bedrock di bawahnya (spawnX, surfaceY).
        Tile door = tiles[Tile.GetIndex(WorldGenerator.SpawnTileX, WorldGenerator.SpawnTileY)];
        Tile underDoor = tiles[Tile.GetIndex(WorldGenerator.SpawnTileX, WorldGenerator.SurfaceRow)];

        Assert.Equal(TileIds.WhiteDoor, door.ForegroundId);
        Assert.False(ItemCatalog.IsSolid(door.ForegroundId));
        Assert.Equal(TileIds.Bedrock, underDoor.ForegroundId);
        Assert.Equal(TileIds.CaveBackground, underDoor.BackgroundId);
    }

    [Fact]
    public void PunchWhiteDoorAndSpawnBedrock_AreIndestructible()
    {
        var world = new WorldInstance("DOOR_LOCK_TEST", WorldGenerator.Generate("DOOR_LOCK_TEST"));
        (float playerX, float playerY) = TileCollision.GetSpawnPosition(WorldGenerator.SpawnTileX, WorldGenerator.SpawnTileY);

        for (int i = 0; i < 5; i++)
        {
            world.EnqueueCommand(new SimulatedTileActionCommand(
                playerX,
                playerY,
                new TileActionPacket(TileActionKind.Punch, WorldGenerator.SpawnTileX, WorldGenerator.SpawnTileY, TileIds.Air)));
            world.EnqueueCommand(new SimulatedTileActionCommand(
                playerX,
                playerY,
                new TileActionPacket(TileActionKind.Punch, WorldGenerator.SpawnTileX, WorldGenerator.SurfaceRow, TileIds.Air)));
        }

        world.Tick(1f / 30f);

        Assert.Equal(TileIds.WhiteDoor, world.GetTileCopy(WorldGenerator.SpawnTileX, WorldGenerator.SpawnTileY).ForegroundId);
        Assert.Equal((byte)0, world.GetTileCopy(WorldGenerator.SpawnTileX, WorldGenerator.SpawnTileY).Damage);
        Assert.Equal(TileIds.Bedrock, world.GetTileCopy(WorldGenerator.SpawnTileX, WorldGenerator.SurfaceRow).ForegroundId);
        Assert.Equal((byte)0, world.GetTileCopy(WorldGenerator.SpawnTileX, WorldGenerator.SurfaceRow).Damage);
        Assert.False(world.IsDirty);
    }

    [Fact]
    public void PunchSurfaceDirt_ThreeHitsBreakForegroundToAir_KeepsCaveBackground_AndMarksWorldDirty()
    {
        var world = new WorldInstance("PUNCH_TEST", WorldGenerator.Generate("PUNCH_TEST"));
        (float playerX, float playerY) = TileCollision.GetSpawnPosition(50, 29);

        const int targetX = 51;
        const int targetY = 30; // Baris 30 adalah Dirt dengan CaveBackground

        Assert.False(world.IsDirty);
        Assert.Equal(TileIds.Dirt, world.GetTileCopy(targetX, targetY).ForegroundId);
        Assert.Equal(TileIds.CaveBackground, world.GetTileCopy(targetX, targetY).BackgroundId);
        Assert.Equal(0, world.ActiveDropCount);

        // Pukulan ke-1: Damage = 1
        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.Punch, targetX, targetY, TileIds.Air)));
        world.Tick(1f / 30f);

        Tile afterHit1 = world.GetTileCopy(targetX, targetY);
        Assert.Equal(TileIds.Dirt, afterHit1.ForegroundId);
        Assert.Equal((byte)1, afterHit1.Damage);
        Assert.True(world.IsDirty);

        // Pukulan ke-2: Damage = 2
        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.Punch, targetX, targetY, TileIds.Air)));
        world.Tick(1f / 30f);

        Tile afterHit2 = world.GetTileCopy(targetX, targetY);
        Assert.Equal(TileIds.Dirt, afterHit2.ForegroundId);
        Assert.Equal((byte)2, afterHit2.Damage);

        // Pukulan ke-3: ForegroundId menjadi 0 (Air), CaveBackground tetap utuh (bukan langit bocor), dan drop muncul.
        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.Punch, targetX, targetY, TileIds.Air)));
        world.Tick(1f / 30f);

        Tile afterHit3 = world.GetTileCopy(targetX, targetY);
        Assert.Equal(TileIds.Air, afterHit3.ForegroundId);
        Assert.Equal(TileIds.CaveBackground, afterHit3.BackgroundId);
        Assert.Equal((byte)0, afterHit3.Damage);
        Assert.True(world.ActiveDropCount >= 1);
    }

    [Fact]
    public void PunchUndergroundDirt_BreaksForegroundOnly_PreservingCaveBackgroundUntilSeparatePunches()
    {
        Assert.Equal(WorldConstants.SurfaceY, WorldGenerator.SurfaceRow);

        var world = new WorldInstance("UNDERGROUND_BG_TEST", WorldGenerator.Generate("UNDERGROUND_BG_TEST"));
        const int targetX = 52;
        int deepY = WorldConstants.SurfaceY + 2; // Baris 32 (bawah tanah)
        (float playerX, float playerY) = TileCollision.GetSpawnPosition(targetX, WorldConstants.SurfaceY + 1);

        Assert.Equal(TileIds.Dirt, world.GetTileCopy(targetX, deepY).ForegroundId);
        Assert.Equal(TileIds.CaveBackground, world.GetTileCopy(targetX, deepY).BackgroundId);

        // 3 pukulan memecah Foreground Dirt saja; CaveBackground wajib tetap utuh dan Damage kembali 0!
        for (int i = 0; i < 3; i++)
        {
            world.EnqueueCommand(new SimulatedTileActionCommand(
                playerX,
                playerY,
                new TileActionPacket(TileActionKind.Punch, targetX, deepY, TileIds.Air)));
        }

        world.Tick(1f / 30f);

        Tile afterForegroundBreak = world.GetTileCopy(targetX, deepY);
        Assert.Equal(TileIds.Air, afterForegroundBreak.ForegroundId);
        Assert.Equal(TileIds.CaveBackground, afterForegroundBreak.BackgroundId);
        Assert.Equal((byte)0, afterForegroundBreak.Damage);

        // Pukulan ke-4 baru mulai merusak CaveBackground secara terpisah.
        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.Punch, targetX, deepY, TileIds.Air)));
        world.Tick(1f / 30f);

        Tile afterOneBgHit = world.GetTileCopy(targetX, deepY);
        Assert.Equal(TileIds.Air, afterOneBgHit.ForegroundId);
        Assert.Equal(TileIds.CaveBackground, afterOneBgHit.BackgroundId);
        Assert.Equal((byte)1, afterOneBgHit.Damage);
    }

    [Fact]
    public void PunchBedrock_IsRejected_AndWorldRemainsClean()
    {
        var world = new WorldInstance("BEDROCK_TEST", WorldGenerator.Generate("BEDROCK_TEST"));
        (float playerX, float playerY) = TileCollision.GetSpawnPosition(50, 54);

        for (int i = 0; i < 5; i++)
        {
            world.EnqueueCommand(new SimulatedTileActionCommand(
                playerX,
                playerY,
                new TileActionPacket(TileActionKind.Punch, 50, 55, TileIds.Air)));
        }

        world.Tick(1f / 30f);

        Tile bedrock = world.GetTileCopy(50, 55);
        Assert.Equal(TileIds.Bedrock, bedrock.ForegroundId);
        Assert.Equal((byte)0, bedrock.Damage);
        Assert.False(world.IsDirty);
    }

    [Fact]
    public void PlaceDirt_WithinReachSucceeds_OutOfReachOrOverlappingPlayerRejected()
    {
        var world = new WorldInstance("PLACE_TEST", WorldGenerator.Generate("PLACE_TEST"));
        (float playerX, float playerY) = TileCollision.GetSpawnPosition(50, 29);

        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.PlaceForeground, 60, 29, TileIds.Dirt)));

        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.PlaceForeground, 50, 28, TileIds.Dirt)));

        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Air, world.GetTileCopy(60, 29).ForegroundId);
        Assert.Equal(TileIds.Air, world.GetTileCopy(50, 28).ForegroundId);
        Assert.False(world.IsDirty);

        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.Dirt)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Dirt, world.GetTileCopy(52, 29).ForegroundId);
        Assert.True(world.IsDirty);
    }

    [Fact]
    public void TreeSapling_GrowsToRipeOverTicks_AndDropsHarvestWhenPunched()
    {
        DateTime fakeNow = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);
        var world = new WorldInstance("TREE_TEST", WorldGenerator.Generate("TREE_TEST"), utcNowProvider: () => fakeNow);
        (float playerX, float playerY) = TileCollision.GetSpawnPosition(50, 29);

        // Tanam Wood Seed di (52, 29) tepat di atas Dirt (52, 30).
        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WoodSeed)));
        world.Tick(0.1f);

        Assert.Equal(TileIds.Tree, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal(1, world.ActiveTreeCount);
        Assert.True(world.TryGetTree(52, 29, out TreeData planted));
        Assert.Equal(TileIds.WoodSeed, planted.SeedId);
        Assert.False(planted.IsReady(fakeNow.Ticks));

        // Majukan waktu UTC melewati GrowTime Wood (25 detik).
        fakeNow = fakeNow.AddSeconds(26);
        Assert.True(planted.IsReady(fakeNow.Ticks));

        // Panen saat sudah matang -> pohon hilang dan menghasilkan 2-4 Wood di lantai.
        world.EnqueueCommand(new SimulatedTileActionCommand(playerX, playerY, new TileActionPacket(TileActionKind.Punch, 52, 29, TileIds.Air)));
        world.Tick(1f / 30f);

        Assert.Equal(TileIds.Air, world.GetTileCopy(52, 29).ForegroundId);
        Assert.Equal(0, world.ActiveTreeCount);
        Assert.InRange(world.GetTotalFloorItemCount(TileIds.Wood), 2, 4);
    }

    [Fact]
    public void WorldLock_ClaimsWorld_BlocksNonOwners_AndUnlocksWhenOwnerBreaks()
    {
        var world = new WorldInstance("LOCK_TEST", WorldGenerator.Generate("LOCK_TEST"));
        (float playerX, float playerY) = TileCollision.GetSpawnPosition(50, 29);

        // Alice memasang World Lock di (52, 29).
        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock),
            ActorName: "Alice"));
        world.Tick(1f / 30f);

        Assert.True(world.IsLocked);
        Assert.Equal("Alice", world.LockOwner);
        Assert.Equal(TileIds.WorldLock, world.GetTileCopy(52, 29).ForegroundId);

        // Bob tidak boleh memukul atau memasang blok di world milik Alice.
        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.Punch, 51, 30, TileIds.Air),
            ActorName: "Bob"));
        world.EnqueueCommand(new SimulatedTileActionCommand(
            playerX,
            playerY,
            new TileActionPacket(TileActionKind.PlaceForeground, 51, 29, TileIds.Brick),
            ActorName: "Bob"));
        world.Tick(1f / 30f);

        Assert.Equal((byte)0, world.GetTileCopy(51, 30).Damage);
        Assert.Equal(TileIds.Air, world.GetTileCopy(51, 29).ForegroundId);

        // Alice (pemilik) memecah World Lock dengan 3 pukulan -> world terbuka kembali.
        for (int i = 0; i < 3; i++)
        {
            world.EnqueueCommand(new SimulatedTileActionCommand(
                playerX,
                playerY,
                new TileActionPacket(TileActionKind.Punch, 52, 29, TileIds.Air),
                ActorName: "Alice"));
        }

        world.Tick(1f / 30f);

        Assert.False(world.IsLocked);
        Assert.Equal(TileIds.Air, world.GetTileCopy(52, 29).ForegroundId);
    }

    [Fact]
    public async Task SqlitePersistence_AutoSavesDirtyWorldAndLock_AndReloadsExactState()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"growsandbox_test_{Guid.NewGuid():N}.db");

        try
        {
            var repo = new SqliteWorldRepository(tempDbPath);
            var manager1 = new WorldManager(repo);
            WorldInstance world = manager1.GetOrCreateWorld("PERSIST_WORLD");

            (float playerX, float playerY) = TileCollision.GetSpawnPosition(50, 29);
            world.EnqueueCommand(new SimulatedTileActionCommand(
                playerX,
                playerY,
                new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.WorldLock),
                ActorName: "OwnerOne"));
            for (int i = 0; i < 3; i++)
            {
                world.EnqueueCommand(new SimulatedTileActionCommand(
                    playerX,
                    playerY,
                    new TileActionPacket(TileActionKind.Punch, 51, 30, TileIds.Air),
                    ActorName: "OwnerOne"));
            }

            world.Tick(1f / 30f);
            Assert.True(world.IsDirty);

            Task<int> saveTask = manager1.SaveDirtyWorldsAsync();
            while (!saveTask.IsCompleted)
            {
                world.Tick(1f / 30f);
                await Task.Yield();
            }

            int savedCount = await saveTask;
            world.Tick(1f / 30f);

            Assert.Equal(1, savedCount);
            Assert.False(world.IsDirty);

            var manager2 = new WorldManager(new SqliteWorldRepository(tempDbPath));
            WorldInstance reloaded = manager2.GetOrCreateWorld("persist_world");

            Assert.Equal("PERSIST_WORLD", reloaded.Name);
            Assert.False(reloaded.IsDirty);
            Assert.True(reloaded.IsLocked);
            Assert.Equal("OwnerOne", reloaded.LockOwner);
            Assert.Equal(TileIds.WorldLock, reloaded.GetTileCopy(52, 29).ForegroundId);
            Assert.Equal(TileIds.Air, reloaded.GetTileCopy(51, 30).ForegroundId);
            Assert.Equal(TileIds.Bedrock, reloaded.GetTileCopy(50, 55).ForegroundId);
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
