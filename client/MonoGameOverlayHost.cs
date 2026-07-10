using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.Xna.Framework.Graphics;
using GameTime = Microsoft.Xna.Framework.GameTime;
using MonoXR.Shared;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;
using XnaVector2 = Microsoft.Xna.Framework.Vector2;

namespace MonoXR.Client
{
 

    /// <summary>
    /// Hosts a headless MonoGame GraphicsDevice inside a desktop app and publishes
    /// a single OpenXR overlay (via MonoXR) composed from any number of MonoGame
    /// render targets, each at its own pixel location on the overlay canvas.
    ///
    /// Usage:
    ///   class MyPanel : OverlayRenderTarget { ... override Update/Render ... }
    ///   var host = new MonoGameOverlayHost(1024, 1024);
    ///   var rt = host.AddRenderTarget(new MyPanel(host.GraphicsDevice, 512, 256, x: 32, y: 32));
    ///   ... call host.RenderFrame(totalSeconds) on a timer ...
    /// </summary>
    public sealed class MonoGameOverlayHost : IDisposable
    {
        private readonly System.Windows.Forms.Form _hiddenForm;
        private readonly OverlayManager _mgr;
        private Overlay _overlay;
        private readonly List<OverlayRenderTarget> _targets = new List<OverlayRenderTarget>();
        private RenderTarget2D _canvasTarget;
        private readonly SpriteBatch _compositor;
        private readonly Texture2D _whitePixel;
        private TimeSpan _lastTotal;
        private bool _canvasDirty = true;
        private bool _disposed;

        /// <summary>Shared device for creating textures, SpriteBatches, fonts, etc.</summary>
        public GraphicsDevice GraphicsDevice { get; }

        /// <summary>The published overlay quad — set Position/Rotation/Size/Space/Visible on it.</summary>
        public Overlay Overlay => _overlay;

        /// <summary>Pixel resolution of the overlay canvas (independent of the VR display size).</summary>
        public int CanvasWidth => _canvasTarget.Width;
        public int CanvasHeight => _canvasTarget.Height;

        /// <summary>
        /// The composed overlay canvas, for desktop previews (read it back with
        /// GetData). Do not hold on to the instance — it is replaced by
        /// <see cref="SetCanvasResolution"/>; re-fetch it each frame.
        /// </summary>
        public Texture2D CanvasTexture => _canvasTarget;

        /// <summary>
        /// Bumped every time the canvas is recomposed. Lets a desktop preview
        /// skip the CPU readback on frames where dirty-flag rendering did no
        /// GPU work (the canvas content is unchanged).
        /// </summary>
        public int CanvasVersion { get; private set; }

        /// <summary>
        /// Physical size of the overlay quad in VR, in meters. Independent of
        /// the canvas pixel resolution; the quad pose is its center, so
        /// changing the size keeps it centered on the current position.
        /// </summary>
        public Vector2 DisplaySize
        {
            get => _overlay.Size;
            set { _overlay.Size = value; _canvasDirty = true; }
        }

        /// <summary>
        /// Distance in meters from the headset default position (world origin)
        /// to the overlay. The quad stays centered straight ahead (x = y = 0).
        /// </summary>
        public float Distance
        {
            get => -_overlay.Position.Z;
            set { _overlay.Position = new Vector3(0f, 0f, -Math.Max(0.05f, value)); _canvasDirty = true; }
        }

        /// <summary>True while an OpenXR app with the MonoXR layer is running (and its process is alive).</summary>
        public bool LayerAttached => _mgr.LayerAttached;

        /// <summary>
        /// Raised when the OpenXR game attaches (true) or closes/crashes (false).
        /// Fires from the thread that calls <see cref="RenderFrame"/>; marshal to
        /// the UI thread before touching UI state.
        /// </summary>
        public event Action<bool>? LayerAttachedChanged
        {
            add => _mgr.LayerAttachedChanged += value;
            remove => _mgr.LayerAttachedChanged -= value;
        }

        public ulong FramesPublished { get; private set; }

        /// <summary>
        /// Maximum update/render rate in frames per second. Calls to
        /// <see cref="RenderFrame"/> that arrive sooner than 1/rate after the
        /// last published frame are skipped entirely (no Update, no Render, no
        /// publish), keeping GPU cost away from the game. 0 disables the cap.
        /// </summary>
        public float MaxFrameRate { get; set; } = 30f;

        private bool _editMode;

