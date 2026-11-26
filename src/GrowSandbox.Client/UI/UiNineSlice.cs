using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.UI;

public enum UiPanelStyle : byte
{
    PanelBlue = 0,
    PanelBlueOpaque = 1,
    PanelDark = 2,
    PanelDarkGold = 3,
    SlotNormal = 4,
    SlotSelected = 5,
    BtnGreen = 6,
    BtnRed = 7,
    BtnGold = 8,
    BtnBlue = 9,
    BtnDisabled = 10,
    SpeechBubble = 11,
}

public enum UiButtonState : byte
{
    Normal = 0,
    Hover = 1,
    Pressed = 2,
    Disabled = 3,
}

/// <summary>
/// Generator tekstur 9-slice 48x48 (corner slice 12px) berbasis Signed Distance Field (SDF)
/// anti-aliased saat startup sehingga sudut membulat (radius ~8px) mulus tanpa gerigi piksel,
/// lengkap dengan gradasi glossy lembut, inner top highlight 1px, dan drop shadow tipis.
/// Seluruh piksel disimpan dalam premultiplied alpha untuk MonoGame BlendState.AlphaBlend.
/// </summary>
public sealed class UiNineSlice : IDisposable
{
    public const int TextureSize = 48;
    public const int DefaultCornerSize = 12;

    private readonly Dictionary<UiPanelStyle, Texture2D> _textures = new();
    private readonly Dictionary<UiPanelStyle, Texture2D> _hoverTextures = new();

    public Texture2D PanelBlue => _textures[UiPanelStyle.PanelBlue];
    public Texture2D PanelBlueOpaque => _textures[UiPanelStyle.PanelBlueOpaque];
    public Texture2D PanelDark => _textures[UiPanelStyle.PanelDark];
    public Texture2D PanelDarkGold => _textures[UiPanelStyle.PanelDarkGold];
    public Texture2D SlotNormal => _textures[UiPanelStyle.SlotNormal];
    public Texture2D SlotSelected => _textures[UiPanelStyle.SlotSelected];
    public Texture2D BtnGreen => _textures[UiPanelStyle.BtnGreen];
    public Texture2D BtnRed => _textures[UiPanelStyle.BtnRed];
    public Texture2D BtnGold => _textures[UiPanelStyle.BtnGold];
    public Texture2D BtnBlue => _textures[UiPanelStyle.BtnBlue];
    public Texture2D BtnDisabled => _textures[UiPanelStyle.BtnDisabled];
    public Texture2D SpeechBubble => _textures[UiPanelStyle.SpeechBubble];

