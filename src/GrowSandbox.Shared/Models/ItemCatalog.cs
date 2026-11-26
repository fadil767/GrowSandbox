using GrowSandbox.Shared.Constants;

namespace GrowSandbox.Shared.Models;

public enum ItemLayer : byte
{
    None = 0,
    Foreground = 1,
    Background = 2,
}

public readonly record struct RgbColor(byte R, byte G, byte B);

public readonly record struct SeedProperties(
    ushort SeedId,
    ushort BlockId,
    string SeedName,
    string TreeName,
    int GrowTimeSeconds,
    RgbColor PrimaryColor,
    RgbColor SecondaryColor,
    byte GemDropMin,
    byte GemDropMax,
    float SeedDropChance,
    byte HarvestMinFruit,
    byte HarvestMaxFruit);

public readonly record struct ItemDefinition(
    ushort Id,
    string Name,
    ItemLayer Layer,
    bool IsSolid,
    byte BreakHits,
    bool IsBreakable,
    bool IsPlaceable,
    ushort DropItemId,
    bool IsSeed = false);

/// <summary>
/// Katalog item, blok, dan pasangan Seed/Tree tunggal di <c>GrowSandbox.Shared</c>
/// agar fisika kolisi klien, visual pohon/seed, dan validasi farming server selalu sinkron.
/// </summary>
public static class ItemCatalog
{
    public const ushort TreeRipeProgress = 100;
    public const ushort MaxInventoryStack = 200;
    public const ushort MaxDropStack = 200;
    public const int MaxGemBalance = 1_000_000;
    public const float HarvestBonusSeedChance = 0.35f;

    private static readonly Dictionary<ushort, SeedProperties> SeedsBySeedId = new()
    {
        [TileIds.DirtSeed] = new(
            SeedId: TileIds.DirtSeed,
            BlockId: TileIds.Dirt,
            SeedName: "Dirt Seed",
            TreeName: "Dirt Tree",
            GrowTimeSeconds: 15,
            PrimaryColor: new RgbColor(168, 108, 56),
            SecondaryColor: new RgbColor(96, 198, 58),
            GemDropMin: 0,
            GemDropMax: 2,
            SeedDropChance: 0.30f,
            HarvestMinFruit: 2,
            HarvestMaxFruit: 4),

        [TileIds.WoodSeed] = new(
            SeedId: TileIds.WoodSeed,
            BlockId: TileIds.Wood,
            SeedName: "Wood Seed",
            TreeName: "Wood Tree",
            GrowTimeSeconds: 25,
            PrimaryColor: new RgbColor(204, 144, 74),
            SecondaryColor: new RgbColor(74, 178, 66),
            GemDropMin: 1,
            GemDropMax: 3,
            SeedDropChance: 0.30f,
            HarvestMinFruit: 2,
            HarvestMaxFruit: 4),

        [TileIds.RockSeed] = new(
            SeedId: TileIds.RockSeed,
            BlockId: TileIds.Rock,
            SeedName: "Rock Seed",
            TreeName: "Rock Tree",
            GrowTimeSeconds: 30,
            PrimaryColor: new RgbColor(154, 164, 178),
            SecondaryColor: new RgbColor(92, 162, 142),
            GemDropMin: 1,
            GemDropMax: 4,
            SeedDropChance: 0.30f,
            HarvestMinFruit: 2,
            HarvestMaxFruit: 4),

        [TileIds.BrickSeed] = new(
            SeedId: TileIds.BrickSeed,
            BlockId: TileIds.Brick,
            SeedName: "Brick Seed",
            TreeName: "Brick Tree",
            GrowTimeSeconds: 45,
            PrimaryColor: new RgbColor(214, 82, 58),
            SecondaryColor: new RgbColor(126, 184, 62),
            GemDropMin: 1,
            GemDropMax: 5,
            SeedDropChance: 0.30f,
            HarvestMinFruit: 2,
            HarvestMaxFruit: 4),

        [TileIds.GlassSeed] = new(
            SeedId: TileIds.GlassSeed,
            BlockId: TileIds.Glass,
            SeedName: "Glass Seed",
            TreeName: "Glass Tree",
            GrowTimeSeconds: 60,
            PrimaryColor: new RgbColor(148, 232, 255),
            SecondaryColor: new RgbColor(92, 208, 224),
            GemDropMin: 2,
            GemDropMax: 6,
            SeedDropChance: 0.30f,
            HarvestMinFruit: 2,
            HarvestMaxFruit: 4),
    };

