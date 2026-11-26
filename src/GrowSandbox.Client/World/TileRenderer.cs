using GrowSandbox.Client.Entities;
using GrowSandbox.Client.Graphics;
using GrowSandbox.Client.Network;
using GrowSandbox.Client.UI;
using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using GrowSandbox.Shared.Physics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.World;

/// <summary>
/// Renderer dunia 2.5D dengan 4 pass terpisah:
/// - Pass 1: Gradasi langit vertikal + Matahari (0.05x) + Awan 3 variasi (0.15x + drift) +
///           Bukit jauh (0.25x) + Bukit dekat (0.45x) dengan kepadatan piksel seragam (2x, bebas mixels).
/// - Pass 2: Tile Background (CaveBackground / wallpaper) dengan penggelapan kontras.
/// - Pass 3: Drop Shadow 2.5D semi-transparan (0,0,0,90) digeser (+4px, +4px) di belakang tile Foreground solid.
/// - Pass 4: Tile Foreground dengan grass-cap hanya saat langit terbuka (Fg==0 && Bg==0 di y-1)
///           dan garis tepi gelap 2px pada sisi blok solid yang bertemu udara.
/// </summary>
public sealed class TileRenderer
{
    public static readonly Color SkyFallbackColor = new(92, 192, 246);

    private static readonly Color BackgroundDarkenTint = new(176, 174, 184);
    private static readonly Color DropShadowColor = Color.FromNonPremultiplied(0, 0, 0, 90);
    private static readonly Color SolidEdgeOutline = Color.FromNonPremultiplied(32, 22, 16, 220);
    private static readonly Color SolidTopHighlight = Color.FromNonPremultiplied(255, 255, 255, 42);
    private static readonly Color DropItemHaloTint = Color.FromNonPremultiplied(180, 235, 255, 48);
    private static readonly Color DropGemHaloTint = Color.FromNonPremultiplied(90, 255, 180, 65);
    private static readonly Color DropIconOutlineTint = Color.FromNonPremultiplied(18, 14, 12, 225);

    private const int DropShadowOffsetPx = 4;
    private const float SunParallaxFactor = 0.05f;
    private const float CloudParallaxFactor = 0.15f;
    private const float CloudDriftSpeedPxPerSec = 10f;
    private const float FarHillsParallaxFactor = 0.25f;
    private const float NearHillsParallaxFactor = 0.45f;

    private static readonly (int BaseX, int Y, int Variant)[] CloudLayout =
    [
        (80, 42, 0),
        (330, 88, 1),
        (590, 34, 2),
        (820, 74, 0),
        (1080, 48, 1),
        (1340, 96, 2),
        (1580, 56, 0),
    ];

    private readonly PixelAtlas _atlas;
    private readonly PixelFont _font;
    private readonly UiNineSlice _nineSlice;
    private readonly UiFontManager _uiFonts;
    private readonly List<(int TileIndex, int TileX, int TileY)> _visibleTorches = new(32);

    public int LastDrawnTileCount { get; private set; }

    public TileRenderer(PixelAtlas atlas, PixelFont font, UiNineSlice nineSlice, UiFontManager uiFonts)
    {
        _atlas = atlas;
        _font = font;
        _nineSlice = nineSlice;
        _uiFonts = uiFonts;
    }

    /// <summary>
    /// Aturan grass cap ketat: lapisan rumput hijau pada blok tanah HANYA digambar jika:
    /// 1) y &lt;= WorldConstants.SurfaceY (bukan di dasar lubang galian bawah tanah), DAN
    /// 2) tile tepat di atasnya (y - 1) benar-benar kosong (ForegroundId == 0 &amp;&amp; BackgroundId == 0).
    /// </summary>
    public static bool ShouldDrawGrassCap(ReadOnlySpan<Tile> tiles, int x, int y)
    {
        if ((uint)x >= WorldConstants.Width || (uint)y >= WorldConstants.Height)
        {
            return false;
        }

        if (y > WorldConstants.SurfaceY)
        {
            return false;
        }

        ushort fg = tiles[y * WorldConstants.Width + x].ForegroundId;
        if (fg != TileIds.Dirt && fg != TileIds.GrassDirt)
        {
            return false;
        }

        if (y == 0)
        {
            return true;
        }

        ref readonly Tile above = ref tiles[(y - 1) * WorldConstants.Width + x];
        return above.ForegroundId == TileIds.Air && above.BackgroundId == TileIds.Air;
    }

    /// <summary>
    /// Pass 1 (screen-space): Gradasi langit vertikal halus, Matahari di langit terbuka dengan halo RadialGlowTexture,
    /// 3 variasi awan (0.15x + drift kontinu), lapisan Atmospheric Haze lembut, bukit jauh (0.25x),
    /// dan bukit dekat (0.45x) yang selalu menempel tepat ke garis SurfaceY setelah transformasi kamera + zoom.
    /// </summary>
    public void DrawPass1SkyAndParallax(SpriteBatch spriteBatch, Viewport viewport, Camera2D camera, float elapsedTime)
    {
        // 1. Gradasi langit vertikal halus dari atas ke cakrawala.
        spriteBatch.Draw(
            _atlas.SkyGradient,
            new Rectangle(0, 0, viewport.Width, viewport.Height),
            Color.White);

        // Skala latar yang tetap tajam & proporsional di semua level zoom (1.0x..2.5x).
        float hillScale = PixelAtlas.TexelScale * camera.Zoom;
        float skyPropScale = PixelAtlas.TexelScale * Math.Clamp(0.75f + camera.Zoom * 0.25f, 0.95f, 1.40f);
        float worldCenterX = WorldConstants.WorldPixelWidth * 0.5f;
        float camOffsetX = camera.Position.X - worldCenterX;

        // 1a. Matahari di area langit terbuka (~72% lebar layar, ~17% dari atas, aman dari panel HUD kanan atas)
        //     dilengkapi halo cahaya hangat lembut memakai RadialGlowTexture.
        int sunW = (int)MathF.Round(_atlas.Sun.Width * skyPropScale);
        int sunH = (int)MathF.Round(_atlas.Sun.Height * skyPropScale);
        float rawSunCenterX = viewport.Width * 0.72f - camOffsetX * SunParallaxFactor;
        float maxSafeSunCenterX = MathF.Max(viewport.Width * 0.55f, viewport.Width - 265f);
        float sunCenterX = Math.Clamp(rawSunCenterX, viewport.Width * 0.52f, maxSafeSunCenterX);
        float sunCenterY = MathF.Max(88f, viewport.Height * 0.17f);

        int outerHaloSize = (int)MathF.Round(sunW * 3.1f);
        int innerHaloSize = (int)MathF.Round(sunW * 1.9f);
        spriteBatch.Draw(
            _atlas.RadialGlowTexture,
            new Rectangle(
                (int)MathF.Round(sunCenterX - outerHaloSize * 0.5f),
                (int)MathF.Round(sunCenterY - outerHaloSize * 0.5f),
                outerHaloSize,
                outerHaloSize),
            Color.FromNonPremultiplied(255, 244, 184, 88));
        spriteBatch.Draw(
            _atlas.RadialGlowTexture,
            new Rectangle(
                (int)MathF.Round(sunCenterX - innerHaloSize * 0.5f),
                (int)MathF.Round(sunCenterY - innerHaloSize * 0.5f),
                innerHaloSize,
                innerHaloSize),
            Color.FromNonPremultiplied(255, 232, 128, 76));

        spriteBatch.Draw(
            _atlas.Sun,
            new Rectangle(
                (int)MathF.Round(sunCenterX - sunW * 0.5f),
                (int)MathF.Round(sunCenterY - sunH * 0.5f),
                sunW,
                sunH),
            Color.White);

        // 1b. Awan 3 variasi (parallax 0.15x + bergerak pelan ke kanan secara kontinu dengan wrapping otomatis)
        float wrapSpan = MathF.Max(viewport.Width + 320f, 1760f);
        for (int i = 0; i < CloudLayout.Length; i++)
        {
            (int baseX, int y, int variant) = CloudLayout[i];
            Texture2D cloudTex = _atlas.CloudVariants[variant % _atlas.CloudVariants.Length];
            int cw = (int)MathF.Round(cloudTex.Width * skyPropScale);
            int ch = (int)MathF.Round(cloudTex.Height * skyPropScale);

            float rawX = baseX + elapsedTime * CloudDriftSpeedPxPerSec - camera.Position.X * CloudParallaxFactor;
            float wrappedX = ((rawX % wrapSpan) + wrapSpan) % wrapSpan - cw;

            spriteBatch.Draw(cloudTex, new Rectangle((int)MathF.Round(wrappedX), y, cw, ch), Color.White);
        }

        // Hitung posisi layar tepat garis permukaan tanah (WorldConstants.SurfaceY) menggunakan transformasi kamera + zoom.
        float surfaceWorldY = WorldConstants.SurfaceY * WorldConstants.TileSize;
        float surfaceScreenY = camera.WorldToScreen(new Vector2(camera.Position.X, surfaceWorldY)).Y;

        if (surfaceScreenY > -260f)
        {
            int horizonAnchorY = (int)MathF.Round(surfaceScreenY + 12f * camera.Zoom);

            // 1c. Lapisan Atmospheric Haze belakang (memudar dari garis cakrawala ke atas di belakang bukit jauh)
            int backHazeH = (int)MathF.Round(135f * camera.Zoom);
            spriteBatch.Draw(
                _atlas.AtmosphericHaze,
                new Rectangle(0, horizonAnchorY - backHazeH, viewport.Width, backHazeH),
                Color.White * 0.85f);

            // 1d. Bukit jauh hijau kebiruan pudar (parallax 0.25x), menempel erat pada SurfaceY
            int farW = (int)MathF.Round(_atlas.HillsFar.Width * hillScale);
            int farH = (int)MathF.Round(_atlas.HillsFar.Height * hillScale);
            int farTopY = (int)MathF.Round(surfaceScreenY - farH + 14f * camera.Zoom);
            DrawHorizontalParallaxStrip(spriteBatch, _atlas.HillsFar, viewport.Width, farTopY, farW, farH, camera.Position.X * FarHillsParallaxFactor);

            // 1e. Lapisan Atmospheric Haze tengah (di antara bukit jauh & bukit dekat untuk kedalaman udara alami)
            int midHazeH = (int)MathF.Round(96f * camera.Zoom);
            spriteBatch.Draw(
                _atlas.AtmosphericHaze,
                new Rectangle(0, horizonAnchorY - midHazeH, viewport.Width, midHazeH),
                Color.White);

            // 1f. Bukit dekat hijau lebih cerah (parallax 0.45x), menempel rapat pada SurfaceY
            int nearW = (int)MathF.Round(_atlas.HillsNear.Width * hillScale);
            int nearH = (int)MathF.Round(_atlas.HillsNear.Height * hillScale);
            int nearTopY = (int)MathF.Round(surfaceScreenY - nearH + 8f * camera.Zoom);
            DrawHorizontalParallaxStrip(spriteBatch, _atlas.HillsNear, viewport.Width, nearTopY, nearW, nearH, camera.Position.X * NearHillsParallaxFactor);

            // 1g. Kabut horizon tipis di depan bukit dekat agar foreground blok & karakter makin menonjol
            int frontHazeH = (int)MathF.Round(52f * camera.Zoom);
            spriteBatch.Draw(
                _atlas.AtmosphericHaze,
                new Rectangle(0, horizonAnchorY - frontHazeH, viewport.Width, frontHazeH),
                Color.White * 0.45f);
        }
    }

