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
        public RenderTarget2D Target { get; }

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

        /// <summary>Width currently occupied on the canvas (collapsed bar or full size).</summary>
        public int DisplayWidth => IsCollapsed ? Math.Min(CollapsedWidth, Width) : Width;
        public int DisplayHeight => IsCollapsed ? Math.Min(CollapsedHeight, Height) : Height;

        /// <summary>
        /// True when this target needs re-rendering and the canvas re-publishing.
        /// Starts true so the first frame always draws. The host clears it after
        /// rendering; call <see cref="Invalidate"/> whenever your content changes.
        /// </summary>
        public bool IsDirty { get; private set; } = true;

        /// <summary>Mark this target as changed so the next frame re-renders and publishes it.</summary>
        public void Invalidate() => IsDirty = true;

        internal void ClearDirty() => IsDirty = false;

        protected OverlayRenderTarget(GraphicsDevice device, int width, int height, int x, int y)
        {
            GraphicsDevice = device;
            Target = new RenderTarget2D(device, width, height);
            X = x;
            Y = y;
            Name = GetType().Name;
        }

        /// <summary>Called once per frame, before any target renders. Put game/animation logic here.</summary>
        public abstract void Update(GameTime gameTime);

        /// <summary>Called once per frame with this render target already bound — just draw.</summary>
        public abstract void Render(GameTime gameTime);

        public virtual void Dispose() => Target.Dispose();
    }
}
