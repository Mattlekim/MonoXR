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
| `client/` | `MonoXR.Client` — C# library: `MonoGameOverlayHost` (high level), `OverlayManager`/`Overlay` (low level), `MonoGameInterop` |
| `sample/` | `MonoXR.Sample` — console app publishing a multi-panel overlay via `MonoGameOverlayHost` |

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
- **Attach handling** — `LayerAttached` / `LayerAttachedChanged` report when a
  game with the layer is running; the host republishes automatically on attach
  and self-heals a wedged shared-texture handshake by rebuilding the overlay.
- **Runtime reconfiguration** — `SetCanvasResolution` changes the canvas pixel
  size without touching the VR pose; `DisplaySize`/`Distance`/`Overlay.*`
  adjust the quad in VR at any time (call `Invalidate()` after direct
  `Overlay` pose changes).

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
   1024×1024 overlay with two panels and logs attach/detach; this already
   validates the client, shared textures, and control block with no headset.
3. Start any D3D11 OpenXR app / game. The overlay canvas should appear 2.5 m in
   front of you — an animated panel plus a color-cycling panel. Press `E` in the
   sample's console to toggle edit mode and drag panels with the mouse.
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
