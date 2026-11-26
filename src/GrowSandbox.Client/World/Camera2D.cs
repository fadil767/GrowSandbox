using GrowSandbox.Shared.Constants;
using Microsoft.Xna.Framework;

namespace GrowSandbox.Client.World;

/// <summary>
/// Kamera 2D sederhana yang mendukung pan manual, zoom,
/// mengikuti target (seperti titik spawn world), dan menyediakan viewport bounds
/// untuk frustum culling tile.
/// </summary>
public sealed class Camera2D
{
    public const float MinZoom = 0.5f;
    public const float MaxZoom = 3.0f;

    private float _zoom = 1.0f;

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

    public Camera2D(int viewportWidth, int viewportHeight)
    {
        ViewportWidth = Math.Max(1, viewportWidth);
        ViewportHeight = Math.Max(1, viewportHeight);
    }

    public void UpdateViewport(int width, int height)
    {
        ViewportWidth = Math.Max(1, width);
        ViewportHeight = Math.Max(1, height);
        ClampPosition();
    }

    public void CenterOnTile(int tileX, int tileY)
    {
        Position = TileToWorldCenter(tileX, tileY);
        ClampPosition();
    }

    public void Follow(Vector2 targetWorldPosition, float dt, float smoothSpeed = 8f)
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

    public Matrix GetViewMatrix()
    {
        return Matrix.CreateTranslation(-Position.X, -Position.Y, 0f) *
               Matrix.CreateScale(_zoom, _zoom, 1f) *
               Matrix.CreateTranslation(ViewportWidth * 0.5f, ViewportHeight * 0.5f, 0f);
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
    /// Menghitung kotak batas dunia (dalam piksel) yang saat ini terlihat oleh viewport kamera.
    /// </summary>
    public Rectangle GetVisibleWorldBounds()
    {
        float visibleWidth = ViewportWidth / _zoom;
        float visibleHeight = ViewportHeight / _zoom;

        int left = (int)MathF.Floor(Position.X - visibleWidth * 0.5f);
        int top = (int)MathF.Floor(Position.Y - visibleHeight * 0.5f);
        int width = (int)MathF.Ceiling(visibleWidth);
        int height = (int)MathF.Ceiling(visibleHeight);

        return new Rectangle(left, top, width, height);
    }

    public void ClampPosition()
    {
        if (!ClampToWorldBounds)
        {
            return;
        }

        float halfWidth = (ViewportWidth / _zoom) * 0.5f;
        float halfHeight = (ViewportHeight / _zoom) * 0.5f;
        float worldWidth = WorldConstants.WorldPixelWidth;
        float worldHeight = WorldConstants.WorldPixelHeight;

        float x = halfWidth * 2f >= worldWidth
            ? worldWidth * 0.5f
            : Math.Clamp(Position.X, halfWidth, worldWidth - halfWidth);

        float y = halfHeight * 2f >= worldHeight
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
