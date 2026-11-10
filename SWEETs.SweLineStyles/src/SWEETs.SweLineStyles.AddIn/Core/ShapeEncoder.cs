using System;
using System.Collections.Generic;
using System.Linq;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    /// <summary>
    /// Turns lines and arcs (shape units) into shape specification bytes.
    ///
    /// Coordinates are snapped to a grid of 1/<c>divisor</c> shape units. With a divisor above 1 the bytes start with
    /// "3,divisor" (divide following lengths) and end with "4,divisor" (restore), so a fine grid keeps the shape's real
    /// size. Lines become vector bytes when they fit one of the 16 directions (length 1-15), otherwise code 8
    /// displacements. Arcs become bulge arcs (code 12, at most 180° each); a full circle with a whole-number radius
    /// becomes an octant arc (code 10). Pen-up moves join the pieces, starting from the insertion point.
    /// The editor always previews the decoded result, so any rounding is visible before saving.
    /// </summary>
    public static class ShapeEncoder
    {
        public static byte[] Encode(IEnumerable<ShapePrimitive> primitives, int divisor, out List<string> notes)
        {
            var log = new List<string>();   // local: an out parameter cannot be used inside local functions
            notes = log;
            if (divisor < 1 || divisor > 255) throw new ArgumentOutOfRangeException(nameof(divisor), "Grid divisor must be 1-255.");
            var o = new List<byte>();
            if (divisor > 1) { o.Add(3); o.Add((byte)divisor); }

            var cur = (x: 0, y: 0);
            (int x, int y) Q(Pt p) => ((int)Math.Round(p.X * divisor), (int)Math.Round(p.Y * divisor));

            void Displace(int dx, int dy)
            {
                while (dx != 0 || dy != 0)
                {
                    int sx = Math.Max(-127, Math.Min(127, dx)), sy = Math.Max(-127, Math.Min(127, dy));
                    o.Add(8); o.Add(unchecked((byte)(sbyte)sx)); o.Add(unchecked((byte)(sbyte)sy));
                    dx -= sx; dy -= sy;
                }
            }

            void MoveTo((int x, int y) p)
            {
                if (p == cur) return;
                o.Add(2);
                Displace(p.x - cur.x, p.y - cur.y);
                o.Add(1);
                cur = p;
            }

            void LineTo((int x, int y) p)
            {
                int dx = p.x - cur.x, dy = p.y - cur.y;
                if (dx == 0 && dy == 0) return;
                for (int d = 0; d < 16; d++)
                {
                    var u = ShapeInterpreter.Directions[d];
                    for (int len = 1; len <= 15; len++)
                        if (u.X * len == dx && u.Y * len == dy) { o.Add((byte)(len << 4 | d)); cur = p; return; }
                }
                Displace(dx, dy);
                cur = p;
            }

            void BulgeTo((int x, int y) p, double includedAngle)
            {
                int dx = p.x - cur.x, dy = p.y - cur.y;
                if (dx == 0 && dy == 0) return;
                if (Math.Abs(dx) > 127 || Math.Abs(dy) > 127)
                {
                    log.Add("An arc is too large for a bulge arc at this grid resolution; it was replaced by a straight line.");
                    LineTo(p);
                    return;
                }
                int bulge = (int)Math.Round(127 * Math.Tan(includedAngle / 4));
                bulge = Math.Max(-127, Math.Min(127, bulge));
                o.Add(12); o.Add(unchecked((byte)(sbyte)dx)); o.Add(unchecked((byte)(sbyte)dy)); o.Add(unchecked((byte)(sbyte)bulge));
                cur = p;
            }

            foreach (var prim in Order(primitives.ToList()))
            {
                if (prim is LinePrim l)
                {
                    MoveTo(Q(l.P1));
                    LineTo(Q(l.P2));
                }
                else if (prim is ArcPrim a)
                {
                    double rFine = a.Radius * divisor;
                    var cFine = Q(a.Center);
                    bool wholeCircle = a.IsFullCircle && Math.Abs(rFine - Math.Round(rFine)) < 1e-6 && Math.Round(rFine) >= 1 && Math.Round(rFine) <= 255
                                       && Math.Abs(a.Center.X * divisor - cFine.x) < 1e-6 && Math.Abs(a.Center.Y * divisor - cFine.y) < 1e-6;
                    if (wholeCircle)
                    {
                        int r = (int)Math.Round(rFine);
                        MoveTo((cFine.x + r, cFine.y));
                        o.Add(10); o.Add((byte)r); o.Add(a.Sweep < 0 ? (byte)0x80 : (byte)0x00);   // start octant 0, 8 octants
                        continue;
                    }
                    int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(a.Sweep) / Math.PI - 1e-9));
                    MoveTo(Q(a.Start));
                    for (int k = 1; k <= pieces; k++)
                        BulgeTo(Q(a.PointAt(a.StartAngle + a.Sweep * k / pieces)), a.Sweep / pieces);
                }
            }

            if (divisor > 1) { o.Add(4); o.Add((byte)divisor); }
            o.Add(0);
            if (o.Count > ShapeDefinition.MaxBytes) log.Add($"The shape needs {o.Count} bytes; the limit is {ShapeDefinition.MaxBytes}. Use a coarser grid or fewer elements.");
            return o.ToArray();
        }

        /// <summary>Greedy drawing order from the insertion point: next nearest start or end (reversing as needed).</summary>
        private static IEnumerable<ShapePrimitive> Order(List<ShapePrimitive> left)
        {
            var pos = new Pt(0, 0);
            while (left.Count > 0)
            {
                int best = 0; bool reverse = false; double bestD = double.MaxValue;
                for (int k = 0; k < left.Count; k++)
                {
                    double ds = pos.DistanceTo(left[k].Start), de = pos.DistanceTo(left[k].End);
                    if (ds < bestD) { bestD = ds; best = k; reverse = false; }
                    if (de < bestD && !(left[k] is ArcPrim a && a.IsFullCircle)) { bestD = de; best = k; reverse = true; }
                }
                var p = reverse ? left[best].Reversed() : left[best];
                left.RemoveAt(best);
                pos = p.End;
                yield return p;
            }
        }

        /// <summary>Smallest power-of-two divisor (≤ 64) that puts every endpoint and circle centre on the grid.</summary>
        public static int SuggestDivisor(IEnumerable<ShapePrimitive> prims)
        {
            var pts = new List<Pt>();
            foreach (var p in prims)
            {
                pts.Add(p.Start); pts.Add(p.End);
                if (p is ArcPrim a && a.IsFullCircle) pts.Add(a.Center);
            }
            foreach (int d in new[] { 1, 2, 4, 8, 16, 32, 64 })
                if (pts.All(q => Math.Abs(q.X * d - Math.Round(q.X * d)) < 1e-6 && Math.Abs(q.Y * d - Math.Round(q.Y * d)) < 1e-6))
                    return d;
            return 64;
        }
    }
}
