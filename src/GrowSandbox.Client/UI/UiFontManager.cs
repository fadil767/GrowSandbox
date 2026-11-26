using System.Text;
using FontStashSharp;
using FontStashSharp.Interfaces;
using GrowSandbox.Client.Graphics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.UI;

public enum UiFontSize : byte
{
    Tiny = 0,   // 11px — angka slot inventory & shortcut kecil
    Small = 1,  // 13px — label sekunder, log chat, badge, debug F3, nameplate
    Normal = 2, // 15px — teks tombol, kotak input, keterangan utama
    Title = 3,  // 19px — judul modal, header world, angka Gem
    Header = 4, // 28px — logo layar Login & banner World Entry
}

/// <summary>
/// Pengelola tipografi smooth resolusi tinggi berbasis <see cref="FontStashSharp"/> (.ttf anti-aliased)
/// dengan adapter renderer MonoGame mandiri dan pencarian font otomatis
/// (Content/Fonts/ui_bold.ttf -> font sistem Windows -> fallback aman ke PixelFont).
/// Seluruh teks dirender pada koordinat terbulat (<see cref="MathF.Floor"/>) dengan drop shadow 1px lembut.
/// </summary>
public sealed class UiFontManager : IDisposable
{
    private readonly PixelFont _fallbackFont;
    private readonly MonoGameTexture2DManager? _textureManager;
    private readonly MonoGameFontRenderer? _renderer;
    private readonly FontSystem? _fontSystem;
    private readonly DynamicSpriteFont? _tinyFont;
    private readonly DynamicSpriteFont? _smallFont;
    private readonly DynamicSpriteFont? _normalFont;
    private readonly DynamicSpriteFont? _titleFont;
    private readonly DynamicSpriteFont? _headerFont;

    public bool IsSmoothFontLoaded => _fontSystem is not null && _renderer is not null;
    public string LoadedFontName { get; }

    public UiFontManager(GraphicsDevice graphicsDevice, PixelFont fallbackFont)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        _fallbackFont = fallbackFont ?? throw new ArgumentNullException(nameof(fallbackFont));

        string? resolvedPath = FindBestFontPath();
        if (resolvedPath is not null)
        {
            try
            {
                byte[] fontBytes = File.ReadAllBytes(resolvedPath);
                var settings = new FontSystemSettings
                {
                    PremultiplyAlpha = true,
                    FontResolutionFactor = 1.0f,
                    KernelWidth = 0,
                    KernelHeight = 0,
                };

                _textureManager = new MonoGameTexture2DManager(graphicsDevice);
                _renderer = new MonoGameFontRenderer(_textureManager);
                _fontSystem = new FontSystem(settings);
                _fontSystem.AddFont(fontBytes);

                _tinyFont = _fontSystem.GetFont(11f);
                _smallFont = _fontSystem.GetFont(13f);
                _normalFont = _fontSystem.GetFont(15f);
                _titleFont = _fontSystem.GetFont(19f);
                _headerFont = _fontSystem.GetFont(28f);
                LoadedFontName = Path.GetFileName(resolvedPath);
                return;
            }
            catch
            {
                _fontSystem?.Dispose();
                _textureManager?.Dispose();
                _fontSystem = null;
                _textureManager = null;
                _renderer = null;
            }
        }

