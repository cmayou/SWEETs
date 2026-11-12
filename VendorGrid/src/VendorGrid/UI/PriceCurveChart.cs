using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VendorGrid.Core;

namespace VendorGrid.UI
{
    /// <summary>
    /// Unit price against order quantity (logarithmic X axis), one step line per vendor listing.
    /// Hover a marker for its price break; click anywhere to set the order quantity.
    /// The same numbers are available as text in the volume discount matrix below the chart.
    /// </summary>
    public sealed class PriceCurveChart : FrameworkElement
    {
        public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
            "Series", typeof(IEnumerable), typeof(PriceCurveChart), new FrameworkPropertyMetadata(null, OnSeriesChanged));

        public static readonly DependencyProperty CurrentQuantityProperty = DependencyProperty.Register(
            "CurrentQuantity", typeof(int), typeof(PriceCurveChart),
            new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty QuantityCommandProperty = DependencyProperty.Register(
            "QuantityCommand", typeof(ICommand), typeof(PriceCurveChart), new PropertyMetadata(null));

        static readonly Brush Surface = Frozen("#FFFFFF");
        static readonly Brush GridLine = Frozen("#E5E7EB");
        static readonly Brush AxisInk = Frozen("#4B5563");
        static readonly Brush CurrentInk = Frozen("#111827");
        static readonly Pen GridPen = MakePen("#E5E7EB", 1, null);
        static readonly Pen AxisPen = MakePen("#9CA3AF", 1, null);
        static readonly Pen CurrentPen = MakePen("#111827", 1.25, new DoubleCollection { 4, 3 });
        static readonly Typeface Face = new Typeface("Segoe UI");
        static readonly Typeface FaceBold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        const double Left = 66, Right = 22, Top = 16, Bottom = 38;

        readonly List<Hit> _hits = new List<Hit>();
        Hit? _hover;
        INotifyCollectionChanged _watched;

        struct Hit
        {
            public Point At;
            public ChartSeriesVM Series;
            public ChartPoint Break;
        }

        public PriceCurveChart()
        {
            MinHeight = 220;
            Focusable = false;
            ClipToBounds = true;
            Cursor = Cursors.Cross;
        }

        public IEnumerable Series { get { return (IEnumerable)GetValue(SeriesProperty); } set { SetValue(SeriesProperty, value); } }
        public int CurrentQuantity { get { return (int)GetValue(CurrentQuantityProperty); } set { SetValue(CurrentQuantityProperty, value); } }
        public ICommand QuantityCommand { get { return (ICommand)GetValue(QuantityCommandProperty); } set { SetValue(QuantityCommandProperty, value); } }

        // ---- data wiring --------------------------------------------------------------------------

        static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((PriceCurveChart)d).Rewire(e.OldValue as IEnumerable, e.NewValue as IEnumerable);
        }

        void Rewire(IEnumerable oldSeries, IEnumerable newSeries)
        {
            if (_watched != null) _watched.CollectionChanged -= SeriesCollectionChanged;
            _watched = null;
            if (oldSeries != null) foreach (var s in oldSeries.OfType<INotifyPropertyChanged>()) s.PropertyChanged -= SeriesItemChanged;

            if (newSeries != null)
            {
                foreach (var s in newSeries.OfType<INotifyPropertyChanged>()) s.PropertyChanged += SeriesItemChanged;
                _watched = newSeries as INotifyCollectionChanged;
                if (_watched != null) _watched.CollectionChanged += SeriesCollectionChanged;
            }
            InvalidateVisual();
        }

