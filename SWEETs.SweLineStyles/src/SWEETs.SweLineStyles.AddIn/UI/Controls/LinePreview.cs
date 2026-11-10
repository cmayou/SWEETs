using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using SWEETs.SweLineStyles.AddIn.Core;

namespace SWEETs.SweLineStyles.AddIn.UI.Controls
{
    /// <summary>Tessellated shapes by name, shared by every preview. Bump <see cref="Revision"/> after changes.</summary>
    public static class ShapeCatalog
    {
        private static Dictionary<string, List<Pt[]>> _shapes = new Dictionary<string, List<Pt[]>>(StringComparer.OrdinalIgnoreCase);
        public static int Revision { get; private set; }

        public static void Set(IEnumerable<ShapeDefinition> shapes)
        {
            var map = new Dictionary<string, List<Pt[]>>(StringComparer.OrdinalIgnoreCase);
            var byNumber = shapes.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First().Bytes);
            foreach (var s in shapes)
            {
                var polys = new List<Pt[]>();
                try
                {
                    foreach (var p in ShapeInterpreter.Decode(s.Bytes, n => byNumber.TryGetValue(n, out var b) ? b : null))
                        p.Tessellate(polys);
                }
                catch (FormatException) { /* malformed bytes: shown empty; the shape card reports the error */ }
                map[s.Name ?? ""] = polys;
            }
            _shapes = map;
            Revision++;
        }

        /// <summary>Adds or replaces one entry (the shape editor's live preview) without touching the others.</summary>
        public static void SetPreview(string key, IEnumerable<ShapePrimitive> primitives)
        {
            var polys = new List<Pt[]>();
            foreach (var p in primitives ?? Enumerable.Empty<ShapePrimitive>()) p.Tessellate(polys);
            var map = new Dictionary<string, List<Pt[]>>(_shapes, StringComparer.OrdinalIgnoreCase) { [key] = polys };
            _shapes = map;
            Revision++;
        }

