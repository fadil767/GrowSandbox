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
/// Antarmuka HUD bergaya Growtopia yang tajam, bersih, dan responsif:
/// - Panel world kiri atas dengan latar gelap semi-transparan GtPanelDark + bevel 1px halus & padding merata
/// - Konsol chat dengan word-wrap berbasis lebar, pewarnaan tipe pesan, fade-out halus, & scroll
/// - Inventory drawer bawah yang dapat dilipat/dibuka dengan animasi Lerp halus, panah ▲/▼, dan slot bebas tabrakan
/// - Debug overlay F3 semi-transparan di kanan atas (tersembunyi secara default)
/// </summary>
public sealed class HudRenderer
{
    public const int SlotsPerRow = 10;
    private const int SlotSize = 54;
    private const int SlotGap = 5;
    private const int MaxVisibleChatLines = 7;

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

    internal static readonly Color ChatSystemColor = new(255, 226, 120);
    internal static readonly Color ChatPlayerNameColor = new(132, 232, 255);
    internal static readonly Color ChatErrorColor = new(255, 144, 156);
    internal static readonly Color WorldStatusCyan = new(164, 232, 255);

    private static readonly string[] QuickWorlds = ["START", "FARM", "TRADE", "BUILD"];

    private readonly PixelAtlas _atlas;
    private readonly PixelFont _font;

    private Rectangle _chatToggleRect;
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

    private ushort _lastPlaceableItemId = TileIds.Dirt;
    private float _drawerExpandProgress;
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

    public HudRenderer(PixelAtlas atlas, PixelFont font)
    {
        _atlas = atlas;
        _font = font;
    }

    public void CloseAllModals()
    {
        IsWarpModalOpen = false;
        IsMenuModalOpen = false;
        IsLockModalOpen = false;
        IsTradeModalOpen = false;
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
            WorldBannerSubtitle = "OPEN WORLD  -  PLACE A WORLD LOCK TO CLAIM!";
        }
        else if (lockData.IsPublic)
        {
            WorldBannerSubtitle = $"PUBLIC LOCK BY {lockData.OwnerName.ToUpperInvariant()}  -  ANYONE CAN BUILD";
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

        // Animasi slide-up / slide-down inventory drawer yang halus (~14f * dt)
        float drawerTarget = IsInventoryExpanded ? 1f : 0f;
        _drawerExpandProgress = MathHelper.Lerp(_drawerExpandProgress, drawerTarget, Math.Clamp(14f * dt, 0f, 1f));
        if (MathF.Abs(_drawerExpandProgress - drawerTarget) < 0.002f)
        {
            _drawerExpandProgress = drawerTarget;
        }

        // Animasi tampil/sembunyi kotak chat yang mulus
        float chatTarget = IsChatVisible ? 1f : 0f;
        _chatVisibilityAlpha = MathHelper.Lerp(_chatVisibilityAlpha, chatTarget, Math.Clamp(14f * dt, 0f, 1f));
        if (MathF.Abs(_chatVisibilityAlpha - chatTarget) < 0.005f)
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
            || _chatToggleRect.Contains(mousePosition)
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
        WrenchPlayerPopupView? wrenchPlayerPopup = null)
    {
        DrawTopLeftWorldAndChat(spriteBatch, client, isChatTyping, chatDraft, elapsedTime);
        DrawTopRightCluster(spriteBatch, viewport, client);

        if (IsDebugOverlayVisible)
        {
            DrawDebugOverlay(spriteBatch, viewport, in debugMetrics);
        }

        DrawWorldEntryBanner(spriteBatch, viewport);

        if (!IsTradeModalOpen)
        {
            DrawIncomingTradeInviteBanner(spriteBatch, viewport, client.PendingTradeInvite, mousePosition);
            DrawWrenchPlayerPopup(spriteBatch, viewport, wrenchPlayerPopup, mousePosition);
            DrawCollapsibleInventoryDrawer(spriteBatch, viewport, client.Inventory);
            DrawTopCenterToast(spriteBatch, viewport);

            if (IsWarpModalOpen)
            {
                DrawWarpModal(spriteBatch, viewport, client, warpDraft, elapsedTime);
            }
            else if (IsLockModalOpen)
            {
                DrawWorldLockModal(spriteBatch, viewport, client, lockAccessDraft, elapsedTime);
            }
            else if (IsMenuModalOpen)
            {
                DrawMenuModal(spriteBatch, viewport, client, isSfxMuted);
            }
        }
        else
        {
            _inviteBannerRect = Rectangle.Empty;
            _inviteAcceptBtnRect = Rectangle.Empty;
            _inviteDeclineBtnRect = Rectangle.Empty;
            _playerPopupRect = Rectangle.Empty;
            _playerPopupTradeBtnRect = Rectangle.Empty;
            _playerPopupGiveAccessBtnRect = Rectangle.Empty;

            // Dim background, lalu tampilkan Inventory Drawer (aktif agar bisa klik item ke slot trade) & Modal Trade di atasnya.
            spriteBatch.Draw(_atlas.Pixel, new Rectangle(0, 0, viewport.Width, viewport.Height), new Color(8, 18, 30, 148));
            DrawCollapsibleInventoryDrawer(spriteBatch, viewport, client.Inventory);
            DrawTradeModal(spriteBatch, viewport, client, mousePosition, elapsedTime);
            DrawTopCenterToast(spriteBatch, viewport);
        }
    }

    private void DrawTopLeftWorldAndChat(
        SpriteBatch spriteBatch,
        GameClient client,
        bool isChatTyping,
        string chatDraft,
        float elapsedTime)
    {
        const int margin = 12;
        const int padX = 10;
        const int padY = 8;

        // 1. Tombol toggle chat kiri atas
        const int btnSize = 42;
        _chatToggleRect = new Rectangle(margin, margin, btnSize, btnSize);
        DrawGtPanel(spriteBatch, _chatToggleRect, IsChatVisible ? GtPanelSolid : GtPanelDarkTranslucent, GtBorderSubtle, topHighlightAlpha: 52);
        spriteBatch.Draw(_atlas.ChatIcon, new Rectangle(_chatToggleRect.X + 11, _chatToggleRect.Y + 11, 20, 20), Color.White);

        // 2. Panel World kiri atas: GtPanelDark semi-transparan + highlight bevel 1px tipis + padding >= 8px
        string worldTitle = (client.HasWorldSnapshot ? client.CurrentWorldName : "CONNECTING").ToUpperInvariant();
        int totalPlayers = client.RemotePlayers.Count + (client.HasWorldSnapshot ? 1 : 0);
        string statusText = client.IsWorldLocked
            ? (client.IsWorldPublicLock
                ? $"PUBLIC LOCK | {totalPlayers} ONLINE"
                : $"LOCKED BY {client.WorldLockOwner.ToUpperInvariant()} | {totalPlayers} ONLINE")
            : $"OPEN | {totalPlayers} ONLINE";
        Color statusColor = client.IsWorldLocked
            ? (client.IsWorldPublicLock ? new Color(156, 248, 178) : ChatSystemColor)
            : WorldStatusCyan;

        Vector2 titleSize = _font.MeasureString(worldTitle, scale: 2);
        Vector2 statusSize = _font.MeasureString(statusText, scale: 1);
        int lockIconAdvance = client.IsWorldLocked ? 16 : 0;

        int contentW = (int)MathF.Ceiling(MathF.Max(titleSize.X + 2, statusSize.X + lockIconAdvance));
        int panelW = Math.Max(188, contentW + padX * 2);
        int panelH = padY + (int)titleSize.Y + 5 + (int)statusSize.Y + padY;
        var worldPanel = new Rectangle(_chatToggleRect.Right + 8, margin, panelW, panelH);

        DrawGtPanel(spriteBatch, worldPanel, GtPanelDarkTranslucent, client.IsWorldLocked ? GtGoldBorder : GtBorderSubtle, topHighlightAlpha: 48);

        int textX = worldPanel.X + padX;
        int titleY = worldPanel.Y + padY;
        _font.DrawString(spriteBatch, worldTitle, new Vector2(textX, titleY), Color.White, scale: 2, dropShadow: true, bold: true);

        int statusY = titleY + (int)titleSize.Y + 5;
        if (client.IsWorldLocked)
        {
            spriteBatch.Draw(_atlas.GetItemIcon(TileIds.WorldLock), new Rectangle(textX, statusY - 3, 12, 12), Color.White);
            _font.DrawString(spriteBatch, statusText, new Vector2(textX + lockIconAdvance, statusY), statusColor, scale: 1, dropShadow: true);
        }
        else
        {
            _font.DrawString(spriteBatch, statusText, new Vector2(textX, statusY), statusColor, scale: 1, dropShadow: true);
        }

        // 3. Kotak Log Chat (word-wrap berbasis lebar, pewarnaan per tipe pesan, fade-out, scroll)
        int consoleX = margin;
        int consoleY = Math.Max(_chatToggleRect.Bottom, worldPanel.Bottom) + 8;
        const int consoleW = 408;
        const int chatPad = 9;
        float wrapWidth = consoleW - chatPad * 2;

        float effectiveVisibility = isChatTyping ? 1f : _chatVisibilityAlpha;
        if (effectiveVisibility <= 0.02f)
        {
            _chatLogRect = Rectangle.Empty;
            _chatInputRect = Rectangle.Empty;
            return;
        }

        // Bangun baris hasil word-wrap untuk seluruh chat yang relevan
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
                string full = $">> {entry.Message}";
                List<string> lines = _font.WrapText(full, wrapWidth, scale: 1);
                for (int l = 0; l < lines.Count; l++)
                {
                    wrappedRows.Add((null, Color.Transparent, lines[l], ChatSystemColor, fade));
                }
            }
            else if (entry.Kind == ChatMessageKind.Error)
            {
                string full = $"! {entry.Message}";
                List<string> lines = _font.WrapText(full, wrapWidth, scale: 1);
                for (int l = 0; l < lines.Count; l++)
                {
                    wrappedRows.Add((null, Color.Transparent, lines[l], ChatErrorColor, fade));
                }
            }
            else
            {
                string prefix = $"{entry.SenderName}: ";
                string combined = prefix + entry.Message;
                List<string> lines = _font.WrapText(combined, wrapWidth, scale: 1);
                for (int l = 0; l < lines.Count; l++)
                {
                    if (l == 0 && lines[0].StartsWith(prefix, StringComparison.Ordinal))
                    {
                        string remainder = lines[0][prefix.Length..];
                        wrappedRows.Add((prefix, ChatPlayerNameColor, remainder, Color.White, fade));
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

        const int rowStep = 13;
        int logBottomY = consoleY;

        if (shownCount > 0 || isChatTyping)
        {
            float maxRowFade = isChatTyping ? 1f : 0f;
            for (int r = startIndex; r < endIndex; r++)
            {
                maxRowFade = MathF.Max(maxRowFade, wrappedRows[r].FadeAlpha);
            }

            float panelAlpha = effectiveVisibility * maxRowFade;
            int visibleRows = Math.Max(1, shownCount);
            int logH = visibleRows * rowStep + chatPad * 2 - 4;
            _chatLogRect = new Rectangle(consoleX, consoleY, consoleW, logH);
            logBottomY = _chatLogRect.Bottom;

            byte bgAlpha = (byte)((isChatTyping ? GtPanelFill.A : 132) * panelAlpha);
            byte borderAlpha = (byte)((isChatTyping ? 195 : 120) * panelAlpha);
            DrawGtPanel(
                spriteBatch,
                _chatLogRect,
                new Color(GtPanelFill.R, GtPanelFill.G, GtPanelFill.B, bgAlpha),
                new Color(GtBorderBright.R, GtBorderBright.G, GtBorderBright.B, borderAlpha),
                topHighlightAlpha: (byte)(36 * panelAlpha));

            if (shownCount == 0)
            {
                var hintColor = new Color((byte)204, (byte)236, (byte)255, (byte)(210 * panelAlpha));
                _font.DrawString(spriteBatch, "Type a message or /join <WORLD>...", new Vector2(consoleX + chatPad, consoleY + chatPad), hintColor, scale: 1);
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
                        var pCol = WithAlpha(row.PrefixColor, alpha);
                        var bCol = WithAlpha(row.BodyColor, alpha);
                        _font.DrawString(spriteBatch, row.Prefix, new Vector2(consoleX + chatPad, drawY), pCol, scale: 1);
                        int prefixWidth = _font.Advance(row.Prefix, scale: 1);
                        _font.DrawString(spriteBatch, row.Body, new Vector2(consoleX + chatPad + prefixWidth, drawY), bCol, scale: 1);
                    }
                    else
                    {
                        _font.DrawString(spriteBatch, row.Body, new Vector2(consoleX + chatPad, drawY), WithAlpha(row.BodyColor, alpha), scale: 1);
                    }
                }

                // Indikator scroll kecil bila ada riwayat di atas
                if (_maxChatScrollOffset > 0 && isChatTyping)
                {
                    int trackH = Math.Max(12, logH - 12);
                    int thumbH = Math.Max(10, trackH * MaxVisibleChatLines / wrappedRows.Count);
                    float ratio = 1f - ((float)_chatScrollOffset / _maxChatScrollOffset);
                    int thumbY = _chatLogRect.Y + 6 + (int)((trackH - thumbH) * ratio);
                    spriteBatch.Draw(_atlas.Pixel, new Rectangle(_chatLogRect.Right - 5, thumbY, 2, thumbH), new Color(164, 232, 255, 190));
                }
            }
        }
        else
        {
            _chatLogRect = Rectangle.Empty;
        }

        // Kotak input aktif HANYA muncul saat sedang mengetik (ENTER / T) agar tidak menghalangi pandangan
        if (isChatTyping)
        {
            int inputY = (logBottomY > consoleY ? logBottomY + 5 : consoleY);
            _chatInputRect = new Rectangle(consoleX, inputY, consoleW, 28);
            DrawGtPanel(spriteBatch, _chatInputRect, new Color(GtPanelDark.R, GtPanelDark.G, GtPanelDark.B, (byte)238), GtGoldBorder, topHighlightAlpha: 55);

            bool showCursor = ((int)(elapsedTime * 2.4f)) % 2 == 0;
            int maxDraftChars = Math.Max(10, (int)((consoleW - 28) / (float)PixelFont.CharAdvance));
            string shownDraft = chatDraft.Length > maxDraftChars ? chatDraft[^maxDraftChars..] : chatDraft;
            string line = $"> {shownDraft}{(showCursor ? "|" : "")}";
            _font.DrawString(spriteBatch, line, new Vector2(_chatInputRect.X + 9, _chatInputRect.Y + 10), Color.White, scale: 1);
        }
        else
        {
            _chatInputRect = Rectangle.Empty;
        }
    }