        /// <summary>When true, a red border is drawn around the overlay canvas edge.</summary>
        public bool EditMode
        {
            get => _editMode;
            set
            {
                if (_editMode == value) return;
                _editMode = value;
                _canvasDirty = true;
                // Let targets react (e.g. draw a placeholder while edited).
                foreach (var rt in _targets)
                {
                    rt.EditMode = value;
                    rt.Invalidate();
                }
            }
        }

        /// <summary>Pixel thickness of the edit-mode border.</summary>
        public int EditBorderThickness { get; set; } = 6;

        // ----- Edit-mode mouse dragging ------------------------------------
        // GetCursorPos/GetAsyncKeyState read global input state, so this works
        // while the game (e.g. iRacing) has focus. The primary monitor is
        // mapped proportionally onto the overlay canvas and a cursor is drawn
        // on the canvas so the mapped position is visible in VR. Clicks are
        // NOT swallowed — they still reach the focused game.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int VK_LBUTTON = 0x01;

        // ----- Edit-mode per-target buttons ---------------------------------
        // Each target gets a small button bar in edit mode: [Collapse|Expand]
        // and [Hide|Show]. The bar sits just above the target (or just inside
        // it when the target touches the canvas top) and keeps working while
        // the target is hidden — hidden targets are drawn as a faint ghost at
        // the very back of the canvas so they can still be found and shown.
        private const int EditBtnWidth = 130;
        private const int EditBtnHeight = 40;
        private const int EditBtnGap = 8;

        /// <summary>
        /// Font for the edit-mode Collapse/Hide button labels. Optional — when
        /// null the buttons are drawn with simple glyphs instead of text.
        /// </summary>
        public SpriteFont? EditFont { get; set; }

        /// <summary>
        /// Optional edit-mode cursor source, in canvas pixels. Polled every
        /// frame before the default primary-monitor mapping; return null to
        /// fall back to that mapping (e.g. when the mouse is outside your
        /// window). Runs on the thread that calls <see cref="RenderFrame"/>.
        /// </summary>
        public Func<(int X, int Y)?>? CursorOverride { get; set; }

        /// <summary>
        /// Optional edit-mode left-button source, polled alongside
        /// <see cref="CursorOverride"/>. Return null to fall back to global
        /// GetAsyncKeyState polling. Useful to feed real window mouse events
        /// (e.g. from a preview window) so fast clicks can't fall between two
        /// polls. Runs on the thread that calls <see cref="RenderFrame"/>.
        /// </summary>
        public Func<bool?>? LeftButtonOverride { get; set; }

        private int _cursorX, _cursorY;
        private bool _lmbWasDown;
        private OverlayRenderTarget? _dragTarget;
        private int _dragOffsetX, _dragOffsetY;
        private OverlayRenderTarget? _editCapture; // target that claimed the gesture via OnEditPress

        /// <summary>The render target currently being dragged in edit mode (null when none).</summary>
        public OverlayRenderTarget? DragTarget => _dragTarget;

        /// <summary>
        /// Raised when an edit-mode drag ends (mouse released or edit mode
        /// turned off), with the target at its final position. Fires from the
        /// thread that calls <see cref="RenderFrame"/>.
        /// </summary>
        public event Action<OverlayRenderTarget>? DragCompleted;

        private void UpdateEditInput()
        {
            if (!EditMode)
            {
                if (_dragTarget != null)
                {
                    var done = _dragTarget;
                    _dragTarget = null;
                    DragCompleted?.Invoke(done);
                }
                if (_editCapture != null)
                {
                    var cap = _editCapture;
                    _editCapture = null;
                    cap.OnEditRelease(_cursorX - cap.X, _cursorY - cap.Y);
                    _canvasDirty = true;
                }
                _lmbWasDown = false;
                return;
            }

            int mx, my;
            if (CursorOverride?.Invoke() is { } over)
            {
                mx = Math.Clamp(over.X, 0, CanvasWidth - 1);
                my = Math.Clamp(over.Y, 0, CanvasHeight - 1);
            }
            else
            {
                if (!GetCursorPos(out var p)) return;
                int screenW = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
                int screenH = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
                mx = Math.Clamp(p.X * CanvasWidth / screenW, 0, CanvasWidth - 1);
                my = Math.Clamp(p.Y * CanvasHeight / screenH, 0, CanvasHeight - 1);
            }

            if (mx != _cursorX || my != _cursorY)
            {
                _cursorX = mx;
                _cursorY = my;
                _canvasDirty = true; // cursor is drawn on the canvas
            }

            bool lmb = LeftButtonOverride?.Invoke()
                       ?? ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0);

