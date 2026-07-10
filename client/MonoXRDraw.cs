using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;

namespace MonoXR.Client
{
    /// <summary>
    /// SpriteBatch drawing helpers for the in-VR UI: antialiased rounded
    /// rectangles, circles, outlines and gradient fades — all built from
    /// small runtime-baked textures so no content pipeline is needed.
    /// Textures are cached per GraphicsDevice; call inside Begin/End with
    /// the default (premultiplied AlphaBlend) sprite batch state.
    /// </summary>
    public static class MonoXRDraw
    {
        private static readonly Dictionary<(GraphicsDevice, int), Texture2D> _circles = new();
        private static readonly Dictionary<(GraphicsDevice, int, int), Texture2D> _rings = new();
        private static readonly Dictionary<GraphicsDevice, Texture2D> _whites = new();
        private static readonly Dictionary<GraphicsDevice, Texture2D> _vFades = new();
        private static readonly Dictionary<GraphicsDevice, Texture2D> _hFades = new();

        private static Texture2D White(GraphicsDevice dev)
        {
            if (_whites.TryGetValue(dev, out var t) && !t.IsDisposed) return t;
            t = new Texture2D(dev, 1, 1);
            t.SetData(new[] { XnaColor.White });
            _whites[dev] = t;
            return t;
        }

        /// <summary>Antialiased filled circle, diameter 2×radius.</summary>
        public static Texture2D Circle(GraphicsDevice dev, int radius)
        {
            radius = Math.Max(1, radius);
            if (_circles.TryGetValue((dev, radius), out var t) && !t.IsDisposed) return t;

            int d = radius * 2;
            var data = new XnaColor[d * d];
            float c = radius - 0.5f;
            for (int y = 0; y < d; y++)
                for (int x = 0; x < d; x++)
                {
                    float dist = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float cov = Math.Clamp(radius - dist, 0f, 1f);
                    data[y * d + x] = XnaColor.White * cov; // premultiplied
                }
            t = new Texture2D(dev, d, d);
            t.SetData(data);
            _circles[(dev, radius)] = t;
            return t;
        }

        /// <summary>Antialiased circle outline (annulus) — for rounded-rect borders.</summary>
        private static Texture2D Ring(GraphicsDevice dev, int radius, int thickness)
        {
            radius = Math.Max(1, radius);
            thickness = Math.Clamp(thickness, 1, radius);
            if (_rings.TryGetValue((dev, radius, thickness), out var t) && !t.IsDisposed) return t;

            int d = radius * 2;
            var data = new XnaColor[d * d];
            float c = radius - 0.5f;
            for (int y = 0; y < d; y++)
                for (int x = 0; x < d; x++)
                {
                    float dist = MathF.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    float outer = Math.Clamp(radius - dist, 0f, 1f);
                    float inner = Math.Clamp(dist - (radius - thickness), 0f, 1f);
                    data[y * d + x] = XnaColor.White * (outer * inner);
                }
            t = new Texture2D(dev, d, d);
            t.SetData(data);
            _rings[(dev, radius, thickness)] = t;
            return t;
        }

        /// <summary>1×256 premultiplied white→transparent vertical fade.</summary>
        private static Texture2D VFade(GraphicsDevice dev)
        {
            if (_vFades.TryGetValue(dev, out var t) && !t.IsDisposed) return t;
            var data = new XnaColor[256];
            for (int i = 0; i < 256; i++)
                data[i] = XnaColor.White * (1f - i / 255f);
            t = new Texture2D(dev, 1, 256);
            t.SetData(data);
            _vFades[dev] = t;
            return t;
        }

        /// <summary>256×1 premultiplied white→transparent horizontal fade.</summary>
        private static Texture2D HFade(GraphicsDevice dev)
        {
            if (_hFades.TryGetValue(dev, out var t) && !t.IsDisposed) return t;
            var data = new XnaColor[256];
            for (int i = 0; i < 256; i++)
                data[i] = XnaColor.White * (1f - i / 255f);
            t = new Texture2D(dev, 256, 1);
            t.SetData(data);
            _hFades[dev] = t;
            return t;
        }

