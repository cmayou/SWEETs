using System;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Geometry-first symbol kind rules for IEC and ANSI drawing conventions. Each rule is a small,
    /// independent predicate so new kinds can be added without touching the rest.
    /// </summary>
    public static class SymbolClassifier
    {
        private sealed class Features
        {
            public List<PathShape> Circles;
            public List<PathShape> Rects;
            public List<Segment> Diagonals;
            public List<Segment> AxisSegs;
            public bool PinAxisVertical;
            public int Pins;
            public double PinAxisCoord; // x of the pin line when vertical, y when horizontal
            public string Inner;
        }

        public static SymbolKind Classify(DetectedSymbol s, SymbolStandard std)
        {
            var f = Extract(s);

            if (s.Shapes.Count(x => x.IsTriangle) == 1 && f.Pins <= 1 && s.Bounds.Width <= 6 && s.Bounds.Height <= 6)
                return SymbolKind.OffPageArrow;

            if (f.Circles.Count >= 2 && CirclesOverlap(f.Circles)) return SymbolKind.Transformer;

            var big = f.Circles.FirstOrDefault(c => c.Bounds.Width >= 8);
            bool motorText = System.Text.RegularExpressions.Regex.IsMatch(f.Inner, @"(^|\s)(M|MTR|\d?~|3~)(\s|$)");
            if ((big != null || f.Circles.Count > 0) && motorText) return SymbolKind.Motor;
            if (big != null && f.Pins == 2) return SymbolKind.Coil;          // ANSI coil: plain circle, two pins
            if (big != null && f.Pins >= 3) return SymbolKind.Motor;

            if (f.Circles.Count == 1)
            {
                var c = f.Circles[0];
                if (HasCrossInside(f.Diagonals, c.Bounds)) return SymbolKind.Lamp;
                if (c.Bounds.Width <= 4 && f.Pins <= 2 && f.Diagonals.Count == 0) return SymbolKind.Terminal;
                if (std == SymbolStandard.ANSI && f.Pins == 2) return SymbolKind.Coil; // ANSI coil is a circle
            }

            if (f.Rects.Count >= 1)
            {
                var r = f.Rects.OrderByDescending(x => x.Bounds.Area).First();
                if (f.Pins >= 3 && r.Bounds.Width > 10 && r.Bounds.Height > 10) return SymbolKind.BlackBox;
                if (LineThrough(f, r.Bounds)) return SymbolKind.Fuse;
                if (f.Pins == 2 && f.Diagonals.Count == 0) return SymbolKind.Coil;
            }

            if (f.Diagonals.Count >= 1 && f.Pins >= 2)
            {
                if (HasSmallCross(f.Diagonals)) return SymbolKind.CircuitBreaker;
                bool tick = HasNcTick(f);
                if (HasActuator(f)) return tick ? SymbolKind.PushbuttonNc : SymbolKind.PushbuttonNo;
                if (std == SymbolStandard.ANSI && HasParallelPlates(f)) return SymbolKind.NcContact; // ANSI NC = plates + slash
                return tick ? SymbolKind.NcContact : SymbolKind.NoContact;
            }

            if (f.Pins >= 2 && HasParallelPlates(f)) return SymbolKind.NoContact; // ANSI NO "| |"
            return SymbolKind.Unknown;
        }

        /// <summary>
        /// Kind implied by standard terminal designations (IEC 60445 / EN 50005, also used by NEMA vendors):
        /// A1/A2 coil, X1/X2 or H1/H2 lamp, U/V/W motor, x3/x4 NO and x1/x2 NC auxiliary contacts, Ln/Tn main contacts.
        /// </summary>
        public static SymbolKind? KindFromPinLabels(DetectedSymbol s)
        {
            var labels = s.Pins.Select(p => p.Label?.Trim().ToUpperInvariant()).Where(l => !string.IsNullOrEmpty(l)).ToList();
            if (labels.Count < 2) return null;
            bool Has(params string[] ls) => ls.All(labels.Contains);
            if (Has("A1", "A2")) return SymbolKind.Coil;
            if (Has("X1", "X2") || Has("H1", "H2")) return SymbolKind.Lamp;
            if (Has("U", "V", "W") || Has("U1", "V1", "W1")) return SymbolKind.Motor;
            if (labels.Count == 2)
            {
                if (labels.All(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^[LT]\d$")) && labels.Any(l => l[0] == 'L') && labels.Any(l => l[0] == 'T'))
                    return SymbolKind.NoContact;
                if (labels.All(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d\d$")))
                {
                    var last = labels.Select(l => l[1]).OrderBy(c => c).ToArray();
                    if (last[0] == '3' && last[1] == '4') return SymbolKind.NoContact;
                    if (last[0] == '1' && last[1] == '2') return SymbolKind.NcContact;
                }
            }
            return null;
        }

        /// <summary>Loose geometric compatibility check used to choose between several allowed kinds.</summary>
        public static bool Plausible(DetectedSymbol s, SymbolKind k)
        {
            var f = Extract(s);
            switch (k)
            {
                case SymbolKind.Motor: return f.Circles.Any(c => c.Bounds.Width >= 6);
                case SymbolKind.Coil: return f.Pins == 2 && (f.Circles.Count > 0 || f.Rects.Count > 0);
                case SymbolKind.Lamp: return f.Circles.Count > 0;
                case SymbolKind.Terminal: return f.Circles.Any(c => c.Bounds.Width <= 4) && f.Pins <= 2;
                case SymbolKind.BlackBox: return f.Pins >= 3 || f.Rects.Any(r => r.Bounds.Width > 6);
                case SymbolKind.NoContact:
                case SymbolKind.NcContact:
                case SymbolKind.PushbuttonNo:
                case SymbolKind.PushbuttonNc:
                case SymbolKind.CircuitBreaker:
                case SymbolKind.Fuse:
                case SymbolKind.Overload: return f.Pins >= 2;
                default: return true;
            }
        }

        /// <summary>Number of independent pole lines (pin pairs on opposite sides sharing a coordinate).</summary>
        public static int CountPoles(DetectedSymbol s)
        {
            var vertical = s.Pins.Count(p => p.Side == PinSide.Top || p.Side == PinSide.Bottom) >= s.Pins.Count(p => p.Side == PinSide.Left || p.Side == PinSide.Right);
            var lines = s.Pins.Select(p => Math.Round(vertical ? p.Location.X : p.Location.Y, 0)).Distinct().Count();
            return Math.Max(1, lines);
        }

        private static Features Extract(DetectedSymbol s)
        {
            var f = new Features
            {
                Circles = s.Shapes.Where(x => x.IsRoundish && !x.Filled).ToList(),
                Rects = s.Shapes.Where(x => x.IsAxisRectangle).ToList(),
                Diagonals = s.Segments.Where(x => !x.FromCurve && x.Axis == Axis.None && x.Length > 0.8).ToList(),
                AxisSegs = s.Segments.Where(x => !x.FromCurve && x.Axis != Axis.None && !x.FromClosedPath).ToList(),
                Pins = s.Pins.Count,
                Inner = string.Join(" ", s.InnerTexts).ToUpperInvariant(),
            };
            f.PinAxisVertical = s.Pins.Count == 0 || s.Pins.Count(p => p.Side == PinSide.Top || p.Side == PinSide.Bottom) >= s.Pins.Count / 2.0;
            f.PinAxisCoord = s.Pins.Count == 0 ? (f.PinAxisVertical ? s.Bounds.Center.X : s.Bounds.Center.Y)
                : s.Pins.Average(p => f.PinAxisVertical ? p.Location.X : p.Location.Y);
            return f;
        }

        private static bool CirclesOverlap(List<PathShape> c) =>
            c.Any(a => c.Any(b => !ReferenceEquals(a, b) && a.Bounds.Intersects(b.Bounds)));

        private static bool HasCrossInside(List<Segment> diags, Rect circle)
        {
            var inside = diags.Where(d => circle.Contains(d.A, 0.3) && circle.Contains(d.B, 0.3)).ToList();
            return inside.Count >= 2 && inside.Any(a => inside.Any(b => !ReferenceEquals(a, b) && Cross(a, b)));
        }

        private static bool HasSmallCross(List<Segment> diags)
        {
            var small = diags.Where(d => d.Length < 4).ToList();
            return small.Any(a => small.Any(b => !ReferenceEquals(a, b) && Cross(a, b)));
        }

        private static bool Cross(Segment p, Segment q)
        {
            double d(Pt a, Pt b, Pt c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            return d(q.A, q.B, p.A) * d(q.A, q.B, p.B) < 0 && d(p.A, p.B, q.A) * d(p.A, p.B, q.B) < 0;
        }

        /// <summary>IEC NC: short segment perpendicular to the pin axis, starting on the pin line.</summary>
        private static bool HasNcTick(Features f) =>
            f.AxisSegs.Any(s => s.Length <= 4.5 && IsPerpendicular(s, f) && TouchesPinLine(s, f));

        /// <summary>Pushbutton/actuator: perpendicular segment off the pin line, ending in a short T-head.</summary>
        private static bool HasActuator(Features f)
        {
            foreach (var s in f.AxisSegs.Where(s => IsPerpendicular(s, f) && !TouchesPinLine(s, f) && s.Length >= 2))
            {
                var far = DistanceFromPinLine(s.A, f) > DistanceFromPinLine(s.B, f) ? s.A : s.B;
                if (f.AxisSegs.Any(h => !ReferenceEquals(h, s) && !IsPerpendicular(h, f) && h.Length <= 4 && h.DistanceTo(far) < 0.3))
                    return true;
            }
            return false;
        }

        /// <summary>ANSI contact plates: two equal perpendicular segments crossing the pin line, 1.5–6 mm apart.</summary>
        private static bool HasParallelPlates(Features f)
        {
            var perp = f.AxisSegs.Where(s => IsPerpendicular(s, f) && s.Length >= 2.5 && s.Length <= 7 && CrossesPinLine(s, f)).ToList();
            for (int i = 0; i < perp.Count; i++)
                for (int j = i + 1; j < perp.Count; j++)
                {
                    double gap = Math.Abs(AlongAxis(perp[i], f) - AlongAxis(perp[j], f));
                    if (Math.Abs(perp[i].Length - perp[j].Length) < 0.5 && gap >= 1.5 && gap <= 6) return true;
                }
            return false;
        }

        private static bool LineThrough(Features f, Rect r) =>
            f.AxisSegs.Any(s => !IsPerpendicular(s, f) && (f.PinAxisVertical ? s.Bounds.Height : s.Bounds.Width) >= 0.8 * (f.PinAxisVertical ? r.Height : r.Width)
                                && r.Contains(s.Bounds.Center, 0.1));

        private static bool IsPerpendicular(Segment s, Features f) =>
            f.PinAxisVertical ? s.Axis == Axis.Horizontal : s.Axis == Axis.Vertical;

        private static double DistanceFromPinLine(Pt p, Features f) =>
            Math.Abs((f.PinAxisVertical ? p.X : p.Y) - f.PinAxisCoord);

        /// <summary>One end sits on the pin line (IEC NC tick).</summary>
        private static bool TouchesPinLine(Segment s, Features f) =>
            Math.Min(DistanceFromPinLine(s.A, f), DistanceFromPinLine(s.B, f)) < 0.4;

        /// <summary>The segment straddles the pin line (ANSI contact plate).</summary>
        private static bool CrossesPinLine(Segment s, Features f)
        {
            double a = (f.PinAxisVertical ? s.A.X : s.A.Y) - f.PinAxisCoord;
            double b = (f.PinAxisVertical ? s.B.X : s.B.Y) - f.PinAxisCoord;
            return a * b < 0 && Math.Min(Math.Abs(a), Math.Abs(b)) > 0.4;
        }

        private static double AlongAxis(Segment s, Features f) => f.PinAxisVertical ? s.A.Y : s.A.X;
    }
}
