using System;
using Microsoft.Xna.Framework.Graphics;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace MonoXR.Client
{
    /// <summary>
    /// In-VR section header: bright text with a divider line filling the rest
    /// of the row, matching the "VR Overlay"/"Telemetry" section style in the
    /// WPF settings window. Not selectable — menu navigation skips it.
    /// </summary>
    public sealed class MonoXRLabel : MonoXRControl
    {
        public MonoXRLabel(string text)
        {
            Text = text;
            IsEnabled = false;   // labels are never a navigation target
            PreferredHeight = 36;
        }

        public string Text { get; set; }

        public override void Draw(SpriteBatch spriteBatch, SpriteFont font, Texture2D white)
        {
            // Small rounded accent tick in front of the header text.
            int tickH = font.LineSpacing - 12;
            int tickY = Bounds.Y + (Bounds.Height - tickH) / 2;
            MonoXRDraw.RoundedRect(spriteBatch, new XnaRectangle(Bounds.X, tickY, 5, tickH), 2, AccentSelected);

            int textX = Bounds.X + 14;
            int textY = Bounds.Y + (Bounds.Height - font.LineSpacing) / 2;
            spriteBatch.DrawString(font, Text, new XnaVector2(textX, textY), SelectedText);

            // Divider fading out toward the row's right edge.
            int lineX = textX + (int)Math.Ceiling(font.MeasureString(Text).X) + 12;
            int lineY = Bounds.Y + Bounds.Height / 2;
            if (lineX < Bounds.Right)
                MonoXRDraw.HorizontalFade(spriteBatch, new XnaRectangle(lineX, lineY, Bounds.Right - lineX, 2), Divider);
        }
    }
}