    private static void DrawHorizontalParallaxStrip(
        SpriteBatch spriteBatch,
        Texture2D texture,
        int viewportWidth,
        int topY,
        int stripW,
        int stripH,
        float parallaxOffsetX)
    {
        if (stripW <= 0 || stripH <= 0)
        {
            return;
        }

        int offset = (int)MathF.Floor(parallaxOffsetX) % stripW;
        if (offset < 0) offset += stripW;

        for (int x = -offset; x < viewportWidth + stripW; x += stripW)
        {
            spriteBatch.Draw(texture, new Rectangle(x, topY, stripW, stripH), Color.White);
        }
    }

    /// <summary>
    /// Menjalankan Pass 2 (Background), Pass 3 (Drop Shadow 2.5D), dan Pass 4 (Foreground + Outline + Crack + Tile Shake/Hit-Flash)
    /// dalam satu siklus world-space menggunakan frustum culling viewport kamera.
    /// </summary>
    public void DrawWorldPasses2To4(
        SpriteBatch spriteBatch,
        Tile[] tiles,
        IReadOnlyDictionary<int, TreeData> trees,
        Camera2D camera,
        TileFeedbackTracker feedbackTracker,
        float elapsedTime,
        long utcNowTicks)
    {
        _visibleTorches.Clear();

        if (tiles.Length != WorldConstants.TotalTiles)
        {
            LastDrawnTileCount = 0;
            return;
        }

        int tileSize = WorldConstants.TileSize;
        // Frustum Culling dinamis langsung dari sudut kiri-atas & kanan-bawah viewport hasil transformasi kamera (+ margin 2 tile)
        (int minX, int maxX, int minY, int maxY) = camera.GetVisibleTileRange(marginTiles: 2);

        if (minX > maxX || minY > maxY)
        {
            LastDrawnTileCount = 0;
            return;
        }

        int drawn = 0;
        bool hasFeedback = feedbackTracker.HasActiveEffects;

        // =========================================================================
        // PASS 2: Tile Background (CaveBackground / WoodBackground) dengan penggelapan kontras
        // =========================================================================
        for (int y = minY; y <= maxY; y++)
        {
            int rowOffset = y * WorldConstants.Width;
            int pixelY = y * tileSize;

            for (int x = minX; x <= maxX; x++)
            {
                int tileIndex = rowOffset + x;
                ref readonly Tile tile = ref tiles[tileIndex];
                if (tile.BackgroundId == TileIds.Air)
                {
                    continue;
                }

                if (IsOpaqueSolidTileId(tile.ForegroundId))
                {
                    continue;
                }

                int drawX = x * tileSize;
                int drawY = pixelY;
                byte flashAlpha = 0;
                if (hasFeedback && tile.ForegroundId == TileIds.Air &&
                    feedbackTracker.TryGetFeedback(tileIndex, out int sx, out int sy, out flashAlpha))
                {
                    drawX += sx;
                    drawY += sy;
                }

                var bgDest = new Rectangle(drawX, drawY, tileSize, tileSize);
                Texture2D bgTex = _atlas.GetTileTexture(tile.BackgroundId, 0, elapsedTime);
                Color bgTint = ComputeBackgroundDepthTint(y);
                spriteBatch.Draw(bgTex, bgDest, bgTint);

                if (flashAlpha > 0)
                {
                    spriteBatch.Draw(_atlas.Pixel, bgDest, Color.FromNonPremultiplied(255, 255, 255, flashAlpha));
                }

                drawn++;
            }
        }

        // =========================================================================
        // PASS 3: Drop Shadow 2.5D (+4px, +4px) untuk setiap tile Foreground solid
        // (mengecualikan prop non-solid seperti WhiteDoor, Torch, TreeSapling)
        // =========================================================================
        int shadowMinX = Math.Max(0, minX - 1);
        int shadowMinY = Math.Max(0, minY - 1);

        for (int y = shadowMinY; y <= maxY; y++)
        {
            int rowOffset = y * WorldConstants.Width;
            int shadowY = y * tileSize + DropShadowOffsetPx;

            for (int x = shadowMinX; x <= maxX; x++)
            {
                ushort fgId = tiles[rowOffset + x].ForegroundId;
                if (fgId == TileIds.Air || !ItemCatalog.IsSolid(fgId))
                {
                    continue;
                }

                var shadowRect = new Rectangle(x * tileSize + DropShadowOffsetPx, shadowY, tileSize, tileSize);
                spriteBatch.Draw(_atlas.Pixel, shadowRect, DropShadowColor);
            }
        }

        // =========================================================================
        // PASS 4: Tile Foreground + Grass Cap Langit Terbuka + Garis Tepi Gelap 2px
        //         + Crack Overlay 4 Tahap (Damage/Hardness) + Tile Shake & Hit-Flash
        // =========================================================================
        for (int y = minY; y <= maxY; y++)
        {
            int rowOffset = y * WorldConstants.Width;
            int pixelY = y * tileSize;

            for (int x = minX; x <= maxX; x++)
            {
                int tileIndex = rowOffset + x;
                ref readonly Tile tile = ref tiles[tileIndex];

                if (tile.ForegroundId == TileIds.Air)
                {
                    if (tile.Damage > 0 && tile.BackgroundId != TileIds.Air)
                    {
                        int bx = x * tileSize;
                        int by = pixelY;
                        if (hasFeedback && feedbackTracker.TryGetFeedback(tileIndex, out int sx, out int sy, out _))
                        {
                            bx += sx;
                            by += sy;
                        }

                        int bgCrackIdx = ResolveCrackStageIndex(tile.Damage, tile.BackgroundId);
                        spriteBatch.Draw(_atlas.CrackOverlays[bgCrackIdx], new Rectangle(bx, by, tileSize, tileSize), Color.White);
                    }

                    continue;
                }

                int drawX = x * tileSize;
                int drawY = pixelY;
                byte flashAlpha = 0;

                if (hasFeedback && feedbackTracker.TryGetFeedback(tileIndex, out int shakeX, out int shakeY, out flashAlpha))
                {
                    drawX += shakeX;
                    drawY += shakeY;
                }

                var dest = new Rectangle(drawX, drawY, tileSize, tileSize);

                if (tile.ForegroundId == TileIds.Torch)
                {
                    // Kumpulkan koordinat obor untuk Pass Pencahayaan Additive terpisah (tanpa kotak kuning solid)
                    _visibleTorches.Add((tileIndex, x, y));
                }
                else if (tile.ForegroundId == TileIds.WorldLock)
                {
                    // Aura kilau emas halus pada tile World Lock (premultiplied RadialGlowTexture, bebas sudut kotak)
                    float shimmer = 0.5f + 0.5f * MathF.Sin(elapsedTime * 3.2f + x * 0.7f);
                    int auraSize = (int)MathF.Round(tileSize * (2.05f + 0.16f * shimmer));
                    var auraRect = new Rectangle(
                        dest.X + tileSize / 2 - auraSize / 2,
                        dest.Y + tileSize / 2 - auraSize / 2,
                        auraSize,
                        auraSize);
                    byte auraAlpha = (byte)(36 + (int)(shimmer * 26f));
                    spriteBatch.Draw(_atlas.RadialGlowTexture, auraRect, Color.FromNonPremultiplied(255, 222, 86, auraAlpha));
                }

                // Aturan grass cap ketat: hanya bila y <= SurfaceY DAN y-1 kosong (Fg==0 && Bg==0).
                ushort visualFgId = tile.ForegroundId;
                if (visualFgId == TileIds.Dirt || visualFgId == TileIds.GrassDirt)
                {
                    visualFgId = ShouldDrawGrassCap(tiles, x, y) ? TileIds.GrassDirt : TileIds.Dirt;
                }

                // 4-bit Auto-Tiling (Atas=+1, Kanan=+2, Bawah=+4, Kiri=+8) untuk blok terrain & bangunan solid
                if (visualFgId == TileIds.Tree)
                {
                    bool hasTree = trees.TryGetValue(tileIndex, out TreeData treeData);
                    ushort seedId = hasTree
                        ? treeData.SeedId
                        : (ItemCatalog.IsSeed(tile.ExtraDataId) ? tile.ExtraDataId : TileIds.WoodSeed);
                    int growthStage = hasTree ? treeData.GetGrowthStage(utcNowTicks) : 0;
                    Texture2D treeTex = _atlas.GetTreeStageTexture(seedId, growthStage);

                    if (growthStage >= 3)
                    {
                        // Pohon matang (100% / Ready): kilau lembut RadialGlowTexture + goyang pelan + bintang buah berkedip
                        float shimmer = 0.55f + 0.45f * MathF.Sin(elapsedTime * 4.2f + x * 1.1f);
                        byte glowAlpha = (byte)(34 + (int)(shimmer * 24f));
                        Color glowTint = ItemCatalog.TryGetSeedProperties(seedId, out SeedProperties sp)
                            ? Color.FromNonPremultiplied(sp.PrimaryColor.R, sp.PrimaryColor.G, sp.PrimaryColor.B, glowAlpha)
                            : Color.FromNonPremultiplied(255, 230, 112, glowAlpha);
                        int glowSize = (int)MathF.Round(tileSize * 1.8f);
                        spriteBatch.Draw(
                            _atlas.RadialGlowTexture,
                            new Rectangle(dest.X + tileSize / 2 - glowSize / 2, dest.Y + tileSize / 2 - glowSize / 2 - 2, glowSize, glowSize),
                            glowTint);

                        int swayX = (int)MathF.Round(MathF.Sin(elapsedTime * 2.4f + x) * 1.2f);
                        spriteBatch.Draw(treeTex, new Rectangle(dest.X + swayX, dest.Y - 2, tileSize, tileSize + 2), Color.White);

                        float twinkle = MathF.Sin(elapsedTime * 6.0f + x * 1.7f + y);
                        if (twinkle > 0.15f)
                        {
                            byte spAlpha = (byte)(Math.Clamp((twinkle - 0.15f) / 0.85f, 0f, 1f) * 235f);
                            int spSize = twinkle > 0.7f ? 10 : 8;
                            spriteBatch.Draw(
                                _atlas.GemSparkle,
                                new Rectangle(dest.X + swayX + tileSize - spSize - 2, dest.Y + 2, spSize, spSize),
                                Color.FromNonPremultiplied(255, 250, 176, spAlpha));
                        }
                    }
                    else
                    {
                        spriteBatch.Draw(treeTex, dest, Color.White);
                    }

                    drawn++;
                    continue;
                }

                Texture2D fgTex;
                int bitmask4 = 0;
                bool isAutoTiled = _atlas.HasAutoTileVariants(visualFgId);
                if (isAutoTiled)
                {
                    bitmask4 = Compute4BitMask(tiles, x, y);
                    int variant = ((x * 73856093) ^ (y * 19349663)) & 3;
                    fgTex = _atlas.GetAutoTileTexture(visualFgId, bitmask4, variant);
                }
                else
                {
                    fgTex = _atlas.GetTileTexture(visualFgId, tile.ExtraDataId, elapsedTime);
                }

                // Depth Shading (1.0 di permukaan -> 0.72 dekat bedrock) + Ambient Occlusion (0.90 bila terkurung penuh bitmask==15)
                Color fgTint = ComputeForegroundShadeTint(visualFgId, y, isAutoTiled, bitmask4);
                spriteBatch.Draw(fgTex, dest, fgTint);

                // Kilau bintang emas berkedip pelan di pojok atas blok World Lock
                if (tile.ForegroundId == TileIds.WorldLock)
                {
                    float twinkle = MathF.Sin(elapsedTime * 5.2f + x * 1.3f + y);
                    if (twinkle > 0.1f)
                    {
                        byte spAlpha = (byte)(Math.Clamp((twinkle - 0.1f) / 0.9f, 0f, 1f) * 235f);
                        int spSize = twinkle > 0.7f ? 12 : 10;
                        spriteBatch.Draw(
                            _atlas.GemSparkle,
                            new Rectangle(dest.Right - spSize + 2, dest.Y - 2, spSize, spSize),
                            Color.FromNonPremultiplied(255, 242, 146, spAlpha));
                    }
                }

                // Garis tepi gelap cadangan hanya untuk blok solid non-autotiled (blok autotiled sudah membulat + ber-outline).
                if (!isAutoTiled && IsOpaqueSolidTileId(tile.ForegroundId))
                {
                    if (!IsOpaqueSolidAt(tiles, x, y - 1))
                    {
                        spriteBatch.Draw(_atlas.Pixel, new Rectangle(dest.X, dest.Y, tileSize, 2), SolidEdgeOutline);
                        spriteBatch.Draw(_atlas.Pixel, new Rectangle(dest.X, dest.Y + 2, tileSize, 2), SolidTopHighlight);
                    }

                    if (!IsOpaqueSolidAt(tiles, x, y + 1))
                    {
                        spriteBatch.Draw(_atlas.Pixel, new Rectangle(dest.X, dest.Bottom - 2, tileSize, 2), SolidEdgeOutline);
                    }

                    if (!IsOpaqueSolidAt(tiles, x - 1, y))
                    {
                        spriteBatch.Draw(_atlas.Pixel, new Rectangle(dest.X, dest.Y, 2, tileSize), SolidEdgeOutline);
                    }

                    if (!IsOpaqueSolidAt(tiles, x + 1, y))
                    {
                        spriteBatch.Draw(_atlas.Pixel, new Rectangle(dest.Right - 2, dest.Y, 2, tileSize), SolidEdgeOutline);
                    }
                }

                // Crack overlay 4 tahap proporsional terhadap rasio Damage / Hardness
                if (tile.Damage > 0)
                {
                    int crackIdx = ResolveCrackStageIndex(tile.Damage, tile.ForegroundId);
                    spriteBatch.Draw(_atlas.CrackOverlays[crackIdx], dest, Color.White);
                }

                // Hit-flash putih tipis (premultiplied alpha ~70 selama 50ms) saat blok baru terkena pukulan
                if (flashAlpha > 0)
                {
                    spriteBatch.Draw(_atlas.Pixel, dest, Color.FromNonPremultiplied(255, 255, 255, flashAlpha));
                }

                drawn++;
            }
        }

        LastDrawnTileCount = drawn;
    }