    private void DrawTopRightCluster(SpriteBatch spriteBatch, Viewport viewport, GameClient client)
    {
        int right = viewport.Width - 12;
        const int y = 12;

        // Tombol Menu (kanan ujung)
        _menuButtonRect = new Rectangle(right - 38, y, 38, 38);
        DrawGtPanel(spriteBatch, _menuButtonRect, GtPanelSolid, GtBorderBright, topHighlightAlpha: 64);
        spriteBatch.Draw(_atlas.MenuIcon, new Rectangle(_menuButtonRect.X + 9, _menuButtonRect.Y + 9, 20, 20), Color.White);

        // Tombol World Select / Warp (ikon Globe)
        _warpButtonRect = new Rectangle(_menuButtonRect.X - 44, y, 38, 38);
        DrawGtPanel(spriteBatch, _warpButtonRect, GtPanelSolid, GtBorderBright, topHighlightAlpha: 64);
        spriteBatch.Draw(_atlas.GlobeIcon, new Rectangle(_warpButtonRect.X + 7, _warpButtonRect.Y + 7, 24, 24), Color.White);

        // Penghitung Gems
        string gemsText = client.GemCount.ToString("N0");
        Vector2 gemTextSize = _font.MeasureString(gemsText, scale: 2);
        int gemPillW = Math.Max(114, (int)gemTextSize.X + 52);
        var gemPill = new Rectangle(_warpButtonRect.X - gemPillW - 8, y + 1, gemPillW, 36);

        DrawGtPanel(spriteBatch, gemPill, GtPanelDarkTranslucent, GtBorderSubtle, topHighlightAlpha: 48);
        spriteBatch.Draw(_atlas.GemIcon, new Rectangle(gemPill.X + 8, gemPill.Y + 6, 24, 24), Color.White);
        _font.DrawString(
            spriteBatch,
            gemsText,
            new Vector2(gemPill.Right - gemTextSize.X - 10, gemPill.Y + 11),
            new Color(246, 255, 250),
            scale: 2,
            dropShadow: true,
            bold: true);
    }

    /// <summary>
    /// Debug Overlay semi-transparan di kanan atas (di bawah tombol Gem/Menu),
    /// tersembunyi secara default dan aktif saat tombol F3 ditekan.
    /// </summary>
    private void DrawDebugOverlay(SpriteBatch spriteBatch, Viewport viewport, in DebugMetrics m)
    {
        string[] lines =
        [
            $"FPS      : {m.Fps}",
            $"WORLD    : {(string.IsNullOrEmpty(m.WorldName) ? "-" : m.WorldName)}",
            $"PLAYERS  : {m.PlayerCount}",
            $"TILES    : {m.VisibleTiles}/{m.TotalTiles}",
            $"PING     : {m.PingMs} ms",
            $"TILE POS : ({m.PlayerTileX}, {m.PlayerTileY})",
        ];

        const int pad = 9;
        const int lineH = 12;
        float maxW = 0f;
        for (int i = 0; i < lines.Length; i++)
        {
            maxW = MathF.Max(maxW, _font.MeasureString(lines[i], scale: 1).X);
        }

        int panelW = (int)MathF.Ceiling(maxW) + pad * 2 + 4;
        int panelH = lines.Length * lineH + pad * 2 - 2;
        var rect = new Rectangle(viewport.Width - panelW - 12, 58, panelW, panelH);

        DrawGtPanel(
            spriteBatch,
            rect,
            new Color(10, 26, 44, 185),
            new Color(126, 218, 255, 165),
            topHighlightAlpha: 34);

        for (int i = 0; i < lines.Length; i++)
        {
            Color c = i == 0 ? new Color(138, 246, 152) : new Color(216, 242, 255);
            _font.DrawString(spriteBatch, lines[i], new Vector2(rect.X + pad, rect.Y + pad + i * lineH), c, scale: 1);
        }
    }

    private void DrawWorldEntryBanner(SpriteBatch spriteBatch, Viewport viewport)
    {
        if (WorldBannerTimer <= 0f || string.IsNullOrEmpty(WorldBannerTitle))
        {
            return;
        }

        float alpha = WorldBannerTimer < 0.8f ? Math.Clamp(WorldBannerTimer / 0.8f, 0f, 1f) : 1f;
        byte a = (byte)(alpha * 255);

        string title = $"WORLD {WorldBannerTitle}";
        Vector2 titleSize = _font.MeasureString(title, scale: 3);
        Vector2 subSize = _font.MeasureString(WorldBannerSubtitle, scale: 1);

        int bx = (int)MathF.Floor((viewport.Width - titleSize.X) * 0.5f);
        int by = 74;

        _font.DrawStringThinOutline(
            spriteBatch,
            title,
            new Vector2(bx, by),
            new Color((byte)255, (byte)242, (byte)128, a),
            scale: 3,
            outlineColor: new Color((byte)14, (byte)34, (byte)56, a));

        int sx = (int)MathF.Floor((viewport.Width - subSize.X) * 0.5f);
        _font.DrawString(
            spriteBatch,
            WorldBannerSubtitle,
            new Vector2(sx, by + 28),
            new Color((byte)240, (byte)250, (byte)255, a),
            scale: 1,
            dropShadow: true);
    }