            if (lmb && !_lmbWasDown)
            {
                // Press: the button bars win over dragging; otherwise pick the
                // topmost (last-added) visible target under the cursor.
                // Collapsed targets only occupy their collapsed bar.
                if (!HandleEditButtonClick(mx, my))
                {
                    var hit = HitTest(mx, my);
                    // The target gets first refusal (e.g. dragging an internal
                    // element like a column header); otherwise the whole
                    // target drags as before.
                    if (hit != null && hit.OnEditPress(mx - hit.X, my - hit.Y))
                    {
                        _editCapture = hit;
                        _canvasDirty = true;
                    }
                    else if (hit != null)
                    {
                        _dragTarget = hit;
                        _dragOffsetX = mx - hit.X;
                        _dragOffsetY = my - hit.Y;
                    }
                }
            }
            else if (!lmb && _dragTarget != null)
            {
                var done = _dragTarget;
                _dragTarget = null;
                DragCompleted?.Invoke(done);
            }
            else if (!lmb && _editCapture != null)
            {
                var cap = _editCapture;
                _editCapture = null;
                cap.OnEditRelease(mx - cap.X, my - cap.Y);
                _canvasDirty = true;
            }

            if (lmb && _dragTarget != null)
            {
                _dragTarget.X = Math.Clamp(mx - _dragOffsetX, 0, Math.Max(0, CanvasWidth - _dragTarget.DisplayWidth));
                _dragTarget.Y = Math.Clamp(my - _dragOffsetY, 0, Math.Max(0, CanvasHeight - _dragTarget.DisplayHeight));
            }
            else if (lmb && _editCapture != null)
            {
                _editCapture.OnEditDrag(mx - _editCapture.X, my - _editCapture.Y);
            }

            _lmbWasDown = lmb;
        }

        /// <summary>Both edit-mode button rects for a target (Collapse then Hide).</summary>
        private (XnaRectangle Collapse, XnaRectangle Hide) GetEditButtons(OverlayRenderTarget rt)
        {
            int by = rt.Y - EditBtnHeight - 6;
            if (by < 0) by = rt.Y + 6; // target touches the canvas top: bar goes inside
            int barW = EditBtnWidth * 2 + EditBtnGap;
            int bx = Math.Clamp(rt.X, 0, Math.Max(0, CanvasWidth - barW));
            return (new XnaRectangle(bx, by, EditBtnWidth, EditBtnHeight),
                    new XnaRectangle(bx + EditBtnWidth + EditBtnGap, by, EditBtnWidth, EditBtnHeight));
        }

