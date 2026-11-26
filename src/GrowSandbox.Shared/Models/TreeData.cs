using GrowSandbox.Shared.Constants;

namespace GrowSandbox.Shared.Models;

/// <summary>
/// Data otoritatif sebuah pohon yang sedang tumbuh atau siap panen menggunakan timestamp UTC
/// agar progres pertumbuhan tetap akurat meski server dimatikan dan dinyalakan ulang.
/// </summary>
public readonly record struct TreeData(
    int TileIndex,
    ushort SeedId,
    long PlantedUtcTicks,
    long ReadyUtcTicks,
    byte FruitCount)
{
    public int TileX => TileIndex % WorldConstants.Width;
    public int TileY => TileIndex / WorldConstants.Width;

    public bool IsReady(long utcNowTicks) => utcNowTicks >= ReadyUtcTicks;

    public float GetGrowthProgress(long utcNowTicks)
    {
        long duration = ReadyUtcTicks - PlantedUtcTicks;
        if (duration <= 0)
        {
            return 1f;
        }

        return Math.Clamp((float)(utcNowTicks - PlantedUtcTicks) / duration, 0f, 1f);
    }

    /// <summary>
    /// 4 tahap pertumbuhan visual:
    /// 0 (0-33%): tunas kecil, 1 (33-66%): batang muda, 2 (66-99%): pohon penuh tanpa buah, 3 (100%): matang berbuah.
    /// </summary>
    public int GetGrowthStage(long utcNowTicks)
    {
        if (utcNowTicks >= ReadyUtcTicks)
        {
            return 3;
        }

        float progress = GetGrowthProgress(utcNowTicks);
        if (progress < 1f / 3f)
        {
            return 0;
        }

        if (progress < 2f / 3f)
        {
            return 1;
        }

        return 2;
    }

    public double GetRemainingSeconds(long utcNowTicks)
    {
        long remainingTicks = ReadyUtcTicks - utcNowTicks;
        return remainingTicks <= 0 ? 0.0 : remainingTicks / (double)TimeSpan.TicksPerSecond;
    }
}
