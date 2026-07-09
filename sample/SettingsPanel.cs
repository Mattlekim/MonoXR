using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MonoXR.Client;
using Color = Microsoft.Xna.Framework.Color;
using Rectangle = Microsoft.Xna.Framework.Rectangle;

namespace MonoXR.Sample;

/// <summary>
/// A settings-style panel built from the MonoXR controls: a group box holding
/// a menu of sliders and a checkbox, navigated via <see cref="OverlayNavigation"/>
/// (the sample maps the arrow keys to it). Only re-renders when navigation or a
/// value actually changes something.
/// </summary>
public sealed class SettingsPanel : OverlayRenderTarget
{
    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;
    private readonly SpriteFont _font;
    private readonly MonoXRGroupBox _group;
    private readonly MonoXRMenuControl _menu;

    /// <summary>The slider driving the overlay's opacity readout (demo only).</summary>
    public MonoXRSliderControl Volume { get; }

    /// <summary>Checkbox the sample wires to the pulse panel's visibility.</summary>
    public MonoXRCheckbox PulseEnabled { get; }

    public SettingsPanel(GraphicsDevice device, int width, int height, int x, int y)
        : base(device, width, height, x, y)
    {
        _sb = new SpriteBatch(device);
        _white = new Texture2D(device, 1, 1);
        _white.SetData(new[] { Color.White });
        _font = RuntimeFont.Create(device);

        _menu = new MonoXRMenuControl { ItemHeight = 56, ItemSpacing = 6 };
        _menu.Add(new MonoXRLabel("Demo settings"));

        Volume = new MonoXRSliderControl("Volume", 0f, 100f, 60f, step: 5f);
        _menu.Add(Volume);

        _menu.Add(new MonoXRSliderControl("Opacity", 0f, 1f, 1f, step: 0.1f, format: "0.0")
        {
            FillColor = new Color(0x4C, 0xAF, 0x7D),
        });

        PulseEnabled = new MonoXRCheckbox("Show pulse panel", isChecked: true);
        _menu.Add(PulseEnabled);

        _group = new MonoXRGroupBox("Settings", _menu);

        // Every navigation action goes to the menu, and anything it changed
        // (selection, slider value, checkbox) needs a re-render.
        OverlayNavigation.Navigated += OnNavigated;
    }

    private void OnNavigated(OverlayNavAction action)
    {
        _menu.HandleNavigation(action);
        Invalidate();
    }

    public override void Update(GameTime gameTime)
    {
        _group.Bounds = new Rectangle(16, 8, Width - 32, Height - 24);
        _group.Update(gameTime);
    }

    public override void Render(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(16, 16, 26, 235));
        _sb.Begin();
        _group.Draw(_sb, _font, _white);
        _sb.End();
    }

    public override void Dispose()
    {
        OverlayNavigation.Navigated -= OnNavigated;
        _font.Texture.Dispose();
        _white.Dispose();
        _sb.Dispose();
        base.Dispose();
    }
}
