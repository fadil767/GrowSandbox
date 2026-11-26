using GrowSandbox.Shared.Constants;
using GrowSandbox.Shared.Models;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.Graphics;

public enum CharacterAnimFrame : byte
{
    Idle = 0,
    IdleBreath = 1,
    IdleBlink = 2,
    Walk0 = 3,
    Walk1 = 4,
    Walk2 = 5,
    Walk3 = 6,
    Jump = 7,
    Fall = 8,
    Punch = 9,
}

/// <summary>
/// Kumpulan frame sprite karakter (resolusi logis 18x24 texel -> dirender 36x48 pada skala 2x seragam).
/// </summary>
public sealed class CharacterSpriteSet
{
    private readonly Texture2D[] _frames;

    public CharacterSpriteSet(Texture2D[] frames)
    {
        _frames = frames;
    }

    public Texture2D GetFrame(CharacterAnimFrame frame) => _frames[(int)frame];
}

/// <summary>
/// Generator tekstur pixel-art prosedural dengan kepadatan piksel (texel density) seragam 16x16 per tile (2x -> 32x32):
/// - 4-bit Auto-Tiling (16 varian sudut membulat + tepi gelap 1px + highlight atas) pre-generated saat startup
/// - Sprite karakter 18x24 texel bebas artefak dengan outline 1px rapi & 10 frame animasi (Idle/Walk/Jump/Fall/Punch)
/// - Efek kilasan sabit punch, 4 tahap retakan, bayangan elips, matahari, 3 variasi awan, & bukit paralaks
/// </summary>
public sealed class PixelAtlas : IDisposable
{
    /// <summary>Rasio piksel layar per 1 texel logis (16 texel * 2 = 32 px per tile).</summary>
    public const int TexelScale = 2;

    public const int CharTexWidth = 18;
    public const int CharTexHeight = 26;

    public Texture2D Pixel { get; }
    public Texture2D SkyGradient { get; }
    public Texture2D Sun { get; }
    public Texture2D[] CloudVariants { get; }
    public Texture2D HillsFar { get; }
    public Texture2D HillsNear { get; }
    public Texture2D RadialGlowTexture { get; }
    public Texture2D TorchGlow => RadialGlowTexture;
    public Texture2D DropGlow => RadialGlowTexture;
    public Texture2D DropShadowEllipse { get; }
    public Texture2D GemSparkle { get; }
    public Texture2D PunchSlash { get; }
    public Texture2D OwnerCrown { get; }

    public Texture2D FistIcon { get; }
    public Texture2D WrenchIcon { get; }
    public Texture2D GemIcon { get; }
    public Texture2D GlobeIcon { get; }
    public Texture2D MenuIcon { get; }
    public Texture2D ChatIcon { get; }

    public Texture2D[] CrackOverlays { get; }
    public Texture2D[] TreeStages { get; }
    public Texture2D[] TorchFrames { get; }

    public CharacterSpriteSet LocalCharacterSprites { get; }
    public CharacterSpriteSet[] RemoteCharacterSprites { get; }

    private readonly Dictionary<ushort, Texture2D> _tileTextures = new();
    private readonly Dictionary<ushort, Texture2D[]> _autoTileVariants = new();
    private readonly Dictionary<ushort, Texture2D[][]> _multiVariantAutoTiles = new();
    private readonly Dictionary<ushort, Texture2D[]> _seedTreeStages = new();
    private readonly List<Texture2D> _ownedTextures = new();

    public PixelAtlas(GraphicsDevice gd)
    {
        ArgumentNullException.ThrowIfNull(gd);

        Pixel = Register(CreateSinglePixel(gd, Color.White));
        SkyGradient = Register(CreateSkyGradient(gd, 1, 256));

        Sun = Register(CreateSunTexture(gd, 44));
        CloudVariants =
        [
            Register(CreateCloudLarge(gd)),
            Register(CreateCloudMedium(gd)),
            Register(CreateCloudSmall(gd)),
        ];

        HillsFar = Register(CreateFarHillsTexture(gd, 320, 86));
        HillsNear = Register(CreateNearHillsTexture(gd, 320, 68));
        RadialGlowTexture = Register(CreateRadialGlowTexture(gd, size: 128));
        DropShadowEllipse = Register(CreateEllipseShadowTexture(gd, 16, 6));
        GemSparkle = Register(CreateGemSparkleTexture(gd));
        PunchSlash = Register(CreatePunchSlashTexture(gd));
        OwnerCrown = Register(CreateOwnerCrownTexture(gd));

        FistIcon = Register(CreateFistIcon(gd));
        WrenchIcon = Register(CreateWrenchIcon(gd));
        GemIcon = Register(CreateGemIcon(gd));
        GlobeIcon = Register(CreateGlobeIcon(gd));
        MenuIcon = Register(CreateMenuIcon(gd));
        ChatIcon = Register(CreateChatIcon(gd));

        // 4-bit Auto-Tiling (16 varian 0..15) pre-generated saat startup:
        // Dirt & GrassDirt menggunakan 4 varian dasar seamless (4 x 16 = 64 tekstur) tanpa pola garis horizontal.
        RegisterMultiVariantAutoTiledBlock(
            gd,
            TileIds.Dirt,
            [
                BuildSeamlessDirtPixels(variant: 0, withGrass: false),
                BuildSeamlessDirtPixels(variant: 1, withGrass: false),
                BuildSeamlessDirtPixels(variant: 2, withGrass: false),
                BuildSeamlessDirtPixels(variant: 3, withGrass: false),
            ],
            new Color(46, 28, 16));
        RegisterMultiVariantAutoTiledBlock(
            gd,
            TileIds.GrassDirt,
            [
                BuildSeamlessDirtPixels(variant: 0, withGrass: true),
                BuildSeamlessDirtPixels(variant: 1, withGrass: true),
                BuildSeamlessDirtPixels(variant: 2, withGrass: true),
                BuildSeamlessDirtPixels(variant: 3, withGrass: true),
            ],
            new Color(36, 46, 22));
        RegisterAutoTiledBlock(gd, TileIds.Stone, BuildStonePixels(), new Color(42, 46, 54));
        RegisterAutoTiledBlock(gd, TileIds.Wood, BuildWoodPixels(isBackground: false), new Color(56, 34, 16));
        RegisterAutoTiledBlock(gd, TileIds.Brick, BuildBrickPixels(), new Color(78, 30, 24));
        RegisterAutoTiledBlock(gd, TileIds.Bedrock, BuildBedrockPixels(), new Color(18, 20, 26));

        Texture2D whiteDoorTex = Register(CreateWhiteDoorTexture(gd));
        _tileTextures[TileIds.CaveBackground] = Register(CreateCaveBackgroundTexture(gd));
        _tileTextures[TileIds.WhiteDoor] = whiteDoorTex;
        _tileTextures[TileIds.Spawn] = whiteDoorTex;
        _tileTextures[TileIds.WoodBackground] = Register(MakeTex(gd, 16, 16, BuildWoodPixels(isBackground: true)));
        _tileTextures[TileIds.Glass] = Register(CreateGlassTexture(gd));
        _tileTextures[TileIds.WorldLock] = Register(CreateWorldLockTexture(gd));
        _tileTextures[TileIds.Gem] = GemIcon;

        TorchFrames =
        [
            Register(CreateTorchTexture(gd, frame: 0)),
            Register(CreateTorchTexture(gd, frame: 1)),
        ];
        _tileTextures[TileIds.Torch] = TorchFrames[0];

        // Pre-generate ikon Seed (16x16) & 4 tahap pohon berwarna untuk setiap blok farmable saat startup.
        foreach (SeedProperties seed in ItemCatalog.GetAllSeeds())
        {
            _tileTextures[seed.SeedId] = Register(CreateSeedIconTexture(gd, seed.PrimaryColor, seed.SecondaryColor));
            _seedTreeStages[seed.SeedId] =
            [
                Register(CreateColoredTreeStageTexture(gd, stage: 0, seed.PrimaryColor, seed.SecondaryColor)),
                Register(CreateColoredTreeStageTexture(gd, stage: 1, seed.PrimaryColor, seed.SecondaryColor)),
                Register(CreateColoredTreeStageTexture(gd, stage: 2, seed.PrimaryColor, seed.SecondaryColor)),
                Register(CreateColoredTreeStageTexture(gd, stage: 3, seed.PrimaryColor, seed.SecondaryColor)),
            ];
        }

        TreeStages = _seedTreeStages[TileIds.WoodSeed];
        _tileTextures[TileIds.Tree] = TreeStages[3];

        CrackOverlays =
        [
            Register(CreateCrackTexture(gd, level: 1)),
            Register(CreateCrackTexture(gd, level: 2)),
            Register(CreateCrackTexture(gd, level: 3)),
            Register(CreateCrackTexture(gd, level: 4)),
        ];

        // Pre-generate sprite sheet karakter 18x24 texel (seragam 1:1 dengan tile 16x16)
        LocalCharacterSprites = BuildCharacterSpriteSet(
            gd,
            hairColor: new Color(236, 74, 24),
            shirtColor: new Color(86, 174, 62),
            pantsColor: new Color(64, 102, 164));

        RemoteCharacterSprites =
        [
            BuildCharacterSpriteSet(gd, new Color(246, 204, 62), new Color(62, 148, 232), new Color(58, 72, 104)),
            BuildCharacterSpriteSet(gd, new Color(56, 42, 34), new Color(224, 78, 86), new Color(66, 86, 128)),
            BuildCharacterSpriteSet(gd, new Color(238, 128, 48), new Color(164, 88, 224), new Color(52, 68, 98)),
            BuildCharacterSpriteSet(gd, new Color(42, 46, 62), new Color(46, 192, 186), new Color(72, 82, 108)),
            BuildCharacterSpriteSet(gd, new Color(118, 62, 34), new Color(236, 156, 44), new Color(60, 84, 118)),
        ];
    }

