using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MonoXR.Client
{
    /// <summary>Display state of an <see cref="OverlayRenderTarget"/> on the canvas.</summary>
    public enum OverlayTargetState
    {
        /// <summary>Not composited at all.</summary>
        Hidden,
        /// <summary>Fully shown at its normal size.</summary>
        Visible,
        /// <summary>Shrunk to a small bar showing only the target's <see cref="OverlayRenderTarget.Name"/>.</summary>
        Collapsed,
    }

    /// <summary>Which canvas edge an <see cref="OverlayRenderTarget"/> is docked to.</summary>
    public enum OverlayDockSide
    {
        /// <summary>Free-standing: placed and dragged anywhere.</summary>
        None,
        Left,
        Right,
    }

    /// <summary>
    /// One MonoGame render target placed somewhere on the overlay canvas.
    /// Inherit from this and override <see cref="Update"/> (game logic) and
    /// <see cref="Render"/> (drawing, target already bound). Move it at runtime
    /// by changing <see cref="X"/>/<see cref="Y"/>.
    /// </summary>
    public abstract class OverlayRenderTarget : IDisposable
    {
        public RenderTarget2D Target { get; private set; }

        /// <summary>Shared device for creating textures, SpriteBatches, fonts, etc.</summary>
        public GraphicsDevice GraphicsDevice { get; }

        public int Width => Target.Width;
        public int Height => Target.Height;

        private int _x, _y;
        private OverlayTargetState _state = OverlayTargetState.Visible;

        /// <summary>Pixel location of this target's top-left corner in the overlay canvas.</summary>
        public int X { get => _x; set { if (_x != value) { _x = value; Invalidate(); } } }
        public int Y { get => _y; set { if (_y != value) { _y = value; Invalidate(); } } }

        /// <summary>
        /// Display name shown when the target is collapsed (and useful for
        /// tooling). Defaults to the subclass type name.
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// True while the host is in edit mode. Set by the host (which also
        /// invalidates the target on changes) so targets that are normally
        /// invisible when idle can draw a placeholder to position them by.
        /// </summary>
        public bool EditMode { get; internal set; }

        /// <summary>
        /// True for a target that only exists while the host is in edit mode
        /// (e.g. an in-VR panel for arranging the overlay itself). Outside
        /// edit mode it is not updated, rendered, composited or hit-tested —
        /// not even as a hidden ghost; inside it behaves like any other target.
        /// </summary>
        public bool EditModeOnly { get; set; }

        private bool _available = true;

        /// <summary>
        /// App-controlled gate, separate from the user's Hide toggle: false
        /// means this target does not apply at the moment (e.g. a panel the
        /// app only shows in certain situations) and it is skipped entirely,
        /// not even drawn as a hidden ghost. Edit mode ignores it, so the
        /// panel can still be found and placed.
        /// </summary>
        public bool Available
        {
            get => _available;
            set { if (_available != value) { _available = value; Invalidate(); } }
        }

        /// <summary>True when this target takes part outside edit mode as things stand.</summary>
        public bool IsLive => _available && !EditModeOnly;

        private bool _editHidden;

        /// <summary>
        /// App-controlled gate for edit mode: true leaves this target out
        /// entirely while editing — not drawn, not outlined, not hit-tested, not
        /// even as a hidden ghost. <see cref="Available"/> is ignored in edit
        /// mode so panels can be found and placed; this is for a panel that does
        /// not apply to the situation being edited (e.g. a replay-only panel
        /// while arranging the in-car layout). Has no effect outside edit mode.
        /// </summary>
        public bool EditHidden
        {
            get => _editHidden;
            set { if (_editHidden != value) { _editHidden = value; Invalidate(); } }
        }

        /// <summary>
        /// True when this target takes no part right now: outside edit mode when
        /// not <see cref="IsLive"/>, inside it when <see cref="EditHidden"/>.
        /// </summary>
        public bool IsSkipped(bool editMode) => (IsDocked && !_dockOpen) || (editMode ? _editHidden : !IsLive);

        private OverlayDockSide _dockSide;
        private bool _dockOpen;

        /// <summary>
        /// Docks the target to a canvas edge, vertically centred: the host then
        /// owns its position and size (no dragging, no edit button bar), shows
        /// only a small arrow tab while it is closed, and the panel itself once
        /// the tab has been clicked (<see cref="DockOpen"/>). Meant for panels
        /// that only exist in edit mode (<see cref="EditModeOnly"/>).
        /// </summary>
        public OverlayDockSide DockSide
        {
            get => _dockSide;
            set { if (_dockSide != value) { _dockSide = value; Invalidate(); } }
        }

        /// <summary>Whether a docked target is expanded. Closed (the start state) it takes no part but for its tab.</summary>
        public bool DockOpen
        {
            get => _dockOpen;
            set { if (_dockOpen != value) { _dockOpen = value; Invalidate(); } }
        }

        public bool IsDocked => _dockSide != OverlayDockSide.None;

        /// <summary>Hidden / Visible / Collapsed. Changing it re-renders and republishes.</summary>
        public OverlayTargetState State
        {
            get => _state;
            set { if (_state != value) { _state = value; Invalidate(); } }
        }

        /// <summary>
        /// Compatibility wrapper over <see cref="State"/>: false = Hidden,
        /// true = shown (does not disturb a Collapsed state).
        /// </summary>
        public bool Visible
        {
            get => _state != OverlayTargetState.Hidden;
            set
            {
                if (value && _state == OverlayTargetState.Hidden) State = OverlayTargetState.Visible;
                else if (!value) State = OverlayTargetState.Hidden;
            }
        }

        public bool IsCollapsed => _state == OverlayTargetState.Collapsed;

        /// <summary>
        /// Pixel size of the collapsed bar — the top-left region of the target
        /// composited when <see cref="State"/> is Collapsed. Subclasses should
        /// draw their name bar inside this region in <see cref="Render"/>.
        /// </summary>
        public virtual int CollapsedWidth => 220;
        public virtual int CollapsedHeight => 44;

        /// <summary>Texture region that is composited (collapsed bar or the full target), in target pixels.</summary>
        public int SourceWidth => IsCollapsed ? Math.Min(CollapsedWidth, Width) : Width;
        public int SourceHeight => IsCollapsed ? Math.Min(CollapsedHeight, Height) : Height - SourceTop;

        /// <summary>Height of the title bar at the top of this target's picture, for targets that draw one (0: none). See <see cref="TitleBarHidden"/>.</summary>
        public virtual int TitleBarSourceHeight => 0;

        private bool _titleBarHidden;

        /// <summary>
        /// Leave the title bar out of what is shown: the window then starts under it. Only
        /// for targets that report a <see cref="TitleBarSourceHeight"/>; a collapsed target
        /// always shows its name.
        /// </summary>
        public bool TitleBarHidden
        {
            get => _titleBarHidden;
            set { if (_titleBarHidden == value) return; _titleBarHidden = value; Invalidate(); }
        }

        /// <summary>First row of the picture that is shown (below a hidden title bar).</summary>
        public int SourceTop => _titleBarHidden && !IsCollapsed ? Math.Clamp(TitleBarSourceHeight, 0, Math.Max(0, Height - 1)) : 0;

        public const float MinScale = 0.4f;
        public const float MaxScale = 3f;
        private float _scale = 1f;

        /// <summary>
        /// User-facing size multiplier. The target still renders at its own
        /// pixel size; the host stretches that texture by this factor when it
        /// composites it, so every target is resizable without any layout
        /// code of its own. Clamped to <see cref="MinScale"/>..<see cref="MaxScale"/>.
        /// </summary>
        public float Scale
        {
            get => _scale;
            set
            {
                float v = float.IsFinite(value) ? Math.Clamp(value, MinScale, MaxScale) : 1f;
                if (v != _scale) { _scale = v; Invalidate(); }
            }
        }

        public const float MinOpacity = 0.1f;

        private float _opacity = 1f;

        /// <summary>
        /// User-facing opacity, 0.1 (mostly see-through) to 1 (as drawn). The
        /// host multiplies the whole target by this when it composites it, so
        /// every target can be made more transparent without code of its own.
        /// </summary>
        public float Opacity
        {
            get => _opacity;
            set
            {
                float v = float.IsFinite(value) ? Math.Clamp(value, MinOpacity, 1f) : 1f;
                if (v != _opacity) { _opacity = v; Invalidate(); }
            }
        }

        // ---- Placement in 3D (only used while MonoGameOverlayHost.PerWindowOverlays is on) ----
        // Each window then is its own OpenXR overlay, and these move it away from where
        // its canvas position puts it. They never touch the canvas layout, so the
        // pointer (which works in canvas pixels) keeps hitting the window.

        /// <summary>
        /// Meters to push this window away from (positive) or toward (negative) the
        /// viewer, along the line from the view origin to the window. Its apparent
        /// size stays the same, so it grows and shrinks in the world as it moves.
        /// </summary>
        public float DistanceOffset { get; set; }

        /// <summary>Turn about the window's own vertical axis, degrees (positive turns it toward +X).</summary>
        public float YawDegrees { get; set; }

        /// <summary>Tilt about the window's own horizontal axis, degrees (positive tips the top toward the viewer).</summary>
        public float PitchDegrees { get; set; }

        /// <summary>Spin about the window's own normal, degrees.</summary>
        public float RollDegrees { get; set; }

        /// <summary>
        /// Bend this window around the viewer on a vertical cylinder (needs a runtime with
        /// cylinder layers, otherwise it stays flat). 0 is flat, 1 wraps it evenly around
        /// the viewer so every point is the same distance away, less is a gentler curve and
        /// more a tighter one. The window then turns to face the viewer.
        /// </summary>
        public float CurveAmount { get; set; }

        /// <summary>
        /// Meters this window's picture stands out of its frame (positive, toward the
        /// viewer) or sinks behind it (negative), added to the host's
        /// <see cref="MonoGameOverlayHost.StereoDepth"/>. Only while
        /// <see cref="MonoGameOverlayHost.Stereo3D"/> is on, when the window is shown by
        /// one overlay per eye and each eye's picture is shifted to match.
        /// </summary>
        public float StereoDepthOffset { get; set; }

        /// <summary>
        /// A picture for the right eye, same size as <see cref="Target"/> (for stereo
        /// video, say). While <see cref="MonoGameOverlayHost.Stereo3D"/> is on the host
        /// shows <see cref="Target"/> to the left eye and this to the right; null shows
        /// <see cref="Target"/> to both. Keep it drawn, and call <see cref="Invalidate"/>
        /// when it changes. The host does not own it: dispose it yourself.
        /// </summary>
        public RenderTarget2D? RightEyeTarget { get; set; }

        /// <summary>True when any of the 3D placement values differs from "where the canvas puts it".</summary>
        public bool HasPlacementOverride =>
            DistanceOffset != 0f || YawDegrees != 0f || PitchDegrees != 0f || RollDegrees != 0f || CurveAmount != 0f;

        /// <summary>Size currently occupied on the canvas: the source region times <see cref="Scale"/>.</summary>
        public int DisplayWidth => Math.Max(1, (int)MathF.Round(SourceWidth * _scale));
        public int DisplayHeight => Math.Max(1, (int)MathF.Round(SourceHeight * _scale));

        /// <summary>
        /// True when this target needs re-rendering and the canvas re-publishing.
        /// Starts true so the first frame always draws. The host clears it after
        /// rendering; call <see cref="Invalidate"/> whenever your content changes.
        /// </summary>
        public bool IsDirty { get; private set; } = true;

        /// <summary>Mark this target as changed so the next frame re-renders and publishes it.</summary>
        public void Invalidate() => IsDirty = true;

        /// <summary>
        /// Recreate the backing texture at a new size (e.g. a user resize in
        /// edit mode). Contents are lost; the target re-renders next frame.
        /// Call only from the render thread (edit-input callbacks are).
        /// </summary>
        protected void Resize(int width, int height)
        {
            if (width == Target.Width && height == Target.Height) return;
            Target.Dispose();
            Target = new RenderTarget2D(GraphicsDevice, Math.Max(1, width), Math.Max(1, height));
            Invalidate();
        }

        internal void ClearDirty() => IsDirty = false;

        protected OverlayRenderTarget(GraphicsDevice device, int width, int height, int x, int y)
        {
            GraphicsDevice = device;
            Target = new RenderTarget2D(device, width, height);
            X = x;
            Y = y;
            Name = GetType().Name;
        }

        /// <summary>
        /// Edit-mode pointer press inside this target, in target-local pixels.
        /// Return true to capture the gesture: the host then routes
        /// <see cref="OnEditDrag"/>/<see cref="OnEditRelease"/> here instead of
        /// dragging the target itself. Default: not handled (target drags).
        /// </summary>
        public virtual bool OnEditPress(int x, int y) => false;

        /// <summary>Pointer moved while captured via <see cref="OnEditPress"/> (target-local pixels; may go out of bounds).</summary>
        public virtual void OnEditDrag(int x, int y) { }

        /// <summary>Pointer released — or edit mode ended — while captured via <see cref="OnEditPress"/>.</summary>
        public virtual void OnEditRelease(int x, int y) { }

        /// <summary>
        /// Pointer press inside this target OUTSIDE edit mode (normal use, e.g.
        /// dragging a slider), in target-local pixels. Return true to capture
        /// the gesture so <see cref="OnPointerDrag"/>/<see cref="OnPointerRelease"/>
        /// follow. Default: not handled (the click does nothing on the overlay).
        /// </summary>
        public virtual bool OnPointerPress(int x, int y) => false;

        /// <summary>Pointer moved while captured via <see cref="OnPointerPress"/> (target-local pixels; may go out of bounds).</summary>
        public virtual void OnPointerDrag(int x, int y) { }

        /// <summary>Pointer released — or edit mode started — while captured via <see cref="OnPointerPress"/>.</summary>
        public virtual void OnPointerRelease(int x, int y) { }

        /// <summary>
        /// Pointer resting over this target OUTSIDE edit mode, in target-local
        /// pixels — every frame, button or no button, for the topmost target
        /// under the cursor only. Use it to highlight what a click would hit.
        /// Edit mode sends no hover (the pointer is arranging panels there).
        /// </summary>
        public virtual void OnPointerMove(int x, int y) { }

        /// <summary>The pointer left this target (or edit mode began): drop any hover highlight.</summary>
        public virtual void OnPointerExit() { }

        /// <summary>
        /// Pointer resting over this target IN edit mode (no button held), in target-local
        /// pixels, for the topmost target under the cursor only. For targets with controls of
        /// their own, such as a drop-down list, that highlight what a press would pick.
        /// </summary>
        public virtual void OnEditHover(int x, int y) { }

        /// <summary>The pointer left this target (or a button was pressed, or edit mode ended): drop any <see cref="OnEditHover"/> highlight.</summary>
        public virtual void OnEditHoverExit() { }

        /// <summary>
        /// Right-button press inside this target OUTSIDE edit mode, in
        /// target-local pixels — for a context menu drawn by the target
        /// itself. There is no right-button drag or release: the button is
        /// only reported when it goes down. Return true when it was used.
        /// </summary>
        public virtual bool OnPointerRightPress(int x, int y) => false;

        /// <summary>Called once per frame, before any target renders. Put game/animation logic here.</summary>
        public abstract void Update(GameTime gameTime);

        /// <summary>Called once per frame with this render target already bound — just draw.</summary>
        public abstract void Render(GameTime gameTime);

        public virtual void Dispose() => Target.Dispose();
    }
}
