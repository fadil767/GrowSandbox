using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace GrowSandbox.Client.Graphics;

/// <summary>
/// Bitmap pixel font 5x7 mandiri dengan pembulatan koordinat ke integer (<see cref="MathF.Floor"/>),
/// drop shadow 1px bersih (alpha 180), dan fungsi <see cref="WrapText(string, float, int)"/> berbasis lebar piksel.
/// </summary>
public sealed class PixelFont : IDisposable
{
    public const int GlyphWidth = 5;
    public const int GlyphHeight = 7;
    public const int CharAdvance = 6;
    public const int LineHeight = 9;

    private const int FirstChar = 32;
    private const int LastChar = 126;
    private const int CharsPerRow = 16;

    private readonly Texture2D _atlas;

    public PixelFont(GraphicsDevice graphicsDevice)
    {
        ArgumentNullException.ThrowIfNull(graphicsDevice);
        _atlas = BuildAtlas(graphicsDevice);
    }

    public int Advance(string? text, int scale = 1) =>
        string.IsNullOrEmpty(text) ? 0 : text.Length * CharAdvance * scale;

    public Vector2 MeasureString(string? text, int scale = 1)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Vector2.Zero;
        }

        int maxLineLen = 0;
        int currentLen = 0;
        int lines = 1;

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                if (currentLen > maxLineLen) maxLineLen = currentLen;
                currentLen = 0;
                lines++;
            }
            else
            {
                currentLen++;
            }
        }

        if (currentLen > maxLineLen) maxLineLen = currentLen;
        int width = maxLineLen > 0 ? (maxLineLen * CharAdvance - 1) * scale : 0;
        int height = ((lines - 1) * LineHeight + GlyphHeight) * scale;
        return new Vector2(width, height);
    }

    /// <summary>
    /// Menggambar teks piksel tajam pada koordinat integer (<see cref="MathF.Floor"/>)
    /// dengan 1px drop shadow bersih (+1px, +1px, hitam alpha 180).
    /// </summary>
    public void DrawString(
        SpriteBatch spriteBatch,
        string? text,
        Vector2 position,
        Color color,
        int scale = 1,
        bool dropShadow = true,
        bool bold = false)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0)
        {
            return;
        }

        int ix = (int)MathF.Floor(position.X);
        int iy = (int)MathF.Floor(position.Y);

        if (dropShadow)
        {
            byte shadowAlpha = (byte)(180 * (color.A / 255f));
            var shadowColor = new Color((byte)0, (byte)0, (byte)0, shadowAlpha);
            DrawCore(spriteBatch, text, ix + 1, iy + 1, shadowColor, scale);
            if (bold)
            {
                DrawCore(spriteBatch, text, ix + 2, iy + 1, shadowColor, scale);
            }
        }

        DrawCore(spriteBatch, text, ix, iy, color, scale);
        if (bold)
        {
            DrawCore(spriteBatch, text, ix + 1, iy, color, scale);
        }
    }

    /// <summary>
    /// Menggambar teks dengan outline tipis 1px (4-arah kardinal) pada koordinat integer yang tajam,
    /// cocok untuk judul banner besar dan angka kontras tinggi.
    /// </summary>
    public void DrawStringThinOutline(
        SpriteBatch spriteBatch,
        string? text,
        Vector2 position,
        Color color,
        int scale = 1,
        Color? outlineColor = null)
    {
        if (string.IsNullOrEmpty(text) || color.A == 0)
        {
            return;
        }

        int ix = (int)MathF.Floor(position.X);
        int iy = (int)MathF.Floor(position.Y);
        Color oc = outlineColor ?? new Color((byte)0, (byte)0, (byte)0, (byte)(210 * (color.A / 255f)));

        DrawCore(spriteBatch, text, ix - 1, iy, oc, scale);
        DrawCore(spriteBatch, text, ix + 1, iy, oc, scale);
        DrawCore(spriteBatch, text, ix, iy - 1, oc, scale);
        DrawCore(spriteBatch, text, ix, iy + 1, oc, scale);
        DrawCore(spriteBatch, text, ix + 1, iy + 1, oc, scale);
        DrawCore(spriteBatch, text, ix, iy, color, scale);
    }

    /// <summary>
    /// Membungkus teks secara otomatis berdasarkan lebar maksimum piksel (<paramref name="maxWidth"/>)
    /// tanpa pemotongan elipsis.
    /// </summary>
    public List<string> WrapText(string? text, float maxWidth, int scale = 1)
    {
        var lines = new List<string>();
        if (string.IsNullOrEmpty(text))
        {
            lines.Add(string.Empty);
            return lines;
        }

        int charStep = Math.Max(1, CharAdvance * scale);
        int maxChars = Math.Max(4, (int)MathF.Floor((maxWidth + scale) / charStep));

        foreach (string paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = new StringBuilder();
            string[] words = paragraph.Split(' ');

            for (int w = 0; w < words.Length; w++)
            {
                string word = words[w];

                // Pecah kata yang lebih panjang dari satu baris penuh.
                while (word.Length > maxChars)
                {
                    if (current.Length > 0)
                    {
                        lines.Add(current.ToString());
                        current.Clear();
                    }

                    lines.Add(word[..maxChars]);
                    word = word[maxChars..];
                }

                int projected = current.Length == 0 ? word.Length : current.Length + 1 + word.Length;
                if (projected > maxChars && current.Length > 0)
                {
                    lines.Add(current.ToString());
                    current.Clear();
                }

                if (current.Length > 0)
                {
                    current.Append(' ');
                }

                current.Append(word);
            }

            if (current.Length > 0)
            {
                lines.Add(current.ToString());
            }
        }

        if (lines.Count == 0)
        {
            lines.Add(string.Empty);
        }

        return lines;
    }

    private void DrawCore(SpriteBatch spriteBatch, string text, int startX, int startY, Color color, int scale)
    {
        int x = startX;
        int y = startY;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\n')
            {
                x = startX;
                y += LineHeight * scale;
                continue;
            }

            int code = c;
            if (code < FirstChar || code > LastChar)
            {
                code = '?';
            }

            int glyphIndex = code - FirstChar;
            int col = glyphIndex % CharsPerRow;
            int row = glyphIndex / CharsPerRow;

            var source = new Rectangle(col * GlyphWidth, row * GlyphHeight, GlyphWidth, GlyphHeight);
            var dest = new Rectangle(x, y, GlyphWidth * scale, GlyphHeight * scale);
            spriteBatch.Draw(_atlas, dest, source, color);

            x += CharAdvance * scale;
        }
    }

    private static Texture2D BuildAtlas(GraphicsDevice graphicsDevice)
    {
        int totalChars = LastChar - FirstChar + 1;
        int rows = (totalChars + CharsPerRow - 1) / CharsPerRow;
        int texWidth = CharsPerRow * GlyphWidth;
        int texHeight = rows * GlyphHeight;
        var pixels = new Color[texWidth * texHeight];

        for (int code = FirstChar; code <= LastChar; code++)
        {
            int idx = code - FirstChar;
            int cellX = (idx % CharsPerRow) * GlyphWidth;
            int cellY = (idx / CharsPerRow) * GlyphHeight;
            byte[] bitmap = GetGlyphRows((char)code);

            for (int r = 0; r < GlyphHeight; r++)
            {
                byte bits = bitmap[r];
                for (int c = 0; c < GlyphWidth; c++)
                {
                    if ((bits & (1 << (4 - c))) != 0)
                    {
                        pixels[(cellY + r) * texWidth + (cellX + c)] = Color.White;
                    }
                }
            }
        }

        var texture = new Texture2D(graphicsDevice, texWidth, texHeight);
        texture.SetData(pixels);
        return texture;
    }

    private static byte[] GetGlyphRows(char ch)
    {
        return ch switch
        {
            ' ' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00],
            '!' => [0x04, 0x04, 0x04, 0x04, 0x04, 0x00, 0x04],
            '"' => [0x0A, 0x0A, 0x00, 0x00, 0x00, 0x00, 0x00],
            '#' => [0x0A, 0x1F, 0x0A, 0x0A, 0x1F, 0x0A, 0x00],
            '$' => [0x04, 0x0F, 0x14, 0x0E, 0x05, 0x1E, 0x04],
            '%' => [0x19, 0x1A, 0x04, 0x04, 0x0B, 0x13, 0x00],
            '&' => [0x0C, 0x12, 0x14, 0x08, 0x15, 0x12, 0x0D],
            '\'' => [0x04, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00],
            '(' => [0x02, 0x04, 0x08, 0x08, 0x08, 0x04, 0x02],
            ')' => [0x08, 0x04, 0x02, 0x02, 0x02, 0x04, 0x08],
            '*' => [0x00, 0x04, 0x15, 0x0E, 0x15, 0x04, 0x00],
            '+' => [0x00, 0x04, 0x04, 0x1F, 0x04, 0x04, 0x00],
            ',' => [0x00, 0x00, 0x00, 0x00, 0x04, 0x04, 0x08],
            '-' => [0x00, 0x00, 0x00, 0x1F, 0x00, 0x00, 0x00],
            '.' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x0C, 0x0C],
            '/' => [0x01, 0x02, 0x02, 0x04, 0x08, 0x08, 0x10],
            '0' => [0x0E, 0x11, 0x13, 0x15, 0x19, 0x11, 0x0E],
            '1' => [0x04, 0x0C, 0x04, 0x04, 0x04, 0x04, 0x0E],
            '2' => [0x0E, 0x11, 0x01, 0x06, 0x08, 0x10, 0x1F],
            '3' => [0x1F, 0x02, 0x04, 0x06, 0x01, 0x11, 0x0E],
            '4' => [0x02, 0x06, 0x0A, 0x12, 0x1F, 0x02, 0x02],
            '5' => [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
            '6' => [0x06, 0x08, 0x10, 0x1E, 0x11, 0x11, 0x0E],
            '7' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x08, 0x08],
            '8' => [0x0E, 0x11, 0x11, 0x0E, 0x11, 0x11, 0x0E],
            '9' => [0x0E, 0x11, 0x11, 0x0F, 0x01, 0x02, 0x0C],
            ':' => [0x00, 0x0C, 0x0C, 0x00, 0x0C, 0x0C, 0x00],
            ';' => [0x00, 0x04, 0x04, 0x00, 0x04, 0x04, 0x08],
            '<' => [0x02, 0x04, 0x08, 0x10, 0x08, 0x04, 0x02],
            '=' => [0x00, 0x00, 0x1F, 0x00, 0x1F, 0x00, 0x00],
            '>' => [0x08, 0x04, 0x02, 0x01, 0x02, 0x04, 0x08],
            '?' => [0x0E, 0x11, 0x01, 0x02, 0x04, 0x00, 0x04],
            '@' => [0x0E, 0x11, 0x17, 0x15, 0x17, 0x10, 0x0E],
            'A' => [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            'B' => [0x1E, 0x11, 0x11, 0x1E, 0x11, 0x11, 0x1E],
            'C' => [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
            'D' => [0x1C, 0x12, 0x11, 0x11, 0x11, 0x12, 0x1C],
            'E' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
            'F' => [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
            'G' => [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
            'H' => [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            'I' => [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
            'J' => [0x07, 0x02, 0x02, 0x02, 0x02, 0x12, 0x0C],
            'K' => [0x11, 0x12, 0x14, 0x18, 0x14, 0x12, 0x11],
            'L' => [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
            'M' => [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
            'N' => [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
            'O' => [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            'P' => [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
            'Q' => [0x0E, 0x11, 0x11, 0x11, 0x15, 0x12, 0x0D],
            'R' => [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
            'S' => [0x0F, 0x10, 0x10, 0x0E, 0x01, 0x01, 0x1E],
            'T' => [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            'U' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            'V' => [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
            'W' => [0x11, 0x11, 0x11, 0x15, 0x15, 0x1B, 0x11],
            'X' => [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
            'Y' => [0x11, 0x11, 0x0A, 0x04, 0x04, 0x04, 0x04],
            'Z' => [0x1F, 0x01, 0x02, 0x04, 0x08, 0x10, 0x1F],
            '[' => [0x0E, 0x08, 0x08, 0x08, 0x08, 0x08, 0x0E],
            '\\' => [0x10, 0x08, 0x08, 0x04, 0x02, 0x02, 0x01],
            ']' => [0x0E, 0x02, 0x02, 0x02, 0x02, 0x02, 0x0E],
            '^' => [0x04, 0x0A, 0x11, 0x00, 0x00, 0x00, 0x00],
            '_' => [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1F],
            '`' => [0x08, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00],
            'a' => [0x00, 0x00, 0x0E, 0x01, 0x0F, 0x11, 0x0F],
            'b' => [0x10, 0x10, 0x1E, 0x11, 0x11, 0x11, 0x1E],
            'c' => [0x00, 0x00, 0x0F, 0x10, 0x10, 0x10, 0x0F],
            'd' => [0x01, 0x01, 0x0F, 0x11, 0x11, 0x11, 0x0F],
            'e' => [0x00, 0x00, 0x0E, 0x11, 0x1F, 0x10, 0x0E],
            'f' => [0x06, 0x08, 0x1E, 0x08, 0x08, 0x08, 0x08],
            'g' => [0x00, 0x0F, 0x11, 0x11, 0x0F, 0x01, 0x0E],
            'h' => [0x10, 0x10, 0x1E, 0x11, 0x11, 0x11, 0x11],
            'i' => [0x04, 0x00, 0x0C, 0x04, 0x04, 0x04, 0x0E],
            'j' => [0x02, 0x00, 0x06, 0x02, 0x02, 0x12, 0x0C],
            'k' => [0x10, 0x10, 0x12, 0x14, 0x18, 0x14, 0x12],
            'l' => [0x0C, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
            'm' => [0x00, 0x00, 0x1A, 0x15, 0x15, 0x15, 0x11],
            'n' => [0x00, 0x00, 0x1E, 0x11, 0x11, 0x11, 0x11],
            'o' => [0x00, 0x00, 0x0E, 0x11, 0x11, 0x11, 0x0E],
            'p' => [0x00, 0x00, 0x1E, 0x11, 0x11, 0x1E, 0x10],
            'q' => [0x00, 0x00, 0x0F, 0x11, 0x11, 0x0F, 0x01],
            'r' => [0x00, 0x00, 0x16, 0x19, 0x10, 0x10, 0x10],
            's' => [0x00, 0x00, 0x0F, 0x10, 0x0E, 0x01, 0x1E],
            't' => [0x08, 0x08, 0x1E, 0x08, 0x08, 0x09, 0x06],
            'u' => [0x00, 0x00, 0x11, 0x11, 0x11, 0x11, 0x0F],
            'v' => [0x00, 0x00, 0x11, 0x11, 0x11, 0x0A, 0x04],
            'w' => [0x00, 0x00, 0x11, 0x11, 0x15, 0x15, 0x0A],
            'x' => [0x00, 0x00, 0x11, 0x0A, 0x04, 0x0A, 0x11],
            'y' => [0x00, 0x00, 0x11, 0x11, 0x0F, 0x01, 0x0E],
            'z' => [0x00, 0x00, 0x1F, 0x02, 0x04, 0x08, 0x1F],
            '{' => [0x02, 0x04, 0x04, 0x08, 0x04, 0x04, 0x02],
            '|' => [0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            '}' => [0x08, 0x04, 0x04, 0x02, 0x04, 0x04, 0x08],
            '~' => [0x00, 0x08, 0x15, 0x02, 0x00, 0x00, 0x00],
            _ => [0x0E, 0x11, 0x01, 0x02, 0x04, 0x00, 0x04],
        };
    }

    public void Dispose()
    {
        _atlas.Dispose();
    }
}
