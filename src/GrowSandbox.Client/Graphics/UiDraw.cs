using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.Graphics;

/// <summary>
/// Helper menggambar panel bersudut membulat ala UI Growtopia menggunakan tekstur 1x1 piksel.
/// </summary>
public static class UiDraw
{
    public static void RoundedRect(SpriteBatch sb, Texture2D pixel, Rectangle r, Color color, int radius = 6)
    {
        if (r.Width <= 0 || r.Height <= 0)
        {
            return;
        }

        radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
        if (radius <= 0)
        {
            sb.Draw(pixel, r, color);
            return;
        }

        sb.Draw(pixel, new Rectangle(r.X, r.Y + radius, r.Width, r.Height - 2 * radius), color);

        for (int i = 0; i < radius; i++)
        {
            float dy = radius - i - 0.5f;
            int inset = (int)MathF.Round(radius - MathF.Sqrt(MathF.Max(0f, radius * radius - dy * dy)));
            int w = r.Width - 2 * inset;
            if (w <= 0)
            {
                continue;
            }

            sb.Draw(pixel, new Rectangle(r.X + inset, r.Y + i, w, 1), color);
            sb.Draw(pixel, new Rectangle(r.X + inset, r.Bottom - 1 - i, w, 1), color);
        }
    }

    /// <summary>
    /// Panel opaque bersudut membulat dengan bingkai tebal (gunakan warna isi yang opaque).
    /// </summary>
    public static void Panel(
        SpriteBatch sb,
        Texture2D pixel,
        Rectangle r,
        Color fill,
        Color border,
        int thickness = 2,
        int radius = 6)
    {
        RoundedRect(sb, pixel, r, border, radius);
        Rectangle inner = r;
        inner.Inflate(-thickness, -thickness);
        RoundedRect(sb, pixel, inner, fill, Math.Max(0, radius - thickness));
    }
}