        public static List<Pt[]> Get(string name) => name != null && _shapes.TryGetValue(name, out var p) ? p : null;
    }

    /// <summary>
    /// Draws a line-style pattern along a horizontal line: dashes, gaps, dots, embedded shapes (from
    /// <see cref="ShapeCatalog"/>) and embedded text, with each element's S/R/A/X/Y transform applied.
    /// Scale is automatic (about three pattern repeats, limited so shapes and text fit the height) times <see cref="Zoom"/>.
    /// </summary>
    public sealed class LinePreview : FrameworkElement
    {
        public static readonly DependencyProperty PatternProperty = DependencyProperty.Register(
            nameof(Pattern), typeof(string), typeof(LinePreview), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(
            nameof(Revision), typeof(int), typeof(LinePreview), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
            nameof(Zoom), typeof(double), typeof(LinePreview), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke), typeof(Brush), typeof(LinePreview), new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness), typeof(double), typeof(LinePreview), new FrameworkPropertyMetadata(1.25, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty ShowScaleProperty = DependencyProperty.Register(
            nameof(ShowScale), typeof(bool), typeof(LinePreview), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public string Pattern { get => (string)GetValue(PatternProperty); set => SetValue(PatternProperty, value); }
        /// <summary>Bind to the view model's shape revision so previews redraw when shapes change.</summary>
        public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }
        public double Zoom { get => (double)GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        public double StrokeThickness { get => (double)GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
        /// <summary>Draws a one-unit scale bar in the lower-left corner.</summary>
        public bool ShowScale { get => (bool)GetValue(ShowScaleProperty); set => SetValue(ShowScaleProperty, value); }

        private string _parsedFor;
        private List<PatternElement> _parsed;

        private static readonly Typeface TextFace = new Typeface("Segoe UI");

        protected override void OnRender(DrawingContext dc)
        {
            double w = ActualWidth, h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-testable background
            if (w < 4 || h < 4) return;

            if (!ReferenceEquals(_parsedFor, Pattern))
            {
                _parsedFor = Pattern;
                try { _parsed = LinePattern.Parse(Pattern); } catch (FormatException) { _parsed = null; }
            }
            var pen = new Pen(Stroke, StrokeThickness) { StartLineCap = PenLineCap.Flat, EndLineCap = PenLineCap.Flat };
            pen.Freeze();
            double y0 = Math.Round(h / 2) + 0.5;

            if (_parsed == null || LinePattern.Period(_parsed) <= 0)
            {
                var bad = new Pen(new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)), 1) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
                dc.DrawLine(bad, new Point(4, y0), new Point(w - 4, y0));
                return;
            }

            double period = LinePattern.Period(_parsed);
            double extent = 0;
            foreach (var e in _parsed.Where(x => x.IsComplex))
            {
                double reach = Math.Abs(e.Y) + Math.Abs(e.X);
                if (e.Kind == PatternKind.Shape)
                {
                    var polys = ShapeCatalog.Get(e.Content);
                    if (polys != null)
                        foreach (var poly in polys)
                            foreach (var p in poly) reach = Math.Max(reach, Math.Abs(e.Y) + p.Length * e.Scale);
                }
                else reach = Math.Max(reach, Math.Abs(e.Y) + e.Scale * 1.2);
                extent = Math.Max(extent, reach);
            }

            double pad = 6;
            double usable = w - 2 * pad;
            double unit = usable / (period * 3);
            if (extent > 0) unit = Math.Min(unit, h * 0.42 / extent);
            unit *= Math.Max(0.05, Zoom);

            double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));

            double pos = 0, end = usable / unit;
            int guard = 0;
            while (pos < end && guard++ < 5000)
            {
                foreach (var e in _parsed)
                {
                    double x = pad + pos * unit;
                    switch (e.Kind)
                    {
                        case PatternKind.Dash:
                            dc.DrawLine(pen, new Point(x, y0), new Point(pad + Math.Min(pos + e.Length, end) * unit, y0));
                            pos += e.Length;
                            break;
                        case PatternKind.Gap:
                            pos += e.Length;
                            break;
                        case PatternKind.Dot:
                            dc.DrawEllipse(Stroke, null, new Point(x, y0), StrokeThickness * 0.8, StrokeThickness * 0.8);
                            break;
                        case PatternKind.Shape:
                            DrawShape(dc, pen, e, x, y0, unit);
                            break;
                        case PatternKind.Text:
                            DrawText(dc, e, x, y0, unit, pixelsPerDip);
                            break;
                    }
                    if (pos >= end) break;
                }
            }

            if (ShowScale)
            {
                var sp = new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), 1);
                double len = unit;
                string label = "1 unit";
                while (len > usable / 2) { len /= 10; label = label == "1 unit" ? "0.1 unit" : "0.01 unit"; }
                double by = h - 6;
                dc.DrawLine(sp, new Point(pad, by), new Point(pad + len, by));
                dc.DrawLine(sp, new Point(pad, by - 3), new Point(pad, by + 1));
                dc.DrawLine(sp, new Point(pad + len, by - 3), new Point(pad + len, by + 1));
                var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TextFace, 10, sp.Brush, pixelsPerDip);
                dc.DrawText(ft, new Point(pad + len + 6, by - ft.Height + 3));
            }
            dc.Pop();
        }

        private static double Rad(double deg) => deg * Math.PI / 180;

        private void DrawShape(DrawingContext dc, Pen pen, PatternElement e, double x, double y0, double unit)
        {
            var polys = ShapeCatalog.Get(e.Content);
            if (polys == null)
            {
                // Unknown shape: a small marker so the position is still visible.
                var mark = new Pen(new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C)), 1);
                dc.DrawRectangle(null, mark, new Rect(x - 3, y0 - 3, 6, 6));
                return;
            }
            double s = e.Scale, r = Rad(e.Rotation), cos = Math.Cos(r), sin = Math.Sin(r);
            Point Map(Pt p)
            {
                double px = p.X * s, py = p.Y * s;
                double rx = px * cos - py * sin + e.X, ry = px * sin + py * cos + e.Y;
                return new Point(x + rx * unit, y0 - ry * unit);
            }
            var geo = new StreamGeometry();
            using (var g = geo.Open())
                foreach (var poly in polys)
                {
                    if (poly.Length < 2) continue;
                    g.BeginFigure(Map(poly[0]), false, false);
                    g.PolyLineTo(poly.Skip(1).Select(Map).ToList(), true, true);
                }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);
        }

        private void DrawText(DrawingContext dc, PatternElement e, double x, double y0, double unit, double pixelsPerDip)
        {
            if (string.IsNullOrEmpty(e.Content)) return;
            // S is the text height in drawing units (style height 0). Segoe UI cap height ≈ 0.7 em.
            double em = Math.Max(1, e.Scale * unit / 0.7);
            var ft = new FormattedText(e.Content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, TextFace, em, Stroke, pixelsPerDip);
            var origin = new Point(x + e.X * unit, y0 - e.Y * unit);     // text insertion = baseline, left
            dc.PushTransform(new RotateTransform(-e.Rotation, origin.X, origin.Y));
            dc.DrawText(ft, new Point(origin.X, origin.Y - ft.Baseline));
            dc.Pop();
        }
    }
}