        /// <summary>
        /// Toggle Collapse/Hide if the click landed on a target's edit button
        /// (topmost target first). Works for hidden targets too — that's how
        /// they are shown again. Returns true when a button was hit.
        /// </summary>
        private bool HandleEditButtonClick(int mx, int my)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var rt = _targets[i];
                var (collapse, hide) = GetEditButtons(rt);
                if (collapse.Contains(mx, my))
                {
                    rt.State = rt.IsCollapsed ? OverlayTargetState.Visible : OverlayTargetState.Collapsed;
                    _canvasDirty = true;
                    return true;
                }
                if (hide.Contains(mx, my))
                {
                    rt.State = rt.Visible ? OverlayTargetState.Hidden : OverlayTargetState.Visible;
                    _canvasDirty = true;
                    return true;
                }
            }
            return false;
        }

        /// <summary>Topmost (last-added) shown target whose displayed bounds contain the point.</summary>
        private OverlayRenderTarget? HitTest(int mx, int my)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var rt = _targets[i];
                if (!rt.Visible) continue;
                if (mx >= rt.X && mx < rt.X + rt.DisplayWidth && my >= rt.Y && my < rt.Y + rt.DisplayHeight)
                    return rt;
            }
            return null;
        }

        /// <summary>
        /// Force the next frame to composite and publish even if no target is
        /// dirty — needed after changing the overlay pose directly through
        /// <see cref="Overlay"/> (the pose is only pushed to VR on publish).
        /// </summary>
        public void Invalidate() => _canvasDirty = true;

        public MonoGameOverlayHost(int canvasWidth, int canvasHeight)
        {
            // MonoGame needs a real HWND for its swap chain; a hidden WinForms
            // window (never shown) satisfies it. All output goes to render
            // targets, nothing is ever presented to this window.
            _hiddenForm = new System.Windows.Forms.Form { ShowInTaskbar = false };
            var pp = new PresentationParameters
            {
                DeviceWindowHandle = _hiddenForm.Handle,
                BackBufferWidth = Math.Max(1, canvasWidth),
                BackBufferHeight = Math.Max(1, canvasHeight),
                IsFullScreen = false,
            };
            GraphicsDevice = new GraphicsDevice(GraphicsAdapter.DefaultAdapter, GraphicsProfile.HiDef, pp);

            // GPU-side canvas the targets are composited into; published to the
            // overlay with a device-local CopyResource (both are R8G8B8A8_UNORM),
            // so no frame ever crosses to the CPU.
            _canvasTarget = new RenderTarget2D(GraphicsDevice, canvasWidth, canvasHeight);
            _compositor = new SpriteBatch(GraphicsDevice);
            _whitePixel = new Texture2D(GraphicsDevice, 1, 1);
            _whitePixel.SetData(new[] { XnaColor.White });

            _mgr = new OverlayManager(MonoGameInterop.GetDevicePointer(GraphicsDevice));

            // With dirty-flag rendering the client goes idle after the last
            // change, but a freshly attached layer (game start/restart) has no
            // content yet and shows nothing until the next publish. Republish
            // on every attach so the overlay appears immediately.
            _mgr.LayerAttachedChanged += attached => { if (attached) _canvasDirty = true; };

            _overlay = _mgr.CreateOverlay(canvasWidth, canvasHeight);
            _overlay.Space = MonoXrSpace.World;
            _overlay.Position = new Vector3(0f, 0f, -3f);
            _overlay.Size = new Vector2(2.5f, 2.5f);
            _overlay.Visible = true;
        }

        /// <summary>
        /// Register an <see cref="OverlayRenderTarget"/> subclass instance so its
        /// Update/Render run each frame and it is composited onto the canvas.
        /// Returns the same instance for convenience.
        /// </summary>
        public T AddRenderTarget<T>(T renderTarget) where T : OverlayRenderTarget
        {
            renderTarget.EditMode = _editMode;
            _targets.Add(renderTarget);
            return renderTarget;
        }

        /// <summary>
        /// Change the pixel resolution of the overlay canvas without touching
        /// its VR pose or size. Recreates the canvas render target and the
        /// shared overlay texture; the layer picks up the new swapchain
        /// automatically. Render targets keep their pixel positions/sizes.
        /// </summary>
        public void SetCanvasResolution(int width, int height)
        {
            if (_disposed) return;
            width = Math.Max(16, width);
            height = Math.Max(16, height);
            if (width == CanvasWidth && height == CanvasHeight) return;

            var old = _overlay;
            var newOverlay = _mgr.CreateOverlay(width, height);
            newOverlay.Space = old.Space;
            newOverlay.Position = old.Position;
            newOverlay.Rotation = old.Rotation;
            newOverlay.Size = old.Size;
            newOverlay.ZOrder = old.ZOrder;
            newOverlay.Visible = old.Visible;
            old.Dispose();
            _overlay = newOverlay;

            _canvasTarget.Dispose();
            _canvasTarget = new RenderTarget2D(GraphicsDevice, width, height);
            foreach (var rt in _targets) rt.Invalidate();
            _canvasDirty = true;
        }

        /// <summary>
        /// Run every registered target's Update then Render, composite all
        /// targets into the overlay canvas at their locations, and publish the
        /// frame.
        /// </summary>
        public void RenderFrame(float totalSeconds)
        {
            if (_disposed) return;

            var total = TimeSpan.FromSeconds(totalSeconds);

            // Sample the mouse and apply edit-mode dragging on EVERY call,
            // before the frame-rate cap — so callers can tick faster than
            // MaxFrameRate for responsive input (clicks would otherwise fall
            // between samples on capped ticks). Changes just mark things
            // dirty; the actual render still happens at the capped rate.
            UpdateEditInput();

            // Frame-rate cap: skip the whole frame if it is too soon. The
            // skipped time is not lost — the next processed frame's delta
            // covers it, because _lastTotal only advances on processed frames.
            if (MaxFrameRate > 0 && FramesPublished > 0 &&
                total - _lastTotal < TimeSpan.FromSeconds(1.0 / MaxFrameRate))
                return;

            var gameTime = new GameTime(total, total - _lastTotal);
            _lastTotal = total;

            // Hidden targets are completely dormant outside edit mode: no
            // Update, no Render, and their dirty flag can't wake the canvas.
            // In edit mode they stay live so the 10% ghost reflects reality.
            foreach (var rt in _targets)
            {
                if (!rt.Visible && !EditMode) continue;
                rt.Update(gameTime);
            }

            // Dirty-flag rendering: when no target changed and nothing at the
            // canvas level changed (pose, edit mode, resolution), skip all GPU
            // work AND the publish — the layer then keeps re-showing the last
            // frame for free. Heartbeat still runs so attach/detach tracking works.
            bool anyDirty = _canvasDirty;
            foreach (var rt in _targets)
            {
                if (!rt.Visible && !EditMode) continue;
                anyDirty |= rt.IsDirty;
            }
            if (!anyDirty)
            {
                _mgr.Heartbeat();
                return;
            }

            // Only re-render targets that actually changed; clean targets keep
            // their existing texture and are just re-composited. A dormant
            // hidden target keeps its dirty flag, so it re-renders as soon as
            // it is shown (or edit mode exposes its ghost).
            foreach (var rt in _targets)
            {
                if (!rt.IsDirty) continue;
                if (!rt.Visible && !EditMode) continue;
                GraphicsDevice.SetRenderTarget(rt.Target);
                rt.Render(gameTime);
                rt.ClearDirty();
            }

            // Compose on the GPU: transparent canvas, then each visible target
            // drawn at (X, Y). Targets render with SpriteBatch's default
            // (premultiplied) AlphaBlend onto a transparent clear, so the same
            // blend here composites them correctly: a target's transparent
            // pixels leave whatever is underneath intact (Opaque would punch
            // holes through lower targets wherever rects overlap — rounded
            // corners, or a target that hides by drawing nothing), uncovered
            // canvas stays fully transparent in VR, and covered areas get the
            // target's own alpha (the layer submits with
            // BLEND_TEXTURE_SOURCE_ALPHA).
            GraphicsDevice.SetRenderTarget(_canvasTarget);
            GraphicsDevice.Clear(XnaColor.Transparent);
            _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            if (EditMode)
            {
                // Hidden targets: drawn first (behind everything) as a 10%-
                // opacity ghost so they can still be located and shown again.
                foreach (var rt in _targets)
                {
                    if (rt.Visible) continue;
                    var ghostSrc = new XnaRectangle(0, 0, rt.DisplayWidth, rt.DisplayHeight);
                    _compositor.Draw(rt.Target,
                        new XnaRectangle(rt.X, rt.Y, rt.DisplayWidth, rt.DisplayHeight),
                        ghostSrc, XnaColor.White * 0.1f);
                }
            }
            foreach (var rt in _targets)
            {
                if (!rt.Visible) continue;
                // Collapsed targets only contribute their top-left name-bar
                // region (the subclass draws the bar there when collapsed).
                var src = new XnaRectangle(0, 0, rt.DisplayWidth, rt.DisplayHeight);
                _compositor.Draw(rt.Target,
                    new XnaRectangle(rt.X, rt.Y, rt.DisplayWidth, rt.DisplayHeight),
                    src, XnaColor.White);
            }
            _compositor.End();
            if (EditMode)
            {
                // Separate alpha-blended batch: button label glyphs need real
                // blending, which BlendState.Opaque would turn into holes.
                _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

                int t = Math.Max(1, EditBorderThickness);
                int w = _canvasTarget.Width, h = _canvasTarget.Height;
                _compositor.Draw(_whitePixel, new XnaRectangle(0, 0, w, t), XnaColor.Red);
                _compositor.Draw(_whitePixel, new XnaRectangle(0, h - t, w, t), XnaColor.Red);
                _compositor.Draw(_whitePixel, new XnaRectangle(0, t, t, h - 2 * t), XnaColor.Red);
                _compositor.Draw(_whitePixel, new XnaRectangle(w - t, t, t, h - 2 * t), XnaColor.Red);

                // Outline each target so its bounds are obvious; the one being
                // dragged is highlighted, hidden ghosts get a dim outline.
                foreach (var rt in _targets)
                {
                    int dw = rt.DisplayWidth, dh = rt.DisplayHeight;
                    var c = rt == _dragTarget ? XnaColor.Lime
                          : !rt.Visible       ? XnaColor.Gray * 0.5f
                                              : XnaColor.Yellow;
                    _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y, dw, 2), c);
                    _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y + dh - 2, dw, 2), c);
                    _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y, 2, dh), c);
                    _compositor.Draw(_whitePixel, new XnaRectangle(rt.X + dw - 2, rt.Y, 2, dh), c);
                }

                // Per-target Collapse/Hide button bars (also on hidden ghosts,
                // so Show remains reachable).
                foreach (var rt in _targets)
                {
                    var (collapse, hide) = GetEditButtons(rt);
                    DrawEditButton(collapse, rt.IsCollapsed ? "Expand" : "Collapse", rt.IsCollapsed);
                    DrawEditButton(hide, rt.Visible ? "Hide" : "Show", !rt.Visible);
                }

                // Crosshair cursor at the mapped mouse position, visible in VR.
                _compositor.Draw(_whitePixel, new XnaRectangle(_cursorX - 12, _cursorY - 1, 24, 3), XnaColor.Red);
                _compositor.Draw(_whitePixel, new XnaRectangle(_cursorX - 1, _cursorY - 12, 3, 24), XnaColor.Red);

                _compositor.End();
            }
            GraphicsDevice.SetRenderTarget(null);
            CanvasVersion++;

            // Publish with a device-local GPU copy — no readback, no upload.
            // If the layer held the shared texture this instant (publish
            // skipped), stay dirty so the next frame retries — otherwise the
            // last change would never reach VR.
            bool published = _overlay.Update(MonoGameInterop.GetTexturePointer(_canvasTarget));
            _mgr.Heartbeat();
            if (published)
            {
                FramesPublished++;
                _canvasDirty = false;
                _publishFailStreak = 0;
            }
            else
            {
                _canvasDirty = true;

                // Self-heal: a publish can fail transiently (layer holds the
                // keyed mutex this instant), but ~3s of consecutive failures
                // while a layer is attached means the mutex handshake is
                // wedged (e.g. after a client reconnect). Rebuild the overlay
                // — new shared texture, fresh mutex — to restart it.
                if (_mgr.LayerAttached && ++_publishFailStreak >= 90)
                {
                    _publishFailStreak = 0;
                    RecreateOverlay();
                }
            }
        }

        /// <summary>
        /// Draw one edit-mode button (must be inside an alpha-blend batch).
        /// <paramref name="active"/> highlights the button when its toggle is
        /// engaged (collapsed / hidden).
        /// </summary>
        private void DrawEditButton(XnaRectangle r, string label, bool active)
        {
            var bg = active ? new XnaColor(90, 60, 140) : new XnaColor(40, 40, 60);
            var edge = active ? XnaColor.MediumPurple : XnaColor.Yellow;
            int radius = r.Height / 2;
            MonoXRDraw.RoundedRect(_compositor, r, radius, bg);
            MonoXRDraw.RoundedRectOutline(_compositor, r, radius, 2, edge);

            if (EditFont != null)
            {
                var size = EditFont.MeasureString(label);
                var pos = new XnaVector2(
                    r.X + (int)((r.Width - size.X) / 2),
                    r.Y + (int)((r.Height - size.Y) / 2));
                _compositor.DrawString(EditFont, label, pos, XnaColor.White);
            }
            else
            {
                // No font provided: minimal glyph — a minus/plus stand-in so
                // the buttons are at least distinguishable.
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                _compositor.Draw(_whitePixel, new XnaRectangle(cx - 8, cy - 1, 16, 3), XnaColor.White);
                if (active)
                    _compositor.Draw(_whitePixel, new XnaRectangle(cx - 1, cy - 8, 3, 16), XnaColor.White);
            }
        }

        private int _publishFailStreak;

        /// <summary>Replace the overlay with an identical one backed by a new shared texture.</summary>
        private void RecreateOverlay()
        {
            var old = _overlay;
            var fresh = _mgr.CreateOverlay(CanvasWidth, CanvasHeight);
            fresh.Space = old.Space;
            fresh.Position = old.Position;
            fresh.Rotation = old.Rotation;
            fresh.Size = old.Size;
            fresh.ZOrder = old.ZOrder;
            fresh.Visible = old.Visible;
            old.Dispose();
            _overlay = fresh;
            _canvasDirty = true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var rt in _targets) rt.Dispose();
            _targets.Clear();
            _whitePixel.Dispose();
            _compositor.Dispose();
            _canvasTarget.Dispose();
            _overlay.Dispose();
            _mgr.Dispose();
            GraphicsDevice.Dispose();
            _hiddenForm.Dispose();
        }
    }
}