    /// <summary>
    /// Collapsible Inventory Drawer dengan animasi slide-up/slide-down halus (MathHelper.Lerp),
    /// mode ringkas default 1 baris (10 slot: Fist, Wrench, + 8 item cepat), mode terbuka 2+ baris,
    /// ikon panah ▲/▼ pada tab handle, dan tata letak slot 54x54 bebas tabrakan.
    /// </summary>
    private void DrawCollapsibleInventoryDrawer(
        SpriteBatch spriteBatch,
        Viewport viewport,
        IReadOnlyList<InventorySlot> inventory)
    {
        _slotHitboxes.Clear();

        int expandedRows = Math.Clamp((int)MathF.Ceiling((inventory.Count + 2) / (float)SlotsPerRow), 2, 3);
        int compactGridH = SlotSize;
        int expandedGridH = expandedRows * SlotSize + (expandedRows - 1) * SlotGap;

        int gridW = SlotsPerRow * SlotSize + (SlotsPerRow - 1) * SlotGap;
        int drawerW = gridW + 20;
        int compactDrawerH = compactGridH + 18;
        int expandedDrawerH = expandedGridH + 18;

        int currentDrawerH = (int)MathF.Round(MathHelper.Lerp(compactDrawerH, expandedDrawerH, _drawerExpandProgress));
        int drawerX = (viewport.Width - drawerW) / 2;
        int drawerY = viewport.Height - currentDrawerH - 6;

        _drawerBoundsRect = new Rectangle(drawerX, drawerY, drawerW, currentDrawerH);

        // Tab Handle di atas drawer + ikon panah ▲ (terlipat) / ▼ (terbuka)
        ActiveToolKind activeTool = GetActiveTool(inventory, out ushort activeItemId);
        string activeLabel = IsTradeModalOpen
            ? "CLICK ITEM TO OFFER (+1 / SHIFT:+10)"
            : activeTool switch
            {
                ActiveToolKind.Fist => "FIST (PUNCH)",
                ActiveToolKind.Wrench => "WRENCH (INSPECT / TRADE)",
                _ => ItemCatalog.IsBackgroundItem(activeItemId)
                    ? $"{ItemCatalog.GetName(activeItemId).ToUpperInvariant()} [WALL]"
                    : ItemCatalog.GetName(activeItemId).ToUpperInvariant(),
            };

        Vector2 labelSize = _font.MeasureString(activeLabel, scale: 1);
        const int arrowMargin = 16;
        int handleW = Math.Max(184, (int)labelSize.X + arrowMargin * 2 + 24);
        const int handleH = 24;
        _drawerHandleRect = new Rectangle((viewport.Width - handleW) / 2, drawerY - handleH + 2, handleW, handleH);

        DrawGtPanel(spriteBatch, _drawerHandleRect, GtPanelSolid, GtBorderBright, topHighlightAlpha: 56);

        int labelX = (int)MathF.Floor(_drawerHandleRect.X + (_drawerHandleRect.Width - labelSize.X) * 0.5f);
        int labelY = _drawerHandleRect.Y + 8;
        _font.DrawString(spriteBatch, activeLabel, new Vector2(labelX, labelY), new Color(255, 240, 140), scale: 1, dropShadow: true);

        // Gambar ikon panah pixel-art kiri & kanan (▲ saat terlipat, ▼ saat terbuka)
        int leftArrowX = _drawerHandleRect.X + 10;
        int rightArrowX = _drawerHandleRect.Right - 17;
        int arrowY = _drawerHandleRect.Y + 10;
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

        // Tombol DROP (Q) di kanan atas laci inventory (dinonaktifkan selama sesi Trade aktif)
        bool canDropSelected = !IsTradeModalOpen && activeTool == ActiveToolKind.Item && ItemCatalog.IsDroppable(activeItemId);
        const int dropBtnW = 74;
        _dropButtonRect = new Rectangle(_drawerBoundsRect.Right - dropBtnW - 6, drawerY - handleH + 2, dropBtnW, handleH);
        Color dropFill = canDropSelected ? new Color(188, 122, 34) : GtPanelDarkTranslucent;
        Color dropBorder = canDropSelected ? GtGoldBorder : new Color(96, 152, 188, 175);
        DrawGtPanel(spriteBatch, _dropButtonRect, dropFill, dropBorder, topHighlightAlpha: canDropSelected ? (byte)64 : (byte)28);

        const string dropLabel = "DROP [Q]";
        Vector2 dropLabelSize = _font.MeasureString(dropLabel, scale: 1);
        Color dropTextColor = canDropSelected ? Color.White : new Color(150, 184, 206);
        _font.DrawString(
            spriteBatch,
            dropLabel,
            new Vector2(_dropButtonRect.X + (_dropButtonRect.Width - dropLabelSize.X) * 0.5f, _dropButtonRect.Y + 8),
            dropTextColor,
            scale: 1,
            dropShadow: true);

        // Badan laci inventory
        DrawGtPanel(spriteBatch, _drawerBoundsRect, GtPanelSolid, GtBorderBright, topHighlightAlpha: 64);

        // Jumlah baris yang digambar selama transisi slide
        int rowsToRender = _drawerExpandProgress > 0.03f ? expandedRows : 1;
        int totalSlots = rowsToRender * SlotsPerRow;
        int bottomClipY = _drawerBoundsRect.Bottom - 6;

        for (int i = 0; i < totalSlots; i++)
        {
            int col = i % SlotsPerRow;
            int row = i / SlotsPerRow;
            int sx = drawerX + 10 + col * (SlotSize + SlotGap);
            int sy = drawerY + 9 + row * (SlotSize + SlotGap);

            // Hanya tampilkan baris tambahan yang sudah masuk ke dalam area drawer selama animasi slide
            if (sy + SlotSize > bottomClipY + 4)
            {
                continue;
            }

            var rect = new Rectangle(sx, sy, SlotSize, SlotSize);
            _slotHitboxes.Add((rect, i));

            bool selected = i == SelectedSlotIndex;
            Color fill = selected ? GtSlotSelectedFill : GtSlotFill;
            Color border = selected ? GtGoldBorder : new Color(96, 174, 218);

            UiDraw.Panel(spriteBatch, _atlas.Pixel, rect, fill, border, thickness: selected ? 2 : 1, radius: 5);
            spriteBatch.Draw(
                _atlas.Pixel,
                new Rectangle(rect.X + 3, rect.Y + 2, rect.Width - 6, 1),
                new Color(255, 255, 255, selected ? 85 : 42));

            // Tata letak non-overlapping di dalam slot 54x54:
            // - Shortcut (1..9, 0) di pojok kiri atas (y: 4..11)
            // - Ikon item 24x24 di tengah (x: 15..39, y: 14..38)
            // - Jumlah item di pojok kanan bawah (y: 43..50)
            if (row == 0)
            {
                string keyLabel = col == 9 ? "0" : (col + 1).ToString();
                _font.DrawString(spriteBatch, keyLabel, new Vector2(sx + 5, sy + 4), new Color(188, 228, 255), scale: 1, dropShadow: true);
            }

            var iconRect = new Rectangle(sx + 15, sy + 14, 24, 24);
            if (i == 0)
            {
                spriteBatch.Draw(_atlas.FistIcon, iconRect, Color.White);
            }
            else if (i == 1)
            {
                spriteBatch.Draw(_atlas.WrenchIcon, iconRect, Color.White);
            }
            else
            {
                int invIdx = i - 2;
                if (invIdx < inventory.Count)
                {
                    InventorySlot slot = inventory[invIdx];
                    Texture2D icon = _atlas.GetItemIcon(slot.ItemId);
                    spriteBatch.Draw(icon, iconRect, Color.White);

                    string countText = slot.Count.ToString();
                    Vector2 countSize = _font.MeasureString(countText, scale: 1);
                    int countX = (int)MathF.Floor(sx + SlotSize - countSize.X - 5);
                    int countY = sy + SlotSize - 11;
                    _font.DrawString(spriteBatch, countText, new Vector2(countX, countY), Color.White, scale: 1, dropShadow: true);
                }
            }
        }
    }

    private void DrawWarpModal(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        string warpDraft,
        float elapsedTime)
    {
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(0, 0, viewport.Width, viewport.Height), new Color(8, 18, 30, 150));

        const int w = 436;
        const int h = 256;
        int x = (viewport.Width - w) / 2;
        int y = (viewport.Height - h) / 2;
        _modalPanelRect = new Rectangle(x, y, w, h);

        DrawGtPanel(spriteBatch, _modalPanelRect, GtPanelSolid, GtBorderBright, topHighlightAlpha: 65, borderThickness: 2);
        _font.DrawString(spriteBatch, "ENTER WORLD", new Vector2(x + 20, y + 18), new Color(255, 238, 118), scale: 2, dropShadow: true, bold: true);

        _modalCloseBtnRect = new Rectangle(x + w - 38, y + 14, 26, 26);
        DrawGtPanel(spriteBatch, _modalCloseBtnRect, GtRedBtn, GtRedBorder, topHighlightAlpha: 55);
        _font.DrawString(spriteBatch, "X", new Vector2(_modalCloseBtnRect.X + 10, _modalCloseBtnRect.Y + 9), Color.White, scale: 1, dropShadow: true);

        _font.DrawString(spriteBatch, "Type any world name (new worlds are created automatically):", new Vector2(x + 20, y + 52), new Color(220, 242, 255), scale: 1);

        var inputBox = new Rectangle(x + 20, y + 74, w - 140, 34);
        DrawGtPanel(spriteBatch, inputBox, GtPanelDark, GtGoldBorder, topHighlightAlpha: 40);
        bool cursor = ((int)(elapsedTime * 2.4f)) % 2 == 0;
        string shown = warpDraft.ToUpperInvariant();
        _font.DrawString(spriteBatch, $"{shown}{(cursor ? "|" : "")}", new Vector2(inputBox.X + 10, inputBox.Y + 10), Color.White, scale: 2, dropShadow: true);

        _modalPrimaryBtnRect = new Rectangle(inputBox.Right + 10, y + 74, 90, 34);
        DrawGtPanel(spriteBatch, _modalPrimaryBtnRect, GtGreenBtn, GtGreenBorder, topHighlightAlpha: 60);
        _font.DrawString(spriteBatch, "GO!", new Vector2(_modalPrimaryBtnRect.X + 27, _modalPrimaryBtnRect.Y + 10), Color.White, scale: 2, dropShadow: true, bold: true);

