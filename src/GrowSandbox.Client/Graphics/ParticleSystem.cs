using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.Graphics;

/// <summary>
/// Sistem partikel serpihan blok & debu lompatan berbasis fixed-size array (object pool 256 partikel)
/// agar 100% zero-GC di dalam update dan render loop.
/// </summary>
public sealed class ParticleSystem
{
    public const int PoolCapacity = 256;

    private struct Particle
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public Color Color;
        public float Gravity;
        public float Drag;
        public int Size;
        public float Life;
        public float MaxLife;
    }

    private readonly Particle[] _pool = new Particle[PoolCapacity];
    private int _activeCount;
    private int _overwriteCursor;

    public int ActiveCount => _activeCount;

    public void Clear() => _activeCount = 0;

    /// <summary>
    /// Memunculkan 4–6 partikel piksel (2x2 sampai 4x4 px) saat blok dipukul, memudar dalam 0.35 detik.
    /// </summary>
    public void SpawnBlockHit(Vector2 worldCenter, Color baseColor)
    {
        int count = Random.Shared.Next(4, 7);
        SpawnDebrisCore(worldCenter, baseColor, count, minSpeed: 55f, maxSpeed: 135f, lifeSeconds: 0.35f);
    }

    /// <summary>
    /// Meledakkan 12–16 partikel serpihan saat blok hancur (ForegroundId menjadi 0).
    /// </summary>
    public void SpawnBlockBreak(Vector2 worldCenter, Color baseColor)
    {
        int count = Random.Shared.Next(12, 17);
        SpawnDebrisCore(worldCenter, baseColor, count, minSpeed: 75f, maxSpeed: 185f, lifeSeconds: 0.42f);
    }

    /// <summary>
    /// Memunculkan 3–4 partikel debu putih kecil di bawah kaki karakter saat melompat atau mendarat.
    /// </summary>
    public void SpawnFootDust(Vector2 feetCenter, bool isLanding)
    {
        int count = Random.Shared.Next(3, 5);
        for (int i = 0; i < count; i++)
        {
            float spread = (i - (count - 1) * 0.5f) * 28f + Random.Shared.Next(-12, 13);
            float vx = spread * (isLanding ? 1.5f : 1.0f);
            float vy = isLanding ? -Random.Shared.Next(18, 42) : -Random.Shared.Next(10, 28);
            byte brightness = (byte)Random.Shared.Next(226, 255);

            AddParticle(new Particle
            {
                Position = new Vector2(feetCenter.X + Random.Shared.Next(-7, 8), feetCenter.Y - 2f),
                Velocity = new Vector2(vx, vy),
                Color = new Color(brightness, brightness, (byte)Math.Min(255, brightness + 4), (byte)215),
                Gravity = 60f,
                Drag = 4.5f,
                Size = Random.Shared.Next(2, 5),
                Life = 0.28f,
                MaxLife = 0.28f,
            });
        }
    }

    public void SpawnPickupSparkle(Vector2 worldCenter, bool isGem = false)
    {
        Color sparkleColor = isGem ? new Color(92, 252, 172) : new Color(255, 240, 128);
        for (int i = 0; i < 8; i++)
        {
            float angle = i * (MathF.PI * 2f / 8f);
            float speed = 38f + (float)Random.Shared.NextDouble() * 44f;

            AddParticle(new Particle
            {
                Position = worldCenter,
                Velocity = new Vector2(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed - 30f),
                Color = sparkleColor,
                Gravity = 180f,
                Drag = 3.0f,
                Size = 3,
                Life = 0.30f,
                MaxLife = 0.30f,
            });
        }
    }

    private void SpawnDebrisCore(
        Vector2 worldCenter,
        Color baseColor,
        int count,
        float minSpeed,
        float maxSpeed,
        float lifeSeconds)
    {
        for (int i = 0; i < count; i++)
        {
            // Arahkan sedikit melengkung ke atas sebelum jatuh terkena gravitasi.
            float angle = -MathF.PI * (0.12f + (float)Random.Shared.NextDouble() * 0.76f);
            if (Random.Shared.Next(4) == 0)
            {
                angle = (float)(Random.Shared.NextDouble() * Math.PI * 2.0);
            }

            float speed = minSpeed + (float)Random.Shared.NextDouble() * (maxSpeed - minSpeed);
            float shade = 0.80f + (float)Random.Shared.NextDouble() * 0.35f;

            var tinted = new Color(
                (byte)Math.Clamp((int)(baseColor.R * shade), 0, 255),
                (byte)Math.Clamp((int)(baseColor.G * shade), 0, 255),
                (byte)Math.Clamp((int)(baseColor.B * shade), 0, 255));

            AddParticle(new Particle
            {
                Position = worldCenter + new Vector2(Random.Shared.Next(-8, 9), Random.Shared.Next(-8, 9)),
                Velocity = new Vector2(MathF.Cos(angle) * speed, MathF.Sin(angle) * speed - 45f),
                Color = tinted,
                Gravity = 560f,
                Drag = 2.2f,
                Size = Random.Shared.Next(2, 5), // 2x2 hingga 4x4 px
                Life = lifeSeconds,
                MaxLife = lifeSeconds,
            });
        }
    }

    private void AddParticle(in Particle particle)
    {
        if (_activeCount < PoolCapacity)
        {
            _pool[_activeCount++] = particle;
        }
        else
        {
            _pool[_overwriteCursor] = particle;
            _overwriteCursor = (_overwriteCursor + 1) % PoolCapacity;
        }
    }

    public void Update(float dt)
    {
        for (int i = _activeCount - 1; i >= 0; i--)
        {
            ref Particle p = ref _pool[i];
            p.Life -= dt;
            if (p.Life <= 0f)
            {
                _pool[i] = _pool[--_activeCount];
                continue;
            }

            p.Velocity.Y += p.Gravity * dt;
            p.Velocity.X *= MathF.Max(0f, 1f - p.Drag * dt);
            p.Position.X += p.Velocity.X * dt;
            p.Position.Y += p.Velocity.Y * dt;
        }
    }

    public void Draw(SpriteBatch spriteBatch, Texture2D pixel)
    {
        for (int i = 0; i < _activeCount; i++)
        {
            ref readonly Particle p = ref _pool[i];
            float alpha = Math.Clamp(p.Life / p.MaxLife, 0f, 1f);
            byte a = (byte)(p.Color.A * alpha);
            var color = new Color(p.Color.R, p.Color.G, p.Color.B, a);

            int half = p.Size >> 1;
            int px = (int)MathF.Floor(p.Position.X) - half;
            int py = (int)MathF.Floor(p.Position.Y) - half;
            spriteBatch.Draw(pixel, new Rectangle(px, py, p.Size, p.Size), color);
        }
    }
}
