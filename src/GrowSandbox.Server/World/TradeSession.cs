using GrowSandbox.Shared.Networking;
using GrowSandbox.Shared.Networking.Packets;

namespace GrowSandbox.Server.World;

/// <summary>
/// Undangan trade tertunda di dalam satu <see cref="WorldInstance"/> yang otomatis kedaluwarsa setelah 15 detik.
/// </summary>
public sealed class PendingTradeInvite
{
    public const double LifetimeSeconds = 15.0;

    public int RequesterPlayerId { get; }
    public int RequesterAccountId { get; }
    public string RequesterName { get; }
    public int TargetPlayerId { get; }
    public int TargetAccountId { get; }
    public string TargetName { get; }
    public double CreatedAtSeconds { get; }
    public double ExpiresAtSeconds { get; }

    public PendingTradeInvite(
        PlayerSession requester,
        PlayerSession target,
        double nowSeconds)
    {
        RequesterPlayerId = requester.PlayerId;
        RequesterAccountId = requester.AccountId;
        RequesterName = requester.Name;
        TargetPlayerId = target.PlayerId;
        TargetAccountId = target.AccountId;
        TargetName = target.Name;
        CreatedAtSeconds = nowSeconds;
        ExpiresAtSeconds = nowSeconds + LifetimeSeconds;
    }

    public bool IsExpired(double nowSeconds) => nowSeconds >= ExpiresAtSeconds;
}

/// <summary>
/// Sesi Trade 2 arah yang authoritative, anti-quick-swap scam, dan anti-duplikasi
/// di dalam satu <see cref="WorldInstance"/> (hanya dimutasi secara serial oleh loop Tick dunia).
/// </summary>
public sealed class TradeSession
{
    public const int SlotCount = NetworkConstants.TradeOfferSlotCount;
    public const double EditCooldownSeconds = 2.0;

    public int PlayerIdA { get; }
    public int AccountIdA { get; }
    public string NameA { get; }

    public int PlayerIdB { get; }
    public int AccountIdB { get; }
    public string NameB { get; }

    public TradeOfferSlot[] OffersA { get; } = new TradeOfferSlot[SlotCount];
    public TradeOfferSlot[] OffersB { get; } = new TradeOfferSlot[SlotCount];

    public bool AcceptedA { get; set; }
    public bool AcceptedB { get; set; }
    public bool ConfirmedA { get; set; }
    public bool ConfirmedB { get; set; }

    public double CooldownUntilSeconds { get; private set; }
    public bool CooldownSyncPending { get; set; }

    public TradeSession(PlayerSession playerA, PlayerSession playerB, double nowSeconds)
    {
        PlayerIdA = playerA.PlayerId;
        AccountIdA = playerA.AccountId;
        NameA = playerA.Name;

        PlayerIdB = playerB.PlayerId;
        AccountIdB = playerB.AccountId;
        NameB = playerB.Name;

        for (int i = 0; i < SlotCount; i++)
        {
            OffersA[i] = TradeOfferSlot.Empty;
            OffersB[i] = TradeOfferSlot.Empty;
        }

        CooldownUntilSeconds = nowSeconds;
        CooldownSyncPending = false;
    }

    public bool ContainsPlayer(int playerId) => PlayerIdA == playerId || PlayerIdB == playerId;

    public bool IsPlayerA(int playerId) => PlayerIdA == playerId;

    public int GetPartnerPlayerId(int playerId) => playerId == PlayerIdA ? PlayerIdB : PlayerIdA;

    public TradeOfferSlot[] GetOffersFor(int playerId) => playerId == PlayerIdA ? OffersA : OffersB;

    public bool IsEditCooldownActive(double nowSeconds) => nowSeconds < CooldownUntilSeconds;

    public float GetLockCooldownRemaining(double nowSeconds) =>
        (float)Math.Max(0.0, CooldownUntilSeconds - nowSeconds);

    public TradeSessionStatus GetStatus(double nowSeconds)
    {
        if (IsEditCooldownActive(nowSeconds))
        {
            return TradeSessionStatus.CooldownLocked;
        }

        if (AcceptedA && AcceptedB)
        {
            return TradeSessionStatus.ReadyForFinalConfirm;
        }

        return TradeSessionStatus.Negotiating;
    }

    /// <summary>
    /// Proteksi Anti-Quick-Swap Scam:
    /// setiap perubahan isi slot penawaran langsung mereset status Accept &amp; FinalConfirm kedua belah pihak
    /// dan mengaktifkan jeda kunci 2.0 detik sebelum Accept dapat ditekan lagi.
    /// </summary>
    public void OnOfferEdited(double nowSeconds)
    {
        AcceptedA = false;
        AcceptedB = false;
        ConfirmedA = false;
        ConfirmedB = false;
        CooldownUntilSeconds = nowSeconds + EditCooldownSeconds;
        CooldownSyncPending = true;
    }

    public void ResetConfirmations()
    {
        ConfirmedA = false;
        ConfirmedB = false;
    }

    public void ResetApprovals()
    {
        AcceptedA = false;
        AcceptedB = false;
        ConfirmedA = false;
        ConfirmedB = false;
    }

    public Dictionary<ushort, ushort> AggregateOffers(bool forPlayerA)
    {
        TradeOfferSlot[] source = forPlayerA ? OffersA : OffersB;
        var summary = new Dictionary<ushort, ushort>(SlotCount);
        for (int i = 0; i < source.Length; i++)
        {
            TradeOfferSlot slot = source[i];
            if (slot.IsEmpty)
            {
                continue;
            }

            if (summary.TryGetValue(slot.ItemId, out ushort existing))
            {
                summary[slot.ItemId] = (ushort)Math.Min(ushort.MaxValue, existing + slot.Count);
            }
            else
            {
                summary[slot.ItemId] = slot.Count;
            }
        }

        return summary;
    }

    public TradeStateSyncPacket ToSyncPacket(double nowSeconds)
    {
        var copyA = new TradeOfferSlot[SlotCount];
        var copyB = new TradeOfferSlot[SlotCount];
        Array.Copy(OffersA, copyA, SlotCount);
        Array.Copy(OffersB, copyB, SlotCount);

        return new TradeStateSyncPacket(
            Status: GetStatus(nowSeconds),
            AccountIdA: AccountIdA,
            NameA: NameA,
            AccountIdB: AccountIdB,
            NameB: NameB,
            OffersA: copyA,
            OffersB: copyB,
            AcceptedA: AcceptedA,
            AcceptedB: AcceptedB,
            ConfirmedA: ConfirmedA,
            ConfirmedB: ConfirmedB,
            LockCooldownRemaining: GetLockCooldownRemaining(nowSeconds));
    }
}
