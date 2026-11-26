using GrowSandbox.Server.Network;
using GrowSandbox.Server.Persistence;
using GrowSandbox.Server.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using Microsoft.Data.Sqlite;

namespace GrowSandbox.Tests;

public class TradeSystemTests
{
    private static (GameServer Server, WorldInstance World, PeerSession PeerA, PeerSession PeerB) CreateTwoPlayerTradeWorld(
        SqliteAccountRepository accountRepo,
        string worldName = "TRADE_ARENA",
        Dictionary<ushort, ushort>? customInvA = null,
        Dictionary<ushort, ushort>? customInvB = null)
    {
        var worldManager = new WorldManager(accountRepository: accountRepo);
        var server = new GameServer(worldManager, accountRepo);

        var peerA = new PeerSession(peerId: 1, peer: null, remoteAddress: "127.0.0.10");
        var peerB = new PeerSession(peerId: 2, peer: null, remoteAddress: "127.0.0.11");

        AuthResponsePacket regA = server.ProcessRegister(peerA, new RegisterRequestPacket("TraderA", "PassTraderA1"));
        AuthResponsePacket regB = server.ProcessRegister(peerB, new RegisterRequestPacket("TraderB", "PassTraderB2"));
        Assert.True(regA.Success);
        Assert.True(regB.Success);

        if (customInvA is not null)
        {
            byte[] blobA = AccountInventoryCodec.Encode(customInvA);
            accountRepo.SaveProgress(peerA.AccountId, peerA.Gems, blobA, worldName);
            peerA.ApplyPersistedProfile(customInvA, peerA.Gems, worldName);
        }

        if (customInvB is not null)
        {
            byte[] blobB = AccountInventoryCodec.Encode(customInvB);
            accountRepo.SaveProgress(peerB.AccountId, peerB.Gems, blobB, worldName);
            peerB.ApplyPersistedProfile(customInvB, peerB.Gems, worldName);
        }

        WorldInstance? world = server.JoinSessionToWorld(peerA, worldName);
        Assert.NotNull(world);
        server.JoinSessionToWorld(peerB, worldName);
        world.Tick(1f / 30f);

        return (server, world, peerA, peerB);
    }

    private static void OpenTradeSession(WorldInstance world, PeerSession requester, PeerSession target)
    {
        world.EnqueueCommand(new TradeRequestCommand(
            requester.PeerId,
            new TradeRequestPacket(TargetAccountId: target.AccountId, TargetUsername: target.Username)));
        world.Tick(1f / 30f);

        Assert.True(world.HasPendingTradeInvite(requester.PeerId, target.PeerId));

        world.EnqueueCommand(new TradeInviteResponseCommand(
            target.PeerId,
            new TradeInviteResponsePacket(requester.AccountId, Accepted: true)));
        world.Tick(1f / 30f);

        Assert.Equal(1, world.ActiveTradeCount);
        Assert.True(world.IsPlayerInActiveTrade(requester.PeerId));
        Assert.True(world.IsPlayerInActiveTrade(target.PeerId));
    }

