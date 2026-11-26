using GrowSandbox.Client.Graphics;
using GrowSandbox.Client.Network;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking.Packets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.UI;

public enum ActiveToolKind
{
    Fist,
    Wrench,
    Item,
}

public enum HudClickResult
{
    None,
    Consumed,
    ToggleChatInput,
    OpenWarpModal,
    ToggleMenuModal,
    WarpToWorld,
    Respawn,
    ToggleMuteSfx,
    DropSelectedItem,
    ToggleLockPublic,
    AddLockAccess,
    RemoveLockAccess,
    PickUpWorldLock,
    AcceptTradeInvite,
    DeclineTradeInvite,
    WrenchPopupTrade,
    WrenchPopupGiveAccess,
    TradeToggleAccept,
    TradeFinalConfirm,
    TradeCancel,
    TradeClickLocalSlot,
    TradeAdjustQuickAmount,
    TradeClickInventorySlot,
    Logout,
    ExitGame,
}

public enum TradeQuickAdjustKind : byte
{
    None = 0,
    MinusOne = 1,
    PlusOne = 2,
    PlusTen = 3,
    Max = 4,
    Clear = 5,
}

public readonly record struct WrenchPlayerPopupView(
    int TargetPlayerId,
    string TargetPlayerName,
    Vector2 ScreenAnchor,
    bool CanGiveWorldLockAccess);

public readonly record struct DebugMetrics(
    int Fps,
    string WorldName,
    int PlayerCount,
    int VisibleTiles,
    int TotalTiles,
    int PingMs,
    int PlayerTileX,
    int PlayerTileY);

/// <summary>
/// Antarmuka HUD modern bergaya Growtopia dengan panel 9-slice SDF anti-aliased (<see cref="UiNineSlice"/>),
/// tipografi TrueType resolusi tinggi (<see cref="UiFontManager"/>), pemisahan pass render UI smooth
/// (<see cref="SamplerState.LinearClamp"/>) vs. ikon pixel-art tajam (<see cref="SamplerState.PointClamp"/>),
/// serta animasi micro-interaction cubic ease-out.
/// </summary>
public sealed class HudRenderer
{
    public const int SlotsPerRow = 10;
    private const int SlotSize = 54;
    private const int SlotGap = 5;
    private const int MaxVisibleChatLines = 7;
    private const int TopBarHeight = 42;
    private const int TopBarGap = 8;
    private const int ScreenMargin = 12;

    private const float DrawerAnimDuration = 0.22f;
    private const float ModalPopDuration = 0.13f;
    private const float BannerSlideInDuration = 0.22f;

    // Palet warna Gt* yang dibagikan konsisten antara HUD in-game dan layar AuthMenu
    internal static readonly Color GtPanelFill = new(24, 96, 146, 188);
    internal static readonly Color GtPanelSolid = new(28, 106, 158);
    internal static readonly Color GtPanelDark = new(18, 66, 104);
    internal static readonly Color GtPanelDarkTranslucent = new(18, 66, 104, 215);
    internal static readonly Color GtBorderBright = new(126, 218, 255);
    internal static readonly Color GtBorderSubtle = new(126, 218, 255, 190);
    internal static readonly Color GtSlotFill = new(34, 88, 128);
    internal static readonly Color GtSlotSelectedFill = new(62, 134, 184);
    internal static readonly Color GtGoldBorder = new(255, 220, 68);
    internal static readonly Color GtGreenBtn = new(56, 168, 76);
    internal static readonly Color GtGreenBorder = new(146, 242, 154);
    internal static readonly Color GtRedBtn = new(196, 62, 62);
    internal static readonly Color GtRedBorder = new(255, 154, 154);

    internal static readonly Color ChatSystemColor = new(255, 228, 122);
    internal static readonly Color ChatPlayerNameColor = new(132, 232, 255);
    internal static readonly Color ChatErrorColor = new(255, 148, 158);
    internal static readonly Color WorldStatusCyan = new(166, 234, 255);
    internal static readonly Color DisabledTextColor = new(162, 184, 202);

    private static readonly string[] QuickWorlds = ["START", "FARM", "TRADE", "BUILD"];

    private readonly PixelAtlas _atlas;
    private readonly UiNineSlice _nineSlice;
    private readonly UiFontManager _uiFonts;

    // Antrean ikon pixel-art (dirender terpisah memakai SamplerState.PointClamp) & pil jumlah di atas ikon
    private readonly List<QueuedPixelIcon> _pixelIcons = new(64);
    private readonly List<QueuedCountPill> _countPills = new(40);

    private Rectangle _chatToggleRect;
    private Rectangle _worldPanelRect;
    private Rectangle _gemPillRect;
    private Rectangle _chatLogRect;
    private Rectangle _chatInputRect;
    private Rectangle _warpButtonRect;
    private Rectangle _menuButtonRect;
    private Rectangle _drawerHandleRect;
    private Rectangle _dropButtonRect;
    private Rectangle _drawerBoundsRect;
    private readonly List<(Rectangle Rect, int SlotIndex)> _slotHitboxes = new(30);

    // Hitbox modal
    private Rectangle _modalPanelRect;
    private Rectangle _modalPrimaryBtnRect;
    private Rectangle _modalLogoutBtnRect;
    private Rectangle _modalSecondaryBtnRect;
    private Rectangle _modalMuteBtnRect;
    private Rectangle _modalCloseBtnRect;
    private readonly List<(Rectangle Rect, string WorldName)> _quickWorldBtnRects = new(4);

    // Hitbox modal konfigurasi World Lock
    private Rectangle _lockPublicBtnRect;
    private Rectangle _lockAddBtnRect;
    private Rectangle _lockPickUpBtnRect;
    private Rectangle _lockCloseFooterBtnRect;
    private readonly List<(Rectangle Rect, string PlayerName)> _lockRemoveBtnRects = new(WorldLockData.MaxAccessListCount);

    // Hitbox Banner Undangan Trade masuk & Popup Wrench pada karakter pemain lain
    private Rectangle _inviteBannerRect;
    private Rectangle _inviteAcceptBtnRect;
    private Rectangle _inviteDeclineBtnRect;
    private Rectangle _playerPopupRect;
    private Rectangle _playerPopupTradeBtnRect;
    private Rectangle _playerPopupGiveAccessBtnRect;

    // Hitbox Modal Jendela Trade 2 Arah
    private readonly Rectangle[] _tradeLocalSlotRects = new Rectangle[4];
    private readonly Rectangle[] _tradePartnerSlotRects = new Rectangle[4];
    private readonly List<(Rectangle Rect, TradeQuickAdjustKind Kind, bool Enabled)> _tradeQuickBtnRects = new(5);
    private Rectangle _tradePrimaryBtnRect;
    private Rectangle _tradeCancelBtnRect;
    private bool _tradePrimaryBtnDisabled;
    private bool _tradePrimaryIsFinalConfirm;

    // Notifikasi Toast singkat di atas layar
    private string _toastMessage = string.Empty;
    private ToastKind _toastKind = ToastKind.Info;
    private float _toastTimer;
    private float _toastDuration = 2.8f;

    // State animasi Cubic Ease-Out
    private ushort _lastPlaceableItemId = TileIds.Dirt;
    private bool _lastExpandedTarget;
    private float _drawerAnimFrom;
    private float _drawerAnimTo;
    private float _drawerAnimElapsed = DrawerAnimDuration;
    private float _drawerExpandProgress;

    private int _activeModalKey; // 0=None, 1=Warp, 2=Lock, 3=Menu, 4=Trade
    private float _modalAnimElapsed = ModalPopDuration;

    private int _lastInviteRequesterId = -1;
    private float _inviteAnimElapsed;

    private float _chatVisibilityAlpha = 1f;
    private int _chatScrollOffset;
    private int _maxChatScrollOffset;

    public int SelectedSlotIndex { get; set; } // 0 = Fist, 1 = Wrench, 2+ = Inventory[index - 2]
    public int FocusedTradeSlotIndex { get; set; }
    public byte ClickedTradeSlotIndex { get; private set; }
    public TradeQuickAdjustKind ClickedTradeQuickAdjust { get; private set; }
    public int ClickedTradeInventorySlotIndex { get; private set; } = -1;
    public bool IsInventoryExpanded { get; set; }
    public bool IsChatVisible { get; set; } = true;
    public bool IsDebugOverlayVisible { get; set; }
    public bool IsWarpModalOpen { get; set; }
    public bool IsMenuModalOpen { get; set; }
    public bool IsLockModalOpen { get; set; }
    public bool IsTradeModalOpen { get; set; }
    public bool HasOpenModal => IsWarpModalOpen || IsMenuModalOpen || IsLockModalOpen || IsTradeModalOpen;

    public string WorldBannerTitle { get; private set; } = string.Empty;
    public string WorldBannerSubtitle { get; private set; } = string.Empty;
    public float WorldBannerTimer { get; private set; }

    public HudRenderer(PixelAtlas atlas, UiNineSlice nineSlice, UiFontManager uiFonts)
    {
        _atlas = atlas;
        _nineSlice = nineSlice;
        _uiFonts = uiFonts;
    }

    public void CloseAllModals()
    {
        IsWarpModalOpen = false;
        IsMenuModalOpen = false;
        IsLockModalOpen = false;
        IsTradeModalOpen = false;
        _activeModalKey = 0;
        _modalAnimElapsed = ModalPopDuration;
        FocusedTradeSlotIndex = 0;
        _inviteBannerRect = Rectangle.Empty;
        _inviteAcceptBtnRect = Rectangle.Empty;
        _inviteDeclineBtnRect = Rectangle.Empty;
        _playerPopupRect = Rectangle.Empty;
        _playerPopupTradeBtnRect = Rectangle.Empty;
        _playerPopupGiveAccessBtnRect = Rectangle.Empty;
    }

    public void ShowWorldBanner(string worldName, WorldLockData lockData)
    {
        WorldBannerTitle = worldName.ToUpperInvariant();
        UpdateWorldBannerOwner(lockData);
        WorldBannerTimer = 3.8f;
    }

    public void UpdateWorldBannerOwner(WorldLockData lockData)
    {
        if (!lockData.IsLocked || string.IsNullOrEmpty(lockData.OwnerName))
        {
            WorldBannerSubtitle = "OPEN WORLD  •  PLACE A WORLD LOCK TO CLAIM!";
        }
        else if (lockData.IsPublic)
        {
            WorldBannerSubtitle = $"PUBLIC LOCK BY {lockData.OwnerName.ToUpperInvariant()}  •  ANYONE CAN BUILD";
        }
        else
        {
            WorldBannerSubtitle = $"LOCKED BY {lockData.OwnerName.ToUpperInvariant()}";
        }
    }

