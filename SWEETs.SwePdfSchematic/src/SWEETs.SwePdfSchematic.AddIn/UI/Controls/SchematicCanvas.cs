using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using SWEETs.SwePdfSchematic.AddIn.UI.ViewModels;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.AddIn.UI.Controls
{
    /// <summary>
    /// Local vector preview of a PDF page, drawn from the geometry the reader extracted (no rasterizer needed),
    /// with recognition overlays. Wheel = zoom at cursor, drag = pan, click = select symbol, F = fit.
    /// </summary>
    public sealed class SchematicCanvas : FrameworkElement
    {
        public static readonly DependencyProperty PageProperty = DependencyProperty.Register(nameof(Page), typeof(PageAnalysis), typeof(SchematicCanvas),
            new FrameworkPropertyMetadata(null, (d, e) => ((SchematicCanvas)d).OnPageChanged()));
        public static readonly DependencyProperty CardsProperty = DependencyProperty.Register(nameof(Cards), typeof(IEnumerable), typeof(SchematicCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty SelectedCardProperty = DependencyProperty.Register(nameof(SelectedCard), typeof(SymbolCardViewModel), typeof(SchematicCanvas),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((SchematicCanvas)d).OnSelectedChanged()));
        public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(nameof(Revision), typeof(int), typeof(SchematicCanvas),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public PageAnalysis Page { get => (PageAnalysis)GetValue(PageProperty); set => SetValue(PageProperty, value); }
        public IEnumerable Cards { get => (IEnumerable)GetValue(CardsProperty); set => SetValue(CardsProperty, value); }
        public SymbolCardViewModel SelectedCard { get => (SymbolCardViewModel)GetValue(SelectedCardProperty); set => SetValue(SelectedCardProperty, value); }
        /// <summary>Bump to force an overlay redraw after review state changes.</summary>
        public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

        private static readonly Pen LinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x6B, 0x72, 0x80)), 1));
        private static readonly Pen WirePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x11, 0x18, 0x27)), 1.4));
        private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x37, 0x41, 0x51)));
        private static readonly Brush SheetBrush = Brushes.White;
        private static readonly Brush ExcludedBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x10, 0x11, 0x18, 0x27)));
        private static readonly Typeface Face = new Typeface("Segoe UI");

        private double _scale = 1;      // device px per mm
        private Vector _offset;         // device px
        private Point? _dragStart;
        private Vector _dragOffset;
        private bool _dragged;
        private DrawingGroup _base;     // cached page vectors (mm space)

        public SchematicCanvas()
        {
            Focusable = true;
            ClipToBounds = true;
            AutomationProperties.SetName(this, "Schematic page preview");
            SizeChanged += (s, e) => { if (e.PreviousSize.Width < 1) Fit(); };
        }

        private static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

        private void OnPageChanged()
        {
            _base = BuildBase(Page);
            Fit();
        }

        private void OnSelectedChanged()
        {
            var c = SelectedCard;
            if (c == null || Page == null || c.Page != Page.PageNumber) return;
            // keep the selected symbol in view
            var r = ToScreen(c.Model.Bounds);
            if (r.Left < 0 || r.Top < 0 || r.Right > ActualWidth || r.Bottom > ActualHeight)
            {
                var center = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
                _offset += new Vector(ActualWidth / 2 - center.X, ActualHeight / 2 - center.Y);
            }
            InvalidateVisual();
        }

        public void Fit()
        {
            if (Page == null || ActualWidth < 1) { InvalidateVisual(); return; }
            double w = Page.WidthMm, h = Page.HeightMm;
            _scale = Math.Min((ActualWidth - 24) / w, (ActualHeight - 24) / h);
            _offset = new Vector((ActualWidth - w * _scale) / 2, (ActualHeight - h * _scale) / 2);
            InvalidateVisual();
        }

        private static DrawingGroup BuildBase(PageAnalysis page)
        {
            var g = new DrawingGroup();
            if (page == null) return g;
            using (var dc = g.Open())
            {
                var wireSegs = new HashSet<Segment>(page.Nets.SelectMany(n => n.Segments));
                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                    foreach (var s in page.AllSegments)
                    {
                        ctx.BeginFigure(new Point(s.A.X, page.HeightMm - s.A.Y), false, false);
                        ctx.LineTo(new Point(s.B.X, page.HeightMm - s.B.Y), true, false);
                    }
                geo.Freeze();
                dc.DrawGeometry(null, new Pen(LinePen.Brush, 0.18), geo);

                var wires = new StreamGeometry();
                using (var ctx = wires.Open())
                    foreach (var s in wireSegs)
                    {
                        ctx.BeginFigure(new Point(s.A.X, page.HeightMm - s.A.Y), false, false);
                        ctx.LineTo(new Point(s.B.X, page.HeightMm - s.B.Y), true, false);
                    }
                wires.Freeze();
                dc.DrawGeometry(null, new Pen(WirePen.Brush, 0.3), wires);

                foreach (var d in page.JunctionDots) dc.DrawEllipse(WirePen.Brush, null, new Point(d.X, page.HeightMm - d.Y), 0.6, 0.6);
                foreach (var r in page.ExcludedRegions) dc.DrawRectangle(ExcludedBrush, null, new System.Windows.Rect(r.Left, page.HeightMm - r.Top, r.Width, r.Height));

                foreach (var t in page.AllTexts)
                {
                    double size = Math.Max(1.2, t.FontSize > 0 ? t.FontSize : t.Bounds.Height * 0.9);
                    var ft = new FormattedText(t.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, TextBrush, 1.0);
                    var origin = new Point(t.Bounds.Left, page.HeightMm - t.Bounds.Top);
                    if (t.Direction == Core.Geometry.TextDirection.Rotate90 || t.Direction == Core.Geometry.TextDirection.Rotate270)
                    {
                        dc.PushTransform(new RotateTransform(-90, t.Bounds.Left, page.HeightMm - t.Bounds.Bottom));
                        dc.DrawText(ft, new Point(t.Bounds.Left, page.HeightMm - t.Bounds.Bottom - ft.Height));
                        dc.Pop();
                    }
                    else dc.DrawText(ft, origin);
                }
            }
            g.Freeze();
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xEF)), null, new System.Windows.Rect(0, 0, ActualWidth, ActualHeight));
            var page = Page;
            if (page == null) return;

            dc.PushTransform(new TranslateTransform(_offset.X, _offset.Y));
            dc.PushTransform(new ScaleTransform(_scale, _scale));
            dc.DrawRectangle(SheetBrush, null, new System.Windows.Rect(0, 0, page.WidthMm, page.HeightMm));
            if (_base != null) dc.DrawDrawing(_base);
            dc.Pop(); dc.Pop();

            if (page.IsRasterOnly) return;

            // overlays in device space so strokes stay crisp at any zoom
            foreach (var card in (Cards ?? new SymbolCardViewModel[0]).OfType<SymbolCardViewModel>().Where(c => c.Page == page.PageNumber))
            {
                bool sel = ReferenceEquals(card, SelectedCard);
                var r = ToScreen(card.Model.Bounds.Inflate(1.0));
                var brush = card.StateBrush;
                var fill = new SolidColorBrush(((SolidColorBrush)brush).Color) { Opacity = sel ? 0.16 : 0.07 };
                dc.DrawRoundedRectangle(fill, new Pen(brush, sel ? 2.5 : 1.25) { DashStyle = card.State == ReviewState.Ignored ? DashStyles.Dash : DashStyles.Solid }, r, 3, 3);
                foreach (var pin in card.Model.Pins)
                    dc.DrawEllipse(brush, null, ToScreen(pin.Location), 2.5, 2.5);

                if (sel || _scale > 3)
                {
                    var label = new FormattedText(card.Tag + " · " + card.KindText, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 11, Brushes.White, 1.0);
                    var lr = new System.Windows.Rect(r.Left, r.Top - label.Height - 6, label.Width + 10, label.Height + 4);
                    dc.DrawRoundedRectangle(brush, null, lr, 3, 3);
                    dc.DrawText(label, new Point(lr.Left + 5, lr.Top + 2));
                }
            }

            foreach (var l in page.OffPageLinks)
            {
                var p = ToScreen(l.Location);
                var b = l.PairedLinkId != null ? Palette.Accent : (l.TargetPage.HasValue ? Palette.Warning : Palette.Neutral);
                dc.DrawEllipse(null, new Pen(b, 1.75), p, 6, 6);
                if (_scale > 2 && (l.XrefText ?? l.Label) != null)
                {
                    var ft = new FormattedText((l.PairedLinkId != null ? "↔ " : "") + (l.XrefText ?? l.Label), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, b, 1.0);
                    dc.DrawText(ft, new Point(p.X + 8, p.Y - ft.Height / 2));
                }
            }
        }

        private Point ToScreen(Pt p) => new Point(_offset.X + p.X * _scale, _offset.Y + (Page.HeightMm - p.Y) * _scale);
        private System.Windows.Rect ToScreen(Core.Geometry.Rect r)
        {
            var a = ToScreen(new Pt(r.Left, r.Top));
            return new System.Windows.Rect(a.X, a.Y, Math.Max(2, r.Width * _scale), Math.Max(2, r.Height * _scale));
        }
        private Pt ToPage(Point p) => new Pt((p.X - _offset.X) / _scale, Page.HeightMm - (p.Y - _offset.Y) / _scale);

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            var at = e.GetPosition(this);
            double factor = e.Delta > 0 ? 1.2 : 1 / 1.2;
            double newScale = Math.Max(0.2, Math.Min(60, _scale * factor));
            factor = newScale / _scale;
            _offset = new Vector(at.X - (at.X - _offset.X) * factor, at.Y - (at.Y - _offset.Y) * factor);
            _scale = newScale;
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            _dragStart = e.GetPosition(this);
            _dragOffset = _offset;
            _dragged = false;
            CaptureMouse();
            e.Handled = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragStart == null) return;
            var d = e.GetPosition(this) - _dragStart.Value;
            if (d.Length > 3) _dragged = true;
            if (_dragged) { _offset = _dragOffset + d; Cursor = Cursors.SizeAll; InvalidateVisual(); }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            Cursor = null;
            if (!_dragged && Page != null)
            {
                var p = ToPage(e.GetPosition(this));
                var hit = (Cards ?? new SymbolCardViewModel[0]).OfType<SymbolCardViewModel>()
                    .Where(c => c.Page == Page.PageNumber && c.Model.Bounds.Inflate(1.5).Contains(p))
                    .OrderBy(c => c.Model.Bounds.Area).FirstOrDefault();
                if (hit != null) SelectedCard = hit;
            }
            _dragStart = null;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.F) { Fit(); e.Handled = true; }
            else if (e.Key == Key.Add || e.Key == Key.OemPlus) { _scale *= 1.2; InvalidateVisual(); e.Handled = true; }
            else if (e.Key == Key.Subtract || e.Key == Key.OemMinus) { _scale /= 1.2; InvalidateVisual(); e.Handled = true; }
        }
    }
}