    /// <summary>
    /// Pass Pencahayaan Terpisah (wajib dipanggil dalam SpriteBatch dengan BlendState.Additive &amp; SamplerState.LinearClamp):
    /// menggambar cahaya hangat RadialGlowTexture beradius ~2.8 tile pada setiap Obor dengan kelap-kelip halus.
    /// </summary>
    public void DrawLightingPass(SpriteBatch spriteBatch, float elapsedTime)
    {
        if (_visibleTorches.Count == 0)
        {
            return;
        }

        int tileSize = WorldConstants.TileSize;
        for (int i = 0; i < _visibleTorches.Count; i++)
        {
            (int tileIndex, int tx, int ty) = _visibleTorches[i];

            float flicker = MathF.Sin(elapsedTime * 5.5f + tileIndex);
            float radiusTiles = 2.8f * (1f + 0.05f * flicker);
            int diameterPx = (int)MathF.Round(radiusTiles * 2f * tileSize);

            int centerX = tx * tileSize + tileSize / 2;
            int centerY = ty * tileSize + tileSize / 2 - 4;

            byte alpha = (byte)Math.Clamp((int)MathF.Round(110f + 15f * flicker), 88, 132);
            var warmGlow = new Color((byte)255, (byte)195, (byte)95, alpha);

            spriteBatch.Draw(
                _atlas.RadialGlowTexture,
                new Rectangle(centerX - diameterPx / 2, centerY - diameterPx / 2, diameterPx, diameterPx),
                warmGlow);
        }
    }

