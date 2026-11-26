using Microsoft.Xna.Framework.Audio;

namespace GrowSandbox.Client.Audio;

/// <summary>
/// Generator dan pemutar efek suara (SFX) retro prosedural PCM 16-bit mono di memori
/// tanpa aset audio eksternal. Aman jika perangkat tidak memiliki output audio.
/// </summary>
public sealed class SfxManager : IDisposable
{
    private const int SampleRate = 22050;

    private SoundEffect? _punch;
    private SoundEffect? _blockBreak;
    private SoundEffect? _blockPlace;
    private SoundEffect? _jump;
    private SoundEffect? _gemPickup;
    private SoundEffect? _itemPickup;
    private SoundEffect? _uiClick;
    private SoundEffect? _worldWarp;
    private SoundEffect? _tradeInvite;

    public bool IsAudioAvailable { get; private set; }
    public bool IsMuted { get; set; }

    public SfxManager()
    {
        try
        {
            _punch = CreatePunchSfx();
            _blockBreak = CreateBlockBreakSfx();
            _blockPlace = CreateBlockPlaceSfx();
            _jump = CreateJumpSfx();
            _gemPickup = CreateGemPickupSfx();
            _itemPickup = CreateItemPickupSfx();
            _uiClick = CreateUiClickSfx();
            _worldWarp = CreateWorldWarpSfx();
            _tradeInvite = CreateTradeInviteSfx();
            IsAudioAvailable = true;
        }
        catch
        {
            // Tetap berjalan normal tanpa crash bila sistem tidak memiliki perangkat audio/OpenAL.
            IsAudioAvailable = false;
            DisposeSounds();
        }
    }

    public bool ToggleMute()
    {
        IsMuted = !IsMuted;
        return IsMuted;
    }

    public void PlayPunch()
    {
        float pitchJitter = (float)(Random.Shared.NextDouble() * 0.16 - 0.08);
        SafePlay(_punch, volume: 0.34f, pitch: pitchJitter);
    }

    public void PlayBlockBreak()
    {
        float pitchJitter = (float)(Random.Shared.NextDouble() * 0.10 - 0.05);
        SafePlay(_blockBreak, volume: 0.36f, pitch: pitchJitter);
    }

    public void PlayBlockPlace()
    {
        float pitchJitter = (float)(Random.Shared.NextDouble() * 0.08 - 0.04);
        SafePlay(_blockPlace, volume: 0.30f, pitch: pitchJitter);
    }

    public void PlayJump() => SafePlay(_jump, volume: 0.24f, pitch: 0f);

    public void PlayGemPickup() => SafePlay(_gemPickup, volume: 0.28f, pitch: 0f);

    public void PlayItemPickup() => SafePlay(_itemPickup, volume: 0.22f, pitch: 0f);

    public void PlayUiClick() => SafePlay(_uiClick, volume: 0.22f, pitch: 0f);

    public void PlayWorldWarp() => SafePlay(_worldWarp, volume: 0.26f, pitch: 0f);

    public void PlayTradeInvite() => SafePlay(_tradeInvite, volume: 0.24f, pitch: 0f);

    private void SafePlay(SoundEffect? effect, float volume, float pitch)
    {
        if (!IsAudioAvailable || IsMuted || effect is null)
        {
            return;
        }

        try
        {
            effect.Play(Math.Clamp(volume, 0f, 1f), Math.Clamp(pitch, -1f, 1f), pan: 0f);
        }
        catch
        {
            // Abaikan error perangkat audio runtime agar game tidak terganggu.
        }
    }

