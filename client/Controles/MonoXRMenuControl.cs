using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;
using GameTime = Microsoft.Xna.Framework.GameTime;
using XnaColor = Microsoft.Xna.Framework.Color;
using XnaRectangle = Microsoft.Xna.Framework.Rectangle;

namespace MonoXR.Client
{
    /// <summary>
    /// Vertical list of <see cref="MonoXRControl"/>s with one selected (highlighted)
    /// item. Up/Down move the selection (wrapping, skipping disabled items);
    /// Increase/Decrease go to the selected control — a slider steps its value,
    /// a checkbox toggles. Wire it up from the hosting overlay with
    /// <c>OverlayNavigation.Navigated += menu.HandleNavigation;</c> and call
    /// <see cref="Update"/>/<see cref="Draw"/> from the overlay's own passes.
    /// Items are laid out automatically inside <see cref="MonoXRControl.Bounds"/>.
    /// </summary>
    public sealed class MonoXRMenuControl : MonoXRControl
    {
        private readonly List<MonoXRControl> _items = new();
        private int _selected = -1;
        private MonoXRControl? _pointerCapture; // item that claimed the current pointer gesture

        // Scrolling. When the items are taller than Bounds, a window of whole rows is shown from
        // _first (rows that do not fit entirely are left out rather than cut), with a scroll bar on
        // the right. Wheel, scroll bar and Up/Down navigation move it.
        private int _first;
        private bool _barDrag;
        private const int BarWidth = 8;
        private const int BarGutter = BarWidth + 8;
        private const int WheelRowsPerNotch = 2;
        private static readonly XnaColor BarTrack = new XnaColor(0x37, 0x37, 0x4E);

        public int ItemHeight { get; set; } = 64;
        public int ItemSpacing { get; set; } = 8;

        public IReadOnlyList<MonoXRControl> Items => _items;

        public MonoXRControl? SelectedItem =>
            _selected >= 0 && _selected < _items.Count ? _items[_selected] : null;

        public void Add(MonoXRControl item)
        {
            _items.Add(item);
            if (_selected < 0 && item.IsEnabled)
                _selected = _items.Count - 1;
            SyncSelection();
        }

        /// <summary>Selects <paramref name="item"/> (if it is in the menu and enabled).</summary>
        public void Select(MonoXRControl item)
        {
            int i = _items.IndexOf(item);
            if (i < 0 || !item.IsEnabled) return;
            _selected = i;
            SyncSelection();
        }

        public void Clear()
        {
            _items.Clear();
            _selected = -1;
            _pointerCapture = null;
            _barDrag = false;
        }

        private int HeightOf(MonoXRControl item) => item.PreferredHeight > 0 ? item.PreferredHeight : ItemHeight;

        /// <summary>True when the rows are taller than the area they are drawn in.</summary>
        public bool CanScroll
        {
            get
            {
                if (Bounds.Height <= 0 || _items.Count == 0) return false;
                int total = -ItemSpacing;
                foreach (var item in _items) total += HeightOf(item) + ItemSpacing;
                return total > Bounds.Height;
            }
        }

        /// <summary>One past the last row that fits entirely when the window starts at <paramref name="first"/>.</summary>
        private int EndFrom(int first)
        {
            int y = 0, i = first;
            while (i < _items.Count)
            {
                int h = HeightOf(_items[i]);
                if (y + h > Bounds.Height) break;
                y += h + ItemSpacing;
                i++;
            }
            return Math.Max(i, Math.Min(first + 1, _items.Count)); // always at least one row
        }

        /// <summary>The first row at which the window reaches the end of the list.</summary>
        private int MaxFirst
        {
            get
            {
                if (!CanScroll) return 0;
                int first = _items.Count - 1;
                while (first > 0 && EndFrom(first - 1) >= _items.Count) first--;
                return first;
            }
        }

        private void SetFirst(int first) => _first = Math.Clamp(first, 0, MaxFirst);

        /// <summary>Scroll so the row at <paramref name="index"/> is on show.</summary>
        public void EnsureVisible(int index)
        {
            if (!CanScroll || index < 0 || index >= _items.Count) { _first = 0; return; }
            _first = Math.Clamp(_first, 0, MaxFirst);
            if (index < _first) _first = index;
            while (index >= EndFrom(_first) && _first < MaxFirst) _first++;
        }

        private XnaRectangle BarTrackRect => new XnaRectangle(Bounds.Right - BarWidth, Bounds.Y, BarWidth, Bounds.Height);

        private XnaRectangle BarThumbRect
        {
            get
            {
                var track = BarTrackRect;
                int max = MaxFirst;
                int visible = Math.Max(1, EndFrom(_first) - _first);
                int h = Math.Max(24, track.Height * visible / Math.Max(visible, _items.Count));
                int range = Math.Max(1, track.Height - h);
                int y = track.Y + (max == 0 ? 0 : range * Math.Clamp(_first, 0, max) / max);
                return new XnaRectangle(track.X, y, track.Width, h);
            }
        }

        private void ScrollToBarY(int y, int grab)
        {
            var track = BarTrackRect;
            int range = Math.Max(1, track.Height - BarThumbRect.Height);
            float f = Math.Clamp((y - grab - track.Y) / (float)range, 0f, 1f);
            SetFirst((int)Math.Round(f * MaxFirst));
        }

