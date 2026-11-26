using GrowSandbox.Shared.Constants;
using Microsoft.Xna.Framework;

namespace GrowSandbox.Client.World;

/// <summary>
/// Kamera 2D profesional dengan:
/// - Default zoom 1.75x + smooth exponential zoom (1.0x..2.5x) berpusat pada karakter
/// - Smooth exponential follow damping + subtle horizontal look-ahead (±18px)
/// - Pembatasan batas world (clamping) dinamis sesuai ViewportWidth/Zoom &amp; ViewportHeight/Zoom
/// - Pembulatan translasi layar konsisten untuk mencegah celah garis sub-pixel antar tile
/// - Frustum culling dinamis berbasis sudut viewport hasil transformasi kamera
/// </summary>
public sealed class Camera2D
{
    public const float DefaultZoom = 1.75f;
    public const float MinZoom = 1.0f;
    public const float MaxZoom = 2.5f;
    public const float ZoomStep = 0.15f;
    public const float ZoomSmoothingRate = 12.0f;
    public const float FollowSmoothingRate = 9.5f;
    public const float HorizontalLookAheadPx = 18.0f;

    private float _zoom = DefaultZoom;
    private float _targetZoom = DefaultZoom;
    private float _lookAheadX;

    public Vector2 Position { get; set; }
    public int ViewportWidth { get; set; }
    public int ViewportHeight { get; set; }
    public bool ClampToWorldBounds { get; set; } = true;

    public float Zoom
    {
        get => _zoom;
        set
        {
            _zoom = Math.Clamp(value, MinZoom, MaxZoom);
            ClampPosition();
        }
    }

    public float TargetZoom
    {
        get => _targetZoom;
        set => _targetZoom = Math.Clamp(value, MinZoom, MaxZoom);
    }

    public Camera2D(int viewportWidth, int viewportHeight, float initialZoom = DefaultZoom)
    {
        ViewportWidth = Math.Max(1, viewportWidth);
        ViewportHeight = Math.Max(1, viewportHeight);
        float clamped = Math.Clamp(initialZoom, MinZoom, MaxZoom);
        _zoom = clamped;
        _targetZoom = clamped;
    }

    public void UpdateViewport(int width, int height)
    {
        ViewportWidth = Math.Max(1, width);
        ViewportHeight = Math.Max(1, height);
        ClampPosition();
    }

    public void AdjustTargetZoom(float delta)
    {
        TargetZoom = _targetZoom + delta;
    }

    public void ResetZoom()
    {
        TargetZoom = DefaultZoom;
    }

    public void CenterOnTile(int tileX, int tileY)
    {
        SnapTo(TileToWorldCenter(tileX, tileY));
    }

    /// <summary>
    /// Langsung menempelkan kamera ke titik target tanpa animasi lerp panjang (dipakai saat masuk world baru).
    /// </summary>
    public void SnapTo(Vector2 worldPosition, float? zoom = null)
    {
        if (zoom.HasValue)
        {
            float clamped = Math.Clamp(zoom.Value, MinZoom, MaxZoom);
            _zoom = clamped;
            _targetZoom = clamped;
        }

        _lookAheadX = 0f;
        Position = worldPosition;
        ClampPosition();
    }

    /// <summary>
    /// Interpolasi zoom halus dan frame-rate independent menuju <see cref="TargetZoom"/>,
    /// berpusat di tengah layar (posisi karakter).
    /// </summary>
    public void UpdateZoom(float dt)
    {
        if (dt <= 0f)
        {
            return;
        }

        float factor = 1f - MathF.Exp(-ZoomSmoothingRate * dt);
        _zoom = MathHelper.Lerp(_zoom, _targetZoom, factor);
        if (MathF.Abs(_zoom - _targetZoom) < 0.0005f)
        {
            _zoom = _targetZoom;
        }

        ClampPosition();
    }

    /// <summary>
    /// Mengikuti karakter dengan exponential damping (9.5/s) + look-ahead horizontal tipis (±18px ke arah gerak).
    /// </summary>
    public void FollowWithLookAhead(Vector2 playerCenterWorld, float playerVelocityX, float dt)
    {
        float safeDt = MathF.Max(0f, dt);
        float desiredLookAhead = MathF.Abs(playerVelocityX) > 12f
            ? MathF.Sign(playerVelocityX) * HorizontalLookAheadPx
            : 0f;

        float lookFactor = 1f - MathF.Exp(-6.5f * safeDt);
        _lookAheadX = MathHelper.Lerp(_lookAheadX, desiredLookAhead, lookFactor);
        if (MathF.Abs(_lookAheadX) < 0.05f && desiredLookAhead == 0f)
        {
            _lookAheadX = 0f;
        }

        var targetPos = new Vector2(playerCenterWorld.X + _lookAheadX, playerCenterWorld.Y);
        float followFactor = 1f - MathF.Exp(-FollowSmoothingRate * safeDt);
        Position = Vector2.Lerp(Position, targetPos, followFactor);
        ClampPosition();
    }