    public UiNineSlice(GraphicsDevice graphicsDevice)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);

        // Panel biru-cyan semi-transparan bergaya Growtopia dengan glossy top half & drop shadow tipis
        _textures[UiPanelStyle.PanelBlue] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(36, 126, 184),
            FillBottom: new Color(20, 82, 128),
            FillAlpha: 0.86f,
            BorderColor: new Color(126, 218, 255),
            BorderAlpha: 0.96f,
            BorderThickness: 1.85f,
            CornerRadius: 8.0f,
            TopGlossStrength: 0.22f,
            InnerHighlightAlpha: 0.56f,
            DropShadowAlpha: 0.36f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0f));

        // Panel biru lebih solid untuk modal & inventory drawer agar teks sangat mudah dibaca
        _textures[UiPanelStyle.PanelBlueOpaque] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(38, 124, 178),
            FillBottom: new Color(22, 86, 134),
            FillAlpha: 0.95f,
            BorderColor: new Color(132, 222, 255),
            BorderAlpha: 1.0f,
            BorderThickness: 2.0f,
            CornerRadius: 8.5f,
            TopGlossStrength: 0.20f,
            InnerHighlightAlpha: 0.62f,
            DropShadowAlpha: 0.42f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0f));

        // Panel biru tua semi-transparan untuk kotak input, latar log chat, debug overlay, dan badge
        _textures[UiPanelStyle.PanelDark] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(18, 62, 98),
            FillBottom: new Color(12, 42, 70),
            FillAlpha: 0.88f,
            BorderColor: new Color(112, 198, 240),
            BorderAlpha: 0.85f,
            BorderThickness: 1.45f,
            CornerRadius: 7.5f,
            TopGlossStrength: 0.10f,
            InnerHighlightAlpha: 0.30f,
            DropShadowAlpha: 0.32f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0f));

        // Panel gelap dengan border emas (untuk input aktif, World Lock panel, dan highlight toast)
        _textures[UiPanelStyle.PanelDarkGold] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(20, 68, 108),
            FillBottom: new Color(13, 46, 76),
            FillAlpha: 0.92f,
            BorderColor: new Color(255, 220, 74),
            BorderAlpha: 1.0f,
            BorderThickness: 1.85f,
            CornerRadius: 7.5f,
            TopGlossStrength: 0.12f,
            InnerHighlightAlpha: 0.38f,
            DropShadowAlpha: 0.34f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0.24f));

        // Slot inventory membulat dengan efek cekung (inset) halus
        _textures[UiPanelStyle.SlotNormal] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(26, 72, 108),
            FillBottom: new Color(36, 94, 136),
            FillAlpha: 0.93f,
            BorderColor: new Color(102, 180, 224),
            BorderAlpha: 0.90f,
            BorderThickness: 1.45f,
            CornerRadius: 7.5f,
            TopGlossStrength: 0.0f,
            InnerHighlightAlpha: 0.0f,
            DropShadowAlpha: 0.18f,
            IsConcaveSlot: true,
            GlowBorderAlpha: 0f));

        // Slot terpilih dengan border emas bercahaya
        _textures[UiPanelStyle.SlotSelected] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(50, 118, 168),
            FillBottom: new Color(68, 146, 198),
            FillAlpha: 0.97f,
            BorderColor: new Color(255, 222, 72),
            BorderAlpha: 1.0f,
            BorderThickness: 2.15f,
            CornerRadius: 7.5f,
            TopGlossStrength: 0.08f,
            InnerHighlightAlpha: 0.32f,
            DropShadowAlpha: 0.24f,
            IsConcaveSlot: true,
            GlowBorderAlpha: 0.42f));

        // Tombol Glossy + varian Hover (+15% lebih terang)
        RegisterButtonPair(graphicsDevice, UiPanelStyle.BtnGreen, new NineSliceSpec(
            FillTop: new Color(72, 192, 92),
            FillBottom: new Color(42, 142, 62),
            FillAlpha: 0.98f,
            BorderColor: new Color(156, 248, 164),
            BorderAlpha: 1.0f,
            BorderThickness: 1.75f,
            CornerRadius: 8.0f,
            TopGlossStrength: 0.26f,
            InnerHighlightAlpha: 0.65f,
            DropShadowAlpha: 0.36f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0f));

        RegisterButtonPair(graphicsDevice, UiPanelStyle.BtnRed, new NineSliceSpec(
            FillTop: new Color(216, 78, 78),
            FillBottom: new Color(168, 44, 48),
            FillAlpha: 0.98f,
            BorderColor: new Color(255, 166, 166),
            BorderAlpha: 1.0f,
            BorderThickness: 1.75f,
            CornerRadius: 8.0f,
            TopGlossStrength: 0.24f,
            InnerHighlightAlpha: 0.58f,
            DropShadowAlpha: 0.36f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0f));

        RegisterButtonPair(graphicsDevice, UiPanelStyle.BtnGold, new NineSliceSpec(
            FillTop: new Color(232, 164, 44),
            FillBottom: new Color(180, 112, 22),
            FillAlpha: 0.98f,
            BorderColor: new Color(255, 232, 112),
            BorderAlpha: 1.0f,
            BorderThickness: 1.85f,
            CornerRadius: 8.0f,
            TopGlossStrength: 0.28f,
            InnerHighlightAlpha: 0.72f,
            DropShadowAlpha: 0.38f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0.22f));

        RegisterButtonPair(graphicsDevice, UiPanelStyle.BtnBlue, new NineSliceSpec(
            FillTop: new Color(48, 146, 206),
            FillBottom: new Color(28, 100, 154),
            FillAlpha: 0.96f,
            BorderColor: new Color(138, 224, 255),
            BorderAlpha: 1.0f,
            BorderThickness: 1.75f,
            CornerRadius: 8.0f,
            TopGlossStrength: 0.24f,
            InnerHighlightAlpha: 0.60f,
            DropShadowAlpha: 0.36f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0f));

        _textures[UiPanelStyle.BtnDisabled] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(78, 96, 112),
            FillBottom: new Color(56, 72, 86),
            FillAlpha: 0.86f,
            BorderColor: new Color(132, 152, 170),
            BorderAlpha: 0.75f,
            BorderThickness: 1.45f,
            CornerRadius: 8.0f,
            TopGlossStrength: 0.08f,
            InnerHighlightAlpha: 0.22f,
            DropShadowAlpha: 0.22f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0f));

        // Speech bubble putih lembut membulat dengan border biru tua
        _textures[UiPanelStyle.SpeechBubble] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(255, 255, 255),
            FillBottom: new Color(238, 246, 252),
            FillAlpha: 0.97f,
            BorderColor: new Color(28, 54, 82),
            BorderAlpha: 0.96f,
            BorderThickness: 1.85f,
            CornerRadius: 8.0f,
            TopGlossStrength: 0.0f,
            InnerHighlightAlpha: 0.0f,
            DropShadowAlpha: 0.32f,
            IsConcaveSlot: false,
            GlowBorderAlpha: 0f));

        // Hover varian untuk SlotNormal agar slot ikut merespons halus saat mouse lewat
        _hoverTextures[UiPanelStyle.SlotNormal] = BuildTexture(graphicsDevice, new NineSliceSpec(
            FillTop: new Color(36, 94, 138),
            FillBottom: new Color(48, 118, 166),
            FillAlpha: 0.96f,
            BorderColor: new Color(156, 228, 255),
            BorderAlpha: 0.98f,
            BorderThickness: 1.65f,
            CornerRadius: 7.5f,
            TopGlossStrength: 0.06f,
            InnerHighlightAlpha: 0.22f,
            DropShadowAlpha: 0.20f,
            IsConcaveSlot: true,
            GlowBorderAlpha: 0.14f));
    }

    public Texture2D GetTexture(UiPanelStyle style, bool hover = false)
    {
        if (hover && _hoverTextures.TryGetValue(style, out Texture2D? hoverTex))
        {
            return hoverTex;
        }

        return _textures[style];
    }

    /// <summary>
    /// Menggambar tekstur 9-slice ke persegi panjang tujuan ukuran apa pun tanpa merusak kelengkungan sudut.
    /// </summary>
    public static void DrawNineSlice(SpriteBatch sb, Texture2D tex, Rectangle dest, int cornerSize, Color tint)
    {
        if (dest.Width <= 0 || dest.Height <= 0 || tint.A == 0)
        {
            return;
        }

        int sc = Math.Clamp(cornerSize, 1, Math.Min(tex.Width / 2, tex.Height / 2));
        int dcw = Math.Min(sc, dest.Width / 2);
        int dch = Math.Min(sc, dest.Height / 2);

        int swMid = tex.Width - sc * 2;
        int shMid = tex.Height - sc * 2;
        int dwMid = dest.Width - dcw * 2;
        int dhMid = dest.Height - dch * 2;

        int sx0 = 0;
        int sx1 = sc;
        int sx2 = tex.Width - sc;

        int sy0 = 0;
        int sy1 = sc;
        int sy2 = tex.Height - sc;

        int dx0 = dest.X;
        int dx1 = dest.X + dcw;
        int dx2 = dest.Right - dcw;

        int dy0 = dest.Y;
        int dy1 = dest.Y + dch;
        int dy2 = dest.Bottom - dch;

        // 4 Sudut (tidak pernah terdistorsi)
        sb.Draw(tex, new Rectangle(dx0, dy0, dcw, dch), new Rectangle(sx0, sy0, sc, sc), tint);
        sb.Draw(tex, new Rectangle(dx2, dy0, dcw, dch), new Rectangle(sx2, sy0, sc, sc), tint);
        sb.Draw(tex, new Rectangle(dx0, dy2, dcw, dch), new Rectangle(sx0, sy2, sc, sc), tint);
        sb.Draw(tex, new Rectangle(dx2, dy2, dcw, dch), new Rectangle(sx2, sy2, sc, sc), tint);

        // Tepi atas & bawah
        if (dwMid > 0)
        {
            sb.Draw(tex, new Rectangle(dx1, dy0, dwMid, dch), new Rectangle(sx1, sy0, swMid, sc), tint);
            sb.Draw(tex, new Rectangle(dx1, dy2, dwMid, dch), new Rectangle(sx1, sy2, swMid, sc), tint);
        }

        // Tepi kiri & kanan
        if (dhMid > 0)
        {
            sb.Draw(tex, new Rectangle(dx0, dy1, dcw, dhMid), new Rectangle(sx0, sy1, sc, shMid), tint);
            sb.Draw(tex, new Rectangle(dx2, dy1, dcw, dhMid), new Rectangle(sx2, sy1, sc, shMid), tint);
        }

        // Bagian tengah
        if (dwMid > 0 && dhMid > 0)
        {
            sb.Draw(tex, new Rectangle(dx1, dy1, dwMid, dhMid), new Rectangle(sx1, sy1, swMid, shMid), tint);
        }
    }

    public void DrawPanel(SpriteBatch sb, UiPanelStyle style, Rectangle dest, float alpha = 1f, int cornerSize = DefaultCornerSize)
    {
        if (alpha <= 0.005f)
        {
            return;
        }

        Color tint = alpha >= 0.995f ? Color.White : Color.White * Math.Clamp(alpha, 0f, 1f);
        DrawNineSlice(sb, _textures[style], dest, cornerSize, tint);
    }

    public void DrawPanelTinted(SpriteBatch sb, UiPanelStyle style, Rectangle dest, Color premultipliedTint, int cornerSize = DefaultCornerSize)
    {
        DrawNineSlice(sb, _textures[style], dest, cornerSize, premultipliedTint);
    }

    public void DrawSlot(SpriteBatch sb, Rectangle dest, bool selected, bool hovered, float alpha = 1f, int cornerSize = DefaultCornerSize)
    {
        Texture2D tex = selected
            ? _textures[UiPanelStyle.SlotSelected]
            : GetTexture(UiPanelStyle.SlotNormal, hover: hovered);
        Color tint = alpha >= 0.995f ? Color.White : Color.White * Math.Clamp(alpha, 0f, 1f);
        DrawNineSlice(sb, tex, dest, cornerSize, tint);
    }

    /// <summary>
    /// Menggambar tombol membulat glossy lengkap dengan state Normal, Hover (+15% terang),
    /// Pressed (bergeser turun +1px), dan Disabled (abu-abu). Mengembalikan persegi hasil gambar
    /// (termasuk pergeseran +1px saat pressed) agar label teks ikut turun secara sinkron.
    /// </summary>
    public Rectangle DrawButton(
        SpriteBatch sb,
        UiPanelStyle buttonStyle,
        Rectangle dest,
        UiButtonState state,
        float alpha = 1f,
        int cornerSize = DefaultCornerSize)
    {
        Rectangle renderRect = state == UiButtonState.Pressed
            ? new Rectangle(dest.X, dest.Y + 1, dest.Width, dest.Height)
            : dest;

        Texture2D tex = state switch
        {
            UiButtonState.Disabled => _textures[UiPanelStyle.BtnDisabled],
            UiButtonState.Hover => GetTexture(buttonStyle, hover: true),
            _ => _textures[buttonStyle],
        };

        float clampedAlpha = Math.Clamp(alpha, 0f, 1f);
        Color tint = state == UiButtonState.Pressed
            ? new Color(232, 232, 238, 255) * clampedAlpha
            : Color.White * clampedAlpha;

        DrawNineSlice(sb, tex, renderRect, cornerSize, tint);
        return renderRect;
    }

    public static UiButtonState EvaluateButtonState(Rectangle rect, Point mousePosition, bool isLeftMouseDown, bool enabled = true)
    {
        if (!enabled)
        {
            return UiButtonState.Disabled;
        }

        if (!rect.Contains(mousePosition))
        {
            return UiButtonState.Normal;
        }

        return isLeftMouseDown ? UiButtonState.Pressed : UiButtonState.Hover;
    }

    private void RegisterButtonPair(GraphicsDevice gd, UiPanelStyle style, NineSliceSpec normalSpec)
    {
        _textures[style] = BuildTexture(gd, normalSpec);

        // Hover state: +15% lebih terang pada warna isi & border + sedikit peningkatan kilau
        NineSliceSpec hoverSpec = normalSpec with
        {
            FillTop = Brighten(normalSpec.FillTop, 1.15f),
            FillBottom = Brighten(normalSpec.FillBottom, 1.15f),
            BorderColor = Brighten(normalSpec.BorderColor, 1.12f),
            TopGlossStrength = MathF.Min(0.40f, normalSpec.TopGlossStrength + 0.05f),
            InnerHighlightAlpha = MathF.Min(0.88f, normalSpec.InnerHighlightAlpha + 0.12f),
        };
        _hoverTextures[style] = BuildTexture(gd, hoverSpec);
    }

    private static Color Brighten(Color c, float factor)
    {
        byte r = (byte)Math.Clamp((int)MathF.Round(c.R * factor + 8f * (factor - 1f)), 0, 255);
        byte g = (byte)Math.Clamp((int)MathF.Round(c.G * factor + 8f * (factor - 1f)), 0, 255);
        byte b = (byte)Math.Clamp((int)MathF.Round(c.B * factor + 8f * (factor - 1f)), 0, 255);
        return new Color(r, g, b, c.A);
    }

    private static Texture2D BuildTexture(GraphicsDevice graphicsDevice, NineSliceSpec spec)
    {
        const int size = TextureSize;
        var pixels = new Color[size * size];

        // Kotak panel utama meninggalkan ruang 2.5px di bawah khusus untuk drop shadow halus
        // sementara seluruh kurva sudut (radius <= 8.5px) tetap berada penuh dalam corner slice 12px.
        const float left = 0.75f;
        const float right = size - 0.75f;
        const float top = 0.75f;
        const float bottom = size - 3.0f;

        float cx = (left + right) * 0.5f;
        float cy = (top + bottom) * 0.5f;
        float halfW = (right - left) * 0.5f;
        float halfH = (bottom - top) * 0.5f;
        float radius = spec.CornerRadius;

        for (int y = 0; y < size; y++)
        {
            float py = y + 0.5f;
            float normY = Math.Clamp((py - top) / (bottom - top), 0f, 1f);

            for (int x = 0; x < size; x++)
            {
                float px = x + 0.5f;

                float d = RoundedBoxSdf(px, py, cx, cy, halfW, halfH, radius);

                // Anti-aliasing tepi luar panel selebar ~1.2px
                float outerCov = SmoothStep(0.62f, -0.62f, d);

                // Inner region (di dalam border)
                float innerD = d + spec.BorderThickness;
                float innerCov = SmoothStep(0.55f, -0.55f, innerD);

                // Drop shadow lembut tepat di bawah panel (hanya di luar badan panel agar kaca tidak keruh)
                float shadowA = 0f;
                if (spec.DropShadowAlpha > 0f && outerCov < 0.999f)
                {
                    float sd = RoundedBoxSdf(px, py, cx, cy + 1.9f, halfW - 0.4f, halfH - 0.1f, radius);
                    shadowA = SmoothStep(2.0f, -0.9f, sd) * spec.DropShadowAlpha * (1f - outerCov);
                }

                // Efek cahaya emas luar tipis untuk SlotSelected / PanelDarkGold / BtnGold
                float outerGlowA = 0f;
                if (spec.GlowBorderAlpha > 0f && d > -0.5f && d < 2.2f)
                {
                    outerGlowA = SmoothStep(2.0f, 0.0f, d) * spec.GlowBorderAlpha * (1f - outerCov);
                }

                // Warna isi vertikal (gradasi atas -> bawah)
                float fillR = MathHelper.Lerp(spec.FillTop.R / 255f, spec.FillBottom.R / 255f, normY);
                float fillG = MathHelper.Lerp(spec.FillTop.G / 255f, spec.FillBottom.G / 255f, normY);
                float fillB = MathHelper.Lerp(spec.FillTop.B / 255f, spec.FillBottom.B / 255f, normY);

                // Gradasi glossy lembut di separuh atas (normY 0..0.52)
                if (spec.TopGlossStrength > 0f)
                {
                    float glossBand = SmoothStep(0.54f, 0.02f, normY);
                    // Sedikit lebih kuat di seperempat atas untuk kesan kaca/plastik halus Growtopia
                    float gloss = glossBand * glossBand * spec.TopGlossStrength;
                    fillR = MathHelper.Lerp(fillR, 1f, gloss);
                    fillG = MathHelper.Lerp(fillG, 1f, gloss);
                    fillB = MathHelper.Lerp(fillB, 1f, gloss);
                }

                // Efek cekung (concave/inset) khusus slot inventory: bayangan dalam di atas, highlight halus di bawah
                if (spec.IsConcaveSlot && innerCov > 0.001f)
                {
                    float distFromTop = MathF.Max(0f, py - (top + spec.BorderThickness));
                    float distFromBottom = MathF.Max(0f, (bottom - spec.BorderThickness) - py);
                    float distFromLeft = MathF.Max(0f, px - (left + spec.BorderThickness));
                    float distFromRight = MathF.Max(0f, (right - spec.BorderThickness) - px);

                    float topShade = SmoothStep(4.2f, 0f, distFromTop) * 0.28f;
                    float sideShade = MathF.Max(SmoothStep(2.5f, 0f, distFromLeft), SmoothStep(2.5f, 0f, distFromRight)) * 0.12f;
                    float shade = MathF.Min(0.35f, topShade + sideShade);
                    fillR *= 1f - shade;
                    fillG *= 1f - shade;
                    fillB *= 1f - shade;

                    float bottomRim = SmoothStep(2.4f, 0f, distFromBottom) * 0.16f;
                    fillR = MathHelper.Lerp(fillR, 1f, bottomRim);
                    fillG = MathHelper.Lerp(fillG, 1f, bottomRim);
                    fillB = MathHelper.Lerp(fillB, 1f, bottomRim);
                }

                // Inner highlight 1px terang mengikuti kontur tepi dalam atas
                if (spec.InnerHighlightAlpha > 0f && innerCov > 0.01f && py < cy)
                {
                    float distInward = -innerD;
                    float rimBand = Math.Clamp(1f - MathF.Abs(distInward - 0.65f) / 0.85f, 0f, 1f);
                    float topMask = SmoothStep(top + radius + spec.BorderThickness + 2.5f, top + spec.BorderThickness + 1.2f, py);
                    float hi = rimBand * topMask * spec.InnerHighlightAlpha;
                    fillR = MathHelper.Lerp(fillR, 1f, hi);
                    fillG = MathHelper.Lerp(fillG, 1f, hi);
                    fillB = MathHelper.Lerp(fillB, 1f, hi);
                }

                // Inner glow lembut di tepi dalam untuk SlotSelected / PanelDarkGold
                if (spec.GlowBorderAlpha > 0f && innerCov > 0.01f)
                {
                    float innerGlow = SmoothStep(3.6f, 0f, -innerD) * (spec.GlowBorderAlpha * 0.55f);
                    fillR = MathHelper.Lerp(fillR, spec.BorderColor.R / 255f, innerGlow);
                    fillG = MathHelper.Lerp(fillG, spec.BorderColor.G / 255f, innerGlow);
                    fillB = MathHelper.Lerp(fillB, spec.BorderColor.B / 255f, innerGlow);
                }

                // Campurkan warna interior & border secara mulus pada area panel
                float borderR = spec.BorderColor.R / 255f;
                float borderG = spec.BorderColor.G / 255f;
                float borderB = spec.BorderColor.B / 255f;

                float panelR = MathHelper.Lerp(borderR, fillR, innerCov);
                float panelG = MathHelper.Lerp(borderG, fillG, innerCov);
                float panelB = MathHelper.Lerp(borderB, fillB, innerCov);
                float panelA = MathHelper.Lerp(spec.BorderAlpha, spec.FillAlpha, innerCov) * outerCov;

                // Komposisi panel di atas drop shadow / outer glow dengan premultiplied alpha
                float bgA = shadowA;
                float bgR = 0f;
                float bgG = 0f;
                float bgB = 0f;

                if (outerGlowA > 0.001f)
                {
                    float glowA = outerGlowA;
                    float outBgA = glowA + bgA * (1f - glowA);
                    if (outBgA > 0.0001f)
                    {
                        bgR = (borderR * glowA + bgR * bgA * (1f - glowA)) / outBgA;
                        bgG = (borderG * glowA + bgG * bgA * (1f - glowA)) / outBgA;
                        bgB = (borderB * glowA + bgB * bgA * (1f - glowA)) / outBgA;
                        bgA = outBgA;
                    }
                }

                float finalA = panelA + bgA * (1f - panelA);
                if (finalA <= 0.001f)
                {
                    pixels[y * size + x] = Color.Transparent;
                    continue;
                }

                float finalR = (panelR * panelA + bgR * bgA * (1f - panelA)) / finalA;
                float finalG = (panelG * panelA + bgG * bgA * (1f - panelA)) / finalA;
                float finalB = (panelB * panelA + bgB * bgA * (1f - panelA)) / finalA;

                // Simpan dalam format premultiplied alpha agar cocok dengan BlendState.AlphaBlend MonoGame
                byte pr = (byte)Math.Clamp((int)MathF.Round(finalR * finalA * 255f), 0, 255);
                byte pg = (byte)Math.Clamp((int)MathF.Round(finalG * finalA * 255f), 0, 255);
                byte pb = (byte)Math.Clamp((int)MathF.Round(finalB * finalA * 255f), 0, 255);
                byte pa = (byte)Math.Clamp((int)MathF.Round(finalA * 255f), 0, 255);
                pixels[y * size + x] = new Color(pr, pg, pb, pa);
            }
        }

        var texture = new Texture2D(graphicsDevice, size, size);
        texture.SetData(pixels);
        return texture;
    }

    private static float RoundedBoxSdf(float px, float py, float cx, float cy, float halfW, float halfH, float radius)
    {
        float qx = MathF.Abs(px - cx) - (halfW - radius);
        float qy = MathF.Abs(py - cy) - (halfH - radius);
        float ox = MathF.Max(qx, 0f);
        float oy = MathF.Max(qy, 0f);
        return MathF.Sqrt(ox * ox + oy * oy) + MathF.Min(MathF.Max(qx, qy), 0f) - radius;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public void Dispose()
    {
        foreach (Texture2D tex in _textures.Values)
        {
            tex.Dispose();
        }

        foreach (Texture2D tex in _hoverTextures.Values)
        {
            tex.Dispose();
        }

        _textures.Clear();
        _hoverTextures.Clear();
    }

    private readonly record struct NineSliceSpec(
        Color FillTop,
        Color FillBottom,
        float FillAlpha,
        Color BorderColor,
        float BorderAlpha,
        float BorderThickness,
        float CornerRadius,
        float TopGlossStrength,
        float InnerHighlightAlpha,
        float DropShadowAlpha,
        bool IsConcaveSlot,
        float GlowBorderAlpha);
}
