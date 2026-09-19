# MonoXR

Inject **MonoGame render targets into OpenXR as overlays**. Render-target
content is published as an `XrCompositionLayerQuad` in the running VR app, with
programmatic position, orientation, and size — world-locked or head-locked.

This is the same architecture DesktopXR uses (an OpenXR **API layer** that adds
composition layers inside the game's own frame submission), but with an inlet so
*your* textures can be shown. It works across OpenXR runtimes — including SteamVR —
because the layer runs inside the game's OpenXR call chain rather than relying on
the (largely unsupported) `XR_EXTX_overlay` extension.

> **Graphics API:** D3D11 only in this version. The target VR app must use the
> `XR_KHR_D3D11_enable` graphics binding (most OpenXR titles do). D3D12/Vulkan
> hosts are future work.

---

## How it works

```
 Your app (client process)                    VR game (its process)
 ┌──────────────────────────────┐             ┌─────────────────────────────┐
 │ MonoGameOverlayHost          │             │  XR_APILAYER_NOVELTY_monoxr │
 │  panels (OverlayRenderTarget)│             │  (our DLL, in the game)     │
 │   composited on GPU into a   │ shared D3D11│  • opens the shared texture │
 │   canvas RenderTarget2D      │  texture    │  • copies into XrSwapchain  │
 │ Overlay.Update(texturePtr) ──┼──► (named ─►│  • appends XrComposition    │
 │   GPU CopyResource, no CPU   │  NT handle +│    LayerQuad with your pose │
 │   readback                   │ keyed mutex)│                             │
 │ OverlayManager               │             │  hooks xrEndFrame           │
 │   writes control block ──────┼──► Local\MonoXR-Control (shared memory)   │
 └──────────────────────────────┘             └─────────────────────────────┘
```

- **`Local\MonoXR-Control`** — a shared-memory control block with one slot per
  overlay: active/visible flags, pose, size, reference space, texture
  format/size, and the shared-texture name. ABI defined once in
  `shared/monoxr_abi.h` and mirrored in `shared/MonoXrAbi.cs`. The client
  attaches to an existing block if one is already there (e.g. the app was
  restarted while the game kept running), so reconnects just work.
- **Shared textures** — one per overlay, created by the client as a D3D11 shared
  texture (named NT handle + keyed mutex). The client copies frames into it; the
  layer copies it into an OpenXR swapchain image. A keyed mutex hands the
  texture back and forth without tearing; the client's acquire is non-blocking,
  so the game loop never stalls if the layer is busy or absent.
- **Zero-copy publishing** — the client wraps the *game's own* MonoGame D3D11
  device (`MonoGameInterop.GetDevicePointer`), so publishing a frame is a single
  GPU-side `CopyResource` from the render target into the shared texture. No
  frame ever crosses to the CPU. (A CPU byte-upload path still exists for
  non-MonoGame callers: `Overlay.Update(ReadOnlySpan<byte>)`.)
- **Attach/detach tracking** — `OverlayManager.LayerAttached` is true while an
  OpenXR session with the layer is running *and* the game process is alive
  (dead-PID detection catches crashed games). `LayerAttachedChanged` fires on
  transitions, and the host republishes automatically when a game attaches so
  the overlay appears immediately.

## Layout

| Path | What |
|---|---|
| `shared/monoxr_abi.h` / `MonoXrAbi.cs` | The cross-process ABI (keep in sync) |
| `layer/` | Native C++ OpenXR API layer (CMake) → `XR_APILAYER_NOVELTY_monoxr.dll` + `MonoXR.json` |
| `client/` | `MonoXR.Client` — C# library: `MonoGameOverlayHost` (high level), `OverlayManager`/`Overlay` (low level), `MonoGameInterop`, in-VR UI controls (`Controles/`) |
| `sample/` | `MonoXR.Sample` — console app publishing a multi-panel overlay via `MonoGameOverlayHost`, incl. a settings panel using the controls |

## Build

**Prerequisites:** Visual Studio 2022 (MSVC), CMake ≥ 3.20, .NET 8 SDK.

```powershell
# 1. Native layer  (produces layer/build/Release/XR_APILAYER_NOVELTY_monoxr.dll + MonoXR.json)
./build-layer.ps1

# 2. Client + sample
dotnet build MonoXR.sln -c Release
```

## Install the layer

Register it as a per-user implicit OpenXR layer (no admin; loads into every
OpenXR app). The generated manifest points at the absolute DLL path, so re-run
this after rebuilding if the path changes.

```powershell
./install-layer.ps1              # register
./install-layer.ps1 -Uninstall   # remove
```

Prefer to enable it only for one launch? Skip the install and set env vars in the
shell that starts the VR app:

```powershell
$env:XR_API_LAYER_PATH   = "$PWD\layer\build\Release"
$env:XR_ENABLE_API_LAYERS = "XR_APILAYER_NOVELTY_monoxr"
```

### Use the DLL in your own project

1. Your project must target **`net8.0-windows`, x64**, and reference
   **`MonoGame.Framework.WindowsDX`** (the zero-copy path reflects into the
   WindowsDX backend). `MonoGameOverlayHost` also needs
   `<UseWindowsForms>true</UseWindowsForms>` (it creates a hidden window).
2. Unzip the artifact into a folder in your project, e.g. `libs\MonoXR\`.
3. Reference the main DLL and copy the dependency DLLs to your output:

```xml
<ItemGroup>
  <Reference Include="MonoXR.Client">
    <HintPath>libs\MonoXR\MonoXR.Client.dll</HintPath>
  </Reference>
  <None Include="libs\MonoXR\*.dll" Exclude="libs\MonoXR\MonoXR.Client.dll"
        CopyToOutputDirectory="PreserveNewest" Link="%(Filename)%(Extension)" />
</ItemGroup>
```

4. Install the OpenXR layer on the machine (see [Install the layer](#install-the-layer))
   — the DLL only publishes textures; the layer is what draws them in VR.
5. Follow either usage section below — the API is identical whether you
   reference the DLL or the `client/MonoXR.Client.csproj` project directly.

## Use it: MonoGameOverlayHost (recommended)

`MonoGameOverlayHost` runs a headless MonoGame `GraphicsDevice` inside any
desktop app (console, WinForms, WPF — no MonoGame `Game` loop needed) and
publishes **one overlay canvas** composed from any number of *panels*. Each
panel is an `OverlayRenderTarget` subclass with its own pixel position on the
canvas:

```csharp
using MonoXR.Client;

class MyPanel : OverlayRenderTarget
{
    public MyPanel(GraphicsDevice device) : base(device, 512, 256, x: 32, y: 32) { }

    public override void Update(GameTime gameTime)
    {
        // game/animation logic; call Invalidate() when content changed
    }

    public override void Render(GameTime gameTime)
    {
        // draw — this panel's render target is already bound
    }
}

var host = new MonoGameOverlayHost(canvasWidth: 1024, canvasHeight: 1024);
host.DisplaySize = new Vector2(2f, 2f);   // meters in VR
host.Distance    = 2.5f;                  // meters in front of the origin
var panel = host.AddRenderTarget(new MyPanel(host.GraphicsDevice));

// drive it from any loop or timer:
host.RenderFrame((float)stopwatch.Elapsed.TotalSeconds);
```

What the host does for you:

- **GPU compositing** — panels are drawn onto a transparent canvas at their
  `X`/`Y`; uncovered pixels stay transparent in VR (the layer submits with
  source-alpha blending). Publishing is a device-local `CopyResource`.
- **Dirty-flag rendering** — a panel is only re-rendered when its `IsDirty`
  flag is set (call `Invalidate()` when your content changes). When nothing is
  dirty the host skips all GPU work *and* the publish; the layer keeps showing
  the last frame for free.
- **Frame-rate cap** — `MaxFrameRate` (default 30) skips calls that arrive too
  soon, keeping GPU cost away from the game. Set 0 to disable.
- **Edit mode** — set `EditMode = true` to draw a red canvas border, outline
  every panel, and let the user **drag panels with the mouse** — global cursor
  and button state are read, so it works while the VR game has focus, and a
  crosshair cursor is drawn on the canvas so the position is visible in VR.
  `DragCompleted` fires with the panel at its final position (persist it there).
  Each panel's button bar also has **- / 100% / +** size controls and a drag
  grip in its bottom-right corner; they set `OverlayRenderTarget.Scale` (the
  host stretches the panel's texture, so panels need no layout code for it) and
  `ScaleChanged` fires when a resize finishes (persist `Scale` there).
- **Cursor matching** — while a game is attached, the edit cursor is mapped
  from the mouse position inside the *game's window* (found via the process id
  the layer publishes), not the whole monitor. `BeginCursorCalibration()` runs
  a two-click fit (the user puts the game's own in-VR cursor on a marker and
  clicks, twice) so the red cross lands on the game's cursor; persist
  `CursorCalibration` when `CursorCalibrationCompleted` fires and restore it
  with `SetCursorCalibration`.
- **Attach handling** — `LayerAttached` / `LayerAttachedChanged` report when a
  game with the layer is running; the host republishes automatically on attach
  and self-heals a wedged shared-texture handshake by rebuilding the overlay.
- **Runtime reconfiguration** — `SetCanvasResolution` changes the canvas pixel
  size without touching the VR pose; `DisplaySize`/`Distance`/`Overlay.*`
  adjust the quad in VR at any time (call `Invalidate()` after direct
  `Overlay` pose changes).
- **Recentering** — the layer publishes the headset pose every frame;
  `Recenter()` centers the overlay on the headset's current line of sight at
  `Distance`, facing the user (head roll is ignored, so it stays level).
  Persist `OriginPosition`/`OriginYaw`/`OriginPitch` and restore them with
  `SetOrigin`.

## UI controls

`client/Controles/` has a small set of ready-made widgets for building
settings-style panels inside an `OverlayRenderTarget`, styled like a dark WPF
settings window:

| Control | What |
|---|---|
| `MonoXRMenuControl` | Vertical list of controls with one selected item; wraps and skips disabled items |
| `MonoXRSliderControl` | Min/max/step slider with a value bar, thumb, and numeric readout; `ValueChanged` event |
| `MonoXRCheckbox` | WPF-style checkbox (optional three-state) with `Checked`/`Unchecked`/`Click` events |
| `MonoXRLabel` | Section header with a divider line; never a navigation target |
| `MonoXRGroupBox` | Border + header around a single content control (typically a menu) |

Controls are lightweight: they don't own a `SpriteBatch`, font, or textures —
your panel passes its own to `Draw(spriteBatch, font, white)` inside its normal
`Render` pass, and coordinates are local to the panel. Since overlays have no
mouse focus in VR, input is action-based: route Up/Down/Increase/Decrease from
wherever you detect it (global hotkey, gamepad, console key) through the static
`OverlayNavigation.Raise(...)`, and let the menu handle it:

```csharp
var menu = new MonoXRMenuControl();
menu.Add(new MonoXRLabel("Demo settings"));
menu.Add(new MonoXRSliderControl("Volume", 0f, 100f, 60f, step: 5f));
menu.Add(new MonoXRCheckbox("Show pulse panel", isChecked: true));
var group = new MonoXRGroupBox("Settings", menu);

OverlayNavigation.Navigated += a => { menu.HandleNavigation(a); Invalidate(); };

// in the panel's Update:  group.Bounds = ...; group.Update(gameTime);
// in the panel's Render:  _sb.Begin(); group.Draw(_sb, font, white); _sb.End();
```

Up/Down move the selection; Increase/Decrease go to the selected control — a
slider steps by `Step`, a checkbox toggles. Remember to `Invalidate()` the
hosting panel on navigation so the host re-renders it.

Text needs a `SpriteFont`. If your app has no MonoGame content pipeline, see
`sample/RuntimeFont.cs` — it builds one at runtime by rasterizing ASCII glyphs
with GDI+. A complete panel using the controls is in `sample/SettingsPanel.cs`.

## Use it: low-level API

For full control — e.g. several independent quads, head-locked overlays, or a
non-MonoGame renderer — use `OverlayManager`/`Overlay` directly:

```csharp
using MonoXR.Client;
using MonoXR.Shared;

// wrap the game's device for zero-copy GPU publishing…
var mgr = new OverlayManager(MonoGameInterop.GetDevicePointer(GraphicsDevice));
// …or `new OverlayManager()` for a private device + CPU upload path.

var overlay = mgr.CreateOverlay(1024, 512);
overlay.Space    = MonoXrSpace.World;                 // or .Head
overlay.Position = new System.Numerics.Vector3(0, 0, -1.5f);  // meters
overlay.Size     = new System.Numerics.Vector2(1.2f, 0.6f);   // meters

// each frame, after rendering into your RenderTarget2D:
overlay.Update(MonoGameInterop.GetTexturePointer(renderTarget)); // GPU copy
// or: overlay.Update(rgbaBytes);                                // CPU upload
mgr.Heartbeat();
```

`Overlay.Update(IntPtr)` returns `false` when the frame was skipped because the
layer held the shared texture that instant — retry next frame (the host does
this for you).

## Test

1. `./build-layer.ps1` then `./install-layer.ps1`.
2. Run the sample: `dotnet run --project sample -c Release`. It publishes a
   1024×1024 overlay with three panels and logs attach/detach; this already
   validates the client, shared textures, and control block with no headset.
3. Start any D3D11 OpenXR app / game. The overlay canvas should appear 2.5 m in
   front of you — an animated panel, a color-cycling panel, and a settings panel
   built from the UI controls. In the sample's console, press `E` to toggle edit
   mode (drag panels with the mouse) and use the arrow keys to navigate the
   settings panel: Up/Down select, Left/Right adjust (the checkbox shows/hides
   the pulse panel).
4. Layer logs: `%TEMP%\MonoXR\layer.log`.

## Limitations / notes

- **D3D11 host only.** No D3D12/Vulkan/OpenGL game support yet.
- **MonoGame WindowsDX backend** is required for the zero-copy path
  (`MonoGameInterop` reflects into its SharpDX internals). The CPU
  `Update(ReadOnlySpan<byte>)` path works with anything that can hand over
  RGBA8 bytes.
- **Same GPU adapter.** Shared textures open across processes only on the same
  adapter; on multi-GPU systems the client and the game must use the same GPU.
  (Adapter LUID negotiation is a future addition.)
- **Format** is fixed to `R8G8B8A8_UNORM` (matches MonoGame `SurfaceFormat.Color`).
  The runtime must expose that swapchain format; if it only offers the sRGB
  variant, a typeless-copy path would be needed.
- **Anti-cheat friendly by design** — the client never touches the game
  process; it only shares a texture and a control block. Process liveness is
  probed with limited query access and fails open when even that is denied
  (e.g. iRacing).
