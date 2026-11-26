using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.Graphics;

/// <summary>
/// Sistem teks melayang ringan (object pool tetap berkapasitas 32) untuk umpan balik perolehan
/// Gem (+1 Gem hijau zamrud) atau item (+1 Dirt putih) yang naik perlahan lalu memudar dalam 0.7 detik.
/// </summary>
public sealed class FloatingTextSystem
{
    public const float DefaultDurationSeconds = 0.7f;
    private const int PoolCapacity = 32;

    private struct FloatingEntry
    {
        public Vector2 Position;
        public string Text;
        public Color Color;
        public float Elapsed;
        public float Duration;
    }

    private readonly FloatingEntry[] _pool = new FloatingEntry[PoolCapacity];
    private int _activeCount;
    private int _overwriteCursor;

    public void Clear() => _activeCount = 0;

    public void Spawn(Vector2 worldPosition, string text, Color color, float durationSeconds = DefaultDurationSeconds)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var entry = new FloatingEntry
        {
            Position = worldPosition,
            Text = text,
            Color = color,
            Elapsed = 0f,
            Duration = durationSeconds,
        };

        if (_activeCount < PoolCapacity)
        {
            _pool[_activeCount++] = entry;
        }
        else
        {
            _pool[_overwriteCursor] = entry;
            _overwriteCursor = (_overwriteCursor + 1) % PoolCapacity;
        }
    }

    public void Update(float dt)
    {
        for (int i = _activeCount - 1; i >= 0; i--)
        {
            ref FloatingEntry entry = ref _pool[i];
            entry.Elapsed += dt;
            if (entry.Elapsed >= entry.Duration)
            {
                _pool[i] = _pool[--_activeCount];
                continue;
            }

            float progress = entry.Elapsed / entry.Duration;
            float riseSpeed = 32f * (1f - progress * 0.45f);
            entry.Position.Y -= riseSpeed * dt;
        }
    }

    public void Draw(SpriteBatch spriteBatch, PixelFont font)
    {
        for (int i = 0; i < _activeCount; i++)
        {
            ref readonly FloatingEntry entry = ref _pool[i];
            float progress = Math.Clamp(entry.Elapsed / entry.Duration, 0f, 1f);
            float alpha = progress < 0.35f ? 1f : Math.Clamp(1f - (progress - 0.35f) / 0.65f, 0f, 1f);

            var color = new Color(entry.Color.R, entry.Color.G, entry.Color.B, (byte)(alpha * 255));
            Vector2 size = font.MeasureString(entry.Text, scale: 1);
            var pos = new Vector2(
                MathF.Floor(entry.Position.X - size.X * 0.5f),
                MathF.Floor(entry.Position.Y - size.Y * 0.5f));

            font.DrawString(spriteBatch, entry.Text, pos, color, scale: 1, dropShadow: true);
        }
    }
}