        private int _barGrab;

        public void HandleNavigation(OverlayNavAction action)
        {
            switch (action)
            {
                case OverlayNavAction.Up: MoveSelection(-1); break;
                case OverlayNavAction.Down: MoveSelection(+1); break;
                case OverlayNavAction.PreviousControl: MoveSelection(-1); break;
                case OverlayNavAction.NextControl: MoveSelection(+1); break;
                case OverlayNavAction.Increase: SelectedItem?.Increase(); break;
                case OverlayNavAction.Decrease: SelectedItem?.Decrease(); break;
            }
        }

        // Forward adjust actions so a menu can itself sit inside another menu.
        public override void Increase() => SelectedItem?.Increase();
        public override void Decrease() => SelectedItem?.Decrease();

        /// <summary>Moves the selection by <paramref name="direction"/>, wrapping and skipping disabled items.</summary>
        public void MoveSelection(int direction)
        {
            if (_items.Count == 0) return;
            int start = _selected < 0 ? 0 : _selected;
            int i = start;
            for (int step = 0; step < _items.Count; step++)
            {
                i = (i + direction + _items.Count) % _items.Count;
                if (_items[i].IsEnabled) break;
            }
            _selected = i;
            SyncSelection();
        }

        /// <summary>
        /// Pointer press: selects the enabled item under the pointer and
        /// forwards the press to it. Returns true when that item captured the
        /// gesture (e.g. a slider bar or checkbox was hit).
        /// </summary>
        public override bool OnPointerPress(int x, int y)
        {
            LayoutItems();
            if (CanScroll)
            {
                var track = BarTrackRect;
                if (x >= track.X - 6 && x < track.Right + 6 && y >= track.Y && y < track.Bottom)
                {
                    var thumb = BarThumbRect;
                    _barGrab = y >= thumb.Y && y < thumb.Bottom ? y - thumb.Y : thumb.Height / 2;
                    _barDrag = true;
                    ScrollToBarY(y, _barGrab);
                    return true;
                }
            }
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (!item.IsEnabled || !item.Bounds.Contains(x, y)) continue;
                _selected = i;
                SyncSelection();
                if (!item.OnPointerPress(x, y)) return false;
                _pointerCapture = item;
                return true;
            }
            return false;
        }

        public override void OnPointerDrag(int x, int y)
        {
            if (_barDrag) { ScrollToBarY(y, _barGrab); return; }
            _pointerCapture?.OnPointerDrag(x, y);
        }

        /// <summary>
        /// True when the pointer is over an item that takes the wheel (a slider), which comes before
        /// anything else; otherwise over the list while it can scroll.
        /// </summary>
        public override bool WantsWheel(int x, int y)
        {
            LayoutItems();
            foreach (var item in _items)
                if (item.Bounds.Contains(x, y) && item.WantsWheel(x, y)) return true;
            return CanScroll && Bounds.Contains(x, y);
        }

        public override bool OnPointerWheel(int x, int y, int delta)
        {
            LayoutItems();
            foreach (var item in _items)
                if (item.Bounds.Contains(x, y) && item.WantsWheel(x, y))
                    return item.OnPointerWheel(x, y, delta);
            if (!CanScroll || !Bounds.Contains(x, y)) return false;
            int rows = (int)Math.Round(delta / 120.0 * WheelRowsPerNotch);
            if (rows == 0) rows = delta > 0 ? 1 : -1;
            SetFirst(_first - rows); // wheel up = towards the top
            return true;
        }

        public override void Update(GameTime gameTime)
        {
            LayoutItems();
            foreach (var item in _items)
                item.Update(gameTime);
        }

        public override void Draw(SpriteBatch spriteBatch, SpriteFont font, Texture2D white)
        {
            LayoutItems();
            foreach (var item in _items)
                if (item.Bounds.Height > 0) item.Draw(spriteBatch, font, white);

            if (CanScroll)
            {
                var track = BarTrackRect;
                MonoXRDraw.RoundedRect(spriteBatch, track, BarWidth / 2, BarTrack);
                MonoXRDraw.RoundedRect(spriteBatch, BarThumbRect, BarWidth / 2, AccentColor * (_barDrag ? 1f : 0.7f));
            }
        }

        /// <summary>Stacks items top-to-bottom inside <see cref="MonoXRControl.Bounds"/>; rows scrolled off or not fitting get an empty rectangle.</summary>
        private void LayoutItems()
        {
            bool scroll = CanScroll;
            if (!scroll) _first = 0;
            else _first = Math.Clamp(_first, 0, MaxFirst);
            int width = Bounds.Width - (scroll ? BarGutter : 0);
            int end = scroll ? EndFrom(_first) : _items.Count;

            int y = Bounds.Y;
            for (int i = 0; i < _items.Count; i++)
            {
                var item = _items[i];
                if (i < _first || i >= end) { item.Bounds = XnaRectangle.Empty; continue; }
                int h = HeightOf(item);
                item.Bounds = new XnaRectangle(Bounds.X, y, width, h);
                y += h + ItemSpacing;
            }
        }

        private void SyncSelection()
        {
            for (int i = 0; i < _items.Count; i++)
                _items[i].IsSelected = i == _selected;
            EnsureVisible(_selected); // arrow keys / controller moving to a row off screen scroll to it
        }
    }
}
