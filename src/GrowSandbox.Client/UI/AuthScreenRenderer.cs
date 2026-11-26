using GrowSandbox.Client.Graphics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.UI;

public enum ClientScreenState : byte
{
    AuthMenu = 0,
    EnteringWorld = 1,
    InGame = 2,
}

public enum AuthTabKind : byte
{
    Login = 0,
    Register = 1,
}

public enum AuthInputField : byte
{
    Username = 0,
    Password = 1,
    ConfirmPassword = 2,
    ServerAddress = 3,
}

public enum AuthClickAction : byte
{
    None = 0,
    SwitchToLogin,
    SwitchToRegister,
    FocusField,
    ToggleShowPassword,
    Submit,
}

public readonly record struct AuthFormViewState(
    AuthTabKind ActiveTab,
    AuthInputField FocusedField,
    string Username,
    string Password,
    string ConfirmPassword,
    string ServerAddress,
    bool ShowPassword,
    bool IsBusy,
    string StatusMessage,
    bool IsErrorStatus);

/// <summary>
/// Renderer layar autentikasi (Login / Register) dan transisi EnteringWorld dengan panel 9-slice SDF
/// anti-aliased (<see cref="UiNineSlice"/>) dan tipografi TrueType tajam (<see cref="UiFontManager"/>).
/// Dirender dengan <see cref="SamplerState.LinearClamp"/>.
/// </summary>
public sealed class AuthScreenRenderer
{
    private readonly PixelAtlas _atlas;
    private readonly UiNineSlice _nineSlice;
    private readonly UiFontManager _uiFonts;

    private Rectangle _loginTabRect;
    private Rectangle _registerTabRect;
    private Rectangle _usernameRect;
    private Rectangle _passwordRect;
    private Rectangle _toggleEyeRect;
    private Rectangle _confirmPasswordRect;
    private Rectangle _serverRect;
    private Rectangle _submitBtnRect;

    public AuthScreenRenderer(PixelAtlas atlas, UiNineSlice nineSlice, UiFontManager uiFonts)
    {
        _atlas = atlas;
        _nineSlice = nineSlice;
        _uiFonts = uiFonts;
    }

    public AuthClickAction HandleLeftClick(Point mousePosition, AuthTabKind activeTab, out AuthInputField focusedField)
    {
        focusedField = AuthInputField.Username;

        if (_loginTabRect.Contains(mousePosition))
        {
            return AuthClickAction.SwitchToLogin;
        }

        if (_registerTabRect.Contains(mousePosition))
        {
            return AuthClickAction.SwitchToRegister;
        }

        if (_toggleEyeRect.Contains(mousePosition))
        {
            return AuthClickAction.ToggleShowPassword;
        }

        if (_usernameRect.Contains(mousePosition))
        {
            focusedField = AuthInputField.Username;
            return AuthClickAction.FocusField;
        }

        if (_passwordRect.Contains(mousePosition))
        {
            focusedField = AuthInputField.Password;
            return AuthClickAction.FocusField;
        }

        if (activeTab == AuthTabKind.Register && _confirmPasswordRect.Contains(mousePosition))
        {
            focusedField = AuthInputField.ConfirmPassword;
            return AuthClickAction.FocusField;
        }

        if (_serverRect.Contains(mousePosition))
        {
            focusedField = AuthInputField.ServerAddress;
            return AuthClickAction.FocusField;
        }

        if (_submitBtnRect.Contains(mousePosition))
        {
            return AuthClickAction.Submit;
        }

        return AuthClickAction.None;
    }

    public void DrawAuthMenu(
        SpriteBatch spriteBatch,
        Viewport viewport,
        in AuthFormViewState state,
        float elapsedTime,
        Point mousePosition = default,
        bool isLeftMouseDown = false)
    {
        bool isRegister = state.ActiveTab == AuthTabKind.Register;
        const int cardW = 468;
        int cardH = isRegister ? 448 : 380;
        const int titleBlockH = 74;
        int totalH = titleBlockH + cardH + 28;

        int startY = Math.Max(18, (viewport.Height - totalH) / 2);
        int cardX = (viewport.Width - cardW) / 2;
        int cardY = startY + titleBlockH;

        // 1. Judul besar GROWSANDBOX (UiFontSize.Header 28px) dengan outline tajam & warna emas
        const string title = "GROWSANDBOX";
        Vector2 titleSize = _uiFonts.MeasureString(title, UiFontSize.Header);
        float titleX = (viewport.Width - titleSize.X) * 0.5f;
        float titleY = startY;

        _uiFonts.DrawStringOutline(
            spriteBatch,
            title,
            new Vector2(titleX, titleY),
            new Color(255, 240, 128),
            UiFontSize.Header,
            outlineColor: new Color(20, 48, 82));

        const string subtitle = "2D MULTIPLAYER SANDBOX";
        Vector2 subSize = _uiFonts.MeasureString(subtitle, UiFontSize.Small);
        _uiFonts.DrawString(
            spriteBatch,
            subtitle,
            new Vector2((viewport.Width - subSize.X) * 0.5f, titleY + titleSize.Y + 4f),
            new Color(238, 250, 255),
            UiFontSize.Small,
            dropShadow: true);

        // 2. Kartu panel utama 9-slice glossy biru
        var cardRect = new Rectangle(cardX, cardY, cardW, cardH);
        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelBlueOpaque, cardRect);