    private static Color ComputeBackgroundDepthTint(int y)
    {
        if (y <= WorldConstants.SurfaceY)
        {
            return BackgroundDarkenTint;
        }

        float depthSpan = MathF.Max(1f, (WorldConstants.Height - 5) - WorldConstants.SurfaceY);
        float t = Math.Clamp((y - WorldConstants.SurfaceY) / depthSpan, 0f, 1f);
        float depthFactor = MathHelper.Lerp(1.0f, 0.78f, t);

        return new Color(
            (byte)Math.Clamp((int)MathF.Round(BackgroundDarkenTint.R * depthFactor), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(BackgroundDarkenTint.G * depthFactor), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(BackgroundDarkenTint.B * depthFactor), 0, 255));
    }

    /// <summary>
    /// Depth Shading &amp; Ambient Occlusion ringan untuk tile foreground:
    /// - Semakin dalam di bawah SurfaceY, kecerahan turun halus dari 1.0f hingga minimal 0.72f di dekat bedrock.
    /// - Blok terrain yang tertutup blok solid di keempat sisinya (bitmask == 15) diredupkan 0.90f agar tepian galian menonjol.
    /// </summary>
    private static Color ComputeForegroundShadeTint(ushort visualFgId, int y, bool isAutoTiled, int bitmask4)
    {
        // Prop bercahaya / pintu spawn / kaca tetap terang penuh
        if (visualFgId is TileIds.Torch or TileIds.WorldLock or TileIds.WhiteDoor or TileIds.Spawn or TileIds.Glass or TileIds.Tree)
        {
            return Color.White;
        }

        float depthShade = 1.0f;
        if (y > WorldConstants.SurfaceY)
        {
            float depthSpan = MathF.Max(1f, (WorldConstants.Height - 5) - WorldConstants.SurfaceY);
            float t = Math.Clamp((y - WorldConstants.SurfaceY) / depthSpan, 0f, 1f);
            depthShade = MathHelper.Lerp(1.0f, 0.72f, t);
        }

        float aoShade = 1.0f;
        if (isAutoTiled && bitmask4 == 15 &&
            visualFgId is TileIds.Dirt or TileIds.GrassDirt or TileIds.Stone)
        {
            aoShade = 0.90f;
        }

        float combined = depthShade * aoShade;
        if (combined >= 0.995f)
        {
            return Color.White;
        }

        byte v = (byte)Math.Clamp((int)MathF.Round(combined * 255f), 0, 255);
        return new Color(v, v, v);
    }

    private int ResolveCrackStageIndex(byte damage, ushort tileId)
    {
        int hardness = Math.Max(1, (int)ItemCatalog.GetBreakHits(tileId));
        int maxIdx = _atlas.CrackOverlays.Length - 1;
        if (hardness > 1 && damage >= hardness - 1)
        {
            return maxIdx; // Tahap 4: hampir pecah pada pukulan terakhir
        }

        float ratio = Math.Clamp((float)damage / hardness, 0f, 1f);
        return Math.Clamp((int)MathF.Floor(ratio * _atlas.CrackOverlays.Length), 0, maxIdx - 1);
    }

    /// <summary>
    /// Menghitung bitmask 4-bit tetangga ortogonal untuk auto-tiling:
    /// Atas (+1), Kanan (+2), Bawah (+4), Kiri (+8).
    /// </summary>
    public static int Compute4BitMask(ReadOnlySpan<Tile> tiles, int x, int y)
    {
        int mask = 0;
        if (IsSolidNeighbor(tiles, x, y - 1)) mask |= 1; // Atas
        if (IsSolidNeighbor(tiles, x + 1, y)) mask |= 2; // Kanan
        if (IsSolidNeighbor(tiles, x, y + 1)) mask |= 4; // Bawah
        if (IsSolidNeighbor(tiles, x - 1, y)) mask |= 8; // Kiri
        return mask;
    }

    private static bool IsSolidNeighbor(ReadOnlySpan<Tile> tiles, int x, int y)
    {
        if ((uint)x >= WorldConstants.Width || (uint)y >= WorldConstants.Height)
        {
            return true;
        }

        return IsOpaqueSolidTileId(tiles[y * WorldConstants.Width + x].ForegroundId);
    }

    private static bool IsOpaqueSolidTileId(ushort fgId) =>
        fgId != TileIds.Air && fgId != TileIds.Glass && fgId != TileIds.WorldLock && ItemCatalog.IsSolid(fgId);

    private static bool IsOpaqueSolidAt(Tile[] tiles, int x, int y)
    {
        if ((uint)x >= WorldConstants.Width || (uint)y >= WorldConstants.Height)
        {
            return true;
        }

        return IsOpaqueSolidTileId(tiles[y * WorldConstants.Width + x].ForegroundId);
    }

