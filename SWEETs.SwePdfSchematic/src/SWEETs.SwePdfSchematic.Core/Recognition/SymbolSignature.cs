using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Translation/scale/rotation/mirror-invariant fingerprint of a symbol's linework.
    /// <see cref="Key"/> is an exact key for learned mappings; <see cref="Similarity"/> powers fuzzy reuse.
    /// </summary>
    public sealed class SymbolSignature
    {
        private const int N = 8;

        public int Pins { get; set; }
        public int Poles { get; set; }
        public int Circles { get; set; }
        public int Rectangles { get; set; }
        public int Triangles { get; set; }
        public int Diagonals { get; set; }
        public int Filled { get; set; }
        public int AspectBucket { get; set; }
        public string InnerText { get; set; }
        /// <summary>8x8 occupancy grid, 64 bits, canonical orientation, smallest of the mirror pair.</summary>
        public ulong Grid { get; set; }

        public string Key =>
            $"P{Pins}.{Poles}|C{Circles}|R{Rectangles}|T{Triangles}|D{Math.Min(Diagonals, 9)}|F{Filled}|A{AspectBucket}|I{InnerText ?? ""}|G{Grid:X16}";

        public static SymbolSignature Compute(IList<Segment> segs, IList<PathShape> shapes, IList<SymbolPin> pins, IEnumerable<string> innerTexts, int poles)
        {
            var sig = new SymbolSignature
            {
                Pins = pins.Count,
                Poles = poles,
                Circles = shapes.Count(s => s.IsRoundish && !s.Filled),
                Rectangles = shapes.Count(s => s.IsAxisRectangle),
                Triangles = shapes.Count(s => s.IsTriangle),
                Filled = shapes.Count(s => s.Filled),
                Diagonals = segs.Count(s => !s.FromCurve && s.Axis == Axis.None && s.Length > 0.8),
                InnerText = string.Join("", innerTexts.Select(t => t.Trim().ToUpperInvariant()).OrderBy(t => t)),
            };

            // Canonical orientation: pin axis vertical.
            bool horizontal = pins.Count > 0 && pins.Count(p => p.Side == PinSide.Left || p.Side == PinSide.Right) > pins.Count / 2.0;
            var pts = new List<Pt>();
            foreach (var s in segs) Sample(s, pts);
            foreach (var p in pins) pts.Add(p.Location);
            if (pts.Count == 0) return sig;
            if (horizontal) pts = pts.Select(p => new Pt(-p.Y, p.X)).ToList();

            var r = Rect.FromPoints(pts);
            double aspect = r.Height < 1e-6 ? 10 : r.Width / r.Height;
            sig.AspectBucket = aspect < 0.35 ? 0 : aspect < 0.7 ? 1 : aspect < 1.4 ? 2 : aspect < 2.8 ? 3 : 4;

            ulong g1 = Rasterize(pts, r, false);
            ulong g2 = Rasterize(pts, r, true);
            ulong g3 = Rasterize(pts.Select(p => new Pt(p.X, -p.Y)).ToList(), Rect.FromPoints(pts.Select(p => new Pt(p.X, -p.Y))), false);
            ulong g4 = Rasterize(pts.Select(p => new Pt(p.X, -p.Y)).ToList(), Rect.FromPoints(pts.Select(p => new Pt(p.X, -p.Y))), true);
            sig.Grid = new[] { g1, g2, g3, g4 }.Min();
            return sig;
        }

        private static void Sample(Segment s, List<Pt> pts)
        {
            int steps = Math.Max(2, (int)Math.Ceiling(s.Length / 0.4));
            for (int i = 0; i <= steps; i++)
            {
                double t = (double)i / steps;
                pts.Add(new Pt(s.A.X + t * (s.B.X - s.A.X), s.A.Y + t * (s.B.Y - s.A.Y)));
            }
        }

        private static ulong Rasterize(List<Pt> pts, Rect r, bool mirrorX)
        {
            ulong bits = 0;
            double w = Math.Max(r.Width, 1e-6), h = Math.Max(r.Height, 1e-6);
            foreach (var p in pts)
            {
                double u = (p.X - r.Left) / w, v = (p.Y - r.Bottom) / h;
                if (mirrorX) u = 1 - u;
                // round before flooring so identical shapes at different page positions land in identical cells
                int cx = Math.Min(N - 1, Math.Max(0, (int)Math.Floor(Math.Round(u * N, 4))));
                int cy = Math.Min(N - 1, Math.Max(0, (int)Math.Floor(Math.Round(v * N, 4))));
                bits |= 1UL << (cy * N + cx);
            }
            return bits;
        }

        /// <summary>0..1. Grid Jaccard (60%) plus structural feature agreement (40%).</summary>
        public double Similarity(SymbolSignature o)
        {
            if (o == null) return 0;
            ulong inter = Grid & o.Grid, uni = Grid | o.Grid;
            double jac = uni == 0 ? 1 : (double)PopCount(inter) / PopCount(uni);
            int agree = 0, total = 7;
            if (Pins == o.Pins) agree++;
            if (Poles == o.Poles) agree++;
            if (Circles == o.Circles) agree++;
            if (Rectangles == o.Rectangles) agree++;
            if (Triangles == o.Triangles) agree++;
            if (Math.Abs(Diagonals - o.Diagonals) <= 1) agree++;
            if (AspectBucket == o.AspectBucket) agree++;
            double feat = (double)agree / total;
            if (!string.Equals(InnerText ?? "", o.InnerText ?? "", StringComparison.Ordinal)) feat *= 0.8;
            return 0.6 * jac + 0.4 * feat;
        }

        private static int PopCount(ulong x)
        {
            int c = 0;
            while (x != 0) { x &= x - 1; c++; }
            return c;
        }
    }
}
