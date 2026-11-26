namespace GrowSandbox.Shared.Constants;

/// <summary>
/// Konstanta fisika bersama untuk simulasi otoritatif server dan prediksi lokal klien.
/// </summary>
public static class PhysicsConstants
{
    /// <summary>Percepatan gravitasi ke bawah (piksel / detik²).</summary>
    public const float Gravity = 1800f;

    /// <summary>Kecepatan gerak horizontal pemain (piksel / detik).</summary>
    public const float MoveSpeed = 240f;

    /// <summary>Besar gaya/kecepatan awal lompatan ke atas (piksel / detik).</summary>
    public const float JumpForce = 540f;

    /// <summary>Jarak jangkauan interaksi tile maksimum pemain dalam satuan tile.</summary>
    public const int MaxReachTiles = 3;

    public const float MaxReachPixels = MaxReachTiles * WorldConstants.TileSize;
    public const float MaxFallSpeed = 900f;

    public const int TickRate = 60;
    public const float FixedDeltaTime = 1f / TickRate;

    /// <summary>Frekuensi pengiriman PlayerState dari klien ke server (20 kali/detik).</summary>
    public const int ClientStateSendRate = 20;
    public const float ClientStateSendInterval = 1f / ClientStateSendRate;

    /// <summary>Toleransi margin kecepatan server untuk mengakomodasi jitter jaringan.</summary>
    public const float SpeedValidationTolerance = 1.35f;

    public const float PlayerWidth = 24f;
    public const float PlayerHeight = 44f;

    /// <summary>Radius auto-stack item drop yang sama di lantai (1.2 tile = 38.4 px).</summary>
    public const float DropAutoStackRadiusTiles = 1.2f;
    public const float DropAutoStackRadiusPx = DropAutoStackRadiusTiles * WorldConstants.TileSize;

    /// <summary>Radius pungut item drop oleh pemain (0.9 tile = 28.8 px).</summary>
    public const float DropPickupRadiusTiles = 0.9f;
    public const float DropPickupRadiusPx = DropPickupRadiusTiles * WorldConstants.TileSize;

    /// <summary>Cooldown khusus (detik) bagi pemain yang baru saja membuang item agar tidak langsung tersedot kembali.</summary>
    public const float DropperPickupCooldownSeconds = 0.45f;
}
