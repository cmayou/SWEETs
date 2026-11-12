using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChangeLog.Core.Query;

namespace ChangeLog.AddIn.UI.Controls
{
    /// <summary>Stacked daily activity bars (added / edited / removed) with hover readout.</summary>
    public sealed class DayChart : FrameworkElement
    {
        public static readonly DependencyProperty BucketsProperty = DependencyProperty.Register(
            nameof(Buckets), typeof(IList<DayBucket>), typeof(DayChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly Typeface Face = new Typeface("Segoe UI");
        private int _hover = -1;

        public DayChart()
        {
            MinHeight = 90;
            ToolTipService.SetInitialShowDelay(this, 0);
            Focusable = false;
        }

        public IList<DayBucket> Buckets
        {
            get { return (IList<DayBucket>)GetValue(BucketsProperty); }
            set { SetValue(BucketsProperty, value); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var b = Buckets;
            if (b == null || b.Count == 0) return;
            var i = (int)(e.GetPosition(this).X / (ActualWidth / b.Count));
            i = Math.Max(0, Math.Min(b.Count - 1, i));
            if (i == _hover) return;
            _hover = i;
            var d = b[i];
            ToolTip = d.Day.ToString("ddd d MMM", CultureInfo.CurrentCulture) + ": " + d.Total + " changes (" + d.Added + " added, " + d.Edited + " edited, " + d.Removed + " removed)";
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            _hover = -1;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var b = Buckets;
            var w = ActualWidth;
            var h = ActualHeight;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
            if (b == null || b.Count == 0 || w <= 0) return;

            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            const double labelH = 16;
            var plotH = Math.Max(10, h - labelH);
            var max = Math.Max(1, b.Max(x => x.Total));
            var slot = w / b.Count;
            var bar = Math.Max(2, Math.Min(18, slot * 0.6));
            var axis = new Pen(Palette.Brush(Color.FromRgb(0xDA, 0xDD, 0xE3)), 1);
            dc.DrawLine(axis, new Point(0, plotH + 0.5), new Point(w, plotH + 0.5));

            for (var i = 0; i < b.Count; i++)
            {
                var x = i * slot + (slot - bar) / 2;
                var y = plotH;
                if (i == _hover) dc.DrawRectangle(Palette.Brush(Color.FromRgb(0xF0, 0xF2, 0xF5)), null, new Rect(i * slot, 0, slot, plotH));
                y = Stack(dc, x, y, bar, b[i].Removed, max, plotH, Palette.Removed);
                y = Stack(dc, x, y, bar, b[i].Edited, max, plotH, Palette.Edited);
                Stack(dc, x, y, bar, b[i].Added, max, plotH, Palette.Added);

                var labelEvery = Math.Max(1, (int)Math.Ceiling(34 / slot));
                if (i % labelEvery == 0 || i == b.Count - 1)
                {
                    var t = new FormattedText(b[i].Day.ToString("d/M", CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight, Face, 9.5, Palette.Brush(Color.FromRgb(0x6B, 0x72, 0x80)), dpi);
                    dc.DrawText(t, new Point(i * slot + (slot - t.Width) / 2, plotH + 2));
                }
            }
        }

        private static double Stack(DrawingContext dc, double x, double y, double width, int value, int max, double plotH, Color c)
        {
            if (value <= 0) return y;
            var hgt = Math.Max(1.5, value / (double)max * (plotH - 4));
            dc.DrawRectangle(Palette.Brush(c), null, new Rect(x, y - hgt, width, hgt));
            return y - hgt;
        }
    }
}