    // 1. Punch: dentuman singkat frekuensi rendah-menengah + sedikit noise lembut
    private static SoundEffect CreatePunchSfx()
    {
        return Synthesize(0.070f, (t, dur, rng) =>
        {
            float p = t / dur;
            float env = MathF.Exp(-p * 6.5f) * SmoothAttack(t, 0.003f);
            float freq = MathHelperLerp(195f, 72f, MathF.Sqrt(p));
            float thud = MathF.Sin(2f * MathF.PI * freq * t);
            float noise = ((float)rng.NextDouble() * 2f - 1f) * 0.26f * MathF.Exp(-p * 11f);
            return (thud * 0.78f + noise) * env;
        }, seed: 101);
    }

    // 2. BlockBreak: suara pecah renyah (crumble burst 3 butiran + bass pop turun)
    private static SoundEffect CreateBlockBreakSfx()
    {
        float lp = 0f;
        return Synthesize(0.165f, (t, dur, rng) =>
        {
            float p = t / dur;
            float burst = 0.55f + 0.45f * MathF.Abs(MathF.Sin(p * MathF.PI * 5.5f));
            float env = MathF.Exp(-p * 4.2f) * burst * SmoothAttack(t, 0.004f);

            float white = (float)rng.NextDouble() * 2f - 1f;
            lp = lp * 0.58f + white * 0.42f; // Low-pass agar renyah, tidak menusuk telinga

            float toneFreq = MathHelperLerp(270f, 88f, p);
            float body = TriangleWave(toneFreq * t) * MathF.Exp(-p * 7.5f);
            return (lp * 0.68f + body * 0.48f) * env;
        }, seed: 202);
    }

    // 3. BlockPlace: ketukan/klik padat lembut
    private static SoundEffect CreateBlockPlaceSfx()
    {
        return Synthesize(0.055f, (t, dur, _) =>
        {
            float p = t / dur;
            float env = MathF.Exp(-p * 8.0f) * SmoothAttack(t, 0.002f);
            float freq = MathHelperLerp(340f, 175f, p);
            float tap = MathF.Sin(2f * MathF.PI * freq * t) * 0.75f
                      + MathF.Sin(2f * MathF.PI * freq * 2.0f * t) * 0.22f * MathF.Exp(-p * 14f);
            return tap * env;
        }, seed: 303);
    }

    // 4. Jump: sapuan nada naik cepat (triangle/sine wave halus)
    private static SoundEffect CreateJumpSfx()
    {
        float phase = 0f;
        return Synthesize(0.115f, (t, dur, _) =>
        {
            float p = t / dur;
            float env = SmoothAttack(t, 0.006f) * MathF.Pow(1f - p, 1.35f);
            float freq = MathHelperLerp(235f, 560f, MathF.Pow(p, 0.75f));
            phase += freq / SampleRate;
            float wave = TriangleWave(phase) * 0.55f + MathF.Sin(2f * MathF.PI * phase) * 0.45f;
            return wave * env;
        }, seed: 404);
    }

    // 5. GemPickup: denting dua nada cerah (arpeggio singkat B5 -> E6)
    private static SoundEffect CreateGemPickupSfx()
    {
        return Synthesize(0.185f, (t, dur, _) =>
        {
            float note1 = t < 0.075f
                ? MathF.Sin(2f * MathF.PI * 987.77f * t) * MathF.Exp(-t * 28f) * SmoothAttack(t, 0.003f)
                : 0f;

            float t2 = t - 0.060f;
            float note2 = t2 >= 0f
                ? (MathF.Sin(2f * MathF.PI * 1318.51f * t2) * 0.80f + MathF.Sin(2f * MathF.PI * 2637.02f * t2) * 0.16f)
                  * MathF.Exp(-t2 * 18f) * SmoothAttack(t2, 0.003f)
                : 0f;

            return (note1 * 0.65f + note2 * 0.75f);
        }, seed: 505);
    }

    // Item pickup lembut (satu nada pop hangat)
    private static SoundEffect CreateItemPickupSfx()
    {
        float phase = 0f;
        return Synthesize(0.080f, (t, dur, _) =>
        {
            float p = t / dur;
            float env = SmoothAttack(t, 0.003f) * MathF.Exp(-p * 6.5f);
            float freq = MathHelperLerp(520f, 760f, p);
            phase += freq / SampleRate;
            return MathF.Sin(2f * MathF.PI * phase) * env * 0.8f;
        }, seed: 555);
    }