        LoadedFontName = "PixelFont (Fallback)";
    }

    public Vector2 MeasureString(string? text, UiFontSize size = UiFontSize.Normal)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Vector2.Zero;
        }

        DynamicSpriteFont? font = GetDynamicFont(size);
        if (font is not null)
        {
            System.Numerics.Vector2 measured = font.MeasureString(text);
            return new Vector2(measured.X, measured.Y);
        }

        return _fallbackFont.MeasureString(text, GetFallbackScale(size));
    }

    public int GetLineHeight(UiFontSize size = UiFontSize.Normal)
    {
        DynamicSpriteFont? font = GetDynamicFont(size);
        if (font is not null)
        {
            return Math.Max(10, font.LineHeight);
        }

        return PixelFont.LineHeight * GetFallbackScale(size);
    }

    /// <summary>
    /// Menggambar teks anti-aliased tajam pada koordinat terbulat (<see cref="MathF.Floor"/>)
    /// dengan drop shadow 1px lembut (<c>new Color(0, 0, 0, 150)</c>) yang selalu proporsional terhadap alpha warna teks.
    /// </summary>
    public void DrawString(
        SpriteBatch spriteBatch,
        string? text,
        Vector2 position,
        Color color,
        UiFontSize size = UiFontSize.Normal,
        bool dropShadow = true)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0)
        {
            return;
        }

        float fx = MathF.Floor(position.X);
        float fy = MathF.Floor(position.Y);

        DynamicSpriteFont? font = GetDynamicFont(size);
        if (font is not null && _renderer is not null)
        {
            _renderer.CurrentBatch = spriteBatch;
            try
            {
                if (dropShadow)
                {
                    byte shadowAlpha = (byte)Math.Clamp((int)MathF.Round(150f * (color.A / 255f)), 0, 255);
                    var shadowColor = new FSColor((byte)0, (byte)0, (byte)0, shadowAlpha);
                    font.DrawText(_renderer, text, new System.Numerics.Vector2(fx + 1f, fy + 1f), shadowColor);
                }

                font.DrawText(_renderer, text, new System.Numerics.Vector2(fx, fy), ToFsColor(color));
            }
            finally
            {
                _renderer.CurrentBatch = null;
            }

            return;
        }

        _fallbackFont.DrawString(
            spriteBatch,
            text,
            new Vector2(fx, fy),
            color,
            scale: GetFallbackScale(size),
            dropShadow: dropShadow,
            bold: size is UiFontSize.Title or UiFontSize.Header);
    }

    /// <summary>
    /// Menggambar teks berukuran besar dengan outline lembut 4-arah + drop shadow (ideal untuk header Login & World Banner).
    /// </summary>
    public void DrawStringOutline(
        SpriteBatch spriteBatch,
        string? text,
        Vector2 position,
        Color color,
        UiFontSize size = UiFontSize.Header,
        Color? outlineColor = null)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0)
        {
            return;
        }

        float fx = MathF.Floor(position.X);
        float fy = MathF.Floor(position.Y);
        float alphaRatio = color.A / 255f;

        DynamicSpriteFont? font = GetDynamicFont(size);
        if (font is not null && _renderer is not null)
        {
            Color ocXna = outlineColor ?? Color.FromNonPremultiplied(16, 36, 62, (int)MathF.Round(215f * alphaRatio));
            FSColor oc = ToFsColor(ocXna);
            var dropShadow = new FSColor((byte)0, (byte)0, (byte)0, (byte)Math.Clamp((int)MathF.Round(150f * alphaRatio), 0, 255));
            FSColor mainCol = ToFsColor(color);

            _renderer.CurrentBatch = spriteBatch;
            try
            {
                font.DrawText(_renderer, text, new System.Numerics.Vector2(fx + 2f, fy + 3f), dropShadow);
                font.DrawText(_renderer, text, new System.Numerics.Vector2(fx - 1f, fy), oc);
                font.DrawText(_renderer, text, new System.Numerics.Vector2(fx + 1f, fy), oc);
                font.DrawText(_renderer, text, new System.Numerics.Vector2(fx, fy - 1f), oc);
                font.DrawText(_renderer, text, new System.Numerics.Vector2(fx, fy + 1f), oc);
                font.DrawText(_renderer, text, new System.Numerics.Vector2(fx, fy), mainCol);
            }
            finally
            {
                _renderer.CurrentBatch = null;
            }

            return;
        }

        _fallbackFont.DrawStringThinOutline(
            spriteBatch,
            text,
            new Vector2(fx, fy),
            color,
            scale: GetFallbackScale(size),
            outlineColor: outlineColor);
    }

    /// <summary>
    /// Menggambar teks tepat di tengah persegi tujuan secara horizontal & vertikal.
    /// </summary>
    public void DrawCentered(
        SpriteBatch spriteBatch,
        string? text,
        Rectangle bounds,
        Color color,
        UiFontSize size = UiFontSize.Normal,
        bool dropShadow = true,
        float offsetY = 0f)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        Vector2 measured = MeasureString(text, size);
        float x = bounds.X + (bounds.Width - measured.X) * 0.5f;
        float y = bounds.Y + (bounds.Height - measured.Y) * 0.5f + offsetY;
        DrawString(spriteBatch, text, new Vector2(x, y), color, size, dropShadow);
    }

    /// <summary>
    /// Membungkus teks secara akurat berdasarkan lebar piksel aktual font yang aktif.
    /// </summary>
    public List<string> WrapText(string? text, float maxWidth, UiFontSize size = UiFontSize.Small)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        if (!IsSmoothFontLoaded)
        {
            return _fallbackFont.WrapText(text, maxWidth, GetFallbackScale(size));
        }

        float safeMaxW = MathF.Max(24f, maxWidth);
        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = new StringBuilder();
            string[] words = paragraph.Split(' ');

            for (int w = 0; w < words.Length; w++)
            {
                string word = words[w];
                if (word.Length == 0)
                {
                    continue;
                }

                while (MeasureString(word, size).X > safeMaxW && word.Length > 1)
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    int fitLen = FindMaxFittingPrefixLength(word, safeMaxW, size);
                    lines.Add(word[..fitLen]);
                    word = word[fitLen..];
                }

                string candidate = current.Length == 0 ? word : $"{current} {word}";
                if (current.Length > 0 && MeasureString(candidate, size).X > safeMaxW)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        return lines;
    }

    /// <summary>
    /// Memotong teks agar muat dalam <paramref name="maxWidth"/> piksel.
    /// Gunakan <paramref name="keepTail"/> = true untuk kotak input agar karakter terbaru selalu terlihat.
    /// </summary>
    public string FitToWidth(string? text, float maxWidth, UiFontSize size = UiFontSize.Normal, bool keepTail = false)
    {
        if (string.IsNullOrEmpty(text) || maxWidth <= 8f)
        {
            return string.Empty;
        }

        if (MeasureString(text, size).X <= maxWidth)
        {
            return text;
        }

        if (keepTail)
        {
            int lo = 0;
            int hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                string sub = text[mid..];
                if (MeasureString(sub, size).X <= maxWidth)
                {
                    hi = mid;
                }
                else
                {
                    lo = mid + 1;
                }
            }

            return lo < text.Length ? text[lo..] : string.Empty;
        }

        int fit = FindMaxFittingPrefixLength(text, maxWidth, size);
        return text[..fit];
    }

    /// <summary>
    /// Membuat warna premultiplied yang aman untuk BlendState.AlphaBlend saat melakukan animasi fade alpha.
    /// </summary>
    public static Color PremultiplyAlpha(Color baseColor, float alpha)
    {
        float clamped = Math.Clamp(alpha, 0f, 1f);
        byte a = (byte)Math.Clamp((int)MathF.Round(baseColor.A * clamped), 0, 255);
        return Color.FromNonPremultiplied(baseColor.R, baseColor.G, baseColor.B, a);
    }

    private int FindMaxFittingPrefixLength(string word, float maxWidth, UiFontSize size)
    {
        int lo = 1;
        int hi = word.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (MeasureString(word[..mid], size).X <= maxWidth)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    private DynamicSpriteFont? GetDynamicFont(UiFontSize size) => size switch
    {
        UiFontSize.Tiny => _tinyFont,
        UiFontSize.Small => _smallFont,
        UiFontSize.Normal => _normalFont,
        UiFontSize.Title => _titleFont,
        UiFontSize.Header => _headerFont,
        _ => _normalFont,
    };

    private static int GetFallbackScale(UiFontSize size) => size switch
    {
        UiFontSize.Tiny => 1,
        UiFontSize.Small => 1,
        UiFontSize.Normal => 1,
        UiFontSize.Title => 2,
        UiFontSize.Header => 3,
        _ => 1,
    };

    private static FSColor ToFsColor(Color c) => new(c.R, c.G, c.B, c.A);

    private static string? FindBestFontPath()
    {
        // 1. Prioritas pertama: font kustom dalam Content/Fonts/ui_bold.ttf
        string[] customCandidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Content", "Fonts", "ui_bold.ttf"),
            Path.Combine(Directory.GetCurrentDirectory(), "Content", "Fonts", "ui_bold.ttf"),
        ];

        for (int i = 0; i < customCandidates.Length; i++)
        {
            if (File.Exists(customCandidates[i]))
            {
                return customCandidates[i];
            }
        }

        // 2. Prioritas kedua: font bold/rounded bawaan Windows
        string[] fontDirs =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Fonts),
            @"C:\Windows\Fonts",
        ];

        string[] systemFileNames = ["segoeuib.ttf", "trebucbd.ttf", "arialbd.ttf"];

        for (int d = 0; d < fontDirs.Length; d++)
        {
            string dir = fontDirs[d];
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            for (int f = 0; f < systemFileNames.Length; f++)
            {
                string fullPath = Path.Combine(dir, systemFileNames[f]);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        // 3. Tidak ditemukan -> fallback otomatis ke PixelFont
        return null;
    }

    public void Dispose()
    {
        _fontSystem?.Dispose();
        _textureManager?.Dispose();
    }

    private sealed class MonoGameTexture2DManager : ITexture2DManager, IDisposable
    {
        private readonly GraphicsDevice _graphicsDevice;
        private readonly List<Texture2D> _createdTextures = new(4);

        public MonoGameTexture2DManager(GraphicsDevice graphicsDevice)
        {
            _graphicsDevice = graphicsDevice;
        }

        public object CreateTexture(int width, int height)
        {
            var texture = new Texture2D(_graphicsDevice, width, height, mipmap: false, SurfaceFormat.Color);
            _createdTextures.Add(texture);
            return texture;
        }

        public System.Drawing.Point GetTextureSize(object texture)
        {
            var tex = (Texture2D)texture;
            return new System.Drawing.Point(tex.Width, tex.Height);
        }

        public void SetTextureData(object texture, System.Drawing.Rectangle bounds, byte[] data)
        {
            var tex = (Texture2D)texture;
            var rect = new Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            tex.SetData(0, rect, data, 0, bounds.Width * bounds.Height * 4);
        }

        public void Dispose()
        {
            for (int i = 0; i < _createdTextures.Count; i++)
            {
                _createdTextures[i].Dispose();
            }

            _createdTextures.Clear();
        }
    }

    private sealed class MonoGameFontRenderer : IFontStashRenderer
    {
        public ITexture2DManager TextureManager { get; }
        public SpriteBatch? CurrentBatch { get; set; }

        public MonoGameFontRenderer(ITexture2DManager textureManager)
        {
            TextureManager = textureManager;
        }

        public void Draw(
            object texture,
            System.Numerics.Vector2 pos,
            System.Drawing.Rectangle? src,
            FSColor color,
            float rotation,
            System.Numerics.Vector2 scale,
            float depth)
        {
            if (CurrentBatch is null)
            {
                return;
            }

            var tex = (Texture2D)texture;
            Rectangle? xnaSrc = src.HasValue
                ? new Rectangle(src.Value.X, src.Value.Y, src.Value.Width, src.Value.Height)
                : null;
            var xnaColor = new Color(color.R, color.G, color.B, color.A);

            CurrentBatch.Draw(
                tex,
                new Vector2(pos.X, pos.Y),
                xnaSrc,
                xnaColor,
                rotation,
                Vector2.Zero,
                new Vector2(scale.X, scale.Y),
                SpriteEffects.None,
                depth);
        }
    }
}
