using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SWEETs.SweLineStyles.AddIn.Core;

namespace SWEETs.SweLineStyles.AddIn.UI.Controls
{
    /// <summary>Thumbnail of a shape, fitted to the control, with the insertion point and line axis marked.</summary>
    public sealed class ShapePreview : FrameworkElement
    {
        public static readonly DependencyProperty PrimitivesProperty = DependencyProperty.Register(
            nameof(Primitives), typeof(IList<ShapePrimitive>), typeof(ShapePreview), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(ShapePreview), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

        public IList<ShapePrimitive> Primitives { get => (IList<ShapePrimitive>)GetValue(PrimitivesProperty); set => SetValue(PrimitivesProperty, value); }
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

        private static readonly Pen AxisPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xB8, 0xBD, 0xC7)), 1) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) });
        private static readonly Pen OriginPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x1D, 0x4E, 0xD8)), 1.25));

        private static Pen Frozen(Pen p) { p.Freeze(); return p; }

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            var prims = Primitives;
            if (prims == null || w < 8 || h < 8) return;

            Bounds.Of(prims, out double minX, out double minY, out double maxX, out double maxY);
            // Include the insertion point so its position relative to the geometry is visible.
            minX = Math.Min(minX, 0); minY = Math.Min(minY, 0); maxX = Math.Max(maxX, 0); maxY = Math.Max(maxY, 0);
            double bw = Math.Max(maxX - minX, 1e-6), bh = Math.Max(maxY - minY, 1e-6);
            double pad = 8;
            double k = Math.Min((w - 2 * pad) / bw, (h - 2 * pad) / bh);
            double ox = (w - bw * k) / 2 - minX * k, oy = (h + bh * k) / 2 + minY * k;
            Point Map(Pt p) => new Point(ox + p.X * k, oy - p.Y * k);

            dc.DrawLine(AxisPen, new Point(0, oy), new Point(w, oy));

            var pen = new Pen(Stroke, 1.5) { LineJoin = PenLineJoin.Round };
            pen.Freeze();
            var polys = new List<Pt[]>();
            foreach (var p in prims) p.Tessellate(polys);
            var geo = new StreamGeometry();
            using (var g = geo.Open())
                foreach (var poly in polys.Where(x => x.Length > 1))
                {
                    g.BeginFigure(Map(poly[0]), false, false);
                    g.PolyLineTo(poly.Skip(1).Select(Map).ToList(), true, true);
                }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);

            var o = Map(new Pt(0, 0));
            dc.DrawEllipse(null, OriginPen, o, 3, 3);
        }
    }
}
