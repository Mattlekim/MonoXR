using System.Diagnostics;
using System.Numerics;
using MonoXR.Client;
using MonoXR.Sample;

// One 1024x1024 overlay canvas in VR, composed from two panels. The host owns
// a headless MonoGame device — no Game loop or window needed.
using var host = new MonoGameOverlayHost(1024, 1024);
host.DisplaySize = new Vector2(2f, 2f);   // meters in VR
host.Distance = 2.5f;                     // meters in front of the origin

host.AddRenderTarget(new SlidingBoxPanel(host.GraphicsDevice, 640, 320, x: 32, y: 32));
var pulse = host.AddRenderTarget(new PulsePanel(host.GraphicsDevice, 256, 256, x: 736, y: 736));

// Settings panel built from the MonoXR controls (group box, menu, sliders,
// checkbox), driven by OverlayNavigation — mapped to the arrow keys below.
var settings = host.AddRenderTarget(new SettingsPanel(host.GraphicsDevice, 560, 420, x: 32, y: 384));
settings.PulseEnabled.Checked += () => pulse.Visible = true;
settings.PulseEnabled.Unchecked += () => pulse.Visible = false;
settings.Volume.ValueChanged += v => Console.WriteLine($"[MonoXR] Volume slider: {v}");

host.LayerAttachedChanged += attached => Console.WriteLine(attached
    ? "[MonoXR] OpenXR game attached — overlay is visible in VR."
    : "[MonoXR] OpenXR game closed.");
host.DragCompleted += p => Console.WriteLine($"[MonoXR] Panel moved to ({p.X}, {p.Y}).");

Console.WriteLine("MonoXR Sample — publishing a 3-panel overlay via MonoGameOverlayHost");
Console.WriteLine($"[MonoXR] Layer attached: {host.LayerAttached}");
Console.WriteLine("Keys:  E = toggle edit mode (drag panels with the mouse)   Esc = quit");
Console.WriteLine("       Up/Down = select setting   Left/Right = adjust it");

var clock = Stopwatch.StartNew();
bool running = true;
bool pollKeys = !Console.IsInputRedirected;
while (running)
{
    try
    {
        while (pollKeys && Console.KeyAvailable)
        {
            var key = Console.ReadKey(intercept: true).Key;
            if (key == ConsoleKey.Escape) running = false;
            else if (key == ConsoleKey.E)
            {
                host.EditMode = !host.EditMode;
                Console.WriteLine($"[MonoXR] Edit mode {(host.EditMode ? "ON — drag panels with the mouse" : "off")}.");
            }
            else if (key == ConsoleKey.UpArrow) OverlayNavigation.Raise(OverlayNavAction.Up);
            else if (key == ConsoleKey.DownArrow) OverlayNavigation.Raise(OverlayNavAction.Down);
            else if (key == ConsoleKey.RightArrow) OverlayNavigation.Raise(OverlayNavAction.Increase);
            else if (key == ConsoleKey.LeftArrow) OverlayNavigation.Raise(OverlayNavAction.Decrease);
        }
    }
    catch (InvalidOperationException)
    {
        pollKeys = false; // no interactive console (e.g. output piped) — keep publishing
    }

    // The host paces itself (MaxFrameRate, default 30 fps) and skips all GPU
    // work when no panel is dirty; this loop just needs to call in often.
    host.RenderFrame((float)clock.Elapsed.TotalSeconds);
    Thread.Sleep(4);
}