    public bool HasAutoTileVariants(ushort tileId) =>
        _multiVariantAutoTiles.ContainsKey(tileId) || _autoTileVariants.ContainsKey(tileId);

    public Texture2D GetAutoTileTexture(ushort tileId, int bitmask4, int variant = 0)
    {
        if (_multiVariantAutoTiles.TryGetValue(tileId, out Texture2D[][]? variantSets))
        {
            return variantSets[variant & 3][bitmask4 & 0x0F];
        }

        if (_autoTileVariants.TryGetValue(tileId, out Texture2D[]? variants))
        {
            return variants[bitmask4 & 0x0F];
        }

        return GetTileTexture(tileId);
    }

    public Texture2D GetTreeStageTexture(ushort seedId, int stage)
    {
        int clamped = Math.Clamp(stage, 0, 3);
        if (_seedTreeStages.TryGetValue(seedId, out Texture2D[]? stages))
        {
            return stages[clamped];
        }

        return TreeStages[clamped];
    }

    public Texture2D GetTileTexture(ushort tileId, ushort extraData = 0, float animTime = 0f)
    {
        if (tileId == TileIds.Torch)
        {
            int frame = ((int)(animTime * 6f)) % TorchFrames.Length;
            return TorchFrames[frame];
        }

        if (tileId == TileIds.Tree)
        {
            ushort seedId = ItemCatalog.IsSeed(extraData) ? extraData : TileIds.WoodSeed;
            return GetTreeStageTexture(seedId, stage: 3);
        }

        return _tileTextures.TryGetValue(tileId, out Texture2D? tex) ? tex : Pixel;
    }

    public Texture2D GetItemIcon(ushort itemId)
    {
        if (itemId == TileIds.Tree)
        {
            return TreeStages[2];
        }

        if (itemId == TileIds.Gem)
        {
            return GemIcon;
        }

        // Gunakan varian bitmask 0 (blok terisolasi dengan 4 sudut membulat + outline lengkap) sebagai ikon hotbar!
        if (_multiVariantAutoTiles.TryGetValue(itemId, out Texture2D[][]? variantSets))
        {
            return variantSets[0][0];
        }

        if (_autoTileVariants.TryGetValue(itemId, out Texture2D[]? variants))
        {
            return variants[0];
        }

        return _tileTextures.TryGetValue(itemId, out Texture2D? tex) ? tex : Pixel;
    }

    public CharacterSpriteSet GetRemoteCharacterSprites(int playerId) =>
        RemoteCharacterSprites[Math.Abs(playerId) % RemoteCharacterSprites.Length];

    public static Color GetParticleColor(ushort tileId)
    {
        if (ItemCatalog.TryGetSeedProperties(tileId, out SeedProperties seed))
        {
            return new Color(seed.PrimaryColor.R, seed.PrimaryColor.G, seed.PrimaryColor.B);
        }

        return tileId switch
        {
            TileIds.GrassDirt => new Color(96, 188, 62),
            TileIds.Dirt => new Color(152, 96, 52),
            TileIds.CaveBackground => new Color(96, 62, 38),
            TileIds.Stone => new Color(142, 150, 160),
            TileIds.Wood => new Color(186, 130, 72),
            TileIds.WoodBackground => new Color(122, 82, 46),
            TileIds.Brick => new Color(196, 78, 56),
            TileIds.Glass => new Color(185, 240, 255),
            TileIds.Torch => new Color(255, 200, 64),
            TileIds.WorldLock => new Color(255, 218, 68),
            TileIds.Tree => new Color(86, 192, 72),
            TileIds.Gem => new Color(72, 232, 164),
            TileIds.WhiteDoor or TileIds.Spawn => new Color(235, 238, 244),
            _ => new Color(180, 180, 180),
        };
    }

    /// <summary>
    /// Pre-generate 4 varian seamless x 16 bitmask 4-bit auto-tile saat startup (tanpa alokasi di render loop).
    /// </summary>
    private void RegisterMultiVariantAutoTiledBlock(
        GraphicsDevice gd,
        ushort tileId,
        Color[][] baseVariants,
        Color outlineColor)
    {
        var variantSets = new Texture2D[baseVariants.Length][];
        for (int v = 0; v < baseVariants.Length; v++)
        {
            variantSets[v] = BuildAutoTileMaskSet(gd, baseVariants[v], outlineColor);
        }

        _multiVariantAutoTiles[tileId] = variantSets;
        _tileTextures[tileId] = variantSets[0][15];
    }

    /// <summary>
    /// Pre-generate 16 varian 4-bit auto-tile (Atas=+1, Kanan=+2, Bawah=+4, Kiri=+8) pada resolusi 16x16.
    /// Garis highlight terang &amp; border gelap 1px HANYA digambar di sisi yang TIDAK bertetangga blok solid;
    /// untuk bitmask 15 (terkurung penuh), tidak ada garis tepi sama sekali.
    /// </summary>
    private void RegisterAutoTiledBlock(GraphicsDevice gd, ushort tileId, Color[] basePixels, Color outlineColor)
    {
        Texture2D[] variants = BuildAutoTileMaskSet(gd, basePixels, outlineColor);
        _autoTileVariants[tileId] = variants;
        _tileTextures[tileId] = variants[15];
    }

    private Texture2D[] BuildAutoTileMaskSet(GraphicsDevice gd, Color[] basePixels, Color outlineColor)
    {
        var variants = new Texture2D[16];

        for (int mask = 0; mask < 16; mask++)
        {
            bool hasTop = (mask & 1) != 0;
            bool hasRight = (mask & 2) != 0;
            bool hasBottom = (mask & 4) != 0;
            bool hasLeft = (mask & 8) != 0;

            var pixels = new Color[16 * 16];
            Array.Copy(basePixels, pixels, 256);

            // Highlight tipis di tepi dalam atas HANYA bila sisi atas terbuka ke udara (menerus mulus di tepi kiri/kanan)
            if (!hasTop)
            {
                int startX = hasLeft ? 0 : 1;
                int endX = hasRight ? 15 : 14;
                for (int x = startX; x <= endX; x++)
                {
                    pixels[1 * 16 + x] = Color.Lerp(pixels[1 * 16 + x], Color.White, 0.18f);
                }
            }

            // Sedikit penggelapan di tepi dalam bawah HANYA bila sisi bawah terbuka
            if (!hasBottom)
            {
                int startX = hasLeft ? 0 : 1;
                int endX = hasRight ? 15 : 14;
                for (int x = startX; x <= endX; x++)
                {
                    pixels[14 * 16 + x] = Color.Lerp(pixels[14 * 16 + x], Color.Black, 0.15f);
                }
            }

            // Garis tepi gelap 1px HANYA pada sisi yang tidak bertetangga dengan blok solid
            if (!hasTop)
            {
                for (int x = 0; x < 16; x++) pixels[0 * 16 + x] = outlineColor;
            }

            if (!hasBottom)
            {
                for (int x = 0; x < 16; x++) pixels[15 * 16 + x] = outlineColor;
            }

            if (!hasLeft)
            {
                for (int y = 0; y < 16; y++) pixels[y * 16 + 0] = outlineColor;
            }

            if (!hasRight)
            {
                for (int y = 0; y < 16; y++) pixels[y * 16 + 15] = outlineColor;
            }

            // Pembulatan sudut luar (2 piksel logis) agar platform & lubang galian terasa empuk khas Growtopia
            if (!hasTop && !hasLeft)
            {
                pixels[0 * 16 + 0] = Color.Transparent;
                pixels[0 * 16 + 1] = Color.Transparent;
                pixels[1 * 16 + 0] = Color.Transparent;
                pixels[0 * 16 + 2] = outlineColor;
                pixels[1 * 16 + 1] = outlineColor;
                pixels[2 * 16 + 0] = outlineColor;
            }

            if (!hasTop && !hasRight)
            {
                pixels[0 * 16 + 15] = Color.Transparent;
                pixels[0 * 16 + 14] = Color.Transparent;
                pixels[1 * 16 + 15] = Color.Transparent;
                pixels[0 * 16 + 13] = outlineColor;
                pixels[1 * 16 + 14] = outlineColor;
                pixels[2 * 16 + 15] = outlineColor;
            }

            if (!hasBottom && !hasLeft)
            {
                pixels[15 * 16 + 0] = Color.Transparent;
                pixels[15 * 16 + 1] = Color.Transparent;
                pixels[14 * 16 + 0] = Color.Transparent;
                pixels[15 * 16 + 2] = outlineColor;
                pixels[14 * 16 + 1] = outlineColor;
                pixels[13 * 16 + 0] = outlineColor;
            }

            if (!hasBottom && !hasRight)
            {
                pixels[15 * 16 + 15] = Color.Transparent;
                pixels[15 * 16 + 14] = Color.Transparent;
                pixels[14 * 16 + 15] = Color.Transparent;
                pixels[15 * 16 + 13] = outlineColor;
                pixels[14 * 16 + 14] = outlineColor;
                pixels[13 * 16 + 15] = outlineColor;
            }

            variants[mask] = Register(MakeTex(gd, 16, 16, pixels));
        }

        return variants;
    }

