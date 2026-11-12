using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ChangeLog.Core.Model;
using Geometry = ChangeLog.Core.Model.Geometry;

namespace ChangeLog.AddIn.UI.Controls
{
    /// <summary>One drawable object on the compare canvas.</summary>
    public sealed class CanvasItem
    {
        public ObjectKey Key { get; set; }
        public Geometry Shape { get; set; }

        /// <summary>null = unchanged.</summary>
        public ChangeAction? Action { get; set; }

        /// <summary>Old position of an edited object (drawn as a dashed ghost).</summary>
        public bool IsGhost { get; set; }

        public string Label { get; set; }
    }

    /// <summary>
    /// Renders one schematic page from captured geometry: unchanged objects in grey, added in
    /// green, removed in dashed red, edited in amber with a dashed ghost at the old position.
    /// Wheel = zoom at cursor, drag = pan, double-click = fit, click = select.
    /// </summary>
    public sealed class PageCanvas : FrameworkElement
    {
        public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
            nameof(Items), typeof(IList<CanvasItem>), typeof(PageCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((PageCanvas)d).Fit()));

        public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
            nameof(SelectedKey), typeof(ObjectKey?), typeof(PageCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static readonly DependencyProperty ShowUnchangedProperty = DependencyProperty.Register(
            nameof(ShowUnchanged), typeof(bool), typeof(PageCanvas),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly Typeface Face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        private double _scale = 1;
        private Vector _offset;
        private Point? _dragStart;
        private Vector _dragOffset;
        private bool _dragged;

        public PageCanvas()
        {
            ClipToBounds = true;
            Focusable = true;
            Cursor = Cursors.Hand;
            SizeChanged += (s, e) => Fit();
        }

        public IList<CanvasItem> Items
        {
            get { return (IList<CanvasItem>)GetValue(ItemsProperty); }
            set { SetValue(ItemsProperty, value); }
        }

        public ObjectKey? SelectedKey
        {
            get { return (ObjectKey?)GetValue(SelectedKeyProperty); }
            set { SetValue(SelectedKeyProperty, value); }
        }

        public bool ShowUnchanged
        {
            get { return (bool)GetValue(ShowUnchangedProperty); }
            set { SetValue(ShowUnchangedProperty, value); }
        }

        /// <summary>Zooms so all geometry fits with a margin.</summary>
        public void Fit()
        {
            var items = Items;
            if (items == null || items.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0)
            {
                InvalidateVisual();
                return;
            }

            var r = Bounds(items);
            var sx = (ActualWidth - 40) / Math.Max(1, r.Width);
            var sy = (ActualHeight - 40) / Math.Max(1, r.Height);
            _scale = Math.Max(0.01, Math.Min(sx, sy));
            // world (x, y) -> screen (x*s + ox, -y*s + oy)
            _offset = new Vector(20 - r.Left * _scale + (ActualWidth - 40 - r.Width * _scale) / 2,
                20 + r.Bottom * _scale + (ActualHeight - 40 - r.Height * _scale) / 2);
            InvalidateVisual();
        }

        private Point ToScreen(double x, double y) => new Point(x * _scale + _offset.X, -y * _scale + _offset.Y);

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Brushes.White, new Pen(Palette.Brush(Color.FromRgb(0xDA, 0xDD, 0xE3)), 1), new Rect(0, 0, ActualWidth, ActualHeight));
            var items = Items;
            if (items == null || items.Count == 0)
            {
                var dpi0 = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                var t = new FormattedText("No drawable objects on this page", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 12,
                    Palette.Brush(Palette.Unchanged), dpi0);
                dc.DrawText(t, new Point((ActualWidth - t.Width) / 2, (ActualHeight - t.Height) / 2));
                return;
            }

            // Unchanged first (background), then ghosts, then changes on top.
            var ordered = items.Where(i => i.Action == null && ShowUnchanged)
                .Concat(items.Where(i => i.IsGhost))
                .Concat(items.Where(i => i.Action != null && !i.IsGhost));
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            foreach (var it in ordered) Draw(dc, it, dpi);
        }