    private static readonly Dictionary<ushort, SeedProperties> SeedsByBlockId = new()
    {
        [TileIds.Dirt] = SeedsBySeedId[TileIds.DirtSeed],
        [TileIds.GrassDirt] = SeedsBySeedId[TileIds.DirtSeed],
        [TileIds.Wood] = SeedsBySeedId[TileIds.WoodSeed],
        [TileIds.Rock] = SeedsBySeedId[TileIds.RockSeed],
        [TileIds.Brick] = SeedsBySeedId[TileIds.BrickSeed],
        [TileIds.Glass] = SeedsBySeedId[TileIds.GlassSeed],
    };

    private static readonly Dictionary<ushort, ItemDefinition> Definitions = new()
    {
        [TileIds.Air] = new(TileIds.Air, "Air", ItemLayer.None, IsSolid: false, BreakHits: 0, IsBreakable: false, IsPlaceable: false, DropItemId: TileIds.Air),
        [TileIds.Bedrock] = new(TileIds.Bedrock, "Bedrock", ItemLayer.Foreground, IsSolid: true, BreakHits: 0, IsBreakable: false, IsPlaceable: false, DropItemId: TileIds.Air),
        [TileIds.Dirt] = new(TileIds.Dirt, "Dirt", ItemLayer.Foreground, IsSolid: true, BreakHits: 3, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.Dirt),
        [TileIds.DirtBackground] = new(TileIds.DirtBackground, "Cave Wall", ItemLayer.Background, IsSolid: false, BreakHits: 3, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.DirtBackground),
        [TileIds.Spawn] = new(TileIds.Spawn, "Main Door", ItemLayer.Foreground, IsSolid: false, BreakHits: 0, IsBreakable: false, IsPlaceable: false, DropItemId: TileIds.Air),
        [TileIds.GrassDirt] = new(TileIds.GrassDirt, "Grass Dirt", ItemLayer.Foreground, IsSolid: true, BreakHits: 3, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.Dirt),
        [TileIds.Rock] = new(TileIds.Rock, "Rock", ItemLayer.Foreground, IsSolid: true, BreakHits: 4, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.Rock),
        [TileIds.Wood] = new(TileIds.Wood, "Wood Plank", ItemLayer.Foreground, IsSolid: true, BreakHits: 3, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.Wood),
        [TileIds.Brick] = new(TileIds.Brick, "Red Brick", ItemLayer.Foreground, IsSolid: true, BreakHits: 4, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.Brick),
        [TileIds.Glass] = new(TileIds.Glass, "Glass", ItemLayer.Foreground, IsSolid: true, BreakHits: 2, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.Glass),
        [TileIds.WoodBackground] = new(TileIds.WoodBackground, "Wood Wall", ItemLayer.Background, IsSolid: false, BreakHits: 3, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.WoodBackground),
        [TileIds.Torch] = new(TileIds.Torch, "Torch", ItemLayer.Foreground, IsSolid: false, BreakHits: 2, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.Torch),
        [TileIds.WorldLock] = new(TileIds.WorldLock, "World Lock", ItemLayer.Foreground, IsSolid: true, BreakHits: 3, IsBreakable: true, IsPlaceable: true, DropItemId: TileIds.WorldLock),
        [TileIds.Tree] = new(TileIds.Tree, "Tree", ItemLayer.Foreground, IsSolid: false, BreakHits: 1, IsBreakable: true, IsPlaceable: false, DropItemId: TileIds.Air),
        [TileIds.Gem] = new(TileIds.Gem, "Gems", ItemLayer.None, IsSolid: false, BreakHits: 0, IsBreakable: false, IsPlaceable: false, DropItemId: TileIds.Air),
        [TileIds.WhiteDoor] = new(TileIds.WhiteDoor, "White Door", ItemLayer.Foreground, IsSolid: false, BreakHits: 0, IsBreakable: false, IsPlaceable: false, DropItemId: TileIds.Air),
        [TileIds.Fist] = new(TileIds.Fist, "Fist", ItemLayer.None, IsSolid: false, BreakHits: 0, IsBreakable: false, IsPlaceable: false, DropItemId: TileIds.Air),
        [TileIds.Wrench] = new(TileIds.Wrench, "Wrench", ItemLayer.None, IsSolid: false, BreakHits: 0, IsBreakable: false, IsPlaceable: false, DropItemId: TileIds.Air),

        // Pasangan Seed untuk blok farmable utama (ID blok + 1000)
        [TileIds.DirtSeed] = new(TileIds.DirtSeed, "Dirt Seed", ItemLayer.Foreground, IsSolid: false, BreakHits: 1, IsBreakable: false, IsPlaceable: true, DropItemId: TileIds.DirtSeed, IsSeed: true),
        [TileIds.WoodSeed] = new(TileIds.WoodSeed, "Wood Seed", ItemLayer.Foreground, IsSolid: false, BreakHits: 1, IsBreakable: false, IsPlaceable: true, DropItemId: TileIds.WoodSeed, IsSeed: true),
        [TileIds.RockSeed] = new(TileIds.RockSeed, "Rock Seed", ItemLayer.Foreground, IsSolid: false, BreakHits: 1, IsBreakable: false, IsPlaceable: true, DropItemId: TileIds.RockSeed, IsSeed: true),
        [TileIds.BrickSeed] = new(TileIds.BrickSeed, "Brick Seed", ItemLayer.Foreground, IsSolid: false, BreakHits: 1, IsBreakable: false, IsPlaceable: true, DropItemId: TileIds.BrickSeed, IsSeed: true),
        [TileIds.GlassSeed] = new(TileIds.GlassSeed, "Glass Seed", ItemLayer.Foreground, IsSolid: false, BreakHits: 1, IsBreakable: false, IsPlaceable: true, DropItemId: TileIds.GlassSeed, IsSeed: true),
    };