        void SeriesCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null) foreach (var s in e.OldItems.OfType<INotifyPropertyChanged>()) s.PropertyChanged -= SeriesItemChanged;
            if (e.NewItems != null) foreach (var s in e.NewItems.OfType<INotifyPropertyChanged>()) s.PropertyChanged += SeriesItemChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset && _watched != null)
            {
                // Clear() raises Reset without the old items; re-subscribe to whatever is there now.
                foreach (var s in ((IEnumerable)_watched).OfType<INotifyPropertyChanged>())
                {
                    s.PropertyChanged -= SeriesItemChanged;
                    s.PropertyChanged += SeriesItemChanged;
                }
            }
            _hover = null;
            InvalidateVisual();
        }

        void SeriesItemChanged(object sender, PropertyChangedEventArgs e) { _hover = null; InvalidateVisual(); }

        List<ChartSeriesVM> Visible()
        {
            var all = Series;
            if (all == null) return new List<ChartSeriesVM>();
            return all.OfType<ChartSeriesVM>().Where(s => s.IsVisible && s.Points != null && s.Points.Count > 0).ToList();
        }

        // ---- scales -------------------------------------------------------------------------------

        sealed class Scale
        {
            public Rect Plot;
            public double LogMax;
            public decimal YMax;
            public double X(int qty) { return Plot.Left + Math.Log10(Math.Max(1, qty)) / LogMax * Plot.Width; }
            public double Y(decimal price) { return Plot.Bottom - (double)(price / YMax) * Plot.Height; }
            public int QtyAt(double x)
            {
                var t = Math.Max(0, Math.Min(1, (x - Plot.Left) / Plot.Width));
                return (int)Math.Max(1, Math.Round(Math.Pow(10, t * LogMax)));
            }
            public int XMax { get { return (int)Math.Round(Math.Pow(10, LogMax)); } }
        }

        Scale BuildScale(List<ChartSeriesVM> series)
        {
            int maxQty = Math.Max(CurrentQuantity, 100);
            decimal maxPrice = 0m;
            foreach (var s in series)
                foreach (var p in s.Points)
                {
                    if (p.Quantity > maxQty) maxQty = p.Quantity;
                    if (p.UnitPrice > maxPrice) maxPrice = p.UnitPrice;
                }
            if (maxPrice <= 0) maxPrice = 1m;

            var sc = new Scale
            {
                Plot = new Rect(Left, Top, Math.Max(10, ActualWidth - Left - Right), Math.Max(10, ActualHeight - Top - Bottom)),
                LogMax = Math.Ceiling(Math.Log10(maxQty)),
                YMax = NiceCeiling(maxPrice * 1.08m)
            };
            if (sc.LogMax < 2) sc.LogMax = 2;
            return sc;
        }

        static decimal NiceCeiling(decimal v)
        {
            if (v <= 0) return 1m;
            double exp = Math.Floor(Math.Log10((double)v));
            decimal unit = (decimal)Math.Pow(10, exp - 1);
            if (unit <= 0) unit = 0.0001m;
            return Math.Ceiling(v / unit) * unit;
        }

        // ---- rendering ----------------------------------------------------------------------------

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            var size = new Size(ActualWidth, ActualHeight);
            dc.DrawRectangle(Surface, null, new Rect(size));
            _hits.Clear();

            var series = Visible();
            if (series.Count == 0 || size.Width < 120 || size.Height < 100)
            {
                Text(dc, Loc.T("No priced listings to chart yet."), size.Width / 2, size.Height / 2 - 8, 13, AxisInk, false, TextAlignment.Center);
                return;
            }

            var sc = BuildScale(series);
            var currency = series[0].Currency;
            DrawGrid(dc, sc, currency);

            foreach (var s in series) DrawSeries(dc, sc, s);
            DrawCurrentQuantity(dc, sc);
            DrawHover(dc, sc, currency);
        }

        void DrawGrid(DrawingContext dc, Scale sc, string currency)
        {
            // Y grid (price)
            const int ticks = 5;
            for (int i = 0; i <= ticks; i++)
            {
                decimal v = sc.YMax * i / ticks;
                double y = sc.Y(v);
                dc.DrawLine(i == 0 ? AxisPen : GridPen, new Point(sc.Plot.Left, y), new Point(sc.Plot.Right, y));
                Text(dc, FormatPrice(v, currency), sc.Plot.Left - 8, y - 8, 11, AxisInk, false, TextAlignment.Right);
            }

            // X grid (quantity): label every decade, light tick at 2 and 5.
            int decades = (int)sc.LogMax;
            for (int d = 0; d <= decades; d++)
            {
                int qty = (int)Math.Round(Math.Pow(10, d));
                double x = sc.X(qty);
                dc.DrawLine(GridPen, new Point(x, sc.Plot.Top), new Point(x, sc.Plot.Bottom));
                Text(dc, Money.Count(qty), x, sc.Plot.Bottom + 6, 11, AxisInk, false, TextAlignment.Center);
                if (d < decades)
                    foreach (var m in new[] { 2, 5 })
                    {
                        double xm = sc.X(qty * m);
                        dc.DrawLine(AxisPen, new Point(xm, sc.Plot.Bottom), new Point(xm, sc.Plot.Bottom + 3));
                    }
            }
            dc.DrawLine(AxisPen, new Point(sc.Plot.Left, sc.Plot.Top), new Point(sc.Plot.Left, sc.Plot.Bottom));
            Text(dc, Loc.T("Order quantity (log scale)"), sc.Plot.Left + sc.Plot.Width / 2, sc.Plot.Bottom + 21, 11, AxisInk, false, TextAlignment.Center);
        }

        void DrawSeries(DrawingContext dc, Scale sc, ChartSeriesVM s)
        {
            var pts = s.Points.OrderBy(p => p.Quantity).ToList();
            var pen = new Pen(s.Brush, 2) { LineJoin = PenLineJoin.Round };
            if (s.Dash == 1) pen.DashStyle = new DashStyle(new DoubleCollection { 6, 3 }, 0);
            else if (s.Dash == 2) { pen.DashStyle = new DashStyle(new DoubleCollection { 1.2, 3 }, 0); pen.DashCap = PenLineCap.Round; }
            pen.Freeze();
            var markerPen = new Pen(s.Brush, 2);
            markerPen.Freeze();

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(sc.X(pts[0].Quantity), sc.Y(pts[0].UnitPrice)), false, false);
                for (int i = 0; i < pts.Count; i++)
                {
                    double y = sc.Y(pts[i].UnitPrice);
                    double xEnd = i + 1 < pts.Count ? sc.X(pts[i + 1].Quantity) : sc.X(sc.XMax);
                    ctx.LineTo(new Point(xEnd, y), true, false);
                    if (i + 1 < pts.Count) ctx.LineTo(new Point(xEnd, sc.Y(pts[i + 1].UnitPrice)), true, false);
                }
            }
            geo.Freeze();
            dc.DrawGeometry(null, pen, geo);

            foreach (var p in pts)
            {
                var at = new Point(sc.X(p.Quantity), sc.Y(p.UnitPrice));
                DrawMarker(dc, s.Marker, Surface, markerPen, at, 4.5);
                _hits.Add(new Hit { At = at, Series = s, Break = p });
            }
        }

        /// <summary>Marker shape differs per series (circle, square, diamond, triangle, cross) so series are distinguishable without colour.</summary>
        static void DrawMarker(DrawingContext dc, int shape, Brush fill, Pen pen, Point c, double r)
        {
            switch (shape)
            {
                case 1:
                    dc.DrawRectangle(fill, pen, new Rect(c.X - r, c.Y - r, 2 * r, 2 * r));
                    break;
                case 2:
                    dc.DrawGeometry(fill, pen, Polygon(new Point(c.X, c.Y - r - 1), new Point(c.X + r + 1, c.Y), new Point(c.X, c.Y + r + 1), new Point(c.X - r - 1, c.Y)));
                    break;
                case 3:
                    dc.DrawGeometry(fill, pen, Polygon(new Point(c.X, c.Y - r - 1), new Point(c.X + r + 1, c.Y + r), new Point(c.X - r - 1, c.Y + r)));
                    break;
                case 4:
                    dc.DrawLine(pen, new Point(c.X - r, c.Y), new Point(c.X + r, c.Y));
                    dc.DrawLine(pen, new Point(c.X, c.Y - r), new Point(c.X, c.Y + r));
                    break;
                default:
                    dc.DrawEllipse(fill, pen, c, r, r);
                    break;
            }
        }

        static Geometry Polygon(params Point[] points)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(points[0], true, true);
                for (int i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, false);
            }
            g.Freeze();
            return g;
        }

        void DrawCurrentQuantity(DrawingContext dc, Scale sc)
        {
            int q = Math.Max(1, CurrentQuantity);
            double x = sc.X(q);
            if (x < sc.Plot.Left || x > sc.Plot.Right) return;
            dc.DrawLine(CurrentPen, new Point(x, sc.Plot.Top), new Point(x, sc.Plot.Bottom));
            var label = Loc.F("Your qty {0}", Money.Count(q));
            var ft = MakeText(label, 11, CurrentInk, true);
            double lx = Math.Min(Math.Max(x + 6, sc.Plot.Left), sc.Plot.Right - ft.Width - 4);
            dc.DrawText(ft, new Point(lx, sc.Plot.Top + 2));
        }

        void DrawHover(DrawingContext dc, Scale sc, string currency)
        {
            if (!_hover.HasValue) return;
            var h = _hover.Value;
            DrawMarker(dc, h.Series.Marker, h.Series.Brush, new Pen(Surface, 2), h.At, 6.5);

            var line1 = h.Series.Name;
            var line2 = Loc.F("{0}+ pcs   {1} each", Money.Count(h.Break.Quantity), FormatPrice(h.Break.UnitPrice, h.Series.Currency));
            var line3 = Loc.F("Extended  {0}", Money.Symbol(h.Series.Currency) + (h.Break.UnitPrice * h.Break.Quantity).ToString("#,##0.00", Loc.Culture));
            var t1 = MakeText(line1, 12, CurrentInk, true);
            var t2 = MakeText(line2, 11, AxisInk, false);
            var t3 = MakeText(line3, 11, AxisInk, false);
            double w = Math.Max(t1.Width, Math.Max(t2.Width, t3.Width)) + 20;
            double ht = 12 + t1.Height + t2.Height + t3.Height + 6;
            double x = h.At.X + 12, y = h.At.Y - ht - 8;
            if (x + w > ActualWidth - 4) x = h.At.X - w - 12;
            if (y < 4) y = h.At.Y + 14;

            dc.DrawRoundedRectangle(Surface, MakePen("#9CA3AF", 1, null), new Rect(x, y, w, ht), 4, 4);
            dc.DrawText(t1, new Point(x + 10, y + 6));
            dc.DrawText(t2, new Point(x + 10, y + 6 + t1.Height));
            dc.DrawText(t3, new Point(x + 10, y + 6 + t1.Height + t2.Height));
        }

        // ---- interaction --------------------------------------------------------------------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.GetPosition(this);
            Hit? best = null;
            double bestDist = 14 * 14;
            foreach (var h in _hits)
            {
                double dx = h.At.X - p.X, dy = h.At.Y - p.Y;
                double d = dx * dx + dy * dy;
                if (d <= bestDist) { bestDist = d; best = h; }
            }
            bool changed = best.HasValue != _hover.HasValue
                || (best.HasValue && (best.Value.At != _hover.Value.At || !ReferenceEquals(best.Value.Series, _hover.Value.Series)));
            _hover = best;
            if (changed) InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hover.HasValue) { _hover = null; InvalidateVisual(); }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);
            var cmd = QuantityCommand;
            if (cmd == null) return;

            int qty;
            if (_hover.HasValue) qty = _hover.Value.Break.Quantity;
            else
            {
                var series = Visible();
                if (series.Count == 0) return;
                var sc = BuildScale(series);
                var p = e.GetPosition(this);
                if (p.X < sc.Plot.Left || p.X > sc.Plot.Right) return;
                qty = sc.QtyAt(p.X);
            }
            if (cmd.CanExecute(qty)) cmd.Execute(qty);
        }

        // ---- helpers ------------------------------------------------------------------------------

        static string FormatPrice(decimal v, string currency)
        {
            return Money.Symbol(currency) + v.ToString(v < 1m ? "0.000" : "#,##0.00", Loc.Culture);
        }

        FormattedText MakeText(string text, double size, Brush brush, bool bold)
        {
            return new FormattedText(text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                bold ? FaceBold : Face, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        void Text(DrawingContext dc, string text, double x, double y, double size, Brush brush, bool bold, TextAlignment align)
        {
            var ft = MakeText(text, size, brush, bold);
            double left = align == TextAlignment.Center ? x - ft.Width / 2 : align == TextAlignment.Right ? x - ft.Width : x;
            dc.DrawText(ft, new Point(left, y));
        }

        static Brush Frozen(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        static Pen MakePen(string hex, double thickness, DoubleCollection dashes)
        {
            var pen = new Pen(Frozen(hex), thickness);
            if (dashes != null) pen.DashStyle = new DashStyle(dashes, 0);
            pen.Freeze();
            return pen;
        }
    }
}
