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
/// Renderer layar autentikasi (Login / Register) dan transisi EnteringWorld
/// dengan palet warna Gt* yang konsisten dengan <see cref="HudRenderer"/>.
/// </summary>
public sealed class AuthScreenRenderer
{
    private readonly PixelAtlas _atlas;
    private readonly PixelFont _font;

    private Rectangle _loginTabRect;
    private Rectangle _registerTabRect;
    private Rectangle _usernameRect;
    private Rectangle _passwordRect;
    private Rectangle _toggleEyeRect;
    private Rectangle _confirmPasswordRect;
    private Rectangle _serverRect;
    private Rectangle _submitBtnRect;

    public AuthScreenRenderer(PixelAtlas atlas, PixelFont font)
    {
        _atlas = atlas;
        _font = font;
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

    public void DrawAuthMenu(SpriteBatch spriteBatch, Viewport viewport, in AuthFormViewState state, float elapsedTime)
    {
        bool isRegister = state.ActiveTab == AuthTabKind.Register;
        const int cardW = 446;
        int cardH = isRegister ? 396 : 338;
        const int titleBlockH = 66;
        int totalH = titleBlockH + cardH + 22;

        int startY = Math.Max(20, (viewport.Height - totalH) / 2);
        int cardX = (viewport.Width - cardW) / 2;
        int cardY = startY + titleBlockH;

        // 1. Judul besar GROWSANDBOX dengan outline tajam & drop shadow
        const string title = "GROWSANDBOX";
        Vector2 titleSize = _font.MeasureString(title, scale: 4);
        int titleX = (int)MathF.Floor((viewport.Width - titleSize.X) * 0.5f);
        int titleY = startY;

        // Drop shadow lembut + thin outline + warna emas cerah khas Growtopia
        _font.DrawString(spriteBatch, title, new Vector2(titleX + 2, titleY + 3), new Color(12, 30, 52, 210), scale: 4, dropShadow: false);
        _font.DrawStringThinOutline(
            spriteBatch,
            title,
            new Vector2(titleX, titleY),
            new Color(255, 240, 128),
            scale: 4,
            outlineColor: new Color(22, 52, 86));

        const string subtitle = "2D MULTIPLAYER SANDBOX";
        Vector2 subSize = _font.MeasureString(subtitle, scale: 1);
        _font.DrawString(
            spriteBatch,
            subtitle,
            new Vector2((viewport.Width - subSize.X) * 0.5f, titleY + (int)titleSize.Y + 8),
            Color.White,
            scale: 1,
            dropShadow: true);

        // 2. Kartu panel utama bergaya Growtopia
        var cardRect = new Rectangle(cardX, cardY, cardW, cardH);
        DrawGtPanel(
            spriteBatch,
            cardRect,
            new Color(HudRenderer.GtPanelFill.R, HudRenderer.GtPanelFill.G, HudRenderer.GtPanelFill.B, (byte)224),
            HudRenderer.GtBorderBright,
            topHighlightAlpha: 72,
            borderThickness: 2);

        const int pad = 22;
        int innerX = cardX + pad;
        int innerW = cardW - pad * 2;
        int cursorY = cardY + 18;

        // 3. Dua tab switch di atas form: [ LOGIN ] dan [ REGISTER ]
        int tabGap = 8;
        int tabW = (innerW - tabGap) / 2;
        const int tabH = 34;

        _loginTabRect = new Rectangle(innerX, cursorY, tabW, tabH);
        _registerTabRect = new Rectangle(innerX + tabW + tabGap, cursorY, tabW, tabH);

        DrawTabButton(spriteBatch, _loginTabRect, "LOGIN", active: !isRegister);
        DrawTabButton(spriteBatch, _registerTabRect, "REGISTER", active: isRegister);
        cursorY += tabH + 16;

        bool blinkCursor = ((int)(elapsedTime * 2.4f)) % 2 == 0;

        // 4. Field Username
        DrawFieldLabel(spriteBatch, "USERNAME", innerX, cursorY);
        cursorY += 12;
        _usernameRect = new Rectangle(innerX, cursorY, innerW, 32);
        DrawTextInputBox(
            spriteBatch,
            _usernameRect,
            state.Username,
            placeholder: "3-16 chars (letters, numbers, _)",
            focused: state.FocusedField == AuthInputField.Username,
            masked: false,
            blinkCursor: blinkCursor,
            rightReservePx: 0);
        cursorY += 42;

        // 5. Field Password (tersamarkan •••••• + tombol toggle mata)
        DrawFieldLabel(spriteBatch, "PASSWORD", innerX, cursorY);
        cursorY += 12;
        _passwordRect = new Rectangle(innerX, cursorY, innerW, 32);
        _toggleEyeRect = new Rectangle(_passwordRect.Right - 42, _passwordRect.Y + 4, 38, 24);
        DrawTextInputBox(
            spriteBatch,
            _passwordRect,
            state.Password,
            placeholder: "Minimum 6 characters",
            focused: state.FocusedField == AuthInputField.Password,
            masked: !state.ShowPassword,
            blinkCursor: blinkCursor,
            rightReservePx: 46);
        DrawEyeToggleButton(spriteBatch, _toggleEyeRect, state.ShowPassword);
        cursorY += 42;

        // 6. Field Confirm Password (hanya saat tab REGISTER aktif)
        if (isRegister)
        {
            DrawFieldLabel(spriteBatch, "CONFIRM PASSWORD", innerX, cursorY);
            cursorY += 12;
            _confirmPasswordRect = new Rectangle(innerX, cursorY, innerW, 32);
            DrawTextInputBox(
                spriteBatch,
                _confirmPasswordRect,
                state.ConfirmPassword,
                placeholder: "Re-enter password",
                focused: state.FocusedField == AuthInputField.ConfirmPassword,
                masked: !state.ShowPassword,
                blinkCursor: blinkCursor,
                rightReservePx: 0);
            cursorY += 42;
        }
        else
        {
            _confirmPasswordRect = Rectangle.Empty;
        }

        // 7. Field kecil Server di bagian bawah (default 127.0.0.1:9050)
        DrawFieldLabel(spriteBatch, "SERVER (IP:PORT)", innerX, cursorY);
        cursorY += 12;
        _serverRect = new Rectangle(innerX, cursorY, innerW, 28);
        DrawTextInputBox(
            spriteBatch,
            _serverRect,
            state.ServerAddress,
            placeholder: "stallion-hills.tun.ply.gg:11872",
            focused: state.FocusedField == AuthInputField.ServerAddress,
            masked: false,
            blinkCursor: blinkCursor,
            rightReservePx: 0);
        cursorY += 40;

        // 8. Tombol utama hijau (LOGIN / CREATE ACCOUNT)
        _submitBtnRect = new Rectangle(innerX, cursorY, innerW, 38);
        Color btnFill = state.IsBusy ? HudRenderer.GtSlotFill : HudRenderer.GtGreenBtn;
        Color btnBorder = state.IsBusy ? HudRenderer.GtBorderSubtle : HudRenderer.GtGreenBorder;
        DrawGtPanel(spriteBatch, _submitBtnRect, btnFill, btnBorder, topHighlightAlpha: 68, borderThickness: 2);

        string btnLabel = state.IsBusy
            ? "CONNECTING..."
            : (isRegister ? "CREATE ACCOUNT" : "LOGIN");
        Vector2 btnLabelSize = _font.MeasureString(btnLabel, scale: 2);
        _font.DrawString(
            spriteBatch,
            btnLabel,
            new Vector2(
                _submitBtnRect.X + (_submitBtnRect.Width - btnLabelSize.X) * 0.5f,
                _submitBtnRect.Y + (_submitBtnRect.Height - btnLabelSize.Y) * 0.5f),
            Color.White,
            scale: 2,
            dropShadow: true,
            bold: true);
        cursorY += 46;

        // 9. Area pesan status / error (merah muda terang untuk error, kuning untuk info/connecting)
        if (!string.IsNullOrWhiteSpace(state.StatusMessage))
        {
            Color msgColor = state.IsErrorStatus ? HudRenderer.ChatErrorColor : HudRenderer.ChatSystemColor;
            List<string> wrapped = _font.WrapText(state.StatusMessage, innerW, scale: 1);
            int maxLines = Math.Min(2, wrapped.Count);
            for (int i = 0; i < maxLines; i++)
            {
                Vector2 lineSize = _font.MeasureString(wrapped[i], scale: 1);
                _font.DrawString(
                    spriteBatch,
                    wrapped[i],
                    new Vector2(cardX + (cardW - lineSize.X) * 0.5f, cursorY + i * 12),
                    msgColor,
                    scale: 1,
                    dropShadow: true);
            }
        }

        // Petunjuk pintasan di bawah kartu
        const string footerHint = "TAB: Next Field   |   ENTER: Submit   |   ESC: Exit";
        Vector2 hintSize = _font.MeasureString(footerHint, scale: 1);
        _font.DrawString(
            spriteBatch,
            footerHint,
            new Vector2((viewport.Width - hintSize.X) * 0.5f, cardRect.Bottom + 10),
            new Color(224, 244, 255),
            scale: 1,
            dropShadow: true);
    }

    public void DrawEnteringWorld(
        SpriteBatch spriteBatch,
        Viewport viewport,
        string worldName,
        string displayName,
        float elapsedTime)
    {
        const int w = 420;
        const int h = 136;
        int x = (viewport.Width - w) / 2;
        int y = (viewport.Height - h) / 2;
        var rect = new Rectangle(x, y, w, h);

        DrawGtPanel(
            spriteBatch,
            rect,
            new Color(HudRenderer.GtPanelFill.R, HudRenderer.GtPanelFill.G, HudRenderer.GtPanelFill.B, (byte)228),
            HudRenderer.GtBorderBright,
            topHighlightAlpha: 68,
            borderThickness: 2);

        int dotCount = ((int)(elapsedTime * 3f) % 3) + 1;
        string dots = new string('.', dotCount);
        string title = $"ENTERING {(string.IsNullOrWhiteSpace(worldName) ? "START" : worldName.ToUpperInvariant())}{dots}";
        Vector2 titleSize = _font.MeasureString(title, scale: 2);
        _font.DrawString(
            spriteBatch,
            title,
            new Vector2(x + (w - titleSize.X) * 0.5f, y + 32),
            new Color(255, 240, 128),
            scale: 2,
            dropShadow: true,
            bold: true);

        string greeting = string.IsNullOrWhiteSpace(displayName)
            ? "Authenticated!"
            : $"Welcome back, {displayName}!";
        Vector2 greetSize = _font.MeasureString(greeting, scale: 1);
        _font.DrawString(
            spriteBatch,
            greeting,
            new Vector2(x + (w - greetSize.X) * 0.5f, y + 68),
            Color.White,
            scale: 1,
            dropShadow: true);

        const string sub = "Receiving world snapshot from server...";
        Vector2 subSize = _font.MeasureString(sub, scale: 1);
        _font.DrawString(
            spriteBatch,
            sub,
            new Vector2(x + (w - subSize.X) * 0.5f, y + 92),
            HudRenderer.WorldStatusCyan,
            scale: 1,
            dropShadow: true);
    }

    private void DrawTabButton(SpriteBatch spriteBatch, Rectangle rect, string label, bool active)
    {
        Color fill = active ? HudRenderer.GtPanelSolid : HudRenderer.GtPanelDark;
        Color border = active ? HudRenderer.GtGoldBorder : HudRenderer.GtBorderSubtle;
        DrawGtPanel(spriteBatch, rect, fill, border, topHighlightAlpha: active ? (byte)64 : (byte)28, borderThickness: active ? 2 : 1);

        Vector2 size = _font.MeasureString(label, scale: 2);
        Color textColor = active ? new Color(255, 242, 136) : new Color(172, 214, 240);
        _font.DrawString(
            spriteBatch,
            label,
            new Vector2(rect.X + (rect.Width - size.X) * 0.5f, rect.Y + (rect.Height - size.Y) * 0.5f),
            textColor,
            scale: 2,
            dropShadow: true,
            bold: active);
    }

    private void DrawFieldLabel(SpriteBatch spriteBatch, string label, int x, int y)
    {
        _font.DrawString(spriteBatch, label, new Vector2(x, y), HudRenderer.WorldStatusCyan, scale: 1, dropShadow: true);
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
        Color border = focused ? HudRenderer.GtGoldBorder : HudRenderer.GtBorderSubtle;
        DrawGtPanel(spriteBatch, rect, HudRenderer.GtPanelDark, border, topHighlightAlpha: focused ? (byte)54 : (byte)28, borderThickness: focused ? 2 : 1);

        int textX = rect.X + 10;
        int textY = rect.Y + (rect.Height - PixelFont.GlyphHeight) / 2;
        int availW = Math.Max(40, rect.Width - 20 - rightReservePx);

        if (string.IsNullOrEmpty(value))
        {
            if (focused && blinkCursor)
            {
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(textX, textY - 1, 2, PixelFont.GlyphHeight + 2), Color.White);
            }
            else if (!focused)
            {
                _font.DrawString(spriteBatch, placeholder, new Vector2(textX, textY), new Color(132, 174, 204), scale: 1, dropShadow: false);
            }

            return;
        }

        if (masked)
        {
            // Render titik-titik bulat tajam (••••••) agar tidak bergantung pada glyph Unicode.
            const int dotSize = 4;
            const int dotStep = 7;
            int maxDots = Math.Max(4, (availW - 6) / dotStep);
            int shownDots = Math.Min(value.Length, maxDots);
            int dotY = rect.Y + (rect.Height - dotSize) / 2;

            for (int i = 0; i < shownDots; i++)
            {
                int dx = textX + i * dotStep;
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(dx + 1, dotY + 1, dotSize, dotSize), new Color(0, 0, 0, 180));
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(dx, dotY, dotSize, dotSize), Color.White);
            }