        /// <summary>
        /// Filled rounded rectangle. <paramref name="roundTop"/>/<paramref name="roundBottom"/>
        /// square off individual ends (e.g. a title bar rounded only at the top).
        /// </summary>
        public static void RoundedRect(SpriteBatch sb, XnaRectangle r, int radius, XnaColor color,
                                       bool roundTop = true, bool roundBottom = true)
        {
            var dev = sb.GraphicsDevice;
            var white = White(dev);
            radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
            if (radius <= 0 || (!roundTop && !roundBottom))
            {
                sb.Draw(white, r, color);
                return;
            }

            var circle = Circle(dev, radius);
            int topR = roundTop ? radius : 0;
            int botR = roundBottom ? radius : 0;

            if (roundTop)
            {
                sb.Draw(circle, new XnaRectangle(r.X, r.Y, radius, radius),
                        new XnaRectangle(0, 0, radius, radius), color);
                sb.Draw(circle, new XnaRectangle(r.Right - radius, r.Y, radius, radius),
                        new XnaRectangle(radius, 0, radius, radius), color);
                sb.Draw(white, new XnaRectangle(r.X + radius, r.Y, r.Width - radius * 2, radius), color);
            }
            if (roundBottom)
            {
                sb.Draw(circle, new XnaRectangle(r.X, r.Bottom - radius, radius, radius),
                        new XnaRectangle(0, radius, radius, radius), color);
                sb.Draw(circle, new XnaRectangle(r.Right - radius, r.Bottom - radius, radius, radius),
                        new XnaRectangle(radius, radius, radius, radius), color);
                sb.Draw(white, new XnaRectangle(r.X + radius, r.Bottom - radius, r.Width - radius * 2, radius), color);
            }
            // Middle band between the rounded ends.
            sb.Draw(white, new XnaRectangle(r.X, r.Y + topR, r.Width, r.Height - topR - botR), color);
        }

        /// <summary>Rounded rectangle outline (border only).</summary>
        public static void RoundedRectOutline(SpriteBatch sb, XnaRectangle r, int radius,
                                              int thickness, XnaColor color)
        {
            var dev = sb.GraphicsDevice;
            var white = White(dev);
            radius = Math.Min(radius, Math.Min(r.Width, r.Height) / 2);
            thickness = Math.Max(1, thickness);
            if (radius <= thickness)
            {
                sb.Draw(white, new XnaRectangle(r.X, r.Y, r.Width, thickness), color);
                sb.Draw(white, new XnaRectangle(r.X, r.Bottom - thickness, r.Width, thickness), color);
                sb.Draw(white, new XnaRectangle(r.X, r.Y + thickness, thickness, r.Height - thickness * 2), color);
                sb.Draw(white, new XnaRectangle(r.Right - thickness, r.Y + thickness, thickness, r.Height - thickness * 2), color);
                return;
            }

            var ring = Ring(dev, radius, thickness);
            sb.Draw(ring, new XnaRectangle(r.X, r.Y, radius, radius),
                    new XnaRectangle(0, 0, radius, radius), color);
            sb.Draw(ring, new XnaRectangle(r.Right - radius, r.Y, radius, radius),
                    new XnaRectangle(radius, 0, radius, radius), color);
            sb.Draw(ring, new XnaRectangle(r.X, r.Bottom - radius, radius, radius),
                    new XnaRectangle(0, radius, radius, radius), color);
            sb.Draw(ring, new XnaRectangle(r.Right - radius, r.Bottom - radius, radius, radius),
                    new XnaRectangle(radius, radius, radius, radius), color);
            sb.Draw(white, new XnaRectangle(r.X + radius, r.Y, r.Width - radius * 2, thickness), color);
            sb.Draw(white, new XnaRectangle(r.X + radius, r.Bottom - thickness, r.Width - radius * 2, thickness), color);
            sb.Draw(white, new XnaRectangle(r.X, r.Y + radius, thickness, r.Height - radius * 2), color);
            sb.Draw(white, new XnaRectangle(r.Right - thickness, r.Y + radius, thickness, r.Height - radius * 2), color);
        }

        /// <summary>
        /// Rectangle fading from <paramref name="color"/> at the top to fully
        /// transparent at the bottom (flip with <paramref name="up"/>) — for
        /// sheens and soft glows under accent lines.
        /// </summary>
        public static void VerticalFade(SpriteBatch sb, XnaRectangle r, XnaColor color, bool up = false)
        {
            sb.Draw(VFade(sb.GraphicsDevice), r, null, color, 0f, XnaVector2Zero,
                    up ? SpriteEffects.FlipVertically : SpriteEffects.None, 0f);
        }

        /// <summary>Rectangle fading from <paramref name="color"/> at the left to transparent at the right.</summary>
        public static void HorizontalFade(SpriteBatch sb, XnaRectangle r, XnaColor color, bool left = false)
        {
            sb.Draw(HFade(sb.GraphicsDevice), r, null, color, 0f, XnaVector2Zero,
                    left ? SpriteEffects.FlipHorizontally : SpriteEffects.None, 0f);
        }

        private static readonly Microsoft.Xna.Framework.Vector2 XnaVector2Zero = Microsoft.Xna.Framework.Vector2.Zero;
    }
}
