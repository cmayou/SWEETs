using System;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Geometry;

namespace SWEETs.SwePdfSchematic.Core.Pdf
{
    /// <summary>A dashed rectangle drawn around part of a schematic (SWE location / function box).</summary>
    public sealed class RegionBox
    {
        public Rect Bounds;
        public int Color = -1;
        /// <summary>Text written just above or inside the top-left corner ("L2 - Conveyors"); filled in by the recognizer.</summary>
        public string Label;
    }

    /// <summary>
    /// Turns exporter-specific drawing tricks back into plain linework before recognition:
    ///  1. thick lines exported as thin filled rectangles (Teigha / SOLIDWORKS Electrical, AutoCAD lineweights)
    ///     become a single centre-line segment carrying the stroke width;
    ///  2. linetypes exported as separate dash pieces (dash, dash-dot, hidden) are re-joined into one dashed segment;
    ///  3. tiny curve marks (&lt;0.4 mm) are dropped as noise;
    ///  4. dashed rectangles larger than a symbol become <see cref="RegionBox"/>es and leave the linework.
    /// Runs on every page; on PDFs drawn with plain strokes it changes nothing.
    /// </summary>
    public static class LinetypeNormalizer
    {
        /// <summary>Filled rectangles this thin (mm) are strokes, not shapes.</summary>
        public const double MaxStrokeWidth = 1.0;
        /// <summary>Longest single dash piece (mm) and largest gap between pieces of one dashed line.</summary>
        public const double MaxDash = 3.5, MaxDashGap = 1.2;
        /// <summary>Minimum side of a dashed rectangle treated as a region box.</summary>
        public const double MinRegionSide = 15.0;

        public static void Normalize(RawPage page)
        {
            ThickLinesToCentreLines(page);
            DropTinyMarks(page);
            JoinDashes(page);
            ExtractRegionBoxes(page);
        }

        private static void ThickLinesToCentreLines(RawPage page)
        {
            var convert = new Dictionary<int, PathShape>();
            foreach (var sh in page.Shapes)
            {
                if (!sh.Filled || !sh.Closed || sh.HasCurves || sh.VertexCount != 4) continue;
                double w = sh.Bounds.Width, h = sh.Bounds.Height, thin = Math.Min(w, h), len = Math.Max(w, h);
                if (thin > MaxStrokeWidth || len < 1.5 * thin || len < 0.25) continue;
                // must really be an axis rectangle: all its edges axis-aligned
                var edges = page.Segments.Where(s => s.PathId == sh.Id).ToList();
                if (edges.Count < 3 || edges.Any(e => e.Axis == Axis.None)) continue;
                convert[sh.Id] = sh;
            }
            if (convert.Count == 0) return;

            var output = new List<Segment>(page.Segments.Count);
            var done = new HashSet<int>();
            foreach (var s in page.Segments)
            {
                if (!convert.TryGetValue(s.PathId, out var sh)) { output.Add(s); continue; }
                if (!done.Add(sh.Id)) continue;
                var b = sh.Bounds;
                bool horizontal = b.Width >= b.Height;
                var a = horizontal ? new Pt(b.Left, b.Center.Y) : new Pt(b.Center.X, b.Bottom);
                var e = horizontal ? new Pt(b.Right, b.Center.Y) : new Pt(b.Center.X, b.Top);
                output.Add(new Segment(a, e) { PathId = sh.Id, LineWidth = Math.Min(b.Width, b.Height), Color = sh.Color });
            }
            page.Segments = output;
            page.Shapes.RemoveAll(sh => convert.ContainsKey(sh.Id));
        }

        private static void DropTinyMarks(RawPage page)
        {
            var tiny = new HashSet<int>(page.Shapes.Where(sh => sh.HasCurves && !sh.IsDot && Math.Max(sh.Bounds.Width, sh.Bounds.Height) < 0.4).Select(sh => sh.Id));
            if (tiny.Count == 0) return;
            page.Segments.RemoveAll(s => tiny.Contains(s.PathId));
            page.Shapes.RemoveAll(sh => tiny.Contains(sh.Id));
        }