            if (focused && blinkCursor)
            {
                int caretX = textX + shownDots * dotStep + 1;
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(caretX, textY - 1, 2, PixelFont.GlyphHeight + 2), Color.White);
            }
        }
        else
        {
            int maxChars = Math.Max(6, (availW - 6) / PixelFont.CharAdvance);
            string shown = value.Length > maxChars ? value[^maxChars..] : value;
            _font.DrawString(spriteBatch, shown, new Vector2(textX, textY), Color.White, scale: 1, dropShadow: true);

            if (focused && blinkCursor)
            {
                int caretX = textX + _font.Advance(shown, scale: 1);
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(caretX, textY - 1, 2, PixelFont.GlyphHeight + 2), Color.White);
            }
        }
    }

    private void DrawEyeToggleButton(SpriteBatch spriteBatch, Rectangle rect, bool showPassword)
    {
        Color fill = showPassword ? HudRenderer.GtSlotSelectedFill : HudRenderer.GtSlotFill;
        Color border = showPassword ? HudRenderer.GtGoldBorder : HudRenderer.GtBorderSubtle;
        DrawGtPanel(spriteBatch, rect, fill, border, topHighlightAlpha: 42);

        // Ikon mata pixel-art kecil + label singkat
        int cx = rect.X + rect.Width / 2;
        int cy = rect.Y + rect.Height / 2;
        Color eyeCol = showPassword ? new Color(255, 240, 132) : new Color(204, 236, 255);

        // Kelopak mata atas & bawah
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 5, cy - 3, 10, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 7, cy - 2, 2, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx + 5, cy - 2, 2, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 8, cy - 1, 2, 2), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx + 6, cy - 1, 2, 2), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 7, cy + 1, 2, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx + 5, cy + 1, 2, 1), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 5, cy + 2, 10, 1), eyeCol);

        // Pupil di tengah
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 2, cy - 2, 4, 4), eyeCol);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx - 1, cy - 1, 2, 2), HudRenderer.GtPanelDark);

        // Garis silang diagonal bila password sedang disembunyikan
        if (!showPassword)
        {
            for (int d = -4; d <= 4; d++)
            {
                spriteBatch.Draw(_atlas.Pixel, new Rectangle(cx + d, cy + d / 2, 2, 1), HudRenderer.ChatErrorColor);
            }
        }
    }

    private void DrawGtPanel(
        SpriteBatch spriteBatch,
        Rectangle rect,
        Color fill,
        Color border,
        byte topHighlightAlpha = 48,
        int borderThickness = 1)
    {
        UiDraw.RoundedRect(spriteBatch, _atlas.Pixel, rect, fill, radius: 5);

        spriteBatch.Draw(_atlas.Pixel, new Rectangle(rect.X + 2, rect.Y, rect.Width - 4, borderThickness), border);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(rect.X + 2, rect.Bottom - borderThickness, rect.Width - 4, borderThickness), border);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(rect.X, rect.Y + 2, borderThickness, rect.Height - 4), border);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(rect.Right - borderThickness, rect.Y + 2, borderThickness, rect.Height - 4), border);

        if (topHighlightAlpha > 0 && rect.Width > 8)
        {
            spriteBatch.Draw(
                _atlas.Pixel,
                new Rectangle(rect.X + 3, rect.Y + borderThickness, rect.Width - 6, 1),
                new Color((byte)255, (byte)255, (byte)255, topHighlightAlpha));
        }
    }
}