        const int pad = 24;
        int innerX = cardX + pad;
        int innerW = cardW - pad * 2;
        int cursorY = cardY + 20;

        // 3. Dua tab switch di atas form: [ LOGIN ] dan [ REGISTER ]
        const int tabGap = 10;
        int tabW = (innerW - tabGap) / 2;
        const int tabH = 38;

        _loginTabRect = new Rectangle(innerX, cursorY, tabW, tabH);
        _registerTabRect = new Rectangle(innerX + tabW + tabGap, cursorY, tabW, tabH);

        DrawTabButton(spriteBatch, _loginTabRect, "LOGIN", active: !isRegister, mousePosition, isLeftMouseDown);
        DrawTabButton(spriteBatch, _registerTabRect, "REGISTER", active: isRegister, mousePosition, isLeftMouseDown);
        cursorY += tabH + 16;

        bool blinkCursor = ((int)(elapsedTime * 2.4f)) % 2 == 0;

        // 4. Field Username
        DrawFieldLabel(spriteBatch, "USERNAME", innerX, cursorY);
        cursorY += 18;
        _usernameRect = new Rectangle(innerX, cursorY, innerW, 36);
        DrawTextInputBox(
            spriteBatch,
            _usernameRect,
            state.Username,
            placeholder: "3-16 chars (letters, numbers, _)",
            focused: state.FocusedField == AuthInputField.Username,
            masked: false,
            blinkCursor: blinkCursor,
            rightReservePx: 0);
        cursorY += 46;

        // 5. Field Password (tersamarkan + tombol toggle mata)
        DrawFieldLabel(spriteBatch, "PASSWORD", innerX, cursorY);
        cursorY += 18;
        _passwordRect = new Rectangle(innerX, cursorY, innerW, 36);
        _toggleEyeRect = new Rectangle(_passwordRect.Right - 46, _passwordRect.Y + 4, 40, 26);
        DrawTextInputBox(
            spriteBatch,
            _passwordRect,
            state.Password,
            placeholder: "Minimum 6 characters",
            focused: state.FocusedField == AuthInputField.Password,
            masked: !state.ShowPassword,
            blinkCursor: blinkCursor,
            rightReservePx: 50);
        DrawEyeToggleButton(spriteBatch, _toggleEyeRect, state.ShowPassword, mousePosition, isLeftMouseDown);
        cursorY += 46;

        // 6. Field Confirm Password (khusus tab REGISTER)
        if (isRegister)
        {
            DrawFieldLabel(spriteBatch, "CONFIRM PASSWORD", innerX, cursorY);
            cursorY += 18;
            _confirmPasswordRect = new Rectangle(innerX, cursorY, innerW, 36);
            DrawTextInputBox(
                spriteBatch,
                _confirmPasswordRect,
                state.ConfirmPassword,
                placeholder: "Re-enter password",
                focused: state.FocusedField == AuthInputField.ConfirmPassword,
                masked: !state.ShowPassword,
                blinkCursor: blinkCursor,
                rightReservePx: 0);
            cursorY += 46;
        }
        else
        {
            _confirmPasswordRect = Rectangle.Empty;
        }

        // 7. Field Server (IP:PORT)
        DrawFieldLabel(spriteBatch, "SERVER (IP:PORT)", innerX, cursorY);
        cursorY += 18;
        _serverRect = new Rectangle(innerX, cursorY, innerW, 34);
        DrawTextInputBox(
            spriteBatch,
            _serverRect,
            state.ServerAddress,
            placeholder: "127.0.0.1:9050",
            focused: state.FocusedField == AuthInputField.ServerAddress,
            masked: false,
            blinkCursor: blinkCursor,
            rightReservePx: 0);
        cursorY += 46;