    [Fact]
    public void NormalTrade_Succeeds_ConservesTotalItems_AndPersistsAtomicallyToSqlite()
    {
        string tempDbPath = Path.Combine(Path.GetTempPath(), $"growsandbox_trade_ok_{Guid.NewGuid():N}.db");

        try
        {
            int accountIdA;
            int accountIdB;

            using (var accountRepo = new SqliteAccountRepository(tempDbPath))
            {
                (GameServer server, WorldInstance world, PeerSession peerA, PeerSession peerB) =
                    CreateTwoPlayerTradeWorld(accountRepo);
                using (server)
                {
                    accountIdA = peerA.AccountId;
                    accountIdB = peerB.AccountId;

                    var endedEvents = new List<(int PlayerId, TradeEndedPacket Packet)>();
                    var systemChats = new List<(int PlayerId, string Message)>();
                    world.TradeEndedSent += (pid, pkt) => endedEvents.Add((pid, pkt));
                    world.SystemChatSent += (pid, msg) => systemChats.Add((pid, msg));

                    // Catat total item sebelum trade untuk verifikasi konservasi item (anti-duplikasi).
                    ushort[] trackedItems = [TileIds.Dirt, TileIds.Wood, TileIds.WorldLock, TileIds.RockSeed];
                    var totalsBefore = new Dictionary<ushort, int>();
                    foreach (ushort id in trackedItems)
                    {
                        totalsBefore[id] =
                            world.GetPlayerInventoryCount(peerA.PeerId, id) +
                            world.GetPlayerInventoryCount(peerB.PeerId, id);
                    }

                    OpenTradeSession(world, peerA, peerB);

                    // A menawarkan 10 Dirt (slot 0) + 1 WorldLock (slot 1); B menawarkan 5 Wood (slot 0) + 2 RockSeed (slot 1).
                    world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Dirt, 10)));
                    world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(1, TileIds.WorldLock, 1)));
                    world.EnqueueCommand(new TradeUpdateOfferCommand(peerB.PeerId, new TradeUpdateOfferPacket(0, TileIds.Wood, 5)));
                    world.EnqueueCommand(new TradeUpdateOfferCommand(peerB.PeerId, new TradeUpdateOfferPacket(1, TileIds.RockSeed, 2)));
                    world.Tick(0.1f);

                    // Selama EditCooldown 2.0 detik belum habis, Accept wajib ditolak!
                    world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
                    world.Tick(0.5f);
                    Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket lockedState));
                    Assert.Equal(TradeSessionStatus.CooldownLocked, lockedState.Status);
                    Assert.False(lockedState.AcceptedA);
                    Assert.True(lockedState.LockCooldownRemaining > 0f);

                    // Lewati sisa cooldown 2.0 detik.
                    world.Tick(2.0f);
                    Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket unlockedState));
                    Assert.Equal(TradeSessionStatus.Negotiating, unlockedState.Status);
                    Assert.Equal(0f, unlockedState.LockCooldownRemaining);

                    // FinalConfirm sebelum kedua pihak Accept wajib ditolak.
                    world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
                    world.Tick(0.05f);
                    Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket preAcceptState));
                    Assert.False(preAcceptState.ConfirmedA);

                    // Kedua pihak menekan Accept -> status ReadyForFinalConfirm.
                    world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
                    world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
                    world.Tick(0.05f);
                    Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket readyState));
                    Assert.Equal(TradeSessionStatus.ReadyForFinalConfirm, readyState.Status);
                    Assert.True(readyState.AcceptedA);
                    Assert.True(readyState.AcceptedB);

                    // Kedua pihak menekan FinalConfirm -> Atomic Commit!
                    world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
                    world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
                    world.Tick(0.05f);

                    Assert.Equal(0, world.ActiveTradeCount);
                    Assert.False(world.IsPlayerInActiveTrade(peerA.PeerId));
                    Assert.False(world.IsPlayerInActiveTrade(peerB.PeerId));

                    // Verifikasi paket TradeEnded(Completed: true) dan log chat sistem terkirim ke kedua pihak.
                    Assert.Equal(2, endedEvents.Count);
                    Assert.All(endedEvents, e => Assert.True(e.Packet.Completed));
                    Assert.Contains(systemChats, c => c.PlayerId == peerA.PeerId && c.Message.Contains("[TRADE]"));
                    Assert.Contains(systemChats, c => c.PlayerId == peerB.PeerId && c.Message.Contains("[TRADE]"));

                    // Verifikasi saldo akhir RAM (starter: Dirt=30, Wood=15, WorldLock=2, RockSeed=3).
                    Assert.Equal((ushort)20, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Dirt));
                    Assert.Equal((ushort)40, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.Dirt));
                    Assert.Equal((ushort)1, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.WorldLock));
                    Assert.Equal((ushort)3, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.WorldLock));
                    Assert.Equal((ushort)20, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Wood));
                    Assert.Equal((ushort)10, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.Wood));
                    Assert.Equal((ushort)5, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.RockSeed));
                    Assert.Equal((ushort)1, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.RockSeed));

                    // Verifikasi hukum konservasi item: total tiap item di antara 2 akun wajib persis sama!
                    foreach (ushort id in trackedItems)
                    {
                        int totalAfter =
                            world.GetPlayerInventoryCount(peerA.PeerId, id) +
                            world.GetPlayerInventoryCount(peerB.PeerId, id);
                        Assert.Equal(totalsBefore[id], totalAfter);
                    }

                    // Verifikasi cache PeerSession di GameServer ikut tersinkronisasi dan bersih (sudah di-commit).
                    Assert.Equal((ushort)20, peerA.Inventory[TileIds.Dirt]);
                    Assert.Equal((ushort)40, peerB.Inventory[TileIds.Dirt]);
                    Assert.False(peerA.IsProfileDirty);
                    Assert.False(peerB.IsProfileDirty);
                }
            }

            // Buka ulang database SQLite dari disk untuk memverifikasi persistensi transaksional langsung pasca trade.
            using (var repoVerify = new SqliteAccountRepository(tempDbPath))
            {
                AccountRecord? accA = repoVerify.GetById(accountIdA);
                AccountRecord? accB = repoVerify.GetById(accountIdB);
                Assert.NotNull(accA);
                Assert.NotNull(accB);

                Dictionary<ushort, ushort>? dbInvA = AccountInventoryCodec.Decode(accA.InventoryBlob);
                Dictionary<ushort, ushort>? dbInvB = AccountInventoryCodec.Decode(accB.InventoryBlob);
                Assert.NotNull(dbInvA);
                Assert.NotNull(dbInvB);

                Assert.Equal((ushort)20, dbInvA[TileIds.Dirt]);
                Assert.Equal((ushort)40, dbInvB[TileIds.Dirt]);
                Assert.Equal((ushort)1, dbInvA[TileIds.WorldLock]);
                Assert.Equal((ushort)3, dbInvB[TileIds.WorldLock]);
                Assert.Equal((ushort)20, dbInvA[TileIds.Wood]);
                Assert.Equal((ushort)10, dbInvB[TileIds.Wood]);
                Assert.Equal((ushort)5, dbInvA[TileIds.RockSeed]);
                Assert.Equal((ushort)1, dbInvB[TileIds.RockSeed]);
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
    public void AntiQuickSwapScam_EditingOfferAfterPartnerAccepts_ResetsBothAcceptsAndArms2SecondCooldown()
    {
        using var accountRepo = new SqliteAccountRepository(":memory:");
        (GameServer server, WorldInstance world, PeerSession peerA, PeerSession peerB) =
            CreateTwoPlayerTradeWorld(accountRepo);
        using (server)
        {
            OpenTradeSession(world, peerA, peerB);

            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Dirt, 15)));
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerB.PeerId, new TradeUpdateOfferPacket(0, TileIds.Wood, 10)));
            world.Tick(0.05f); // Proses edit penawaran
            world.Tick(2.1f); // Tunggu cooldown 2.0 detik selesai

            // Kedua pihak menekan Accept, dan A bahkan sudah menekan FinalConfirm.
            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
            world.Tick(0.05f);

            Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket beforeSwap));
            Assert.True(beforeSwap.AcceptedA);
            Assert.True(beforeSwap.AcceptedB);
            Assert.True(beforeSwap.ConfirmedA);
            Assert.False(beforeSwap.ConfirmedB);

            // Skenario Quick-Swap Scam: B diam-diam menurunkan tawaran Wood dari 10 menjadi 1!
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerB.PeerId, new TradeUpdateOfferPacket(0, TileIds.Wood, 1)));
            world.Tick(0.05f);

            Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket afterSwap));
            Assert.False(afterSwap.AcceptedA);
            Assert.False(afterSwap.AcceptedB);
            Assert.False(afterSwap.ConfirmedA);
            Assert.False(afterSwap.ConfirmedB);
            Assert.Equal(TradeSessionStatus.CooldownLocked, afterSwap.Status);
            Assert.InRange(afterSwap.LockCooldownRemaining, 1.8f, 2.01f);

            // Upaya B menekan FinalConfirm langsung setelah swap wajib ditolak!
            world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
            world.Tick(0.1f);
            Assert.Equal(1, world.ActiveTradeCount);
            Assert.Equal((ushort)30, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Dirt));
            Assert.Equal((ushort)15, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.Wood));

            // Mengosongkan slot (Count == 0) juga wajib me-reset Accept & memasang cooldown 2 detik lagi.
            world.Tick(2.1f);
            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.Tick(0.05f);
            Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket acceptedAgain));
            Assert.True(acceptedAgain.AcceptedA);

            world.EnqueueCommand(new TradeUpdateOfferCommand(peerB.PeerId, new TradeUpdateOfferPacket(0, TileIds.Wood, 0)));
            world.Tick(0.05f);
            Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket afterClear));
            Assert.False(afterClear.AcceptedA);
            Assert.False(afterClear.AcceptedB);
            Assert.True(afterClear.OffersB[0].IsEmpty);
            Assert.True(afterClear.LockCooldownRemaining > 1.5f);
        }
    }

    [Fact]
    public void OfferValidation_RejectsOverStock_ForbiddenItems_AndDuplicateItemAcrossSlots()
    {
        using var accountRepo = new SqliteAccountRepository(":memory:");
        (GameServer server, WorldInstance world, PeerSession peerA, PeerSession peerB) =
            CreateTwoPlayerTradeWorld(accountRepo);
        using (server)
        {
            OpenTradeSession(world, peerA, peerB);

            // 1. Mencoba menawarkan jumlah lebih besar dari stok (A hanya punya 30 Dirt, menawarkan 31).
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Dirt, 31)));
            // 2. Mencoba memasukkan Fist, Wrench, Gem, Bedrock, dan ID item tidak valid (9999).
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Fist, 1)));
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Wrench, 1)));
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Gem, 5)));
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Bedrock, 1)));
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, 9999, 1)));
            // 3. SlotIndex di luar rentang 0..3.
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(4, TileIds.Dirt, 5)));
            world.Tick(0.05f);

            Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket stateAfterInvalid));
            Assert.All(stateAfterInvalid.OffersA, slot => Assert.True(slot.IsEmpty));

            // 4. Memasukkan Dirt ke slot 0 (valid), lalu mencoba memasukkan Dirt lagi ke slot 1 -> wajib ditolak!
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Dirt, 10)));
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(1, TileIds.Dirt, 5)));
            world.Tick(0.05f);

            Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket stateAfterDup));
            Assert.Equal(new TradeOfferSlot(TileIds.Dirt, 10), stateAfterDup.OffersA[0]);
            Assert.True(stateAfterDup.OffersA[1].IsEmpty);
        }
    }

    [Fact]
    public void RecipientBagOverflowOver200_CancelsConfirmationAndPreservesInventories()
    {
        using var accountRepo = new SqliteAccountRepository(":memory:");
        var invA = new Dictionary<ushort, ushort> { [TileIds.Dirt] = 50, [TileIds.Wood] = 10 };
        var invB = new Dictionary<ushort, ushort> { [TileIds.Dirt] = 190, [TileIds.Wood] = 20 };

        (GameServer server, WorldInstance world, PeerSession peerA, PeerSession peerB) =
            CreateTwoPlayerTradeWorld(accountRepo, customInvA: invA, customInvB: invB);
        using (server)
        {
            var toasts = new List<(int PlayerId, string Message, ToastKind Kind)>();
            world.PlayerToastSent += (pid, msg, kind) => toasts.Add((pid, msg, kind));

            OpenTradeSession(world, peerA, peerB);

            // A menawarkan 20 Dirt ke B yang sudah memiliki 190 Dirt (190 + 20 = 210 > 200!).
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Dirt, 20)));
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerB.PeerId, new TradeUpdateOfferPacket(0, TileIds.Wood, 5)));
            world.Tick(0.05f);
            world.Tick(2.1f);

            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
            world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
            world.Tick(0.05f);

            // Sesi tetap terbuka tetapi konfirmasi & accept dibatalkan, pesan error jelas dikirim, stok tidak berubah!
            Assert.Equal(1, world.ActiveTradeCount);
            Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket afterOverflow));
            Assert.False(afterOverflow.AcceptedA);
            Assert.False(afterOverflow.AcceptedB);
            Assert.False(afterOverflow.ConfirmedA);
            Assert.False(afterOverflow.ConfirmedB);

            Assert.Equal((ushort)50, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Dirt));
            Assert.Equal((ushort)190, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.Dirt));
            Assert.Equal((ushort)10, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Wood));
            Assert.Equal((ushort)20, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.Wood));
            Assert.Contains(toasts, t => t.Message.Contains("200") && t.Kind == ToastKind.Warning);

            // Setelah A menurunkan penawaran ke 10 Dirt (190 + 10 = 200 tepat di batas), trade berhasil!
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Dirt, 10)));
            world.Tick(0.05f);
            world.Tick(2.1f);
            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
            world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
            world.Tick(0.05f);

            Assert.Equal(0, world.ActiveTradeCount);
            Assert.Equal((ushort)40, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Dirt));
            Assert.Equal((ushort)200, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.Dirt));
            Assert.Equal((ushort)15, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Wood));
            Assert.Equal((ushort)15, world.GetPlayerInventoryCount(peerB.PeerId, TileIds.Wood));
        }
    }

    [Fact]
    public void DisconnectDuringActiveTrade_CancelsSessionCleanly_WithoutMutatingEitherInventory()
    {
        using var accountRepo = new SqliteAccountRepository(":memory:");
        (GameServer server, WorldInstance world, PeerSession peerA, PeerSession peerB) =
            CreateTwoPlayerTradeWorld(accountRepo);
        using (server)
        {
            var endedEvents = new List<(int PlayerId, TradeEndedPacket Packet)>();
            world.TradeEndedSent += (pid, pkt) => endedEvents.Add((pid, pkt));

            OpenTradeSession(world, peerA, peerB);

            world.EnqueueCommand(new TradeUpdateOfferCommand(peerA.PeerId, new TradeUpdateOfferPacket(0, TileIds.Dirt, 25)));
            world.EnqueueCommand(new TradeUpdateOfferCommand(peerB.PeerId, new TradeUpdateOfferPacket(0, TileIds.WorldLock, 2)));
            world.Tick(0.05f);
            world.Tick(2.1f);

            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.EnqueueCommand(new TradeActionCommand(peerB.PeerId, new TradeActionPacket(TradeActionKind.ToggleAccept)));
            world.EnqueueCommand(new TradeActionCommand(peerA.PeerId, new TradeActionPacket(TradeActionKind.FinalConfirm)));
            world.Tick(0.05f);

            Assert.True(world.TryGetActiveTradeState(peerA.PeerId, out TradeStateSyncPacket midConfirm));
            Assert.True(midConfirm.AcceptedA && midConfirm.AcceptedB && midConfirm.ConfirmedA);

            int accountIdA = peerA.AccountId;
            int accountIdB = peerB.AccountId;

            // Pihak B tiba-tiba terputus (disconnect) sebelum FinalConfirm!
            server.DisconnectSession(peerB, "ConnectionLost");

            Assert.Equal(0, world.ActiveTradeCount);
            Assert.False(world.IsPlayerInActiveTrade(peerA.PeerId));
            Assert.Contains(endedEvents, e => e.PlayerId == peerA.PeerId && !e.Packet.Completed);

            // Inventory A dan B tetap 100% utuh (tidak berubah sama sekali di RAM maupun SQLite).
            Assert.Equal((ushort)30, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Dirt));
            Assert.Equal((ushort)2, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.WorldLock));

            AccountRecord? dbA = accountRepo.GetById(accountIdA);
            AccountRecord? dbB = accountRepo.GetById(accountIdB);
            Assert.NotNull(dbA);
            Assert.NotNull(dbB);
            Assert.Equal((ushort)30, AccountInventoryCodec.Decode(dbA.InventoryBlob)![TileIds.Dirt]);
            Assert.Equal((ushort)2, AccountInventoryCodec.Decode(dbB.InventoryBlob)![TileIds.WorldLock]);
        }
    }

    [Fact]
    public void ActiveTrade_BlocksDropPlacePickup_EnforcesSingleSession_AndExpiresInvitesAfter15Seconds()
    {
        using var accountRepo = new SqliteAccountRepository(":memory:");
        (GameServer server, WorldInstance world, PeerSession peerA, PeerSession peerB) =
            CreateTwoPlayerTradeWorld(accountRepo);
        using (server)
        {
            // 1. Verifikasi undangan trade otomatis kedaluwarsa setelah 15 detik.
            world.EnqueueCommand(new TradeRequestCommand(peerA.PeerId, new TradeRequestPacket(peerB.AccountId)));
            world.Tick(0.1f);
            Assert.True(world.HasPendingTradeInvite(peerA.PeerId, peerB.PeerId));

            world.Tick(15.1f);
            Assert.False(world.HasPendingTradeInvite(peerA.PeerId, peerB.PeerId));
            Assert.Equal(0, world.PendingTradeInviteCount);

            // Menerima undangan yang sudah kedaluwarsa wajib gagal.
            world.EnqueueCommand(new TradeInviteResponseCommand(peerB.PeerId, new TradeInviteResponsePacket(peerA.AccountId, Accepted: true)));
            world.Tick(0.05f);
            Assert.Equal(0, world.ActiveTradeCount);

            // 2. Buka sesi trade aktif antara A & B.
            OpenTradeSession(world, peerA, peerB);

            // 3. Pemain ketiga C tidak boleh membuka trade dengan A yang sedang trade.
            var peerC = new PeerSession(peerId: 3, peer: null, remoteAddress: "127.0.0.12");
            Assert.True(server.ProcessRegister(peerC, new RegisterRequestPacket("TraderC", "PassTraderC3")).Success);
            server.JoinSessionToWorld(peerC, "TRADE_ARENA");
            world.Tick(0.05f);

            world.EnqueueCommand(new TradeRequestCommand(peerC.PeerId, new TradeRequestPacket(peerA.AccountId)));
            world.Tick(0.05f);
            Assert.False(world.HasPendingTradeInvite(peerC.PeerId, peerA.PeerId));

            // 4. Selama trade aktif, A tidak boleh membuang item, memasang blok, atau memungut drop di lantai.
            world.EnqueueCommand(new DropItemRequestCommand(peerA.PeerId, new DropItemRequestPacket(TileIds.Dirt, 5)));
            world.EnqueueCommand(new TileActionCommand(peerA.PeerId, new TileActionPacket(TileActionKind.PlaceForeground, 52, 29, TileIds.Dirt)));
            float cx = world.SpawnX + PhysicsConstants.PlayerWidth * 0.5f;
            float cy = world.SpawnY + PhysicsConstants.PlayerHeight * 0.5f;
            world.EnqueueCommand(new SimulatedSpawnDropCommand(TileIds.Rock, 4, cx, cy));
            world.Tick(0.1f);

            Assert.Equal((ushort)30, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Dirt));
            Assert.Equal(TileIds.Air, world.GetTileCopy(52, 29).ForegroundId);
            Assert.Equal((ushort)15, world.GetPlayerInventoryCount(peerA.PeerId, TileIds.Rock)); // Drop tidak dipungut saat trade!

            // 5. Pindah world otomatis membatalkan trade secara bersih.
            server.JoinSessionToWorld(peerA, "OTHER_WORLD");
            Assert.Equal(0, world.ActiveTradeCount);
            Assert.False(world.IsPlayerInActiveTrade(peerB.PeerId));
        }
    }

    [Fact]
    public void SaveTradeProfilesAtomic_RollsBackEntireSqliteTransactionWhenSecondAccountDoesNotExist()
    {
        using var accountRepo = new SqliteAccountRepository(":memory:");
        byte[] initialBlob = AccountInventoryCodec.Encode(new Dictionary<ushort, ushort> { [TileIds.Dirt] = 30 });
        AccountAuthResult regA = accountRepo.Register("AtomicUserA", "AtomicPass123", initialBlob, initialGems: 10);
        Assert.True(regA.Success);
        int accountIdA = regA.Account!.AccountId;

        byte[] modifiedBlobA = AccountInventoryCodec.Encode(new Dictionary<ushort, ushort> { [TileIds.Dirt] = 5 });

        Assert.Throws<InvalidOperationException>(() =>
            accountRepo.SaveTradeProfilesAtomic(
                new AccountProfileSaveEntry(accountIdA, 10, modifiedBlobA, "START"),
                new AccountProfileSaveEntry(999999, 10, initialBlob, "START")));

        // Akun A wajib ter-rollback penuh kembali ke 30 Dirt!
        AccountRecord? persistedA = accountRepo.GetById(accountIdA);
        Assert.NotNull(persistedA);
        Dictionary<ushort, ushort>? decodedA = AccountInventoryCodec.Decode(persistedA.InventoryBlob);
        Assert.NotNull(decodedA);
        Assert.Equal((ushort)30, decodedA[TileIds.Dirt]);
    }
}
