using System;
using Microsoft.Xna.Framework.Graphics;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace MonoXR.Client
{
    /// <summary>
    /// In-VR checkbox mirroring the basic WPF CheckBox contract: nullable
    /// <see cref="IsChecked"/>, optional three-state cycling, and
    /// Checked/Unchecked/Indeterminate/Click events. Toggle it from navigation
    /// input via <see cref="Toggle"/>.
    /// </summary>
    public sealed class MonoXRCheckbox : MonoXRControl
    {
        private bool? _isChecked;

        public MonoXRCheckbox(string content, bool? isChecked = false)
        {
            Content = content;
            _isChecked = isChecked;
        }

        /// <summary>Label drawn next to the box.</summary>
        public string Content { get; set; }

        /// <summary>When true, <see cref="Toggle"/> cycles false → true → null.</summary>
        public bool IsThreeState { get; set; }

        public event Action? Checked;
        public event Action? Unchecked;
        public event Action? Indeterminate;

        /// <summary>Raised on every <see cref="Toggle"/>, after the state events.</summary>
        public event Action? Click;

        /// <summary>true = checked, false = unchecked, null = indeterminate.</summary>
        public bool? IsChecked
        {
            get => _isChecked;
            set
            {
                if (value == _isChecked) return;
                _isChecked = value;
                switch (_isChecked)
                {
                    case true: Checked?.Invoke(); break;
                    case false: Unchecked?.Invoke(); break;
                    case null: Indeterminate?.Invoke(); break;
                }
            }
        }

        /// <summary>Advances the state exactly like a WPF CheckBox click.</summary>
        public void Toggle()
        {
            if (!IsEnabled) return;
            IsChecked = _isChecked switch
            {
                false => true,
                true => IsThreeState ? (bool?)null : false,
                null => false,
            };
            Click?.Invoke();
        }

        // Increase/decrease on a checkbox both just toggle it.
        public override void Increase() => Toggle();
        public override void Decrease() => Toggle();

        // A click anywhere on the row toggles, like clicking a WPF CheckBox's label.
        public override bool OnPointerPress(int x, int y)
        {
            if (!IsEnabled || !Bounds.Contains(x, y)) return false;
            Toggle();
            return true;
        }

        public override void Draw(SpriteBatch spriteBatch, SpriteFont font, Texture2D white)
        {
            DrawSelectionBackground(spriteBatch, white);

            // Drawn as a modern toggle switch: a pill track with a sliding
            // round knob — accent-filled when on, dim when off, knob centered
            // for indeterminate.
            int pad = 12;
            int swH = Math.Min(30, Bounds.Height - 8);
            int swW = swH * 9 / 5;
            int swX = Bounds.X + pad;
            int swY = Bounds.Y + (Bounds.Height - swH) / 2;

            var trackColor = _isChecked == true ? AccentColor : TrackFill;
            MonoXRDraw.RoundedRect(spriteBatch, new XnaRectangle(swX, swY, swW, swH), swH / 2, trackColor);
            if (_isChecked != true)
                MonoXRDraw.RoundedRectOutline(spriteBatch, new XnaRectangle(swX, swY, swW, swH), swH / 2, 2,
                                              IsEnabled ? Divider : DisabledText);
            else if (IsSelected && IsEnabled)
                MonoXRDraw.VerticalFade(spriteBatch, new XnaRectangle(swX + 2, swY + swH, swW - 4, 8),
                                        AccentColor * 0.35f);

            int knobR = swH / 2 - 3;
            int knobCx = _isChecked switch
            {
                true => swX + swW - knobR - 4,
                false => swX + knobR + 4,
                null => swX + swW / 2,
            };
            spriteBatch.Draw(MonoXRDraw.Circle(spriteBatch.GraphicsDevice, knobR),
                new XnaRectangle(knobCx - knobR, swY + swH / 2 - knobR, knobR * 2, knobR * 2),
                IsEnabled ? XnaColor.White : NormalText);

            int textY = Bounds.Y + (Bounds.Height - font.LineSpacing) / 2;
            spriteBatch.DrawString(font, Content,
                                   new XnaVector2(swX + swW + pad, textY), TextColor);
        }
    }
}