        /// <summary>
        /// Collinear axis-aligned pieces of the same colour and width, each at most <see cref="MaxDash"/> long and
        /// separated by at most <see cref="MaxDashGap"/>, are one dashed line when there are at least three of them.
        /// </summary>
        private static void JoinDashes(RawPage page)
        {
            var pieces = page.Segments.Where(s => s.Axis != Axis.None && s.Length <= MaxDash && !s.FromClosedPath && !s.FromCurve).ToList();
            var remove = new HashSet<Segment>();
            var add = new List<Segment>();
            foreach (var axis in new[] { Axis.Horizontal, Axis.Vertical })
            {
                var groups = pieces.Where(s => s.Axis == axis)
                    .GroupBy(s => (fixedKey: (long)Math.Round((axis == Axis.Horizontal ? s.A.Y : s.A.X) * 10), s.Color, w: Math.Round(s.LineWidth, 2)));
                foreach (var g in groups)
                {
                    var ivs = g.Select(s => (lo: axis == Axis.Horizontal ? Math.Min(s.A.X, s.B.X) : Math.Min(s.A.Y, s.B.Y),
                                             hi: axis == Axis.Horizontal ? Math.Max(s.A.X, s.B.X) : Math.Max(s.A.Y, s.B.Y), s))
                               .OrderBy(v => v.lo).ToList();
                    int start = 0;
                    for (int i = 1; i <= ivs.Count; i++)
                    {
                        double runHi = ivs.Skip(start).Take(i - start).Max(v => v.hi);
                        if (i < ivs.Count && ivs[i].lo - runHi <= MaxDashGap) continue;
                        var run = ivs.Skip(start).Take(i - start).ToList();
                        double lo = run[0].lo, hi = run.Max(v => v.hi);
                        bool gaps = run.Count >= 3 && hi - lo >= 4.0;
                        if (gaps)
                        {
                            var src = run[0].s;
                            double c = axis == Axis.Horizontal ? src.A.Y : src.A.X;
                            var seg = axis == Axis.Horizontal ? src.CopyWith(new Pt(lo, c), new Pt(hi, c)) : src.CopyWith(new Pt(c, lo), new Pt(c, hi));
                            seg.Dashed = true;
                            seg.PathId = -1;
                            add.Add(seg);
                            foreach (var v in run) remove.Add(v.s);
                        }
                        start = i;
                    }
                }
            }
            if (remove.Count == 0) return;
            page.Segments.RemoveAll(remove.Contains);
            page.Segments.AddRange(add);
            // dashed lines that continue past a piece longer than MaxDash (rare) are merged by the wire extractor
        }

        /// <summary>Four dashed sides meeting at the corners (within 1.5 mm) and at least <see cref="MinRegionSide"/> long.</summary>
        private static void ExtractRegionBoxes(RawPage page)
        {
            const double tol = 1.5;
            var dashed = page.Segments.Where(s => s.Dashed && s.Length >= MinRegionSide).ToList();
            var hs = dashed.Where(s => s.Axis == Axis.Horizontal).ToList();
            var vs = dashed.Where(s => s.Axis == Axis.Vertical).ToList();
            var used = new HashSet<Segment>();
            foreach (var top in hs.OrderByDescending(s => s.A.Y))
            {
                if (used.Contains(top)) continue;
                double l = Math.Min(top.A.X, top.B.X), r = Math.Max(top.A.X, top.B.X), y = top.A.Y;
                var left = vs.FirstOrDefault(v => !used.Contains(v) && Math.Abs(v.A.X - l) < tol && Math.Abs(Math.Max(v.A.Y, v.B.Y) - y) < tol);
                var right = vs.FirstOrDefault(v => !used.Contains(v) && Math.Abs(v.A.X - r) < tol && Math.Abs(Math.Max(v.A.Y, v.B.Y) - y) < tol);
                if (left == null || right == null) continue;
                double by = Math.Min(Math.Min(left.A.Y, left.B.Y), Math.Min(right.A.Y, right.B.Y));
                var bottom = hs.FirstOrDefault(h => !ReferenceEquals(h, top) && !used.Contains(h) && Math.Abs(h.A.Y - by) < tol
                                                  && Math.Abs(Math.Min(h.A.X, h.B.X) - l) < tol && Math.Abs(Math.Max(h.A.X, h.B.X) - r) < tol);
                if (bottom == null) continue;
                foreach (var s in new[] { top, left, right, bottom }) used.Add(s);
                page.Regions.Add(new RegionBox { Bounds = new Rect(l, by, r, y), Color = top.Color });
            }
            if (used.Count > 0) page.Segments.RemoveAll(used.Contains);
        }
    }
}