    public void DrawDrops(SpriteBatch spriteBatch, IReadOnlyDictionary<uint, WorldDropView> drops, float elapsedTime)
    {
        foreach (WorldDropView drop in drops.Values)
        {
            float sine = MathF.Sin(elapsedTime * 4.5f + drop.SpawnPhase);
            float bobY = sine * 3.5f;
            const int iconSize = 18;
            int centerX = (int)MathF.Floor(drop.Position.X);
            int centerY = (int)MathF.Floor(drop.Position.Y);
            int px = centerX - iconSize / 2;
            int py = (int)MathF.Floor(drop.Position.Y - iconSize * 0.5f + bobY);

            // 1. Bayangan elips tipis di lantai bawah item (mengecil halus saat item melayang naik)
            float shadowScale = 1f - (sine * 0.14f);
            int shadowW = (int)MathF.Round(16f * shadowScale);
            int shadowH = 5;
            float shadowAlphaNorm = (170f - sine * 35f) / 255f;
            spriteBatch.Draw(
                _atlas.DropShadowEllipse,
                new Rectangle(centerX - shadowW / 2, centerY + 10, shadowW, shadowH),
                Color.White * shadowAlphaNorm);

            // 2. Lingkaran halo sangat lembut (~24x24 px) memakai RadialGlowTexture tanpa kotak latar putih/biru
            float haloPulse = 0.92f + 0.08f * MathF.Sin(elapsedTime * 3.8f + drop.SpawnPhase);
            int haloSize = (int)MathF.Round(24f * haloPulse);
            var haloRect = new Rectangle(
                centerX - haloSize / 2,
                py + iconSize / 2 - haloSize / 2,
                haloSize,
                haloSize);

            if (drop.ItemId == TileIds.Gem)
            {
                spriteBatch.Draw(_atlas.RadialGlowTexture, haloRect, DropGemHaloTint);
                DrawOutlinedDropIcon(spriteBatch, _atlas.GemIcon, px, py, iconSize);

                // Kilau bintang kecil berkedip di sudut berlian Gem
                float twinkle = MathF.Sin(elapsedTime * 7.5f + drop.SpawnPhase * 2.1f);
                if (twinkle > 0.15f)
                {
                    byte sparkleAlpha = (byte)(Math.Clamp((twinkle - 0.15f) / 0.85f, 0f, 1f) * 240);
                    int spSize = twinkle > 0.7f ? 10 : 8;
                    spriteBatch.Draw(
                        _atlas.GemSparkle,
                        new Rectangle(px + iconSize - spSize + 2, py - 2, spSize, spSize),
                        Color.FromNonPremultiplied(255, 255, 255, sparkleAlpha));
                }
            }
            else
            {
                spriteBatch.Draw(_atlas.RadialGlowTexture, haloRect, DropItemHaloTint);

                Texture2D icon = _atlas.GetItemIcon(drop.ItemId);
                DrawOutlinedDropIcon(spriteBatch, icon, px, py, iconSize);
            }

            // 3. Badge angka jumlah kecil yang tajam di sudut kanan bawah jika Count > 1
            if (drop.Count > 1)
            {
                string countText = drop.Count.ToString();
                Vector2 textSize = _font.MeasureString(countText, scale: 1);
                int badgeW = (int)MathF.Ceiling(textSize.X) + 5;
                int badgeH = (int)MathF.Ceiling(textSize.Y) + 3;
                int badgeX = px + iconSize + 3 - badgeW;
                int badgeY = py + iconSize + 3 - badgeH;

                UiDraw.Panel(
                    spriteBatch,
                    _atlas.Pixel,
                    new Rectangle(badgeX, badgeY, badgeW, badgeH),
                    fill: Color.FromNonPremultiplied(14, 24, 38, 222),
                    border: Color.FromNonPremultiplied(255, 224, 102, 220),
                    thickness: 1,
                    radius: 2);

                _font.DrawString(
                    spriteBatch,
                    countText,
                    new Vector2(badgeX + 3, badgeY + 2),
                    new Color(255, 242, 128),
                    scale: 1,
                    dropShadow: true);
            }
        }
    }

    private static void DrawOutlinedDropIcon(SpriteBatch spriteBatch, Texture2D icon, int x, int y, int size)
    {
        // Bingkai outline gelap 1px mengikuti kontur ikon agar tajam tanpa latar kotak besar
        spriteBatch.Draw(icon, new Rectangle(x - 1, y, size, size), DropIconOutlineTint);
        spriteBatch.Draw(icon, new Rectangle(x + 1, y, size, size), DropIconOutlineTint);
        spriteBatch.Draw(icon, new Rectangle(x, y - 1, size, size), DropIconOutlineTint);
        spriteBatch.Draw(icon, new Rectangle(x, y + 1, size, size), DropIconOutlineTint);
        spriteBatch.Draw(icon, new Rectangle(x, y, size, size), Color.White);
    }

    /// <summary>
    /// Smart contextual cursor (pengganti kotak merah wireframe):
    /// - Fist / Wrench: 4 sudut fokus (corner brackets 5px) berdenyut pelan HANYA pada tile yang bisa diinteraksi dalam jangkauan.
    /// - Blok placeable: ghost preview semi-transparan (Alpha ~115) + sudut cyan saat valid, atau ghost kemerahan tipis (Alpha ~70) tanpa garis kotak kasar saat tidak valid.
    /// </summary>
    public void DrawSmartCursor(
        SpriteBatch spriteBatch,
        Tile[] tiles,
        int tileX,
        int tileY,
        bool inReach,
        ActiveToolKind activeTool,
        ushort selectedItemId,
        PlayerController localPlayer,
        GameClient client,
        float elapsedTime)
    {
        if (!Tile.IsInBounds(tileX, tileY) || tiles.Length != WorldConstants.TotalTiles)
        {
            return;
        }

        int tileSize = WorldConstants.TileSize;
        int px = tileX * tileSize;
        int py = tileY * tileSize;
        ref readonly Tile targetTile = ref tiles[Tile.GetIndex(tileX, tileY)];

        float pulse = 0.5f + 0.5f * MathF.Sin(elapsedTime * 5.2f);

        if (activeTool == ActiveToolKind.Fist || activeTool == ActiveToolKind.Wrench)
        {
            if (!inReach)
            {
                return;
            }

            bool canInteract = activeTool == ActiveToolKind.Wrench
                ? !targetTile.IsEmpty
                : CanPunchTile(in targetTile);

            if (!canInteract)
            {
                return;
            }

            // 4 sudut fokus (5px) putih/emas lembut (Fist) atau putih/cyan (Wrench) dengan denyut pelan
            Color baseCol = activeTool == ActiveToolKind.Wrench
                ? Color.Lerp(Color.White, new Color(132, 234, 255), pulse)
                : Color.Lerp(Color.White, new Color(255, 224, 112), pulse);
            byte alpha = (byte)(185 + (int)(pulse * 65f));
            var bracketColor = new Color(baseCol.R, baseCol.G, baseCol.B, alpha);

            int breathe = pulse > 0.65f ? -1 : 0;
            DrawCornerBrackets(spriteBatch, px + breathe, py + breathe, tileSize - breathe * 2, armLength: 5, thickness: 2, bracketColor);
            return;
        }

        // Memegang blok/item yang bisa dipasang -> Ghost Placement Preview
        if (!ItemCatalog.IsPlaceable(selectedItemId))
        {
            return;
        }

        bool validPlacement = inReach && CanPlaceItemAt(tiles, tileX, tileY, selectedItemId, localPlayer, client);
        Texture2D previewTex = ItemCatalog.IsSeed(selectedItemId)
            ? _atlas.GetTreeStageTexture(selectedItemId, stage: 0)
            : _atlas.GetTileTexture(selectedItemId, 0, elapsedTime);
        var dest = new Rectangle(px, py, tileSize, tileSize);

        if (validPlacement)
        {
            // Ghost preview semi-transparan (Alpha ~115) + 4 sudut cyan lembut
            spriteBatch.Draw(previewTex, dest, Color.FromNonPremultiplied(255, 255, 255, 115));
            byte cyanAlpha = (byte)(170 + (int)(pulse * 60f));
            DrawCornerBrackets(spriteBatch, px, py, tileSize, armLength: 5, thickness: 2, Color.FromNonPremultiplied(118, 234, 255, cyanAlpha));
        }
        else
        {
            // Target tidak valid (menabrak karakter, terisi, atau di luar jangkauan): ghost kemerahan tipis (Alpha ~70) tanpa garis kotak kasar
            spriteBatch.Draw(previewTex, dest, Color.FromNonPremultiplied(255, 96, 96, 70));
        }
    }

