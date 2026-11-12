using System;
using System.Collections.Generic;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    /// <summary>A point in shape units (1 unit = 1 drawing unit at a line-style scale of S=1).</summary>
    public struct Pt : IEquatable<Pt>
    {
        public readonly double X, Y;
        public Pt(double x, double y) { X = x; Y = y; }
        public static Pt operator +(Pt a, Pt b) => new Pt(a.X + b.X, a.Y + b.Y);
        public static Pt operator -(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
        public static Pt operator *(Pt a, double k) => new Pt(a.X * k, a.Y * k);
        public double Length => Math.Sqrt(X * X + Y * Y);
        public double DistanceTo(Pt o) => (this - o).Length;
        public bool Near(Pt o, double tol = 1e-9) => Math.Abs(X - o.X) <= tol && Math.Abs(Y - o.Y) <= tol;
        public bool Equals(Pt o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is Pt p && Equals(p);
        public override int GetHashCode() => X.GetHashCode() * 397 ^ Y.GetHashCode();
        public override string ToString() => $"({X:0.####}, {Y:0.####})";
    }

    /// <summary>Geometry of a shape: straight lines and circular arcs (a full circle is an arc of 2π).</summary>
    public abstract class ShapePrimitive
    {
        public abstract Pt Start { get; }
        public abstract Pt End { get; }
        public abstract ShapePrimitive Reversed();
        public abstract ShapePrimitive Transformed(Func<Pt, Pt> map, double scale);
        /// <summary>Approximates the primitive with straight segments (for drawing and hit testing).</summary>
        public abstract void Tessellate(List<Pt[]> into, double maxStepDegrees = 5);
        public abstract double DistanceTo(Pt p);
        public abstract void Extend(ref double minX, ref double minY, ref double maxX, ref double maxY);
    }

    public sealed class LinePrim : ShapePrimitive
    {
        public Pt P1 { get; }
        public Pt P2 { get; }
        public LinePrim(Pt p1, Pt p2) { P1 = p1; P2 = p2; }
        public override Pt Start => P1;
        public override Pt End => P2;
        public override ShapePrimitive Reversed() => new LinePrim(P2, P1);
        public override ShapePrimitive Transformed(Func<Pt, Pt> map, double scale) => new LinePrim(map(P1), map(P2));
        public override void Tessellate(List<Pt[]> into, double maxStepDegrees = 5) => into.Add(new[] { P1, P2 });

        public override double DistanceTo(Pt p)
        {
            var d = P2 - P1;
            double len2 = d.X * d.X + d.Y * d.Y;
            if (len2 <= 0) return p.DistanceTo(P1);
            double t = Math.Max(0, Math.Min(1, ((p.X - P1.X) * d.X + (p.Y - P1.Y) * d.Y) / len2));
            return p.DistanceTo(P1 + d * t);
        }

        public override void Extend(ref double minX, ref double minY, ref double maxX, ref double maxY)
        {
            foreach (var q in new[] { P1, P2 })
            {
                minX = Math.Min(minX, q.X); minY = Math.Min(minY, q.Y);
                maxX = Math.Max(maxX, q.X); maxY = Math.Max(maxY, q.Y);
            }
        }
    }

    /// <summary>Circular arc: centre, radius, start angle and signed sweep (radians, positive = counter-clockwise).</summary>
    public sealed class ArcPrim : ShapePrimitive
    {
        public Pt Center { get; }
        public double Radius { get; }
        public double StartAngle { get; }
        public double Sweep { get; }

        public ArcPrim(Pt center, double radius, double startAngle, double sweep)
        {
            Center = center; Radius = radius; StartAngle = startAngle; Sweep = sweep;
        }

        public static ArcPrim Circle(Pt center, double radius) => new ArcPrim(center, radius, 0, 2 * Math.PI);

        /// <summary>Arc from <paramref name="a"/> to <paramref name="b"/> with a signed included angle.</summary>
        public static ArcPrim FromChord(Pt a, Pt b, double includedAngle)
        {
            double d = a.DistanceTo(b);
            if (d <= 0 || Math.Abs(includedAngle) < 1e-12) return null;
            double r = d / (2 * Math.Sin(Math.Abs(includedAngle) / 2));
            var mid = (a + b) * 0.5;
            var n = new Pt(-(b.Y - a.Y) / d, (b.X - a.X) / d);          // left normal of the chord
            double h = r * Math.Cos(includedAngle / 2);                  // signed by cos: >π arcs flip side
            var c = mid + n * (includedAngle > 0 ? h : -h);
            return new ArcPrim(c, r, Math.Atan2(a.Y - c.Y, a.X - c.X), includedAngle);
        }

        /// <summary>Arc through three points (start, a point on the arc, end); null when they are collinear.</summary>
        public static ArcPrim FromThreePoints(Pt a, Pt m, Pt b)
        {
            double d = 2 * (a.X * (m.Y - b.Y) + m.X * (b.Y - a.Y) + b.X * (a.Y - m.Y));
            if (Math.Abs(d) < 1e-12) return null;
            double a2 = a.X * a.X + a.Y * a.Y, m2 = m.X * m.X + m.Y * m.Y, b2 = b.X * b.X + b.Y * b.Y;
            var c = new Pt((a2 * (m.Y - b.Y) + m2 * (b.Y - a.Y) + b2 * (a.Y - m.Y)) / d,
                           (a2 * (b.X - m.X) + m2 * (a.X - b.X) + b2 * (m.X - a.X)) / d);
            double r = a.DistanceTo(c);
            double sa = Math.Atan2(a.Y - c.Y, a.X - c.X);
            double ccwToB = Norm(Math.Atan2(b.Y - c.Y, b.X - c.X) - sa);
            double ccwToM = Norm(Math.Atan2(m.Y - c.Y, m.X - c.X) - sa);
            double sweep = ccwToM < ccwToB ? ccwToB : ccwToB - 2 * Math.PI;
            return new ArcPrim(c, r, sa, sweep);
        }

        private static double Norm(double a)
        {
            a %= 2 * Math.PI;
            return a < 0 ? a + 2 * Math.PI : a;
        }

        public bool IsFullCircle => Math.Abs(Math.Abs(Sweep) - 2 * Math.PI) < 1e-9;
        public Pt PointAt(double angle) => new Pt(Center.X + Radius * Math.Cos(angle), Center.Y + Radius * Math.Sin(angle));
        public override Pt Start => PointAt(StartAngle);
        public override Pt End => PointAt(StartAngle + Sweep);
        public override ShapePrimitive Reversed() => new ArcPrim(Center, Radius, StartAngle + Sweep, -Sweep);

        public override ShapePrimitive Transformed(Func<Pt, Pt> map, double scale)
        {
            // Uniform scale + translation only (DXF import, re-centring); the start point fixes the angle.
            var c = map(Center);
            var s = map(Start);
            return new ArcPrim(c, Radius * Math.Abs(scale), Math.Atan2(s.Y - c.Y, s.X - c.X), Sweep);
        }

        public override void Tessellate(List<Pt[]> into, double maxStepDegrees = 5)
        {
            int n = Math.Max(8, (int)Math.Ceiling(Math.Abs(Sweep) * 180 / Math.PI / maxStepDegrees));
            var pts = new Pt[n + 1];
            for (int i = 0; i <= n; i++) pts[i] = PointAt(StartAngle + Sweep * i / n);
            into.Add(pts);
        }

        public override double DistanceTo(Pt p)
        {
            var list = new List<Pt[]>();
            Tessellate(list, 3);
            double best = double.MaxValue;
            var pts = list[0];
            for (int i = 1; i < pts.Length; i++) best = Math.Min(best, new LinePrim(pts[i - 1], pts[i]).DistanceTo(p));
            return best;
        }

        public override void Extend(ref double minX, ref double minY, ref double maxX, ref double maxY)
        {
            var list = new List<Pt[]>();
            Tessellate(list, 2);
            foreach (var q in list[0])
            {
                minX = Math.Min(minX, q.X); minY = Math.Min(minY, q.Y);
                maxX = Math.Max(maxX, q.X); maxY = Math.Max(maxY, q.Y);
            }
        }
    }

    public static class Bounds
    {
        /// <summary>Bounding box of the primitives; false when the list is empty.</summary>
        public static bool Of(IEnumerable<ShapePrimitive> prims, out double minX, out double minY, out double maxX, out double maxY)
        {
            minX = minY = double.MaxValue; maxX = maxY = double.MinValue;
            bool any = false;
            foreach (var p in prims) { p.Extend(ref minX, ref minY, ref maxX, ref maxY); any = true; }
            if (!any) { minX = minY = maxX = maxY = 0; }
            return any;
        }
    }
}
