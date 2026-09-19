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

        private RenderTarget2D? _previewTarget;
        private int _previewVersion = -1;

        /// <summary>
        /// A copy of the canvas shrunk on the GPU to fit within
        /// <paramref name="maxWidth"/> × <paramref name="maxHeight"/> (aspect
        /// kept, never enlarged) — read THIS back for a desktop preview instead
        /// of <see cref="CanvasTexture"/>: a full-resolution canvas is tens of
        /// megabytes per readback, a window-sized copy is one or two. Only
        /// redrawn when the canvas or the requested size changed. Call from the
        /// thread that calls <see cref="RenderFrame"/>, outside of it; the
        /// returned texture is owned by the host and replaced when the size
        /// changes, so re-fetch it each frame.
        /// </summary>
        public Texture2D GetPreviewTexture(int maxWidth, int maxHeight)
        {
            int cw = CanvasWidth, ch = CanvasHeight;
            float fit = Math.Min(1f, Math.Min(Math.Max(1, maxWidth) / (float)cw, Math.Max(1, maxHeight) / (float)ch));
            int w = Math.Max(1, (int)MathF.Round(cw * fit));
            int h = Math.Max(1, (int)MathF.Round(ch * fit));

            if (_previewTarget == null || _previewTarget.Width != w || _previewTarget.Height != h)
            {
                _previewTarget?.Dispose();
                _previewTarget = new RenderTarget2D(GraphicsDevice, w, h);
                _previewVersion = -1;
            }

            if (_previewVersion != PreviewVersion)
            {
                _previewVersion = PreviewVersion;
                GraphicsDevice.SetRenderTarget(_previewTarget);
                // Opaque: a straight copy of the canvas's (premultiplied) pixels, alpha included.
                _compositor.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.LinearClamp);
                _compositor.Draw(_canvasTarget, new XnaRectangle(0, 0, w, h), XnaColor.White);
                _compositor.End();

                // The crosshair is a separate VR quad, not part of the canvas —
                // draw it onto the preview at a visible size.
                if (_calStep == 0)
                {
                    _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                    DrawCrosshair((int)(_cursorX * fit), (int)(_cursorY * fit), 8, 2);
                    _compositor.End();
                }
                GraphicsDevice.SetRenderTarget(null);
            }
            return _previewTarget;
        }

        /// <summary>
        /// Bumped every time the canvas is recomposed. Lets a desktop preview
        /// skip the CPU readback on frames where dirty-flag rendering did no
        /// GPU work (the canvas content is unchanged).
        /// </summary>
        public int CanvasVersion { get; private set; }

        /// <summary>
        /// Bumped whenever the preview image changes: the canvas was recomposed
        /// OR the cursor moved (the cursor is not part of the canvas). A desktop
        /// preview should key its readback off this rather than <see cref="CanvasVersion"/>.
        /// </summary>
        public int PreviewVersion { get; private set; }

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
        /// Distance in meters from the view origin (see <see cref="SetOrigin"/>;
        /// the world origin until recentered) to the overlay. The quad stays
        /// centered on that origin's look direction.
        /// </summary>
        public float Distance
        {
            get => _distance;
            set { _distance = Math.Max(0.05f, value); ApplyPose(); }
        }

        private float _distance = 3f;

        /// <summary>World-space point the overlay is placed in front of (the user's head when last recentered).</summary>
        public Vector3 OriginPosition { get; private set; }

        /// <summary>Heading of the view origin's look direction: radians about +Y, 0 = facing world -Z.</summary>
        public float OriginYaw { get; private set; }

        /// <summary>Elevation of the view origin's look direction: radians, positive = looking up.</summary>
        public float OriginPitch { get; private set; }

        /// <summary>
        /// Place the overlay relative to a new view origin — e.g. one restored
        /// from settings after an earlier <see cref="Recenter"/>.
        /// </summary>
        public void SetOrigin(Vector3 position, float yaw, float pitch = 0f)
        {
            OriginPosition = position;
            OriginYaw = yaw;
            OriginPitch = pitch;
            ApplyPose();
        }

        /// <summary>
        /// Recenter the overlay on where the headset is looking right now: its
        /// center moves to <see cref="Distance"/> along the headset's current
        /// line of sight (up/down as well as left/right) and it turns to face
        /// the user. Head roll is ignored, so the overlay stays level however
        /// the head is tilted. Returns false when no pose is available (no
        /// OpenXR app running, or the game still has an older layer DLL loaded).
        /// </summary>
        public bool Recenter()
        {
            if (_disposed || !_mgr.TryGetHeadPose(out Vector3 position, out Quaternion rotation))
                return false;

            Vector3 forward = Vector3.Normalize(Vector3.Transform(-Vector3.UnitZ, rotation));
            float pitch = MathF.Asin(Math.Clamp(forward.Y, -1f, 1f));

            // Heading from the horizontal part of the look direction; keep the
            // old one if the user is looking straight up/down (no heading).
            float yaw = OriginYaw;
            if (forward.X * forward.X + forward.Z * forward.Z > 1e-6f)
                yaw = MathF.Atan2(-forward.X, -forward.Z);

            SetOrigin(position, yaw, pitch);
            return true;
        }

        private void ApplyPose()
        {
            // Yaw then pitch, no roll: forward = (-cos p * sin y, sin p, -cos p * cos y).
            var rotation = Quaternion.CreateFromYawPitchRoll(OriginYaw, OriginPitch, 0f);
            _overlay.Rotation = rotation;
            _overlay.Position = OriginPosition + Vector3.Transform(new Vector3(0f, 0f, -_distance), rotation);
            _canvasDirty = true;
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
                if (!value) _selected = null;
                EditModeChanged?.Invoke(value);
                // Let targets react (e.g. draw a placeholder while edited).
                foreach (var rt in _targets)
                {
                    rt.EditMode = value;
                    rt.Invalidate();
                }
            }
        }

        /// <summary>
        /// Raised whenever <see cref="EditMode"/> changes — including from the
        /// in-VR edit toggle in the canvas's top-right corner — so a desktop UI
        /// can keep its own edit button in sync.
        /// </summary>
        public event Action<bool>? EditModeChanged;

        /// <summary>
        /// How long the "enter edit mode" icon stays up after the mouse last
        /// moved (outside edit mode). In edit mode the "done" icon is always shown.
        /// </summary>
        public TimeSpan EditToggleHintDuration { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>Pixel thickness of the edit-mode border.</summary>
        public int EditBorderThickness { get; set; } = 6;

        // ----- Edit-mode mouse dragging ------------------------------------
        // GetCursorPos/GetAsyncKeyState read global input state, so this works
        // while the game (e.g. iRacing) has focus. While a game is attached
        // the mouse position inside the GAME'S WINDOW is mapped onto the canvas
        // (that is the rectangle the game maps onto its own in-VR cursor), and
        // a two-click calibration (BeginCursorCalibration) fits that mapping so
        // the red cross lands exactly on the game's cursor. With no game
        // running the primary monitor is mapped proportionally instead. The
        // cursor is drawn on the canvas so the mapped position is visible in
        // VR. Clicks are NOT swallowed — they still reach the focused game.
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetCursorPos(out NativePoint lpPoint);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out NativeRect rect);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref NativePoint point);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativeRect { public int Left, Top, Right, Bottom; }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NativePoint { public int X; public int Y; }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int VK_LBUTTON = 0x01;
        private const int VK_RBUTTON = 0x02;

        // ----- Edit-mode per-target buttons ---------------------------------
        // Clicking a target in edit mode selects it (clicking empty canvas
        // clears the selection). Only the selected target gets its button
        // bar — [Collapse|Expand] [Hide|Show], the opacity controls [-]
        // [Opacity 100%] [+] (the percentage resets when clicked) and the
        // optional Bind button — and its resize grip in the bottom-right
        // corner (drag it to scale the target). The bar sits just above the
        // target (or just inside it when the target touches the canvas top).
        // Hidden targets are drawn as a faint ghost at the very back of the
        // canvas; clicking a ghost selects it so Show stays reachable.
        private const int EditBtnWidth = 130;
        private const int EditBtnHeight = 40;
        private const int EditBtnGap = 8;
        private const int EditStepBtnWidth = 48;   // [-] and [+]
        private const int EditOpacityBtnWidth = 150; // "Opacity 100%"
        private const float EditOpacityStep = 0.1f;
        private const int EditBindBtnWidth = 210;    // "Bind: B5+B12"
        private const int EditShowBtnWidth = 230;    // "Show: In car"
        private const int ResizeGripSize = 30;

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

        /// <summary>
        /// Optional right-button source, polled like
        /// <see cref="LeftButtonOverride"/>. The right button only opens a
        /// target's own context menu (<see cref="OverlayRenderTarget.OnPointerRightPress"/>)
        /// and never moves or resizes anything, so edit mode ignores it.
        /// </summary>
        public Func<bool?>? RightButtonOverride { get; set; }

        private int _cursorX, _cursorY;
        private bool _lmbWasDown;
        private bool _rmbWasDown;
        private OverlayRenderTarget? _dragTarget;
        private int _dragOffsetX, _dragOffsetY;
        private OverlayRenderTarget? _editCapture; // target that claimed the gesture via OnEditPress
        private OverlayRenderTarget? _pointerCapture; // target that claimed a normal-use press via OnPointerPress
        private OverlayRenderTarget? _hoverTarget;    // topmost target under the cursor (outside edit mode)

        // The red crosshair is its own small quad (topmost slot) that is only
        // re-posed as the mouse moves: the canvas is frame-capped and copying
        // it per mouse move would be costly, so drawing the cursor into it
        // made the cursor stutter in VR.
        private const int CursorTexSize = 64;
        private Overlay _cursorOverlay = null!;
        private RenderTarget2D _cursorTexture = null!;
        private bool _cursorPublished;

        // In-VR edit toggle (top-right icon): shown while editing, and briefly
        // after the mouse moves otherwise.
        private readonly System.Diagnostics.Stopwatch _inputClock = System.Diagnostics.Stopwatch.StartNew();
        private TimeSpan _lastCursorMove = TimeSpan.MinValue;
        private bool _editToggleShown; // as last composed

        // Also stays up while the pointer rests on it, so it can't vanish
        // under the cursor before the click.
        private bool EditToggleVisible =>
            EditMode || (_lastCursorMove != TimeSpan.MinValue &&
                         (_inputClock.Elapsed - _lastCursorMove < EditToggleHintDuration ||
                          GetEditToggleRect().Contains(_cursorX, _cursorY)));

        /// <summary>Canvas rect of the edit toggle icon, sized in meters like the crosshair.</summary>
        private XnaRectangle GetEditToggleRect()
        {
            float pxPerMeter = DisplaySize.X > 0 ? CanvasWidth / DisplaySize.X : 768f;
            int size = Math.Max(48, (int)MathF.Round(0.08f * pxPerMeter));
            int margin = Math.Max(EditBorderThickness + 8, size / 4);
            return new XnaRectangle(CanvasWidth - margin - size, margin, size, size);
        }

        /// <summary>Press on the edit toggle flips edit mode. True when the press was consumed.</summary>
        private bool HandleEditToggleClick(int mx, int my)
        {
            if (!EditToggleVisible || !GetEditToggleRect().Contains(mx, my)) return false;
            EditMode = !EditMode;
            // Restart the hint so the icon doesn't vanish right under the pointer.
            _lastCursorMove = _inputClock.Elapsed;
            return true;
        }

        private OverlayRenderTarget? _resizeTarget; // target whose corner grip is being dragged
        private OverlayRenderTarget? _selected;     // edit-mode selection: shows its buttons + resize grip

        /// <summary>The target selected in edit mode (null when none). Only it shows its buttons and resize grip.</summary>
        public OverlayRenderTarget? SelectedTarget => _selected;

        /// <summary>The render target currently being dragged in edit mode (null when none).</summary>
        public OverlayRenderTarget? DragTarget => _dragTarget;

        /// <summary>
        /// True while a pointer gesture is in flight: a target being dragged or
        /// resized, or one that captured the press itself (e.g. a slider inside
        /// it). Gestures are tracked in CANVAS PIXELS, so a host app must
        /// postpone anything that re-maps them — above all
        /// <see cref="SetCanvasResolution"/> — until this goes false: changing
        /// the mapping mid-gesture slides the content out from under the
        /// pointer, and when the gesture is what drives the resolution (a DPI
        /// or size slider) that feeds back on itself.
        /// </summary>
        public bool IsPointerGestureActive =>
            _dragTarget != null || _resizeTarget != null || _editCapture != null || _pointerCapture != null;

        /// <summary>
        /// Raised when the user finishes resizing a target in edit mode (a
        /// -/+/reset button click, or the corner grip released), with its new
        /// <see cref="OverlayRenderTarget.Scale"/>. Persist it there. Fires from
        /// the thread that calls <see cref="RenderFrame"/>.
        /// </summary>
        public event Action<OverlayRenderTarget>? ScaleChanged;

        /// <summary>
        /// Raised when the user changes a target's
        /// <see cref="OverlayRenderTarget.Opacity"/> with the edit-mode
        /// opacity buttons. Persist it there. Fires from the thread that calls
        /// <see cref="RenderFrame"/>.
        /// </summary>
        public event Action<OverlayRenderTarget>? OpacityChanged;

        /// <summary>
        /// Optional: label and highlight for a per-target "Bind" button at the
        /// end of the edit-mode button bar. When set, the button is shown and
        /// clicking it raises <see cref="BindRequested"/>; the app does the
        /// actual input capture (the host knows nothing about controllers).
        /// Called every composed edit-mode frame.
        /// </summary>
        public Func<OverlayRenderTarget, (string Label, bool Active)>? BindButtonLabel { get; set; }

        /// <summary>Raised when a target's edit-mode Bind button is clicked (see <see cref="BindButtonLabel"/>).</summary>
        public event Action<OverlayRenderTarget>? BindRequested;

        /// <summary>
        /// Optional: label and highlight for a per-target "Show" button at the
        /// end of the edit-mode button bar, for choosing when a panel applies.
        /// The host only draws it — what the choices mean is the app's
        /// business, which it acts on through
        /// <see cref="OverlayRenderTarget.Available"/>. Clicking it raises
        /// <see cref="ShowModeCycleRequested"/>. Called every composed
        /// edit-mode frame.
        /// </summary>
        public Func<OverlayRenderTarget, (string Label, bool Active)>? ShowButtonLabel { get; set; }

        /// <summary>Raised when a target's edit-mode Show button is clicked (see <see cref="ShowButtonLabel"/>).</summary>
        public event Action<OverlayRenderTarget>? ShowModeCycleRequested;

        /// <summary>
        /// Force the canvas to be re-composited and republished on the next
        /// frame. Needed after hiding a target from code outside edit mode:
        /// hidden targets don't count towards the dirty check, so the old
        /// frame (still showing it) would otherwise stay up.
        /// </summary>
        public void InvalidateCanvas() => _canvasDirty = true;

        // Canvas point -> target-local pixels (the target's texture is drawn stretched by Scale).
        private static int ToLocalX(OverlayRenderTarget rt, int canvasX) => (int)((canvasX - rt.X) / rt.Scale);
        private static int ToLocalY(OverlayRenderTarget rt, int canvasY) => (int)((canvasY - rt.Y) / rt.Scale);

        // Keep a target on the canvas after its displayed size changed.
        private void ClampToCanvas(OverlayRenderTarget rt)
        {
            rt.X = Math.Clamp(rt.X, 0, Math.Max(0, CanvasWidth - rt.DisplayWidth));
            rt.Y = Math.Clamp(rt.Y, 0, Math.Max(0, CanvasHeight - rt.DisplayHeight));
        }

        /// <summary>
        /// Raised when an edit-mode drag ends (mouse released or edit mode
        /// turned off), with the target at its final position. Fires from the
        /// thread that calls <see cref="RenderFrame"/>.
        /// </summary>
        public event Action<OverlayRenderTarget>? DragCompleted;

        // ----- Game-window cursor source + calibration ----------------------
        private IntPtr _gameWindow;
        private uint _gameWindowPid;
        private int _nextGameWindowSearch;

        /// <summary>The attached game's main window (largest visible top-level window of its process), or zero.</summary>
        private IntPtr FindGameWindow()
        {
            uint pid = _mgr.LayerProcessId;
            if (pid == 0) { _gameWindow = IntPtr.Zero; _gameWindowPid = 0; return IntPtr.Zero; }
            if (pid == _gameWindowPid && _gameWindow != IntPtr.Zero && IsWindow(_gameWindow))
                return _gameWindow;

            // Not found yet (or the window went away): look again at most once a second.
            int now = Environment.TickCount;
            if (pid == _gameWindowPid && now - _nextGameWindowSearch < 0) return IntPtr.Zero;
            _gameWindowPid = pid;
            _nextGameWindowSearch = now + 1000;

            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint owner);
                if (owner != pid || !GetClientRect(hWnd, out var rc)) return true;
                long area = (long)(rc.Right - rc.Left) * (rc.Bottom - rc.Top);
                if (area > bestArea) { bestArea = area; best = hWnd; }
                return true;
            }, IntPtr.Zero);
            _gameWindow = best;
            return best;
        }

        /// <summary>
        /// Mouse position as a 0..1 fraction of the game window's client area
        /// (clamped — the game's own cursor stops at its window edge too).
        /// False when no game window is available.
        /// </summary>
        private bool TryGetGameCursor(out float u, out float v)
        {
            u = v = 0f;
            IntPtr hWnd = FindGameWindow();
            if (hWnd == IntPtr.Zero || !GetCursorPos(out var p)) return false;
            if (!GetClientRect(hWnd, out var rc) || !ScreenToClient(hWnd, ref p)) return false;
            int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
            if (w <= 1 || h <= 1) return false; // minimized
            u = Math.Clamp(p.X / (float)(w - 1), 0f, 1f);
            v = Math.Clamp(p.Y / (float)(h - 1), 0f, 1f);
            return true;
        }

        // Calibration: canvas fraction = scale * game-window fraction + offset,
        // per axis. Identity (1, 0) = the game window maps onto the whole canvas.
        private float _calScaleX = 1f, _calOffsetX, _calScaleY = 1f, _calOffsetY;

        /// <summary>True once a cursor calibration has been set (by the user or restored from settings).</summary>
        public bool CursorCalibrated { get; private set; }

        /// <summary>The current game-cursor calibration (persist these four values).</summary>
        public (float ScaleX, float OffsetX, float ScaleY, float OffsetY) CursorCalibration =>
            (_calScaleX, _calOffsetX, _calScaleY, _calOffsetY);

        /// <summary>Restore a calibration saved from <see cref="CursorCalibration"/>.</summary>
        public void SetCursorCalibration(float scaleX, float offsetX, float scaleY, float offsetY)
        {
            if (!float.IsFinite(scaleX + offsetX + scaleY + offsetY) || scaleX == 0f || scaleY == 0f) return;
            _calScaleX = scaleX; _calOffsetX = offsetX;
            _calScaleY = scaleY; _calOffsetY = offsetY;
            CursorCalibrated = true;
            _canvasDirty = true;
        }

        /// <summary>Forget the calibration: the game window maps onto the whole canvas again.</summary>
        public void ResetCursorCalibration()
        {
            _calScaleX = _calScaleY = 1f;
            _calOffsetX = _calOffsetY = 0f;
            CursorCalibrated = false;
            _canvasDirty = true;
        }

        // The two marker positions, as canvas fractions. Far enough apart for a
        // stable fit, far enough from the edges that the game's cursor (which
        // stops at the edge of its own UI plane) can normally still reach them.
        private const float CalMarkA = 0.3f, CalMarkB = 0.7f;
        private int _calStep;            // 0 = idle, 1 = first marker, 2 = second marker
        private bool _calWaitRelease;    // ignore the click that pressed the "calibrate" button
        private float _calU1, _calV1;

        /// <summary>True while the two-click cursor calibration is running.</summary>
        public bool IsCalibratingCursor => _calStep != 0;

        /// <summary>
        /// Raised when a calibration ends: true = a new calibration was applied
        /// (persist <see cref="CursorCalibration"/>), false = cancelled or the
        /// two clicks were unusable. Fires from the thread that calls <see cref="RenderFrame"/>.
        /// </summary>
        public event Action<bool>? CursorCalibrationCompleted;

        /// <summary>
        /// Start the two-click calibration that lines the red edit cursor up
        /// with the game's own in-VR mouse cursor. The panels are hidden and a
        /// marker is shown; the user puts the GAME'S cursor on it and
        /// left-clicks, then again for a second marker. Needs edit mode and an
        /// attached game with a window. Returns false if it could not start.
        /// </summary>
        public bool BeginCursorCalibration()
        {
            if (_disposed || !EditMode || !TryGetGameCursor(out _, out _)) return false;
            _calStep = 1;
            _calWaitRelease = true;
            _dragTarget = null; _editCapture = null; _resizeTarget = null; _selected = null;
            _canvasDirty = true;
            return true;
        }

        /// <summary>Abort a running calibration, keeping the previous one.</summary>
        public void CancelCursorCalibration()
        {
            if (_calStep == 0) return;
            _calStep = 0;
            _canvasDirty = true;
            CursorCalibrationCompleted?.Invoke(false);
        }

        /// <summary>One calibration tick: a left click records where the game's cursor is for the current marker.</summary>
        private void UpdateCursorCalibration()
        {
            bool lmb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            bool click = lmb && !_lmbWasDown && !_calWaitRelease;
            if (!lmb) _calWaitRelease = false;
            _lmbWasDown = lmb;
            if (!click) return;

            if (!TryGetGameCursor(out float u, out float v)) { CancelCursorCalibration(); return; }
            if (_calStep == 1)
            {
                _calU1 = u; _calV1 = v;
                _calStep = 2;
                _canvasDirty = true;
                return;
            }

            // Two (window fraction -> canvas fraction) pairs per axis give the line through them.
            _calStep = 0;
            _canvasDirty = true;
            float du = u - _calU1, dv = v - _calV1;
            if (Math.Abs(du) < 0.02f || Math.Abs(dv) < 0.02f)
            {
                CursorCalibrationCompleted?.Invoke(false); // clicks (almost) on top of each other
                return;
            }
            float sx = (CalMarkB - CalMarkA) / du, sy = (CalMarkB - CalMarkA) / dv;
            SetCursorCalibration(sx, CalMarkA - sx * _calU1, sy, CalMarkA - sy * _calV1);
            CursorCalibrationCompleted?.Invoke(true);
        }

        /// <summary>
        /// Map the mouse onto the canvas into _cursorX/_cursorY (drawn as the
        /// red crosshair in and out of edit mode). False when no position is available.
        /// </summary>
        private bool UpdateCursor()
        {
            int mx, my;
            if (CursorOverride?.Invoke() is { } over)
            {
                mx = Math.Clamp(over.X, 0, CanvasWidth - 1);
                my = Math.Clamp(over.Y, 0, CanvasHeight - 1);
            }
            else if (TryGetGameCursor(out float gu, out float gv))
            {
                // Game attached: its window (not the monitor) is what the game
                // maps onto its in-VR cursor; the calibration fits that to the canvas.
                mx = Math.Clamp((int)((_calScaleX * gu + _calOffsetX) * CanvasWidth), 0, CanvasWidth - 1);
                my = Math.Clamp((int)((_calScaleY * gv + _calOffsetY) * CanvasHeight), 0, CanvasHeight - 1);
            }
            else
            {
                if (!GetCursorPos(out var p)) return false;
                int screenW = Math.Max(1, GetSystemMetrics(SM_CXSCREEN));
                int screenH = Math.Max(1, GetSystemMetrics(SM_CYSCREEN));
                mx = Math.Clamp(p.X * CanvasWidth / screenW, 0, CanvasWidth - 1);
                my = Math.Clamp(p.Y * CanvasHeight / screenH, 0, CanvasHeight - 1);
            }

            if (mx != _cursorX || my != _cursorY)
            {
                _cursorX = mx;
                _cursorY = my;
                // No _canvasDirty: the cursor is a separate quad (UpdateCursorOverlay).
                PreviewVersion++;
                _lastCursorMove = _inputClock.Elapsed;
            }
            return true;
        }

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
                    cap.OnEditRelease(ToLocalX(cap, _cursorX), ToLocalY(cap, _cursorY));
                    _canvasDirty = true;
                }
                if (_resizeTarget != null)
                {
                    var resized = _resizeTarget;
                    _resizeTarget = null;
                    ScaleChanged?.Invoke(resized);
                }
                CancelCursorCalibration();
                // The crosshair is shown outside edit mode too, and panels get
                // normal-use pointer input (e.g. dragging a slider).
                if (UpdateCursor()) UpdatePointerInput();
                return;
            }

            // Edit mode is for arranging panels, not using them: no hover, and
            // no context menus — but keep tracking the right button so letting
            // go of it here can't look like a fresh press afterwards.
            SetHover(null, 0, 0);
            _rmbWasDown = PollRightButton();

            // Entering edit mode ends any normal-use gesture.
            if (_pointerCapture != null)
            {
                var cap = _pointerCapture;
                _pointerCapture = null;
                cap.OnPointerRelease(ToLocalX(cap, _cursorX), ToLocalY(cap, _cursorY));
                _canvasDirty = true;
                _lmbWasDown = false;
            }

            // Calibrating: the user is steering the GAME'S cursor onto a marker,
            // so normal edit input (and the preview-window override) is suspended.
            if (_calStep != 0)
            {
                UpdateCursorCalibration();
                return;
            }

            if (!UpdateCursor()) return;
            int mx = _cursorX, my = _cursorY;

            bool lmb = LeftButtonOverride?.Invoke()
                       ?? ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0);

            if (lmb && !_lmbWasDown)
            {
                // Press: the button bars win over dragging; otherwise pick the
                // topmost (last-added) visible target under the cursor.
                // Collapsed targets only occupy their collapsed bar.
                // The top-right Done icon beats everything.
                if (HandleEditToggleClick(mx, my))
                {
                    _lmbWasDown = lmb;
                    return;
                }
                if (!HandleEditButtonClick(mx, my))
                {
                    var hit = HitTest(mx, my);
                    bool wasSelected = hit != null && hit == _selected;

                    // Select what was clicked (a hidden target's ghost too,
                    // so it can be shown again); empty canvas deselects.
                    var select = hit ?? HitTestHidden(mx, my);
                    if (select != _selected)
                    {
                        _selected = select;
                        _canvasDirty = true;
                    }

                    // The corner grip (selected target only) wins over
                    // everything inside the target; then the target gets first
                    // refusal (e.g. dragging an internal element like a column
                    // header); otherwise the whole target drags as before.
                    if (hit != null && wasSelected && !hit.IsCollapsed && GetResizeGrip(hit).Contains(mx, my))
                    {
                        _resizeTarget = hit;
                        _canvasDirty = true;
                    }
                    else if (hit != null && hit.OnEditPress(ToLocalX(hit, mx), ToLocalY(hit, my)))
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
                cap.OnEditRelease(ToLocalX(cap, mx), ToLocalY(cap, my));
                _canvasDirty = true;
            }
            else if (!lmb && _resizeTarget != null)
            {
                var resized = _resizeTarget;
                _resizeTarget = null;
                _canvasDirty = true;
                ScaleChanged?.Invoke(resized);
            }

            if (lmb && _resizeTarget != null)
            {
                // Uniform scale so the bottom-right corner follows the cursor
                // (whichever axis asks for the larger size drives it); the
                // top-left corner stays put.
                var rt = _resizeTarget;
                float sx = (mx - rt.X) / (float)Math.Max(1, rt.SourceWidth);
                float sy = (my - rt.Y) / (float)Math.Max(1, rt.SourceHeight);
                rt.Scale = Math.Max(sx, sy);
            }
            else if (lmb && _dragTarget != null)
            {
                _dragTarget.X = Math.Clamp(mx - _dragOffsetX, 0, Math.Max(0, CanvasWidth - _dragTarget.DisplayWidth));
                _dragTarget.Y = Math.Clamp(my - _dragOffsetY, 0, Math.Max(0, CanvasHeight - _dragTarget.DisplayHeight));
            }
            else if (lmb && _editCapture != null)
            {
                _editCapture.OnEditDrag(ToLocalX(_editCapture, mx), ToLocalY(_editCapture, my));
            }

            _lmbWasDown = lmb;
        }

        /// <summary>
        /// Outside edit mode: route the pointer to the target under it
        /// (OnPointerPress/Drag/Release) — no dragging, resizing or button bars.
        /// </summary>
        /// <summary>
        /// Tell targets which one the pointer is over, so a clickable one can
        /// highlight what a press would hit. Only the topmost target under the
        /// cursor is told, and only outside edit mode.
        /// </summary>
        private void SetHover(OverlayRenderTarget? rt, int mx, int my)
        {
            if (!ReferenceEquals(rt, _hoverTarget))
            {
                _hoverTarget?.OnPointerExit();
                _hoverTarget = rt;
            }
            rt?.OnPointerMove(ToLocalX(rt, mx), ToLocalY(rt, my));
        }

        private bool PollRightButton() =>
            RightButtonOverride?.Invoke() ?? ((GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0);

        private void UpdatePointerInput()
        {
            int mx = _cursorX, my = _cursorY;
            SetHover(_pointerCapture ?? HitTest(mx, my), mx, my);

            // Right button: press only, straight to the target under it.
            bool rmb = PollRightButton();
            if (rmb && !_rmbWasDown)
            {
                var menuHit = HitTest(mx, my);
                if (menuHit != null && menuHit.OnPointerRightPress(ToLocalX(menuHit, mx), ToLocalY(menuHit, my)))
                    _canvasDirty = true;
            }
            _rmbWasDown = rmb;
            bool lmb = LeftButtonOverride?.Invoke()
                       ?? ((GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0);

            if (lmb && !_lmbWasDown && HandleEditToggleClick(mx, my))
            {
                // Now in edit mode: the edit path takes over from the next poll,
                // and _lmbWasDown = true keeps this held press from counting again.
            }
            else if (lmb && !_lmbWasDown)
            {
                var hit = HitTest(mx, my);
                if (hit != null && hit.OnPointerPress(ToLocalX(hit, mx), ToLocalY(hit, my)))
                {
                    _pointerCapture = hit;
                    _canvasDirty = true;
                }
            }
            else if (!lmb && _pointerCapture != null)
            {
                var cap = _pointerCapture;
                _pointerCapture = null;
                cap.OnPointerRelease(ToLocalX(cap, mx), ToLocalY(cap, my));
                _canvasDirty = true;
            }
            else if (lmb && _pointerCapture != null)
            {
                _pointerCapture.OnPointerDrag(ToLocalX(_pointerCapture, mx), ToLocalY(_pointerCapture, my));
            }

            _lmbWasDown = lmb;
        }

        /// <summary>
        /// Edit-mode button rects for a target, left to right: Collapse, Hide,
        /// then -, opacity%, + for transparency, then Bind (when enabled).
        /// An <see cref="OverlayRenderTarget.EditModeOnly"/> target keeps only
        /// the opacity controls: it is gone the moment edit mode ends, so
        /// collapsing it, hiding it or binding a show/hide button to it has
        /// nothing to act on. Buttons a target doesn't get come back empty.
        /// </summary>
        private (XnaRectangle Collapse, XnaRectangle Hide,
                 XnaRectangle Fainter, XnaRectangle OpacityReset, XnaRectangle Stronger,
                 XnaRectangle Bind, XnaRectangle Show)
            GetEditButtons(OverlayRenderTarget rt)
        {
            int by = rt.Y - EditBtnHeight - 6;
            if (by < 0) by = rt.Y + 6; // target touches the canvas top: bar goes inside
            bool hasState = !rt.EditModeOnly;
            bool hasBind = BindButtonLabel != null && !rt.EditModeOnly;
            bool hasShow = ShowButtonLabel != null && !rt.EditModeOnly;
            int barW = (hasState ? (EditBtnWidth + EditBtnGap) * 2 : 0)
                     + EditStepBtnWidth * 2 + EditOpacityBtnWidth + EditBtnGap * 2
                     + (hasBind ? EditBindBtnWidth + EditBtnGap : 0)
                     + (hasShow ? EditShowBtnWidth + EditBtnGap : 0);
            int bx = Math.Clamp(rt.X, 0, Math.Max(0, CanvasWidth - barW));

            XnaRectangle Next(int width)
            {
                var r = new XnaRectangle(bx, by, width, EditBtnHeight);
                bx += width + EditBtnGap;
                return r;
            }
            // Tuple elements are evaluated left to right, so Next() lays the
            // bar out in the order the buttons are drawn.
            return (hasState ? Next(EditBtnWidth) : XnaRectangle.Empty,
                    hasState ? Next(EditBtnWidth) : XnaRectangle.Empty,
                    Next(EditStepBtnWidth), Next(EditOpacityBtnWidth), Next(EditStepBtnWidth),
                    hasBind ? Next(EditBindBtnWidth) : XnaRectangle.Empty,
                    hasShow ? Next(EditShowBtnWidth) : XnaRectangle.Empty);
        }

        /// <summary>Resize grip: the bottom-right corner square of a target's displayed bounds.</summary>
        private static XnaRectangle GetResizeGrip(OverlayRenderTarget rt)
        {
            int size = Math.Min(ResizeGripSize, Math.Min(rt.DisplayWidth, rt.DisplayHeight));
            return new XnaRectangle(rt.X + rt.DisplayWidth - size, rt.Y + rt.DisplayHeight - size, size, size);
        }

        private void SetOpacityFromButton(OverlayRenderTarget rt, float opacity)
        {
            // Snap to whole 10% steps so repeated clicks land on clean values.
            rt.Opacity = MathF.Round(opacity / EditOpacityStep) * EditOpacityStep;
            _canvasDirty = true;
            OpacityChanged?.Invoke(rt);
        }

        /// <summary>
        /// Handle a click on the selected target's edit buttons (the only
        /// target showing them). Works for a selected hidden target too —
        /// that's how it is shown again. Returns true when a button was hit.
        /// </summary>
        private bool HandleEditButtonClick(int mx, int my)
        {
            var rt = _selected;
            if (rt != null)
            {
                var (collapse, hide, fainter, opacityReset, stronger, bind, show) = GetEditButtons(rt);
                if (!bind.IsEmpty && bind.Contains(mx, my))
                {
                    BindRequested?.Invoke(rt);
                    _canvasDirty = true;
                    return true;
                }
                if (!show.IsEmpty && show.Contains(mx, my))
                {
                    ShowModeCycleRequested?.Invoke(rt);
                    _canvasDirty = true;
                    return true;
                }
                if (!collapse.IsEmpty && collapse.Contains(mx, my))
                {
                    rt.State = rt.IsCollapsed ? OverlayTargetState.Visible : OverlayTargetState.Collapsed;
                    _canvasDirty = true;
                    return true;
                }
                if (!hide.IsEmpty && hide.Contains(mx, my))
                {
                    rt.State = rt.Visible ? OverlayTargetState.Hidden : OverlayTargetState.Visible;
                    _canvasDirty = true;
                    return true;
                }
                if (fainter.Contains(mx, my)) { SetOpacityFromButton(rt, rt.Opacity - EditOpacityStep); return true; }
                if (stronger.Contains(mx, my)) { SetOpacityFromButton(rt, rt.Opacity + EditOpacityStep); return true; }
                if (opacityReset.Contains(mx, my)) { SetOpacityFromButton(rt, 1f); return true; }
            }
            return false;
        }

        /// <summary>Topmost hidden target (drawn as a ghost in edit mode) whose bounds contain the point.</summary>
        private OverlayRenderTarget? HitTestHidden(int mx, int my)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var rt = _targets[i];
                if (rt.Visible) continue;
                if (!rt.IsLive && !EditMode) continue;
                if (mx >= rt.X && mx < rt.X + rt.DisplayWidth && my >= rt.Y && my < rt.Y + rt.DisplayHeight)
                    return rt;
            }
            return null;
        }

        /// <summary>Topmost (last-added) shown target whose displayed bounds contain the point.</summary>
        private OverlayRenderTarget? HitTest(int mx, int my)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var rt = _targets[i];
                if (!rt.Visible) continue;
                if (!rt.IsLive && !EditMode) continue;
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
            _overlay.Size = new Vector2(2.5f, 2.5f);
            _overlay.Visible = true;
            ApplyPose(); // 3m straight ahead of the world origin

            _cursorTexture = new RenderTarget2D(GraphicsDevice, CursorTexSize, CursorTexSize);
            RenderCursorTexture();
            _cursorOverlay = _mgr.CreateOverlay(CursorTexSize, CursorTexSize, topmost: true);
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
            UpdateCursorOverlay();

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
                if (!rt.IsLive && !EditMode) continue;
                rt.Update(gameTime);
            }

            // Dirty-flag rendering: when no target changed and nothing at the
            // canvas level changed (pose, edit mode, resolution), skip all GPU
            // work AND the publish — the layer then keeps re-showing the last
            // frame for free. Heartbeat still runs so attach/detach tracking works.
            bool anyDirty = _canvasDirty || (_calStep == 0 && EditToggleVisible != _editToggleShown);
            foreach (var rt in _targets)
            {
                if (!rt.Visible && !EditMode) continue;
                if (!rt.IsLive && !EditMode) continue;
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
                if (!rt.IsLive && !EditMode) continue;
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
            // Calibrating the cursor: panels are hidden so nothing covers the
            // game's own cursor; only the marker is drawn.
            if (_calStep != 0) DrawCursorCalibration();
            else ComposeTargets();
            GraphicsDevice.SetRenderTarget(null);
            CanvasVersion++;
            PreviewVersion++;

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

        /// <summary>Composite every target (and the edit-mode chrome) onto the bound, cleared canvas.</summary>
        private void ComposeTargets()
        {
            // Linear sampling: identical to point sampling for targets at 100%
            // (integer positions, 1:1 texels) and smooth for resized ones.
            _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
            if (EditMode)
            {
                // Hidden targets: drawn first (behind everything) as a 10%-
                // opacity ghost so they can still be located and shown again.
                foreach (var rt in _targets)
                {
                    if (rt.Visible) continue;
                    var ghostSrc = new XnaRectangle(0, 0, rt.SourceWidth, rt.SourceHeight);
                    _compositor.Draw(rt.Target,
                        new XnaRectangle(rt.X, rt.Y, rt.DisplayWidth, rt.DisplayHeight),
                        ghostSrc, XnaColor.White * 0.1f);
                }
            }
            foreach (var rt in _targets)
            {
                if (!rt.Visible) continue;
                if (!rt.IsLive && !EditMode) continue;
                // Collapsed targets only contribute their top-left name-bar
                // region (the subclass draws the bar there when collapsed).
                // The source region is stretched to the Scale-d display size.
                var src = new XnaRectangle(0, 0, rt.SourceWidth, rt.SourceHeight);
                _compositor.Draw(rt.Target,
                    new XnaRectangle(rt.X, rt.Y, rt.DisplayWidth, rt.DisplayHeight),
                    src, XnaColor.White * rt.Opacity);
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
                // dragged/resized is highlighted, the selection is cyan and
                // hidden ghosts get a dim outline.
                foreach (var rt in _targets)
                {
                    int dw = rt.DisplayWidth, dh = rt.DisplayHeight;
                    bool active = rt == _dragTarget || rt == _resizeTarget;
                    var c = active          ? XnaColor.Lime
                          : rt == _selected ? XnaColor.Cyan
                          : !rt.Visible     ? XnaColor.Gray * 0.5f
                                            : XnaColor.Yellow;
                    _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y, dw, 2), c);
                    _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y + dh - 2, dw, 2), c);
                    _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y, 2, dh), c);
                    _compositor.Draw(_whitePixel, new XnaRectangle(rt.X + dw - 2, rt.Y, 2, dh), c);

                    // Resize grip: diagonal "stair" in the bottom-right corner
                    // (only where dragging it works — the selected target,
                    // shown and expanded).
                    if (rt == _selected && rt.Visible && !rt.IsCollapsed)
                    {
                        var grip = GetResizeGrip(rt);
                        const int bar = 5;
                        for (int i = 0; i * bar < grip.Width; i++)
                        {
                            int len = Math.Min(grip.Width, (i + 1) * bar);
                            _compositor.Draw(_whitePixel,
                                new XnaRectangle(grip.Right - len, grip.Y + i * bar, len, Math.Min(bar, grip.Bottom - (grip.Y + i * bar))), c);
                        }
                    }
                }

                // Button bar for the selected target only (also when it is a
                // hidden ghost, so Show stays reachable): Collapse / Hide,
                // opacity, Bind.
                if (_selected is { } sel)
                {
                    var rt = sel;
                    var (collapse, hide, fainter, opacityReset, stronger, bind, show) = GetEditButtons(rt);
                    if (!collapse.IsEmpty) DrawEditButton(collapse, rt.IsCollapsed ? "Expand" : "Collapse", rt.IsCollapsed);
                    if (!hide.IsEmpty) DrawEditButton(hide, rt.Visible ? "Hide" : "Show", !rt.Visible);
                    DrawEditButton(fainter, "-", false);
                    DrawEditButton(opacityReset, $"Opacity {MathF.Round(rt.Opacity * 100f):0}%", rt.Opacity < 1f);
                    DrawEditButton(stronger, "+", false);
                    if (!bind.IsEmpty && BindButtonLabel != null)
                    {
                        var (label, active) = BindButtonLabel(rt);
                        DrawEditButton(bind, label, active);
                    }
                    if (!show.IsEmpty && ShowButtonLabel != null)
                    {
                        var (label, active) = ShowButtonLabel(rt);
                        DrawEditButton(show, label, active);
                    }
                }

                _compositor.End();
            }

            _editToggleShown = EditToggleVisible;
            if (_editToggleShown)
            {
                _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
                DrawEditToggle(GetEditToggleRect(), EditMode);
                _compositor.End();
            }

            // (The crosshair is not drawn here — see UpdateCursorOverlay.)
        }

        /// <summary>
        /// Place the crosshair quad over the cursor's canvas position, on the
        /// canvas plane. Runs on every RenderFrame call (not frame-capped) and
        /// only rewrites the pose, which the layer reads every VR frame.
        /// </summary>
        private void UpdateCursorOverlay()
        {
            var canvas = _overlay;
            _cursorOverlay.Space = canvas.Space;
            _cursorOverlay.Rotation = canvas.Rotation;
            _cursorOverlay.Size = new Vector2(CursorHalfLengthMeters * 2f, CursorHalfLengthMeters * 2f);
            // Hidden while calibrating (the user lines up the GAME'S cursor then).
            _cursorOverlay.Visible = canvas.Visible && _calStep == 0;

            // Quad-local: centered, +X right, +Y up, in meters.
            float u = (_cursorX + 0.5f) / CanvasWidth, v = (_cursorY + 0.5f) / CanvasHeight;
            var local = new Vector3((u - 0.5f) * canvas.Size.X, (0.5f - v) * canvas.Size.Y, 0f);
            _cursorOverlay.Position = canvas.Position + Vector3.Transform(local, canvas.Rotation);

            if (_cursorPublished)
                _cursorOverlay.UpdatePose();
            else
                _cursorPublished = _cursorOverlay.Update(MonoGameInterop.GetTexturePointer(_cursorTexture));
        }

        /// <summary>Draw the crosshair texture for the cursor quad (once).</summary>
        private void RenderCursorTexture()
        {
            GraphicsDevice.SetRenderTarget(_cursorTexture);
            GraphicsDevice.Clear(XnaColor.Transparent);
            int thick = (int)MathF.Round(CursorTexSize * CursorThicknessMeters / (CursorHalfLengthMeters * 2f));
            _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            DrawCrosshair(CursorTexSize / 2, CursorTexSize / 2, CursorTexSize / 2, Math.Max(2, thick));
            _compositor.End();
            GraphicsDevice.SetRenderTarget(null);
        }

        // Crosshair size: 12 px arm / 3 px line at the default 768 px/m.
        private const float CursorHalfLengthMeters = 12f / 768f;
        private const float CursorThicknessMeters = 3f / 768f;

        /// <summary>Red cross centered on (cx, cy); the compositor must be inside Begin/End.</summary>
        private void DrawCrosshair(int cx, int cy, int half, int thick)
        {
            _compositor.Draw(_whitePixel, new XnaRectangle(cx - half, cy - thick / 2, half * 2, thick), XnaColor.Red);
            _compositor.Draw(_whitePixel, new XnaRectangle(cx - thick / 2, cy - half, thick, half * 2), XnaColor.Red);
        }

        /// <summary>
        /// Calibration screen: a hollow marker at the current calibration point
        /// plus instructions. The marker's centre is left transparent on
        /// purpose — the overlay is composited on top of the game, so an
        /// opaque centre would hide the very cursor the user is lining up.
        /// </summary>
        private void DrawCursorCalibration()
        {
            _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

            int w = _canvasTarget.Width, h = _canvasTarget.Height;
            int t = Math.Max(1, EditBorderThickness);
            _compositor.Draw(_whitePixel, new XnaRectangle(0, 0, w, t), XnaColor.Lime);
            _compositor.Draw(_whitePixel, new XnaRectangle(0, h - t, w, t), XnaColor.Lime);
            _compositor.Draw(_whitePixel, new XnaRectangle(0, t, t, h - 2 * t), XnaColor.Lime);
            _compositor.Draw(_whitePixel, new XnaRectangle(w - t, t, t, h - 2 * t), XnaColor.Lime);

            float f = _calStep == 1 ? CalMarkA : CalMarkB;
            int cx = (int)(f * w), cy = (int)(f * h);

            // Four arms with a gap around the exact point.
            const int gap = 14, arm = 60, thick = 5;
            _compositor.Draw(_whitePixel, new XnaRectangle(cx - gap - arm, cy - thick / 2, arm, thick), XnaColor.Lime);
            _compositor.Draw(_whitePixel, new XnaRectangle(cx + gap, cy - thick / 2, arm, thick), XnaColor.Lime);
            _compositor.Draw(_whitePixel, new XnaRectangle(cx - thick / 2, cy - gap - arm, thick, arm), XnaColor.Lime);
            _compositor.Draw(_whitePixel, new XnaRectangle(cx - thick / 2, cy + gap, thick, arm), XnaColor.Lime);

            if (EditFont != null)
            {
                string line1 = $"Mouse calibration {_calStep} / 2";
                string line2 = "Put the GAME'S mouse cursor on the centre of the green cross, then left-click.";
                // Text goes below the marker for the first point and above it
                // for the second, so it always stays on the canvas.
                int ty = _calStep == 1 ? cy + gap + arm + 16 : cy - gap - arm - 16 - EditFont.LineSpacing * 2;
                DrawCalibrationText(line1, cx, ty, w);
                DrawCalibrationText(line2, cx, ty + EditFont.LineSpacing, w);
            }

            _compositor.End();
        }

        private void DrawCalibrationText(string text, int centerX, int y, int canvasWidth)
        {
            var size = EditFont!.MeasureString(text);
            int x = Math.Clamp(centerX - (int)(size.X / 2), 8, Math.Max(8, canvasWidth - (int)size.X - 8));
            var back = new XnaRectangle(x - 6, y - 2, (int)size.X + 12, (int)size.Y + 4);
            _compositor.Draw(_whitePixel, back, XnaColor.Black * 0.75f);
            _compositor.DrawString(EditFont, text, new XnaVector2(x, y), XnaColor.White);
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
                if (active || label == "+")
                    _compositor.Draw(_whitePixel, new XnaRectangle(cx - 1, cy - 8, 3, 16), XnaColor.White);
            }
        }

        /// <summary>
        /// Top-right edit toggle: a pencil (enter edit mode) or, while editing,
        /// a green check (done). Drawn from primitives, so no font is needed.
        /// </summary>
        private void DrawEditToggle(XnaRectangle r, bool editing)
        {
            int radius = r.Width / 4;
            var bg = editing ? new XnaColor(30, 90, 50) : new XnaColor(40, 40, 60);
            var edge = editing ? XnaColor.LimeGreen : XnaColor.Yellow;
            MonoXRDraw.RoundedRect(_compositor, r, radius, bg * 0.9f);
            MonoXRDraw.RoundedRectOutline(_compositor, r, radius, Math.Max(2, r.Width / 24), edge);

            float s = r.Width;
            float t = Math.Max(3f, s / 12f); // stroke thickness
            var o = new XnaVector2(r.X, r.Y);
            if (editing)
            {
                // Check mark.
                DrawLine(o + new XnaVector2(0.26f * s, 0.52f * s), o + new XnaVector2(0.43f * s, 0.69f * s), t, XnaColor.White);
                DrawLine(o + new XnaVector2(0.43f * s, 0.69f * s), o + new XnaVector2(0.75f * s, 0.32f * s), t, XnaColor.White);
            }
            else
            {
                // Pencil: diagonal body, white tip, and a short base line.
                var tip = o + new XnaVector2(0.27f * s, 0.73f * s);
                var neck = o + new XnaVector2(0.37f * s, 0.63f * s);
                var end = o + new XnaVector2(0.73f * s, 0.27f * s);
                DrawLine(neck, end, t * 1.8f, XnaColor.Gold);
                DrawLine(tip, neck, t * 1.1f, XnaColor.White);
                DrawLine(o + new XnaVector2(0.25f * s, 0.80f * s), o + new XnaVector2(0.55f * s, 0.80f * s), t * 0.6f, XnaColor.White * 0.7f);
            }
        }

        /// <summary>Thick line from a to b (a stretched, rotated pixel); the compositor must be inside Begin/End.</summary>
        private void DrawLine(XnaVector2 a, XnaVector2 b, float thickness, XnaColor color)
        {
            var d = b - a;
            _compositor.Draw(_whitePixel, a, null, color, MathF.Atan2(d.Y, d.X),
                             new XnaVector2(0f, 0.5f), new XnaVector2(d.Length(), thickness),
                             SpriteEffects.None, 0f);
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
            _previewTarget?.Dispose();
            _canvasTarget.Dispose();
            _cursorOverlay.Dispose();
            _cursorTexture.Dispose();
            _overlay.Dispose();
            _mgr.Dispose();
            GraphicsDevice.Dispose();
            _hiddenForm.Dispose();
        }
    }
}