    public void Follow(Vector2 targetWorldPosition, float dt, float smoothSpeed = FollowSmoothingRate)
    {
        float factor = 1f - MathF.Exp(-smoothSpeed * MathF.Max(0f, dt));
        Position = Vector2.Lerp(Position, targetWorldPosition, factor);
        ClampPosition();
    }

    public void Move(Vector2 deltaWorld)
    {
        Position += deltaWorld;
        ClampPosition();
    }

    /// <summary>
    /// View matrix dengan pembulatan translasi layar akhir ke piksel bulat
    /// agar mencegah celah garis halus (sub-pixel tile seams) saat bergerak maupun di-zoom.
    /// </summary>
    public Matrix GetViewMatrix()
    {
        float scale = _zoom;
        float tx = MathF.Round(ViewportWidth * 0.5f - Position.X * scale);
        float ty = MathF.Round(ViewportHeight * 0.5f - Position.Y * scale);

        return new Matrix(
            scale, 0f,    0f, 0f,
            0f,    scale, 0f, 0f,
            0f,    0f,    1f, 0f,
            tx,    ty,    0f, 1f);
    }

    public Vector2 ScreenToWorld(Vector2 screenPosition)
    {
        Matrix inverse = Matrix.Invert(GetViewMatrix());
        return Vector2.Transform(screenPosition, inverse);
    }

    public Vector2 WorldToScreen(Vector2 worldPosition)
    {
        return Vector2.Transform(worldPosition, GetViewMatrix());
    }

    public (int TileX, int TileY) ScreenToTile(Vector2 screenPosition)
    {
        Vector2 world = ScreenToWorld(screenPosition);
        int tileX = (int)MathF.Floor(world.X / WorldConstants.TileSize);
        int tileY = (int)MathF.Floor(world.Y / WorldConstants.TileSize);
        return (tileX, tileY);
    }

    /// <summary>
    /// Menghitung rentang indeks tile terlihat (Frustum Culling dinamis) langsung dari sudut kiri-atas
    /// dan kanan-bawah viewport dalam koordinat world hasil transformasi kamera (+ margin tile).
    /// </summary>
    public (int MinX, int MaxX, int MinY, int MaxY) GetVisibleTileRange(int marginTiles = 2)
    {
        Matrix inverse = Matrix.Invert(GetViewMatrix());
        Vector2 topLeft = Vector2.Transform(Vector2.Zero, inverse);
        Vector2 bottomRight = Vector2.Transform(new Vector2(ViewportWidth, ViewportHeight), inverse);

        int tileSize = WorldConstants.TileSize;
        int minX = Math.Clamp((int)MathF.Floor(topLeft.X / tileSize) - marginTiles, 0, WorldConstants.Width - 1);
        int maxX = Math.Clamp((int)MathF.Ceiling(bottomRight.X / tileSize) + marginTiles, 0, WorldConstants.Width - 1);
        int minY = Math.Clamp((int)MathF.Floor(topLeft.Y / tileSize) - marginTiles, 0, WorldConstants.Height - 1);
        int maxY = Math.Clamp((int)MathF.Ceiling(bottomRight.Y / tileSize) + marginTiles, 0, WorldConstants.Height - 1);

        return (minX, maxX, minY, maxY);
    }

    /// <summary>
    /// Menghitung kotak batas dunia (dalam piksel) yang saat ini terlihat oleh viewport kamera.
    /// </summary>
    public Rectangle GetVisibleWorldBounds()
    {
        Matrix inverse = Matrix.Invert(GetViewMatrix());
        Vector2 topLeft = Vector2.Transform(Vector2.Zero, inverse);
        Vector2 bottomRight = Vector2.Transform(new Vector2(ViewportWidth, ViewportHeight), inverse);

        int left = (int)MathF.Floor(topLeft.X);
        int top = (int)MathF.Floor(topLeft.Y);
        int width = Math.Max(1, (int)MathF.Ceiling(bottomRight.X - left));
        int height = Math.Max(1, (int)MathF.Ceiling(bottomRight.Y - top));

        return new Rectangle(left, top, width, height);
    }

    public void ClampPosition()
    {
        if (!ClampToWorldBounds)
        {
            return;
        }

        float visibleWidth = ViewportWidth / _zoom;
        float visibleHeight = ViewportHeight / _zoom;
        float halfWidth = visibleWidth * 0.5f;
        float halfHeight = visibleHeight * 0.5f;
        float worldWidth = WorldConstants.WorldPixelWidth;
        float worldHeight = WorldConstants.WorldPixelHeight;

        float x = visibleWidth >= worldWidth
            ? worldWidth * 0.5f
            : Math.Clamp(Position.X, halfWidth, worldWidth - halfWidth);

        float y = visibleHeight >= worldHeight
            ? worldHeight * 0.5f
            : Math.Clamp(Position.Y, halfHeight, worldHeight - halfHeight);

        Position = new Vector2(x, y);
    }

    public static Vector2 TileToWorldCenter(int tileX, int tileY)
    {
        return new Vector2(
            (tileX + 0.5f) * WorldConstants.TileSize,
            (tileY + 0.5f) * WorldConstants.TileSize);
    }
}
