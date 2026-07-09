using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoXR.Client;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace MonoXR.Sample;

/// <summary>
/// Animated panel: a gold box sliding left/right inside a border. Animates
/// continuously, so it invalidates itself every update — the worst case for
/// the host's dirty-flag rendering.
/// </summary>
public sealed class SlidingBoxPanel : OverlayRenderTarget
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private float _time;

    public SlidingBoxPanel(GraphicsDevice device, int width, int height, int x, int y)
        : base(device, width, height, x, y)
    {
        _sb = new SpriteBatch(device);
        _white = new Texture2D(device, 1, 1);
        _white.SetData(new[] { Color.White });
    }

    public override void Update(GameTime gameTime)
    {
        _time += (float)gameTime.ElapsedGameTime.TotalSeconds;
        Invalidate();
    }

    public override void Render(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(20, 40, 80));
        _sb.Begin();
        int bw = Width / 6;
        int bx = (int)((MathF.Sin(_time * 1.5f) * 0.5f + 0.5f) * (Width - bw));
        _sb.Draw(_white, new Rectangle(bx, Height / 2 - bw / 2, bw, bw), Color.Gold);
        DrawBorder(6, Color.White);
        _sb.End();
    }

    private void DrawBorder(int t, Color c)
    {
        _sb.Draw(_white, new Rectangle(0, 0, Width, t), c);
        _sb.Draw(_white, new Rectangle(0, Height - t, Width, t), c);
        _sb.Draw(_white, new Rectangle(0, 0, t, Height), c);
        _sb.Draw(_white, new Rectangle(Width - t, 0, t, Height), c);
    }

    public override void Dispose()
    {
        _white.Dispose();
        _sb.Dispose();
        base.Dispose();
    }
}

/// <summary>
/// Mostly-static panel: a color swatch that steps hue once per second. It only
/// calls <see cref="OverlayRenderTarget.Invalidate"/> on those steps, so the
/// host re-renders it (and republishes the canvas) just once a second — the
/// dirty-flag path the host is built around.
/// </summary>
public sealed class PulsePanel : OverlayRenderTarget
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private int _lastStep = -1;
    private Color _color = Color.DarkRed;

    public PulsePanel(GraphicsDevice device, int width, int height, int x, int y)
        : base(device, width, height, x, y)
    {
        _sb = new SpriteBatch(device);
        _white = new Texture2D(device, 1, 1);
        _white.SetData(new[] { Color.White });
    }

    public override void Update(GameTime gameTime)
    {
        int step = (int)gameTime.TotalGameTime.TotalSeconds;
        if (step == _lastStep) return;
        _lastStep = step;

        float hue = step % 12 / 12f * MathF.Tau;
        _color = new Color(
            (int)(127 + 100 * MathF.Sin(hue)),
            (int)(127 + 100 * MathF.Sin(hue + 2.1f)),
            (int)(127 + 100 * MathF.Sin(hue + 4.2f)));
        Invalidate();
    }

    public override void Render(GameTime gameTime)
    {
        GraphicsDevice.Clear(_color);
        _sb.Begin();
        int t = 6;
        _sb.Draw(_white, new Rectangle(0, 0, Width, t), Color.White);
        _sb.Draw(_white, new Rectangle(0, Height - t, Width, t), Color.White);
        _sb.Draw(_white, new Rectangle(0, 0, t, Height), Color.White);
        _sb.Draw(_white, new Rectangle(Width - t, 0, t, Height), Color.White);
        _sb.End();
    }

    public override void Dispose()
    {
        _white.Dispose();
        _sb.Dispose();
        base.Dispose();
    }
}