        _font.DrawString(spriteBatch, $"Current: {client.CurrentWorldName}  |  Quick Worlds:", new Vector2(x + 20, y + 126), WorldStatusCyan, scale: 1);

        _quickWorldBtnRects.Clear();
        int btnW = (w - 40 - 24) / QuickWorlds.Length;
        for (int i = 0; i < QuickWorlds.Length; i++)
        {
            var qRect = new Rectangle(x + 20 + i * (btnW + 8), y + 146, btnW, 38);
            _quickWorldBtnRects.Add((qRect, QuickWorlds[i]));
            DrawGtPanel(spriteBatch, qRect, GtSlotFill, GtBorderBright, topHighlightAlpha: 45);

            Vector2 tw = _font.MeasureString(QuickWorlds[i], scale: 1);
            _font.DrawString(
                spriteBatch,
                QuickWorlds[i],
                new Vector2(qRect.X + (qRect.Width - tw.X) * 0.5f, qRect.Y + 15),
                Color.White,
                scale: 1,
                dropShadow: true);
        }

        _font.DrawString(spriteBatch, "Press ENTER to warp  |  ESC to cancel", new Vector2(x + 20, y + h - 26), new Color(188, 226, 252), scale: 1);
    }

    private void DrawTopCenterToast(SpriteBatch spriteBatch, Viewport viewport)
    {
        if (_toastTimer <= 0f || string.IsNullOrEmpty(_toastMessage))
        {
            return;
        }

        float fadeIn = Math.Clamp((_toastDuration - _toastTimer) / 0.16f, 0f, 1f);
        float fadeOut = Math.Clamp(_toastTimer / 0.45f, 0f, 1f);
        float alpha = MathF.Min(fadeIn, fadeOut);
        if (alpha <= 0.02f)
        {
            return;
        }

        Vector2 msgSize = _font.MeasureString(_toastMessage, scale: 1);
        const int padX = 14;
        const int padY = 8;
        int w = (int)MathF.Ceiling(msgSize.X) + padX * 2;
        int h = (int)MathF.Ceiling(msgSize.Y) + padY * 2;
        int x = (viewport.Width - w) / 2;
        int y = _inviteBannerRect != Rectangle.Empty ? _inviteBannerRect.Bottom + 8 : 14;

        Color borderBase = _toastKind switch
        {
            ToastKind.Warning => ChatErrorColor,
            ToastKind.Success => GtGoldBorder,
            _ => GtBorderBright,
        };
        Color textBase = _toastKind switch
        {
            ToastKind.Warning => new Color(255, 196, 200),
            ToastKind.Success => new Color(255, 244, 154),
            _ => Color.White,
        };

        var rect = new Rectangle(x, y, w, h);
        DrawGtPanel(
            spriteBatch,
            rect,
            new Color(GtPanelDark.R, GtPanelDark.G, GtPanelDark.B, (byte)(226 * alpha)),
            WithAlpha(borderBase, alpha),
            topHighlightAlpha: (byte)(52 * alpha));

        _font.DrawString(
            spriteBatch,
            _toastMessage,
            new Vector2(x + padX, y + padY),
            WithAlpha(textBase, alpha),
            scale: 1,
            dropShadow: true);
    }

    private void DrawWorldLockModal(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        string accessDraft,
        float elapsedTime)
    {
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(0, 0, viewport.Width, viewport.Height), new Color(8, 18, 30, 155));

        const int w = 500;
        const int h = 414;
        int x = (viewport.Width - w) / 2;
        int y = (viewport.Height - h) / 2;
        _modalPanelRect = new Rectangle(x, y, w, h);

        DrawGtPanel(spriteBatch, _modalPanelRect, GtPanelSolid, GtBorderBright, topHighlightAlpha: 68, borderThickness: 2);

        // Header dengan ikon gembok emas & tombol tutup X
        spriteBatch.Draw(_atlas.GetItemIcon(TileIds.WorldLock), new Rectangle(x + 18, y + 14, 24, 24), Color.White);
        _font.DrawString(spriteBatch, "WORLD LOCK CONFIG", new Vector2(x + 50, y + 18), new Color(255, 238, 118), scale: 2, dropShadow: true, bold: true);

        _modalCloseBtnRect = new Rectangle(x + w - 38, y + 14, 26, 26);
        DrawGtPanel(spriteBatch, _modalCloseBtnRect, GtRedBtn, GtRedBorder, topHighlightAlpha: 55);
        _font.DrawString(spriteBatch, "X", new Vector2(_modalCloseBtnRect.X + 10, _modalCloseBtnRect.Y + 9), Color.White, scale: 1, dropShadow: true);

        WorldLockData lockData = client.LockData;
        string ownerLine = $"OWNER: {lockData.OwnerName}   |   TILE: ({lockData.LockX}, {lockData.LockY})   |   ACCESS: {lockData.AccessList.Count}/{WorldLockData.MaxAccessListCount}";
        _font.DrawString(spriteBatch, ownerLine, new Vector2(x + 20, y + 50), WorldStatusCyan, scale: 1, dropShadow: true);

        // 1. Toggle Allow Public Build (ON/OFF)
        _lockPublicBtnRect = new Rectangle(x + 20, y + 70, w - 40, 32);
        Color pubFill = lockData.IsPublic ? GtGreenBtn : GtPanelDark;
        Color pubBorder = lockData.IsPublic ? GtGreenBorder : GtBorderBright;
        DrawGtPanel(spriteBatch, _lockPublicBtnRect, pubFill, pubBorder, topHighlightAlpha: 56);

        string pubLabel = lockData.IsPublic
            ? "ALLOW PUBLIC BUILD: ON  (ANYONE CAN BUILD)"
            : "ALLOW PUBLIC BUILD: OFF  (OWNER & ACCESS LIST ONLY)";
        Vector2 pubSize = _font.MeasureString(pubLabel, scale: 1);
        _font.DrawString(
            spriteBatch,
            pubLabel,
            new Vector2(_lockPublicBtnRect.X + (_lockPublicBtnRect.Width - pubSize.X) * 0.5f, _lockPublicBtnRect.Y + 12),
            Color.White,
            scale: 1,
            dropShadow: true);

        // 2. Input nama pemain + tombol Add
        _font.DrawString(spriteBatch, "Grant build access to player:", new Vector2(x + 20, y + 114), new Color(224, 244, 255), scale: 1);

        var inputBox = new Rectangle(x + 20, y + 130, w - 134, 30);
        DrawGtPanel(spriteBatch, inputBox, GtPanelDark, GtGoldBorder, topHighlightAlpha: 42);
        bool cursor = ((int)(elapsedTime * 2.4f)) % 2 == 0;
        string draftShown = accessDraft.Length > 24 ? accessDraft[..24] : accessDraft;
        _font.DrawString(
            spriteBatch,
            $"{draftShown}{(cursor ? "|" : "")}",
            new Vector2(inputBox.X + 9, inputBox.Y + 11),
            Color.White,
            scale: 1,
            dropShadow: true);

        _lockAddBtnRect = new Rectangle(inputBox.Right + 10, y + 130, 84, 30);
        DrawGtPanel(spriteBatch, _lockAddBtnRect, GtGreenBtn, GtGreenBorder, topHighlightAlpha: 60);
        Vector2 addSize = _font.MeasureString("ADD", scale: 1);
        _font.DrawString(
            spriteBatch,
            "ADD",
            new Vector2(_lockAddBtnRect.X + (_lockAddBtnRect.Width - addSize.X) * 0.5f, _lockAddBtnRect.Y + 11),
            Color.White,
            scale: 1,
            dropShadow: true);

        // 3. Daftar Access List (2 kolom x maks 10 baris = 20 pemain) dengan tombol [X]
        var listBox = new Rectangle(x + 20, y + 170, w - 40, 186);
        DrawGtPanel(spriteBatch, listBox, GtPanelDarkTranslucent, GtBorderSubtle, topHighlightAlpha: 32);

        _lockRemoveBtnRects.Clear();
        if (lockData.AccessList.Count == 0)
        {
            const string emptyMsg = "No players in access list yet. Type a name above and click ADD.";
            Vector2 emptySize = _font.MeasureString(emptyMsg, scale: 1);
            _font.DrawString(
                spriteBatch,
                emptyMsg,
                new Vector2(listBox.X + (listBox.Width - emptySize.X) * 0.5f, listBox.Y + 86),
                new Color(172, 212, 236),
                scale: 1);
        }
        else
        {
            const int cols = 2;
            int colW = (listBox.Width - 20) / cols;
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
                int rx = listBox.X + 8 + col * (colW + 4);
                int ry = listBox.Y + 5 + row * rowH;

                var removeBtn = new Rectangle(rx + colW - 24, ry + 1, 20, 15);
                _lockRemoveBtnRects.Add((removeBtn, entryName));

                DrawGtPanel(spriteBatch, removeBtn, GtRedBtn, GtRedBorder, topHighlightAlpha: 40);
                _font.DrawString(spriteBatch, "X", new Vector2(removeBtn.X + 7, removeBtn.Y + 4), Color.White, scale: 1);

                string clippedName = entryName.Length > 20 ? entryName[..20] : entryName;
                _font.DrawString(spriteBatch, clippedName, new Vector2(rx + 4, ry + 4), Color.White, scale: 1, dropShadow: true);
                idx++;
            }
        }

        // 4. Footer: tombol Pick Up Lock + tombol Close
        _lockPickUpBtnRect = new Rectangle(x + 20, y + h - 46, 216, 32);
        DrawGtPanel(spriteBatch, _lockPickUpBtnRect, new Color(186, 122, 34), GtGoldBorder, topHighlightAlpha: 60);
        Vector2 pickSize = _font.MeasureString("PICK UP LOCK", scale: 1);
        _font.DrawString(
            spriteBatch,
            "PICK UP LOCK",
            new Vector2(_lockPickUpBtnRect.X + (_lockPickUpBtnRect.Width - pickSize.X) * 0.5f, _lockPickUpBtnRect.Y + 12),
            Color.White,
            scale: 1,
            dropShadow: true);

        _lockCloseFooterBtnRect = new Rectangle(x + w - 216, y + h - 46, 196, 32);
        DrawGtPanel(spriteBatch, _lockCloseFooterBtnRect, GtSlotFill, GtBorderBright, topHighlightAlpha: 55);
        Vector2 closeSize = _font.MeasureString("CLOSE [ESC]", scale: 1);
        _font.DrawString(
            spriteBatch,
            "CLOSE [ESC]",
            new Vector2(_lockCloseFooterBtnRect.X + (_lockCloseFooterBtnRect.Width - closeSize.X) * 0.5f, _lockCloseFooterBtnRect.Y + 12),
            Color.White,
            scale: 1,
            dropShadow: true);
    }

    private void DrawMenuModal(SpriteBatch spriteBatch, Viewport viewport, GameClient client, bool isSfxMuted)
    {
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(0, 0, viewport.Width, viewport.Height), new Color(8, 18, 30, 150));

        const int w = 444;
        const int h = 352;
        int x = (viewport.Width - w) / 2;
        int y = (viewport.Height - h) / 2;
        _modalPanelRect = new Rectangle(x, y, w, h);

        DrawGtPanel(spriteBatch, _modalPanelRect, GtPanelSolid, GtBorderBright, topHighlightAlpha: 65, borderThickness: 2);
        _font.DrawString(spriteBatch, "GROWSANDBOX MENU", new Vector2(x + 20, y + 18), new Color(255, 238, 118), scale: 2, dropShadow: true, bold: true);

        _modalCloseBtnRect = new Rectangle(x + w - 38, y + 14, 26, 26);
        DrawGtPanel(spriteBatch, _modalCloseBtnRect, GtRedBtn, GtRedBorder, topHighlightAlpha: 55);
        _font.DrawString(spriteBatch, "X", new Vector2(_modalCloseBtnRect.X + 10, _modalCloseBtnRect.Y + 9), Color.White, scale: 1, dropShadow: true);

        string[] lines =
        [
            $"PLAYER: {client.LocalPlayerName}   |   GEMS: {client.GemCount}",
            $"WORLD : {client.CurrentWorldName} ({(client.IsWorldLocked ? "Locked: " + client.WorldLockOwner : "Open")})",
            "",
            "CONTROLS:",
            "  A / D + SPACE : Move & Jump",
            "  1 (Fist)      : Left-Click to Punch blocks",
            "  2 (Wrench)    : Inspect tiles or click player to Trade",
            "  3-0 / E       : Select Blocks, Seeds, World Lock",
            "  Q / DROP BTN  : Drop 1 selected item to floor",
            "  I / B         : Toggle Inventory Drawer",
            "  Right-Click   : Place selected block/wall/seed",
            "  ENTER / TAB   : Chat / Warp World",
            "  M / F3        : Mute SFX / Debug Overlay",
        ];

        for (int i = 0; i < lines.Length; i++)
        {
            Color c = i < 2 ? ChatSystemColor : (i == 3 ? WorldStatusCyan : Color.White);
            _font.DrawString(spriteBatch, lines[i], new Vector2(x + 20, y + 52 + i * 15), c, scale: 1);
        }

        // Tombol Toggle Mute / Unmute SFX di dalam Menu
        _modalMuteBtnRect = new Rectangle(x + 20, y + h - 94, w - 40, 34);
        Color muteFill = isSfxMuted ? GtPanelDark : GtSlotFill;
        Color muteBorder = isSfxMuted ? GtRedBorder : GtBorderBright;
        DrawGtPanel(spriteBatch, _modalMuteBtnRect, muteFill, muteBorder, topHighlightAlpha: 52);

        string muteLabel = isSfxMuted ? "SFX AUDIO: MUTED  [PRESS M TO UNMUTE]" : "SFX AUDIO: ON  [PRESS M TO MUTE]";
        Vector2 muteTextSize = _font.MeasureString(muteLabel, scale: 1);
        Color muteTextColor = isSfxMuted ? ChatErrorColor : new Color(148, 248, 166);
        _font.DrawString(
            spriteBatch,
            muteLabel,
            new Vector2(_modalMuteBtnRect.X + (_modalMuteBtnRect.Width - muteTextSize.X) * 0.5f, _modalMuteBtnRect.Y + 13),
            muteTextColor,
            scale: 1,
            dropShadow: true);

        const int footerBtnW = 130;
        const int footerGap = 7;
        int footerY = y + h - 50;

        _modalPrimaryBtnRect = new Rectangle(x + 20, footerY, footerBtnW, 34);
        DrawGtPanel(spriteBatch, _modalPrimaryBtnRect, GtGreenBtn, GtGreenBorder, topHighlightAlpha: 60);
        Vector2 respawnSize = _font.MeasureString("RESPAWN", scale: 1);
        _font.DrawString(
            spriteBatch,
            "RESPAWN",
            new Vector2(_modalPrimaryBtnRect.X + (_modalPrimaryBtnRect.Width - respawnSize.X) * 0.5f, _modalPrimaryBtnRect.Y + 13),
            Color.White,
            scale: 1,
            dropShadow: true);

        _modalLogoutBtnRect = new Rectangle(_modalPrimaryBtnRect.Right + footerGap, footerY, footerBtnW, 34);
        DrawGtPanel(spriteBatch, _modalLogoutBtnRect, new Color(188, 122, 34), GtGoldBorder, topHighlightAlpha: 60);
        Vector2 logoutSize = _font.MeasureString("LOGOUT", scale: 1);
        _font.DrawString(
            spriteBatch,
            "LOGOUT",
            new Vector2(_modalLogoutBtnRect.X + (_modalLogoutBtnRect.Width - logoutSize.X) * 0.5f, _modalLogoutBtnRect.Y + 13),
            Color.White,
            scale: 1,
            dropShadow: true);

        _modalSecondaryBtnRect = new Rectangle(_modalLogoutBtnRect.Right + footerGap, footerY, footerBtnW, 34);
        DrawGtPanel(spriteBatch, _modalSecondaryBtnRect, GtRedBtn, GtRedBorder, topHighlightAlpha: 60);
        Vector2 exitSize = _font.MeasureString("EXIT GAME", scale: 1);
        _font.DrawString(
            spriteBatch,
            "EXIT GAME",
            new Vector2(_modalSecondaryBtnRect.X + (_modalSecondaryBtnRect.Width - exitSize.X) * 0.5f, _modalSecondaryBtnRect.Y + 13),
            Color.White,
            scale: 1,
            dropShadow: true);
    }

    /// <summary>
    /// Banner undangan Trade non-intrusif di bagian atas tengah layar dengan tombol ACCEPT / DECLINE
    /// dan progress bar hitung mundur 15 detik.
    /// </summary>
    private void DrawIncomingTradeInviteBanner(
        SpriteBatch spriteBatch,
        Viewport viewport,
        IncomingTradeInviteView? invite,
        Point mousePosition)
    {
        if (invite is null || invite.RemainingSeconds <= 0f)
        {
            _inviteBannerRect = Rectangle.Empty;
            _inviteAcceptBtnRect = Rectangle.Empty;
            _inviteDeclineBtnRect = Rectangle.Empty;
            return;
        }

        string title = $"{invite.RequesterName} mengajak Trade!";
        string timerLine = $"Berakhir dalam {Math.Max(1, (int)MathF.Ceiling(invite.RemainingSeconds))}s";
        Vector2 titleSize = _font.MeasureString(title, scale: 1);

        const int btnW = 78;
        const int btnH = 26;
        const int btnGap = 6;
        int textAreaW = Math.Max(192, (int)MathF.Ceiling(titleSize.X) + 12);
        int w = 44 + textAreaW + btnW * 2 + btnGap + 18;
        const int h = 54;
        int x = (viewport.Width - w) / 2;
        const int y = 12;

        _inviteBannerRect = new Rectangle(x, y, w, h);
        DrawGtPanel(spriteBatch, _inviteBannerRect, GtPanelDarkTranslucent, GtGoldBorder, topHighlightAlpha: 64, borderThickness: 2);

        // Lencana ikon tukar panah 2 arah di sisi kiri
        var badgeRect = new Rectangle(x + 10, y + 11, 24, 24);
        DrawGtPanel(spriteBatch, badgeRect, GtPanelSolid, GtGoldBorder, topHighlightAlpha: 55);
        DrawPixelTradeArrows(spriteBatch, badgeRect.X + 5, badgeRect.Y + 6, GtGoldBorder);

        // Teks utama & timer
        int tx = badgeRect.Right + 10;
        _font.DrawString(spriteBatch, title, new Vector2(tx, y + 12), Color.White, scale: 1, dropShadow: true, bold: true);
        _font.DrawString(spriteBatch, timerLine, new Vector2(tx, y + 26), WorldStatusCyan, scale: 1, dropShadow: true);

        // Tombol hijau [ ACCEPT ] & merah [ DECLINE ]
        int btnY = y + 10;
        _inviteDeclineBtnRect = new Rectangle(x + w - 10 - btnW, btnY, btnW, btnH);
        _inviteAcceptBtnRect = new Rectangle(_inviteDeclineBtnRect.X - btnGap - btnW, btnY, btnW, btnH);

        bool acceptHover = _inviteAcceptBtnRect.Contains(mousePosition);
        bool declineHover = _inviteDeclineBtnRect.Contains(mousePosition);

        Color acceptFill = acceptHover ? new Color(72, 192, 92) : GtGreenBtn;
        Color declineFill = declineHover ? new Color(220, 80, 80) : GtRedBtn;

        DrawGtPanel(spriteBatch, _inviteAcceptBtnRect, acceptFill, GtGreenBorder, topHighlightAlpha: 68);
        Vector2 accSize = _font.MeasureString("ACCEPT", scale: 1);
        _font.DrawString(
            spriteBatch,
            "ACCEPT",
            new Vector2(_inviteAcceptBtnRect.X + (_inviteAcceptBtnRect.Width - accSize.X) * 0.5f, _inviteAcceptBtnRect.Y + 9),
            Color.White,
            scale: 1,
            dropShadow: true,
            bold: true);

        DrawGtPanel(spriteBatch, _inviteDeclineBtnRect, declineFill, GtRedBorder, topHighlightAlpha: 68);
        Vector2 decSize = _font.MeasureString("DECLINE", scale: 1);
        _font.DrawString(
            spriteBatch,
            "DECLINE",
            new Vector2(_inviteDeclineBtnRect.X + (_inviteDeclineBtnRect.Width - decSize.X) * 0.5f, _inviteDeclineBtnRect.Y + 9),
            Color.White,
            scale: 1,
            dropShadow: true,
            bold: true);

        // Progress bar timer 15 detik di bagian bawah banner
        int barX = x + 10;
        int barY = y + h - 8;
        int barW = w - 20;
        const int barH = 4;
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(barX, barY, barW, barH), new Color(10, 30, 50, 220));
        int fillW = Math.Max(0, (int)MathF.Round(barW * invite.Progress));
        if (fillW > 0)
        {
            Color barColor = invite.RemainingSeconds <= 4f ? new Color(255, 132, 92) : GtGoldBorder;
            spriteBatch.Draw(_atlas.Pixel, new Rectangle(barX, barY, fillW, barH), barColor);
        }
    }

    /// <summary>
    /// Popup aksi kecil saat mengklik karakter pemain lain memakai Wrench (dalam jangkauan ~4 tile):
    /// menampilkan tombol [ TRADE ] serta [ GIVE ACCESS ] khusus pemilik World Lock.
    /// </summary>
    private void DrawWrenchPlayerPopup(
        SpriteBatch spriteBatch,
        Viewport viewport,
        WrenchPlayerPopupView? popup,
        Point mousePosition)
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
        Vector2 headerSize = _font.MeasureString(header, scale: 1);

        int w = Math.Max(148, (int)MathF.Ceiling(headerSize.X) + 28);
        int h = p.CanGiveWorldLockAccess ? 96 : 64;

        int px = (int)MathF.Round(p.ScreenAnchor.X - w * 0.5f);
        int py = (int)MathF.Round(p.ScreenAnchor.Y - h - 18f);
        px = Math.Clamp(px, 10, Math.Max(10, viewport.Width - w - 10));
        py = Math.Clamp(py, 10, Math.Max(10, viewport.Height - h - 90));

        _playerPopupRect = new Rectangle(px, py, w, h);
        DrawGtPanel(spriteBatch, _playerPopupRect, GtPanelDarkTranslucent, GtBorderBright, topHighlightAlpha: 64, borderThickness: 2);

        // Nama pemain target
        _font.DrawString(
            spriteBatch,
            header,
            new Vector2(px + (w - headerSize.X) * 0.5f, py + 8),
            ChatPlayerNameColor,
            scale: 1,
            dropShadow: true,
            bold: true);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(px + 8, py + 21, w - 16, 1), new Color(126, 218, 255, 110));

        // Tombol [ TRADE ]
        _playerPopupTradeBtnRect = new Rectangle(px + 10, py + 28, w - 20, 26);
        bool tradeHover = _playerPopupTradeBtnRect.Contains(mousePosition);
        DrawGtPanel(
            spriteBatch,
            _playerPopupTradeBtnRect,
            tradeHover ? new Color(72, 192, 92) : GtGreenBtn,
            GtGreenBorder,
            topHighlightAlpha: 68);
        Vector2 tradeLabelSize = _font.MeasureString("TRADE", scale: 1);
        _font.DrawString(
            spriteBatch,
            "TRADE",
            new Vector2(_playerPopupTradeBtnRect.X + (_playerPopupTradeBtnRect.Width - tradeLabelSize.X) * 0.5f, _playerPopupTradeBtnRect.Y + 9),
            Color.White,
            scale: 1,
            dropShadow: true,
            bold: true);

        // Tombol [ GIVE ACCESS ] khusus pemilik World Lock
        if (p.CanGiveWorldLockAccess)
        {
            _playerPopupGiveAccessBtnRect = new Rectangle(px + 10, py + 60, w - 20, 26);
            bool accessHover = _playerPopupGiveAccessBtnRect.Contains(mousePosition);
            DrawGtPanel(
                spriteBatch,
                _playerPopupGiveAccessBtnRect,
                accessHover ? new Color(214, 146, 46) : new Color(188, 122, 34),
                GtGoldBorder,
                topHighlightAlpha: 68);
            Vector2 accessLabelSize = _font.MeasureString("GIVE ACCESS", scale: 1);
            _font.DrawString(
                spriteBatch,
                "GIVE ACCESS",
                new Vector2(_playerPopupGiveAccessBtnRect.X + (_playerPopupGiveAccessBtnRect.Width - accessLabelSize.X) * 0.5f, _playerPopupGiveAccessBtnRect.Y + 9),
                Color.White,
                scale: 1,
                dropShadow: true,
                bold: true);
        }
        else
        {
            _playerPopupGiveAccessBtnRect = Rectangle.Empty;
        }
    }

    /// <summary>
    /// Modal Jendela Trade 2 Arah bergaya Growtopia:
    /// - Header: TRADE + nama kedua pihak (Kamu <-> Lawan)
    /// - Sisi Kiri (Penawaranmu): 4 slot besar + kontrol cepat (-1, +1, +10, MAX, CLR) + status badge
    /// - Sisi Kanan (Penawaran Lawan): 4 slot besar read-only + tooltip hover + status badge
    /// - Tombol aksi 2 tahap: [ ACCEPT ] (cooldown timer saat perubahan) -> emas berkilau [ DO THE TRADE! ] + [ CANCEL ]
    /// </summary>
    private void DrawTradeModal(
        SpriteBatch spriteBatch,
        Viewport viewport,
        GameClient client,
        Point mousePosition,
        float elapsedTime)
    {
        ActiveTradeView? trade = client.ActiveTrade;
        if (trade is null)
        {
            return;
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

        const int w = 640;
        const int h = 412;
        int x = (viewport.Width - w) / 2;
        int maxBottom = _drawerHandleRect != Rectangle.Empty ? _drawerHandleRect.Y - 12 : viewport.Height - 108;
        int idealY = (viewport.Height - h) / 2 - 38;
        int y = Math.Clamp(idealY, 16, Math.Max(16, maxBottom - h));
        _modalPanelRect = new Rectangle(x, y, w, h);

        DrawGtPanel(spriteBatch, _modalPanelRect, GtPanelSolid, GtBorderBright, topHighlightAlpha: 72, borderThickness: 2);

        // === HEADER ===
        _font.DrawString(spriteBatch, "TRADE", new Vector2(x + 20, y + 16), GtGoldBorder, scale: 2, dropShadow: true, bold: true);

        // Nama kedua pemain: <Kamu> [panah 2 arah] <Lawan>
        string myLabel = myName.ToUpperInvariant();
        string partnerLabel = partnerName.ToUpperInvariant();
        Vector2 myLabelSize = _font.MeasureString(myLabel, scale: 1);
        Vector2 partnerLabelSize = _font.MeasureString(partnerLabel, scale: 1);
        const int arrowGapW = 28;
        int pairTotalW = (int)(myLabelSize.X + arrowGapW + partnerLabelSize.X);
        int pairStartX = x + (w - pairTotalW) / 2 + 12;
        int pairY = y + 21;

        _font.DrawString(spriteBatch, myLabel, new Vector2(pairStartX, pairY), ChatPlayerNameColor, scale: 1, dropShadow: true, bold: true);
        DrawPixelTradeArrows(spriteBatch, pairStartX + (int)myLabelSize.X + 7, pairY - 2, GtGoldBorder);
        _font.DrawString(spriteBatch, partnerLabel, new Vector2(pairStartX + (int)myLabelSize.X + arrowGapW, pairY), Color.White, scale: 1, dropShadow: true, bold: true);

        _modalCloseBtnRect = new Rectangle(x + w - 38, y + 13, 26, 26);
        DrawGtPanel(spriteBatch, _modalCloseBtnRect, GtRedBtn, GtRedBorder, topHighlightAlpha: 55);
        _font.DrawString(spriteBatch, "X", new Vector2(_modalCloseBtnRect.X + 10, _modalCloseBtnRect.Y + 9), Color.White, scale: 1, dropShadow: true);

        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x + 16, y + 46, w - 32, 1), new Color(126, 218, 255, 110));

        // === DUA KOLOM PENAWARAN ===
        const int colW = 286;
        const int colH = 266;
        int colY = y + 54;
        int leftColX = x + 18;
        int rightColX = x + w - 18 - colW;

        Color leftBorder = myConfirmed ? GtGoldBorder : (myAccepted ? GtGreenBorder : GtBorderSubtle);
        Color rightBorder = partnerConfirmed ? GtGoldBorder : (partnerAccepted ? GtGreenBorder : GtBorderSubtle);

        var leftColRect = new Rectangle(leftColX, colY, colW, colH);
        var rightColRect = new Rectangle(rightColX, colY, colW, colH);

        DrawGtPanel(spriteBatch, leftColRect, GtPanelDarkTranslucent, leftBorder, topHighlightAlpha: 44, borderThickness: myAccepted || myConfirmed ? 2 : 1);
        DrawGtPanel(spriteBatch, rightColRect, GtPanelDarkTranslucent, rightBorder, topHighlightAlpha: 44, borderThickness: partnerAccepted || partnerConfirmed ? 2 : 1);

        // Panah pemisah di tengah kedua kolom
        int midX = (leftColRect.Right + rightColRect.Left) / 2;
        DrawPixelTradeArrows(spriteBatch, midX - 7, colY + 114, bothAccepted ? GtGoldBorder : GtBorderBright);

        // Judul kolom kiri & kanan
        const string yourHeader = "YOUR OFFER";
        Vector2 yourHeaderSize = _font.MeasureString(yourHeader, scale: 1);
        _font.DrawString(spriteBatch, yourHeader, new Vector2(leftColX + (colW - yourHeaderSize.X) * 0.5f, colY + 10), ChatPlayerNameColor, scale: 1, dropShadow: true, bold: true);

        string partnerHeader = $"{partnerLabel}'S OFFER";
        if (partnerHeader.Length > 26) partnerHeader = partnerHeader[..26];
        Vector2 partnerHeaderSize = _font.MeasureString(partnerHeader, scale: 1);
        _font.DrawString(spriteBatch, partnerHeader, new Vector2(rightColX + (colW - partnerHeaderSize.X) * 0.5f, colY + 10), new Color(255, 240, 168), scale: 1, dropShadow: true, bold: true);

        // === GRID 2x2 SLOT BESAR (72x72) UNTUK MASING-MASING SISI ===
        const int offerSlotSize = 72;
        const int offerSlotGap = 12;
        int gridTotalW = offerSlotSize * 2 + offerSlotGap;
        int leftGridX = leftColX + (colW - gridTotalW) / 2;
        int rightGridX = rightColX + (colW - gridTotalW) / 2;
        int gridStartY = colY + 28;

        TradeOfferSlot? hoveredTooltipSlot = null;
        bool hoveredTooltipIsLocal = false;

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

            DrawTradeOfferSlotBox(
                spriteBatch,
                localRect,
                localSlot,
                slotNumber: i + 1,
                isFocused: localFocused,
                isHovered: localHover,
                isReadOnly: false);

            if (localHover && !localSlot.IsEmpty)
            {
                hoveredTooltipSlot = localSlot;
                hoveredTooltipIsLocal = true;
            }

            // Sisi Kanan (Penawaran Lawan - Read-Only)
            int rsx = rightGridX + col * (offerSlotSize + offerSlotGap);
            int rsy = gridStartY + row * (offerSlotSize + offerSlotGap);
            var partnerRect = new Rectangle(rsx, rsy, offerSlotSize, offerSlotSize);
            _tradePartnerSlotRects[i] = partnerRect;

            bool partnerHover = partnerRect.Contains(mousePosition);
            TradeOfferSlot partnerSlot = partnerOffers[i];

            DrawTradeOfferSlotBox(
                spriteBatch,
                partnerRect,
                partnerSlot,
                slotNumber: i + 1,
                isFocused: false,
                isHovered: partnerHover,
                isReadOnly: true);

            if (partnerHover && !partnerSlot.IsEmpty)
            {
                hoveredTooltipSlot = partnerSlot;
                hoveredTooltipIsLocal = false;
            }
        }

        // === KONTROL CEPAT (+1, +10, MAX, -1, CLR) DI BAWAH 4 SLOT KIRI ===
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
        const int quickBtnW = 48;
        const int quickBtnH = 22;
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

            bool qHover = enabled && qRect.Contains(mousePosition);
            Color qFill = !enabled
                ? new Color(24, 52, 76, 175)
                : quickDefs[q].IsDanger
                    ? (qHover ? new Color(216, 78, 78) : GtRedBtn)
                    : (qHover ? GtSlotSelectedFill : GtSlotFill);
            Color qBorder = !enabled
                ? new Color(72, 114, 146, 160)
                : quickDefs[q].IsDanger ? GtRedBorder : GtBorderBright;
            Color qText = enabled ? Color.White : new Color(128, 160, 184);

            DrawGtPanel(spriteBatch, qRect, qFill, qBorder, topHighlightAlpha: enabled ? (byte)50 : (byte)20);
            Vector2 qSize = _font.MeasureString(quickDefs[q].Label, scale: 1);
            _font.DrawString(
                spriteBatch,
                quickDefs[q].Label,
                new Vector2(qRect.X + (qRect.Width - qSize.X) * 0.5f, qRect.Y + 7),
                qText,
                scale: 1,
                dropShadow: true);
        }

        // Petunjuk pemeriksaan read-only di bawah 4 slot kanan
        const string inspectHint = "HOVER SLOT TO INSPECT ITEM";
        Vector2 inspectHintSize = _font.MeasureString(inspectHint, scale: 1);
        _font.DrawString(
            spriteBatch,
            inspectHint,
            new Vector2(rightColX + (colW - inspectHintSize.X) * 0.5f, quickY + 7),
            new Color(168, 214, 240),
            scale: 1,
            dropShadow: true);

        // === INDIKATOR STATUS BADGE DI BAWAH MASING-MASING SISI ===
        int badgeY = colY + colH - 34;
        DrawTradeStatusBadge(spriteBatch, new Rectangle(leftColX + 20, badgeY, colW - 40, 24), myAccepted, myConfirmed);
        DrawTradeStatusBadge(spriteBatch, new Rectangle(rightColX + 20, badgeY, colW - 40, 24), partnerAccepted, partnerConfirmed);

        // === TOMBOL AKSI BAWAH (TAHAP 1: ACCEPT / COOLDOWN -> TAHAP 2: DO THE TRADE! + CANCEL) ===
        int footerY = colY + colH + 14;
        const int footerH = 38;
        const int cancelW = 204;
        const int footerGap = 12;
        int primaryW = w - 36 - cancelW - footerGap;

        _tradePrimaryBtnRect = new Rectangle(x + 18, footerY, primaryW, footerH);
        _tradeCancelBtnRect = new Rectangle(_tradePrimaryBtnRect.Right + footerGap, footerY, cancelW, footerH);

        bool primaryHover = _tradePrimaryBtnRect.Contains(mousePosition);
        bool cancelHover = _tradeCancelBtnRect.Contains(mousePosition);

        if (cooldownActive)
        {
            _tradePrimaryBtnDisabled = true;
            _tradePrimaryIsFinalConfirm = false;

            DrawGtPanel(
                spriteBatch,
                _tradePrimaryBtnRect,
                new Color(64, 80, 96),
                new Color(142, 162, 180),
                topHighlightAlpha: 28,
                borderThickness: 2);

            string waitLabel = $"WAIT ({cooldown:0.0}s)";
            Vector2 waitSize = _font.MeasureString(waitLabel, scale: 2);
            _font.DrawString(
                spriteBatch,
                waitLabel,
                new Vector2(_tradePrimaryBtnRect.X + (_tradePrimaryBtnRect.Width - waitSize.X) * 0.5f, _tradePrimaryBtnRect.Y + 11),
                new Color(210, 224, 236),
                scale: 2,
                dropShadow: true,
                bold: true);
        }
        else if (bothAccepted)
        {
            _tradePrimaryIsFinalConfirm = true;
            _tradePrimaryBtnDisabled = myConfirmed;

            if (myConfirmed)
            {
                DrawGtPanel(
                    spriteBatch,
                    _tradePrimaryBtnRect,
                    new Color(146, 114, 34),
                    GtGoldBorder,
                    topHighlightAlpha: 45,
                    borderThickness: 2);

                const string waitingPartner = "WAITING PARTNER...";
                Vector2 wpSize = _font.MeasureString(waitingPartner, scale: 1);
                _font.DrawString(
                    spriteBatch,
                    waitingPartner,
                    new Vector2(_tradePrimaryBtnRect.X + (_tradePrimaryBtnRect.Width - wpSize.X) * 0.5f, _tradePrimaryBtnRect.Y + 15),
                    new Color(255, 246, 194),
                    scale: 1,
                    dropShadow: true,
                    bold: true);
            }
            else
            {
                // Efek emas berkilau untuk tombol final [ DO THE TRADE! ]
                float shimmer = 0.5f + 0.5f * MathF.Sin(elapsedTime * 6.5f);
                Color goldFill = Color.Lerp(
                    primaryHover ? new Color(224, 162, 28) : new Color(196, 136, 18),
                    new Color(246, 196, 48),
                    shimmer);
                DrawGtPanel(spriteBatch, _tradePrimaryBtnRect, goldFill, GtGoldBorder, topHighlightAlpha: 120, borderThickness: 2);

                // Kilatan cahaya horizontal halus bergerak melintasi tombol emas
                int glintW = 46;
                float sweep = (elapsedTime * 1.15f) % 1.4f;
                if (sweep <= 1.0f)
                {
                    int gx = _tradePrimaryBtnRect.X + 4 + (int)((_tradePrimaryBtnRect.Width - glintW - 8) * sweep);
                    spriteBatch.Draw(
                        _atlas.Pixel,
                        new Rectangle(gx, _tradePrimaryBtnRect.Y + 3, glintW, _tradePrimaryBtnRect.Height - 6),
                        new Color(255, 255, 224, 48));
                }

                const string finalLabel = "DO THE TRADE!";
                Vector2 finalSize = _font.MeasureString(finalLabel, scale: 2);
                _font.DrawString(
                    spriteBatch,
                    finalLabel,
                    new Vector2(_tradePrimaryBtnRect.X + (_tradePrimaryBtnRect.Width - finalSize.X) * 0.5f, _tradePrimaryBtnRect.Y + 11),
                    Color.White,
                    scale: 2,
                    dropShadow: true,
                    bold: true);
            }
        }
        else
        {
            _tradePrimaryBtnDisabled = false;
            _tradePrimaryIsFinalConfirm = false;

            Color btnFill = myAccepted
                ? (primaryHover ? new Color(50, 152, 102) : new Color(36, 128, 84))
                : (primaryHover ? new Color(72, 192, 92) : GtGreenBtn);

            DrawGtPanel(spriteBatch, _tradePrimaryBtnRect, btnFill, GtGreenBorder, topHighlightAlpha: 72, borderThickness: 2);

            string acceptLabel = myAccepted ? "ACCEPTED (UNDO)" : "ACCEPT";
            int textScale = myAccepted ? 1 : 2;
            Vector2 accSize = _font.MeasureString(acceptLabel, scale: textScale);
            int textY = myAccepted ? _tradePrimaryBtnRect.Y + 15 : _tradePrimaryBtnRect.Y + 11;
            _font.DrawString(
                spriteBatch,
                acceptLabel,
                new Vector2(_tradePrimaryBtnRect.X + (_tradePrimaryBtnRect.Width - accSize.X) * 0.5f, textY),
                Color.White,
                scale: textScale,
                dropShadow: true,
                bold: true);
        }

        // Tombol merah [ CANCEL ]
        DrawGtPanel(
            spriteBatch,
            _tradeCancelBtnRect,
            cancelHover ? new Color(220, 78, 78) : GtRedBtn,
            GtRedBorder,
            topHighlightAlpha: 68,
            borderThickness: 2);
        const string cancelLabel = "CANCEL [ESC]";
        Vector2 cancelSize = _font.MeasureString(cancelLabel, scale: 1);
        _font.DrawString(
            spriteBatch,
            cancelLabel,
            new Vector2(_tradeCancelBtnRect.X + (_tradeCancelBtnRect.Width - cancelSize.X) * 0.5f, _tradeCancelBtnRect.Y + 15),
            Color.White,
            scale: 1,
            dropShadow: true,
            bold: true);

        // Baris petunjuk anti-scam di bawah tombol
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
        Vector2 hintSize = _font.MeasureString(footerHint, scale: 1);
        _font.DrawString(
            spriteBatch,
            footerHint,
            new Vector2(x + (w - hintSize.X) * 0.5f, y + h - 22),
            hintColor,
            scale: 1,
            dropShadow: true);

        // Tooltip melayang di atas segalanya saat kursor mengarah ke slot item terisi
        if (hoveredTooltipSlot is { } tipSlot)
        {
            DrawTradeItemTooltip(spriteBatch, viewport, mousePosition, tipSlot, hoveredTooltipIsLocal);
        }
    }

    private void DrawTradeOfferSlotBox(
        SpriteBatch spriteBatch,
        Rectangle rect,
        TradeOfferSlot slot,
        int slotNumber,
        bool isFocused,
        bool isHovered,
        bool isReadOnly)
    {
        Color fill = !slot.IsEmpty
            ? (isHovered ? GtSlotSelectedFill : GtSlotFill)
            : (isFocused && !isReadOnly ? new Color(42, 102, 146) : new Color(24, 68, 102));
        Color border = isHovered
            ? GtGoldBorder
            : (isFocused && !isReadOnly ? GtGoldBorder : new Color(96, 174, 218));

        UiDraw.Panel(spriteBatch, _atlas.Pixel, rect, fill, border, thickness: isFocused || isHovered ? 2 : 1, radius: 5);
        spriteBatch.Draw(
            _atlas.Pixel,
            new Rectangle(rect.X + 3, rect.Y + 2, rect.Width - 6, 1),
            new Color(255, 255, 255, isFocused || isHovered ? 88 : 42));

        // Nomor slot kecil di kiri atas
        _font.DrawString(
            spriteBatch,
            slotNumber.ToString(),
            new Vector2(rect.X + 5, rect.Y + 4),
            new Color(168, 216, 246, 190),
            scale: 1,
            dropShadow: true);

        if (slot.IsEmpty)
        {
            const string empty = "EMPTY";
            Vector2 emptySize = _font.MeasureString(empty, scale: 1);
            _font.DrawString(
                spriteBatch,
                empty,
                new Vector2(rect.X + (rect.Width - emptySize.X) * 0.5f, rect.Y + (rect.Height - emptySize.Y) * 0.5f),
                new Color(120, 166, 198, 175),
                scale: 1);
            return;
        }

        // Sprite ikon item besar (40x40) dari PixelAtlas
        Texture2D icon = _atlas.GetItemIcon(slot.ItemId);
        var iconRect = new Rectangle(rect.X + (rect.Width - 40) / 2, rect.Y + 12, 40, 40);
        spriteBatch.Draw(icon, iconRect, Color.White);

        // Badge jumlah di kanan bawah
        string qtyText = $"x{slot.Count}";
        Vector2 qtySize = _font.MeasureString(qtyText, scale: 1);
        int badgeW = (int)MathF.Ceiling(qtySize.X) + 8;
        int badgeH = 14;
        var qtyBadge = new Rectangle(rect.Right - badgeW - 3, rect.Bottom - badgeH - 3, badgeW, badgeH);
        UiDraw.RoundedRect(spriteBatch, _atlas.Pixel, qtyBadge, new Color(10, 32, 54, 224), radius: 3);
        _font.DrawString(
            spriteBatch,
            qtyText,
            new Vector2(qtyBadge.X + 4, qtyBadge.Y + 3),
            GtGoldBorder,
            scale: 1,
            dropShadow: true,
            bold: true);
    }

    private void DrawTradeStatusBadge(SpriteBatch spriteBatch, Rectangle rect, bool accepted, bool confirmed)
    {
        Color fill;
        Color border;
        Color textColor;
        string text;
        bool showCheck;

        if (confirmed)
        {
            fill = new Color(156, 114, 24);
            border = GtGoldBorder;
            textColor = new Color(255, 244, 152);
            text = "Confirmed";
            showCheck = true;
        }
        else if (accepted)
        {
            fill = GtGreenBtn;
            border = GtGreenBorder;
            textColor = Color.White;
            text = "Accepted";
            showCheck = true;
        }
        else
        {
            fill = GtPanelDark;
            border = GtBorderSubtle;
            textColor = new Color(188, 226, 250);
            text = "Editing...";
            showCheck = false;
        }

        DrawGtPanel(spriteBatch, rect, fill, border, topHighlightAlpha: 48);

        Vector2 textSize = _font.MeasureString(text, scale: 1);
        int checkExtraW = showCheck ? 13 : 0;
        int contentW = (int)MathF.Ceiling(textSize.X) + checkExtraW;
        int startX = rect.X + (rect.Width - contentW) / 2;
        int textY = rect.Y + (rect.Height - (int)textSize.Y) / 2;

        _font.DrawString(spriteBatch, text, new Vector2(startX, textY), textColor, scale: 1, dropShadow: true, bold: showCheck);
        if (showCheck)
        {
            DrawPixelCheckmark(spriteBatch, startX + (int)MathF.Ceiling(textSize.X) + 5, textY + 1, confirmed ? GtGoldBorder : Color.White);
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
        string category = slot.ItemId == TileIds.WorldLock
            ? "WORLD LOCK"
            : ItemCatalog.IsSeed(slot.ItemId)
                ? "SEED"
                : ItemCatalog.IsBackgroundItem(slot.ItemId)
                    ? "WALL"
                    : "BLOCK";
        string line1 = itemName.ToUpperInvariant();
        string line2 = $"Amount: {slot.Count}  |  {category}";
        string? line3 = isLocalSlot ? "Click: -1  |  R-Click/Shift: Clear" : null;

        float maxW = MathF.Max(
            _font.MeasureString(line1, scale: 1).X,
            _font.MeasureString(line2, scale: 1).X);
        if (line3 is not null)
        {
            maxW = MathF.Max(maxW, _font.MeasureString(line3, scale: 1).X);
        }

        const int pad = 9;
        int w = (int)MathF.Ceiling(maxW) + pad * 2 + 4;
        int h = (line3 is not null ? 3 : 2) * 13 + pad * 2 - 2;

        int tx = mousePosition.X + 16;
        int ty = mousePosition.Y + 14;
        if (tx + w > viewport.Width - 8) tx = mousePosition.X - w - 10;
        if (ty + h > viewport.Height - 8) ty = viewport.Height - h - 8;
        tx = Math.Max(8, tx);
        ty = Math.Max(8, ty);

        var rect = new Rectangle(tx, ty, w, h);
        DrawGtPanel(spriteBatch, rect, new Color(12, 34, 58, 244), GtGoldBorder, topHighlightAlpha: 56);

        _font.DrawString(spriteBatch, line1, new Vector2(tx + pad, ty + pad), GtGoldBorder, scale: 1, dropShadow: true, bold: true);
        _font.DrawString(spriteBatch, line2, new Vector2(tx + pad, ty + pad + 13), Color.White, scale: 1, dropShadow: true);
        if (line3 is not null)
        {
            _font.DrawString(spriteBatch, line3, new Vector2(tx + pad, ty + pad + 26), WorldStatusCyan, scale: 1);
        }
    }

    private void DrawPixelCheckmark(SpriteBatch sb, int x, int y, Color color)
    {
        var shadow = new Color(0, 0, 0, 185);
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
        var shadow = new Color(0, 0, 0, 180);

        // Panah atas mengarah ke kanan (->)
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, y + 3, 12, 2), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 9, y + 1, 2, 6), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x, y + 2, 12, 2), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 9, y, 2, 6), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 11, y + 1, 2, 4), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 13, y + 2, 1, 2), color);

        // Panah bawah mengarah ke kiri (<-)
        int ly = y + 7;
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, ly + 3, 12, 2), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 5, ly + 1, 2, 6), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 2, ly + 2, 12, 2), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, ly, 2, 6), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, ly + 1, 2, 4), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x, ly + 2, 1, 2), color);
    }

    /// <summary>
    /// Panel konsisten bergaya Growtopia: latar membulat + border tipis 1-2px + garis bevel highlight 1px di tepi atas.
    /// </summary>
    private void DrawGtPanel(
        SpriteBatch spriteBatch,
        Rectangle rect,
        Color fill,
        Color border,
        byte topHighlightAlpha = 48,
        int borderThickness = 1)
    {
        UiDraw.RoundedRect(spriteBatch, _atlas.Pixel, rect, fill, radius: 5);

        // Border tipis rapi di sekeliling panel
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(rect.X + 2, rect.Y, rect.Width - 4, borderThickness), border);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(rect.X + 2, rect.Bottom - borderThickness, rect.Width - 4, borderThickness), border);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(rect.X, rect.Y + 2, borderThickness, rect.Height - 4), border);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(rect.Right - borderThickness, rect.Y + 2, borderThickness, rect.Height - 4), border);

        // Garis highlight 1px halus di tepi dalam atas (efek glossy bevel halus, tidak menutup teks)
        if (topHighlightAlpha > 0 && rect.Width > 8)
        {
            spriteBatch.Draw(
                _atlas.Pixel,
                new Rectangle(rect.X + 3, rect.Y + borderThickness, rect.Width - 6, 1),
                new Color((byte)255, (byte)255, (byte)255, topHighlightAlpha));
        }
    }

    private void DrawPixelArrowUp(SpriteBatch sb, int x, int y, Color color)
    {
        var shadow = new Color(0, 0, 0, 180);
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
        var shadow = new Color(0, 0, 0, 180);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, y + 1, 7, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 2, y + 2, 5, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, y + 3, 3, 1), shadow);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 4, y + 4, 1, 1), shadow);

        sb.Draw(_atlas.Pixel, new Rectangle(x, y, 7, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 1, y + 1, 5, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 2, y + 2, 3, 1), color);
        sb.Draw(_atlas.Pixel, new Rectangle(x + 3, y + 3, 1, 1), color);
    }

    private static Color WithAlpha(Color color, float alpha) =>
        new(color.R, color.G, color.B, (byte)Math.Clamp((int)(color.A * alpha), 0, 255));
}
