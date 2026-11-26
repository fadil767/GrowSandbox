using GrowSandbox.Shared.Models;

namespace GrowSandbox.Client.World;

/// <summary>
/// Pelacak efek umpan balik tile ringan berbasis fixed-size struct array (zero-GC)
/// untuk animasi tile shake (±2px selama 120ms) dan hit-flash putih (alpha ~70 selama 50ms)
/// setiap kali tile menerima damage dari server (<c>TileUpdateDelta</c>).
/// </summary>
public sealed class TileFeedbackTracker
{
    public const float ShakeDurationSeconds = 0.12f;
    public const float FlashDurationSeconds = 0.05f;
    public const float MaxShakePixels = 2f;
    public const byte MaxHitFlashAlpha = 70;

    private const int PoolCapacity = 32;

    private struct ActiveEffect
    {
        public int TileIndex;
        public float Elapsed;
        public float PhaseSeed;
    }

    private readonly ActiveEffect[] _pool = new ActiveEffect[PoolCapacity];
    private int _activeCount;
    private float _seedCounter;

    public bool HasActiveEffects => _activeCount > 0;

    public void TriggerHit(int tileX, int tileY)
    {
        if (!Tile.IsInBounds(tileX, tileY))
        {
            return;
        }

        int index = Tile.GetIndex(tileX, tileY);
        _seedCounter += 1.91f;

        // Perbarui slot yang sudah aktif untuk tile yang sama.
        for (int i = 0; i < _activeCount; i++)
        {
            if (_pool[i].TileIndex == index)
            {
                _pool[i].Elapsed = 0f;
                _pool[i].PhaseSeed = _seedCounter;
                return;
            }
        }

        int slot;
        if (_activeCount < PoolCapacity)
        {
            slot = _activeCount++;
        }
        else
        {
            // Timpa slot tertua jika pool penuh (zero allocation).
            slot = 0;
            float oldest = _pool[0].Elapsed;
            for (int i = 1; i < PoolCapacity; i++)
            {
                if (_pool[i].Elapsed > oldest)
                {
                    oldest = _pool[i].Elapsed;
                    slot = i;
                }
            }
        }

        _pool[slot] = new ActiveEffect
        {
            TileIndex = index,
            Elapsed = 0f,
            PhaseSeed = _seedCounter,
        };
    }

    public void Clear() => _activeCount = 0;

    public void Update(float dt)
    {
        for (int i = _activeCount - 1; i >= 0; i--)
        {
            _pool[i].Elapsed += dt;
            if (_pool[i].Elapsed >= ShakeDurationSeconds)
            {
                _pool[i] = _pool[--_activeCount];
            }
        }
    }

    public bool TryGetFeedback(int tileIndex, out int shakeOffsetX, out int shakeOffsetY, out byte flashAlpha)
    {
        if (_activeCount == 0)
        {
            shakeOffsetX = 0;
            shakeOffsetY = 0;
            flashAlpha = 0;
            return false;
        }

        for (int i = 0; i < _activeCount; i++)
        {
            ref readonly ActiveEffect effect = ref _pool[i];
            if (effect.TileIndex != tileIndex)
            {
                continue;
            }

            float shakeDecay = Math.Clamp(1f - (effect.Elapsed / ShakeDurationSeconds), 0f, 1f);
            float amp = MaxShakePixels * shakeDecay;
            shakeOffsetX = (int)MathF.Round(MathF.Sin(effect.Elapsed * 110f + effect.PhaseSeed) * amp);
            shakeOffsetY = (int)MathF.Round(MathF.Cos(effect.Elapsed * 95f + effect.PhaseSeed * 1.3f) * (amp * 0.7f));

            if (effect.Elapsed < FlashDurationSeconds)
            {
                float flashDecay = 1f - (effect.Elapsed / FlashDurationSeconds);
                flashAlpha = (byte)Math.Clamp((int)(MaxHitFlashAlpha * flashDecay), 0, MaxHitFlashAlpha);
            }
            else
            {
                flashAlpha = 0;
            }

            return true;
        }

        shakeOffsetX = 0;
        shakeOffsetY = 0;
        flashAlpha = 0;
        return false;
    }
}
