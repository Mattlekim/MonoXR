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

        private SwapChainRenderTarget? _presentedTo;
        private int _presentedVersion = -1;

        /// <summary>
        /// Draw the canvas (letterboxed, with the edit cursor) straight into a
        /// swap chain bound to a window and present it — the desktop preview
        /// path with no CPU readback: the canvas never leaves the GPU, unlike
        /// <see cref="GetPreviewTexture"/> + GetData. Cheap to call every tick:
        /// it only draws when the canvas or cursor changed, the swap chain is a
        /// different one (e.g. recreated after a resize), or
        /// <paramref name="force"/> (the window was exposed). Call from the
        /// thread that calls <see cref="RenderFrame"/>, outside of it. Returns
        /// true when a frame was presented.
        /// </summary>
        /// <param name="target">Swap chain sized to the window's client area.</param>
        public bool PresentPreview(SwapChainRenderTarget target, bool force = false)
        {
            if (_disposed) return false;
            if (!force && ReferenceEquals(_presentedTo, target) && _presentedVersion == PreviewVersion) return false;
            _presentedTo = target;
            _presentedVersion = PreviewVersion;

            int cw = CanvasWidth, ch = CanvasHeight, tw = target.Width, th = target.Height;
            float scale = Math.Min(tw / (float)cw, th / (float)ch);
            int dw = Math.Max(1, (int)MathF.Round(cw * scale));
            int dh = Math.Max(1, (int)MathF.Round(ch * scale));
            int dx = (tw - dw) / 2, dy = (th - dh) / 2;

            GraphicsDevice.SetRenderTarget(target);
            // Dark backing so the canvas's transparent regions read as empty space.
            GraphicsDevice.Clear(new XnaColor(0x0E, 0x0E, 0x1A));

            // The canvas holds premultiplied alpha, same as when it is composed
            // for VR: the default AlphaBlend lays it over the backing correctly.
            _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
            _compositor.Draw(_canvasTarget, new XnaRectangle(dx, dy, dw, dh), XnaColor.White);
            _compositor.End();

            // The crosshair is a separate VR quad, not part of the canvas.
            if (_calStep == 0)
            {
                _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
                DrawCrosshair(dx + (int)(_cursorX * scale), dy + (int)(_cursorY * scale), 8, 2);
                _compositor.End();
            }

            GraphicsDevice.SetRenderTarget(null);
            target.Present();
            return true;
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
            set { _overlay.Size = value; ApplyPose(); }
        }

        /// <summary>
        /// Distance in meters from the view origin (see <see cref="SetOrigin"/>;
        /// the world origin until recentered) to the overlay. The quad stays
        /// centered on that origin's look direction, shifted by
        /// <see cref="OffsetX"/> / <see cref="OffsetY"/>.
        /// </summary>
        public float Distance
        {
            get => _distance;
            set { _distance = Math.Max(0.05f, value); ApplyPose(); }
        }

        private float _distance = 3f;

        /// <summary>
        /// Sideways shift in meters of the overlay from the view origin's look
        /// direction, along the quad's own right axis (positive = to the right).
        /// </summary>
        public float OffsetX
        {
            get => _offsetX;
            set { _offsetX = value; ApplyPose(); }
        }

        /// <summary>Vertical shift in meters along the quad's own up axis (positive = up).</summary>
        public float OffsetY
        {
            get => _offsetY;
            set { _offsetY = value; ApplyPose(); }
        }

        private float _offsetX, _offsetY;

        /// <summary>
        /// Bend the overlay onto a vertical cylinder around the view origin, so
        /// it wraps around the user instead of standing flat. The radius is
        /// <see cref="Distance"/> / <see cref="CurveAmount"/>, <see cref="DisplaySize"/>
        /// is the arc length and height, and <see cref="OffsetX"/> then slides
        /// the overlay around the cylinder (as an arc) rather than sideways.
        /// Needs a runtime with cylinder layers: reads false (flat) while the
        /// layer has reported that the runtime has none, see
        /// <see cref="CurvedSupported"/>.
        /// </summary>
        public bool Curved
        {
            get => _curved && CurvedSupported != false;
            set { _curved = value; ApplyPose(); }
        }

        /// <summary>What was last asked for, whether or not the runtime can show it.</summary>
        public bool CurvedRequested => _curved;

        /// <summary>
        /// Whether the headset runtime can draw curved overlays: null until the
        /// layer has said (no OpenXR app attached yet), so callers should only
        /// refuse <see cref="Curved"/> on an explicit false.
        /// </summary>
        public bool? CurvedSupported => _disposed ? null : _mgr.CylinderLayersSupported;

        private bool _curved;
        private bool _appliedCurved;

        /// <summary>
        /// How strongly a <see cref="Curved"/> overlay bends, 0.1 to 1.5. 1 is
        /// the cylinder around the view origin (every point of the overlay the
        /// same distance away); less is a gentler curve (a wider cylinder), more
        /// a tighter one.
        /// </summary>
        public float CurveAmount
        {
            get => _curveAmount;
            set { _curveAmount = Math.Clamp(value, 0.1f, 1.5f); ApplyPose(); }
        }

        private float _curveAmount = 1f;

        /// <summary>
        /// Split the overlay into three flat screens, like a triple monitor
        /// setup: the canvas is cut into a left, a centre and a right part, each
        /// shown on its own quad. <see cref="DisplaySize"/> is the width of the
        /// three laid side by side, <see cref="TripleCenterPercent"/> how much of
        /// it the centre takes, and the two outer screens are turned in toward
        /// the viewer by <see cref="TripleSideAngle"/>. Takes the place of
        /// <see cref="Curved"/> while on.
        /// </summary>
        public bool Triple
        {
            get => _triple;
            set { _triple = value; ApplyPose(); }
        }

        /// <summary>Width of the centre screen as a percentage of the whole canvas, 20 to 90. The outer screens share the rest equally.</summary>
        public float TripleCenterPercent
        {
            get => _tripleCenter;
            set { _tripleCenter = Math.Clamp(value, 20f, 90f); ApplyPose(); }
        }

        /// <summary>How far the outer screens are turned in toward the viewer, in degrees: 0 keeps all three in one flat line, 90 sets them side-on.</summary>
        public float TripleSideAngle
        {
            get => _tripleAngle;
            set { _tripleAngle = Math.Clamp(value, 0f, 90f); ApplyPose(); }
        }

        /// <summary>
        /// A fourth screen above the centre one (needs <see cref="Triple"/>): the
        /// same width as the centre screen, <see cref="TripleTopHeight"/> tall, and
        /// tipped forward by <see cref="TripleTopAngle"/>. Its picture is an extra
        /// band along the top of the canvas, so it is reached by moving the cursor
        /// up past the other screens; panels already on the canvas move down with
        /// the rest (see <see cref="CanvasExtraHeightMeters"/>).
        /// </summary>
        public bool TripleTop
        {
            get => _tripleTop;
            set { _tripleTop = value; ApplyPose(); }
        }

        /// <summary>Height of the top screen in meters.</summary>
        public float TripleTopHeight
        {
            get => _tripleTopHeight;
            set { _tripleTopHeight = Math.Clamp(value, 0.2f, 4f); ApplyPose(); }
        }

        /// <summary>How far the top screen tips forward, in degrees: 0 is upright, 90 faces straight down.</summary>
        public float TripleTopAngle
        {
            get => _tripleTopAngle;
            set { _tripleTopAngle = Math.Clamp(value, 0f, 90f); ApplyPose(); }
        }

        /// <summary>
        /// Meters of canvas the top screen adds above the normal area: its height
        /// while it is on, otherwise 0. The caller sizes the canvas as
        /// (<see cref="DisplaySize"/>.Y + this) x pixels per meter.
        /// </summary>
        public float CanvasExtraHeightMeters => _triple && _tripleTop ? _tripleTopHeight : 0f;

        private bool _triple, _appliedTriple, _tripleTop;
        private float _tripleCenter = 50f, _tripleAngle = 30f, _tripleTopHeight = 1f, _tripleTopAngle = 30f;
        private Overlay[]? _tripleOverlays; // left, centre, right (and top); only while Triple is on

        // One screen of the triple layout: the canvas columns it shows, its
        // width in meters, and where it sits in the view origin's frame (a yaw
        // about its own up axis; positive turns it toward +X).
        // The canvas rectangle it shows (X0, Y0, Px, Py), its size in meters, and
        // its turn: Yaw about its own up axis (positive toward +X), Pitch about
        // its own right axis (positive tips the top toward the viewer).
        private readonly record struct TripleSegment(int X0, int Y0, int Px, int Py,
                                                     float WidthM, float HeightM, float Yaw, float Pitch, Vector3 Center);

        private static Quaternion SegmentRotation(TripleSegment s) =>
            Quaternion.CreateFromAxisAngle(Vector3.UnitY, s.Yaw) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, s.Pitch);

        // Height in canvas pixels of the band along the top of the canvas that the
        // top screen shows (0 without one): its share of the canvas height.
        private int BandPx()
        {
            float extraM = CanvasExtraHeightMeters;
            if (extraM <= 0f) return 0;
            int h = CanvasHeight;
            float mainM = _overlay.Size.Y;
            int mainPx = Math.Clamp((int)MathF.Round(h * mainM / (mainM + extraM)), 1, Math.Max(1, h - 1));
            return h - mainPx;
        }

        // The band height the panels were last arranged around; they are moved
        // by the difference when the canvas is rebuilt with a different band.
        private int _bandPxApplied;

        // Left, centre, right, then the top screen when there is one.
        private TripleSegment[] TripleLayout()
        {
            Vector2 size = _overlay.Size;
            int w = CanvasWidth, h = CanvasHeight;

            // The top screen's band runs along the top of the canvas; the other
            // three screens show what is below it.
            float extraM = CanvasExtraHeightMeters;
            int topPx = BandPx();
            int mainPx = h - topPx;

            int centerPx = Math.Clamp((int)MathF.Round(w * _tripleCenter / 100f), 1, Math.Max(1, w - 2));
            int leftPx = (w - centerPx) / 2, rightPx = w - centerPx - leftPx;

            float perPx = size.X / w; // every screen shows the canvas at the same scale
            float cm = centerPx * perPx, lm = leftPx * perPx, rm = rightPx * perPx;
            float angle = _tripleAngle * MathF.PI / 180f, sin = MathF.Sin(angle), cos = MathF.Cos(angle);
            float z = -_distance;

            // The centre faces straight ahead. Each outer screen hinges on a
            // centre edge and swings toward the viewer (+Z) by the angle.
            var segs = new List<TripleSegment>(4)
            {
                new TripleSegment(0, topPx, leftPx, mainPx, lm, size.Y, angle, 0f,
                    new Vector3(_offsetX - cm / 2f - lm / 2f * cos, _offsetY, z + lm / 2f * sin)),
                new TripleSegment(leftPx, topPx, centerPx, mainPx, cm, size.Y, 0f, 0f, new Vector3(_offsetX, _offsetY, z)),
                new TripleSegment(leftPx + centerPx, topPx, rightPx, mainPx, rm, size.Y, -angle, 0f,
                    new Vector3(_offsetX + cm / 2f + rm / 2f * cos, _offsetY, z + rm / 2f * sin)),
            };

            // The top screen hinges on the centre screen's top edge and tips
            // forward (its top toward the viewer, so its face looks down).
            if (extraM > 0f && topPx >= 1)
            {
                float pitch = _tripleTopAngle * MathF.PI / 180f;
                var hinge = new Vector3(_offsetX, _offsetY + size.Y / 2f, z);
                segs.Add(new TripleSegment(leftPx, 0, centerPx, topPx, cm, extraM, 0f, pitch,
                    hinge + new Vector3(0f, extraM / 2f * MathF.Cos(pitch), extraM / 2f * MathF.Sin(pitch))));
            }
            return segs.ToArray();
        }

        // Make sure the three overlays exist and match the layout's pixel slices.
        private void EnsureTriple(TripleSegment[] segs)
        {
            if (_tripleOverlays != null)
            {
                bool same = _tripleOverlays.Length == segs.Length;
                for (int i = 0; same && i < segs.Length; i++)
                    same &= _tripleOverlays[i].Width == segs[i].Px && _tripleOverlays[i].Height == segs[i].Py;
                if (same) return;
                DisposeTriple();
            }
            _tripleOverlays = new Overlay[segs.Length];
            for (int i = 0; i < segs.Length; i++) _tripleOverlays[i] = _mgr.CreateOverlay(segs[i].Px, segs[i].Py);
        }

        private void DisposeTriple()
        {
            if (_tripleOverlays == null) return;
            foreach (var o in _tripleOverlays) o.Dispose();
            _tripleOverlays = null;
        }

        // Radius of the cylinder, and where its vertical axis sits: forward of
        // the view origin by (distance - radius), so the surface is always
        // _distance ahead at the overlay's centre line.
        private float CurveRadius => _distance / _curveAmount;

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
            _appliedTriple = _triple;
            if (_appliedTriple)
            {
                // Three flat screens; the single canvas overlay is hidden and
                // not published while they are up.
                _appliedCurved = false;
                var segs = TripleLayout();
                EnsureTriple(segs);
                for (int i = 0; i < segs.Length; i++)
                {
                    var o = _tripleOverlays![i];
                    o.Space = _overlay.Space;
                    o.Rotation = rotation * SegmentRotation(segs[i]);
                    o.Position = OriginPosition + Vector3.Transform(segs[i].Center, rotation);
                    o.Size = new Vector2(segs[i].WidthM, segs[i].HeightM);
                    o.CurveRadius = 0f;
                    o.ZOrder = _overlay.ZOrder;
                    o.Visible = true;
                }
                _overlay.Visible = false;
                _overlay.UpdatePose();
                _canvasDirty = true;
                return;
            }
            DisposeTriple();
            _overlay.Visible = true;
            _appliedCurved = Curved;
            if (_appliedCurved)
            {
                // Slide around the cylinder by the offset's arc length, facing the axis.
                float radius = CurveRadius;
                float angle = _offsetX / radius;
                _overlay.Rotation = rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -angle);
                _overlay.Position = OriginPosition + Vector3.Transform(
                    new Vector3(radius * MathF.Sin(angle), _offsetY, radius - _distance - radius * MathF.Cos(angle)), rotation);
                _overlay.CurveRadius = radius;
            }
            else
            {
                _overlay.Rotation = rotation;
                _overlay.Position = OriginPosition + Vector3.Transform(new Vector3(_offsetX, _offsetY, -_distance), rotation);
                _overlay.CurveRadius = 0f;
            }
            _canvasDirty = true;
        }

        /// <summary>
        /// Latest headset pose in world space, as published by the layer each
        /// frame. False when no OpenXR app is attached, or the running layer
        /// predates head-pose publishing.
        /// </summary>
        public bool TryGetHeadPose(out Vector3 position, out Quaternion rotation)
        {
            position = default;
            rotation = Quaternion.Identity;
            return !_disposed && _mgr.TryGetHeadPose(out position, out rotation);
        }

        /// <summary>
        /// Ask the layer to poll the headset's eye tracker. Needed for
        /// <see cref="TryGetEyeGazeCanvasPoint"/>; leave it off otherwise so
        /// nothing reads the user's eyes while eye gaze is not in use.
        /// </summary>
        public bool EyeGazeRequested
        {
            get => !_disposed && _mgr.EyeGazeRequested;
            set { if (!_disposed) _mgr.EyeGazeRequested = value; }
        }

        /// <summary>
        /// EXPERIMENTAL: filter the head pose the game receives (smoothing, minimum
        /// movement, locked position). Needs a game running with the updated layer.
        /// </summary>
        public HeadTrackingSettings HeadTracking
        {
            get => _headTracking;
            set
            {
                _headTracking = value;
                if (!_disposed) _mgr.SetHeadTracking(value);
            }
        }

        private HeadTrackingSettings _headTracking;

        /// <summary>What the layer reports about eye tracking (see <see cref="MonoXrGazeState"/>).</summary>
        public MonoXrGazeState EyeGazeState => _disposed ? MonoXrGazeState.Unknown : _mgr.EyeGazeState;

        /// <summary>
        /// Where the headset's line of sight crosses the overlay canvas, in
        /// canvas pixels (the same coordinates as the edit cursor and target
        /// positions). The point can fall outside the canvas, which just means
        /// the user is looking past the overlay; false means no pose is
        /// available, or the canvas is behind the head or edge-on to it.
        /// </summary>
        public bool TryGetGazeCanvasPoint(out float x, out float y)
        {
            x = 0f;
            y = 0f;
            return TryGetHeadPose(out Vector3 eye, out Quaternion head) && CanvasPointOfRay(eye, head, out x, out y);
        }

        /// <summary>
        /// Where the user's eyes are looking on the overlay canvas, in the same
        /// canvas pixels as <see cref="TryGetGazeCanvasPoint"/>. False unless
        /// the layer reports a live eye gaze (<see cref="EyeGazeRequested"/>
        /// must be on, and the headset must have working eye tracking).
        /// </summary>
        public bool TryGetEyeGazeCanvasPoint(out float x, out float y)
        {
            x = 0f;
            y = 0f;
            return !_disposed && _mgr.TryGetEyeGaze(out Vector3 eye, out Quaternion gaze) &&
                   CanvasPointOfRay(eye, gaze, out x, out y);
        }

        // Intersect a ray (origin plus the rotation's -Z) with the canvas plane.
        private bool CanvasPointOfRay(Vector3 eye, Quaternion head, out float x, out float y)
        {
            x = 0f;
            y = 0f;

            var size = DisplaySize;
            if (!(size.X > 0f) || !(size.Y > 0f)) return false;

            // The canvas quad, exactly as ApplyPose places it: centered
            // _distance along the origin's look direction, its own +X to the
            // right, +Y up and +Z back out toward the user.
            var quad = Quaternion.CreateFromYawPitchRoll(OriginYaw, OriginPitch, 0f);
            if (_appliedTriple) return CanvasPointOfRayTriple(eye, head, quad, size, out x, out y);
            if (_appliedCurved) return CanvasPointOfRayCurved(eye, head, quad, size, out x, out y);

            Vector3 center = OriginPosition + Vector3.Transform(new Vector3(_offsetX, _offsetY, -_distance), quad);
            Vector3 right = Vector3.Transform(Vector3.UnitX, quad);
            Vector3 up = Vector3.Transform(Vector3.UnitY, quad);
            Vector3 normal = Vector3.Transform(Vector3.UnitZ, quad);

            Vector3 look = Vector3.Transform(-Vector3.UnitZ, head);
            float lookLen = look.Length();
            if (!(lookLen > 1e-4f)) return false;
            look /= lookLen;

            // Ray/plane intersection. A near-zero denominator means the line of
            // sight runs along the canvas plane, so there is no usable hit.
            float denom = Vector3.Dot(look, normal);
            if (MathF.Abs(denom) < 1e-4f) return false;
            float t = Vector3.Dot(center - eye, normal) / denom;
            if (t <= 0f) return false; // canvas is behind the head

            Vector3 local = eye + look * t - center;
            x = (Vector3.Dot(local, right) / size.X + 0.5f) * CanvasWidth;
            y = (0.5f - Vector3.Dot(local, up) / size.Y) * CanvasHeight;
            return float.IsFinite(x) && float.IsFinite(y);
        }

        // The same, for the triple layout: the nearest of the three screens the
        // ray lands inside. A ray that misses all of them (above, below or
        // beside) gets the centre screen's plane extended, so the point still
        // lies outside the canvas.
        private bool CanvasPointOfRayTriple(Vector3 eye, Quaternion head, Quaternion frame, Vector2 size,
                                            out float x, out float y)
        {
            x = 0f;
            y = 0f;

            Vector3 look = Vector3.Transform(-Vector3.UnitZ, head);
            float lookLen = look.Length();
            if (!(lookLen > 1e-4f)) return false;
            look /= lookLen;

            var segs = TripleLayout();
            float bestT = float.MaxValue;
            bool found = false;
            for (int i = 0; i < segs.Length; i++)
            {
                if (!TryHitScreen(segs[i], eye, look, frame, out float t, out float lx, out float ly)) continue;
                bool inside = MathF.Abs(lx) <= segs[i].WidthM / 2f && MathF.Abs(ly) <= segs[i].HeightM / 2f;
                if (inside && t < bestT)
                {
                    bestT = t;
                    found = true;
                    x = segs[i].X0 + (lx / segs[i].WidthM + 0.5f) * segs[i].Px;
                    y = segs[i].Y0 + (0.5f - ly / segs[i].HeightM) * segs[i].Py;
                }
            }
            if (!found)
            {
                if (!TryHitScreen(segs[1], eye, look, frame, out _, out float lx, out float ly)) return false;
                x = segs[1].X0 + (lx / segs[1].WidthM + 0.5f) * segs[1].Px;
                y = segs[1].Y0 + (0.5f - ly / segs[1].HeightM) * segs[1].Py;
            }
            return float.IsFinite(x) && float.IsFinite(y);
        }

        // Ray/plane intersection with one screen: distance along the ray and the
        // hit in the screen's own right/up axes, relative to its centre.
        private bool TryHitScreen(TripleSegment seg, Vector3 eye, Vector3 look, Quaternion frame,
                                  out float t, out float lx, out float ly)
        {
            t = lx = ly = 0f;
            Quaternion rot = frame * SegmentRotation(seg);
            Vector3 center = OriginPosition + Vector3.Transform(seg.Center, frame);
            Vector3 right = Vector3.Transform(Vector3.UnitX, rot);
            Vector3 up = Vector3.Transform(Vector3.UnitY, rot);
            Vector3 normal = Vector3.Transform(Vector3.UnitZ, rot);

            float denom = Vector3.Dot(look, normal);
            if (MathF.Abs(denom) < 1e-4f) return false;
            t = Vector3.Dot(center - eye, normal) / denom;
            if (t <= 0f) return false; // behind the head

            Vector3 local = eye + look * t - center;
            lx = Vector3.Dot(local, right);
            ly = Vector3.Dot(local, up);
            return true;
        }

        // The same, for the curved overlay: intersect the ray with the cylinder
        // (vertical axis in the origin's frame, see CurveRadius) and read the
        // hit as arc length around it and height above its centre line.
        private bool CanvasPointOfRayCurved(Vector3 eye, Quaternion head, Quaternion frame, Vector2 size,
                                            out float x, out float y)
        {
            x = 0f;
            y = 0f;

            var inverse = Quaternion.Inverse(frame);
            Vector3 look = Vector3.Transform(-Vector3.UnitZ, head);
            float lookLen = look.Length();
            if (!(lookLen > 1e-4f)) return false;
            look = Vector3.Transform(look / lookLen, inverse);
            Vector3 p = Vector3.Transform(eye - OriginPosition, inverse);
            float radius = CurveRadius;
            p.Y -= _offsetY;
            p.Z += _distance - radius; // relative to the cylinder's axis

            float a = look.X * look.X + look.Z * look.Z;
            if (a < 1e-6f) return false; // looking straight up or down
            float b = 2f * (p.X * look.X + p.Z * look.Z);
            float c = p.X * p.X + p.Z * p.Z - radius * radius;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return false;

            // From inside the cylinder the far root is the wall in front of the ray.
            float t = (-b + MathF.Sqrt(disc)) / (2f * a);
            if (t <= 0f) return false;

            Vector3 hit = p + look * t;
            float angle = MathF.Atan2(hit.X, -hit.Z) - _offsetX / radius; // from the overlay's centre line
            if (angle > MathF.PI) angle -= 2f * MathF.PI;
            else if (angle < -MathF.PI) angle += 2f * MathF.PI;

            x = (angle * radius / size.X + 0.5f) * CanvasWidth;
            y = (0.5f - hit.Y / size.Y) * CanvasHeight;
            return float.IsFinite(x) && float.IsFinite(y);
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
                if (!value) { _selected = null; _group.Clear(); _marquee = false; _groupDrag = false; _showMenuOpen = false; CloseDocks(); }
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
        // bar — [Collapse|Expand] chevron, [Hide|Show], the opacity controls
        // [-] [opacity icon + 100%] [+] (the percentage resets when clicked), the
        // optional gaze (eye) toggle and the optional Bind button (a gamepad icon, followed by the bound
        // button once there is one) — and its resize grip in the bottom-right
        // corner (drag it to scale the target). The bar sits just above the
        // target (or just inside it when the target touches the canvas top).
        // Hidden targets show in edit mode only as a name pill with a Show button
        // (see GetHiddenPill); clicking the pill selects it, the button shows it.
        private const int EditBtnWidth = 130;
        private const int EditBtnHeight = 40;
        private const int EditBtnGap = 8;
        private const int EditStepBtnWidth = 48;   // [-] and [+]
        private const int EditIconBtnWidth = 64;    // Collapse / Expand chevron, Bind gamepad, gaze eye
        private const int EditOpacityBtnWidth = 130; // opacity icon + "100%"
        private const float EditOpacityStep = 0.1f;
        private const int EditShowChipPad = 28;      // text padding inside a Show dropdown row
        private const int EditShowButtonWidth = 300; // the closed Show dropdown button
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
            // Below the top screen's band when there is one: the corner of the
            // band is not shown on any screen.
            return new XnaRectangle(CanvasWidth - margin - size, BandPx() + margin, size, size);
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

        // Edit-mode multi-selection, made by dragging a box over empty canvas (or
        // Shift+click): two or more targets that move together. Empty while a single
        // target (or none) is selected; then _selected is null, so no button bar.
        private readonly List<OverlayRenderTarget> _group = new();
        private bool _marquee;                       // a selection box is being dragged
        private int _marqueeX0, _marqueeY0;          // where it started (canvas px)
        private bool _groupDrag;                     // the group is being dragged
        private int _groupPressX, _groupPressY;
        private readonly Dictionary<OverlayRenderTarget, (int X, int Y)> _groupStart = new();

        /// <summary>The targets selected together by drag-select or Shift+click (empty unless two or more).</summary>
        public IReadOnlyList<OverlayRenderTarget> SelectedGroup => _group;

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
        /// Optional: a row of toggle chips at the end of the edit-mode button
        /// bar, for choosing when a panel applies (any mix of choices). The host
        /// only draws them — what they mean is the app's business, which it acts
        /// on through <see cref="OverlayRenderTarget.Available"/>. Clicking chip
        /// <c>i</c> raises <see cref="ShowChipClicked"/>. Called every composed
        /// edit-mode frame.
        /// </summary>
        public Func<OverlayRenderTarget, IReadOnlyList<(string Label, bool On)>>? ShowChips { get; set; }

        /// <summary>Raised when chip <c>index</c> of a target's <see cref="ShowChips"/> is clicked.</summary>
        public event Action<OverlayRenderTarget, int>? ShowChipClicked;

        /// <summary>
        /// Optional: adds an eye-icon toggle to the edit-mode button bar for
        /// choosing whether a target reacts to being looked at. Returns whether
        /// it currently does (icon lit) or not (icon struck through). The host
        /// only draws it; clicking raises <see cref="GazeToggleRequested"/> and
        /// the app flips its own setting. Called every composed edit-mode frame.
        /// </summary>
        public Func<OverlayRenderTarget, bool>? GazeButtonState { get; set; }

        /// <summary>Raised when a target's edit-mode gaze (eye) button is clicked (see <see cref="GazeButtonState"/>).</summary>
        public event Action<OverlayRenderTarget>? GazeToggleRequested;

        /// <summary>
        /// Raised after the user changes a target's state in edit mode (Collapse /
        /// Expand, Hide, or Show on a hidden target's pill), so the app can
        /// remember it. Not raised when the app sets <see cref="OverlayRenderTarget.State"/> itself.
        /// </summary>
        public event Action<OverlayRenderTarget>? StateChanged;

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
            _group.Clear(); _marquee = false; _groupDrag = false;
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
                if (_groupDrag) EndGroupDrag();
                _marquee = false;
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

            UpdateArrowNudge();

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
                if (_showMenuOpen && HandleShowMenuClick(mx, my))
                {
                    _lmbWasDown = lmb;
                    return;
                }
                if (HandleDockPress(mx, my))
                {
                    _lmbWasDown = lmb;
                    return;
                }
                if (!HandleHiddenShowClick(mx, my) && !HandleEditButtonClick(mx, my))
                {
                    // A hidden target's pill is drawn above every panel, so it
                    // wins the click over a shown panel underneath it.
                    var pillHit = HitTestHidden(mx, my);
                    var hit = pillHit == null ? HitTest(mx, my) : null;
                    bool wasSelected = hit != null && hit == _selected;

                    // Shift+click adds to / removes from a multi-selection.
                    if (hit != null && KeyDown(VK_SHIFT))
                    {
                        ToggleGroupMember(hit);
                        _canvasDirty = true;
                        _lmbWasDown = lmb;
                        return;
                    }
                    // Pressing on a member of the group drags them all.
                    if (hit != null && _group.Contains(hit))
                    {
                        BeginGroupDrag(mx, my);
                        _lmbWasDown = lmb;
                        return;
                    }
                    if (_group.Count > 0) { _group.Clear(); _canvasDirty = true; }
                    // Empty canvas: the press starts a selection box.
                    if (hit == null && pillHit == null)
                    {
                        _marquee = true;
                        _marqueeX0 = mx;
                        _marqueeY0 = my;
                        _canvasDirty = true;
                    }

                    // Select what was clicked (a hidden target's pill too,
                    // so it can be moved or shown again); empty canvas deselects.
                    var select = hit ?? pillHit;
                    if (select != _selected)
                    {
                        _selected = select;
                        _canvasDirty = true;
                        // Selecting a target raises it above the others: the
                        // list order is the draw order, and hit-testing walks
                        // it back to front.
                        if (select != null) BringToFront(select);
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
                    else if (select != null && !select.Visible)
                    {
                        // A hidden target drags by its name pill.
                        var pill = GetHiddenPill(select).Pill;
                        _dragTarget = select;
                        _dragOffsetX = mx - pill.X;
                        _dragOffsetY = my - pill.Y;
                    }
                }
            }
            else if (!lmb && _groupDrag)
            {
                EndGroupDrag();
            }
            else if (!lmb && _marquee)
            {
                _marquee = false;
                SelectInBox(Math.Min(_marqueeX0, mx), Math.Min(_marqueeY0, my),
                            Math.Max(_marqueeX0, mx), Math.Max(_marqueeY0, my));
                _canvasDirty = true;
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
            else if (lmb && _groupDrag)
            {
                MoveGroupTo(mx - _groupPressX, my - _groupPressY);
            }
            else if (lmb && _marquee)
            {
                _canvasDirty = true; // the box follows the cursor
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

        // Add the target to the multi-selection, or take it out if it is in.
        private void ToggleGroupMember(OverlayRenderTarget rt)
        {
            var members = new List<OverlayRenderTarget>(_group);
            if (members.Count == 0 && _selected != null) members.Add(_selected);
            if (!members.Remove(rt)) members.Add(rt);
            SetSelection(members);
        }

        // One target is the ordinary selection (with its button bar); two or more
        // are a group.
        private void SetSelection(List<OverlayRenderTarget> members)
        {
            _group.Clear();
            if (members.Count >= 2)
            {
                _group.AddRange(members);
                _selected = null;
            }
            else
            {
                _selected = members.Count == 1 ? members[0] : null;
            }
        }

        // Finish a selection box: everything shown that it touches. Panels that
        // only exist for editing (the settings panels) are left out.
        private void SelectInBox(int left, int top, int right, int bottom)
        {
            if (right - left < 4 && bottom - top < 4) return; // a click, not a box

            var found = new List<OverlayRenderTarget>();
            foreach (var rt in _targets)
            {
                if (!rt.Visible || rt.IsSkipped(EditMode) || rt.EditModeOnly) continue;
                if (rt.X < right && rt.X + rt.DisplayWidth > left && rt.Y < bottom && rt.Y + rt.DisplayHeight > top)
                    found.Add(rt);
            }
            SetSelection(found);
        }

        private void BeginGroupDrag(int mx, int my)
        {
            _groupDrag = true;
            _groupPressX = mx;
            _groupPressY = my;
            _groupStart.Clear();
            foreach (var rt in _group) _groupStart[rt] = (rt.X, rt.Y);
            _canvasDirty = true;
        }

        // Move the whole group by the drag distance, stopping when any member
        // reaches the canvas edge so they keep their arrangement.
        private void MoveGroupTo(int dx, int dy)
        {
            int minDx = int.MinValue, maxDx = int.MaxValue, minDy = int.MinValue, maxDy = int.MaxValue;
            foreach (var rt in _group)
            {
                var (x, y) = _groupStart[rt];
                minDx = Math.Max(minDx, -x);
                maxDx = Math.Min(maxDx, CanvasWidth - rt.DisplayWidth - x);
                minDy = Math.Max(minDy, -y);
                maxDy = Math.Min(maxDy, CanvasHeight - rt.DisplayHeight - y);
            }
            dx = Math.Clamp(dx, minDx, Math.Max(minDx, maxDx));
            dy = Math.Clamp(dy, minDy, Math.Max(minDy, maxDy));
            foreach (var rt in _group)
            {
                var (x, y) = _groupStart[rt];
                rt.X = x + dx;
                rt.Y = y + dy;
            }
        }

        private void EndGroupDrag()
        {
            _groupDrag = false;
            foreach (var rt in _group) DragCompleted?.Invoke(rt);
        }

        private const int VK_SHIFT = 0x10, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
        private static readonly TimeSpan NudgeFirstDelay = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan NudgeRepeat = TimeSpan.FromMilliseconds(35);

        private bool _nudgeHeld;
        private bool _nudgeMoved;
        private TimeSpan _nudgeNextAt;
        private OverlayRenderTarget? _nudgeTarget;
        private readonly List<OverlayRenderTarget> _nudgeGroup = new();

        private static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>
        /// Edit mode: the arrow keys move the selected target 1 px (10 with Shift),
        /// repeating while held — for placing a panel more precisely than a mouse
        /// drag allows. Polled like the mouse, so it works while the game has
        /// focus. Raises <see cref="DragCompleted"/> once the keys are released, so
        /// the app saves the new position like it does after a drag.
        /// </summary>
        private void UpdateArrowNudge()
        {
            var rt = _selected ?? (_group.Count > 0 ? _group[0] : null);
            int dx = (KeyDown(VK_RIGHT) ? 1 : 0) - (KeyDown(VK_LEFT) ? 1 : 0);
            int dy = (KeyDown(VK_DOWN) ? 1 : 0) - (KeyDown(VK_UP) ? 1 : 0);

            bool busy = _dragTarget != null || _resizeTarget != null || _editCapture != null || _groupDrag;
            if (rt == null || busy || (dx == 0 && dy == 0))
            {
                if (_nudgeMoved && _nudgeTarget != null)
                {
                    if (_nudgeGroup.Count > 0) foreach (var m in _nudgeGroup) DragCompleted?.Invoke(m);
                    else DragCompleted?.Invoke(_nudgeTarget);
                }
                _nudgeHeld = false;
                _nudgeMoved = false;
                _nudgeTarget = null;
                _nudgeGroup.Clear();
                return;
            }

            var now = _inputClock.Elapsed;
            if (_nudgeHeld && now < _nudgeNextAt) return;
            _nudgeNextAt = now + (_nudgeHeld ? NudgeRepeat : NudgeFirstDelay);
            _nudgeHeld = true;

            int step = KeyDown(VK_SHIFT) ? 10 : 1;
            if (_group.Count > 0)
            {
                // The whole group, kept in step: it stops when any member hits the edge.
                int gx = dx * step, gy = dy * step;
                foreach (var m in _group)
                {
                    gx = Math.Clamp(gx, -m.X, Math.Max(-m.X, CanvasWidth - m.DisplayWidth - m.X));
                    gy = Math.Clamp(gy, -m.Y, Math.Max(-m.Y, CanvasHeight - m.DisplayHeight - m.Y));
                }
                if (gx == 0 && gy == 0) return;
                foreach (var m in _group) { m.X += gx; m.Y += gy; }
                _nudgeGroup.Clear();
                _nudgeGroup.AddRange(_group);
                _nudgeMoved = true;
                _nudgeTarget = rt;
                _canvasDirty = true;
                return;
            }
            int x = Math.Clamp(rt.X + dx * step, 0, Math.Max(0, CanvasWidth - rt.DisplayWidth));
            int y = Math.Clamp(rt.Y + dy * step, 0, Math.Max(0, CanvasHeight - rt.DisplayHeight));
            if (x == rt.X && y == rt.Y) return;

            rt.X = x;
            rt.Y = y;
            _nudgeMoved = true;
            _nudgeTarget = rt;
            _canvasDirty = true;
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
                 XnaRectangle Gaze, XnaRectangle Bind, XnaRectangle Show)
            GetEditButtons(OverlayRenderTarget rt)
        {
            int by = rt.Y - EditBtnHeight - 6;
            if (by < 0) by = rt.Y + 6; // target touches the canvas top: bar goes inside
            bool hasState = !rt.EditModeOnly;
            bool hasGaze = GazeButtonState != null && !rt.EditModeOnly;
            bool hasBind = BindButtonLabel != null && !rt.EditModeOnly;
            bool hasShow = ShowChips != null && !rt.EditModeOnly;
            int bindW = hasBind ? GetBindButtonWidth(rt) : 0;
            int barW = (hasState ? EditIconBtnWidth + EditBtnWidth + EditBtnGap * 2 : 0)
                     + EditStepBtnWidth * 2 + EditOpacityBtnWidth + EditBtnGap * 2
                     + (hasGaze ? EditIconBtnWidth + EditBtnGap : 0)
                     + (hasBind ? bindW + EditBtnGap : 0)
                     + (hasShow ? EditShowButtonWidth + EditBtnGap : 0);
            int bx = Math.Clamp(rt.X, 0, Math.Max(0, CanvasWidth - barW));

            XnaRectangle Next(int width)
            {
                var r = new XnaRectangle(bx, by, width, EditBtnHeight);
                bx += width + EditBtnGap;
                return r;
            }
            // Tuple elements are evaluated left to right, so Next() lays the
            // bar out in the order the buttons are drawn.
            return (hasState ? Next(EditIconBtnWidth) : XnaRectangle.Empty,
                    hasState ? Next(EditBtnWidth) : XnaRectangle.Empty,
                    Next(EditStepBtnWidth), Next(EditOpacityBtnWidth), Next(EditStepBtnWidth),
                    hasGaze ? Next(EditIconBtnWidth) : XnaRectangle.Empty,
                    hasBind ? Next(bindW) : XnaRectangle.Empty,
                    hasShow ? Next(EditShowButtonWidth) : XnaRectangle.Empty);
        }

        /// <summary>
        /// What the Bind button shows next to its gamepad icon: the bound
        /// button(s) (or the capture prompt) from <see cref="BindButtonLabel"/>,
        /// without its "Bind:" lead-in. Empty while nothing is bound, when the
        /// icon says it all.
        /// </summary>
        private string GetBindButtonText(OverlayRenderTarget rt)
        {
            if (BindButtonLabel == null) return string.Empty;
            var (label, active) = BindButtonLabel(rt);
            if (!active) return string.Empty;
            const string lead = "Bind:";
            return label.StartsWith(lead, StringComparison.OrdinalIgnoreCase) ? label.Substring(lead.Length).Trim() : label;
        }

        // ----- Docked panels ---------------------------------------------------
        // A docked target (see OverlayRenderTarget.DockSide) sits against the
        // left or right canvas edge, vertically centred, behind a small arrow
        // tab that opens and closes it. The host places and sizes it; it is never
        // selected, dragged, or given the button bar.
        private const int DockTabWidth = 56;
        private const int DockTabHeight = 180;
        private const int DockMargin = 16;   // clear space kept around an open panel

        private void LayoutDocks()
        {
            foreach (var rt in _targets)
            {
                if (!rt.IsDocked) continue;
                if (rt.State != OverlayTargetState.Visible) rt.State = OverlayTargetState.Visible;

                // Shrink a panel that is too big for the canvas so it always fits.
                // Centred on the screens below the top screen's band, not on the whole canvas.
                int band = BandPx(), area = CanvasHeight - band;
                float fit = MathF.Min(1f, MathF.Min((area - DockMargin) / (float)rt.Height,
                                                    (CanvasWidth - DockTabWidth - DockMargin) / (float)rt.Width));
                rt.Scale = Math.Max(OverlayRenderTarget.MinScale, fit);

                int w = rt.DisplayWidth, h = rt.DisplayHeight;
                rt.X = rt.DockSide == OverlayDockSide.Right ? Math.Max(0, CanvasWidth - w) : 0;
                rt.Y = band + Math.Max(0, (area - h) / 2);
            }
        }

        private void CloseDocks()
        {
            foreach (var rt in _targets)
                if (rt.IsDocked && rt.DockOpen) { rt.DockOpen = false; _canvasDirty = true; }
        }

        // Targets that are docked and exist right now in edit mode (open or not).
        private IEnumerable<OverlayRenderTarget> EditDocks()
        {
            if (!EditMode) yield break;
            foreach (var rt in _targets)
                if (rt.IsDocked && !rt.EditHidden) yield return rt;
        }

        private XnaRectangle GetDockTab(OverlayRenderTarget rt)
        {
            int band = BandPx(), area = CanvasHeight - band; // centred below the top screen's band
            int h = Math.Min(DockTabHeight, area);
            int y = band + (area - h) / 2;
            int panel = rt.DockOpen ? rt.DisplayWidth : 0;
            int x = rt.DockSide == OverlayDockSide.Right ? CanvasWidth - panel - DockTabWidth : panel;
            return new XnaRectangle(x, y, DockTabWidth, h);
        }

        /// <summary>A press on a dock tab toggles its panel; a press inside an open docked panel goes to it. True when used.</summary>
        private bool HandleDockPress(int mx, int my)
        {
            foreach (var rt in EditDocks())
            {
                if (GetDockTab(rt).Contains(mx, my))
                {
                    rt.DockOpen = !rt.DockOpen;
                    if (rt.DockOpen) BringToFront(rt);
                    _selected = null;
                    _canvasDirty = true;
                    return true;
                }
            }
            foreach (var rt in EditDocks())
            {
                if (!rt.DockOpen) continue;
                if (mx < rt.X || mx >= rt.X + rt.DisplayWidth || my < rt.Y || my >= rt.Y + rt.DisplayHeight) continue;
                if (rt.OnEditPress(ToLocalX(rt, mx), ToLocalY(rt, my)))
                {
                    _editCapture = rt;
                    _canvasDirty = true;
                }
                return true; // the panel is in the way either way
            }
            return false;
        }

        /// <summary>A dock tab: a rounded handle on the canvas edge with an arrow that points in to open and out to close.</summary>
        private void DrawDockTab(OverlayRenderTarget rt)
        {
            var r = GetDockTab(rt);
            var bg = rt.DockOpen ? new XnaColor(90, 60, 140) : new XnaColor(40, 40, 60);
            var edge = rt.DockOpen ? XnaColor.MediumPurple : XnaColor.Yellow;
            MonoXRDraw.RoundedRect(_compositor, r, 14, bg);
            MonoXRDraw.RoundedRectOutline(_compositor, r, 14, 2, edge);

            // Arrow toward the canvas centre when closed (pull the panel out), away from it when open.
            bool right = rt.DockSide == OverlayDockSide.Right;
            bool pointsLeft = right != rt.DockOpen;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            for (int k = 0; k < 12; k++)
            {
                int x = pointsLeft ? cx - 8 + k : cx + 8 - k;
                int barH = 2 + k * 2;
                _compositor.Draw(_whitePixel, new XnaRectangle(x, cy - barH / 2, 1, barH), XnaColor.White);
            }
        }

        // The Show setting is one dropdown button on the bar ("Show: Practice, Race")
        // that opens a list with a row per choice (All, then each session type).
        // Rows toggle on click and the list stays open, so several can be ticked;
        // a click anywhere else closes it. It belongs to the selected target.
        private bool _showMenuOpen;

        /// <summary>The closed button's text: "Show: All", or the ticked session types.</summary>
        private string GetShowSummary(OverlayRenderTarget rt)
        {
            var chips = ShowChips!(rt);
            string text;
            if (chips.Count == 0 || chips[0].On) text = "All";
            else
            {
                var on = new List<string>();
                for (int i = 1; i < chips.Count; i++) if (chips[i].On) on.Add(chips[i].Label);
                text = on.Count == 0 ? "All" : string.Join(", ", on);
            }
            text = "Show: " + text;

            // Ellipsize to fit the button, leaving room for the arrow.
            if (EditFont == null) return text;
            float max = EditShowButtonWidth - EditShowChipPad - 30;
            if (EditFont.MeasureString(text).X <= max) return text;
            while (text.Length > 1 && EditFont.MeasureString(text + "...").X > max)
                text = text.Substring(0, text.Length - 1);
            return text.TrimEnd() + "...";
        }

        /// <summary>The open list: one row per choice, below the button (above it when there is no room).</summary>
        private List<XnaRectangle> GetShowMenuRects(OverlayRenderTarget rt, XnaRectangle show)
        {
            var chips = ShowChips!(rt);
            int rowH = EditBtnHeight, gap = 4;
            int width = show.Width;
            if (EditFont != null)
                foreach (var chip in chips)
                    width = Math.Max(width, (int)MathF.Ceiling(EditFont.MeasureString(chip.Label).X) + EditShowChipPad + 40);
            int height = chips.Count * rowH + (chips.Count - 1) * gap;

            int x = Math.Clamp(show.X, 0, Math.Max(0, CanvasWidth - width));
            int y = show.Bottom + 6;
            if (y + height > CanvasHeight) y = Math.Max(0, show.Y - 6 - height);

            var rects = new List<XnaRectangle>(chips.Count);
            for (int i = 0; i < chips.Count; i++)
                rects.Add(new XnaRectangle(x, y + i * (rowH + gap), width, rowH));
            return rects;
        }

        /// <summary>The target whose Show list is open, or null.</summary>
        private OverlayRenderTarget? ShowMenuTarget =>
            _showMenuOpen && EditMode && _selected is { Visible: true } t && ShowChips != null && !t.EditModeOnly ? t : null;

        /// <summary>Press while the Show list is open: toggle the row hit, or close it when the press is elsewhere. True when the press was used.</summary>
        private bool HandleShowMenuClick(int mx, int my)
        {
            var rt = ShowMenuTarget;
            if (rt == null) { _showMenuOpen = false; return false; }

            var show = GetEditButtons(rt).Show;
            var rects = GetShowMenuRects(rt, show);
            for (int i = 0; i < rects.Count; i++)
            {
                if (!rects[i].Contains(mx, my)) continue;
                ShowChipClicked?.Invoke(rt, i);
                _canvasDirty = true;
                return true;
            }

            _showMenuOpen = false;
            _canvasDirty = true;
            return !show.IsEmpty && show.Contains(mx, my); // the button itself just closes it
        }

        private int GetBindButtonWidth(OverlayRenderTarget rt)
        {
            string text = GetBindButtonText(rt);
            if (text.Length == 0 || EditFont == null) return EditIconBtnWidth;
            return EditIconBtnWidth + (int)MathF.Ceiling(EditFont.MeasureString(text).X) + 24;
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
            if (rt != null && rt.Visible)
            {
                var (collapse, hide, fainter, opacityReset, stronger, gaze, bind, show) = GetEditButtons(rt);
                if (!gaze.IsEmpty && gaze.Contains(mx, my))
                {
                    GazeToggleRequested?.Invoke(rt);
                    _canvasDirty = true;
                    return true;
                }
                if (!bind.IsEmpty && bind.Contains(mx, my))
                {
                    BindRequested?.Invoke(rt);
                    _canvasDirty = true;
                    return true;
                }
                if (!show.IsEmpty && show.Contains(mx, my))
                {
                    _showMenuOpen = true;
                    _canvasDirty = true;
                    return true;
                }
                if (!collapse.IsEmpty && collapse.Contains(mx, my))
                {
                    rt.State = rt.IsCollapsed ? OverlayTargetState.Visible : OverlayTargetState.Collapsed;
                    _canvasDirty = true;
                    StateChanged?.Invoke(rt);
                    return true;
                }
                if (!hide.IsEmpty && hide.Contains(mx, my))
                {
                    rt.State = rt.Visible ? OverlayTargetState.Hidden : OverlayTargetState.Visible;
                    _canvasDirty = true;
                    StateChanged?.Invoke(rt);
                    return true;
                }
                if (fainter.Contains(mx, my)) { SetOpacityFromButton(rt, rt.Opacity - EditOpacityStep); return true; }
                if (stronger.Contains(mx, my)) { SetOpacityFromButton(rt, rt.Opacity + EditOpacityStep); return true; }
                if (opacityReset.Contains(mx, my)) { SetOpacityFromButton(rt, 1f); return true; }
            }
            return false;
        }

        /// <summary>Move a target to the end of the list so it is drawn (and hit) above every other.</summary>
        private void BringToFront(OverlayRenderTarget target)
        {
            int i = _targets.IndexOf(target);
            if (i < 0 || i == _targets.Count - 1) return;
            _targets.RemoveAt(i);
            _targets.Add(target);
        }

        /// <summary>
        /// A hidden target in edit mode is just a name pill with a Show button
        /// at its top-left corner (nothing of its content or button bar):
        /// the pill's and button's rects, kept on the canvas.
        /// </summary>
        private (XnaRectangle Pill, XnaRectangle Show) GetHiddenPill(OverlayRenderTarget rt)
        {
            int nameW = EditFont != null ? (int)MathF.Ceiling(EditFont.MeasureString(rt.Name).X) + 40 : 160;
            int totalW = nameW + EditBtnGap + EditBtnWidth;
            int px = Math.Clamp(rt.X, 0, Math.Max(0, CanvasWidth - totalW));
            int py = Math.Clamp(rt.Y, 0, Math.Max(0, CanvasHeight - EditBtnHeight));
            return (new XnaRectangle(px, py, nameW, EditBtnHeight),
                    new XnaRectangle(px + nameW + EditBtnGap, py, EditBtnWidth, EditBtnHeight));
        }

        /// <summary>A click on a hidden target's Show button shows it (and selects it). True when one was hit.</summary>
        private bool HandleHiddenShowClick(int mx, int my)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var rt = _targets[i];
                if (rt.Visible || rt.IsSkipped(EditMode)) continue;
                if (!GetHiddenPill(rt).Show.Contains(mx, my)) continue;
                rt.State = OverlayTargetState.Visible;
                _selected = rt;
                BringToFront(rt);
                _canvasDirty = true;
                StateChanged?.Invoke(rt);
                return true;
            }
            return false;
        }

        /// <summary>Topmost hidden target whose edit-mode name pill or Show button contains the point.</summary>
        private OverlayRenderTarget? HitTestHidden(int mx, int my)
        {
            for (int i = _targets.Count - 1; i >= 0; i--)
            {
                var rt = _targets[i];
                if (rt.Visible) continue;
                if (rt.IsSkipped(EditMode)) continue;
                var (pill, show) = GetHiddenPill(rt);
                if (pill.Contains(mx, my) || show.Contains(mx, my))
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
                if (rt.IsSkipped(EditMode)) continue;
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
            newOverlay.CurveRadius = old.CurveRadius;
            newOverlay.ZOrder = old.ZOrder;
            newOverlay.Visible = old.Visible;
            old.Dispose();
            _overlay = newOverlay;

            _canvasTarget.Dispose();
            _canvasTarget = new RenderTarget2D(GraphicsDevice, width, height);
            foreach (var rt in _targets) rt.Invalidate();

            // The top screen's band sits above everything, so when it appears,
            // grows or goes, the panels move with the rest of the canvas.
            int band = BandPx();
            if (band != _bandPxApplied)
            {
                foreach (var rt in _targets)
                {
                    rt.Y += band - _bandPxApplied;
                    ClampToCanvas(rt);
                }
                _bandPxApplied = band;
            }
            ApplyPose(); // also rebuilds the triple screens at the new size
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

            LayoutDocks();

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

            // Hidden targets are completely dormant: no Update, no Render, and
            // their dirty flag can't wake the canvas. In edit mode they show
            // only as a name pill with a Show button, which needs none of that.
            foreach (var rt in _targets)
            {
                if (!rt.Visible) continue;
                if (rt.IsSkipped(EditMode)) continue;
                rt.Update(gameTime);
            }

            // Dirty-flag rendering: when no target changed and nothing at the
            // canvas level changed (pose, edit mode, resolution), skip all GPU
            // work AND the publish — the layer then keeps re-showing the last
            // frame for free. Heartbeat still runs so attach/detach tracking works.
            bool anyDirty = _canvasDirty || (_calStep == 0 && EditToggleVisible != _editToggleShown);
            foreach (var rt in _targets)
            {
                if (!rt.Visible) continue;
                if (rt.IsSkipped(EditMode)) continue;
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
            // it is shown.
            foreach (var rt in _targets)
            {
                if (!rt.IsDirty) continue;
                if (!rt.Visible) continue;
                if (rt.IsSkipped(EditMode)) continue;
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
            bool published;
            if (_appliedTriple && _tripleOverlays != null)
            {
                // Each screen takes its own columns of the canvas.
                var segs = TripleLayout();
                IntPtr src = MonoGameInterop.GetTexturePointer(_canvasTarget);
                published = true;
                for (int i = 0; i < _tripleOverlays.Length && i < segs.Length; i++)
                    published &= _tripleOverlays[i].Update(src, segs[i].X0, segs[i].Y0);
            }
            else
                published = _overlay.Update(MonoGameInterop.GetTexturePointer(_canvasTarget));
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
                    if (_appliedTriple) { DisposeTriple(); ApplyPose(); }
                    else RecreateOverlay();
                }
            }
        }

        /// <summary>
        /// Edit-mode outline of one shown target so its bounds are obvious: the
        /// one being dragged/resized is highlighted, the selection is cyan. The
        /// selected target also gets its resize grip (a diagonal "stair" in the
        /// bottom-right corner, only where dragging it works).
        /// </summary>
        private void DrawEditOutline(OverlayRenderTarget rt)
        {
            _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            int dw = rt.DisplayWidth, dh = rt.DisplayHeight;
            bool active = rt == _dragTarget || rt == _resizeTarget || (_groupDrag && _group.Contains(rt));
            var c = active                                   ? XnaColor.Lime
                  : rt == _selected || _group.Contains(rt)   ? XnaColor.Cyan
                                                             : XnaColor.Yellow;
            _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y, dw, 2), c);
            _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y + dh - 2, dw, 2), c);
            _compositor.Draw(_whitePixel, new XnaRectangle(rt.X, rt.Y, 2, dh), c);
            _compositor.Draw(_whitePixel, new XnaRectangle(rt.X + dw - 2, rt.Y, 2, dh), c);

            if (rt == _selected && !rt.IsCollapsed)
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
            _compositor.End();
        }

        /// <summary>Composite every target (and the edit-mode chrome) onto the bound, cleared canvas.</summary>
        private void ComposeTargets()
        {
            // Linear sampling: identical to point sampling for targets at 100%
            // (integer positions, 1:1 texels) and smooth for resized ones.
            //
            // In edit mode each target's outline and resize grip are drawn
            // right after its own content, not in a pass over all targets
            // afterwards: otherwise a lower target's outline would be painted
            // across the selected (topmost) one.
            foreach (var rt in _targets)
            {
                if (!rt.Visible) continue;
                if (rt.IsSkipped(EditMode)) continue;
                // Collapsed targets only contribute their top-left name-bar
                // region (the subclass draws the bar there when collapsed).
                // The source region is stretched to the Scale-d display size.
                var src = new XnaRectangle(0, 0, rt.SourceWidth, rt.SourceHeight);
                _compositor.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.LinearClamp);
                _compositor.Draw(rt.Target,
                    new XnaRectangle(rt.X, rt.Y, rt.DisplayWidth, rt.DisplayHeight),
                    src, XnaColor.White * rt.Opacity);
                _compositor.End();

                if (EditMode && !rt.IsDocked) DrawEditOutline(rt);
            }
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

                // With a top screen, only the middle columns of the band along the
                // top of the canvas are shown in VR: dim the rest, and mark where
                // the band ends.
                if (_appliedTriple && _tripleOverlays is { Length: 4 })
                {
                    var top = TripleLayout()[3];
                    var dim = XnaColor.Black * 0.6f;
                    _compositor.Draw(_whitePixel, new XnaRectangle(0, 0, top.X0, top.Py), dim);
                    _compositor.Draw(_whitePixel, new XnaRectangle(top.X0 + top.Px, 0, w - top.X0 - top.Px, top.Py), dim);
                    _compositor.Draw(_whitePixel, new XnaRectangle(0, top.Py - 1, w, 2), XnaColor.Yellow * 0.8f);
                }

                // Hidden targets: just their name and a Show button.
                foreach (var rt in _targets)
                {
                    if (rt.Visible || rt.IsSkipped(EditMode)) continue;
                    var (pill, show) = GetHiddenPill(rt);
                    DrawEditPill(pill, rt.Name, rt == _selected);
                    DrawEditButton(show, "Show", false);
                }

                // The selection box being dragged over empty canvas.
                if (_marquee)
                {
                    int l = Math.Min(_marqueeX0, _cursorX), t0 = Math.Min(_marqueeY0, _cursorY);
                    int w0 = Math.Abs(_cursorX - _marqueeX0), h0 = Math.Abs(_cursorY - _marqueeY0);
                    _compositor.Draw(_whitePixel, new XnaRectangle(l, t0, w0, h0), XnaColor.Cyan * 0.15f);
                    _compositor.Draw(_whitePixel, new XnaRectangle(l, t0, w0, 2), XnaColor.Cyan);
                    _compositor.Draw(_whitePixel, new XnaRectangle(l, t0 + h0 - 2, w0, 2), XnaColor.Cyan);
                    _compositor.Draw(_whitePixel, new XnaRectangle(l, t0, 2, h0), XnaColor.Cyan);
                    _compositor.Draw(_whitePixel, new XnaRectangle(l + w0 - 2, t0, 2, h0), XnaColor.Cyan);
                }

                // Button bar for the selected, shown target only: Collapse /
                // Hide, opacity, Bind, Show mode.
                if (_selected is { Visible: true } sel)
                {
                    var rt = sel;
                    var (collapse, hide, fainter, opacityReset, stronger, gaze, bind, show) = GetEditButtons(rt);
                    if (!collapse.IsEmpty) DrawCollapseButton(collapse, rt.IsCollapsed);
                    if (!hide.IsEmpty) DrawEditButton(hide, rt.Visible ? "Hide" : "Show", !rt.Visible);
                    DrawEditButton(fainter, "-", false);
                    DrawOpacityButton(opacityReset, rt.Opacity);
                    DrawEditButton(stronger, "+", false);
                    if (!gaze.IsEmpty && GazeButtonState != null) DrawGazeButton(gaze, GazeButtonState(rt));
                    if (!bind.IsEmpty && BindButtonLabel != null)
                        DrawBindButton(bind, BindButtonLabel(rt).Active, GetBindButtonText(rt));
                    if (!show.IsEmpty && ShowChips != null)
                        DrawShowButton(show, GetShowSummary(rt), _showMenuOpen);
                }

                foreach (var dockRt in EditDocks()) DrawDockTab(dockRt);

                // The open Show list goes last so it covers whatever is under it.
                if (ShowMenuTarget is { } menuTarget)
                {
                    var chips = ShowChips!(menuTarget);
                    var rects = GetShowMenuRects(menuTarget, GetEditButtons(menuTarget).Show);
                    for (int i = 0; i < chips.Count; i++)
                        DrawShowMenuRow(rects[i], chips[i].Label, chips[i].On);
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
            if (!_appliedTriple && Curved != _appliedCurved) ApplyPose(); // the layer has just reported what the runtime can do
            var canvas = _overlay;
            _cursorOverlay.Space = canvas.Space;
            _cursorOverlay.Rotation = canvas.Rotation;
            _cursorOverlay.CurveRadius = 0f; // always a flat marker
            _cursorOverlay.Size = new Vector2(CursorHalfLengthMeters * 2f, CursorHalfLengthMeters * 2f);
            // Hidden while calibrating (the user lines up the GAME'S cursor then).
            _cursorOverlay.Visible = (canvas.Visible || _appliedTriple) && _calStep == 0;

            if (_appliedTriple)
            {
                // On whichever of the three screens the cursor's column belongs to.
                var frame = Quaternion.CreateFromYawPitchRoll(OriginYaw, OriginPitch, 0f);
                var segs = TripleLayout();
                float cx = _cursorX + 0.5f, cy = _cursorY + 0.5f;
                var seg = segs.Length == 4 && cy < segs[3].Py ? segs[3] // the extra band along the top: the top screen
                        : cx < segs[1].X0 ? segs[0] : cx < segs[2].X0 ? segs[1] : segs[2];
                float lx = ((cx - seg.X0) / seg.Px - 0.5f) * seg.WidthM;
                float ly = (0.5f - (cy - seg.Y0) / seg.Py) * seg.HeightM;
                var turn = SegmentRotation(seg);
                _cursorOverlay.Rotation = frame * turn;
                _cursorOverlay.Position = OriginPosition + Vector3.Transform(
                    seg.Center + Vector3.Transform(new Vector3(lx, ly, 0f), turn), frame);

                if (_cursorPublished)
                    _cursorOverlay.UpdatePose();
                else
                    _cursorPublished = _cursorOverlay.Update(MonoGameInterop.GetTexturePointer(_cursorTexture));
                return;
            }

            // Quad-local: centered, +X right, +Y up, in meters.
            float u = (_cursorX + 0.5f) / CanvasWidth, v = (_cursorY + 0.5f) / CanvasHeight;
            var local = new Vector3((u - 0.5f) * canvas.Size.X, (0.5f - v) * canvas.Size.Y, 0f);
            if (_appliedCurved)
            {
                // Follow the surface: x is arc length, so turn about the axis
                // (which sits one radius behind the quad's centre) by that arc.
                float radius = CurveRadius;
                float phi = local.X / radius;
                local = new Vector3(radius * MathF.Sin(phi), local.Y, radius * (1f - MathF.Cos(phi)));
                _cursorOverlay.Rotation = canvas.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitY, -phi);
            }
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
            DrawEditButtonFrame(r, active);

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

        /// <summary>The closed Show dropdown: its summary text and a down (open) or up arrow.</summary>
        private void DrawShowButton(XnaRectangle r, string text, bool open)
        {
            DrawEditButtonFrame(r, open);
            DrawButtonText(new XnaRectangle(r.X + EditShowChipPad / 2 + 4, r.Y, r.Width - EditShowChipPad - 30, r.Height), text, false);

            // Arrow: a small triangle built from rows.
            int cx = r.Right - 30, cy = r.Y + r.Height / 2;
            for (int i = 0; i < 6; i++)
            {
                int w = open ? 2 + i * 2 : 14 - i * 2;
                _compositor.Draw(_whitePixel, new XnaRectangle(cx - w / 2, cy - 4 + i, w, 1), XnaColor.White);
            }
        }

        /// <summary>One row of the open Show list: a tick box and the choice's name.</summary>
        private void DrawShowMenuRow(XnaRectangle r, string label, bool on)
        {
            MonoXRDraw.RoundedRect(_compositor, r, 8, new XnaColor(30, 30, 48));
            MonoXRDraw.RoundedRectOutline(_compositor, r, 8, 2, on ? XnaColor.MediumPurple : XnaColor.Yellow);

            var box = new XnaRectangle(r.X + 12, r.Y + (r.Height - 22) / 2, 22, 22);
            _compositor.Draw(_whitePixel, box, XnaColor.White);
            _compositor.Draw(_whitePixel, new XnaRectangle(box.X + 2, box.Y + 2, box.Width - 4, box.Height - 4),
                             on ? new XnaColor(120, 80, 190) : new XnaColor(30, 30, 48));
            if (on)
            {
                DrawLine(new XnaVector2(box.X + 5, box.Y + 12), new XnaVector2(box.X + 10, box.Y + 17), 3f, XnaColor.White);
                DrawLine(new XnaVector2(box.X + 10, box.Y + 17), new XnaVector2(box.X + 18, box.Y + 6), 3f, XnaColor.White);
            }
            DrawButtonText(new XnaRectangle(box.Right + 12, r.Y, r.Width - box.Width - 36, r.Height), label, false);
        }

        /// <summary>The pill behind an edit-mode button; returns the fill colour, for icons that cut shapes out of it.</summary>
        private XnaColor DrawEditButtonFrame(XnaRectangle r, bool active)
        {
            var bg = active ? new XnaColor(90, 60, 140) : new XnaColor(40, 40, 60);
            var edge = active ? XnaColor.MediumPurple : XnaColor.Yellow;
            int radius = r.Height / 2;
            MonoXRDraw.RoundedRect(_compositor, r, radius, bg);
            MonoXRDraw.RoundedRectOutline(_compositor, r, radius, 2, edge);
            return bg;
        }

        private void DrawButtonText(XnaRectangle area, string text, bool centerHorizontally)
        {
            if (EditFont == null || text.Length == 0) return;
            var size = EditFont.MeasureString(text);
            int x = centerHorizontally ? area.X + (int)((area.Width - size.X) / 2) : area.X;
            _compositor.DrawString(EditFont, text,
                new XnaVector2(x, area.Y + (int)((area.Height - size.Y) / 2)), XnaColor.White);
        }

        /// <summary>Collapse button: a chevron pointing up (click to collapse) or, once collapsed, down (click to expand).</summary>
        private void DrawCollapseButton(XnaRectangle r, bool collapsed)
        {
            DrawEditButtonFrame(r, collapsed);
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            float dy = collapsed ? -5f : 5f; // arms sit below the tip when pointing up
            var tip = new XnaVector2(cx, cy - dy);
            DrawLine(tip, new XnaVector2(cx - 10f, cy + dy), 4f, XnaColor.White);
            DrawLine(tip, new XnaVector2(cx + 10f, cy + dy), 4f, XnaColor.White);
        }

        /// <summary>Gaze button: an eye, lit while the target reacts to being looked at and struck through when it does not.</summary>
        private void DrawGazeButton(XnaRectangle r, bool enabled)
        {
            var bg = DrawEditButtonFrame(r, enabled);
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            const int halfW = 16, halfH = 9;
            for (int dx = -halfW; dx <= halfW; dx++)
            {
                float u = dx / (float)halfW;
                int h = (int)MathF.Round(halfH * (1f - u * u));
                _compositor.Draw(_whitePixel, new XnaRectangle(cx + dx, cy - h, 1, h * 2 + 1), XnaColor.White);
            }
            const int innerW = 13, innerH = 6;
            for (int dx = -innerW; dx <= innerW; dx++)
            {
                float u = dx / (float)innerW;
                int h = (int)MathF.Round(innerH * (1f - u * u));
                _compositor.Draw(_whitePixel, new XnaRectangle(cx + dx, cy - h, 1, h * 2 + 1), bg);
            }
            const int pupil = 4;
            for (int y = -pupil; y <= pupil; y++)
            {
                int w = (int)MathF.Round(MathF.Sqrt(pupil * pupil - y * y));
                _compositor.Draw(_whitePixel, new XnaRectangle(cx - w, cy + y, w * 2 + 1, 1), XnaColor.White);
            }
            if (!enabled)
            {
                var a = new XnaVector2(cx - 15f, cy + 11f);
                var b = new XnaVector2(cx + 15f, cy - 11f);
                DrawLine(a, b, 7f, bg);
                DrawLine(a, b, 3f, XnaColor.OrangeRed);
            }
        }

        /// <summary>Opacity button: a half-filled disc followed by the current percentage.</summary>
        private void DrawOpacityButton(XnaRectangle r, float opacity)
        {
            var bg = DrawEditButtonFrame(r, opacity < 1f);
            const int radius = 11;
            int cx = r.X + 14 + radius, cy = r.Y + r.Height / 2;
            const int inner = radius - 2;
            for (int y = -radius; y <= radius; y++)
            {
                int wo = (int)MathF.Round(MathF.Sqrt(radius * radius - y * y));
                _compositor.Draw(_whitePixel, new XnaRectangle(cx - wo, cy + y, wo * 2, 1), XnaColor.White);
                if (Math.Abs(y) > inner) continue;
                int wi = (int)MathF.Round(MathF.Sqrt(inner * inner - y * y));
                _compositor.Draw(_whitePixel, new XnaRectangle(cx - wi, cy + y, wi * 2, 1), bg);
                _compositor.Draw(_whitePixel, new XnaRectangle(cx - wi, cy + y, wi, 1), XnaColor.White);
            }
            int textX = cx + radius + 10;
            DrawButtonText(new XnaRectangle(textX, r.Y, r.Right - 14 - textX, r.Height),
                           $"{MathF.Round(opacity * 100f):0}%", true);
        }

        /// <summary>Bind button: a gamepad icon, followed by the bound button(s) once there are any.</summary>
        private void DrawBindButton(XnaRectangle r, bool active, string text)
        {
            var bg = DrawEditButtonFrame(r, active);
            const int w = 32, h = 20;
            var body = new XnaRectangle(r.X + (EditIconBtnWidth - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            MonoXRDraw.RoundedRect(_compositor, body, h / 2, XnaColor.White);

            int cy = body.Y + h / 2;
            int padX = body.X + 9;
            _compositor.Draw(_whitePixel, new XnaRectangle(padX - 5, cy - 1, 11, 3), bg);   // d-pad
            _compositor.Draw(_whitePixel, new XnaRectangle(padX - 1, cy - 5, 3, 11), bg);
            _compositor.Draw(_whitePixel, new XnaRectangle(body.Right - 13, cy - 1, 5, 5), bg); // face buttons
            _compositor.Draw(_whitePixel, new XnaRectangle(body.Right - 8, cy - 5, 5, 5), bg);

            if (text.Length > 0)
                DrawButtonText(new XnaRectangle(r.X + EditIconBtnWidth - 4, r.Y, r.Width - EditIconBtnWidth - 8, r.Height),
                               text, false);
        }

        /// <summary>A label-only pill (a hidden target's name in edit mode); must be inside an alpha-blend batch.</summary>
        private void DrawEditPill(XnaRectangle r, string label, bool selected)
        {
            int radius = r.Height / 2;
            MonoXRDraw.RoundedRect(_compositor, r, radius, new XnaColor(40, 40, 60));
            MonoXRDraw.RoundedRectOutline(_compositor, r, radius, 2, selected ? XnaColor.Cyan : XnaColor.Gray);
            if (EditFont != null)
            {
                var size = EditFont.MeasureString(label);
                _compositor.DrawString(EditFont, label,
                    new XnaVector2(r.X + (int)((r.Width - size.X) / 2), r.Y + (int)((r.Height - size.Y) / 2)),
                    XnaColor.White);
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
            fresh.CurveRadius = old.CurveRadius;
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
            DisposeTriple();
            _overlay.Dispose();
            _mgr.Dispose();
            GraphicsDevice.Dispose();
            _hiddenForm.Dispose();
        }
    }
}