        // 8. Tombol utama hijau glossy (LOGIN / CREATE ACCOUNT)
        _submitBtnRect = new Rectangle(innerX, cursorY, innerW, 42);
        UiButtonState submitState = UiNineSlice.EvaluateButtonState(_submitBtnRect, mousePosition, isLeftMouseDown, enabled: !state.IsBusy);
        Rectangle submitRendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnGreen, _submitBtnRect, submitState);

        string btnLabel = state.IsBusy
            ? "CONNECTING..."
            : (isRegister ? "CREATE ACCOUNT" : "LOGIN");
        _uiFonts.DrawCentered(
            spriteBatch,
            btnLabel,
            new Rectangle(submitRendered.X, submitRendered.Y, submitRendered.Width, submitRendered.Height - 2),
            state.IsBusy ? HudRenderer.DisabledTextColor : Color.White,
            UiFontSize.Title,
            dropShadow: true);
        cursorY += 50;

        // 9. Area pesan status / error
        if (!string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            Color msgColor = state.IsErrorStatus ? HudRenderer.ChatErrorColor : HudRenderer.ChatSystemColor;
            List<string> wrapped = _uiFonts.WrapText(state.StatusMessage, innerW, UiFontSize.Small);
            int lineH = _uiFonts.GetLineHeight(UiFontSize.Small) + 1;
            int maxLines = Math.Min(2, wrapped.Count);
            for (int i = 0; i < maxLines; i++)
            {
                Vector2 lineSize = _uiFonts.MeasureString(wrapped[i], UiFontSize.Small);
                _uiFonts.DrawString(
                    spriteBatch,
                    wrapped[i],
                    new Vector2(cardX + (cardW - lineSize.X) * 0.5f, cursorY + i * lineH),
                    msgColor,
                    UiFontSize.Small,
                    dropShadow: true);
            }
        }

        // Petunjuk pintasan di bawah kartu
        const string footerHint = "TAB: Next Field   •   ENTER: Submit   •   ESC: Exit";
        Vector2 hintSize = _uiFonts.MeasureString(footerHint, UiFontSize.Small);
        _uiFonts.DrawString(
            spriteBatch,
            footerHint,
            new Vector2((viewport.Width - hintSize.X) * 0.5f, cardRect.Bottom + 10),
            new Color(226, 244, 255),
            UiFontSize.Small,
            dropShadow: true);
    }

    public void DrawEnteringWorld(
        SpriteBatch spriteBatch,
        Viewport viewport,
        string worldName,
        string displayName,
        float elapsedTime)
    {
        const int w = 448;
        const int h = 152;
        int x = (viewport.Width - w) / 2;
        int y = (viewport.Height - h) / 2;
        var rect = new Rectangle(x, y, w, h);

        _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.PanelBlueOpaque, rect);

        int dotCount = ((int)(elapsedTime * 3f) % 3) + 1;
        string dots = new string('.', dotCount);
        string title = $"ENTERING {(string.IsNullOrWhiteSpace(worldName) ? "START" : worldName.ToUpperInvariant())}{dots}";
        _uiFonts.DrawCentered(
            spriteBatch,
            title,
            new Rectangle(x + 16, y + 24, w - 32, 32),
            new Color(255, 240, 128),
            UiFontSize.Title,
            dropShadow: true);

        string greeting = string.IsNullOrWhiteSpace(displayName)
            ? "Authenticated!"
            : $"Welcome back, {displayName}!";
        _uiFonts.DrawCentered(
            spriteBatch,
            greeting,
            new Rectangle(x + 16, y + 66, w - 32, 26),
            Color.White,
            UiFontSize.Normal,
            dropShadow: true);

        _uiFonts.DrawCentered(
            spriteBatch,
            "Receiving world snapshot from server...",
            new Rectangle(x + 16, y + 98, w - 32, 24),
            HudRenderer.WorldStatusCyan,
            UiFontSize.Small,
            dropShadow: true);
    }

    private void DrawTabButton(
        SpriteBatch spriteBatch,
        Rectangle rect,
        string label,
        bool active,
        Point mousePosition,
        bool isLeftMouseDown)
    {
        UiButtonState state = UiNineSlice.EvaluateButtonState(rect, mousePosition, isLeftMouseDown);
        Rectangle rendered;
        if (active)
        {
            rendered = _nineSlice.DrawButton(spriteBatch, UiPanelStyle.BtnBlue, rect, state);
        }
        else
        {
            rendered = state == UiButtonState.Pressed
                ? new Rectangle(rect.X, rect.Y + 1, rect.Width, rect.Height)
                : rect;
            _nineSlice.DrawPanel(
                spriteBatch,
                state == UiButtonState.Hover ? UiPanelStyle.PanelBlue : UiPanelStyle.PanelDark,
                rendered);
        }

        Color textColor = active ? new Color(255, 244, 144) : new Color(180, 218, 244);
        _uiFonts.DrawCentered(
            spriteBatch,
            label,
            new Rectangle(rendered.X, rendered.Y, rendered.Width, rendered.Height - 2),
            textColor,
            UiFontSize.Normal,
            dropShadow: true);
    }

    private void DrawFieldLabel(SpriteBatch spriteBatch, string label, int x, int y)
    {
        _uiFonts.DrawString(spriteBatch, label, new Vector2(x + 2, y), HudRenderer.WorldStatusCyan, UiFontSize.Tiny, dropShadow: true);
    }

    private void DrawTextInputBox(
        SpriteBatch spriteBatch,
        Rectangle rect,
        string value,
        string placeholder,
        bool focused,
        bool masked,
        bool blinkCursor,
        int rightReservePx)
    {
        _nineSlice.DrawPanel(spriteBatch, focused ? UiPanelStyle.PanelDarkGold : UiPanelStyle.PanelDark, rect);

        int textX = rect.X + 12;
        float lineH = _uiFonts.MeasureString("Ag", UiFontSize.Normal).Y;
        float textY = rect.Y + (rect.Height - 3 - lineH) * 0.5f;
        float availW = MathF.Max(40f, rect.Width - 24f - rightReservePx);
        int caretY = (int)MathF.Floor(textY + 1f);
        int caretH = Math.Max(14, (int)MathF.Round(lineH - 2f));

        if (string.IsNullOrEmpty(value))
        {
            if (focused && blinkCursor)
            {
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(textX, caretY, 2, caretH), Color.White);
            }
            else if (!focused)
            {
                string ph = _uiFonts.FitToWidth(placeholder, availW, UiFontSize.Small);
                float phY = rect.Y + (rect.Height - 3 - _uiFonts.MeasureString(ph, UiFontSize.Small).Y) * 0.5f;
                _uiFonts.DrawString(spriteBatch, ph, new Vector2(textX, phY), new Color(138, 178, 208), UiFontSize.Small, dropShadow: false);
            }

            return;
        }

        if (masked)
        {
            const int dotSize = 6;
            const int dotStep = 10;
            int maxDots = Math.Max(4, (int)((availW - 8f) / dotStep));
            int shownDots = Math.Min(value.Length, maxDots);
            int dotY = rect.Y + (rect.Height - 3 - dotSize) / 2;

            for (int i = 0; i < shownDots; i++)
            {
                int dx = textX + i * dotStep;
                spriteBatch.Draw(_atlas.RadialGlowTexture, new Rectangle(dx - 1, dotY - 1, dotSize + 2, dotSize + 2), Color.FromNonPremultiplied(0, 0, 0, 140));
                spriteBatch.Draw(_atlas.RadialGlowTexture, new Rectangle(dx, dotY, dotSize, dotSize), Color.White);
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(dx + 1, dotY + 1, dotSize - 2, dotSize - 2), Color.White);
            }

            if (focused && blinkCursor)
            {
                int caretX = textX + shownDots * dotStep + 2;
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(caretX, caretY, 2, caretH), Color.White);
            }
        }
        else
        {
            string shown = _uiFonts.FitToWidth(value, availW - 6f, UiFontSize.Normal, keepTail: true);
            _uiFonts.DrawString(spriteBatch, shown, new Vector2(textX, textY), Color.White, UiFontSize.Normal, dropShadow: true);

            if (focused && blinkCursor)
            {
                int caretX = textX + (int)MathF.Ceiling(_uiFonts.MeasureString(shown, UiFontSize.Normal).X) + 1;
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(caretX, caretY, 2, caretH), Color.White);
            }
        }
    }

    private void DrawEyeToggleButton(
        SpriteBatch spriteBatch,
        Rectangle rect,
        bool showPassword,
        Point mousePosition,
        bool isLeftMouseDown)
    {
        UiButtonState state = UiNineSlice.EvaluateButtonState(rect, mousePosition, isLeftMouseDown);
        Rectangle rendered = _nineSlice.DrawButton(
            spriteBatch,
            showPassword ? UiPanelStyle.BtnGold : UiPanelStyle.BtnBlue,
            rect,
            state,
            cornerSize: 7);

        int cx = rendered.X + rendered.Width / 2;
        int cy = rendered.Y + (rendered.Height - 2) / 2;
        Color eyeCol = showPassword ? Color.White : new Color(216, 240, 255);

        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 5, cy - 3, 10, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 7, cy - 2, 2, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx + 5, cy - 2, 2, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 8, cy - 1, 2, 2), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx + 6, cy - 1, 2, 2), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 7, cy + 1, 2, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx + 5, cy + 1, 2, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 5, cy + 2, 10, 1), eyeCol);

        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 2, cy - 2, 4, 4), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 1, cy - 1, 2, 2), HudRenderer.GtPanelDark);

        if (!showPassword)
        {
            for (int d = -4; d <= 4; d++)
            {
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx + d, cy + d / 2, 2, 1), HudRenderer.ChatErrorColor);
            }
        }
    }
}
