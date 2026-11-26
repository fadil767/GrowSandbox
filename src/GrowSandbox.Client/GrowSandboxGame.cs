using GrowSandbox.Client.Audio;
using GrowSandbox.Client.Config;
using GrowSandbox.Client.Entities;
using GrowSandbox.Client.Graphics;
using GrowSandbox.Client.Network;
using GrowSandbox.Client.UI;
using GrowSandbox.Client.World;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Networking;
using GrowSandbox.Shared.Networking.Packets;
using GrowSandbox.Shared.Physics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace GrowSandbox.Client;

public sealed class GrowSandboxGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly GameClient _gameClient;
    private readonly PlayerController _playerController;
    private readonly ParticleSystem _particles = new();
    private readonly TileFeedbackTracker _tileFeedback = new();
    private readonly FloatingTextSystem _floatingTexts = new();

    private SpriteBatch? _spriteBatch;
    private PixelAtlas? _atlas;
    private PixelFont? _font;
    private TileRenderer? _tileRenderer;
    private HudRenderer? _hud;
    private AuthScreenRenderer? _authScreen;
    private Camera2D? _camera;
    private Camera2D? _menuCamera;
    private SfxManager? _sfx;
    private ClientConfig _config = new();

    private ClientScreenState _screenState = ClientScreenState.AuthMenu;
    private AuthTabKind _authTab = AuthTabKind.Login;
    private AuthInputField _authFocusedField = AuthInputField.Username;
    private string _authUsername = string.Empty;
    private string _authPassword = string.Empty;
    private string _authConfirmPassword = string.Empty;
    private string _authServerAddress = ClientConfig.DefaultServerAddress;
    private bool _authShowPassword;
    private bool _authBusy;
    private string _authStatusMessage = string.Empty;
    private bool _authStatusIsError;
    private string _enteringWorldName = GameClient.DefaultWorldName;

    private KeyboardState _previousKeyboard;
    private MouseState _previousMouse;
    private int _hoveredTileX = -1;
    private int _hoveredTileY = -1;
    private bool _hoveredTileInReach;
    private float _elapsedTime;

    private int _fpsFrameCount;
    private float _fpsTimer;
    private int _currentFps = 60;

    private bool _isChatTyping;
    private string _chatDraft = string.Empty;
    private string _warpDraft = string.Empty;
    private string _lockAccessDraft = string.Empty;
    private int _wrenchPopupTargetPlayerId = -1;
    private string _wrenchPopupTargetName = string.Empty;
    private Point _lastMousePosition;

    private const float WrenchPlayerReachPx = 4f * WorldConstants.TileSize;
    private const float WrenchPopupAutoCloseReachPx = 5.5f * WorldConstants.TileSize;

    public GrowSandboxGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
        };

        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        Window.AllowUserResizing = true;
        IsFixedTimeStep = true;
        TargetElapsedTime = TimeSpan.FromSeconds(PhysicsConstants.FixedDeltaTime);

        _playerController = new PlayerController();
        _gameClient = new GameClient();
        _gameClient.AuthResponseReceived += OnAuthResponseReceived;
        _gameClient.DisconnectedFromServer += OnDisconnectedFromServer;
        _gameClient.WorldSnapshotReceived += OnWorldSnapshotReceived;
        _gameClient.LocalPlayerStateCorrected += OnLocalPlayerStateCorrected;
        _gameClient.TileChanged += OnTileChanged;
        _gameClient.DropPickedUp += OnDropPickedUp;
        _gameClient.WorldLockStateChanged += OnWorldLockStateChanged;
        _gameClient.SystemToastReceived += OnSystemToastReceived;
        _gameClient.TradeInviteReceived += OnTradeInviteReceived;
        _gameClient.TradeStateSynced += OnTradeStateSynced;
        _gameClient.TradeEndedReceived += OnTradeEndedReceived;
    }

    protected override void Initialize()
    {
        Window.Title = "GrowSandbox";

        _camera = new Camera2D(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        _camera.CenterOnTile(WorldConstants.Width / 2, 29);

        _menuCamera = new Camera2D(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        _menuCamera.CenterOnTile(WorldConstants.Width / 2, 26);

        // Muat hanya LastUsername dan LastServerAddress dari client_config.json (tidak pernah menyimpan password).
        _config = ClientConfig.Load();
        _authUsername = _config.LastUsername;
        _authServerAddress = _config.LastServerAddress;
        _authFocusedField = AuthInputField.Username;
        _screenState = ClientScreenState.AuthMenu;

        Window.ClientSizeChanged += OnClientSizeChanged;
        Window.TextInput += OnTextInput;

        _previousKeyboard = Keyboard.GetState();
        _previousMouse = Mouse.GetState();

        // JANGAN otomatis mengirim JoinWorldRequest saat aplikasi dibuka;
        // tunggu sampai Login / Register berhasil di layar AuthMenu.
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        _atlas = new PixelAtlas(GraphicsDevice);
        _font = new PixelFont(GraphicsDevice);
        _tileRenderer = new TileRenderer(_atlas, _font);
        _hud = new HudRenderer(_atlas, _font);
        _authScreen = new AuthScreenRenderer(_atlas, _font);
        _sfx = new SfxManager();
    }

    protected override void Update(GameTime gameTime)
    {
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        _elapsedTime += dt;

        // Perhitungan FPS halus tiap 0.25 detik untuk panel debug F3
        _fpsFrameCount++;
        _fpsTimer += dt;
        if (_fpsTimer >= 0.25f)
        {
            _currentFps = (int)MathF.Round(_fpsFrameCount / _fpsTimer);
            _fpsFrameCount = 0;
            _fpsTimer = 0f;
        }

        KeyboardState keyboard = Keyboard.GetState();
        MouseState mouse = Mouse.GetState();
        _lastMousePosition = mouse.Position;

        _gameClient.PollEvents();
        _gameClient.UpdateTimers(dt);

        if (_screenState == ClientScreenState.AuthMenu)
        {
            UpdateAuthMenu(keyboard, mouse);
        }
        else if (_screenState == ClientScreenState.EnteringWorld)
        {
            UpdateMenuScenicCamera();
            if (IsKeyJustPressed(keyboard, Keys.Escape))
            {
                PerformLogout("Dibatalkan. Silakan login kembali.");
            }
        }
        else
        {
            UpdateInGame(dt, keyboard, mouse);
        }

        _previousKeyboard = keyboard;
        _previousMouse = mouse;
        base.Update(gameTime);
    }

    private void UpdateMenuScenicCamera()
    {
        if (_menuCamera is null)
        {
            return;
        }

        // Gerakkan kamera latar perlahan agar bukit parallax & awan terasa hidup di layar Login/Register.
        float panX = WorldConstants.WorldPixelWidth * 0.5f + MathF.Sin(_elapsedTime * 0.14f) * 340f;
        _menuCamera.Position = new Vector2(panX, 26.5f * WorldConstants.TileSize);
    }

    private void UpdateAuthMenu(KeyboardState keyboard, MouseState mouse)
    {
        UpdateMenuScenicCamera();

        if (!IsActive)
        {
            return;
        }

        if (IsKeyJustPressed(keyboard, Keys.Escape))
        {
            Exit();
            return;
        }

        if (IsKeyJustPressed(keyboard, Keys.Tab))
        {
            bool reverse = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
            CycleAuthField(reverse);
        }

        if (IsKeyJustPressed(keyboard, Keys.Enter) && !_authBusy)
        {
            SubmitAuthForm();
        }

        bool leftClicked = mouse.LeftButton == ButtonState.Pressed &&
                           _previousMouse.LeftButton == ButtonState.Released;

        if (leftClicked && _authScreen is not null)
        {
            AuthClickAction action = _authScreen.HandleLeftClick(mouse.Position, _authTab, out AuthInputField clickedField);
            if (action != AuthClickAction.None)
            {
                _sfx?.PlayUiClick();
            }

            switch (action)
            {
                case AuthClickAction.SwitchToLogin:
                    _authTab = AuthTabKind.Login;
                    if (_authFocusedField == AuthInputField.ConfirmPassword)
                    {
                        _authFocusedField = AuthInputField.Password;
                    }

                    _authStatusMessage = string.Empty;
                    _authStatusIsError = false;
                    break;

                case AuthClickAction.SwitchToRegister:
                    _authTab = AuthTabKind.Register;
                    _authStatusMessage = string.Empty;
                    _authStatusIsError = false;
                    break;

                case AuthClickAction.FocusField:
                    _authFocusedField = clickedField;
                    break;

                case AuthClickAction.ToggleShowPassword:
                    _authShowPassword = !_authShowPassword;
                    break;

                case AuthClickAction.Submit:
                    if (!_authBusy)
                    {
                        SubmitAuthForm();
                    }

                    break;
            }
        }
    }

    private void CycleAuthField(bool reverse)
    {
        AuthInputField[] order = _authTab == AuthTabKind.Register
            ? [AuthInputField.Username, AuthInputField.Password, AuthInputField.ConfirmPassword, AuthInputField.ServerAddress]
            : [AuthInputField.Username, AuthInputField.Password, AuthInputField.ServerAddress];

        int idx = Array.IndexOf(order, _authFocusedField);
        if (idx < 0)
        {
            _authFocusedField = order[0];
            return;
        }

        int next = reverse
            ? (idx + order.Length - 1) % order.Length
            : (idx + 1) % order.Length;
        _authFocusedField = order[next];
    }

    private void SubmitAuthForm()
    {
        string username = _authUsername.Trim();
        string serverAddress = string.IsNullOrWhiteSpace(_authServerAddress)
            ? ClientConfig.DefaultServerAddress
            : _authServerAddress.Trim();

        if (!ClientConfig.TryParseServerAddress(serverAddress, out string host, out int port))
        {
            SetAuthError("Alamat server tidak valid (contoh: 127.0.0.1:9050).");
            _authFocusedField = AuthInputField.ServerAddress;
            return;
        }

        if (username.Length < NetworkConstants.MinUsernameLength ||
            username.Length > NetworkConstants.MaxUsernameLength)
        {
            SetAuthError($"Username harus {NetworkConstants.MinUsernameLength}-{NetworkConstants.MaxUsernameLength} karakter.");
            _authFocusedField = AuthInputField.Username;
            return;
        }

        if (string.IsNullOrEmpty(_authPassword))
        {
            SetAuthError("Password tidak boleh kosong.");
            _authFocusedField = AuthInputField.Password;
            return;
        }

        bool isRegister = _authTab == AuthTabKind.Register;
        if (isRegister)
        {
            if (_authPassword.Length < NetworkConstants.MinPasswordLength)
            {
                SetAuthError($"Password minimal {NetworkConstants.MinPasswordLength} karakter.");
                _authFocusedField = AuthInputField.Password;
                return;
            }

            if (!string.Equals(_authPassword, _authConfirmPassword, StringComparison.Ordinal))
            {
                SetAuthError("Konfirmasi password tidak cocok.");
                _authFocusedField = AuthInputField.ConfirmPassword;
                return;
            }
        }

        // Simpan hanya LastUsername & LastServerAddress ke client_config.json (password tidak pernah disimpan).
        _config.LastUsername = username;
        _config.LastServerAddress = serverAddress;
        _config.Save();

        _authBusy = true;
        _authStatusIsError = false;
        _authStatusMessage = isRegister ? "Connecting & creating account..." : "Connecting...";

        _gameClient.ConnectAndAuthenticate(host, port, isRegister, username, _authPassword);
    }

    private void SetAuthError(string message)
    {
        _authStatusMessage = message;
        _authStatusIsError = true;
    }

    private void UpdateInGame(float dt, KeyboardState keyboard, MouseState mouse)
    {
        if (IsKeyJustPressed(keyboard, Keys.F3) && _hud is not null)
        {
            _hud.IsDebugOverlayVisible = !_hud.IsDebugOverlayVisible;
        }

        if (IsKeyJustPressed(keyboard, Keys.Escape))
        {
            if (_hud is { IsTradeModalOpen: true })
            {
                _gameClient.SendTradeAction(TradeActionKind.Cancel);
                _sfx?.PlayUiClick();
            }
            else if (_hud is { IsLockModalOpen: true })
            {
                _hud.IsLockModalOpen = false;
                _lockAccessDraft = string.Empty;
            }
            else if (_hud is { IsWarpModalOpen: true })
            {
                _hud.IsWarpModalOpen = false;
            }
            else if (_hud is { IsMenuModalOpen: true })
            {
                _hud.IsMenuModalOpen = false;
            }
            else if (_wrenchPopupTargetPlayerId >= 0)
            {
                ClearWrenchPlayerPopup();
            }
            else if (_isChatTyping)
            {
                _isChatTyping = false;
                _chatDraft = string.Empty;
                _hud?.ResetChatScroll();
            }
            else if (_hud is not null)
            {
                _hud.IsMenuModalOpen = true;
            }
        }

        // Sinkronkan status modal Trade & tutup popup Wrench jika pemain target menjauh/keluar.
        if (_hud is not null && _hud.IsTradeModalOpen != _gameClient.IsInActiveTrade)
        {
            _hud.IsTradeModalOpen = _gameClient.IsInActiveTrade;
        }

        ValidateWrenchPlayerPopup();

        HandleInGameKeyboardShortcuts(keyboard);

        bool movementBlocked = _isChatTyping || (_hud?.HasOpenModal ?? false);

        _playerController.Update(dt, keyboard, _gameClient.Tiles, _gameClient, inputBlocked: movementBlocked);

        if (_playerController.JustJumped)
        {
            _particles.SpawnFootDust(_playerController.FeetPosition, isLanding: false);
            _sfx?.PlayJump();
        }
        else if (_playerController.JustLanded)
        {
            _particles.SpawnFootDust(_playerController.FeetPosition, isLanding: true);
        }

        _tileFeedback.Update(dt);
        _particles.Update(dt);
        _floatingTexts.Update(dt);
        _hud?.Update(dt);

        if (_camera is not null)
        {
            HandleScrollWheel(keyboard, mouse, dt, movementBlocked);

            if (_playerController.IsSpawned)
            {
                _camera.Follow(_playerController.CenterPosition, dt, smoothSpeed: 12f);
            }

            UpdateInGameMouseInteractions(mouse, keyboard);
        }
    }

    private void HandleInGameKeyboardShortcuts(KeyboardState keyboard)
    {
        if (_hud is { IsTradeModalOpen: true })
        {
            return;
        }

        if (_hud is { IsLockModalOpen: true })
        {
            if (IsKeyJustPressed(keyboard, Keys.Enter) && !string.IsNullOrWhiteSpace(_lockAccessDraft))
            {
                _gameClient.SendWorldLockConfigRequest(WorldLockConfigAction.AddAccess, _lockAccessDraft.Trim());
                _lockAccessDraft = string.Empty;
                _sfx?.PlayUiClick();
            }

            return;
        }

        if (_hud is { IsWarpModalOpen: true })
        {
            if (IsKeyJustPressed(keyboard, Keys.Enter) && !string.IsNullOrWhiteSpace(_warpDraft))
            {
                _gameClient.SendJoinWorldRequest(_warpDraft.Trim());
                _hud.IsWarpModalOpen = false;
                _warpDraft = string.Empty;
            }

            return;
        }

        if (_hud is { IsMenuModalOpen: true })
        {
            if (IsKeyJustPressed(keyboard, Keys.M))
            {
                ToggleSfxMute();
            }

            return;
        }

        if (IsKeyJustPressed(keyboard, Keys.Enter))
        {
            if (!_isChatTyping)
            {
                _isChatTyping = true;
                _chatDraft = string.Empty;
                if (_hud is not null)
                {
                    _hud.IsChatVisible = true;
                    _hud.ResetChatScroll();
                }
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(_chatDraft))
                {
                    string trimmed = _chatDraft.Trim();
                    if (trimmed.Equals("/trade", StringComparison.OrdinalIgnoreCase))
                    {
                        _gameClient.AddLocalSystemMessage("Gunakan: /trade <username>", ChatMessageKind.Error);
                    }
                    else if (trimmed.StartsWith("/trade ", StringComparison.OrdinalIgnoreCase))
                    {
                        string targetUsername = trimmed[7..].Trim();
                        if (targetUsername.Length > 0)
                        {
                            _gameClient.SendTradeRequest(targetUsername);
                        }
                        else
                        {
                            _gameClient.AddLocalSystemMessage("Gunakan: /trade <username>", ChatMessageKind.Error);
                        }
                    }
                    else
                    {
                        _gameClient.SendChatMessage(trimmed);
                    }
                }

                _isChatTyping = false;
                _chatDraft = string.Empty;
                _hud?.ResetChatScroll();
            }

            return;
        }

        if (_isChatTyping)
        {
            if (IsKeyJustPressed(keyboard, Keys.PageUp)) _hud?.ScrollChat(2);
            if (IsKeyJustPressed(keyboard, Keys.PageDown)) _hud?.ScrollChat(-2);
            return;
        }

        if (IsKeyJustPressed(keyboard, Keys.T))
        {
            _isChatTyping = true;
            _chatDraft = string.Empty;
            if (_hud is not null)
            {
                _hud.IsChatVisible = true;
                _hud.ResetChatScroll();
            }

            return;
        }

        if (IsKeyJustPressed(keyboard, Keys.Tab) && _hud is not null)
        {
            _hud.IsWarpModalOpen = true;
            _warpDraft = string.Empty;
            return;
        }

        if ((IsKeyJustPressed(keyboard, Keys.I) || IsKeyJustPressed(keyboard, Keys.B)) && _hud is not null)
        {
            _hud.IsInventoryExpanded = !_hud.IsInventoryExpanded;
            _sfx?.PlayUiClick();
            return;
        }

        if (IsKeyJustPressed(keyboard, Keys.M))
        {
            ToggleSfxMute();
            return;
        }

        if (_hud is null)
        {
            return;
        }

        Keys[] slotKeys =
        [
            Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5,
            Keys.D6, Keys.D7, Keys.D8, Keys.D9, Keys.D0,
        ];
        for (int i = 0; i < slotKeys.Length; i++)
        {
            if (IsKeyJustPressed(keyboard, slotKeys[i]))
            {
                _hud.SelectedSlotIndex = i;
                break;
            }
        }

        int totalSlots = (_hud.IsInventoryExpanded ? 2 : 1) * HudRenderer.SlotsPerRow;
        if (IsKeyJustPressed(keyboard, Keys.Q))
        {
            TryDropSelectedItem();
        }
        else if (IsKeyJustPressed(keyboard, Keys.E))
        {
            _hud.SelectedSlotIndex = (_hud.SelectedSlotIndex + 1) % totalSlots;
        }
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_screenState == ClientScreenState.AuthMenu)
        {
            if (_authBusy)
            {
                return;
            }

            HandleAuthTextInput(e.Character);
            return;
        }

        if (_screenState != ClientScreenState.InGame)
        {
            return;
        }

        if (_hud is { IsLockModalOpen: true })
        {
            if (e.Character == '\b')
            {
                if (_lockAccessDraft.Length > 0) _lockAccessDraft = _lockAccessDraft[..^1];
                return;
            }

            if (char.IsLetterOrDigit(e.Character) || e.Character is '_' or '-')
            {
                if (_lockAccessDraft.Length < WorldLockData.MaxPlayerNameLength)
                {
                    _lockAccessDraft += e.Character;
                }
            }

            return;
        }

        if (_hud is { IsWarpModalOpen: true })
        {
            if (e.Character == '\b')
            {
                if (_warpDraft.Length > 0) _warpDraft = _warpDraft[..^1];
                return;
            }

            if (char.IsLetterOrDigit(e.Character) || e.Character is '_' or '-')
            {
                if (_warpDraft.Length < 24)
                {
                    _warpDraft += char.ToUpperInvariant(e.Character);
                }
            }

            return;
        }

        if (!_isChatTyping)
        {
            return;
        }

        if (e.Character == '\b')
        {
            if (_chatDraft.Length > 0)
            {
                _chatDraft = _chatDraft[..^1];
            }

            return;
        }

        if (e.Character is '\r' or '\n' or '\t' or (char)27)
        {
            return;
        }

        if (!char.IsControl(e.Character) && _chatDraft.Length < PacketSerializer.MaxChatMessageLength)
        {
            _chatDraft += e.Character;
        }
    }

    private void HandleAuthTextInput(char ch)
    {
        if (ch == '\b')
        {
            switch (_authFocusedField)
            {
                case AuthInputField.Username:
                    if (_authUsername.Length > 0) _authUsername = _authUsername[..^1];
                    break;
                case AuthInputField.Password:
                    if (_authPassword.Length > 0) _authPassword = _authPassword[..^1];
                    break;
                case AuthInputField.ConfirmPassword:
                    if (_authConfirmPassword.Length > 0) _authConfirmPassword = _authConfirmPassword[..^1];
                    break;
                case AuthInputField.ServerAddress:
                    if (_authServerAddress.Length > 0) _authServerAddress = _authServerAddress[..^1];
                    break;
            }

            return;
        }

        if (ch is '\r' or '\n' or '\t' or (char)27 || ch < 32 || ch > 126)
        {
            return;
        }

        switch (_authFocusedField)
        {
            case AuthInputField.Username:
                if ((char.IsLetterOrDigit(ch) || ch == '_') && _authUsername.Length < NetworkConstants.MaxUsernameLength)
                {
                    _authUsername += ch;
                }

                break;

            case AuthInputField.Password:
                if (_authPassword.Length < 64)
                {
                    _authPassword += ch;
                }

                break;

            case AuthInputField.ConfirmPassword:
                if (_authTab == AuthTabKind.Register && _authConfirmPassword.Length < 64)
                {
                    _authConfirmPassword += ch;
                }

                break;

            case AuthInputField.ServerAddress:
                if (!char.IsWhiteSpace(ch) && _authServerAddress.Length < 64)
                {
                    _authServerAddress += ch;
                }

                break;
        }
    }

    private void HandleScrollWheel(KeyboardState keyboard, MouseState mouse, float dt, bool movementBlocked)
    {
        if (_camera is null)
        {
            return;
        }

        int scrollDelta = mouse.ScrollWheelValue - _previousMouse.ScrollWheelValue;
        if (scrollDelta != 0)
        {
            if (_hud is not null && _hud.IsPointerOverChatLog(mouse.Position))
            {
                _hud.ScrollChat(MathF.Sign(scrollDelta));
            }
            else if (!movementBlocked)
            {
                _camera.Zoom += MathF.Sign(scrollDelta) * 0.1f;
            }
        }

        if (!movementBlocked)
        {
            if (keyboard.IsKeyDown(Keys.OemPlus) || keyboard.IsKeyDown(Keys.Add))
            {
                _camera.Zoom += 1.0f * dt;
            }
            else if (keyboard.IsKeyDown(Keys.OemMinus) || keyboard.IsKeyDown(Keys.Subtract))
            {
                _camera.Zoom -= 1.0f * dt;
            }
        }
    }

    private void UpdateInGameMouseInteractions(MouseState mouse, KeyboardState keyboard)
    {
        bool leftClicked = mouse.LeftButton == ButtonState.Pressed &&
                           _previousMouse.LeftButton == ButtonState.Released;
        bool rightClicked = mouse.RightButton == ButtonState.Pressed &&
                            _previousMouse.RightButton == ButtonState.Released;
        bool shiftHeld = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);

        if (_hud is { IsTradeModalOpen: true } && IsActive && rightClicked)
        {
            if (_hud.HandleRightClick(mouse.Position, out byte clearedSlot))
            {
                ClearTradeOfferSlot(clearedSlot);
                _sfx?.PlayUiClick();
            }

            return;
        }

        if (_hud is not null && IsActive && leftClicked)
        {
            HudClickResult result = _hud.HandleLeftClick(mouse.Position, out string? quickWorld);
            if (result != HudClickResult.None &&
                result != HudClickResult.ToggleMuteSfx &&
                result != HudClickResult.DropSelectedItem)
            {
                _sfx?.PlayUiClick();
            }

            switch (result)
            {
                case HudClickResult.Consumed:
                    return;

                case HudClickResult.AcceptTradeInvite:
                    if (_gameClient.PendingTradeInvite is { } acceptInvite)
                    {
                        _gameClient.SendTradeInviteResponse(acceptInvite.RequesterAccountId, accepted: true);
                    }

                    return;

                case HudClickResult.DeclineTradeInvite:
                    if (_gameClient.PendingTradeInvite is { } declineInvite)
                    {
                        _gameClient.SendTradeInviteResponse(declineInvite.RequesterAccountId, accepted: false);
                    }

                    return;

                case HudClickResult.WrenchPopupTrade:
                    if (!string.IsNullOrWhiteSpace(_wrenchPopupTargetName))
                    {
                        _gameClient.SendTradeRequest(_wrenchPopupTargetName);
                    }

                    ClearWrenchPlayerPopup();
                    return;

                case HudClickResult.WrenchPopupGiveAccess:
                    if (!string.IsNullOrWhiteSpace(_wrenchPopupTargetName))
                    {
                        _gameClient.SendWorldLockConfigRequest(WorldLockConfigAction.AddAccess, _wrenchPopupTargetName);
                    }

                    ClearWrenchPlayerPopup();
                    return;

                case HudClickResult.TradeToggleAccept:
                    _gameClient.SendTradeAction(TradeActionKind.ToggleAccept);
                    return;

                case HudClickResult.TradeFinalConfirm:
                    _gameClient.SendTradeAction(TradeActionKind.FinalConfirm);
                    return;

                case HudClickResult.TradeCancel:
                    _gameClient.SendTradeAction(TradeActionKind.Cancel);
                    return;

                case HudClickResult.TradeClickLocalSlot:
                    HandleTradeLocalSlotClick(_hud.ClickedTradeSlotIndex, shiftHeld);
                    return;

                case HudClickResult.TradeAdjustQuickAmount:
                    HandleTradeQuickAdjust(_hud.ClickedTradeQuickAdjust);
                    return;

                case HudClickResult.TradeClickInventorySlot:
                    HandleTradeInventorySlotClick(_hud.ClickedTradeInventorySlotIndex, shiftHeld);
                    return;

                case HudClickResult.ToggleChatInput:
                    _isChatTyping = true;
                    _chatDraft = string.Empty;
                    _hud.ResetChatScroll();
                    return;

                case HudClickResult.OpenWarpModal:
                    _isChatTyping = false;
                    _warpDraft = string.Empty;
                    return;

                case HudClickResult.ToggleMenuModal:
                    _isChatTyping = false;
                    return;

                case HudClickResult.ToggleMuteSfx:
                    ToggleSfxMute();
                    return;

                case HudClickResult.DropSelectedItem:
                    TryDropSelectedItem();
                    return;

                case HudClickResult.ToggleLockPublic:
                    _gameClient.SendWorldLockConfigRequest(WorldLockConfigAction.TogglePublic);
                    return;

                case HudClickResult.AddLockAccess:
                    if (!string.IsNullOrWhiteSpace(_lockAccessDraft))
                    {
                        _gameClient.SendWorldLockConfigRequest(WorldLockConfigAction.AddAccess, _lockAccessDraft.Trim());
                        _lockAccessDraft = string.Empty;
                    }

                    return;

                case HudClickResult.RemoveLockAccess:
                    if (!string.IsNullOrWhiteSpace(quickWorld))
                    {
                        _gameClient.SendWorldLockConfigRequest(WorldLockConfigAction.RemoveAccess, quickWorld);
                    }

                    return;

                case HudClickResult.PickUpWorldLock:
                    _gameClient.SendWorldLockConfigRequest(WorldLockConfigAction.PickUpLock);
                    _hud.IsLockModalOpen = false;
                    _lockAccessDraft = string.Empty;
                    return;

                case HudClickResult.WarpToWorld:
                    string target = !string.IsNullOrWhiteSpace(quickWorld) ? quickWorld : _warpDraft.Trim();
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        _gameClient.SendJoinWorldRequest(target);
                        _hud.IsWarpModalOpen = false;
                        _warpDraft = string.Empty;
                    }

                    return;

                case HudClickResult.Respawn:
                    _playerController.SpawnAtTile(_gameClient.SpawnTileX, _gameClient.SpawnTileY);
                    return;

                case HudClickResult.Logout:
                    PerformLogout("Berhasil logout. Silakan login kembali.");
                    return;

                case HudClickResult.ExitGame:
                    Exit();
                    return;
            }

            if (result == HudClickResult.None && _wrenchPopupTargetPlayerId >= 0)
            {
                ClearWrenchPlayerPopup();
            }
        }

        if (_camera is null || !_gameClient.HasWorldSnapshot || !_playerController.IsSpawned)
        {
            _hoveredTileX = -1;
            _hoveredTileY = -1;
            _hoveredTileInReach = false;
            return;
        }

        bool cursorInsideWindow = GraphicsDevice.Viewport.Bounds.Contains(mouse.Position);
        bool overHud = _hud?.IsPointerOverHud(mouse.Position) ?? false;
        if (!cursorInsideWindow || overHud)
        {
            _hoveredTileX = -1;
            _hoveredTileY = -1;
            _hoveredTileInReach = false;
            return;
        }

        (_hoveredTileX, _hoveredTileY) = _camera.ScreenToTile(mouse.Position.ToVector2());
        _hoveredTileInReach = TileCollision.IsWithinReach(
            _playerController.PositionX,
            _playerController.PositionY,
            _hoveredTileX,
            _hoveredTileY);

        if (!IsActive || _isChatTyping || (_hud?.HasOpenModal ?? false) || !_gameClient.IsConnected || !Tile.IsInBounds(_hoveredTileX, _hoveredTileY))
        {
            return;
        }

        ushort selectedItemId = TileIds.Air;
        ActiveToolKind activeTool = _hud is not null
            ? _hud.GetActiveTool(_gameClient.Inventory, out selectedItemId)
            : ActiveToolKind.Fist;

        if (leftClicked)
        {
            if (activeTool == ActiveToolKind.Wrench)
            {
                // Cek klik karakter pemain lain terlebih dahulu (dalam jangkauan ~4 tile) untuk popup [ TRADE ] / [ GIVE ACCESS ].
                if (TryFindRemotePlayerAtScreenPoint(mouse.Position, out RemotePlayerState? hitPlayer, out bool withinReach) &&
                    hitPlayer is not null)
                {
                    _sfx?.PlayUiClick();
                    if (withinReach)
                    {
                        _wrenchPopupTargetPlayerId = hitPlayer.PlayerId;
                        _wrenchPopupTargetName = hitPlayer.Name;
                    }
                    else
                    {
                        ClearWrenchPlayerPopup();
                        _hud?.ShowToast($"Dekati {hitPlayer.Name} (maks 4 tile) untuk Wrench!", ToastKind.Warning);
                    }

                    return;
                }

                if (_hoveredTileInReach)
                {
                    _sfx?.PlayUiClick();
                    Tile hoveredTile = _gameClient.Tiles[Tile.GetIndex(_hoveredTileX, _hoveredTileY)];
                    if (hoveredTile.ForegroundId == TileIds.WorldLock || hoveredTile.ForegroundId == TileIds.Tree)
                    {
                        _gameClient.SendWrenchTileRequest(_hoveredTileX, _hoveredTileY);
                    }
                    else
                    {
                        InspectTileWithWrench(_hoveredTileX, _hoveredTileY);
                    }
                }
            }
            else if (activeTool == ActiveToolKind.Item &&
                     ItemCatalog.IsSeed(selectedItemId) &&
                     _hoveredTileInReach &&
                     _gameClient.Tiles[Tile.GetIndex(_hoveredTileX, _hoveredTileY)].ForegroundId == TileIds.Air)
            {
                _playerController.TriggerPunch(_hoveredTileX);
                var plantAction = new TileActionPacket(
                    Action: TileActionKind.PlaceForeground,
                    TileX: _hoveredTileX,
                    TileY: _hoveredTileY,
                    ItemId: selectedItemId);
                _gameClient.SendTileAction(in plantAction);
            }
            else
            {
                _playerController.TriggerPunch(_hoveredTileX);
                if (_hoveredTileInReach)
                {
                    _sfx?.PlayPunch();
                }

                var punch = new TileActionPacket(
                    Action: TileActionKind.Punch,
                    TileX: _hoveredTileX,
                    TileY: _hoveredTileY,
                    ItemId: TileIds.Air);
                _gameClient.SendTileAction(in punch);
            }
        }

        if (rightClicked && _hud is not null)
        {
            ushort placeItemId = _hud.GetRightClickPlaceableItemId(_gameClient.Inventory);
            if (ItemCatalog.IsPlaceable(placeItemId))
            {
                _playerController.TriggerPunch(_hoveredTileX);
                TileActionKind actionKind = ItemCatalog.IsBackgroundItem(placeItemId)
                    ? TileActionKind.PlaceBackground
                    : TileActionKind.PlaceForeground;

                var placeAction = new TileActionPacket(
                    Action: actionKind,
                    TileX: _hoveredTileX,
                    TileY: _hoveredTileY,
                    ItemId: placeItemId);
                _gameClient.SendTileAction(in placeAction);
            }
        }
    }

    private void PerformLogout(string statusMessage)
    {
        _gameClient.SendLogoutRequest();
        ReturnToAuthMenu(statusMessage, isError: false);
    }

    private void ReturnToAuthMenu(string statusMessage, bool isError)
    {
        _authBusy = false;
        _authPassword = string.Empty;
        _authConfirmPassword = string.Empty;
        _authFocusedField = AuthInputField.Username;
        _authStatusMessage = statusMessage;
        _authStatusIsError = isError;

        _playerController.Reset();
        _tileFeedback.Clear();
        _particles.Clear();
        _floatingTexts.Clear();
        _hud?.CloseAllModals();
        ClearWrenchPlayerPopup();
        _isChatTyping = false;
        _chatDraft = string.Empty;
        _warpDraft = string.Empty;
        _lockAccessDraft = string.Empty;
        _hoveredTileX = -1;
        _hoveredTileY = -1;
        _hoveredTileInReach = false;

        _screenState = ClientScreenState.AuthMenu;
    }

    private void InspectTileWithWrench(int tx, int ty)
    {
        Tile tile = _gameClient.Tiles[Tile.GetIndex(tx, ty)];
        string fgName = ItemCatalog.GetName(tile.ForegroundId);
        string bgName = ItemCatalog.GetName(tile.BackgroundId);

        string details;
        if (tile.ForegroundId == TileIds.Tree && _gameClient.Trees.TryGetValue(Tile.GetIndex(tx, ty), out TreeData tree))
        {
            string treeName = ItemCatalog.TryGetSeedProperties(tree.SeedId, out SeedProperties sp) ? sp.TreeName : "Tree";
            long nowTicks = DateTime.UtcNow.Ticks;
            string status = tree.IsReady(nowTicks)
                ? $"READY! Punch to harvest ({tree.FruitCount} fruits)"
                : $"Growing ({Math.Max(1, (int)Math.Ceiling(tree.GetRemainingSeconds(nowTicks)))}s left)";
            details = $"Tile ({tx},{ty}): {treeName} - {status}";
        }
        else if (tile.ForegroundId == TileIds.WorldLock)
        {
            string owner = _gameClient.IsWorldLocked ? _gameClient.WorldLockOwner : "Unclaimed";
            details = $"Tile ({tx},{ty}): World Lock - Owned by {owner}";
        }
        else if (tile.ForegroundId == TileIds.WhiteDoor || tile.ForegroundId == TileIds.Spawn)
        {
            details = $"Tile ({tx},{ty}): White Door (Main Door) of {_gameClient.CurrentWorldName}";
        }
        else
        {
            details = $"Tile ({tx},{ty}): FG={fgName}, BG={bgName}, Damage={tile.Damage}";
        }

        _gameClient.AddLocalSystemMessage(details);
        if (_hud is not null)
        {
            _hud.IsChatVisible = true;
            _hud.ResetChatScroll();
        }
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(TileRenderer.SkyFallbackColor);

        if (_spriteBatch is null || _tileRenderer is null || _camera is null || _menuCamera is null || _atlas is null || _hud is null || _authScreen is null)
        {
            base.Draw(gameTime);
            return;
        }

        if (_screenState == ClientScreenState.AuthMenu)
        {
            // Latar belakang langit gradasi, awan bergerak, dan bukit parallax hidup di balik layar Login/Register.
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            _tileRenderer.DrawPass1SkyAndParallax(_spriteBatch, GraphicsDevice.Viewport, _menuCamera, _elapsedTime);

            var formState = new AuthFormViewState(
                ActiveTab: _authTab,
                FocusedField: _authFocusedField,
                Username: _authUsername,
                Password: _authPassword,
                ConfirmPassword: _authConfirmPassword,
                ServerAddress: _authServerAddress,
                ShowPassword: _authShowPassword,
                IsBusy: _authBusy,
                StatusMessage: _authStatusMessage,
                IsErrorStatus: _authStatusIsError);

            _authScreen.DrawAuthMenu(_spriteBatch, GraphicsDevice.Viewport, in formState, _elapsedTime);
            _spriteBatch.End();

            base.Draw(gameTime);
            return;
        }

        if (_screenState == ClientScreenState.EnteringWorld)
        {
            _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            _tileRenderer.DrawPass1SkyAndParallax(_spriteBatch, GraphicsDevice.Viewport, _menuCamera, _elapsedTime);
            _authScreen.DrawEnteringWorld(_spriteBatch, GraphicsDevice.Viewport, _enteringWorldName, _gameClient.LocalPlayerName, _elapsedTime);
            _spriteBatch.End();

            base.Draw(gameTime);
            return;
        }

        // === ClientScreenState.InGame ===
        // Pass 1: Langit gradasi, matahari (0.05x), awan (0.15x + drift), & bukit paralaks (0.25x / 0.45x)
        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        _tileRenderer.DrawPass1SkyAndParallax(_spriteBatch, GraphicsDevice.Viewport, _camera, _elapsedTime);
        _spriteBatch.End();

        // Pass 2 (Background), Pass 3 (Drop Shadow 2.5D), & Pass 4 (Foreground + Outline + Entities)
        _spriteBatch.Begin(
            sortMode: SpriteSortMode.Deferred,
            blendState: BlendState.AlphaBlend,
            samplerState: SamplerState.PointClamp,
            transformMatrix: _camera.GetViewMatrix());

        if (_gameClient.HasWorldSnapshot)
        {
            long utcNowTicks = DateTime.UtcNow.Ticks;
            _tileRenderer.DrawWorldPasses2To4(_spriteBatch, _gameClient.Tiles, _gameClient.Trees, _camera, _tileFeedback, _elapsedTime, utcNowTicks);

            if (Tile.IsInBounds(_hoveredTileX, _hoveredTileY))
            {
                ActiveToolKind activeTool = _hud.GetActiveTool(_gameClient.Inventory, out ushort selectedItemId);
                _tileRenderer.DrawSmartCursor(
                    _spriteBatch,
                    _gameClient.Tiles,
                    _hoveredTileX,
                    _hoveredTileY,
                    _hoveredTileInReach,
                    activeTool,
                    selectedItemId,
                    _playerController,
                    _gameClient,
                    _elapsedTime);
            }

            _tileRenderer.DrawDrops(_spriteBatch, _gameClient.Drops, _elapsedTime);
            _particles.Draw(_spriteBatch, _atlas.Pixel);

            _tileRenderer.DrawRemotePlayers(_spriteBatch, _gameClient.Tiles, _gameClient.RemotePlayers, _gameClient.WorldLockOwner, _elapsedTime);

            bool localIsOwner = _gameClient.IsWorldLocked &&
                                ((_gameClient.LockData.OwnerAccountId > 0 && _gameClient.LockData.OwnerAccountId == _gameClient.LocalAccountId) ||
                                 string.Equals(_gameClient.WorldLockOwner, _gameClient.LocalPlayerName, StringComparison.OrdinalIgnoreCase));

            _tileRenderer.DrawLocalPlayer(
                _spriteBatch,
                _gameClient.Tiles,
                _playerController,
                _gameClient.LocalPlayerName,
                localIsOwner,
                _gameClient.LocalSpeechBubble,
                _elapsedTime);

            if (Tile.IsInBounds(_hoveredTileX, _hoveredTileY))
            {
                _tileRenderer.DrawTreeTooltip(_spriteBatch, _gameClient.Trees, _hoveredTileX, _hoveredTileY, utcNowTicks);
            }

            if (_font is not null)
            {
                _floatingTexts.Draw(_spriteBatch, _font);
            }
        }

        _spriteBatch.End();

        // Pass 4b: Pencahayaan Obor lembut (RadialGlowTexture + BlendState.Additive + SamplerState.LinearClamp)
        if (_gameClient.HasWorldSnapshot)
        {
            _spriteBatch.Begin(
                sortMode: SpriteSortMode.Deferred,
                blendState: BlendState.Additive,
                samplerState: SamplerState.LinearClamp,
                transformMatrix: _camera.GetViewMatrix());
            _tileRenderer.DrawLightingPass(_spriteBatch, _elapsedTime);
            _spriteBatch.End();
        }

        // Pass 5 (Screen-space HUD + F3 Debug Overlay)
        int playerTileX = (int)MathF.Floor(_playerController.CenterPosition.X / WorldConstants.TileSize);
        int playerTileY = (int)MathF.Floor(_playerController.CenterPosition.Y / WorldConstants.TileSize);
        var debugMetrics = new DebugMetrics(
            Fps: _currentFps,
            WorldName: _gameClient.CurrentWorldName,
            PlayerCount: _gameClient.RemotePlayers.Count + (_playerController.IsSpawned ? 1 : 0),
            VisibleTiles: _tileRenderer.LastDrawnTileCount,
            TotalTiles: WorldConstants.TotalTiles,
            PingMs: _gameClient.PingMs,
            PlayerTileX: playerTileX,
            PlayerTileY: playerTileY);

        _spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        _hud.Draw(
            _spriteBatch,
            GraphicsDevice.Viewport,
            _gameClient,
            in debugMetrics,
            _isChatTyping,
            _chatDraft,
            _warpDraft,
            _lockAccessDraft,
            isSfxMuted: _sfx?.IsMuted ?? false,
            _elapsedTime,
            mousePosition: _lastMousePosition,
            wrenchPlayerPopup: BuildWrenchPlayerPopupView());
        _spriteBatch.End();

        base.Draw(gameTime);
    }

    private void OnAuthResponseReceived(AuthResponsePacket response)
    {
        _authBusy = false;

        if (response.Success && response.AccountId > 0)
        {
            // Hapus password dari memori segera setelah autentikasi berhasil.
            _authPassword = string.Empty;
            _authConfirmPassword = string.Empty;
            _authUsername = response.DisplayName;
            _config.LastUsername = response.DisplayName;
            _config.Save();

            _enteringWorldName = string.IsNullOrWhiteSpace(response.LastWorld)
                ? GameClient.DefaultWorldName
                : response.LastWorld;

            _screenState = ClientScreenState.EnteringWorld;
            _gameClient.SendJoinWorldRequest(_enteringWorldName);
        }
        else if (!response.Success)
        {
            SetAuthError(string.IsNullOrWhiteSpace(response.ErrorMessage) ? "Login/Register gagal." : response.ErrorMessage);
            _screenState = ClientScreenState.AuthMenu;
        }
    }

    private void OnDisconnectedFromServer(string reason)
    {
        string msg = _screenState is ClientScreenState.InGame or ClientScreenState.EnteringWorld
            ? "Koneksi ke server terputus"
            : "Koneksi ke server terputus. Pastikan server berjalan.";
        ReturnToAuthMenu(msg, isError: true);
    }

    private void OnTileChanged(int tileX, int tileY, Tile previous, Tile current)
    {
        var center = new Vector2((tileX + 0.5f) * WorldConstants.TileSize, (tileY + 0.5f) * WorldConstants.TileSize);

        if (previous.ForegroundId != TileIds.Air && current.ForegroundId == TileIds.Air)
        {
            _particles.SpawnBlockBreak(center, PixelAtlas.GetParticleColor(previous.ForegroundId));
            _sfx?.PlayBlockBreak();
        }
        else if (previous.BackgroundId != TileIds.Air && current.BackgroundId == TileIds.Air)
        {
            _particles.SpawnBlockBreak(center, PixelAtlas.GetParticleColor(previous.BackgroundId));
            _sfx?.PlayBlockBreak();
        }
        else if ((previous.ForegroundId == TileIds.Air && current.ForegroundId != TileIds.Air) ||
                 (previous.BackgroundId == TileIds.Air && current.BackgroundId != TileIds.Air))
        {
            _sfx?.PlayBlockPlace();
        }
        else if (current.Damage > previous.Damage)
        {
            _tileFeedback.TriggerHit(tileX, tileY);
            ushort hitId = current.ForegroundId != TileIds.Air ? current.ForegroundId : current.BackgroundId;
            _particles.SpawnBlockHit(center, PixelAtlas.GetParticleColor(hitId));
        }
    }

    private void OnDropPickedUp(WorldDropView drop, bool isLocalCollector)
    {
        bool isGem = drop.ItemId == TileIds.Gem;
        _particles.SpawnPickupSparkle(drop.Position, isGem);

        if (isLocalCollector)
        {
            if (isGem)
            {
                _sfx?.PlayGemPickup();
            }
            else
            {
                _sfx?.PlayItemPickup();
            }

            string label = isGem
                ? (drop.Count == 1 ? "+1 Gem" : $"+{drop.Count} Gems")
                : $"+{drop.Count} {ItemCatalog.GetName(drop.ItemId)}";
            Color color = isGem ? new Color(76, 246, 160) : Color.White;
            _floatingTexts.Spawn(drop.Position - new Vector2(0f, 10f), label, color);
        }
    }

    private void OnWorldSnapshotReceived(string worldName, int spawnTileX, int spawnTileY)
    {
        _tileFeedback.Clear();
        _particles.Clear();
        _floatingTexts.Clear();
        _playerController.SpawnAtTile(spawnTileX, spawnTileY);
        if (_camera is not null)
        {
            _camera.Position = _playerController.CenterPosition;
            _camera.ClampPosition();
        }

        if (_hud is not null)
        {
            _hud.IsLockModalOpen = false;
            _hud.IsTradeModalOpen = false;
            _lockAccessDraft = string.Empty;
            _hud.ShowWorldBanner(worldName, _gameClient.LockData);
        }

        ClearWrenchPlayerPopup();

        _screenState = ClientScreenState.InGame;
        _sfx?.PlayWorldWarp();
    }

    private void TryDropSelectedItem()
    {
        if (_hud is null || !_gameClient.IsConnected || !_playerController.IsSpawned)
        {
            return;
        }

        ActiveToolKind activeTool = _hud.GetActiveTool(_gameClient.Inventory, out ushort itemId);
        if (activeTool != ActiveToolKind.Item || !ItemCatalog.IsDroppable(itemId))
        {
            return;
        }

        _gameClient.SendDropItemRequest(itemId, count: 1);
        _sfx?.PlayBlockPlace();
    }

    private void ToggleSfxMute()
    {
        if (_sfx is null)
        {
            return;
        }

        bool muted = _sfx.ToggleMute();
        if (!muted)
        {
            _sfx.PlayUiClick();
        }

        _gameClient.AddLocalSystemMessage(muted ? "SFX Audio muted (M)." : "SFX Audio unmuted (M).");
    }

    private void OnWorldLockStateChanged(WorldLockData lockData, bool openConfigModal)
    {
        if (_hud is null)
        {
            return;
        }

        _hud.UpdateWorldBannerOwner(lockData);

        if (!lockData.IsLocked)
        {
            _hud.IsLockModalOpen = false;
            _lockAccessDraft = string.Empty;
            return;
        }

        if (openConfigModal)
        {
            _hud.IsLockModalOpen = true;
            _hud.IsWarpModalOpen = false;
            _hud.IsMenuModalOpen = false;
            _isChatTyping = false;
            _lockAccessDraft = string.Empty;
            _sfx?.PlayUiClick();
        }
    }

    private void OnSystemToastReceived(string message, ToastKind kind)
    {
        _hud?.ShowToast(message, kind);
    }

    private void OnTradeInviteReceived(TradeInvitePacket packet)
    {
        _sfx?.PlayTradeInvite();
    }

    private void OnTradeStateSynced(TradeStateSyncPacket packet)
    {
        ClearWrenchPlayerPopup();
        _isChatTyping = false;

        if (_hud is not null)
        {
            bool wasOpen = _hud.IsTradeModalOpen;
            _hud.IsWarpModalOpen = false;
            _hud.IsMenuModalOpen = false;
            _hud.IsLockModalOpen = false;
            _hud.IsTradeModalOpen = true;
            if (!wasOpen)
            {
                _hud.FocusedTradeSlotIndex = 0;
                _sfx?.PlayUiClick();
            }
        }
    }

    private void OnTradeEndedReceived(TradeEndedPacket packet)
    {
        if (_hud is not null)
        {
            _hud.IsTradeModalOpen = false;
            _hud.FocusedTradeSlotIndex = 0;
        }

        if (packet.Completed)
        {
            _sfx?.PlayGemPickup();
            string message = string.IsNullOrWhiteSpace(packet.ReasonMessage)
                ? "Trade berhasil diselesaikan!"
                : packet.ReasonMessage;
            _gameClient.AddLocalSystemMessage(message, ChatMessageKind.System);
            if (_hud is not null)
            {
                _hud.IsChatVisible = true;
                _hud.ResetChatScroll();
            }
        }
        else
        {
            _sfx?.PlayUiClick();
            string reason = string.IsNullOrWhiteSpace(packet.ReasonMessage)
                ? "Trade dibatalkan."
                : packet.ReasonMessage;
            _hud?.ShowToast(reason, ToastKind.Warning);
            _gameClient.AddLocalSystemMessage(reason, ChatMessageKind.Error);
        }
    }

    private void ClearWrenchPlayerPopup()
    {
        _wrenchPopupTargetPlayerId = -1;
        _wrenchPopupTargetName = string.Empty;
    }

    private void ValidateWrenchPlayerPopup()
    {
        if (_wrenchPopupTargetPlayerId < 0)
        {
            return;
        }

        if (!_gameClient.RemotePlayers.TryGetValue(_wrenchPopupTargetPlayerId, out RemotePlayerState? target))
        {
            ClearWrenchPlayerPopup();
            return;
        }

        Vector2 targetCenter = target.RenderPosition + new Vector2(PhysicsConstants.PlayerWidth * 0.5f, PhysicsConstants.PlayerHeight * 0.5f);
        float distSq = Vector2.DistanceSquared(_playerController.CenterPosition, targetCenter);
        if (distSq > WrenchPopupAutoCloseReachPx * WrenchPopupAutoCloseReachPx)
        {
            ClearWrenchPlayerPopup();
        }
    }

    private WrenchPlayerPopupView? BuildWrenchPlayerPopupView()
    {
        if (_wrenchPopupTargetPlayerId < 0 ||
            _camera is null ||
            (_hud?.HasOpenModal ?? false) ||
            !_gameClient.RemotePlayers.TryGetValue(_wrenchPopupTargetPlayerId, out RemotePlayerState? target))
        {
            return null;
        }

        Vector2 worldAnchor = new(target.RenderPosition.X + PhysicsConstants.PlayerWidth * 0.5f, target.RenderPosition.Y - 10f);
        Vector2 screenAnchor = _camera.WorldToScreen(worldAnchor);
        return new WrenchPlayerPopupView(
            TargetPlayerId: target.PlayerId,
            TargetPlayerName: target.Name,
            ScreenAnchor: screenAnchor,
            CanGiveWorldLockAccess: _gameClient.IsLocalWorldLockOwner());
    }

    private bool TryFindRemotePlayerAtScreenPoint(Point screenPoint, out RemotePlayerState? hitPlayer, out bool withinReach)
    {
        hitPlayer = null;
        withinReach = false;
        if (_camera is null || _gameClient.RemotePlayers.Count == 0)
        {
            return false;
        }

        Vector2 worldPos = _camera.ScreenToWorld(screenPoint.ToVector2());
        float bestDistSq = float.MaxValue;

        foreach (RemotePlayerState remote in _gameClient.RemotePlayers.Values)
        {
            float left = remote.RenderPosition.X - 6f;
            float top = remote.RenderPosition.Y - 12f;
            float right = remote.RenderPosition.X + PhysicsConstants.PlayerWidth + 6f;
            float bottom = remote.RenderPosition.Y + PhysicsConstants.PlayerHeight + 4f;

            if (worldPos.X >= left && worldPos.X <= right && worldPos.Y >= top && worldPos.Y <= bottom)
            {
                Vector2 remoteCenter = remote.RenderPosition + new Vector2(PhysicsConstants.PlayerWidth * 0.5f, PhysicsConstants.PlayerHeight * 0.5f);
                float distSq = Vector2.DistanceSquared(_playerController.CenterPosition, remoteCenter);
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    hitPlayer = remote;
                    withinReach = distSq <= WrenchPlayerReachPx * WrenchPlayerReachPx;
                }
            }
        }

        return hitPlayer is not null;
    }

    private TradeOfferSlot[]? GetLocalTradeOffers()
    {
        ActiveTradeView? trade = _gameClient.ActiveTrade;
        if (trade is null)
        {
            return null;
        }

        bool localIsA = (trade.AccountIdA > 0 && trade.AccountIdA == _gameClient.LocalAccountId) ||
                        string.Equals(trade.NameA, _gameClient.LocalPlayerName, StringComparison.OrdinalIgnoreCase);
        return localIsA ? trade.OffersA : trade.OffersB;
    }

    private ushort GetOwnedInventoryCount(ushort itemId)
    {
        IReadOnlyList<InventorySlot> inv = _gameClient.Inventory;
        for (int i = 0; i < inv.Count; i++)
        {
            if (inv[i].ItemId == itemId)
            {
                return inv[i].Count;
            }
        }

        return 0;
    }

    private void ClearTradeOfferSlot(byte slotIndex)
    {
        TradeOfferSlot[]? offers = GetLocalTradeOffers();
        if (offers is null || slotIndex >= offers.Length || offers[slotIndex].IsEmpty)
        {
            return;
        }

        _gameClient.SendTradeUpdateOffer(slotIndex, offers[slotIndex].ItemId, 0);
    }

    private void HandleTradeLocalSlotClick(byte slotIndex, bool shiftHeld)
    {
        TradeOfferSlot[]? offers = GetLocalTradeOffers();
        if (offers is null || slotIndex >= offers.Length || offers[slotIndex].IsEmpty)
        {
            return;
        }

        TradeOfferSlot slot = offers[slotIndex];
        ushort nextCount = shiftHeld
            ? (ushort)0
            : (ushort)Math.Max(0, slot.Count - 1);
        _gameClient.SendTradeUpdateOffer(slotIndex, slot.ItemId, nextCount);
    }

    private void HandleTradeInventorySlotClick(int hudSlotIndex, bool shiftHeld)
    {
        TradeOfferSlot[]? offers = GetLocalTradeOffers();
        if (_hud is null || offers is null)
        {
            return;
        }

        int invIndex = hudSlotIndex - 2;
        if (invIndex < 0 || invIndex >= _gameClient.Inventory.Count)
        {
            return;
        }

        InventorySlot invSlot = _gameClient.Inventory[invIndex];
        if (invSlot.Count == 0 || !ItemCatalog.IsTradeable(invSlot.ItemId))
        {
            _hud.ShowToast("Item ini tidak dapat diperdagangkan!", ToastKind.Warning);
            return;
        }

        int delta = shiftHeld ? 10 : 1;

        // Jika ItemId sudah ada di salah satu slot penawaran, tambahkan langsung pada slot tersebut.
        for (int i = 0; i < offers.Length; i++)
        {
            if (!offers[i].IsEmpty && offers[i].ItemId == invSlot.ItemId)
            {
                _hud.FocusedTradeSlotIndex = i;
                ushort updated = (ushort)Math.Min(invSlot.Count, offers[i].Count + delta);
                if (updated != offers[i].Count)
                {
                    _gameClient.SendTradeUpdateOffer((byte)i, invSlot.ItemId, updated);
                }

                return;
            }
        }

        // Cari slot kosong (prioritaskan slot terfokus jika kosong, lalu slot kosong pertama).
        int targetSlot = -1;
        int focused = Math.Clamp(_hud.FocusedTradeSlotIndex, 0, offers.Length - 1);
        if (offers[focused].IsEmpty)
        {
            targetSlot = focused;
        }
        else
        {
            for (int i = 0; i < offers.Length; i++)
            {
                if (offers[i].IsEmpty)
                {
                    targetSlot = i;
                    break;
                }
            }
        }

        if (targetSlot < 0)
        {
            _hud.ShowToast("Semua 4 slot penawaran sudah terisi!", ToastKind.Warning);
            return;
        }

        _hud.FocusedTradeSlotIndex = targetSlot;
        ushort initialCount = (ushort)Math.Min(invSlot.Count, delta);
        _gameClient.SendTradeUpdateOffer((byte)targetSlot, invSlot.ItemId, initialCount);
    }

    private void HandleTradeQuickAdjust(TradeQuickAdjustKind kind)
    {
        TradeOfferSlot[]? offers = GetLocalTradeOffers();
        if (_hud is null || offers is null || kind == TradeQuickAdjustKind.None)
        {
            return;
        }

        int focused = Math.Clamp(_hud.FocusedTradeSlotIndex, 0, offers.Length - 1);
        TradeOfferSlot currentSlot = offers[focused];

        if (!currentSlot.IsEmpty)
        {
            ushort owned = GetOwnedInventoryCount(currentSlot.ItemId);
            ushort nextCount = kind switch
            {
                TradeQuickAdjustKind.MinusOne => (ushort)Math.Max(0, currentSlot.Count - 1),
                TradeQuickAdjustKind.PlusOne => (ushort)Math.Min(owned, currentSlot.Count + 1),
                TradeQuickAdjustKind.PlusTen => (ushort)Math.Min(owned, currentSlot.Count + 10),
                TradeQuickAdjustKind.Max => owned,
                TradeQuickAdjustKind.Clear => 0,
                _ => currentSlot.Count,
            };

            if (nextCount != currentSlot.Count)
            {
                _gameClient.SendTradeUpdateOffer((byte)focused, currentSlot.ItemId, nextCount);
            }

            return;
        }

        // Jika slot fokus kosong, gunakan item tradeable yang sedang dipilih di inventory bawah.
        if (kind is TradeQuickAdjustKind.MinusOne or TradeQuickAdjustKind.Clear)
        {
            return;
        }

        ActiveToolKind activeTool = _hud.GetActiveTool(_gameClient.Inventory, out ushort selectedId);
        if (activeTool != ActiveToolKind.Item || !ItemCatalog.IsTradeable(selectedId))
        {
            return;
        }

        ushort ownedSelected = GetOwnedInventoryCount(selectedId);
        if (ownedSelected == 0)
        {
            return;
        }

        int targetSlot = focused;
        ushort existingCount = 0;
        for (int i = 0; i < offers.Length; i++)
        {
            if (!offers[i].IsEmpty && offers[i].ItemId == selectedId)
            {
                targetSlot = i;
                existingCount = offers[i].Count;
                _hud.FocusedTradeSlotIndex = i;
                break;
            }
        }

        ushort targetCount = kind switch
        {
            TradeQuickAdjustKind.PlusOne => (ushort)Math.Min(ownedSelected, existingCount + 1),
            TradeQuickAdjustKind.PlusTen => (ushort)Math.Min(ownedSelected, existingCount + 10),
            TradeQuickAdjustKind.Max => ownedSelected,
            _ => existingCount,
        };

        if (targetCount != existingCount)
        {
            _gameClient.SendTradeUpdateOffer((byte)targetSlot, selectedId, targetCount);
        }
    }

    private void OnLocalPlayerStateCorrected(PlayerStatePacket authoritativeState)
    {
        _playerController.ApplyServerCorrection(authoritativeState);
    }

    private void OnClientSizeChanged(object? sender, EventArgs e)
    {
        _camera?.UpdateViewport(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
        _menuCamera?.UpdateViewport(GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
    }

    private bool IsKeyJustPressed(KeyboardState current, Keys key) =>
        current.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Window.ClientSizeChanged -= OnClientSizeChanged;
            Window.TextInput -= OnTextInput;
            _gameClient.AuthResponseReceived -= OnAuthResponseReceived;
            _gameClient.DisconnectedFromServer -= OnDisconnectedFromServer;
            _gameClient.WorldSnapshotReceived -= OnWorldSnapshotReceived;
            _gameClient.LocalPlayerStateCorrected -= OnLocalPlayerStateCorrected;
            _gameClient.TileChanged -= OnTileChanged;
            _gameClient.DropPickedUp -= OnDropPickedUp;
            _gameClient.WorldLockStateChanged -= OnWorldLockStateChanged;
            _gameClient.SystemToastReceived -= OnSystemToastReceived;
            _gameClient.TradeInviteReceived -= OnTradeInviteReceived;
            _gameClient.TradeStateSynced -= OnTradeStateSynced;
            _gameClient.TradeEndedReceived -= OnTradeEndedReceived;
            _gameClient.Dispose();
            _sfx?.Dispose();
            _atlas?.Dispose();
            _font?.Dispose();
            _spriteBatch?.Dispose();
        }

        base.Dispose(disposing);
    }
}