    public static IReadOnlyCollection<SeedProperties> GetAllSeeds() => SeedsBySeedId.Values;

    public static bool IsSeed(ushort itemId) => SeedsBySeedId.ContainsKey(itemId);

    public static bool TryGetSeedProperties(ushort seedId, out SeedProperties properties) =>
        SeedsBySeedId.TryGetValue(seedId, out properties);

    public static bool TryGetSeedForBlock(ushort blockId, out SeedProperties properties) =>
        SeedsByBlockId.TryGetValue(blockId, out properties);

    public static bool TryGet(ushort itemId, out ItemDefinition definition) =>
        Definitions.TryGetValue(itemId, out definition);

    public static ItemDefinition Get(ushort itemId) =>
        Definitions.TryGetValue(itemId, out ItemDefinition def)
            ? def
            : new ItemDefinition(itemId, $"Item #{itemId}", ItemLayer.Foreground, IsSolid: true, BreakHits: 3, IsBreakable: true, IsPlaceable: true, DropItemId: itemId);

    public static bool IsSolid(ushort foregroundId) =>
        foregroundId != TileIds.Air && Get(foregroundId).IsSolid;

    public static bool IsBackgroundItem(ushort itemId) =>
        Get(itemId).Layer == ItemLayer.Background;

    public static bool IsPlaceable(ushort itemId) =>
        TryGet(itemId, out ItemDefinition def) && def.IsPlaceable;

    /// <summary>
    /// Item dapat dibuang pemain dari inventory hanya jika terdaftar dan bukan alat/blok terlarang
    /// (menolak Air, Fist, Wrench, Bedrock, Spawn, WhiteDoor, Tree, dan mata uang Gem).
    /// </summary>
    public static bool IsDroppable(ushort itemId)
    {
        if (itemId is TileIds.Air or TileIds.Fist or TileIds.Wrench or TileIds.Bedrock or TileIds.Spawn or TileIds.WhiteDoor or TileIds.Tree or TileIds.Gem)
        {
            return false;
        }

        return TryGet(itemId, out ItemDefinition def) && def.IsPlaceable;
    }

    /// <summary>
    /// Item boleh dimasukkan ke slot penawaran Trade hanya jika terdaftar di katalog
    /// dan bukan alat/blok terlarang (Fist, Wrench, Air, Bedrock, Spawn, WhiteDoor, Tree, Gem).
    /// </summary>
    public static bool IsTradeable(ushort itemId) => IsDroppable(itemId);

    public static byte GetBreakHits(ushort tileId) =>
        TryGet(tileId, out ItemDefinition def) && def.BreakHits > 0 ? def.BreakHits : (byte)3;

    public static string GetName(ushort itemId) => Get(itemId).Name;
}