        private void Draw(DrawingContext dc, CanvasItem it, double dpi)
        {
            var g = it.Shape;
            if (g == null) return;
            var color = it.Action.HasValue ? Palette.For(it.Action.Value) : Palette.Unchanged;
            var selected = SelectedKey.HasValue && SelectedKey.Value.Equals(it.Key) && !it.IsGhost;
            var width = it.Action.HasValue ? 2.0 : 1.0;
            var pen = new Pen(Palette.Brush(color), selected ? width + 1.5 : width);
            if (it.IsGhost || it.Action == ChangeAction.Removed) pen.DashStyle = DashStyles.Dash;
            pen.Freeze();
            var fill = it.Action.HasValue && !it.IsGhost ? Palette.Soft(color) : null;

            if (selected)
            {
                var halo = new Pen(Palette.Soft(Palette.Accent), 10);
                halo.Freeze();
                DrawShape(dc, g, null, halo);
            }

            DrawShape(dc, g, fill, pen);

            if (it.Action.HasValue && !it.IsGhost && !string.IsNullOrEmpty(it.Label))
            {
                var anchor = ToScreen(Math.Max(g.X1, g.X2), Math.Max(g.Y1, g.Y2));
                var t = new FormattedText(it.Label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, Palette.Brush(color), dpi);
                dc.DrawRectangle(Palette.Brush(Color.FromArgb(0xDD, 0xFF, 0xFF, 0xFF)), null, new Rect(anchor.X + 3, anchor.Y - t.Height - 1, t.Width + 4, t.Height));
                dc.DrawText(t, new Point(anchor.X + 5, anchor.Y - t.Height - 1));
            }
        }

        private void DrawShape(DrawingContext dc, Geometry g, Brush fill, Pen pen)
        {
            switch (g.Shape)
            {
                case GeometryShape.Segment:
                    dc.DrawLine(pen, ToScreen(g.X1, g.Y1), ToScreen(g.X2, g.Y2));
                    break;
                case GeometryShape.Rect:
                {
                    var origin = ToScreen(g.X1, g.Y1);
                    var rect = new Rect(ToScreen(g.X1, g.Y2), ToScreen(g.X2, g.Y1));
                    var rotated = Math.Abs(g.Rotation) > 0.01;
                    if (rotated) dc.PushTransform(new RotateTransform(-g.Rotation, origin.X, origin.Y));
                    dc.DrawRectangle(fill, pen, rect);
                    if (rotated) dc.Pop();
                    break;
                }

                default:
                {
                    var p = ToScreen(g.X1, g.Y1);
                    dc.DrawEllipse(fill, pen, p, 4, 4);
                    break;
                }
            }
        }

        private static Rect Bounds(IEnumerable<CanvasItem> items)
        {
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var i in items.Where(i => i.Shape != null))
            {
                xs.Add(i.Shape.X1);
                xs.Add(i.Shape.X2);
                ys.Add(i.Shape.Y1);
                ys.Add(i.Shape.Y2);
            }

            if (xs.Count == 0) return new Rect(0, 0, 100, 100);
            return new Rect(new Point(xs.Min(), ys.Min()), new Point(xs.Max(), ys.Max()));
        }

        // ------------------------------------------------------------- interaction

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var p = e.GetPosition(this);
            var factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
            _offset = new Vector(p.X - (p.X - _offset.X) * factor, p.Y - (p.Y - _offset.Y) * factor);
            _scale *= factor;
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            if (e.ClickCount == 2)
            {
                Fit();
                return;
            }

            _dragStart = e.GetPosition(this);
            _dragOffset = _offset;
            _dragged = false;
            CaptureMouse();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragStart == null) return;
            var d = e.GetPosition(this) - _dragStart.Value;
            if (d.Length > 3) _dragged = true;
            _offset = _dragOffset + d;
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            if (_dragStart != null && !_dragged) SelectAt(e.GetPosition(this));
            _dragStart = null;
        }

        private void SelectAt(Point p)
        {
            var items = Items;
            if (items == null) return;
            CanvasItem best = null;
            var bestD = 12.0;
            foreach (var it in items.Where(i => !i.IsGhost && i.Shape != null && (ShowUnchanged || i.Action != null)))
            {
                var d = Distance(p, it.Shape);
                if (d < bestD || (Math.Abs(d - bestD) < 0.01 && it.Action != null))
                {
                    bestD = d;
                    best = it;
                }
            }

            SelectedKey = best?.Key;
        }

        private double Distance(Point p, Geometry g)
        {
            if (g.Shape == GeometryShape.Segment) return SegmentDistance(p, ToScreen(g.X1, g.Y1), ToScreen(g.X2, g.Y2));
            if (g.Shape == GeometryShape.Rect)
            {
                var r = new Rect(ToScreen(g.X1, g.Y2), ToScreen(g.X2, g.Y1));
                if (r.Contains(p)) return 0;
                var dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
                var dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
                return Math.Sqrt(dx * dx + dy * dy);
            }

            return (p - ToScreen(g.X1, g.Y1)).Length;
        }

        private static double SegmentDistance(Point p, Point a, Point b)
        {
            var ab = b - a;
            var len2 = ab.LengthSquared;
            if (len2 < 1e-9) return (p - a).Length;
            var t = Math.Max(0, Math.Min(1, Vector.Multiply(p - a, ab) / len2));
            return (p - (a + t * ab)).Length;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.F || e.Key == Key.Home)
            {
                Fit();
                e.Handled = true;
            }
        }
    }
}