    // 6a. UiClick: klik tombol HUD halus
    private static SoundEffect CreateUiClickSfx()
    {
        return Synthesize(0.032f, (t, dur, _) =>
        {
            float p = t / dur;
            float env = SmoothAttack(t, 0.0015f) * MathF.Exp(-p * 10f);
            float freq = MathHelperLerp(740f, 520f, p);
            return MathF.Sin(2f * MathF.PI * freq * t) * env * 0.75f;
        }, seed: 606);
    }

    // 6b. WorldWarp: arpeggio lembut 3 nada (C5 -> E5 -> G5) saat berpindah world
    private static SoundEffect CreateWorldWarpSfx()
    {
        return Synthesize(0.36f, (t, dur, _) =>
        {
            float Note(float start, float freq)
            {
                float dt = t - start;
                if (dt < 0f) return 0f;
                return (MathF.Sin(2f * MathF.PI * freq * dt) * 0.82f + TriangleWave(freq * dt) * 0.18f)
                     * SmoothAttack(dt, 0.008f) * MathF.Exp(-dt * 9.5f);
            }

            return (Note(0.00f, 523.25f) + Note(0.065f, 659.25f) + Note(0.130f, 783.99f)) * 0.52f;
        }, seed: 707);
    }

    // 7. TradeInvite: notifikasi denting lembut dua nada hangat (G5 -> C6)
    private static SoundEffect CreateTradeInviteSfx()
    {
        return Synthesize(0.24f, (t, dur, _) =>
        {
            float Note(float start, float freq)
            {
                float dt = t - start;
                if (dt < 0f) return 0f;
                return (MathF.Sin(2f * MathF.PI * freq * dt) * 0.85f + MathF.Sin(2f * MathF.PI * freq * 2f * dt) * 0.15f)
                     * SmoothAttack(dt, 0.008f) * MathF.Exp(-dt * 14f);
            }

            return (Note(0.00f, 783.99f) + Note(0.085f, 1046.50f)) * 0.62f;
        }, seed: 808);
    }

    private static SoundEffect Synthesize(float durationSeconds, Func<float, float, Random, float> generator, int seed)
    {
        int sampleCount = Math.Max(1, (int)(SampleRate * durationSeconds));
        var buffer = new byte[sampleCount * 2];
        var rng = new Random(seed);

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / SampleRate;
            float sample = Math.Clamp(generator(t, durationSeconds, rng), -0.95f, 0.95f);
            short pcm = (short)(sample * short.MaxValue);
            buffer[i * 2] = (byte)(pcm & 0xFF);
            buffer[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
        }

        return new SoundEffect(buffer, SampleRate, AudioChannels.Mono);
    }

    private static float TriangleWave(float cycles)
    {
        float frac = cycles - MathF.Floor(cycles);
        return 4f * MathF.Abs(frac - 0.5f) - 1f;
    }

    private static float SmoothAttack(float t, float attackSeconds) =>
        t < attackSeconds ? t / attackSeconds : 1f;

    private static float MathHelperLerp(float a, float b, float t) => a + (b - a) * t;

    private void DisposeSounds()
    {
        _punch?.Dispose();
        _blockBreak?.Dispose();
        _blockPlace?.Dispose();
        _jump?.Dispose();
        _gemPickup?.Dispose();
        _itemPickup?.Dispose();
        _uiClick?.Dispose();
        _worldWarp?.Dispose();
        _tradeInvite?.Dispose();
        _punch = _blockBreak = _blockPlace = _jump = _gemPickup = _itemPickup = _uiClick = _worldWarp = _tradeInvite = null;
    }

    public void Dispose()
    {
        DisposeSounds();
    }
}