    private static bool CanPunchTile(in Tile tile)
    {
        if (tile.ForegroundId != TileIds.Air)
        {
            if (tile.ForegroundId == TileIds.Bedrock ||
                tile.ForegroundId == TileIds.WhiteDoor ||
                tile.ForegroundId == TileIds.Spawn)
            {
                return false;
            }

            return !tile.Flags.HasFlag(TileFlags.Locked) || tile.ForegroundId == TileIds.WorldLock;
        }

        return tile.BackgroundId != TileIds.Air && !tile.Flags.HasFlag(TileFlags.Locked);
    }

    private static bool CanPlaceItemAt(
        Tile[] tiles,
        int tileX,
        int tileY,
        ushort itemId,
        PlayerController localPlayer,
        GameClient client)
    {
        ref readonly Tile tile = ref tiles[Tile.GetIndex(tileX, tileY)];
        if (tile.Flags.HasFlag(TileFlags.Locked))
        {
            return false;
        }

        if (ItemCatalog.IsBackgroundItem(itemId))
        {
            return tile.BackgroundId == TileIds.Air;
        }

        if (tile.ForegroundId != TileIds.Air)
        {
            return false;
        }

        if (itemId == TileIds.WorldLock)
        {
            if (client.IsWorldLocked)
            {
                return false;
            }

            if (tileY + 1 < WorldConstants.Height)
            {
                ushort belowFg = tiles[Tile.GetIndex(tileX, tileY + 1)].ForegroundId;
                if (belowFg is TileIds.WhiteDoor or TileIds.Spawn or TileIds.Bedrock)
                {
                    return false;
                }
            }
        }

        if (ItemCatalog.IsSeed(itemId))
        {
            if (tileY + 1 >= WorldConstants.Height ||
                !TileCollision.IsSolidTile(in tiles[Tile.GetIndex(tileX, tileY + 1)]))
            {
                return false;
            }
        }

        if (ItemCatalog.IsSolid(itemId))
        {
            if (TileCollision.IntersectsTile(
                    localPlayer.PositionX,
                    localPlayer.PositionY,
                    PhysicsConstants.PlayerWidth,
                    PhysicsConstants.PlayerHeight,
                    tileX,
                    tileY))
            {
                return false;
            }

            foreach (RemotePlayerState remote in client.RemotePlayers.Values)
            {
                if (TileCollision.IntersectsTile(
                        remote.RenderPosition.X,
                        remote.RenderPosition.Y,
                        PhysicsConstants.PlayerWidth,
                        PhysicsConstants.PlayerHeight,
                        tileX,
                        tileY))
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Menampilkan label ringkas di atas pohon yang diarahkan kursor berisi nama pohon
    /// dan hitung mundur waktu matang (atau "READY!" hijau cerah jika sudah siap panen).
    /// </summary>
    public void DrawTreeTooltip(
        SpriteBatch spriteBatch,
        IReadOnlyDictionary<int, TreeData> trees,
        int tileX,
        int tileY,
        long utcNowTicks)
    {
        if (!Tile.IsInBounds(tileX, tileY))
        {
            return;
        }

        int index = Tile.GetIndex(tileX, tileY);
        if (!trees.TryGetValue(index, out TreeData tree))
        {
            return;
        }

        string treeName = ItemCatalog.TryGetSeedProperties(tree.SeedId, out SeedProperties props)
            ? props.TreeName
            : "Tree";
        bool ready = tree.IsReady(utcNowTicks);
        string statusLine;
        if (ready)
        {
            statusLine = "READY!";
        }
        else
        {
            int secs = Math.Max(1, (int)Math.Ceiling(tree.GetRemainingSeconds(utcNowTicks)));
            statusLine = secs >= 60 ? $"{secs / 60}m {secs % 60:D2}s" : $"{secs}s";
        }

        Vector2 nameSize = _font.MeasureString(treeName, scale: 1);
        Vector2 statusSize = _font.MeasureString(statusLine, scale: 1);
        const int padX = 7;
        const int padY = 4;
        int boxW = (int)MathF.Ceiling(MathF.Max(nameSize.X, statusSize.X)) + padX * 2;
        int boxH = (int)nameSize.Y + (int)statusSize.Y + padY * 2 + 3;

        int centerX = tileX * WorldConstants.TileSize + WorldConstants.TileSize / 2;
        int boxX = (int)MathF.Floor(centerX - boxW * 0.5f);
        int boxY = tileY * WorldConstants.TileSize - boxH - 6;

        Color border = ready ? new Color(108, 255, 134, 235) : new Color(126, 218, 255, 220);
        Color statusColor = ready ? new Color(116, 255, 140) : new Color(255, 232, 124);

        UiDraw.Panel(
            spriteBatch,
            _atlas.Pixel,
            new Rectangle(boxX, boxY, boxW, boxH),
            fill: new Color(14, 34, 54, 224),
            border: border,
            thickness: 1,
            radius: 4);

        int nameX = (int)MathF.Floor(boxX + (boxW - nameSize.X) * 0.5f);
        int statusX = (int)MathF.Floor(boxX + (boxW - statusSize.X) * 0.5f);
        _font.DrawString(spriteBatch, treeName, new Vector2(nameX, boxY + padY), Color.White, scale: 1, dropShadow: true);
        _font.DrawString(spriteBatch, statusLine, new Vector2(statusX, boxY + padY + (int)nameSize.Y + 3), statusColor, scale: 1, dropShadow: true);
    }

    private void DrawCornerBrackets(
        SpriteBatch spriteBatch,
        int x,
        int y,
        int size,
        int armLength,
        int thickness,
        Color color)
    {
        // Kiri atas
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x, y, armLength, thickness), color);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x, y, thickness, armLength), color);