    /// <summary>
    /// Membangun 10 frame animasi karakter pada grid logis 18x24 texel (seragam 1:1 dengan tile 16x16),
    /// lengkap dengan outline gelap 1px rapi tanpa artefak kotak putih.
    /// </summary>
    private CharacterSpriteSet BuildCharacterSpriteSet(
        GraphicsDevice gd,
        Color hairColor,
        Color shirtColor,
        Color pantsColor)
    {
        var frames = new Texture2D[10];
        frames[(int)CharacterAnimFrame.Idle] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.Idle));
        frames[(int)CharacterAnimFrame.IdleBreath] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.IdleBreath));
        frames[(int)CharacterAnimFrame.IdleBlink] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.IdleBlink));
        frames[(int)CharacterAnimFrame.Walk0] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.Walk0));
        frames[(int)CharacterAnimFrame.Walk1] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.Walk1));
        frames[(int)CharacterAnimFrame.Walk2] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.Walk2));
        frames[(int)CharacterAnimFrame.Walk3] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.Walk3));
        frames[(int)CharacterAnimFrame.Jump] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.Jump));
        frames[(int)CharacterAnimFrame.Fall] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.Fall));
        frames[(int)CharacterAnimFrame.Punch] = Register(BuildCharacterFrame(gd, hairColor, shirtColor, pantsColor, CharacterAnimFrame.Punch));
        return new CharacterSpriteSet(frames);
    }

    private static Texture2D BuildCharacterFrame(
        GraphicsDevice gd,
        Color hairColor,
        Color shirtColor,
        Color pantsColor,
        CharacterAnimFrame frame)
    {
        const int w = CharTexWidth;  // 18 texel -> 36 px
        const int h = CharTexHeight; // 24 texel -> 48 px
        var p = new Color[w * h];

        var outline = new Color(30, 22, 18);
        var skin = new Color(246, 206, 160);
        var skinShade = new Color(216, 170, 122);
        var hairHi = Color.Lerp(hairColor, new Color(255, 224, 92), 0.32f);
        var hairShade = Color.Lerp(hairColor, Color.Black, 0.22f);
        var shirtHi = Color.Lerp(shirtColor, Color.White, 0.25f);
        var shirtShade = Color.Lerp(shirtColor, Color.Black, 0.25f);
        var belt = new Color(76, 50, 32);
        var buckle = new Color(248, 210, 68);
        var shoe = new Color(68, 44, 28);
        var eyeWhite = Color.White;
        var mouthShade = new Color(184, 134, 96);

        void Set(int x, int y, Color c)
        {
            if (x >= 0 && x < w && y >= 0 && y < h) p[y * w + x] = c;
        }

        void Fill(int x0, int y0, int rw, int rh, Color c)
        {
            for (int y = y0; y < y0 + rh; y++)
                for (int x = x0; x < x0 + rw; x++)
                    Set(x, y, c);
        }

        // Offset vertikal napas & langkah (1 texel halus)
        int bodyShiftY = frame switch
        {
            CharacterAnimFrame.IdleBreath => 1,
            CharacterAnimFrame.Walk0 or CharacterAnimFrame.Walk2 => -1,
            CharacterAnimFrame.Jump => -1,
            _ => 0,
        };

        // Posisi kaki (kiri = belakang, kanan = depan; karakter default menghadap ke KANAN)
        int leftLegX = 5;
        int rightLegX = 9;
        int leftLegY = 20;
        int rightLegY = 20;

        switch (frame)
        {
            case CharacterAnimFrame.Walk0:
                leftLegX = 4; rightLegX = 10; leftLegY = 20; rightLegY = 19;
                break;
            case CharacterAnimFrame.Walk2:
                leftLegX = 6; rightLegX = 8; leftLegY = 19; rightLegY = 20;
                break;
            case CharacterAnimFrame.Jump:
                leftLegX = 5; rightLegX = 9; leftLegY = 19; rightLegY = 18;
                break;
            case CharacterAnimFrame.Fall:
                leftLegX = 4; rightLegX = 10; leftLegY = 20; rightLegY = 20;
                break;
            case CharacterAnimFrame.Punch:
                leftLegX = 4; rightLegX = 10; leftLegY = 20; rightLegY = 20;
                break;
        }

        // 1. Kaki kiri & kanan (celana + sepatu)
        Fill(leftLegX, leftLegY, 3, 3, pantsColor);
        Fill(leftLegX, leftLegY + 3, 3, 2, shoe);

        Fill(rightLegX, rightLegY, 3, 3, pantsColor);
        Fill(rightLegX, rightLegY + 3, 3, 2, shoe);

        // 2. Lengan belakang (kiri)
        int backArmX = frame switch
        {
            CharacterAnimFrame.Walk0 => 3,
            CharacterAnimFrame.Walk2 => 5,
            CharacterAnimFrame.Jump => 3,
            CharacterAnimFrame.Fall => 3,
            _ => 4,
        };
        int backArmY = 15 + bodyShiftY + (frame == CharacterAnimFrame.Jump ? -1 : 0);
        Fill(backArmX, backArmY, 2, 4, shirtShade);
        Fill(backArmX, backArmY + 3, 2, 2, skinShade);

        // 3. Badan / kemeja kotak-kotak + sabuk (pusat di x = 5..11)
        int torsoY = 14 + bodyShiftY;
        Fill(5, torsoY, 7, 6, shirtColor);
        Fill(7, torsoY, 2, 5, shirtHi);
        Fill(5, torsoY + 2, 7, 1, shirtShade);
        Fill(5, torsoY + 5, 7, 1, belt);
        Fill(8, torsoY + 5, 2, 1, buckle);

        // 4. Kepala besar kotak-membulat khas Growtopia (x = 3..13, lebar 11 texel)
        int headY = 5 + bodyShiftY;
        Fill(3, headY + 1, 11, 8, skin);
        Fill(4, headY, 9, 1, skin);
        Fill(3, headY + 8, 11, 1, skinShade);

        // Rambut jabrik penuh di atas kepala + jambang di sisi kiri (belakang kepala)
        Fill(3, headY - 1, 11, 3, hairColor);
        Fill(4, headY - 2, 3, 2, hairColor);
        Fill(7, headY - 3, 4, 2, hairHi);
        Fill(11, headY - 2, 2, 2, hairColor);
        Fill(3, headY + 1, 2, 4, hairShade); // Jambang belakang

        // Mata besar ekspresif menghadap kanan (semua ketat di dalam wajah x = 6..12 — nol artefak!)
        bool blinking = frame == CharacterAnimFrame.IdleBlink;
        int eyeY = headY + 3;
        if (blinking)
        {
            Fill(6, eyeY + 1, 2, 1, outline);
            Fill(10, eyeY + 1, 2, 1, outline);
        }
        else
        {
            Fill(6, eyeY, 2, 2, eyeWhite);
            Fill(10, eyeY, 2, 2, eyeWhite);
            Set(7, eyeY + 1, outline);
            Set(11, eyeY + 1, outline);
        }

        // Senyum tipis rapi di dalam wajah (tidak menembus tepi pipi!)
        Fill(8, headY + 6, 3, 1, mouthShade);
        Set(9, headY + 6, eyeWhite);

        // 5. Lengan depan (kanan) — terjulur cepat saat Punch!
        if (frame == CharacterAnimFrame.Punch)
        {
            int py = torsoY + 1;
            Fill(10, py, 3, 2, shirtColor);
            Fill(13, py, 4, 2, skin);
            Fill(14, py - 1, 3, 3, skin);
            Set(16, py, skinShade);
        }
        else
        {
            int frontArmX = frame switch
            {
                CharacterAnimFrame.Walk0 => 11,
                CharacterAnimFrame.Walk2 => 9,
                CharacterAnimFrame.Jump => 11,
                CharacterAnimFrame.Fall => 12,
                _ => 10,
            };
            int frontArmY = torsoY + (frame == CharacterAnimFrame.Jump ? -1 : 1);
            Fill(frontArmX, frontArmY, 2, 3, shirtColor);
            Fill(frontArmX, frontArmY + 3, 2, 2, skin);
        }

        // 6. Outline gelap 1px otomatis mengelilingi seluruh siluet karakter (bersih, konsisten, nol bocor!)
        var outlined = new Color[w * h];
        Array.Copy(p, outlined, p.Length);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (p[y * w + x].A != 0)
                {
                    continue;
                }

                bool touchesOpaque =
                    (x > 0 && p[y * w + (x - 1)].A > 0) ||
                    (x + 1 < w && p[y * w + (x + 1)].A > 0) ||
                    (y > 0 && p[(y - 1) * w + x].A > 0) ||
                    (y + 1 < h && p[(y + 1) * w + x].A > 0);

                if (touchesOpaque)
                {
                    outlined[y * w + x] = outline;
                }
            }
        }

        return MakeTex(gd, w, h, outlined);
    }

    /// <summary>
    /// Efek kilasan garis sabit putih tipis (16x16 texel) saat memukul atau memasang blok.
    /// </summary>
    private static Texture2D CreatePunchSlashTexture(GraphicsDevice gd)
    {
        const int s = 16;
        var p = new Color[s * s];

        for (int y = 0; y < s; y++)
        {
            for (int x = 0; x < s; x++)
            {
                float dx = x - 4.5f;
                float dy = y - 7.5f;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                if (dx < 0.5f) continue;

                // Busur sabit ramping antara radius 6.2..9.0
                if (dist >= 6.4f && dist <= 8.8f)
                {
                    float core = 1f - MathF.Abs(dist - 7.6f) / 1.2f;
                    float arcFade = Math.Clamp(1f - MathF.Abs(dy) / 7.5f, 0f, 1f);
                    byte alpha = (byte)Math.Clamp((int)(core * arcFade * 245f), 0, 245);
                    if (alpha > 20)
                    {
                        p[y * s + x] = new Color((byte)255, (byte)252, (byte)228, alpha);
                    }
                }
            }
        }

        return MakeTex(gd, s, s, p);
    }

    private static Texture2D CreateOwnerCrownTexture(GraphicsDevice gd)
    {
        const int w = 9;
        const int h = 6;
        var p = new Color[w * h];
        var outline = new Color(46, 30, 10);
        var gold = new Color(255, 214, 52);
        var goldHi = new Color(255, 244, 152);

        void Set(int x, int y, Color c) => p[y * w + x] = c;

        // Bentuk mahkota kecil 3 puncak
        Set(1, 1, goldHi); Set(4, 0, goldHi); Set(7, 1, goldHi);
        for (int x = 1; x <= 7; x++)
        {
            Set(x, 2, (x == 1 || x == 4 || x == 7) ? goldHi : gold);
            Set(x, 3, gold);
            Set(x, 4, gold);
        }

        // Outline 1px rapi
        var outPixels = new Color[w * h];
        Array.Copy(p, outPixels, p.Length);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (p[y * w + x].A > 0) continue;
                bool adj = (x > 0 && p[y * w + x - 1].A > 0) || (x + 1 < w && p[y * w + x + 1].A > 0)
                        || (y > 0 && p[(y - 1) * w + x].A > 0) || (y + 1 < h && p[(y + 1) * w + x].A > 0);
                if (adj) outPixels[y * w + x] = outline;
            }
        }

        return MakeTex(gd, w, h, outPixels);
    }

    private Texture2D Register(Texture2D tex)
    {
        _ownedTextures.Add(tex);
        return tex;
    }

    private static Texture2D CreateSinglePixel(GraphicsDevice gd, Color color)
    {
        var tex = new Texture2D(gd, 1, 1);
        tex.SetData([color]);
        return tex;
    }

    private static Texture2D CreateSkyGradient(GraphicsDevice gd, int width, int height)
    {
        var pixels = new Color[width * height];
        var zenith = new Color(34, 140, 224);
        var midSky = new Color(92, 192, 246);
        var horizon = new Color(204, 240, 255);

        for (int y = 0; y < height; y++)
        {
            float t = (float)y / (height - 1);
            Color c = t < 0.52f
                ? Color.Lerp(zenith, midSky, t / 0.52f)
                : Color.Lerp(midSky, horizon, (t - 0.52f) / 0.48f);

            for (int x = 0; x < width; x++)
            {
                pixels[y * width + x] = c;
            }
        }

        return MakeTex(gd, width, height, pixels);
    }

    private static Texture2D CreateSunTexture(GraphicsDevice gd, int size)
    {
        var p = new Color[size * size];
        float half = size * 0.5f;

        var coreHi = new Color(255, 252, 176);
        var core = new Color(255, 236, 92);
        var midRing = new Color(255, 210, 46);
        var rim = new Color(244, 178, 30);
        var haloInner = new Color(255, 236, 128, 120);
        var haloOuter = new Color(255, 244, 176, 52);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = x + 0.5f - half;
                float dy = y + 0.5f - half;
                float dist = MathF.Sqrt(dx * dx + dy * dy);

                if (dist <= 10.5f) p[y * size + x] = (dx + dy < -4f) ? coreHi : core;
                else if (dist <= 13.0f) p[y * size + x] = midRing;
                else if (dist <= 14.2f) p[y * size + x] = rim;
                else if (dist <= 17.5f) p[y * size + x] = haloInner;
                else if (dist <= 21.0f) p[y * size + x] = haloOuter;
            }
        }

        return MakeTex(gd, size, size, p);
    }

    private static Texture2D CreateCloudLarge(GraphicsDevice gd)
    {
        const int w = 58, h = 22;
        var p = new Color[w * h];
        DrawCloudShape(p, w, h, [(14, 13, 7), (24, 9, 9), (35, 10, 8), (44, 13, 6)], 9, 48, 18);
        return MakeTex(gd, w, h, p);
    }

    private static Texture2D CreateCloudMedium(GraphicsDevice gd)
    {
        const int w = 44, h = 18;
        var p = new Color[w * h];
        DrawCloudShape(p, w, h, [(12, 11, 5), (21, 8, 7), (31, 10, 6)], 8, 35, 15);
        return MakeTex(gd, w, h, p);
    }

    private static Texture2D CreateCloudSmall(GraphicsDevice gd)
    {
        const int w = 32, h = 14;
        var p = new Color[w * h];
        DrawCloudShape(p, w, h, [(10, 8, 4), (17, 6, 5), (23, 8, 4)], 7, 25, 11);
        return MakeTex(gd, w, h, p);
    }

    private static void DrawCloudShape(
        Color[] pixels, int w, int h,
        (int Cx, int Cy, int R)[] puffs,
        int flatLeft, int flatRight, int flatBottom)
    {
        var mask = new bool[w * h];
        foreach ((int cx, int cy, int r) in puffs)
        {
            for (int y = cy - r; y <= cy + r; y++)
            {
                if (y < 0 || y >= h) continue;
                for (int x = cx - r; x <= cx + r; x++)
                {
                    if (x < 0 || x >= w) continue;
                    int dx = x - cx, dy = y - cy;
                    if (dx * dx + dy * dy <= r * r) mask[y * w + x] = true;
                }
            }
        }

        for (int y = flatBottom - 4; y <= flatBottom; y++)
        {
            if (y < 0 || y >= h) continue;
            for (int x = flatLeft; x <= flatRight; x++)
                if (x >= 0 && x < w) mask[y * w + x] = true;
        }

        var topWhite = new Color(255, 255, 255, 245);
        var midWhite = new Color(242, 250, 255, 240);
        var bellyShade = new Color(206, 232, 248, 235);
        var softEdge = new Color(184, 218, 242, 220);

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!mask[y * w + x]) continue;
                bool bottomEdge = y + 1 >= h || !mask[(y + 1) * w + x];
                bool nearBottom = y >= flatBottom - 2;
                bool topHighlight = y == 0 || !mask[(y - 1) * w + x];
                pixels[y * w + x] = bottomEdge ? softEdge : nearBottom ? bellyShade : topHighlight ? topWhite : midWhite;
            }
        }
    }

    private static Texture2D CreateFarHillsTexture(GraphicsDevice gd, int width, int height)
    {
        var p = new Color[width * height];
        var rimColor = new Color(138, 206, 198);
        var topColor = new Color(110, 184, 174);
        var midColor = new Color(90, 162, 156);
        var deepColor = new Color(76, 144, 142);

        for (int x = 0; x < width; x++)
        {
            float a = (float)x / width * MathF.PI * 2f;
            float ridge = 34f + MathF.Sin(a * 2f + 0.5f) * 18f + MathF.Sin(a * 3f + 1.9f) * 9f + MathF.Cos(a * 5f) * 3f;
            int ridgeY = Math.Clamp((int)MathF.Round(ridge), 4, height - 10);

            for (int y = ridgeY; y < height; y++)
            {
                int depth = y - ridgeY;
                p[y * width + x] = depth switch { 0 or 1 => rimColor, <= 9 => topColor, <= 24 => midColor, _ => deepColor };
            }
        }

        return MakeTex(gd, width, height, p);
    }

    private static Texture2D CreateNearHillsTexture(GraphicsDevice gd, int width, int height)
    {
        var p = new Color[width * height];
        var outline = new Color(46, 118, 52);
        var sunlit = new Color(132, 214, 92);
        var topGreen = new Color(96, 188, 72);
        var midGreen = new Color(74, 162, 58);
        var baseGreen = new Color(56, 134, 48);

        for (int x = 0; x < width; x++)
        {
            float a = (float)x / width * MathF.PI * 2f;
            float ridge = 28f + MathF.Sin(a * 2f + 2.4f) * 14f + MathF.Sin(a * 4f + 0.7f) * 6f;
            int ridgeY = Math.Clamp((int)MathF.Round(ridge), 6, height - 8);

            for (int y = ridgeY; y < height; y++)
            {
                int depth = y - ridgeY;
                p[y * width + x] = depth switch { 0 => outline, 1 or 2 => sunlit, <= 10 => topGreen, <= 24 => midGreen, _ => baseGreen };
            }
        }

        return MakeTex(gd, width, height, p);
    }

    /// <summary>
    /// Tekstur cahaya radial lembut 128x128 prosedural:
    /// - Jarak ternormalisasi d >= 1.0 -> Color.Transparent (nol sudut kotak)
    /// - d < 1.0 -> kurva pudar halus alpha = Pow(1 - d, 2.2f) dalam format premultiplied agar mulus di AlphaBlend &amp; Additive.
    /// </summary>
    private static Texture2D CreateRadialGlowTexture(GraphicsDevice gd, int size)
    {
        var pixels = new Color[size * size];
        float half = size * 0.5f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f - half) / half;
                float dy = (y + 0.5f - half) / half;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                if (d >= 1.0f)
                {
                    pixels[y * size + x] = Color.Transparent;
                    continue;
                }

                float alpha = MathF.Pow(1f - d, 2.2f);
                byte a = (byte)Math.Clamp((int)MathF.Round(alpha * 255f), 0, 255);
                pixels[y * size + x] = Color.FromNonPremultiplied(255, 255, 255, a);
            }
        }

        return MakeTex(gd, size, size, pixels);
    }

    /// <summary>
    /// Membangun 4 varian tekstur dasar tanah 16x16 yang 100% seamless (tanpa garis terang/gelap horizontal
    /// di tepi atas/bawah kecuali lapisan rumput saat withGrass=true). Menggunakan palet cokelat hangat
    /// dengan gradasi mikro organik dan variasi posisi batu kerikil di tiap varian 0..3.
    /// </summary>
    private static Color[] BuildSeamlessDirtPixels(int variant, bool withGrass)
    {
        var p = new Color[16 * 16];
        var soilBase = new Color(148, 94, 52);
        var soilWarm = new Color(158, 102, 58);
        var soilSoft = new Color(140, 88, 48);
        var soilMid = new Color(132, 82, 44);
        var soilDeep = new Color(118, 70, 36);
        var pebbleHi = new Color(176, 158, 140);
        var pebbleBody = new Color(132, 118, 104);
        var pebbleShade = new Color(96, 78, 64);

        int seed = 701 + variant * 131;

        // Latar organik seragam tanpa garis horizontal (seluruh tepi x=0/15 & y=0/15 menyatu mulus)
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int n = Hash(x, y, seed);
                int coarse = Hash(x / 2, y / 2, seed + 19);
                Color c = (coarse % 5) switch
                {
                    0 => soilWarm,
                    1 => soilSoft,
                    2 => soilMid,
                    _ => soilBase,
                };

                // Sedikit variasi mikro per piksel (hindari kontras tajam di tepi blok)
                bool isInterior = x is >= 1 and <= 14 && y is >= 1 and <= 14;
                if (isInterior)
                {
                    if ((n & 15) == 0) c = soilWarm;
                    else if ((n & 15) == 1) c = soilMid;
                    else if ((n & 31) == 2) c = soilDeep;
                }

                p[y * 16 + x] = c;
            }
        }

        void Patch(int x0, int y0, int w, int h, Color c)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    if (x >= 1 && x <= 14 && y >= 1 && y <= 14)
                        p[y * 16 + x] = c;
        }

        void Pebble(int x, int y)
        {
            Patch(x, y, 2, 1, pebbleHi);
            Patch(x, y + 1, 2, 1, pebbleBody);
            Patch(x, y + 2, 2, 1, pebbleShade);
        }

        void SmallGrain(int x, int y)
        {
            Patch(x, y, 2, 1, soilDeep);
            Patch(x + 1, y + 1, 1, 1, soilWarm);
        }

        // Variasi posisi kerikil & bercak tanah alami berbeda untuk setiap varian 0..3
        switch (variant & 3)
        {
            case 0:
                Patch(2, 3, 3, 2, soilMid);
                Patch(10, 5, 3, 2, soilWarm);
                Patch(5, 11, 3, 2, soilSoft);
                if (!withGrass) Pebble(3, 5);
                Pebble(11, 10);
                SmallGrain(7, 8);
                break;

            case 1:
                Patch(9, 2, 3, 2, soilMid);
                Patch(3, 9, 3, 2, soilWarm);
                Patch(11, 12, 2, 2, soilDeep);
                if (!withGrass) Pebble(10, 4);
                Pebble(4, 11);
                SmallGrain(7, 6);
                break;

            case 2:
                Patch(4, 2, 3, 2, soilWarm);
                Patch(10, 8, 3, 2, soilMid);
                Patch(2, 12, 3, 2, soilSoft);
                if (!withGrass) Pebble(6, 4);
                Pebble(12, 11);
                SmallGrain(4, 9);
                break;

            default:
                Patch(2, 6, 3, 2, soilMid);
                Patch(9, 10, 3, 2, soilWarm);
                Patch(6, 12, 3, 2, soilMid);
                if (!withGrass) Pebble(11, 3);
                Pebble(3, 10);
                SmallGrain(8, 7);
                break;
        }

        if (withGrass)
        {
            var grassHi = new Color(142, 228, 86);
            var grassMain = new Color(96, 198, 56);
            var grassShade = new Color(56, 144, 38);

            int[] heights = (variant & 3) switch
            {
                0 => [4, 5, 5, 4, 3, 4, 5, 6, 5, 4, 4, 5, 6, 5, 4, 4],
                1 => [4, 4, 5, 6, 5, 4, 4, 5, 4, 3, 4, 5, 5, 4, 5, 4],
                2 => [4, 5, 4, 4, 5, 6, 5, 4, 5, 5, 4, 3, 4, 5, 4, 4],
                _ => [4, 4, 5, 4, 4, 5, 6, 5, 4, 5, 6, 5, 4, 4, 5, 4],
            };

            for (int x = 0; x < 16; x++)
            {
                int h = heights[x];
                for (int y = 0; y < h; y++)
                    p[y * 16 + x] = y <= 1 ? grassHi : y == h - 1 ? grassShade : grassMain;
                if (h < 16) p[h * 16 + x] = soilDeep;
            }
        }

        return p;
    }

    private static Texture2D CreateCaveBackgroundTexture(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var baseWall = new Color(96, 62, 38);
        var midWall = new Color(82, 52, 32);
        var darkWall = new Color(68, 42, 26);
        var stoneBit = new Color(110, 74, 46);

        for (int i = 0; i < 256; i++) p[i] = baseWall;

        void Block(int x0, int y0, int w, int h, Color c)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    if (x >= 0 && x < 16 && y >= 0 && y < 16)
                        p[y * 16 + x] = c;
        }

        Block(1, 2, 4, 2, midWall);
        Block(9, 1, 5, 3, darkWall);
        Block(3, 6, 4, 3, stoneBit);
        Block(11, 7, 4, 2, midWall);
        Block(1, 11, 5, 3, darkWall);
        Block(8, 12, 4, 2, stoneBit);
        Block(13, 13, 2, 2, darkWall);

        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateWhiteDoorTexture(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var outline = new Color(34, 30, 32);
        var frameLight = new Color(214, 220, 228);
        var frameSoft = new Color(178, 186, 198);
        var frameDark = new Color(142, 150, 164);
        var woodWhite = new Color(248, 250, 252);
        var woodCream = new Color(234, 238, 244);
        var plankSeam = new Color(204, 210, 220);
        var knobGold = new Color(244, 188, 38);
        var knobHi = new Color(255, 240, 144);
        var knobShade = new Color(174, 122, 18);

        for (int y = 0; y < 16; y++)
        {
            int left = y switch { 0 => 4, 1 => 2, 2 => 2, _ => 1 };
            int right = 15 - left;

            for (int x = left; x <= right; x++)
            {
                bool isTopOutline = y == 0 || (y == 1 && (x == 2 || x == 3 || x == 12 || x == 13)) || (y == 2 && (x == 2 || x == 13));
                bool isSideOutline = x == left || x == right;
                bool isBottomOutline = y == 15;

                if (isTopOutline || isSideOutline || isBottomOutline)
                {
                    p[y * 16 + x] = outline;
                    continue;
                }

                bool isFrame = x == left + 1 || x == right - 1 || y == 1 || y == 14 || (y == 2 && (x == 3 || x == 12));
                if (isFrame)
                {
                    p[y * 16 + x] = (x == right - 1 || y == 14) ? frameDark : (y <= 2 || x == left + 1 ? frameLight : frameSoft);
                    continue;
                }

                if (x == 6 || x == 10) p[y * 16 + x] = plankSeam;
                else if (y == 3 || y == 13) p[y * 16 + x] = woodCream;
                else p[y * 16 + x] = ((x + y) % 5 == 0 && y > 4) ? woodCream : woodWhite;
            }
        }

        p[8 * 16 + 11] = knobHi;
        p[8 * 16 + 12] = knobGold;
        p[9 * 16 + 11] = knobGold;
        p[9 * 16 + 12] = knobShade;

        return MakeTex(gd, 16, 16, p);
    }

    private static Color[] BuildBedrockPixels()
    {
        var p = new Color[16 * 16];
        var c0 = new Color(44, 48, 56);
        var c1 = new Color(62, 68, 78);
        var c2 = new Color(86, 92, 104);
        var crack = new Color(26, 28, 36);

        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int n = Hash(x / 2, y / 2, 101);
                Color c = (n % 4) switch { 0 => c0, 1 => c1, 2 => c2, _ => c1 };
                if ((x + y) % 7 == 0 && (Hash(x, y, 5) % 3 == 0)) c = crack;
                p[y * 16 + x] = c;
            }
        }

        return p;
    }

    private static Color[] BuildStonePixels()
    {
        var p = new Color[16 * 16];
        var baseGrey = new Color(138, 146, 156);
        var lightGrey = new Color(172, 180, 190);
        var darkGrey = new Color(98, 104, 114);
        var mortar = new Color(72, 78, 88);

        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                bool seamH = y == 7 || y == 15;
                bool seamV = (y < 7 && x == 8) || (y >= 7 && (x == 4 || x == 12));
                if (seamH || seamV)
                {
                    p[y * 16 + x] = mortar;
                    continue;
                }

                int n = Hash(x / 2, y / 2, 211);
                Color c = (n % 4) switch { 0 => lightGrey, 1 => darkGrey, _ => baseGrey };
                if (y == 1 || y == 8) c = Color.Lerp(c, lightGrey, 0.45f);
                p[y * 16 + x] = c;
            }
        }

        return p;
    }

    private static Color[] BuildWoodPixels(bool isBackground)
    {
        var p = new Color[16 * 16];
        var baseWood = isBackground ? new Color(122, 82, 46) : new Color(184, 128, 68);
        var lightWood = isBackground ? new Color(142, 98, 58) : new Color(212, 156, 90);
        var darkWood = isBackground ? new Color(88, 56, 30) : new Color(138, 90, 44);
        var seam = isBackground ? new Color(64, 38, 20) : new Color(94, 56, 26);

        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                if (y % 4 == 0) { p[y * 16 + x] = seam; continue; }
                int plank = y / 4;
                int jointX = (plank % 2 == 0) ? 7 : 13;
                if (x == jointX) { p[y * 16 + x] = seam; continue; }

                int n = Hash(x / 2, y, 55);
                Color c = (y % 4 == 1) ? lightWood : (n % 4 == 0 ? darkWood : baseWood);
                if (!isBackground && (y % 4 == 2) && (x == 2 || x == 13)) c = seam;
                p[y * 16 + x] = c;
            }
        }

        return p;
    }

    private static Color[] BuildBrickPixels()
    {
        var p = new Color[16 * 16];
        var mortar = new Color(214, 206, 194);
        var red1 = new Color(192, 74, 54);
        var red2 = new Color(164, 58, 42);
        var redHi = new Color(218, 100, 76);

        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                int row = y / 4;
                bool isMortarH = y % 4 == 0;
                int offset = (row % 2) * 4;
                bool isMortarV = ((x + offset) % 8) == 0;

                if (isMortarH || isMortarV) p[y * 16 + x] = mortar;
                else if (y % 4 == 1) p[y * 16 + x] = redHi;
                else p[y * 16 + x] = (Hash(x / 2, y, 88) % 3 == 0) ? red2 : red1;
            }
        }

        return p;
    }

    private static Texture2D CreateGlassTexture(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var frame = Color.FromNonPremultiplied(214, 244, 255, 240);
        var inner = Color.FromNonPremultiplied(165, 228, 252, 95);
        var gleam = Color.FromNonPremultiplied(255, 255, 255, 210);

        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                p[y * 16 + x] = (x == 0 || y == 0 || x == 15 || y == 15) ? frame
                    : ((x + y == 6) || (x + y == 7 && x >= 3 && x <= 5) || (x + y == 14 && x >= 9 && x <= 11)) ? gleam : inner;

        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateTorchTexture(GraphicsDevice gd, int frame)
    {
        var p = new Color[16 * 16];
        var outline = new Color(36, 22, 14);
        var stick = new Color(148, 94, 44);
        var stickDark = new Color(100, 60, 26);
        var metal = new Color(82, 88, 98);
        var flameOuter = new Color(255, 112, 20);
        var flameMid = new Color(255, 202, 42);
        var flameCore = new Color(255, 252, 198);

        for (int y = 7; y <= 14; y++)
        {
            p[y * 16 + 6] = outline; p[y * 16 + 7] = stick; p[y * 16 + 8] = stickDark; p[y * 16 + 9] = outline;
        }
        for (int x = 6; x <= 9; x++) p[7 * 16 + x] = metal;

        int sway = frame == 0 ? 0 : 1;
        for (int y = 2; y <= 6; y++)
        {
            int halfW = y switch { 2 => 0, 3 => 1, 4 => 2, 5 => 2, _ => 1 };
            int cx = y <= 3 ? 7 + sway : 7;
            for (int x = cx - halfW; x <= cx + halfW; x++)
            {
                if (x < 0 || x >= 16) continue;
                Color c = flameOuter;
                if (y >= 4 && Math.Abs(x - 7) <= 1) c = flameMid;
                if (y == 5 && (x == 7 || x == 8)) c = flameCore;
                p[y * 16 + x] = c;
            }
        }

        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateWorldLockTexture(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var outline = new Color(46, 32, 12);
        var gold = new Color(252, 202, 44);
        var goldHi = new Color(255, 242, 144);
        var goldDark = new Color(186, 128, 18);
        var steel = new Color(224, 234, 246);
        var steelDark = new Color(132, 144, 160);
        var keyhole = new Color(42, 28, 12);

        for (int y = 1; y <= 6; y++)
            for (int x = 4; x <= 11; x++)
                if ((y <= 2 && x >= 5 && x <= 10) || (y >= 2 && (x == 4 || x == 5 || x == 10 || x == 11)))
                    p[y * 16 + x] = (x == 4 || y == 1) ? steel : steelDark;

        for (int y = 6; y <= 14; y++)
            for (int x = 2; x <= 13; x++)
                p[y * 16 + x] = (x == 2 || x == 13 || y == 6 || y == 14) ? outline
                    : (x == 12 || y == 13) ? goldDark
                    : (x == 3 || y == 7) ? goldHi : gold;

        p[9 * 16 + 7] = keyhole; p[9 * 16 + 8] = keyhole;
        p[10 * 16 + 7] = keyhole; p[10 * 16 + 8] = keyhole;
        p[11 * 16 + 7] = keyhole; p[12 * 16 + 7] = keyhole;

        return MakeTex(gd, 16, 16, p);
    }

    /// <summary>
    /// Ikon Seed 16x16 khas Growtopia (bentuk biji/kantong benih dwitunggal dengan tunas kecil di puncak
    /// berwarna PrimaryColor + SecondaryColor + outline gelap 1px agar mudah dibedakan dari blok biasa).
    /// </summary>
    private static Texture2D CreateSeedIconTexture(GraphicsDevice gd, RgbColor primaryRgb, RgbColor secondaryRgb)
    {
        var p = new Color[16 * 16];
        var outline = new Color(26, 20, 16);
        var primary = new Color(primaryRgb.R, primaryRgb.G, primaryRgb.B);
        var primaryHi = Color.Lerp(primary, Color.White, 0.40f);
        var primaryShade = Color.Lerp(primary, Color.Black, 0.30f);
        var secondary = new Color(secondaryRgb.R, secondaryRgb.G, secondaryRgb.B);
        var secondaryHi = Color.Lerp(secondary, Color.White, 0.32f);

        // Badan biji oval meruncing ke atas (x: 4..11, y: 5..13)
        for (int y = 5; y <= 13; y++)
        {
            int halfW = y switch
            {
                5 => 1,
                6 => 2,
                7 or 8 => 3,
                9 or 10 or 11 => 4,
                12 => 3,
                _ => 2,
            };

            int left = 8 - halfW;
            int right = 7 + halfW;
            for (int x = left; x <= right; x++)
            {
                bool isStripe = (x == 7 || x == 8) && y >= 7 && y <= 11;
                if (isStripe)
                {
                    p[y * 16 + x] = y <= 8 ? secondaryHi : secondary;
                }
                else if (x <= left + 1 && y <= 9)
                {
                    p[y * 16 + x] = primaryHi;
                }
                else if (x >= right - 1 || y >= 12)
                {
                    p[y * 16 + x] = primaryShade;
                }
                else
                {
                    p[y * 16 + x] = primary;
                }
            }
        }

        // Tunas daun mungil di puncak biji (y: 2..4)
        p[4 * 16 + 7] = secondary;
        p[4 * 16 + 8] = secondary;
        p[3 * 16 + 6] = secondaryHi;
        p[3 * 16 + 7] = secondary;
        p[3 * 16 + 9] = secondaryHi;
        p[2 * 16 + 5] = secondaryHi;
        p[2 * 16 + 10] = secondary;

        // Kilau putih kecil pada bahu kiri biji
        p[7 * 16 + 6] = Color.White;

        // Outline 1px gelap otomatis mengelilingi siluet seed
        var outlined = new Color[16 * 16];
        Array.Copy(p, outlined, p.Length);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                if (p[y * 16 + x].A != 0) continue;
                bool adj =
                    (x > 0 && p[y * 16 + (x - 1)].A > 0) ||
                    (x + 1 < 16 && p[y * 16 + (x + 1)].A > 0) ||
                    (y > 0 && p[(y - 1) * 16 + x].A > 0) ||
                    (y + 1 < 16 && p[(y + 1) * 16 + x].A > 0);
                if (adj) outlined[y * 16 + x] = outline;
            }
        }

        return MakeTex(gd, 16, 16, outlined);
    }

    /// <summary>
    /// Generator 4 tahap pertumbuhan pohon 16x16 berdasarkan PrimaryColor (buah/aksen) dan SecondaryColor (daun):
    /// - Tahap 0 (0-33%): tunas kecil di atas tanah
    /// - Tahap 1 (33-66%): batang muda dengan sedikit daun
    /// - Tahap 2 (66-99%): pohon penuh tanpa buah
    /// - Tahap 3 (100% / Ready): pohon penuh dengan 4 buah kecil berwarna PrimaryColor + kilau
    /// </summary>
    private static Texture2D CreateColoredTreeStageTexture(
        GraphicsDevice gd,
        int stage,
        RgbColor primaryRgb,
        RgbColor secondaryRgb)
    {
        var p = new Color[16 * 16];
        var outline = new Color(24, 34, 22);
        var trunk = new Color(138, 86, 42);
        var trunkHi = new Color(166, 108, 56);
        var trunkDark = new Color(92, 54, 24);
        var leaf = new Color(secondaryRgb.R, secondaryRgb.G, secondaryRgb.B);
        var leafHi = Color.Lerp(leaf, Color.White, 0.34f);
        var leafDark = Color.Lerp(leaf, Color.Black, 0.30f);
        var fruit = new Color(primaryRgb.R, primaryRgb.G, primaryRgb.B);
        var fruitHi = Color.Lerp(fruit, Color.White, 0.55f);
        var fruitShade = Color.Lerp(fruit, Color.Black, 0.28f);

        void Set(int x, int y, Color c)
        {
            if (x >= 0 && x < 16 && y >= 0 && y < 16) p[y * 16 + x] = c;
        }

        if (stage <= 0)
        {
            // Tahap 0 (0-33%): tunas mungil di atas tanah
            for (int y = 11; y <= 14; y++) { Set(7, y, trunk); Set(8, y, trunkDark); }
            Set(6, 10, leafHi); Set(7, 10, leaf); Set(8, 10, leaf); Set(9, 10, leafHi);
            Set(5, 11, leafHi); Set(6, 11, leaf); Set(9, 11, leaf); Set(10, 11, leafDark);
            Set(7, 9, leafHi); Set(8, 9, leaf);
        }
        else if (stage == 1)
        {
            // Tahap 1 (33-66%): batang muda dengan mahkota daun kecil
            for (int y = 8; y <= 14; y++) { Set(7, y, trunk); Set(8, y, trunkDark); }
            for (int y = 5; y <= 9; y++)
            {
                int halfW = y is 5 or 9 ? 2 : 3;
                for (int x = 8 - halfW; x <= 7 + halfW; x++)
                {
                    Color c = y <= 6 ? leafHi : (y == 9 ? leafDark : leaf);
                    Set(x, y, c);
                }
            }
        }
        else
        {
            // Tahap 2 (66-99%) & Tahap 3 (100% / Ready): batang kokoh & kanopi rimbun
            for (int y = 8; y <= 14; y++)
            {
                Set(6, y, trunkHi);
                Set(7, y, trunk);
                Set(8, y, trunk);
                Set(9, y, trunkDark);
            }

            // Akar tipis di dasar batang
            Set(5, 14, trunk);
            Set(10, 14, trunkDark);

            for (int y = 1; y <= 9; y++)
            {
                int halfW = y switch
                {
                    1 => 3,
                    2 => 5,
                    3 or 4 or 5 or 6 => 6,
                    7 or 8 => 5,
                    _ => 3,
                };

                for (int x = 8 - halfW; x <= 7 + halfW; x++)
                {
                    int n = Hash(x, y, 404);
                    Color c = (y <= 3 || n % 5 == 0)
                        ? leafHi
                        : (y >= 8 || n % 6 == 0 ? leafDark : leaf);
                    Set(x, y, c);
                }
            }

            // Tahap 3 (100% / Ready): 4 buah kecil berwarna PrimaryColor + highlight cerah
            if (stage >= 3)
            {
                void DrawFruit(int fx, int fy)
                {
                    Set(fx, fy, fruitHi);
                    Set(fx + 1, fy, fruit);
                    Set(fx, fy + 1, fruit);
                    Set(fx + 1, fy + 1, fruitShade);
                }

                DrawFruit(3, 4);
                DrawFruit(11, 3);
                DrawFruit(6, 6);
                DrawFruit(10, 7);
            }
        }

        // Outline gelap 1px otomatis agar siluet pohon tajam di atas langit/awan maupun gua
        var outlined = new Color[16 * 16];
        Array.Copy(p, outlined, p.Length);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                if (p[y * 16 + x].A != 0) continue;
                bool adj =
                    (x > 0 && p[y * 16 + (x - 1)].A > 0) ||
                    (x + 1 < 16 && p[y * 16 + (x + 1)].A > 0) ||
                    (y > 0 && p[(y - 1) * 16 + x].A > 0) ||
                    (y + 1 < 16 && p[(y + 1) * 16 + x].A > 0);
                if (adj) outlined[y * 16 + x] = outline;
            }
        }

        return MakeTex(gd, 16, 16, outlined);
    }

    private static Texture2D CreateEllipseShadowTexture(GraphicsDevice gd, int w, int h)
    {
        var p = new Color[w * h];
        float rx = w * 0.5f, ry = h * 0.5f;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float nx = (x + 0.5f - rx) / rx;
                float ny = (y + 0.5f - ry) / ry;
                float d = nx * nx + ny * ny;
                if (d <= 1.0f)
                {
                    byte alpha = (byte)((1f - d * 0.65f) * 115);
                    p[y * w + x] = new Color((byte)0, (byte)0, (byte)0, alpha);
                }
            }
        }

        return MakeTex(gd, w, h, p);
    }

    private static Texture2D CreateGemSparkleTexture(GraphicsDevice gd)
    {
        const int s = 7;
        var p = new Color[s * s];
        var core = Color.White;
        var glow = Color.FromNonPremultiplied(180, 255, 224, 200);
        var tip = Color.FromNonPremultiplied(255, 255, 255, 110);

        p[3 * s + 3] = core; p[2 * s + 3] = core; p[4 * s + 3] = core; p[3 * s + 2] = core; p[3 * s + 4] = core;
        p[1 * s + 3] = glow; p[5 * s + 3] = glow; p[3 * s + 1] = glow; p[3 * s + 5] = glow;
        p[0 * s + 3] = tip; p[6 * s + 3] = tip; p[3 * s + 0] = tip; p[3 * s + 6] = tip;
        return MakeTex(gd, s, s, p);
    }

    /// <summary>
    /// 4 tahap sprite retakan fisik 16x16 (premultiplied alpha agar bebas garis putih mencolok):
    /// - Garis retakan utama gelap semi-transparan (20, 14, 10, 195) bercabang alami dari pusat blok
    /// - Garis highlight tipis 1px di sisi retakan (255, 255, 255, 45) agar menyatu di atas Dirt, Brick, Wood, dan Rock
    /// </summary>
    private static Texture2D CreateCrackTexture(GraphicsDevice gd, int level)
    {
        var p = new Color[16 * 16];
        var crack = Color.FromNonPremultiplied(20, 14, 10, 195);
        var crackDeep = Color.FromNonPremultiplied(14, 10, 7, 220);
        var highlight = Color.FromNonPremultiplied(255, 255, 255, 45);

        void Plot(int x, int y, bool heavy = false)
        {
            if (x < 0 || x >= 16 || y < 0 || y >= 16)
            {
                return;
            }

            p[y * 16 + x] = heavy ? crackDeep : crack;
            // Highlight 1px tipis hanya di satu sisi kanan agar terasa seperti bibir celah fisik
            if (x + 1 < 16 && p[y * 16 + (x + 1)].A == 0)
            {
                p[y * 16 + (x + 1)] = highlight;
            }
        }

        // Tahap 1: retak halus pendek dari pusat blok
        Plot(7, 7); Plot(8, 7); Plot(8, 8); Plot(7, 9); Plot(9, 8);

        // Tahap 2: 3 cabang alami mulai memanjang
        if (level >= 2)
        {
            Plot(6, 6); Plot(5, 6); Plot(4, 5);
            Plot(10, 9); Plot(11, 9); Plot(11, 10);
            Plot(9, 6); Plot(10, 5);
            Plot(6, 10); Plot(5, 11);
        }

        // Tahap 3: percabangan meluas mendekati keempat tepi blok
        if (level >= 3)
        {
            Plot(3, 5); Plot(2, 4); Plot(3, 7); Plot(2, 8);
            Plot(12, 10); Plot(13, 11); Plot(13, 12);
            Plot(11, 4); Plot(12, 3); Plot(12, 2);
            Plot(4, 11); Plot(4, 12); Plot(3, 13);
            Plot(8, 10); Plot(8, 11); Plot(9, 12);
            Plot(7, 5); Plot(6, 4); Plot(6, 3);
        }

        // Tahap 4: celah retak berat & cabang halus menembus tepi (hampir pecah)
        if (level >= 4)
        {
            Plot(1, 3, heavy: true); Plot(1, 8, heavy: true); Plot(0, 9, heavy: true);
            Plot(14, 12, heavy: true); Plot(14, 13, heavy: true);
            Plot(13, 2, heavy: true); Plot(2, 13, heavy: true); Plot(2, 14, heavy: true);
            Plot(9, 13, heavy: true); Plot(10, 14, heavy: true);
            Plot(5, 2, heavy: true); Plot(5, 1, heavy: true);
            Plot(12, 7, heavy: true); Plot(13, 7, heavy: true); Plot(14, 6, heavy: true);
            Plot(7, 7, heavy: true); Plot(8, 8, heavy: true); Plot(7, 8, heavy: true);
            Plot(6, 7, heavy: true); Plot(9, 9, heavy: true);
        }

        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateFistIcon(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var outline = new Color(36, 22, 14);
        var skin = new Color(246, 200, 156);
        var skinHi = new Color(255, 226, 192);
        var skinShade = new Color(208, 152, 106);

        void Rect(int x0, int y0, int w, int h, Color c)
        {
            for (int y = y0; y < y0 + h; y++)
                for (int x = x0; x < x0 + w; x++)
                    if (x >= 0 && x < 16 && y >= 0 && y < 16) p[y * 16 + x] = c;
        }

        Rect(3, 3, 10, 9, outline); Rect(2, 6, 3, 5, outline); Rect(5, 11, 6, 3, outline);
        Rect(4, 4, 8, 7, skin); Rect(4, 4, 8, 2, skinHi); Rect(4, 9, 8, 2, skinShade);
        p[4 * 16 + 6] = outline; p[5 * 16 + 6] = outline; p[6 * 16 + 6] = outline;
        p[4 * 16 + 8] = outline; p[5 * 16 + 8] = outline; p[6 * 16 + 8] = outline;
        p[4 * 16 + 10] = outline; p[5 * 16 + 10] = outline; p[6 * 16 + 10] = outline;
        Rect(3, 7, 3, 3, skin); p[7 * 16 + 3] = skinHi; Rect(6, 11, 4, 2, skinShade);
        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateWrenchIcon(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var outline = new Color(28, 34, 44);
        var steel = new Color(184, 198, 214);
        var steelHi = new Color(232, 240, 250);
        var steelDark = new Color(122, 136, 154);

        void Plot(int x, int y, Color c) { if (x >= 0 && x < 16 && y >= 0 && y < 16) p[y * 16 + x] = c; }

        for (int i = 0; i < 8; i++)
        {
            int x = 3 + i, y = 12 - i;
            Plot(x - 1, y, outline); Plot(x + 1, y, outline); Plot(x, y - 1, outline); Plot(x, y + 1, outline);
        }
        for (int y = 2; y <= 7; y++) for (int x = 8; x <= 13; x++) Plot(x, y, outline);
        for (int y = 3; y <= 6; y++) for (int x = 9; x <= 12; x++) Plot(x, y, y == 3 ? steelHi : steel);
        Plot(11, 3, Color.Transparent); Plot(12, 3, Color.Transparent); Plot(12, 4, Color.Transparent);
        Plot(13, 2, Color.Transparent); Plot(13, 3, Color.Transparent);
        for (int i = 0; i < 7; i++) { int x = 3 + i, y = 12 - i; Plot(x, y, i % 2 == 0 ? steelHi : steel); Plot(x + 1, y, steelDark); }
        Plot(2, 13, outline); Plot(3, 13, steelDark); Plot(4, 13, outline); Plot(3, 14, outline);
        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateGemIcon(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var outline = new Color(12, 54, 42);
        var gemDark = new Color(24, 154, 108);
        var gemMid = new Color(56, 224, 152);
        var gemBright = new Color(156, 255, 212);

        for (int y = 1; y <= 14; y++)
        {
            int distY = Math.Abs(y - 7);
            int halfW = Math.Max(0, 6 - distY);
            for (int x = 7 - halfW; x <= 8 + halfW; x++)
            {
                bool isBorder = y == 1 || y == 14 || x == 7 - halfW || x == 8 + halfW;
                p[y * 16 + x] = isBorder ? outline : (y <= 5 && x <= 8) ? gemBright : (y >= 9 || x >= 10) ? gemDark : gemMid;
            }
        }

        p[4 * 16 + 6] = Color.White; p[5 * 16 + 6] = Color.White; p[4 * 16 + 7] = Color.White;
        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateGlobeIcon(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var outline = new Color(18, 48, 86);
        var ocean = new Color(54, 164, 242);
        var oceanHi = new Color(118, 208, 255);
        var land = new Color(92, 206, 86);
        var landDark = new Color(58, 156, 62);

        for (int y = 1; y <= 14; y++)
            for (int x = 1; x <= 14; x++)
            {
                float d = MathF.Sqrt((x - 7.5f) * (x - 7.5f) + (y - 7.5f) * (y - 7.5f));
                if (d > 6.6f) continue;
                if (d >= 5.5f) p[y * 16 + x] = outline;
                else
                {
                    bool isLand = (x >= 4 && x <= 7 && y >= 4 && y <= 8) || (x >= 9 && x <= 12 && y >= 7 && y <= 11) || (x >= 8 && x <= 10 && y >= 3 && y <= 5);
                    p[y * 16 + x] = isLand ? (y >= 9 ? landDark : land) : ((x <= 6 && y <= 6) ? oceanHi : ocean);
                }
            }

        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateMenuIcon(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var outline = new Color(18, 42, 68);
        var white = new Color(238, 246, 255);

        void Bar(int y)
        {
            for (int x = 2; x <= 13; x++)
            {
                p[(y - 1) * 16 + x] = outline; p[y * 16 + x] = white; p[(y + 1) * 16 + x] = white; p[(y + 2) * 16 + x] = outline;
            }
        }

        Bar(3); Bar(7); Bar(11);
        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D CreateChatIcon(GraphicsDevice gd)
    {
        var p = new Color[16 * 16];
        var outline = new Color(18, 44, 72);
        var bubble = Color.White;
        var dot = new Color(54, 152, 224);

        for (int y = 2; y <= 11; y++)
            for (int x = 2; x <= 13; x++)
                if (!((y is 2 or 11) && (x is 2 or 13)))
                    p[y * 16 + x] = (y == 2 || y == 11 || x == 2 || x == 13) ? outline : bubble;

        p[12 * 16 + 4] = outline; p[12 * 16 + 5] = bubble; p[12 * 16 + 6] = outline;
        p[13 * 16 + 4] = outline; p[13 * 16 + 5] = outline;
        p[6 * 16 + 5] = dot; p[7 * 16 + 5] = dot; p[6 * 16 + 8] = dot; p[7 * 16 + 8] = dot; p[6 * 16 + 11] = dot; p[7 * 16 + 11] = dot;
        return MakeTex(gd, 16, 16, p);
    }

    private static Texture2D MakeTex(GraphicsDevice gd, int w, int h, Color[] data)
    {
        var tex = new Texture2D(gd, w, h);
        tex.SetData(data);
        return tex;
    }

    private static int Hash(int x, int y, int seed)
    {
        unchecked
        {
            int h = seed ^ (x * 374761393) ^ (y * 668265263);
            h = (h ^ (h >> 13)) * 1274126177;
            return Math.Abs(h ^ (h >> 16));
        }
    }

    public void Dispose()
    {
        foreach (Texture2D tex in _ownedTextures) tex.Dispose();
        _ownedTextures.Clear();
    }
}
