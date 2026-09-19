using System;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace MonoXR.Client
{
    /// <summary>
    /// In-VR slider: name on the left, value bar with numeric readout on the
    /// right. Adjust with <see cref="Increase"/>/<see cref="Decrease"/> (wire
    /// these to <see cref="OverlayNavigation"/> actions in the hosting overlay).
    /// </summary>
    public sealed class MonoXRSliderControl : MonoXRControl
    {
        private float _value;
        private XnaRectangle _track; // value bar as last drawn (empty until drawn)
        private bool _dragging;

        public MonoXRSliderControl(string name, float min, float max, float value,
                                   float step = 1f, string format = "0")
        {
            if (max <= min) throw new ArgumentException("max must be greater than min", nameof(max));
            Name = name;
            Minimum = min;
            Maximum = max;
            Step = step;
            Format = format;
            _value = Math.Clamp(value, min, max);
        }

        public string Name { get; set; }
        public float Minimum { get; }
        public float Maximum { get; }
        public float Step { get; set; }

        /// <summary>Numeric format for the value readout (e.g. "0", "0.0").</summary>
        public string Format { get; set; }

        /// <summary>
        /// Fill color of the value bar, mirroring the WPF slider's per-axis
        /// FillBrush (e.g. blue for Surge, green for Sway). Null falls back to
        /// the shared accent color.
        /// </summary>
        public XnaColor? FillColor { get; set; }

        // WPF slider template colors (Resources/Styles.xaml).
        private static readonly XnaColor TrackEmpty = new XnaColor(0x37, 0x37, 0x4E);
        private static readonly XnaColor ThumbFill = XnaColor.White;
        private static readonly XnaColor ThumbStroke = new XnaColor(0x28, 0x28, 0x40);

        /// <summary>Raised whenever <see cref="Value"/> actually changes.</summary>
        public event Action<float>? ValueChanged;

        public float Value
        {
            get => _value;
            set
            {
                float clamped = Math.Clamp(value, Minimum, Maximum);
                if (clamped == _value) return;
                _value = clamped;
                ValueChanged?.Invoke(_value);
            }
        }

        public override void Increase() { if (IsEnabled) Value += Step; }
        public override void Decrease() { if (IsEnabled) Value -= Step; }

        // Mouse: press on (or near) the bar jumps the thumb there, dragging
        // follows the pointer. Uses the bar geometry from the last Draw.
        public override bool OnPointerPress(int x, int y)
        {
            if (!IsEnabled || _track.Width <= 0) return false;
            var hit = _track;
            hit.Inflate(14, Math.Max(18, (Bounds.Height - _track.Height) / 2));
            if (!hit.Contains(x, y)) return false;
            _dragging = true;
            SetFromPointer(x);
            return true;
        }

        public override void OnPointerDrag(int x, int y)
        {
            if (_dragging) SetFromPointer(x);
        }

        public override void OnPointerRelease(int x, int y) => _dragging = false;

        private void SetFromPointer(int x)
        {
            float t = Math.Clamp((x - _track.X) / (float)_track.Width, 0f, 1f);
            float v = Minimum + t * (Maximum - Minimum);
            if (Step > 0)
                v = Minimum + MathF.Round((v - Minimum) / Step) * Step;
            Value = v;
        }

        public override void Draw(SpriteBatch spriteBatch, SpriteFont font, Texture2D white)
        {
            DrawSelectionBackground(spriteBatch, white);

            int pad = 12;
            int textY = Bounds.Y + (Bounds.Height - font.LineSpacing) / 2;
            spriteBatch.DrawString(font, Name, new XnaVector2(Bounds.X + pad, textY), TextColor);

            // Value bar occupies the right half, leaving room for the readout:
            // a rounded pill track, colored fill with a soft glow underneath,
            // and a circular thumb with a dark ring.
            string readout = _value.ToString(Format);
            int readoutW = (int)Math.Ceiling(font.MeasureString(readout).X);
            int barX = Bounds.X + Bounds.Width / 2;
            int barW = Bounds.X + Bounds.Width - barX - readoutW - pad * 2;
            int barH = 8;
            int barY = Bounds.Y + (Bounds.Height - barH) / 2;
            // While dragging keep the track frozen: the readout width changes
            // with the value, which would otherwise shift the bar under the pointer.
            if (!_dragging) _track = new XnaRectangle(barX, barY, Math.Max(0, barW), barH);
            else barW = _track.Width;
            if (barW > 0)
            {
                float t = Math.Clamp((_value - Minimum) / (Maximum - Minimum), 0f, 1f);
                var fill = !IsEnabled ? DisabledText : (FillColor ?? AccentColor);
                MonoXRDraw.RoundedRect(spriteBatch, new XnaRectangle(barX, barY, barW, barH), barH / 2, TrackEmpty);
                int fillW = (int)(barW * t);
                if (fillW >= barH)
                {
                    MonoXRDraw.RoundedRect(spriteBatch, new XnaRectangle(barX, barY, fillW, barH), barH / 2, fill);
                    // Soft glow under the filled part, stronger when selected.
                    MonoXRDraw.VerticalFade(spriteBatch,
                        new XnaRectangle(barX + 2, barY + barH, fillW - 4, IsSelected ? 10 : 6),
                        fill * (IsSelected ? 0.35f : 0.2f));
                }

                // Circular thumb centered on the value, clamped to the track.
                int thumbR = 11;
                int thumbCx = Math.Clamp(barX + fillW, barX + thumbR, barX + barW - thumbR);
                int thumbCy = Bounds.Y + Bounds.Height / 2;
                if (IsSelected && IsEnabled)
                    spriteBatch.Draw(MonoXRDraw.Circle(spriteBatch.GraphicsDevice, thumbR + 5),
                        new XnaRectangle(thumbCx - thumbR - 5, thumbCy - thumbR - 5, (thumbR + 5) * 2, (thumbR + 5) * 2),
                        fill * 0.35f);
                spriteBatch.Draw(MonoXRDraw.Circle(spriteBatch.GraphicsDevice, thumbR),
                    new XnaRectangle(thumbCx - thumbR, thumbCy - thumbR, thumbR * 2, thumbR * 2), ThumbStroke);
                int innerR = thumbR - 3;
                spriteBatch.Draw(MonoXRDraw.Circle(spriteBatch.GraphicsDevice, innerR),
                    new XnaRectangle(thumbCx - innerR, thumbCy - innerR, innerR * 2, innerR * 2),
                    IsEnabled ? ThumbFill : NormalText);
            }

            spriteBatch.DrawString(font, readout,
                                   new XnaVector2(Bounds.X + Bounds.Width - readoutW - pad, textY),
                                   TextColor);
        }
    }
}
