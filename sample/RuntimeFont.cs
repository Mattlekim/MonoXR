using System.Drawing.Imaging;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaVector3 = Microsoft.Xna.Framework.Vector3;

namespace MonoXR.Sample;

/// <summary>
/// Builds a <see cref="SpriteFont"/> at runtime by rasterizing ASCII glyphs
/// with GDI+ (System.Drawing ships with the WinForms reference the host
/// already needs), so the sample needs no MonoGame content pipeline.
/// </summary>
public static class RuntimeFont
{
    public static SpriteFont Create(GraphicsDevice device, string family = "Segoe UI", float pixelSize = 22f)
    {
        const int first = 32, count = 95; // printable ASCII: ' ' .. '~'
        using var gdiFont = new System.Drawing.Font(family, pixelSize,
            System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
        var format = System.Drawing.StringFormat.GenericTypographic;

        // Measure every glyph first.
        using var measureBmp = new System.Drawing.Bitmap(1, 1);
        using var mg = System.Drawing.Graphics.FromImage(measureBmp);
        int lineHeight = (int)MathF.Ceiling(gdiFont.GetHeight(mg));
        var widths = new int[count];
        for (int i = 0; i < count; i++)
        {
            char c = (char)(first + i);
            var s = mg.MeasureString(c.ToString(), gdiFont, System.Drawing.PointF.Empty, format);
            // GenericTypographic measures whitespace as 0 — give the space a real advance.
            widths[i] = c == ' ' ? (int)(pixelSize * 0.35f) : Math.Max(1, (int)MathF.Ceiling(s.Width));
        }

        // Lay the glyphs out on a grid and rasterize white-on-transparent.
        const int cols = 12, pad = 2;
        int rows = (count + cols - 1) / cols;
        int cellW = widths.Max() + pad, cellH = lineHeight + pad;
        using var bmp = new System.Drawing.Bitmap(cols * cellW, rows * cellH, PixelFormat.Format32bppArgb);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        g.Clear(System.Drawing.Color.Transparent);

        var characters = new List<char>(count);
        var glyphBounds = new List<XnaRectangle>(count);
        var cropping = new List<XnaRectangle>(count);
        var kerning = new List<XnaVector3>(count);
        for (int i = 0; i < count; i++)
        {
            char c = (char)(first + i);
            int cx = i % cols * cellW, cy = i / cols * cellH;
            g.DrawString(c.ToString(), gdiFont, System.Drawing.Brushes.White,
                         new System.Drawing.PointF(cx, cy), format);
            characters.Add(c);
            glyphBounds.Add(new XnaRectangle(cx, cy, widths[i], lineHeight));
            cropping.Add(new XnaRectangle(0, 0, widths[i], lineHeight));
            kerning.Add(new XnaVector3(0, widths[i], 1));
        }

        // Copy to a texture as premultiplied alpha (white text → r=g=b=a).
        var data = new XnaColor[bmp.Width * bmp.Height];
        var bits = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            byte* src = (byte*)bits.Scan0;
            for (int y = 0; y < bmp.Height; y++)
            {
                byte* row = src + y * bits.Stride;
                for (int x = 0; x < bmp.Width; x++)
                {
                    byte a = row[x * 4 + 3];
                    data[y * bmp.Width + x] = new XnaColor(a, a, a, a);
                }
            }
        }
        bmp.UnlockBits(bits);

        var texture = new Texture2D(device, bmp.Width, bmp.Height);
        texture.SetData(data);
        return new SpriteFont(texture, glyphBounds, cropping, characters,
                              lineHeight, spacing: 0f, kerning, defaultCharacter: '?');
    }
}
