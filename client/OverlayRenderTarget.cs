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
        public int SourceHeight => IsCollapsed ? Math.Min(CollapsedHeight, Height) : Height;

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