        // Kanan atas
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x + size - armLength, y, armLength, thickness), color);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x + size - thickness, y, thickness, armLength), color);

        // Kiri bawah
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x, y + size - thickness, armLength, thickness), color);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x, y + size - armLength, thickness, armLength), color);

        // Kanan bawah
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x + size - armLength, y + size - thickness, armLength, thickness), color);
        spriteBatch.Draw(_atlas.Pixel, new Rectangle(x + size - thickness, y + size - armLength, thickness, armLength), color);
    }

    public void DrawLocalPlayer(
        SpriteBatch spriteBatch,
        Tile[] tiles,
        PlayerController player,
        string playerName,
        bool isWorldOwner,
        SpeechBubble? bubble,
        float elapsedTime)
    {
        if (!player.IsSpawned)
        {
            return;
        }

        DrawCharacterSprite(
            spriteBatch,
            tiles,
            _atlas.LocalCharacterSprites,
            player.PositionX,
            player.PositionY,
            player.VelocityX,
            player.VelocityY,
            player.FacingLeft,
            player.IsGrounded,
            player.WalkAnimTime,
            player.IsPunching,
            player.PunchProgress,
            playerName,
            isWorldOwner,
            bubble,
            elapsedTime,
            blinkSeed: 0);
    }

    public void DrawRemotePlayers(
        SpriteBatch spriteBatch,
        Tile[] tiles,
        IReadOnlyDictionary<int, RemotePlayerState> players,
        string worldLockOwner,
        float elapsedTime)
    {
        foreach (RemotePlayerState player in players.Values)
        {
            bool isOwner = !string.IsNullOrEmpty(worldLockOwner) &&
                           string.Equals(worldLockOwner, player.Name, StringComparison.OrdinalIgnoreCase);

            DrawCharacterSprite(
                spriteBatch,
                tiles,
                _atlas.GetRemoteCharacterSprites(player.PlayerId),
                player.RenderPosition.X,
                player.RenderPosition.Y,
                player.VelocityX,
                player.VelocityY,
                player.FacingLeft,
                player.IsGrounded,
                player.WalkAnimTime,
                isPunching: false,
                punchProgress: 0f,
                player.Name,
                isOwner,
                player.ActiveBubble,
                elapsedTime,
                blinkSeed: player.PlayerId + 1);
        }
    }

    private void DrawCharacterSprite(
        SpriteBatch spriteBatch,
        Tile[] tiles,
        CharacterSpriteSet sprites,
        float x,
        float y,
        float velocityX,
        float velocityY,
        bool facingLeft,
        bool isGrounded,
        float walkAnimTime,
        bool isPunching,
        float punchProgress,
        string name,
        bool isWorldOwner,
        SpeechBubble? bubble,
        float elapsedTime,
        int blinkSeed)
    {
        int w = (int)PhysicsConstants.PlayerWidth;  // 24
        int h = (int)PhysicsConstants.PlayerHeight; // 44
        float centerX = x + w * 0.5f;
        float feetY = y + h;

        // 1. Ground Shadow: bayangan elips gelap semi-transparan di permukaan solid di bawah kaki
        DrawCharacterGroundShadow(spriteBatch, tiles, centerX, feetY);

        // 2. Pilih frame animasi berdasarkan state (Punch > Jump/Fall > Walk > Idle/Blink/Breath)
        CharacterAnimFrame animFrame;
        if (isPunching)
        {
            animFrame = CharacterAnimFrame.Punch;
        }
        else if (!isGrounded)
        {
            animFrame = velocityY < 0f ? CharacterAnimFrame.Jump : CharacterAnimFrame.Fall;
        }
        else if (MathF.Abs(velocityX) > 10f || walkAnimTime > 0.01f)
        {
            int walkStep = ((int)walkAnimTime) & 3;
            animFrame = walkStep switch
            {
                0 => CharacterAnimFrame.Walk0,
                1 => CharacterAnimFrame.Walk1,
                2 => CharacterAnimFrame.Walk2,
                _ => CharacterAnimFrame.Walk3,
            };
        }
        else
        {
            // Idle: kedipan mata otomatis tiap ~3.6 detik + animasi napas pelan 1px
            float blinkCycle = (elapsedTime + blinkSeed * 1.43f) % 3.6f;
            if (blinkCycle > 3.46f)
            {
                animFrame = CharacterAnimFrame.IdleBlink;
            }
            else
            {
                animFrame = MathF.Sin(elapsedTime * 2.6f + blinkSeed) > 0.35f
                    ? CharacterAnimFrame.IdleBreath
                    : CharacterAnimFrame.Idle;
            }
        }

        // 3. Render sprite karakter 18x26 texel pada skala 2x exact (36x52 px) — 100% seragam dengan tile 16x16!
        int spriteW = PixelAtlas.CharTexWidth * PixelAtlas.TexelScale;  // 36
        int spriteH = PixelAtlas.CharTexHeight * PixelAtlas.TexelScale; // 52
        int drawX = (int)MathF.Floor(centerX - spriteW * 0.5f);
        int drawY = (int)MathF.Floor(feetY - spriteH);

        SpriteEffects flip = facingLeft ? SpriteEffects.FlipHorizontally : SpriteEffects.None;
        Texture2D frameTex = sprites.GetFrame(animFrame);
        spriteBatch.Draw(frameTex, new Rectangle(drawX, drawY, spriteW, spriteH), null, Color.White, 0f, Vector2.Zero, flip, 0f);

        // Mahkota pemilik World Lock tepat di atas rambut (skala 2x seragam)
        if (isWorldOwner)
        {
            int crownW = _atlas.OwnerCrown.Width * PixelAtlas.TexelScale;
            int crownH = _atlas.OwnerCrown.Height * PixelAtlas.TexelScale;
            int crownX = (int)MathF.Floor(centerX - crownW * 0.5f);
            int crownY = drawY - crownH + 6;
            spriteBatch.Draw(_atlas.OwnerCrown, new Rectangle(crownX, crownY, crownW, crownH), Color.White);
        }

        // 4. Efek kilasan garis sabit putih tipis selama animasi Punch (~140ms)
        if (isPunching)
        {
            int slashSize = 32;
            float forwardShift = 10f + punchProgress * 8f;
            int slashX = facingLeft
                ? (int)MathF.Floor(centerX - forwardShift - slashSize * 0.65f)
                : (int)MathF.Floor(centerX + forwardShift - slashSize * 0.35f);
            int slashY = (int)MathF.Floor(y + 10f);
            byte slashAlpha = (byte)Math.Clamp((int)((1f - punchProgress * 0.7f) * 235f), 0, 235);

            spriteBatch.Draw(
                _atlas.PunchSlash,
                new Rectangle(slashX, slashY, slashSize, slashSize),
                null,
                Color.FromNonPremultiplied(255, 255, 255, slashAlpha),
                0f,
                Vector2.Zero,
                flip,
                0f);
        }
    }

    /// <summary>
    /// Merender Nameplate pemain, Speech Bubble, dan Tree Tooltip dalam koordinat screen-space
    /// menggunakan <see cref="UiNineSlice"/> dan <see cref="UiFontManager"/> (<see cref="SamplerState.LinearClamp"/>)
    /// agar tipografi selalu super tajam & anti-aliased di semua level zoom kamera (1.0x-2.5x).
    /// </summary>
    public void DrawWorldOverlaysScreenSpace(
        SpriteBatch spriteBatch,
        Camera2D camera,
        Viewport viewport,
        PlayerController localPlayer,
        string localPlayerName,
        bool localIsOwner,
        SpeechBubble? localBubble,
        IReadOnlyDictionary<int, RemotePlayerState> remotePlayers,
        string worldLockOwner,
        IReadOnlyDictionary<int, TreeData> trees,
        int hoveredTileX,
        int hoveredTileY,
        long utcNowTicks)
    {
        // 1. Nameplate & Speech Bubble seluruh pemain lain
        foreach (RemotePlayerState remote in remotePlayers.Values)
        {
            bool isOwner = !string.IsNullOrEmpty(worldLockOwner) &&
                           string.Equals(worldLockOwner, remote.Name, StringComparison.OrdinalIgnoreCase);

            DrawPlayerOverlayScreenSpace(
                spriteBatch,
                camera,
                viewport,
                remote.RenderPosition.X,
                remote.RenderPosition.Y,
                remote.Name,
                isOwner,
                remote.ActiveBubble);
        }

        // 2. Nameplate & Speech Bubble pemain lokal di atas agar selalu jelas
        if (localPlayer.IsSpawned)
        {
            DrawPlayerOverlayScreenSpace(
                spriteBatch,
                camera,
                viewport,
                localPlayer.PositionX,
                localPlayer.PositionY,
                localPlayerName,
                localIsOwner,
                localBubble);
        }

        // 3. Tooltip pohon yang diarahkan kursor
        if (Tile.IsInBounds(hoveredTileX, hoveredTileY))
        {
            DrawTreeTooltipScreenSpace(spriteBatch, camera, viewport, trees, hoveredTileX, hoveredTileY, utcNowTicks);
        }
    }

    private void DrawPlayerOverlayScreenSpace(
        SpriteBatch spriteBatch,
        Camera2D camera,
        Viewport viewport,
        float worldX,
        float worldY,
        string name,
        bool isWorldOwner,
        SpeechBubble? bubble)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        float centerX = worldX + PhysicsConstants.PlayerWidth * 0.5f;
        float feetY = worldY + PhysicsConstants.PlayerHeight;
        int spriteH = PixelAtlas.CharTexHeight * PixelAtlas.TexelScale; // 52
        float headTopWorldY = feetY - spriteH - (isWorldOwner ? 14f : 4f);

        Vector2 screenAnchor = camera.WorldToScreen(new Vector2(centerX, headTopWorldY));
        if (screenAnchor.X < -160f || screenAnchor.X > viewport.Width + 160f ||
            screenAnchor.Y < -140f || screenAnchor.Y > viewport.Height + 100f)
        {
            return;
        }

        // Pil nameplate halus dengan 9-slice PanelDark / PanelDarkGold + FontStashSharp 13px
        Vector2 nameSize = _uiFonts.MeasureString(name, UiFontSize.Small);
        const int padX = 9;
        const int padY = 3;
        int pillW = (int)MathF.Ceiling(nameSize.X) + padX * 2;
        int pillH = (int)MathF.Ceiling(nameSize.Y) + padY * 2 + 2;
        int pillX = (int)MathF.Floor(screenAnchor.X - pillW * 0.5f);
        int pillY = (int)MathF.Floor(screenAnchor.Y - pillH - 4f);

        var pillRect = new Rectangle(pillX, pillY, pillW, pillH);
        _nineSlice.DrawPanel(
            spriteBatch,
            isWorldOwner ? UiPanelStyle.PanelDarkGold : UiPanelStyle.PanelDark,
            pillRect,
            alpha: 0.86f,
            cornerSize: 7);

        Color nameColor = isWorldOwner ? new Color(255, 238, 112) : Color.White;
        _uiFonts.DrawCentered(
            spriteBatch,
            name,
            new Rectangle(pillRect.X, pillRect.Y, pillRect.Width, pillRect.Height - 2),
            nameColor,
            UiFontSize.Small,
            dropShadow: true);

        // Speech bubble membulat halus tepat di atas nameplate
        if (bubble is not null && !string.IsNullOrWhiteSpace(bubble.Text))
        {
            List<string> wrapped = _uiFonts.WrapText(bubble.Text, maxWidth: 210f, UiFontSize.Small);
            int lineCount = Math.Min(3, wrapped.Count);
            float maxW = 0f;
            for (int i = 0; i < lineCount; i++)
            {
                maxW = MathF.Max(maxW, _uiFonts.MeasureString(wrapped[i], UiFontSize.Small).X);
            }

            const int bPadX = 11;
            const int bPadY = 6;
            int lineH = _uiFonts.GetLineHeight(UiFontSize.Small) + 1;
            int boxW = Math.Max(36, (int)MathF.Ceiling(maxW) + bPadX * 2);
            int boxH = lineCount * lineH + bPadY * 2 + 2;
            int boxX = (int)MathF.Floor(screenAnchor.X - boxW * 0.5f);
            int boxY = pillY - boxH - 10;

            boxX = Math.Clamp(boxX, 8, Math.Max(8, viewport.Width - boxW - 8));
            boxY = Math.Max(8, boxY);

            var bubbleRect = new Rectangle(boxX, boxY, boxW, boxH);
            _nineSlice.DrawPanel(spriteBatch, UiPanelStyle.SpeechBubble, bubbleRect, cornerSize: 9);

            // Ekor kecil di bagian bawah bubble mengarah ke karakter
            int tailX = Math.Clamp((int)MathF.Floor(screenAnchor.X), boxX + 12, boxX + boxW - 12);
            spriteBatch.Draw(_atlas.Pixel, new Rectangle(tailX - 4, bubbleRect.Bottom - 5, 8, 5), new Color(242, 248, 253));
            spriteBatch.Draw(_atlas.Pixel, new Rectangle(tailX - 4, bubbleRect.Bottom - 1, 2, 2), new Color(28, 54, 82));
            spriteBatch.Draw(_atlas.Pixel, new Rectangle(tailX + 2, bubbleRect.Bottom - 1, 2, 2), new Color(28, 54, 82));
            spriteBatch.Draw(_atlas.Pixel, new Rectangle(tailX - 2, bubbleRect.Bottom + 1, 4, 2), new Color(28, 54, 82));

            for (int i = 0; i < lineCount; i++)
            {
                _uiFonts.DrawString(
                    spriteBatch,
                    wrapped[i],
                    new Vector2(boxX + bPadX, boxY + bPadY + i * lineH),
                    new Color(22, 34, 48),
                    UiFontSize.Small,
                    dropShadow: false);
            }
        }
    }

    private void DrawTreeTooltipScreenSpace(
        SpriteBatch spriteBatch,
        Camera2D camera,
        Viewport viewport,
        IReadOnlyDictionary<int, TreeData> trees,
        int tileX,
        int tileY,
        long utcNowTicks)
    {
        int index = Tile.GetIndex(tileX, tileY);
        if (!trees.TryGetValue(index, out TreeData tree))
        {
            return;
        }

        string treeName = ItemCatalog.TryGetSeedProperties(tree.SeedId, out SeedProperties props)
            ? props.TreeName
            : "Tree";
        bool ready = tree.IsReady(utcNowTicks);
        string statusLine;
        if (ready)
        {
            statusLine = "READY TO HARVEST!";
        }
        else
        {
            int secs = Math.Max(1, (int)Math.Ceiling(tree.GetRemainingSeconds(utcNowTicks)));
            statusLine = secs >= 60 ? $"{secs / 60}m {secs % 60:D2}s left" : $"{secs}s left";
        }

        Vector2 nameSize = _uiFonts.MeasureString(treeName, UiFontSize.Small);
        Vector2 statusSize = _uiFonts.MeasureString(statusLine, UiFontSize.Tiny);
        const int padX = 11;
        const int padY = 6;
        int boxW = (int)MathF.Ceiling(MathF.Max(nameSize.X, statusSize.X)) + padX * 2;
        int boxH = (int)MathF.Ceiling(nameSize.Y + statusSize.Y) + padY * 2 + 4;

        Vector2 anchorWorld = new(
            tileX * WorldConstants.TileSize + WorldConstants.TileSize * 0.5f,
            tileY * WorldConstants.TileSize);
        Vector2 anchorScreen = camera.WorldToScreen(anchorWorld);

        int boxX = (int)MathF.Floor(anchorScreen.X - boxW * 0.5f);
        int boxY = (int)MathF.Floor(anchorScreen.Y - boxH - 10f);
        boxX = Math.Clamp(boxX, 8, Math.Max(8, viewport.Width - boxW - 8));
        boxY = Math.Max(8, boxY);

        var rect = new Rectangle(boxX, boxY, boxW, boxH);
        _nineSlice.DrawPanel(
            spriteBatch,
            ready ? UiPanelStyle.PanelDarkGold : UiPanelStyle.PanelDark,
            rect,
            alpha: 0.94f,
            cornerSize: 8);

        Color statusColor = ready ? new Color(126, 255, 148) : new Color(255, 232, 128);
        float nameX = boxX + (boxW - nameSize.X) * 0.5f;
        float statusX = boxX + (boxW - statusSize.X) * 0.5f;
        _uiFonts.DrawString(spriteBatch, treeName, new Vector2(nameX, boxY + padY), Color.White, UiFontSize.Small);
        _uiFonts.DrawString(spriteBatch, statusLine, new Vector2(statusX, boxY + padY + nameSize.Y + 1f), statusColor, UiFontSize.Tiny);
    }

    private void DrawCharacterGroundShadow(SpriteBatch spriteBatch, Tile[] tiles, float centerX, float feetY)
    {
        if (tiles.Length != WorldConstants.TotalTiles)
        {
            return;
        }

        int tileX = Math.Clamp((int)MathF.Floor(centerX / WorldConstants.TileSize), 0, WorldConstants.Width - 1);
        int startTileY = Math.Clamp((int)MathF.Floor((feetY - 1f) / WorldConstants.TileSize), 0, WorldConstants.Height - 1);
        int maxScanY = Math.Min(WorldConstants.Height - 1, startTileY + 6);

        int groundTileY = -1;
        for (int ty = startTileY; ty <= maxScanY; ty++)
        {
            if (ItemCatalog.IsSolid(tiles[ty * WorldConstants.Width + tileX].ForegroundId))
            {
                groundTileY = ty;
                break;
            }
        }

        if (groundTileY < 0)
        {
            return;
        }

        float groundY = groundTileY * WorldConstants.TileSize;
        float heightAbove = MathF.Max(0f, groundY - feetY);
        const float maxFadeHeight = 160f;
        if (heightAbove >= maxFadeHeight)
        {
            return;
        }

        float factor = 1f - (heightAbove / maxFadeHeight);
        int shadowW = (int)MathF.Round(MathHelper.Lerp(10f, 26f, factor));
        int shadowH = (int)MathF.Round(MathHelper.Lerp(3f, 7f, factor));
        byte alpha = (byte)Math.Clamp((int)(185f * factor * factor), 0, 185);

        spriteBatch.Draw(
            _atlas.DropShadowEllipse,
            new Rectangle((int)MathF.Floor(centerX - shadowW * 0.5f), (int)MathF.Floor(groundY - shadowH * 0.45f), shadowW, shadowH),
            new Color((byte)255, (byte)255, (byte)255, alpha));
    }
}