    public void ShowToast(string message, ToastKind kind = ToastKind.Info, float durationSeconds = 2.8f)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        _toastMessage = message.Trim();
        _toastKind = kind;
        _toastDuration = MathF.Max(1.0f, durationSeconds);
        _toastTimer = _toastDuration;
    }

    public void Update(float dt)
    {
        if (WorldBannerTimer > 0f)
        {
            WorldBannerTimer = MathF.Max(0f, WorldBannerTimer - dt);
        }

        if (_toastTimer > 0f)
        {
            _toastTimer = MathF.Max(0f, _toastTimer - dt);
        }

        // Animasi buka/tutup inventory drawer memakai kurva Cubic Ease-Out (1 - (1 - t)^3)
        if (IsInventoryExpanded != _lastExpandedTarget)
        {
            _lastExpandedTarget = IsInventoryExpanded;
            _drawerAnimFrom = _drawerExpandProgress;
            _drawerAnimTo = IsInventoryExpanded ? 1f : 0f;
            _drawerAnimElapsed = 0f;
        }

        if (_drawerAnimElapsed < DrawerAnimDuration)
        {
            _drawerAnimElapsed = MathF.Min(DrawerAnimDuration, _drawerAnimElapsed + dt);
            float t = CubicEaseOut(_drawerAnimElapsed / DrawerAnimDuration);
            _drawerExpandProgress = MathHelper.Lerp(_drawerAnimFrom, _drawerAnimTo, t);
        }
        else
        {
            _drawerExpandProgress = _drawerAnimTo;
        }

        // Animasi pop-in modal (scale 0.94 -> 1.0, alpha 0 -> 1 selama 130ms)
        int modalKey = GetCurrentModalKey();
        if (modalKey != _activeModalKey)
        {
            _activeModalKey = modalKey;
            _modalAnimElapsed = 0f;
        }
        else if (_activeModalKey != 0 && _modalAnimElapsed < ModalPopDuration)
        {
            _modalAnimElapsed = MathF.Min(ModalPopDuration, _modalAnimElapsed + dt);
        }

        // Animasi slide-in banner undangan Trade
        if (_lastInviteRequesterId >= 0)
        {
            _inviteAnimElapsed = MathF.Min(BannerSlideInDuration, _inviteAnimElapsed + dt);
        }

        // Animasi tampil/sembunyi kotak chat yang mulus
        float chatTarget = IsChatVisible ? 1f : 0f;
        _chatVisibilityAlpha = MathHelper.Lerp(_chatVisibilityAlpha, chatTarget, 1f - MathF.Exp(-14f * dt));
        if (MathF.Abs(_chatVisibilityAlpha - chatTarget) < 0.004f)
        {
            _chatVisibilityAlpha = chatTarget;
        }
    }

    public bool IsPointerOverChatLog(Point mousePosition) =>
        _chatVisibilityAlpha > 0.1f && _chatLogRect.Contains(mousePosition);

    public void ScrollChat(int deltaLines)
    {
        _chatScrollOffset = Math.Clamp(_chatScrollOffset + deltaLines, 0, _maxChatScrollOffset);
    }

    public void ResetChatScroll() => _chatScrollOffset = 0;

    public ActiveToolKind GetActiveTool(IReadOnlyList<InventorySlot> inventory, out ushort itemId)
    {
        if (SelectedSlotIndex == 0)
        {
            itemId = TileIds.Air;
            return ActiveToolKind.Fist;
        }

        if (SelectedSlotIndex == 1)
        {
            itemId = TileIds.Air;
            return ActiveToolKind.Wrench;
        }

        int invIndex = SelectedSlotIndex - 2;
        if (invIndex >= 0 && invIndex < inventory.Count)
        {
            itemId = inventory[invIndex].ItemId;
            if (ItemCatalog.IsPlaceable(itemId))
            {
                _lastPlaceableItemId = itemId;
            }

            return ActiveToolKind.Item;
        }

        itemId = TileIds.Air;
        return ActiveToolKind.Fist;
    }

    public ushort GetRightClickPlaceableItemId(IReadOnlyList<InventorySlot> inventory)
    {
        if (GetActiveTool(inventory, out ushort activeId) == ActiveToolKind.Item &&
            ItemCatalog.IsPlaceable(activeId))
        {
            return activeId;
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            if (inventory[i].ItemId == _lastPlaceableItemId && inventory[i].Count > 0)
            {
                return _lastPlaceableItemId;
            }
        }

        for (int i = 0; i < inventory.Count; i++)
        {
            if (ItemCatalog.IsPlaceable(inventory[i].ItemId) && inventory[i].Count > 0)
            {
                return inventory[i].ItemId;
            }
        }

        return TileIds.Air;
    }

    public bool IsPointerOverHud(Point mousePosition)
    {
        if (HasOpenModal)
        {
            return true;
        }

        return _inviteBannerRect.Contains(mousePosition)
            || _playerPopupRect.Contains(mousePosition)
            || _worldPanelRect.Contains(mousePosition)
            || _gemPillRect.Contains(mousePosition)
            || _chatToggleRect.Contains(mousePosition)
            || _chatLogRect.Contains(mousePosition)
            || _chatInputRect.Contains(mousePosition)
            || _warpButtonRect.Contains(mousePosition)
            || _menuButtonRect.Contains(mousePosition)
            || _drawerHandleRect.Contains(mousePosition)
            || _dropButtonRect.Contains(mousePosition)
            || _drawerBoundsRect.Contains(mousePosition);
    }

    public bool HandleRightClick(Point mousePosition, out byte clearedTradeSlotIndex)
    {
        clearedTradeSlotIndex = 0;
        if (!IsTradeModalOpen)
        {
            return false;
        }

        for (int i = 0; i < _tradeLocalSlotRects.Length; i++)
        {
            if (_tradeLocalSlotRects[i].Contains(mousePosition))
            {
                FocusedTradeSlotIndex = i;
                clearedTradeSlotIndex = (byte)i;
                return true;
            }
        }

        return false;
    }

    public HudClickResult HandleLeftClick(Point mousePosition, out string? selectedWorld)
    {
        selectedWorld = null;
        ClickedTradeQuickAdjust = TradeQuickAdjustKind.None;
        ClickedTradeInventorySlotIndex = -1;

        if (IsTradeModalOpen)
        {
            if (_modalCloseBtnRect.Contains(mousePosition) || _tradeCancelBtnRect.Contains(mousePosition))
            {
                return HudClickResult.TradeCancel;
            }

            if (_tradePrimaryBtnRect.Contains(mousePosition))
            {
                if (_tradePrimaryBtnDisabled)
                {
                    return HudClickResult.Consumed;
                }

                return _tradePrimaryIsFinalConfirm
                    ? HudClickResult.TradeFinalConfirm
                    : HudClickResult.TradeToggleAccept;
            }

            for (int i = 0; i < _tradeLocalSlotRects.Length; i++)
            {
                if (_tradeLocalSlotRects[i].Contains(mousePosition))
                {
                    FocusedTradeSlotIndex = i;
                    ClickedTradeSlotIndex = (byte)i;
                    return HudClickResult.TradeClickLocalSlot;
                }
            }

            for (int i = 0; i < _tradeQuickBtnRects.Count; i++)
            {
                var quick = _tradeQuickBtnRects[i];
                if (quick.Rect.Contains(mousePosition))
                {
                    if (!quick.Enabled)
                    {
                        return HudClickResult.Consumed;
                    }

                    ClickedTradeQuickAdjust = quick.Kind;
                    return HudClickResult.TradeAdjustQuickAmount;
                }
            }

            if (_drawerHandleRect.Contains(mousePosition))
            {
                IsInventoryExpanded = !IsInventoryExpanded;
                return HudClickResult.Consumed;
            }

            for (int i = 0; i < _slotHitboxes.Count; i++)
            {
                if (_slotHitboxes[i].Rect.Contains(mousePosition))
                {
                    SelectedSlotIndex = _slotHitboxes[i].SlotIndex;
                    ClickedTradeInventorySlotIndex = _slotHitboxes[i].SlotIndex;
                    return HudClickResult.TradeClickInventorySlot;
                }
            }

            return HudClickResult.Consumed;
        }

        if (IsWarpModalOpen)
        {
            if (_modalCloseBtnRect.Contains(mousePosition) || !_modalPanelRect.Contains(mousePosition))
            {
                IsWarpModalOpen = false;
                return HudClickResult.Consumed;
            }

            if (_modalPrimaryBtnRect.Contains(mousePosition))
            {
                return HudClickResult.WarpToWorld;
            }

            foreach ((Rectangle rect, string world) in _quickWorldBtnRects)
            {
                if (rect.Contains(mousePosition))
                {
                    selectedWorld = world;
                    IsWarpModalOpen = false;
                    return HudClickResult.WarpToWorld;
                }
            }

            return HudClickResult.Consumed;
        }

        if (IsLockModalOpen)
        {
            if (_modalCloseBtnRect.Contains(mousePosition) ||
                _lockCloseFooterBtnRect.Contains(mousePosition) ||
                !_modalPanelRect.Contains(mousePosition))
            {
                IsLockModalOpen = false;
                return HudClickResult.Consumed;
            }

            if (_lockPublicBtnRect.Contains(mousePosition))
            {
                return HudClickResult.ToggleLockPublic;
            }

            if (_lockAddBtnRect.Contains(mousePosition))
            {
                return HudClickResult.AddLockAccess;
            }

            if (_lockPickUpBtnRect.Contains(mousePosition))
            {
                return HudClickResult.PickUpWorldLock;
            }

            for (int i = 0; i < _lockRemoveBtnRects.Count; i++)
            {
                if (_lockRemoveBtnRects[i].Rect.Contains(mousePosition))
                {
                    selectedWorld = _lockRemoveBtnRects[i].PlayerName;
                    return HudClickResult.RemoveLockAccess;
                }
            }

            return HudClickResult.Consumed;
        }

        if (IsMenuModalOpen)
        {
            if (_modalCloseBtnRect.Contains(mousePosition) || !_modalPanelRect.Contains(mousePosition))
            {
                IsMenuModalOpen = false;
                return HudClickResult.Consumed;
            }

            if (_modalMuteBtnRect.Contains(mousePosition))
            {
                return HudClickResult.ToggleMuteSfx;
            }

            if (_modalPrimaryBtnRect.Contains(mousePosition))
            {
                IsMenuModalOpen = false;
                return HudClickResult.Respawn;
            }

            if (_modalLogoutBtnRect.Contains(mousePosition))
            {
                IsMenuModalOpen = false;
                return HudClickResult.Logout;
            }

            if (_modalSecondaryBtnRect.Contains(mousePosition))
            {
                return HudClickResult.ExitGame;
            }

            return HudClickResult.Consumed;
        }

        if (_inviteBannerRect != Rectangle.Empty && _inviteBannerRect.Contains(mousePosition))
        {
            if (_inviteAcceptBtnRect.Contains(mousePosition))
            {
                return HudClickResult.AcceptTradeInvite;
            }

            if (_inviteDeclineBtnRect.Contains(mousePosition))
            {
                return HudClickResult.DeclineTradeInvite;
            }

            return HudClickResult.Consumed;
        }

        if (_playerPopupRect != Rectangle.Empty && _playerPopupRect.Contains(mousePosition))
        {
            if (_playerPopupTradeBtnRect.Contains(mousePosition))
            {
                return HudClickResult.WrenchPopupTrade;
            }

            if (_playerPopupGiveAccessBtnRect != Rectangle.Empty && _playerPopupGiveAccessBtnRect.Contains(mousePosition))
            {
                return HudClickResult.WrenchPopupGiveAccess;
            }

            return HudClickResult.Consumed;
        }

        if (_chatToggleRect.Contains(mousePosition))
        {
            IsChatVisible = !IsChatVisible;
            if (IsChatVisible) ResetChatScroll();
            return HudClickResult.Consumed;
        }

        if (_chatInputRect.Contains(mousePosition))
        {
            IsChatVisible = true;
            return HudClickResult.ToggleChatInput;
        }

        if (_warpButtonRect.Contains(mousePosition))
        {
            IsWarpModalOpen = true;
            IsMenuModalOpen = false;
            return HudClickResult.OpenWarpModal;
        }

        if (_menuButtonRect.Contains(mousePosition))
        {
            IsMenuModalOpen = !IsMenuModalOpen;
            IsWarpModalOpen = false;
            return HudClickResult.ToggleMenuModal;
        }

        if (_drawerHandleRect.Contains(mousePosition))
        {
            IsInventoryExpanded = !IsInventoryExpanded;
            return HudClickResult.Consumed;
        }

        if (_dropButtonRect.Contains(mousePosition))
        {
            return HudClickResult.DropSelectedItem;
        }

        for (int i = 0; i < _slotHitboxes.Count; i++)
        {
            if (_slotHitboxes[i].Rect.Contains(mousePosition))
            {
                SelectedSlotIndex = _slotHitboxes[i].SlotIndex;
                return HudClickResult.Consumed;
            }
        }

        return HudClickResult.None;
    }

    /// <summary>
    /// Merender seluruh HUD secara terpisah antara pass UI smooth (<see cref="SamplerState.LinearClamp"/>)
    /// dan pass ikon pixel-art tajam (<see cref="SamplerState.PointClamp"/>).
    /// Dipanggil di luar blok SpriteBatch.Begin/End.
    /// </summary>
    public void Draw(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        in DebugMetrics debugMetrics,
        bool isChatTyping,
        string chatDraft,
        string warpDraft,
        string lockAccessDraft,
        bool isSfxMuted,
        float elapsedTime,
        Point mousePosition = default,
        bool isLeftMouseDown = false,
        WrenchPlayerPopupView? wrenchPlayerPopup = null)
    {
        // Sinkronkan trigger animasi pop-in modal jika modal baru dibuka pada frame yang sama
        int modalKey = GetCurrentModalKey();
        if (modalKey != _activeModalKey)
        {
            _activeModalKey = modalKey;
            _modalAnimElapsed = 0f;
        }

        // Sinkronkan animasi slide-in undangan Trade masuk
        IncomingTradeInviteView? invite = client.PendingTradeInvite;
        if (invite is not null && invite.RemainingSeconds > 0f)
        {
            if (_lastInviteRequesterId != invite.RequesterAccountId)
            {
                _lastInviteRequesterId = invite.RequesterAccountId;
                _inviteAnimElapsed = 0f;
            }
        }
        else
        {
            _lastInviteRequesterId = -1;
            _inviteAnimElapsed = 0f;
        }

        // === LAYER 1: BASE HUD (Bar Atas, Chat, F3 Debug, Banner World, Invite, Wrench Popup, & Inventory Drawer) ===
        _pixelIcons.Clear();
        _countPills.Clear();
        HoveredInventorySlot? hoveredInvTooltip = null;

        BeginSmooth(spriteBatch);
        DrawTopLeftWorldAndChat(spriteBatch, client, isChatTyping, chatDraft, elapsedTime, mousePosition, isLeftMouseDown);
        DrawTopRightCluster(spriteBatch, viewport, client, elapsedTime, mousePosition, isLeftMouseDown);

        if (IsDebugOverlayVisible)
        {
            DrawDebugOverlay(spriteBatch, viewport, in debugMetrics);
        }

        DrawWorldEntryBanner(spriteBatch, viewport);

        if (!IsTradeModalOpen)
        {
            DrawIncomingTradeInviteBanner(spriteBatch, viewport, invite, mousePosition, isLeftMouseDown);
            DrawWrenchPlayerPopup(spriteBatch, viewport, wrenchPlayerPopup, mousePosition, isLeftMouseDown);
            hoveredInvTooltip = DrawCollapsibleInventoryDrawer(spriteBatch, viewport, client.Inventory, mousePosition, isLeftMouseDown, allowHover: !HasOpenModal);
        }
        else
        {
            _inviteBannerRect = Rectangle.Empty;
            _inviteAcceptBtnRect = Rectangle.Empty;
            _inviteDeclineBtnRect = Rectangle.Empty;
            _playerPopupRect = Rectangle.Empty;
            _playerPopupTradeBtnRect = Rectangle.Empty;
            _playerPopupGiveAccessBtnRect = Rectangle.Empty;
        }

        spriteBatch.End();

        // Flush ikon pixel-art Base HUD dengan PointClamp (tajam tanpa buram)
        FlushPixelIcons(spriteBatch);

        // Gambar pil jumlah slot di atas ikon + tooltip / toast jika tidak ada modal aktif
        if (_countPills.Count > 0 || (!HasOpenModal && (hoveredInvTooltip.HasValue || _toastTimer > 0f)))
        {
            BeginSmooth(spriteBatch);
            FlushCountPills(spriteBatch);

            if (!HasOpenModal)
            {
                if (hoveredInvTooltip is { } invTip)
                {
                    DrawInventorySlotTooltip(spriteBatch, viewport, invTip, isTradeMode: false);
                }

                DrawTopCenterToast(spriteBatch, viewport);
            }

            spriteBatch.End();
        }

        if (!HasOpenModal)
        {
            return;
        }

        // === LAYER 2: MODAL AKTIF DENGAN ANIMASI POP-IN CUBIC EASE-OUT (scale 0.94 -> 1.0, alpha 0 -> 1 dalam 130ms) ===
        float popT = CubicEaseOut(_modalAnimElapsed / ModalPopDuration);
        float modalScale = MathHelper.Lerp(0.94f, 1.0f, popT);
        float modalAlpha = popT;

        // Dim backdrop di belakang modal
        BeginSmooth(spriteBatch);
        byte dimAlpha = (byte)Math.Clamp((int)MathF.Round((IsTradeModalOpen ? 152f : 148f) * modalAlpha), 0, 255);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(0, 0, viewport.Width, viewport.Height), Color.FromNonPremultiplied(8, 18, 30, dimAlpha));
        spriteBatch.End();

        // Saat Trade aktif, render Inventory Drawer di atas dim backdrop agar tetap terang & dapat diklik
        if (IsTradeModalOpen)
        {
            _pixelIcons.Clear();
            _countPills.Clear();

            BeginSmooth(spriteBatch);
            hoveredInvTooltip = DrawCollapsibleInventoryDrawer(spriteBatch, viewport, client.Inventory, mousePosition, isLeftMouseDown, allowHover: true);
            spriteBatch.End();

            FlushPixelIcons(spriteBatch);

            if (_countPills.Count > 0)
            {
                BeginSmooth(spriteBatch);
                FlushCountPills(spriteBatch);
                spriteBatch.End();
            }
        }

        // Hitung posisi pusat modal untuk transformasi scale pop-in yang mulus
        Rectangle expectedModalRect = ComputeActiveModalBounds(viewport);
        Matrix modalTransform = popT >= 0.999f
            ? Matrix.Identity
            : CreateCenteredScaleMatrix(expectedModalRect, modalScale);

        _pixelIcons.Clear();
        _countPills.Clear();
        TradeTooltipRequest? tradeTooltip = null;

        BeginSmooth(spriteBatch, modalTransform);
        if (IsTradeModalOpen)
        {
            tradeTooltip = DrawTradeModal(spriteBatch, viewport, client, mousePosition, isLeftMouseDown, elapsedTime, modalAlpha);
        }
        else if (IsWarpModalOpen)
        {
            DrawWarpModal(spriteBatch, viewport, client, warpDraft, elapsedTime, mousePosition, isLeftMouseDown, modalAlpha);
        }
        else if (IsLockModalOpen)
        {
            DrawWorldLockModal(spriteBatch, viewport, client, lockAccessDraft, elapsedTime, mousePosition, isLeftMouseDown, modalAlpha);
        }
        else if (IsMenuModalOpen)
        {
            DrawMenuModal(spriteBatch, viewport, client, isSfxMuted, mousePosition, isLeftMouseDown, modalAlpha);
        }

        spriteBatch.End();

        // Flush ikon pixel-art di dalam modal (gembok World Lock, ikon item Trade 40x40) memakai PointClamp + modalTransform
        FlushPixelIcons(spriteBatch, modalTransform);

        // Flush pil jumlah slot Trade (xCount) di atas ikon 40x40
        if (_countPills.Count > 0)
        {
            BeginSmooth(spriteBatch, modalTransform);
            FlushCountPills(spriteBatch);
            spriteBatch.End();
        }

        // === LAYER 3: TOOLTIP MELAYANG & TOAST DI PALING ATAS (UNSCALED, SMOOTH) ===
        if (tradeTooltip.HasValue || (IsTradeModalOpen && hoveredInvTooltip.HasValue) || _toastTimer > 0f)
        {
            BeginSmooth(spriteBatch);
            if (tradeTooltip is { } tip)
            {
                DrawTradeItemTooltip(spriteBatch, viewport, mousePosition, tip.Slot, tip.IsLocalSlot);
            }
            else if (IsTradeModalOpen && hoveredInvTooltip is { } invTip)
            {
                DrawInventorySlotTooltip(spriteBatch, viewport, invTip, isTradeMode: true);
            }

            DrawTopCenterToast(spriteBatch, viewport);
            spriteBatch.End();
        }
    }

    private void BeginSmooth(SpriteBatch spriteBatch, Matrix? transform = null)
    {
        spriteBatch.Begin(
            sortMode: SpriteSortMode.Deferred,
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.LinearClamp,
            transformMatrix: transform);
    }

    private void FlushPixelIcons(SpriteBatch spriteBatch, Matrix? transform = null)
    {
        if (_pixelIcons.Count == 0)
        {
            return;
        }

        spriteBatch.Begin(
            sortMode: SpriteSortMode.Deferred,
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.PointClamp,
            transformMatrix: transform);

        for (int i = 0; i < _pixelIcons.Count; i++)
        {
            QueuedPixelIcon item = _pixelIcons[i];
            spriteBatch.Draw(item.Texture, item.Dest, item.Tint);
        }

        spriteBatch.End();
        _pixelIcons.Clear();
    }

    private void FlushCountPills(SpriteBatch spriteBatch)
    {
        for (int i = 0; i < _countPills.Count; i++)
        {
            QueuedCountPill pill = _countPills[i];
            _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDark, pill.Rect, alpha: 0.90f * pill.Alpha, cornerSize: 6);
            _uiFonts.DrawCentered(
                spriteBatch,
                pill.Text,
                new Rectangle(pill.Rect.X, pill.Rect.Y, pill.Rect.Width, pill.Rect.Height - 1),
                UiFontManager.PremultiplyAlpha(pill.TextColor, pill.Alpha),
                UiFontSize.Tiny,
                dropShadow: true);
        }

        _countPills.Clear();
    }

    private void QueueIcon(Texture2D texture, Rectangle dest, float alpha = 1f)
    {
        Color tint = alpha >= 0.995f ? Color.White : Color.White * Math.Clamp(alpha, 0f, 1f);
        _pixelIcons.Add(new QueuedPixelIcon(texture, dest, tint));
    }

    private void DrawTopLeftWorldAndChat(
        SpriteBatch spriteBatch,
        GameClient client,
        bool isChatTyping,
        string chatDraft,
        float elapsedTime,
        Point mousePosition,
        bool isLeftMouseDown)
    {
        const int margin = ScreenMargin;
        const int y = ScreenMargin;

        // 1. Tombol toggle chat membulat (42x42)
        _chatToggleRect = new Rectangle(margin, y, TopBarHeight, TopBarHeight);
        UiButtonState chatBtnState = UiNineSlice.EvaluateButtonState(_chatToggleRect, mousePosition, isLeftMouseDown);
        UiPanelStyle chatStyle = IsChatVisible ? UiPanelStyle.BtnBlue : UiPanelStyle.PanelDark;
        Rectangle chatRendered = IsChatVisible
            ? _nineSlice.DrawButton(spriteBatch, chatStyle, _chatToggleRect, chatBtnState)
            : DrawToggleDarkButton(spriteBatch, _chatToggleRect, chatBtnState);
        QueueIcon(_atlas.ChatIcon, new Rectangle(chatRendered.X + 11, chatRendered.Y + 10, 20, 20));

        // 2. Panel World kiri atas (tinggi seragam 42px, jarak 8px dari tombol chat)
        string worldTitle = (client.HasWorldSnapshot ? client.CurrentWorldName : "CONNECTING").ToUpperInvariant();
        int totalPlayers = client.RemotePlayers.Count + (client.HasWorldSnapshot ? 1 : 0);
        string statusText = client.IsWorldLocked
            ? (client.IsWorldPublicLock
                ? $"PUBLIC LOCK  •  {totalPlayers} ONLINE"
                : $"LOCKED: {client.WorldLockOwner.ToUpperInvariant()}  •  {totalPlayers} ONLINE")
            : $"OPEN  •  {totalPlayers} ONLINE";
        Color statusColor = client.IsWorldLocked
            ? (client.IsWorldPublicLock ? new Color(152, 248, 174) : ChatSystemColor)
            : WorldStatusCyan;

        Vector2 titleSize = _uiFonts.MeasureString(worldTitle, UiFontSize.Normal);
        Vector2 statusSize = _uiFonts.MeasureString(statusText, UiFontSize.Tiny);

        const int padX = 12;
        int lockBadgeAdvance = client.IsWorldLocked ? 28 : 0;
        int contentW = (int)MathF.Ceiling(MathF.Max(titleSize.X, statusSize.X));
        int panelW = Math.Max(184, contentW + lockBadgeAdvance + padX * 2);

        _worldPanelRect = new Rectangle(_chatToggleRect.Right + TopBarGap, y, panelW, TopBarHeight);
        _nineSlice.DrawPanel(
            spriteBatch,
            client.IsWorldLocked ? UiPanelStyle.PanelDarkGold : UiPanelStyle.PanelDark,
            _worldPanelRect);

        int textStartX = _worldPanelRect.X + padX;
        if (client.IsWorldLocked)
        {
            // Lencana gembok emas kecil rapi di sisi kiri panel world
            var lockBadge = new Rectangle(_worldPanelRect.X + 8, _worldPanelRect.Y + 8, 24, 24);
            _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.SlotSelected, lockBadge, cornerSize: 7);
            QueueIcon(_atlas.GetItemIcon(TileIds.WorldLock), new Rectangle(lockBadge.X + 4, lockBadge.Y + 4, 16, 16));
            textStartX = lockBadge.Right + 8;
        }

        // Susun 2 baris teks terpusat rapi di dalam tinggi 42px (visual 39px di atas shadow)
        float combinedTextH = titleSize.Y + statusSize.Y - 3f;
        float topY = _worldPanelRect.Y + MathF.Max(3f, (TopBarHeight - 3 - combinedTextH) * 0.5f);
        _uiFonts.DrawString(spriteBatch, worldTitle, new Vector2(textStartX, topY), Color.White, UiFontSize.Normal, dropShadow: true);
        _uiFonts.DrawString(spriteBatch, statusText, new Vector2(textStartX, topY + titleSize.Y - 3f), statusColor, UiFontSize.Tiny, dropShadow: true);

        // 3. Kotak Log Chat (word-wrap berbasis lebar font aktual, fade-out halus, & scroll)
        int consoleX = margin;
        int consoleY = y + TopBarHeight + TopBarGap;
        const int consoleW = 424;
        const int chatPad = 10;
        float wrapWidth = consoleW - chatPad * 2 - 6;

        float effectiveVisibility = isChatTyping ? 1f : _chatVisibilityAlpha;
        if (effectiveVisibility <= 0.02f)
        {
            _chatLogRect = Rectangle.Empty;
            _chatInputRect = Rectangle.Empty;
            return;
        }

        var wrappedRows = new List<(string? Prefix, Color PrefixColor, string Body, Color BodyColor, float FadeAlpha)>();
        IReadOnlyList<ChatLogEntry> log = client.ChatLog;

        for (int i = 0; i < log.Count; i++)
        {
            ChatLogEntry entry = log[i];
            float fade = isChatTyping
                ? 1f
                : entry.AgeSeconds < 12f
                    ? 1f
                    : Math.Clamp((16f - entry.AgeSeconds) / 4f, 0f, 1f);

            if (fade <= 0.01f)
            {
                continue;
            }

            if (entry.Kind == ChatMessageKind.System)
            {
                List<string> lines = _uiFonts.WrapText($">> {entry.Message}", wrapWidth, UiFontSize.Small);
                for (int l = 0; l < lines.Count; l++)
                {
                    wrappedRows.Add((null, Color.Transparent, lines[l], ChatSystemColor, fade));
                }
            }
            else if (entry.Kind == ChatMessageKind.Error)
            {
                List<string> lines = _uiFonts.WrapText($"! {entry.Message}", wrapWidth, UiFontSize.Small);
                for (int l = 0; l < lines.Count; l++)
                {
                    wrappedRows.Add((null, Color.Transparent, lines[l], ChatErrorColor, fade));
                }
            }
            else
            {
                string prefix = $"{entry.SenderName}: ";
                List<string> lines = _uiFonts.WrapText(prefix + entry.Message, wrapWidth, UiFontSize.Small);
                for (int l = 0; l < lines.Count; l++)
                {
                    if (l == 0 && lines[0].StartsWith(prefix, StringComparison.Ordinal))
                    {
                        wrappedRows.Add((prefix, ChatPlayerNameColor, lines[0][prefix.Length..], Color.White, fade));
                    }
                    else
                    {
                        wrappedRows.Add((null, Color.Transparent, lines[l], Color.White, fade));
                    }
                }
            }
        }

        _maxChatScrollOffset = Math.Max(0, wrappedRows.Count - MaxVisibleChatLines);
        _chatScrollOffset = Math.Clamp(_chatScrollOffset, 0, _maxChatScrollOffset);

        int shownCount = Math.Min(MaxVisibleChatLines, wrappedRows.Count);
        int endIndex = wrappedRows.Count - _chatScrollOffset;
        int startIndex = Math.Max(0, endIndex - shownCount);

        int rowStep = _uiFonts.GetLineHeight(UiFontSize.Small) + 2;
        int logBottomY = consoleY;

        if (shownCount > 0 || isChatTyping)
        {
            float maxRowFade = isChatTyping ? 1f : 0f;
            for (int r = startIndex; r < endIndex; r++)
            {
                maxRowFade = MathF.Max(maxRowFade, wrappedRows[r].FadeAlpha);
            }

            float panelAlpha = effectiveVisibility * maxRowFade * (isChatTyping ? 0.96f : 0.78f);
            int visibleRows = Math.Max(1, shownCount);
            int logH = visibleRows * rowStep + chatPad * 2 - 2;
            _chatLogRect = new Rectangle(consoleX, consoleY, consoleW, logH);
            logBottomY = _chatLogRect.Bottom;

            _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDark, _chatLogRect, alpha: panelAlpha);

            if (shownCount == 0)
            {
                Color hintCol = UiFontManager.PremultiplyAlpha(new Color(196, 232, 255), effectiveVisibility * 0.85f);
                _uiFonts.DrawString(spriteBatch, "Type a message or /join <WORLD>...", new Vector2(consoleX + chatPad, consoleY + chatPad), hintCol, UiFontSize.Small);
            }
            else
            {
                for (int r = startIndex; r < endIndex; r++)
                {
                    var row = wrappedRows[r];
                    int lineIdx = r - startIndex;
                    int drawY = consoleY + chatPad + lineIdx * rowStep;
                    float alpha = row.FadeAlpha * effectiveVisibility;

                    if (!string.IsNullOrEmpty(row.Prefix))
                    {
                        Color pCol = UiFontManager.PremultiplyAlpha(row.PrefixColor, alpha);
                        Color bCol = UiFontManager.PremultiplyAlpha(row.BodyColor, alpha);
                        _uiFonts.DrawString(spriteBatch, row.Prefix, new Vector2(consoleX + chatPad, drawY), pCol, UiFontSize.Small);
                        float prefixWidth = _uiFonts.MeasureString(row.Prefix, UiFontSize.Small).X;
                        _uiFonts.DrawString(spriteBatch, row.Body, new Vector2(consoleX + chatPad + prefixWidth, drawY), bCol, UiFontSize.Small);
                    }
                    else
                    {
                        _uiFonts.DrawString(spriteBatch, row.Body, new Vector2(consoleX + chatPad, drawY), UiFontManager.PremultiplyAlpha(row.BodyColor, alpha), UiFontSize.Small);
                    }
                }

                // Indikator scroll halus bila ada riwayat di atas
                if (_maxChatScrollOffset > 0 && isChatTyping)
                {
                    int trackH = Math.Max(14, logH - 16);
                    int thumbH = Math.Max(12, trackH * MaxVisibleChatLines / wrappedRows.Count);
                    float ratio = 1f - ((float)_chatScrollOffset / _maxChatScrollOffset);
                    int thumbY = _chatLogRect.Y + 8 + (int)((trackH - thumbH) * ratio);
                    spriteBatch.Draw(_atlas.Pixel, new Rectangle(_chatLogRect.Right - 6, thumbY, 3, thumbH), Color.FromNonPremultiplied(164, 232, 255, 200));
                }
            }
        }
        else
        {
            _chatLogRect = Rectangle.Empty;
        }

        // Kotak input aktif hanya saat mengetik (ENTER / T)
        if (isChatTyping)
        {
            int inputY = (logBottomY > consoleY ? logBottomY + 6 : consoleY);
            _chatInputRect = new Rectangle(consoleX, inputY, consoleW, 34);
            _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDarkGold, _chatInputRect);

            bool showCursor = ((int)(elapsedTime * 2.4f)) % 2 == 0;
            string shownDraft = _uiFonts.FitToWidth(chatDraft, consoleW - 36f, UiFontSize.Normal, keepTail: true);
            string line = $"> {shownDraft}{(showCursor ? "|" : "")}";
            float textY = _chatInputRect.Y + (_chatInputRect.Height - 2 - _uiFonts.MeasureString(">", UiFontSize.Normal).Y) * 0.5f;
            _uiFonts.DrawString(spriteBatch, line, new Vector2(_chatInputRect.X + 10, textY), Color.White, UiFontSize.Normal);
        }
        else
        {
            _chatInputRect = Rectangle.Empty;
        }
    }

    private void DrawTopRightCluster(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        float elapsedTime,
        Point mousePosition,
        bool isLeftMouseDown)
    {
        int right = viewport.Width - ScreenMargin;
        const int y = ScreenMargin;

        // 1. Tombol Menu persegi membulat (42x42)
        _menuButtonRect = new Rectangle(right - TopBarHeight, y, TopBarHeight, TopBarHeight);
        UiButtonState menuState = UiNineSlice.EvaluateButtonState(_menuButtonRect, mousePosition, isLeftMouseDown);
        Rectangle menuRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnBlue, _menuButtonRect, menuState);
        QueueIcon(_atlas.MenuIcon, new Rectangle(menuRendered.X + 11, menuRendered.Y + 10, 20, 20));

        // 2. Tombol World Warp persegi membulat (42x42, jarak 8px)
        _warpButtonRect = new Rectangle(_menuButtonRect.X - TopBarGap - TopBarHeight, y, TopBarHeight, TopBarHeight);
        UiButtonState warpState = UiNineSlice.EvaluateButtonState(_warpButtonRect, mousePosition, isLeftMouseDown);
        Rectangle warpRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnBlue, _warpButtonRect, warpState);
        QueueIcon(_atlas.GlobeIcon, new Rectangle(warpRendered.X + 9, warpRendered.Y + 8, 24, 24));

        // 3. Pil counter Gem (tinggi seragam 42px, jarak 8px) dengan kilau ikon & angka rata kanan tajam
        string gemsText = client.GemCount.ToString("N0");
        Vector2 gemTextSize = _uiFonts.MeasureString(gemsText, UiFontSize.Title);
        int gemPillW = Math.Max(124, (int)MathF.Ceiling(gemTextSize.X) + 56);
        _gemPillRect = new Rectangle(_warpButtonRect.X - TopBarGap - gemPillW, y, gemPillW, TopBarHeight);

        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDark, _gemPillRect);

        // Kilau lembut berdenyut di belakang ikon Gem (dirender smooth dengan LinearClamp)
        float gemPulse = 0.5f + 0.5f * MathF.Sin(elapsedTime * 3.4f);
        byte glowAlpha = (byte)(70 + (int)(gemPulse * 48f));
        spriteBatch.Draw(
            _atlas.RadialGlowTexture,
            new Rectangle(_gemPillRect.X + 4, _gemPillRect.Y + 2, 34, 34),
            Color.FromNonPremultiplied(90, 255, 195, glowAlpha));

        // Ikon Gem pixel-art tajam (PointClamp)
        QueueIcon(_atlas.GemIcon, new Rectangle(_gemPillRect.X + 9, _gemPillRect.Y + 8, 24, 24));

        // Angka Gem rata kanan tajam
        float gemTextY = _gemPillRect.Y + (_gemPillRect.Height - 3 - gemTextSize.Y) * 0.5f;
        _uiFonts.DrawString(
            spriteBatch,
            gemsText,
            new Vector2(_gemPillRect.Right - gemTextSize.X - 12f, gemTextY),
            new Color(242, 255, 248),
            UiFontSize.Title,
            dropShadow: true);
    }

    private void DrawDebugOverlay(SpriteBatch spriteBatch, Viewport viewport, in DebugMetrics m)
    {
        (string Key, string Val)[] rows =
        [
            ("FPS", m.Fps.ToString()),
            ("WORLD", string.IsNullOrEmpty(m.WorldName) ? "-" : m.WorldName),
            ("PLAYERS", m.PlayerCount.ToString()),
            ("TILES", $"{m.VisibleTiles}/{m.TotalTiles}"),
            ("PING", $"{m.PingMs} ms"),
            ("TILE POS", $"({m.PlayerTileX}, {m.PlayerTileY})"),
        ];

        const int padX = 12;
        const int padY = 9;
        int lineH = _uiFonts.GetLineHeight(UiFontSize.Small) + 2;
        const int keyColW = 76;

        float maxValW = 0f;
        for (int i = 0; i < rows.Length; i++)
        {
            maxValW = MathF.Max(maxValW, _uiFonts.MeasureString(rows[i].Val, UiFontSize.Small).X);
        }

        int panelW = Math.Max(188, padX * 2 + keyColW + (int)MathF.Ceiling(maxValW) + 8);
        int panelH = rows.Length * lineH + padY * 2 + 2;
        var rect = new Rectangle(viewport.Width - panelW - ScreenMargin, ScreenMargin + TopBarHeight + TopBarGap, panelW, panelH);

        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDark, rect, alpha: 0.90f);

        for (int i = 0; i < rows.Length; i++)
        {
            int ry = rect.Y + padY + i * lineH;
            _uiFonts.DrawString(spriteBatch, rows[i].Key, new Vector2(rect.X + padX, ry), WorldStatusCyan, UiFontSize.Small);
            Color valColor = i == 0 ? new Color(138, 248, 156) : Color.White;
            _uiFonts.DrawString(spriteBatch, rows[i].Val, new Vector2(rect.X + padX + keyColW, ry), valColor, UiFontSize.Small);
        }
    }

    private void DrawWorldEntryBanner(SpriteBatch spriteBatch, Viewport viewport)
    {
        if (WorldBannerTimer <= 0f || string.IsNullOrEmpty(WorldBannerTitle))
        {
            return;
        }

        float fadeIn = CubicEaseOut(Math.Clamp((3.8f - WorldBannerTimer) / 0.25f, 0f, 1f));
        float fadeOut = WorldBannerTimer < 0.75f ? CubicEaseOut(Math.Clamp(WorldBannerTimer / 0.75f, 0f, 1f)) : 1f;
        float alpha = MathF.Min(fadeIn, fadeOut);
        if (alpha <= 0.02f)
        {
            return;
        }

        string title = $"WORLD {WorldBannerTitle}";
        Vector2 titleSize = _uiFonts.MeasureString(title, UiFontSize.Header);
        Vector2 subSize = _uiFonts.MeasureString(WorldBannerSubtitle, UiFontSize.Small);

        float slideY = MathHelper.Lerp(-12f, 0f, fadeIn);
        float bx = (viewport.Width - titleSize.X) * 0.5f;
        float by = 72f + slideY;

        _uiFonts.DrawStringOutline(
            spriteBatch,
            title,
            new Vector2(bx, by),
            UiFontManager.PremultiplyAlpha(new Color(255, 242, 130), alpha),
            UiFontSize.Header,
            outlineColor: UiFontManager.PremultiplyAlpha(new Color(14, 34, 58), alpha));

        float sx = (viewport.Width - subSize.X) * 0.5f;
        _uiFonts.DrawString(
            spriteBatch,
            WorldBannerSubtitle,
            new Vector2(sx, by + titleSize.Y + 4f),
            UiFontManager.PremultiplyAlpha(new Color(238, 250, 255), alpha),
            UiFontSize.Small,
            dropShadow: true);
    }

    /// <summary>
    /// Collapsible Inventory Drawer dengan animasi Cubic Ease-Out, tab nama item ringkas di tengah,
    /// tombol pil glossy emas DROP [Q] (otomatis disabled saat memegang Fist/Wrench),
    /// pil gelap di belakang angka stok, hover highlight, dan tooltip nama item.
    /// </summary>
    private HoveredInventorySlot? DrawCollapsibleInventoryDrawer(
        SpriteBatch spriteBatch,
        Viewport viewport,
        IReadOnlyList<InventorySlot> inventory,
        Point mousePosition,
        bool isLeftMouseDown,
        bool allowHover)
    {
        _slotHitboxes.Clear();

        int expandedRows = Math.Clamp((int)MathF.Ceiling((inventory.Count + 2) / (float)SlotsPerRow), 2, 3);
        int compactGridH = SlotSize;
        int expandedGridH = expandedRows * SlotSize + (expandedRows - 1) * SlotGap;

        int gridW = SlotsPerRow * SlotSize + (SlotsPerRow - 1) * SlotGap;
        int drawerW = gridW + 22;
        int compactDrawerH = compactGridH + 20;
        int expandedDrawerH = expandedGridH + 20;

        int currentDrawerH = (int)MathF.Round(MathHelper.Lerp(compactDrawerH, expandedDrawerH, _drawerExpandProgress));
        int drawerX = (viewport.Width - drawerW) / 2;
        int drawerY = viewport.Height - currentDrawerH - 6;

        _drawerBoundsRect = new Rectangle(drawerX, drawerY, drawerW, currentDrawerH);

        // 1. Tab nama item ringkas & proporsional di tengah atas drawer (▲ GLASS ▲ / ▼ GLASS ▼)
        ActiveToolKind activeTool = GetActiveTool(inventory, out ushort activeItemId);
        string activeLabel = IsTradeModalOpen
            ? "CLICK ITEM TO OFFER"
            : activeTool switch
            {
                ActiveToolKind.Fist => "FIST",
                ActiveToolKind.Wrench => "WRENCH",
                _ => ItemCatalog.IsBackgroundItem(activeItemId)
                    ? $"{ItemCatalog.GetName(activeItemId).ToUpperInvariant()} [WALL]"
                    : ItemCatalog.GetName(activeItemId).ToUpperInvariant(),
            };

        Vector2 labelSize = _uiFonts.MeasureString(activeLabel, UiFontSize.Small);
        const int handleH = 26;
        int handleW = Math.Clamp((int)MathF.Ceiling(labelSize.X) + 52, 126, 246);
        _drawerHandleRect = new Rectangle((viewport.Width - handleW) / 2, drawerY - handleH + 4, handleW, handleH);

        UiButtonState handleState = allowHover
            ? UiNineSlice.EvaluateButtonState(_drawerHandleRect, mousePosition, isLeftMouseDown)
            : UiButtonState.Normal;
        Rectangle handleRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnBlue, _drawerHandleRect, handleState, cornerSize: 9);

        _uiFonts.DrawCentered(
            spriteBatch,
            activeLabel,
            new Rectangle(handleRendered.X, handleRendered.Y, handleRendered.Width, handleRendered.Height - 2),
            new Color(255, 244, 148),
            UiFontSize.Small,
            dropShadow: true);

        int leftArrowX = handleRendered.X + 12;
        int rightArrowX = handleRendered.Right - 19;
        int arrowY = handleRendered.Y + 10;
        if (IsInventoryExpanded)
        {
            DrawPixelArrowDown(spriteBatch, leftArrowX, arrowY, Color.White);
            DrawPixelArrowDown(spriteBatch, rightArrowX, arrowY, Color.White);
        }
        else
        {
            DrawPixelArrowUp(spriteBatch, leftArrowX, arrowY, Color.White);
            DrawPixelArrowUp(spriteBatch, rightArrowX, arrowY, Color.White);
        }

        // 2. Tombol pil glossy oranye-emas DROP [Q] sejajar di kanan atas laci (otomatis disabled saat memegang Fist/Wrench/Trade)
        bool canDropSelected = !IsTradeModalOpen && activeTool == ActiveToolKind.Item && ItemCatalog.IsDroppable(activeItemId);
        const int dropBtnW = 84;
        _dropButtonRect = new Rectangle(_drawerBoundsRect.Right - dropBtnW - 8, _drawerHandleRect.Y, dropBtnW, handleH);

        UiButtonState dropState = canDropSelected
            ? (allowHover ? UiNineSlice.EvaluateButtonState(_dropButtonRect, mousePosition, isLeftMouseDown) : UiButtonState.Normal)
            : UiButtonState.Disabled;
        Rectangle dropRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGold, _dropButtonRect, dropState, cornerSize: 9);

        _uiFonts.DrawCentered(
            spriteBatch,
            "DROP [Q]",
            new Rectangle(dropRendered.X, dropRendered.Y, dropRendered.Width, dropRendered.Height - 2),
            canDropSelected ? Color.White : DisabledTextColor,
            UiFontSize.Tiny,
            dropShadow: true);

        // 3. Badan laci inventory 9-slice glossy biru
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelBlueOpaque, _drawerBoundsRect);

        // 4. Grid slot inventory (54x54)
        int rowsToRender = _drawerExpandProgress > 0.03f ? expandedRows : 1;
        int totalSlots = rowsToRender * SlotsPerRow;
        int bottomClipY = _drawerBoundsRect.Bottom - 6;

        HoveredInventorySlot? hoveredTooltip = null;

        for (int i = 0; i < totalSlots; i++)
        {
            int col = i % SlotsPerRow;
            int row = i / SlotsPerRow;
            int sx = drawerX + 11 + col * (SlotSize + SlotGap);
            int sy = drawerY + 9 + row * (SlotSize + SlotGap);

            if (sy + SlotSize > bottomClipY + 4)
            {
                continue;
            }

            var rect = new Rectangle(sx, sy, SlotSize, SlotSize);
            _slotHitboxes.Add((rect, i));

            bool selected = i == SelectedSlotIndex;
            bool hovered = allowHover && rect.Contains(mousePosition);

            _nineSlice.DrawSlot(spriteBatch, rect, selected, hovered, cornerSize: 9);

            // Highlight hover tipis tambahan pada slot terpilih atau biasa
            if (hovered)
            {
                spriteBatch.Draw(
                    _atlas.Pixel,
                    new Rectangle(rect.X + 4, rect.Y + 4, rect.Width - 8, rect.Height - 10),
                    Color.FromNonPremultiplied(255, 255, 255, 18));
            }

            // Nomor shortcut (1..9, 0) di pojok kiri atas
            if (row == 0)
            {
                string keyLabel = col == 9 ? "0" : (col + 1).ToString();
                _uiFonts.DrawString(spriteBatch, keyLabel, new Vector2(sx + 6, sy + 4), new Color(188, 228, 255), UiFontSize.Tiny, dropShadow: true);
            }

            var iconRect = new Rectangle(sx + 15, sy + 14, 24, 24);
            if (i == 0)
            {
                QueueIcon(_atlas.FistIcon, iconRect);
                if (hovered)
                {
                    hoveredTooltip = new HoveredInventorySlot(rect, "Fist", "Tool  •  Punch & Harvest", 0, IsTool: true);
                }
            }
            else if (i == 1)
            {
                QueueIcon(_atlas.WrenchIcon, iconRect);
                if (hovered)
                {
                    hoveredTooltip = new HoveredInventorySlot(rect, "Wrench", "Tool  •  Inspect & Trade", 0, IsTool: true);
                }
            }
            else
            {
                int invIdx = i - 2;
                if (invIdx < inventory.Count)
                {
                    InventorySlot slot = inventory[invIdx];
                    QueueIcon(_atlas.GetItemIcon(slot.ItemId), iconRect);

                    // Pil gelap tipis di belakang angka jumlah agar kontras meski ikon terang (Glass / WhiteDoor)
                    string countText = slot.Count.ToString();
                    Vector2 countSize = _uiFonts.MeasureString(countText, UiFontSize.Tiny);
                    int pillW = Math.Max(18, (int)MathF.Ceiling(countSize.X) + 8);
                    const int pillH = 15;
                    var pillRect = new Rectangle(rect.Right - pillW - 3, rect.Bottom - pillH - 4, pillW, pillH);
                    _countPills.Add(new QueuedCountPill(pillRect, countText, Color.White, 1f));

                    if (hovered)
                    {
                        string category = GetItemCategoryLabel(slot.ItemId);
                        hoveredTooltip = new HoveredInventorySlot(
                            rect,
                            ItemCatalog.GetName(slot.ItemId),
                            $"{category}  •  Owned: {slot.Count}",
                            slot.Count,
                            IsTool: false);
                    }
                }
            }
        }

        return hoveredTooltip;
    }

    private void DrawInventorySlotTooltip(
        SpriteBatch spriteBatch,
        Viewport viewport,
        HoveredInventorySlot tip,
        bool isTradeMode)
    {
        string line1 = tip.Name.ToUpperInvariant();
        string line2 = tip.Subtitle;
        string? line3 = isTradeMode && !tip.IsTool ? "Click: +1  •  Shift+Click: +10" : null;

        float maxW = MathF.Max(
            _uiFonts.MeasureString(line1, UiFontSize.Small).X,
            _uiFonts.MeasureString(line2, UiFontSize.Tiny).X);
        if (line3 is not null)
        {
            maxW = MathF.Max(maxW, _uiFonts.MeasureString(line3, UiFontSize.Tiny).X);
        }

        const int padX = 10;
        const int padY = 6;
        int line1H = _uiFonts.GetLineHeight(UiFontSize.Small);
        int line2H = _uiFonts.GetLineHeight(UiFontSize.Tiny);

        int w = (int)MathF.Ceiling(maxW) + padX * 2;
        int h = padY * 2 + line1H + line2H + (line3 is not null ? line2H + 2 : 0) + 2;

        int tx = tip.SlotRect.X + (tip.SlotRect.Width - w) / 2;
        int ty = tip.SlotRect.Y - h - 8;
        tx = Math.Clamp(tx, 8, Math.Max(8, viewport.Width - w - 8));
        ty = Math.Max(8, ty);

        var rect = new Rectangle(tx, ty, w, h);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDarkGold, rect, alpha: 0.96f, cornerSize: 8);

        int cy = ty + padY;
        _uiFonts.DrawString(spriteBatch, line1, new Vector2(tx + padX, cy), GtGoldBorder, UiFontSize.Small, dropShadow: true);
        cy += line1H;
        _uiFonts.DrawString(spriteBatch, line2, new Vector2(tx + padX, cy), new Color(224, 242, 255), UiFontSize.Tiny, dropShadow: true);
        if (line3 is not null)
        {
            cy += line2H + 1;
            _uiFonts.DrawString(spriteBatch, line3, new Vector2(tx + padX, cy), WorldStatusCyan, UiFontSize.Tiny, dropShadow: true);
        }
    }

    private void DrawWarpModal(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        string warpDraft,
        float elapsedTime,
        Point mousePosition,
        bool isLeftMouseDown,
        float modalAlpha)
    {
        Rectangle bounds = ComputeWarpModalBounds(viewport);
        _modalPanelRect = bounds;
        int x = bounds.X;
        int y = bounds.Y;
        int w = bounds.Width;
        int h = bounds.Height;

        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelBlueOpaque, _modalPanelRect, alpha: modalAlpha);

        _uiFonts.DrawString(
            spriteBatch,
            "ENTER WORLD",
            new Vector2(x + 22, y + 16),
            UiFontManager.PremultiplyAlpha(new Color(255, 240, 124), modalAlpha),
            UiFontSize.Title);

        _modalCloseBtnRect = new Rectangle(x + w - 42, y + 14, 28, 28);
        DrawCloseButton(spriteBatch, _modalCloseBtnRect, mousePosition, isLeftMouseDown, modalAlpha);

        _uiFonts.DrawString(
            spriteBatch,
            "Type any world name (new worlds are created automatically):",
            new Vector2(x + 22, y + 50),
            UiFontManager.PremultiplyAlpha(new Color(224, 244, 255), modalAlpha),
            UiFontSize.Small);

        var inputBox = new Rectangle(x + 22, y + 74, w - 144, 38);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDarkGold, inputBox, alpha: modalAlpha);

        bool cursor = ((int)(elapsedTime * 2.4f)) % 2 == 0;
        string shown = _uiFonts.FitToWidth(warpDraft.ToUpperInvariant(), inputBox.Width - 26f, UiFontSize.Title, keepTail: true);
        float inputTextY = inputBox.Y + (inputBox.Height - 3 - _uiFonts.MeasureString("A", UiFontSize.Title).Y) * 0.5f;
        _uiFonts.DrawString(
            spriteBatch,
            $"{shown}{(cursor ? "|" : "")}",
            new Vector2(inputBox.X + 12, inputTextY),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Title);

        _modalPrimaryBtnRect = new Rectangle(inputBox.Right + 10, y + 74, 90, 38);
        UiButtonState goState = UiNineSlice.EvaluateButtonState(_modalPrimaryBtnRect, mousePosition, isLeftMouseDown);
        Rectangle goRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGreen, _modalPrimaryBtnRect, goState, alpha: modalAlpha);
        _uiFonts.DrawCentered(
            spriteBatch,
            "GO!",
            new Rectangle(goRendered.X, goRendered.Y, goRendered.Width, goRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Title);

        _uiFonts.DrawString(
            spriteBatch,
            $"Current: {client.CurrentWorldName}   •   Quick Worlds:",
            new Vector2(x + 22, y + 126),
            UiFontManager.PremultiplyAlpha(WorldStatusCyan, modalAlpha),
            UiFontSize.Small);

        _quickWorldBtnRects.Clear();
        int btnW = (w - 44 - 24) / QuickWorlds.Length;
        for (int i = 0; i < QuickWorlds.Length; i++)
        {
            var qRect = new Rectangle(x + 22 + i * (btnW + 8), y + 150, btnW, 40);
            _quickWorldBtnRects.Add((qRect, QuickWorlds[i]));

            UiButtonState qState = UiNineSlice.EvaluateButtonState(qRect, mousePosition, isLeftMouseDown);
            Rectangle qRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnBlue, qRect, qState, alpha: modalAlpha);
            _uiFonts.DrawCentered(
                spriteBatch,
                QuickWorlds[i],
                new Rectangle(qRendered.X, qRendered.Y, qRendered.Width, qRendered.Height - 2),
                UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
                UiFontSize.Normal);
        }

        _uiFonts.DrawString(
            spriteBatch,
            "Press ENTER to warp   •   ESC to cancel",
            new Vector2(x + 22, y + h - 30),
            UiFontManager.PremultiplyAlpha(new Color(190, 228, 252), modalAlpha),
            UiFontSize.Small);
    }

    private void DrawTopCenterToast(SpriteBatch spriteBatch, Viewport viewport)
    {
        if (_toastTimer <= 0f || string.IsNullOrEmpty(_toastMessage))
        {
            return;
        }

        float age = _toastDuration - _toastTimer;
        float slideInT = CubicEaseOut(Math.Clamp(age / BannerSlideInDuration, 0f, 1f));
        float fadeOutT = _toastTimer < 0.40f ? CubicEaseOut(Math.Clamp(_toastTimer / 0.40f, 0f, 1f)) : 1f;
        float alpha = MathF.Min(slideInT, fadeOutT);
        if (alpha <= 0.02f)
        {
            return;
        }

        Vector2 msgSize = _uiFonts.MeasureString(_toastMessage, UiFontSize.Normal);
        const int padX = 18;
        const int padY = 9;
        int w = (int)MathF.Ceiling(msgSize.X) + padX * 2;
        int h = (int)MathF.Ceiling(msgSize.Y) + padY * 2 + 2;
        int x = (viewport.Width - w) / 2;

        int baseY = _inviteBannerRect != Rectangle.Empty ? _inviteBannerRect.Bottom + 8 : 14;
        float offsetY = MathHelper.Lerp(-16f, 0f, slideInT) + MathHelper.Lerp(-6f, 0f, fadeOutT);
        int y = (int)MathF.Round(baseY + offsetY);

        UiPanelStyle style = _toastKind switch
        {
            ToastKind.Success => UiPanelStyle.PanelDarkGold,
            ToastKind.Warning => UiPanelStyle.BtnRed,
            _ => UiPanelStyle.PanelDark,
        };
        Color textBase = _toastKind switch
        {
            ToastKind.Warning => new Color(255, 236, 238),
            ToastKind.Success => new Color(255, 246, 162),
            _ => Color.White,
        };

        var rect = new Rectangle(x, y, w, h);
        _nineSlice.DrawPanel(spriteBatch, style, rect, alpha: alpha * 0.96f);
        _uiFonts.DrawCentered(
            spriteBatch,
            _toastMessage,
            new Rectangle(rect.X, rect.Y, rect.Width, rect.Height - 2),
            UiFontManager.PremultiplyAlpha(textBase, alpha),
            UiFontSize.Normal,
            dropShadow: true);
    }

    private void DrawWorldLockModal(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        string accessDraft,
        float elapsedTime,
        Point mousePosition,
        bool isLeftMouseDown,
        float modalAlpha)
    {
        Rectangle bounds = ComputeLockModalBounds(viewport);
        _modalPanelRect = bounds;
        int x = bounds.X;
        int y = bounds.Y;
        int w = bounds.Width;
        int h = bounds.Height;

        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelBlueOpaque, _modalPanelRect, alpha: modalAlpha);

        // Header dengan ikon gembok emas (PointClamp) & tombol tutup X
        QueueIcon(_atlas.GetItemIcon(TileIds.WorldLock), new Rectangle(x + 20, y + 14, 24, 24), modalAlpha);
        _uiFonts.DrawString(
            spriteBatch,
            "WORLD LOCK CONFIG",
            new Vector2(x + 52, y + 16),
            UiFontManager.PremultiplyAlpha(new Color(255, 240, 124), modalAlpha),
            UiFontSize.Title);

        _modalCloseBtnRect = new Rectangle(x + w - 42, y + 14, 28, 28);
        DrawCloseButton(spriteBatch, _modalCloseBtnRect, mousePosition, isLeftMouseDown, modalAlpha);

        WorldLockData lockData = client.LockData;
        string ownerLine = $"OWNER: {lockData.OwnerName}   •   TILE: ({lockData.LockX}, {lockData.LockY})   •   ACCESS: {lockData.AccessList.Count}/{WorldLockData.MaxAccessListCount}";
        _uiFonts.DrawString(
            spriteBatch,
            ownerLine,
            new Vector2(x + 22, y + 48),
            UiFontManager.PremultiplyAlpha(WorldStatusCyan, modalAlpha),
            UiFontSize.Small);

        // 1. Toggle Allow Public Build (ON/OFF)
        _lockPublicBtnRect = new Rectangle(x + 22, y + 72, w - 44, 34);
        UiButtonState pubState = UiNineSlice.EvaluateButtonState(_lockPublicBtnRect, mousePosition, isLeftMouseDown);
        Rectangle pubRendered = _nineSlice.DrawButton(
            spriteBatch,
            lockData.IsPublic ? UiPanelStyle.BtnGreen : UiPanelStyle.BtnBlue,
            _lockPublicBtnRect,
            pubState,
            alpha: modalAlpha);

        string pubLabel = lockData.IsPublic
            ? "ALLOW PUBLIC BUILD: ON  (ANYONE CAN BUILD)"
            : "ALLOW PUBLIC BUILD: OFF  (OWNER & ACCESS LIST ONLY)";
        _uiFonts.DrawCentered(
            spriteBatch,
            pubLabel,
            new Rectangle(pubRendered.X, pubRendered.Y, pubRendered.Width, pubRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Small);

        // 2. Input nama pemain + tombol Add
        _uiFonts.DrawString(
            spriteBatch,
            "Grant build access to player:",
            new Vector2(x + 22, y + 116),
            UiFontManager.PremultiplyAlpha(new Color(224, 244, 255), modalAlpha),
            UiFontSize.Small);

        var inputBox = new Rectangle(x + 22, y + 136, w - 140, 34);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDarkGold, inputBox, alpha: modalAlpha);

        bool cursor = ((int)(elapsedTime * 2.4f)) % 2 == 0;
        string draftShown = _uiFonts.FitToWidth(accessDraft, inputBox.Width - 22f, UiFontSize.Normal, keepTail: true);
        float draftY = inputBox.Y + (inputBox.Height - 3 - _uiFonts.MeasureString("A", UiFontSize.Normal).Y) * 0.5f;
        _uiFonts.DrawString(
            spriteBatch,
            $"{draftShown}{(cursor ? "|" : "")}",
            new Vector2(inputBox.X + 10, draftY),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Normal);

        _lockAddBtnRect = new Rectangle(inputBox.Right + 10, y + 136, 86, 34);
        UiButtonState addState = UiNineSlice.EvaluateButtonState(_lockAddBtnRect, mousePosition, isLeftMouseDown);
        Rectangle addRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGreen, _lockAddBtnRect, addState, alpha: modalAlpha);
        _uiFonts.DrawCentered(
            spriteBatch,
            "ADD",
            new Rectangle(addRendered.X, addRendered.Y, addRendered.Width, addRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Normal);

        // 3. Daftar Access List (2 kolom x maks 10 baris = 20 pemain)
        var listBox = new Rectangle(x + 22, y + 180, w - 44, 194);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDark, listBox, alpha: modalAlpha);

        _lockRemoveBtnRects.Clear();
        if (lockData.AccessList.Count == 0)
        {
            _uiFonts.DrawCentered(
                spriteBatch,
                "No players in access list yet. Type a name above and click ADD.",
                listBox,
                UiFontManager.PremultiplyAlpha(new Color(176, 214, 238), modalAlpha),
                UiFontSize.Small);
        }
        else
        {
            const int cols = 2;
            int colW = (listBox.Width - 22) / cols;
            const int rowH = 18;

            int idx = 0;
            foreach (string entryName in lockData.AccessList)
            {
                if (idx >= WorldLockData.MaxAccessListCount)
                {
                    break;
                }

                int col = idx / 10;
                int row = idx % 10;
                int rx = listBox.X + 9 + col * (colW + 4);
                int ry = listBox.Y + 6 + row * rowH;

                var removeBtn = new Rectangle(rx + colW - 24, ry + 1, 20, 16);
                _lockRemoveBtnRects.Add((removeBtn, entryName));

                UiButtonState remState = UiNineSlice.EvaluateButtonState(removeBtn, mousePosition, isLeftMouseDown);
                Rectangle remRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnRed, removeBtn, remState, alpha: modalAlpha, cornerSize: 5);
                _uiFonts.DrawCentered(
                    spriteBatch,
                    "X",
                    new Rectangle(remRendered.X, remRendered.Y, remRendered.Width, remRendered.Height - 1),
                    UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
                    UiFontSize.Tiny);

                string clippedName = _uiFonts.FitToWidth(entryName, colW - 32f, UiFontSize.Small);
                _uiFonts.DrawString(
                    spriteBatch,
                    clippedName,
                    new Vector2(rx + 4, ry + 1),
                    UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
                    UiFontSize.Small);
                idx++;
            }
        }

        // 4. Footer: tombol Pick Up Lock (BtnGold) + tombol Close (BtnBlue)
        _lockPickUpBtnRect = new Rectangle(x + 22, y + h - 50, 224, 36);
        UiButtonState pickState = UiNineSlice.EvaluateButtonState(_lockPickUpBtnRect, mousePosition, isLeftMouseDown);
        Rectangle pickRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGold, _lockPickUpBtnRect, pickState, alpha: modalAlpha);
        _uiFonts.DrawCentered(
            spriteBatch,
            "PICK UP LOCK",
            new Rectangle(pickRendered.X, pickRendered.Y, pickRendered.Width, pickRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Normal);

        _lockCloseFooterBtnRect = new Rectangle(x + w - 222, y + h - 50, 200, 36);
        UiButtonState closeState = UiNineSlice.EvaluateButtonState(_lockCloseFooterBtnRect, mousePosition, isLeftMouseDown);
        Rectangle closeRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnBlue, _lockCloseFooterBtnRect, closeState, alpha: modalAlpha);
        _uiFonts.DrawCentered(
            spriteBatch,
            "CLOSE [ESC]",
            new Rectangle(closeRendered.X, closeRendered.Y, closeRendered.Width, closeRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Normal);
    }

    private void DrawMenuModal(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        bool isSfxMuted,
        Point mousePosition,
        bool isLeftMouseDown,
        float modalAlpha)
    {
        Rectangle bounds = ComputeMenuModalBounds(viewport);
        _modalPanelRect = bounds;
        int x = bounds.X;
        int y = bounds.Y;
        int w = bounds.Width;
        int h = bounds.Height;

        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelBlueOpaque, _modalPanelRect, alpha: modalAlpha);
        _uiFonts.DrawString(
            spriteBatch,
            "GROWSANDBOX MENU",
            new Vector2(x + 22, y + 16),
            UiFontManager.PremultiplyAlpha(new Color(255, 240, 124), modalAlpha),
            UiFontSize.Title);

        _modalCloseBtnRect = new Rectangle(x + w - 42, y + 14, 28, 28);
        DrawCloseButton(spriteBatch, _modalCloseBtnRect, mousePosition, isLeftMouseDown, modalAlpha);

        // Info ringkas pemain & world di dalam PanelDark
        var infoBox = new Rectangle(x + 22, y + 50, w - 44, 48);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDark, infoBox, alpha: modalAlpha);
        _uiFonts.DrawString(
            spriteBatch,
            $"PLAYER: {client.LocalPlayerName}   •   GEMS: {client.GemCount:N0}",
            new Vector2(infoBox.X + 12, infoBox.Y + 6),
            UiFontManager.PremultiplyAlpha(ChatSystemColor, modalAlpha),
            UiFontSize.Small);
        _uiFonts.DrawString(
            spriteBatch,
            $"WORLD: {client.CurrentWorldName} ({(client.IsWorldLocked ? "Locked: " + client.WorldLockOwner : "Open")})",
            new Vector2(infoBox.X + 12, infoBox.Y + 24),
            UiFontManager.PremultiplyAlpha(WorldStatusCyan, modalAlpha),
            UiFontSize.Small);

        // Daftar kontrol dalam 2 kolom rapi
        (string Key, string Desc)[] controls =
        [
            ("A / D + SPACE", "Move & Jump"),
            ("1 (Fist) / L-Click", "Punch blocks & harvest trees"),
            ("2 (Wrench)", "Inspect tile or click player to Trade"),
            ("3-0 / E / R-Click", "Select & place blocks, walls, seeds"),
            ("Q / DROP", "Drop 1 selected item"),
            ("I / B", "Toggle Inventory Drawer"),
            ("ENTER / TAB", "Chat / Warp World"),
            ("M / F3", "Mute SFX / Debug Overlay"),
            ("Scroll / Ctrl+0", "Zoom (1.0x-2.5x) / Reset 1.75x"),
        ];

        int ctrlStartY = y + 108;
        int ctrlRowH = 19;
        for (int i = 0; i < controls.Length; i++)
        {
            int ry = ctrlStartY + i * ctrlRowH;
            _uiFonts.DrawString(
                spriteBatch,
                controls[i].Key,
                new Vector2(x + 24, ry),
                UiFontManager.PremultiplyAlpha(WorldStatusCyan, modalAlpha),
                UiFontSize.Small);
            _uiFonts.DrawString(
                spriteBatch,
                controls[i].Desc,
                new Vector2(x + 158, ry),
                UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
                UiFontSize.Small);
        }

        // Tombol Toggle Mute / Unmute SFX
        _modalMuteBtnRect = new Rectangle(x + 22, y + h - 98, w - 44, 36);
        UiButtonState muteState = UiNineSlice.EvaluateButtonState(_modalMuteBtnRect, mousePosition, isLeftMouseDown);
        Rectangle muteRendered = _nineSlice.DrawButton(
            spriteBatch,
            isSfxMuted ? UiPanelStyle.BtnRed : UiPanelStyle.BtnBlue,
            _modalMuteBtnRect,
            muteState,
            alpha: modalAlpha);

        string muteLabel = isSfxMuted ? "SFX AUDIO: MUTED  [PRESS M TO UNMUTE]" : "SFX AUDIO: ON  [PRESS M TO MUTE]";
        _uiFonts.DrawCentered(
            spriteBatch,
            muteLabel,
            new Rectangle(muteRendered.X, muteRendered.Y, muteRendered.Width, muteRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(isSfxMuted ? Color.White : new Color(170, 255, 186), modalAlpha),
            UiFontSize.Small);

        // Footer: RESPAWN (Green), LOGOUT (Gold), EXIT GAME (Red)
        const int footerGap = 8;
        int footerBtnW = (w - 44 - footerGap * 2) / 3;
        int footerY = y + h - 52;

        _modalPrimaryBtnRect = new Rectangle(x + 22, footerY, footerBtnW, 36);
        UiButtonState respawnState = UiNineSlice.EvaluateButtonState(_modalPrimaryBtnRect, mousePosition, isLeftMouseDown);
        Rectangle respawnRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGreen, _modalPrimaryBtnRect, respawnState, alpha: modalAlpha);
        _uiFonts.DrawCentered(
            spriteBatch,
            "RESPAWN",
            new Rectangle(respawnRendered.X, respawnRendered.Y, respawnRendered.Width, respawnRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Normal);

        _modalLogoutBtnRect = new Rectangle(_modalPrimaryBtnRect.Right + footerGap, footerY, footerBtnW, 36);
        UiButtonState logoutState = UiNineSlice.EvaluateButtonState(_modalLogoutBtnRect, mousePosition, isLeftMouseDown);
        Rectangle logoutRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGold, _modalLogoutBtnRect, logoutState, alpha: modalAlpha);
        _uiFonts.DrawCentered(
            spriteBatch,
            "LOGOUT",
            new Rectangle(logoutRendered.X, logoutRendered.Y, logoutRendered.Width, logoutRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Normal);

        _modalSecondaryBtnRect = new Rectangle(_modalLogoutBtnRect.Right + footerGap, footerY, footerBtnW, 36);
        UiButtonState exitState = UiNineSlice.EvaluateButtonState(_modalSecondaryBtnRect, mousePosition, isLeftMouseDown);
        Rectangle exitRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnRed, _modalSecondaryBtnRect, exitState, alpha: modalAlpha);
        _uiFonts.DrawCentered(
            spriteBatch,
            "EXIT GAME",
            new Rectangle(exitRendered.X, exitRendered.Y, exitRendered.Width, exitRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Normal);
    }

    /// <summary>
    /// Banner undangan Trade non-intrusif dengan animasi slide-in / fade-out Cubic Ease-Out.
    /// </summary>
    private void DrawIncomingTradeInviteBanner(
        SpriteBatch spriteBatch,
        Viewport viewport,
        IncomingTradeInviteView? invite,
        Point mousePosition,
        bool isLeftMouseDown)
    {
        if (invite is null || invite.RemainingSeconds <= 0f)
        {
            _inviteBannerRect = Rectangle.Empty;
            _inviteAcceptBtnRect = Rectangle.Empty;
            _inviteDeclineBtnRect = Rectangle.Empty;
            return;
        }

        float slideInT = CubicEaseOut(Math.Clamp(_inviteAnimElapsed / BannerSlideInDuration, 0f, 1f));
        float fadeOutT = invite.RemainingSeconds < 0.45f
            ? CubicEaseOut(Math.Clamp(invite.RemainingSeconds / 0.45f, 0f, 1f))
            : 1f;
        float alpha = MathF.Min(slideInT, fadeOutT);

        string title = $"{invite.RequesterName} mengajak Trade!";
        string timerLine = $"Berakhir dalam {Math.Max(1, (int)MathF.Ceiling(invite.RemainingSeconds))}s";
        Vector2 titleSize = _uiFonts.MeasureString(title, UiFontSize.Normal);

        const int btnW = 84;
        const int btnH = 30;
        const int btnGap = 6;
        int textAreaW = Math.Max(198, (int)MathF.Ceiling(titleSize.X) + 14);
        int w = 48 + textAreaW + btnW * 2 + btnGap + 20;
        const int h = 58;
        int x = (viewport.Width - w) / 2;
        int y = (int)MathF.Round(12f + MathHelper.Lerp(-20f, 0f, slideInT));

        _inviteBannerRect = new Rectangle(x, y, w, h);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDarkGold, _inviteBannerRect, alpha: alpha);

        // Lencana ikon tukar panah 2 arah
        var badgeRect = new Rectangle(x + 12, y + 12, 28, 28);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelBlue, badgeRect, alpha: alpha, cornerSize: 7);
        DrawPixelTradeArrows(spriteBatch, badgeRect.X + 7, badgeRect.Y + 8, UiFontManager.PremultiplyAlpha(GtGoldBorder, alpha));

        int tx = badgeRect.Right + 10;
        _uiFonts.DrawString(spriteBatch, title, new Vector2(tx, y + 9), UiFontManager.PremultiplyAlpha(Color.White, alpha), UiFontSize.Normal);
        _uiFonts.DrawString(spriteBatch, timerLine, new Vector2(tx, y + 29), UiFontManager.PremultiplyAlpha(WorldStatusCyan, alpha), UiFontSize.Tiny);

        // Tombol ACCEPT (BtnGreen) & DECLINE (BtnRed)
        int btnY = y + 11;
        _inviteDeclineBtnRect = new Rectangle(x + w - 12 - btnW, btnY, btnW, btnH);
        _inviteAcceptBtnRect = new Rectangle(_inviteDeclineBtnRect.X - btnGap - btnW, btnY, btnW, btnH);

        UiButtonState accState = UiNineSlice.EvaluateButtonState(_inviteAcceptBtnRect, mousePosition, isLeftMouseDown);
        Rectangle accRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGreen, _inviteAcceptBtnRect, accState, alpha: alpha, cornerSize: 8);
        _uiFonts.DrawCentered(
            spriteBatch,
            "ACCEPT",
            new Rectangle(accRendered.X, accRendered.Y, accRendered.Width, accRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, alpha),
            UiFontSize.Small);

        UiButtonState decState = UiNineSlice.EvaluateButtonState(_inviteDeclineBtnRect, mousePosition, isLeftMouseDown);
        Rectangle decRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnRed, _inviteDeclineBtnRect, decState, alpha: alpha, cornerSize: 8);
        _uiFonts.DrawCentered(
            spriteBatch,
            "DECLINE",
            new Rectangle(decRendered.X, decRendered.Y, decRendered.Width, decRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, alpha),
            UiFontSize.Small);

        // Progress bar timer 15 detik
        int barX = x + 12;
        int barY = y + h - 9;
        int barW = w - 24;
        const int barH = 4;
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(barX, barY, barW, barH), Color.FromNonPremultiplied(10, 30, 50, (int)(220 * alpha)));
        int fillW = Math.Max(0, (int)MathF.Round(barW * invite.Progress));
        if (fillW > 0)
        {
            Color barColor = invite.RemainingSeconds <= 4f ? new Color(255, 132, 92) : GtGoldBorder;
            spriteBatch.Draw(_atlas.Pixel, new Rectangle(barX, barY, fillW, barH), UiFontManager.PremultiplyAlpha(barColor, alpha));
        }
    }

    private void DrawWrenchPlayerPopup(
        SpriteBatch spriteBatch,
        Viewport viewport,
        WrenchPlayerPopupView? popup,
        Point mousePosition,
        bool isLeftMouseDown)
    {
        if (popup is null)
        {
            _playerPopupRect = Rectangle.Empty;
            _playerPopupTradeBtnRect = Rectangle.Empty;
            _playerPopupGiveAccessBtnRect = Rectangle.Empty;
            return;
        }

        WrenchPlayerPopupView p = popup.Value;
        string header = p.TargetPlayerName.ToUpperInvariant();
        Vector2 headerSize = _uiFonts.MeasureString(header, UiFontSize.Small);

        int w = Math.Max(158, (int)MathF.Ceiling(headerSize.X) + 32);
        int h = p.CanGiveWorldLockAccess ? 108 : 72;

        int px = (int)MathF.Round(p.ScreenAnchor.X - w * 0.5f);
        int py = (int)MathF.Round(p.ScreenAnchor.Y - h - 22f);
        px = Math.Clamp(px, 10, Math.Max(10, viewport.Width - w - 10));
        py = Math.Clamp(py, 10, Math.Max(10, viewport.Height - h - 96));

        _playerPopupRect = new Rectangle(px, py, w, h);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDarkGold, _playerPopupRect);

        _uiFonts.DrawCentered(
            spriteBatch,
            header,
            new Rectangle(px + 8, py + 6, w - 16, 20),
            ChatPlayerNameColor,
            UiFontSize.Small);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(px + 10, py + 27, w - 20, 1), Color.FromNonPremultiplied(126, 218, 255, 95));

        _playerPopupTradeBtnRect = new Rectangle(px + 12, py + 33, w - 24, 30);
        UiButtonState tradeState = UiNineSlice.EvaluateButtonState(_playerPopupTradeBtnRect, mousePosition, isLeftMouseDown);
        Rectangle tradeRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGreen, _playerPopupTradeBtnRect, tradeState, cornerSize: 8);
        _uiFonts.DrawCentered(
            spriteBatch,
            "TRADE",
            new Rectangle(tradeRendered.X, tradeRendered.Y, tradeRendered.Width, tradeRendered.Height - 2),
            Color.White,
            UiFontSize.Small);

        if (p.CanGiveWorldLockAccess)
        {
            _playerPopupGiveAccessBtnRect = new Rectangle(px + 12, py + 68, w - 24, 30);
            UiButtonState accState = UiNineSlice.EvaluateButtonState(_playerPopupGiveAccessBtnRect, mousePosition, isLeftMouseDown);
            Rectangle accRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGold, _playerPopupGiveAccessBtnRect, accState, cornerSize: 8);
            _uiFonts.DrawCentered(
                spriteBatch,
                "GIVE ACCESS",
                new Rectangle(accRendered.X, accRendered.Y, accRendered.Width, accRendered.Height - 2),
                Color.White,
                UiFontSize.Small);
        }
        else
        {
            _playerPopupGiveAccessBtnRect = Rectangle.Empty;
        }
    }

    private TradeTooltipRequest? DrawTradeModal(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        Point mousePosition,
        bool isLeftMouseDown,
        float elapsedTime,
        float modalAlpha)
    {
        ActiveTradeView? trade = client.ActiveTrade;
        if (trade is null)
        {
            return null;
        }

        bool localIsA = (trade.AccountIdA > 0 && trade.AccountIdA == client.LocalAccountId) ||
                        string.Equals(trade.NameA, client.LocalPlayerName, StringComparison.OrdinalIgnoreCase);

        string myName = localIsA ? trade.NameA : trade.NameB;
        string partnerName = localIsA ? trade.NameB : trade.NameA;
        TradeOfferSlot[] myOffers = localIsA ? trade.OffersA : trade.OffersB;
        TradeOfferSlot[] partnerOffers = localIsA ? trade.OffersB : trade.OffersA;
        bool myAccepted = localIsA ? trade.AcceptedA : trade.AcceptedB;
        bool partnerAccepted = localIsA ? trade.AcceptedB : trade.AcceptedA;
        bool myConfirmed = localIsA ? trade.ConfirmedA : trade.ConfirmedB;
        bool partnerConfirmed = localIsA ? trade.ConfirmedB : trade.ConfirmedA;
        float cooldown = trade.LockCooldownRemaining;
        bool cooldownActive = cooldown > 0.01f;
        bool bothAccepted = myAccepted && partnerAccepted && !cooldownActive;

        Rectangle bounds = ComputeTradeModalBounds(viewport);
        _modalPanelRect = bounds;
        int x = bounds.X;
        int y = bounds.Y;
        int w = bounds.Width;
        int h = bounds.Height;

        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelBlueOpaque, _modalPanelRect, alpha: modalAlpha);

        // === HEADER ===
        _uiFonts.DrawString(
            spriteBatch,
            "TRADE",
            new Vector2(x + 22, y + 14),
            UiFontManager.PremultiplyAlpha(GtGoldBorder, modalAlpha),
            UiFontSize.Title);

        string myLabel = myName.ToUpperInvariant();
        string partnerLabel = partnerName.ToUpperInvariant();
        Vector2 myLabelSize = _uiFonts.MeasureString(myLabel, UiFontSize.Normal);
        Vector2 partnerLabelSize = _uiFonts.MeasureString(partnerLabel, UiFontSize.Normal);
        const int arrowGapW = 30;
        int pairTotalW = (int)MathF.Ceiling(myLabelSize.X + arrowGapW + partnerLabelSize.X);
        int pairStartX = x + (w - pairTotalW) / 2 + 12;
        int pairY = y + 17;

        _uiFonts.DrawString(spriteBatch, myLabel, new Vector2(pairStartX, pairY), UiFontManager.PremultiplyAlpha(ChatPlayerNameColor, modalAlpha), UiFontSize.Normal);
        DrawPixelTradeArrows(spriteBatch, pairStartX + (int)MathF.Ceiling(myLabelSize.X) + 8, pairY + 3, UiFontManager.PremultiplyAlpha(GtGoldBorder, modalAlpha));
        _uiFonts.DrawString(spriteBatch, partnerLabel, new Vector2(pairStartX + (int)MathF.Ceiling(myLabelSize.X) + arrowGapW, pairY), UiFontManager.PremultiplyAlpha(Color.White, modalAlpha), UiFontSize.Normal);

        _modalCloseBtnRect = new Rectangle(x + w - 42, y + 13, 28, 28);
        DrawCloseButton(spriteBatch, _modalCloseBtnRect, mousePosition, isLeftMouseDown, modalAlpha);

        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x + 18, y + 48, w - 36, 1), Color.FromNonPremultiplied(126, 218, 255, (int)(95 * modalAlpha)));

        // === DUA KOLOM PENAWARAN ===
        const int colW = 290;
        const int colH = 276;
        int colY = y + 56;
        int leftColX = x + 20;
        int rightColX = x + w - 20 - colW;

        var leftColRect = new Rectangle(leftColX, colY, colW, colH);
        var rightColRect = new Rectangle(rightColX, colY, colW, colH);

        _nineSlice.DrawPanel(spriteBatch, myConfirmed || myAccepted ? UiPanelStyle.PanelDarkGold : UiPanelStyle.PanelDark, leftColRect, alpha: modalAlpha);
        _nineSlice.DrawPanel(spriteBatch, partnerConfirmed || partnerAccepted ? UiPanelStyle.PanelDarkGold : UiPanelStyle.PanelDark, rightColRect, alpha: modalAlpha);

        // Panah pemisah di tengah kedua kolom
        int midX = (leftColRect.Right + rightColRect.Left) / 2;
        DrawPixelTradeArrows(spriteBatch, midX - 7, colY + 118, UiFontManager.PremultiplyAlpha(bothAccepted ? GtGoldBorder : GtBorderBright, modalAlpha));

        _uiFonts.DrawCentered(
            spriteBatch,
            "YOUR OFFER",
            new Rectangle(leftColX, colY + 6, colW, 20),
            UiFontManager.PremultiplyAlpha(ChatPlayerNameColor, modalAlpha),
            UiFontSize.Small);

        string partnerHeader = _uiFonts.FitToWidth($"{partnerLabel}'S OFFER", colW - 20f, UiFontSize.Small);
        _uiFonts.DrawCentered(
            spriteBatch,
            partnerHeader,
            new Rectangle(rightColX, colY + 6, colW, 20),
            UiFontManager.PremultiplyAlpha(new Color(255, 240, 168), modalAlpha),
            UiFontSize.Small);

        // === GRID 2x2 SLOT BESAR (72x72) ===
        const int offerSlotSize = 72;
        const int offerSlotGap = 12;
        int gridTotalW = offerSlotSize * 2 + offerSlotGap;
        int leftGridX = leftColX + (colW - gridTotalW) / 2;
        int rightGridX = rightColX + (colW - gridTotalW) / 2;
        int gridStartY = colY + 30;

        TradeTooltipRequest? tooltipRequest = null;
        FocusedTradeSlotIndex = Math.Clamp(FocusedTradeSlotIndex, 0, 3);

        for (int i = 0; i < 4; i++)
        {
            int col = i % 2;
            int row = i / 2;

            // Sisi Kiri (Penawaranmu)
            int lsx = leftGridX + col * (offerSlotSize + offerSlotGap);
            int lsy = gridStartY + row * (offerSlotSize + offerSlotGap);
            var localRect = new Rectangle(lsx, lsy, offerSlotSize, offerSlotSize);
            _tradeLocalSlotRects[i] = localRect;

            bool localHover = localRect.Contains(mousePosition);
            bool localFocused = i == FocusedTradeSlotIndex;
            TradeOfferSlot localSlot = myOffers[i];

            DrawTradeOfferSlotBox(spriteBatch, localRect, localSlot, i + 1, localFocused, localHover, isReadOnly: false, modalAlpha);
            if (localHover && !localSlot.IsEmpty)
            {
                tooltipRequest = new TradeTooltipRequest(localSlot, IsLocalSlot: true);
            }

            // Sisi Kanan (Penawaran Lawan - Read-Only)
            int rsx = rightGridX + col * (offerSlotSize + offerSlotGap);
            int rsy = gridStartY + row * (offerSlotSize + offerSlotGap);
            var partnerRect = new Rectangle(rsx, rsy, offerSlotSize, offerSlotSize);
            _tradePartnerSlotRects[i] = partnerRect;

            bool partnerHover = partnerRect.Contains(mousePosition);
            TradeOfferSlot partnerSlot = partnerOffers[i];

            DrawTradeOfferSlotBox(spriteBatch, partnerRect, partnerSlot, i + 1, isFocused: false, partnerHover, isReadOnly: true, modalAlpha);
            if (partnerHover && !partnerSlot.IsEmpty)
            {
                tooltipRequest = new TradeTooltipRequest(partnerSlot, IsLocalSlot: false);
            }
        }

        // === KONTROL CEPAT (-1, +1, +10, MAX, CLR) ===
        bool hasFocusedOrSelectedTradeable = !myOffers[FocusedTradeSlotIndex].IsEmpty;
        if (!hasFocusedOrSelectedTradeable)
        {
            ActiveToolKind activeTool = GetActiveTool(client.Inventory, out ushort invItemId);
            hasFocusedOrSelectedTradeable = activeTool == ActiveToolKind.Item && ItemCatalog.IsTradeable(invItemId);
        }

        _tradeQuickBtnRects.Clear();
        (string Label, TradeQuickAdjustKind Kind, bool IsDanger)[] quickDefs =
        [
            ("-1", TradeQuickAdjustKind.MinusOne, false),
            ("+1", TradeQuickAdjustKind.PlusOne, false),
            ("+10", TradeQuickAdjustKind.PlusTen, false),
            ("MAX", TradeQuickAdjustKind.Max, false),
            ("CLR", TradeQuickAdjustKind.Clear, true),
        ];

        int quickY = gridStartY + offerSlotSize * 2 + offerSlotGap + 10;
        const int quickBtnW = 49;
        const int quickBtnH = 24;
        const int quickGap = 5;
        int quickTotalW = quickDefs.Length * quickBtnW + (quickDefs.Length - 1) * quickGap;
        int quickStartX = leftColX + (colW - quickTotalW) / 2;

        for (int q = 0; q < quickDefs.Length; q++)
        {
            var qRect = new Rectangle(quickStartX + q * (quickBtnW + quickGap), quickY, quickBtnW, quickBtnH);
            bool enabled = quickDefs[q].Kind is TradeQuickAdjustKind.MinusOne or TradeQuickAdjustKind.Clear
                ? !myOffers[FocusedTradeSlotIndex].IsEmpty
                : hasFocusedOrSelectedTradeable;
            _tradeQuickBtnRects.Add((qRect, quickDefs[q].Kind, enabled));

            UiButtonState qState = UiNineSlice.EvaluateButtonState(qRect, mousePosition, isLeftMouseDown, enabled);
            UiPanelStyle qStyle = quickDefs[q].IsDanger ? UiPanelStyle.BtnRed : UiPanelStyle.BtnBlue;
            Rectangle qRendered = _nineSlice.DrawButton(spriteBatch, qStyle, qRect, qState, alpha: modalAlpha, cornerSize: 7);

            _uiFonts.DrawCentered(
                spriteBatch,
                quickDefs[q].Label,
                new Rectangle(qRendered.X, qRendered.Y, qRendered.Width, qRendered.Height - 2),
                UiFontManager.PremultiplyAlpha(enabled ? Color.White : DisabledTextColor, modalAlpha),
                UiFontSize.Tiny);
        }

        _uiFonts.DrawCentered(
            spriteBatch,
            "Hover any slot to inspect item",
            new Rectangle(rightColX, quickY, colW, quickBtnH),
            UiFontManager.PremultiplyAlpha(new Color(172, 216, 242), modalAlpha),
            UiFontSize.Tiny);

        // === BADGE STATUS DI BAWAH MASING-MASING SISI ===
        int badgeY = colY + colH - 36;
        DrawTradeStatusBadge(spriteBatch, new Rectangle(leftColX + 18, badgeY, colW - 36, 26), myAccepted, myConfirmed, modalAlpha);
        DrawTradeStatusBadge(spriteBatch, new Rectangle(rightColX + 18, badgeY, colW - 36, 26), partnerAccepted, partnerConfirmed, modalAlpha);

        // === TOMBOL AKSI BAWAH ===
        int footerY = colY + colH + 12;
        const int footerH = 40;
        const int cancelW = 204;
        const int footerGap = 12;
        int primaryW = w - 40 - cancelW - footerGap;

        _tradePrimaryBtnRect = new Rectangle(x + 20, footerY, primaryW, footerH);
        _tradeCancelBtnRect = new Rectangle(_tradePrimaryBtnRect.Right + footerGap, footerY, cancelW, footerH);

        if (cooldownActive)
        {
            _tradePrimaryBtnDisabled = true;
            _tradePrimaryIsFinalConfirm = false;

            Rectangle waitRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnDisabled, _tradePrimaryBtnRect, UiButtonState.Disabled, alpha: modalAlpha);
            _uiFonts.DrawCentered(
                spriteBatch,
                $"WAIT ({cooldown:0.0}s)",
                new Rectangle(waitRendered.X, waitRendered.Y, waitRendered.Width, waitRendered.Height - 2),
                UiFontManager.PremultiplyAlpha(new Color(216, 230, 242), modalAlpha),
                UiFontSize.Title);
        }
        else if (bothAccepted)
        {
            _tradePrimaryIsFinalConfirm = true;
            _tradePrimaryBtnDisabled = myConfirmed;

            if (myConfirmed)
            {
                Rectangle wpRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGold, _tradePrimaryBtnRect, UiButtonState.Normal, alpha: modalAlpha * 0.78f);
                _uiFonts.DrawCentered(
                    spriteBatch,
                    "WAITING FOR PARTNER...",
                    new Rectangle(wpRendered.X, wpRendered.Y, wpRendered.Width, wpRendered.Height - 2),
                    UiFontManager.PremultiplyAlpha(new Color(255, 246, 198), modalAlpha),
                    UiFontSize.Normal);
            }
            else
            {
                UiButtonState finalState = UiNineSlice.EvaluateButtonState(_tradePrimaryBtnRect, mousePosition, isLeftMouseDown);
                Rectangle finalRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGold, _tradePrimaryBtnRect, finalState, alpha: modalAlpha);

                // Kilatan cahaya horizontal halus bergerak melintasi tombol emas
                int glintW = 54;
                float sweep = (elapsedTime * 1.15f) % 1.45f;
                if (sweep <= 1.0f)
                {
                    int gx = finalRendered.X + 6 + (int)((finalRendered.Width - glintW - 12) * sweep);
                    spriteBatch.Draw(
                        _atlas.Pixel,
                        new Rectangle(gx, finalRendered.Y + 4, glintW, finalRendered.Height - 10),
                        Color.FromNonPremultiplied(255, 255, 224, (int)(56 * modalAlpha)));
                }

                _uiFonts.DrawCentered(
                    spriteBatch,
                    "DO THE TRADE!",
                    new Rectangle(finalRendered.X, finalRendered.Y, finalRendered.Width, finalRendered.Height - 2),
                    UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
                    UiFontSize.Title);
            }
        }
        else
        {
            _tradePrimaryBtnDisabled = false;
            _tradePrimaryIsFinalConfirm = false;

            UiButtonState accState = UiNineSlice.EvaluateButtonState(_tradePrimaryBtnRect, mousePosition, isLeftMouseDown);
            Rectangle accRendered = _nineSlice.DrawButton(
                spriteBatch,
                myAccepted ? UiPanelStyle.BtnBlue : UiPanelStyle.BtnGreen,
                _tradePrimaryBtnRect,
                accState,
                alpha: modalAlpha);

            _uiFonts.DrawCentered(
                spriteBatch,
                myAccepted ? "ACCEPTED (CLICK TO UNDO)" : "ACCEPT",
                new Rectangle(accRendered.X, accRendered.Y, accRendered.Width, accRendered.Height - 2),
                UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
                myAccepted ? UiFontSize.Normal : UiFontSize.Title);
        }

        // Tombol merah [ CANCEL ]
        UiButtonState cancelState = UiNineSlice.EvaluateButtonState(_tradeCancelBtnRect, mousePosition, isLeftMouseDown);
        Rectangle cancelRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnRed, _tradeCancelBtnRect, cancelState, alpha: modalAlpha);
        _uiFonts.DrawCentered(
            spriteBatch,
            "CANCEL [ESC]",
            new Rectangle(cancelRendered.X, cancelRendered.Y, cancelRendered.Width, cancelRendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, modalAlpha),
            UiFontSize.Normal);

        // Baris petunjuk anti-scam
        string footerHint = cooldownActive
            ? "Penawaran berubah! Periksa kembali semua item sebelum menekan ACCEPT."
            : bothAccepted
                ? "Tinjau ulang seluruh 4 slot lawan sebelum menekan DO THE TRADE!"
                : "Klik item di inventory bawah (+1 / Shift:+10) atau klik slotmu untuk mengurangi.";
        Color hintColor = cooldownActive
            ? new Color(255, 198, 118)
            : bothAccepted
                ? GtGoldBorder
                : new Color(188, 228, 252);
        _uiFonts.DrawCentered(
            spriteBatch,
            footerHint,
            new Rectangle(x + 16, y + h - 26, w - 32, 20),
            UiFontManager.PremultiplyAlpha(hintColor, modalAlpha),
            UiFontSize.Tiny);

        return tooltipRequest;
    }

    private void DrawTradeOfferSlotBox(
        SpriteBatch spriteBatch,
        Rectangle rect,
        TradeOfferSlot slot,
        int slotNumber,
        bool isFocused,
        bool isHovered,
        bool isReadOnly,
        float modalAlpha)
    {
        bool highlightSelected = (isFocused && !isReadOnly) || isHovered;
        _nineSlice.DrawSlot(spriteBatch, rect, selected: highlightSelected, hovered: isHovered, alpha: modalAlpha, cornerSize: 10);

        _uiFonts.DrawString(
            spriteBatch,
            slotNumber.ToString(),
            new Vector2(rect.X + 7, rect.Y + 4),
            UiFontManager.PremultiplyAlpha(new Color(172, 218, 248), modalAlpha * 0.80f),
            UiFontSize.Tiny);

        if (slot.IsEmpty)
        {
            _uiFonts.DrawCentered(
                spriteBatch,
                "EMPTY",
                rect,
                UiFontManager.PremultiplyAlpha(new Color(128, 174, 206), modalAlpha * 0.72f),
                UiFontSize.Tiny,
                dropShadow: false);
            return;
        }

        // Ikon item 40x40 masuk antrean PointClamp agar pixel-art tetap tajam!
        Texture2D icon = _atlas.GetItemIcon(slot.ItemId);
        var iconRect = new Rectangle(rect.X + (rect.Width - 40) / 2, rect.Y + 12, 40, 40);
        QueueIcon(icon, iconRect, modalAlpha);

        // Pil jumlah xCount di kanan bawah (digambar di atas ikon pada pass smooth pasca-ikon)
        string qtyText = $"x{slot.Count}";
        Vector2 qtySize = _uiFonts.MeasureString(qtyText, UiFontSize.Tiny);
        int badgeW = Math.Max(22, (int)MathF.Ceiling(qtySize.X) + 10);
        const int badgeH = 16;
        var qtyBadge = new Rectangle(rect.Right - badgeW - 4, rect.Bottom - badgeH - 5, badgeW, badgeH);
        _countPills.Add(new QueuedCountPill(qtyBadge, qtyText, GtGoldBorder, modalAlpha));
    }

    private void DrawTradeStatusBadge(SpriteBatch spriteBatch, Rectangle rect, bool accepted, bool confirmed, float modalAlpha)
    {
        UiPanelStyle style;
        Color textColor;
        string text;
        bool showCheck;

        if (confirmed)
        {
            style = UiPanelStyle.BtnGold;
            textColor = Color.White;
            text = "Confirmed";
            showCheck = true;
        }
        else if (accepted)
        {
            style = UiPanelStyle.BtnGreen;
            textColor = Color.White;
            text = "Accepted";
            showCheck = true;
        }
        else
        {
            style = UiPanelStyle.PanelDark;
            textColor = new Color(188, 226, 250);
            text = "Editing...";
            showCheck = false;
        }

        _nineSlice.DrawPanel(spriteBatch, style, rect, alpha: modalAlpha, cornerSize: 8);

        Vector2 textSize = _uiFonts.MeasureString(text, UiFontSize.Small);
        int checkExtraW = showCheck ? 14 : 0;
        float contentW = textSize.X + checkExtraW;
        float startX = rect.X + (rect.Width - contentW) * 0.5f;
        float textY = rect.Y + (rect.Height - 2 - textSize.Y) * 0.5f;

        _uiFonts.DrawString(spriteBatch, text, new Vector2(startX, textY), UiFontManager.PremultiplyAlpha(textColor, modalAlpha), UiFontSize.Small);
        if (showCheck)
        {
            DrawPixelCheckmark(
                spriteBatch,
                (int)MathF.Round(startX + textSize.X + 5f),
                rect.Y + (rect.Height - 8) / 2,
                UiFontManager.PremultiplyAlpha(confirmed ? new Color(255, 248, 176) : Color.White, modalAlpha));
        }
    }

    private void DrawTradeItemTooltip(
        SpriteBatch spriteBatch,
        Viewport viewport,
        Point mousePosition,
        TradeOfferSlot slot,
        bool isLocalSlot)
    {
        string itemName = ItemCatalog.GetName(slot.ItemId);
        string category = GetItemCategoryLabel(slot.ItemId);
        string line1 = itemName.ToUpperInvariant();
        string line2 = $"Amount: {slot.Count}  •  {category}";
        string? line3 = isLocalSlot ? "Click: -1  •  R-Click/Shift: Clear" : null;

        float maxW = MathF.Max(
            _uiFonts.MeasureString(line1, UiFontSize.Small).X,
            _uiFonts.MeasureString(line2, UiFontSize.Tiny).X);
        if (line3 is not null)
        {
            maxW = MathF.Max(maxW, _uiFonts.MeasureString(line3, UiFontSize.Tiny).X);
        }

        const int padX = 11;
        const int padY = 8;
        int line1H = _uiFonts.GetLineHeight(UiFontSize.Small);
        int line2H = _uiFonts.GetLineHeight(UiFontSize.Tiny);
        int w = (int)MathF.Ceiling(maxW) + padX * 2;
        int h = padY * 2 + line1H + line2H + (line3 is not null ? line2H + 2 : 0) + 2;

        int tx = mousePosition.X + 16;
        int ty = mousePosition.Y + 14;
        if (tx + w > viewport.Width - 8) tx = mousePosition.X - w - 10;
        if (ty + h > viewport.Height - 8) ty = viewport.Height - h - 8;
        tx = Math.Max(8, tx);
        ty = Math.Max(8, ty);

        var rect = new Rectangle(tx, ty, w, h);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelDarkGold, rect, alpha: 0.97f, cornerSize: 9);

        int cy = ty + padY;
        _uiFonts.DrawString(spriteBatch, line1, new Vector2(tx + padX, cy), GtGoldBorder, UiFontSize.Small);
        cy += line1H;
        _uiFonts.DrawString(spriteBatch, line2, new Vector2(tx + padX, cy), Color.White, UiFontSize.Tiny);
        if (line3 is not null)
        {
            cy += line2H + 1;
            _uiFonts.DrawString(spriteBatch, line3, new Vector2(tx + padX, cy), WorldStatusCyan, UiFontSize.Tiny);
        }
    }

    private Rectangle DrawToggleDarkButton(SpriteBatch sb, Rectangle rect, UiButtonState state)
    {
        Rectangle renderRect = state == UiButtonState.Pressed
            ? new Rectangle(rect.X, rect.Y + 1, rect.Width, rect.Height)
            : rect;
        _nineSlice.DrawPanel(sb, state == UiButtonState.Hover ? UiPanelStyle.PanelBlue : UiPanelStyle.PanelDark, renderRect);
        return renderRect;
    }

    private void DrawCloseButton(SpriteBatch spriteBatch, Rectangle rect, Point mousePosition, bool isLeftMouseDown, float alpha)
    {
        UiButtonState state = UiNineSlice.EvaluateButtonState(rect, mousePosition, isLeftMouseDown);
        Rectangle rendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnRed, rect, state, alpha: alpha, cornerSize: 8);
        _uiFonts.DrawCentered(
            spriteBatch,
            "X",
            new Rectangle(rendered.X, rendered.Y, rendered.Width, rendered.Height - 2),
            UiFontManager.PremultiplyAlpha(Color.White, alpha),
            UiFontSize.Small);
    }

    private Rectangle ComputeActiveModalBounds(Viewport viewport)
    {
        if (IsTradeModalOpen) return ComputeTradeModalBounds(viewport);
        if (IsWarpModalOpen) return ComputeWarpModalBounds(viewport);
        if (IsLockModalOpen) return ComputeLockModalBounds(viewport);
        if (IsMenuModalOpen) return ComputeMenuModalBounds(viewport);
        return Rectangle.Empty;
    }

    private static Rectangle ComputeWarpModalBounds(Viewport viewport)
    {
        const int w = 456;
        const int h = 258;
        return new Rectangle((viewport.Width - w) / 2, (viewport.Height - h) / 2, w, h);
    }

    private static Rectangle ComputeLockModalBounds(Viewport viewport)
    {
        const int w = 516;
        const int h = 434;
        return new Rectangle((viewport.Width - w) / 2, (viewport.Height - h) / 2, w, h);
    }

    private static Rectangle ComputeMenuModalBounds(Viewport viewport)
    {
        const int w = 472;
        const int h = 390;
        return new Rectangle((viewport.Width - w) / 2, (viewport.Height - h) / 2, w, h);
    }

    private Rectangle ComputeTradeModalBounds(Viewport viewport)
    {
        const int w = 656;
        const int h = 426;
        int x = (viewport.Width - w) / 2;
        int maxBottom = _drawerHandleRect != Rectangle.Empty ? _drawerHandleRect.Y - 12 : viewport.Height - 108;
        int idealY = (viewport.Height - h) / 2 - 36;
        int y = Math.Clamp(idealY, 16, Math.Max(16, maxBottom - h));
        return new Rectangle(x, y, w, h);
    }

    private int GetCurrentModalKey()
    {
        if (IsTradeModalOpen) return 4;
        if (IsWarpModalOpen) return 1;
        if (IsLockModalOpen) return 2;
        if (IsMenuModalOpen) return 3;
        return 0;
    }

    private static Matrix CreateCenteredScaleMatrix(Rectangle bounds, float scale)
    {
        float cx = bounds.X + bounds.Width * 0.5f;
        float cy = bounds.Y + bounds.Height * 0.5f;
        return Matrix.CreateTranslation(-cx, -cy, 0f)
             * Matrix.CreateScale(scale, scale, 1f)
             * Matrix.CreateTranslation(cx, cy, 0f);
    }

    private static float CubicEaseOut(float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        float inv = 1f - t;
        return 1f - inv * inv * inv;
    }

    private static string GetItemCategoryLabel(ushort itemId) =>
        itemId == TileIds.WorldLock
            ? "World Lock"
            : ItemCatalog.IsSeed(itemId)
                ? "Seed"
                : ItemCatalog.IsBackgroundItem(itemId)
                    ? "Wall"
                    : "Block";

    private void DrawPixelCheckmark(SpriteBatch sb, int x, int y, Color color)
    {
        var shadow = Color.FromNonPremultiplied(0, 0, 0, (int)(160 * (color.A / 255f)));
        Point[] pts =
        [
            new(0, 3), new(1, 4), new(2, 5), new(3, 4), new(4, 3), new(5, 2), new(6, 1), new(7, 0),
            new(0, 4), new(1, 5), new(2, 6), new(3, 5), new(4, 4), new(5, 3), new(6, 2), new(7, 1),
        ];
        for (int i = 0; i < pts.Length; i++)
        {
            sb.Draw(_atlas.Pixel, new Rectangle(x + pts[i].X + 1, y + pts[i].Y + 1, 1, 1), shadow);
        }

        for (int i = 0; i < pts.Length; i++)
        {
            sb.Draw(_atlas.Pixel, new Rectangle(x + pts[i].X, y + pts[i].Y, 1, 1), color);
        }
    }

    private void DrawPixelTradeArrows(SpriteBatch sb, int x, int y, Color color)
    {
        var shadow = Color.FromNonPremultiplied(0, 0, 0, (int)(160 * (color.A / 255f)));

        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, y + 3, 12, 2), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 9, y + 1, 2, 6), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x, y + 2, 12, 2), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 9, y, 2, 6), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 11, y + 1, 2, 4), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 13, y + 2, 1, 2), color);

        int ly = y + 7;
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, ly + 3, 12, 2), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 5, ly + 1, 2, 6), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 2, ly + 2, 12, 2), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, ly, 2, 6), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, ly + 1, 2, 4), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x, ly + 2, 1, 2), color);
    }

    private void DrawPixelArrowUp(SpriteBatch sb, int x, int y, Color color)
    {
        var shadow = Color.FromNonPremultiplied(0, 0, 0, 150);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 4, y + 1, 1, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, y + 2, 3, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 2, y + 3, 5, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, y + 4, 7, 1), shadow);

        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, y, 1, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 2, y + 1, 3, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, y + 2, 5, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x, y + 3, 7, 1), color);
    }

    private void DrawPixelArrowDown(SpriteBatch sb, int x, int y, Color color)
    {
        var shadow = Color.FromNonPremultiplied(0, 0, 0, 150);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, y + 1, 7, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 2, y + 2, 5, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, y + 3, 3, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 4, y + 4, 1, 1), shadow);

        sb.Draw(_atlas.Pixel, new Rectangle(x, y, 7, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, y + 1, 5, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 2, y + 2, 3, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, y + 3, 1, 1), color);
    }

    private readonly record struct QueuedPixelIcon(Texture2D Texture, Rectangle Dest, Color Tint);
    private readonly record struct QueuedCountPill(Rectangle Rect, string Text, Color TextColor, float Alpha);
    private readonly record struct HoveredInventorySlot(Rectangle SlotRect, string Name, string Subtitle, int Count, bool IsTool);
    private readonly record struct TradeTooltipRequest(TradeOfferSlot Slot, bool IsLocalSlot);
}
