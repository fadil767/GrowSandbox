namespace GrowSandbox.Shared.Constants;

/// <summary>
/// ID blok, dekorasi, pohon, benih (Seed = BlockId + 1000), dan tile standar
/// yang dibagikan antara server otoritatif dan klien.
/// </summary>
public static class TileIds
{
    public const ushort Air = 0;
    public const ushort Bedrock = 1;
    public const ushort Dirt = 2;
    public const ushort DirtBackground = 3;
    public const ushort CaveBackground = DirtBackground;
    public const ushort Spawn = 4;
    public const ushort GrassDirt = 5;
    public const ushort Stone = 6;
    public const ushort Rock = Stone;
    public const ushort Wood = 7;
    public const ushort Brick = 8;
    public const ushort Glass = 9;
    public const ushort WoodBackground = 10;
    public const ushort Torch = 11;
    public const ushort WorldLock = 12;

    /// <summary>Tile pohon yang tertanam di dunia (non-solid, metadata pertumbuhan disimpan di TreeData).</summary>
    public const ushort Tree = 13;
    public const ushort TreeSapling = Tree;

    /// <summary>Mata uang Gems (saldo authoritative, tidak dapat dipasang).</summary>
    public const ushort Gem = 14;

    /// <summary>Pintu utama putih (Main Door) di titik spawn: non-solid dan tidak dapat dihancurkan.</summary>
    public const ushort WhiteDoor = 15;

    /// <summary>Alat dasar Fist (tidak dapat dipasang atau dibuang ke lantai).</summary>
    public const ushort Fist = 16;

    /// <summary>Alat dasar Wrench (tidak dapat dipasang atau dibuang ke lantai).</summary>
    public const ushort Wrench = 17;

    /// <summary>Offset standar ID Seed terhadap ID blok aslinya (SeedId = BlockId + 1000).</summary>
    public const ushort SeedIdOffset = 1000;

    public const ushort DirtSeed = Dirt + SeedIdOffset;     // 1002
    public const ushort RockSeed = Rock + SeedIdOffset;     // 1006
    public const ushort StoneSeed = RockSeed;               // 1006
    public const ushort WoodSeed = Wood + SeedIdOffset;     // 1007
    public const ushort BrickSeed = Brick + SeedIdOffset;   // 1008
    public const ushort GlassSeed = Glass + SeedIdOffset;   // 1009
}
