using System;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Wire netting for vector schematics:
    ///  1. orthogonal open-path segments are wire candidates;
    ///  2. collinear overlapping pieces are merged;
    ///  3. pieces connect at shared endpoints and T-junctions; plain crossings connect only through a junction dot;
    ///  4. nets shorter than <see cref="RecognitionOptions.MinNetLength"/> are handed back as symbol linework.
    /// </summary>
    public sealed class WireExtractor
    {
        private readonly RecognitionOptions _o;
        public WireExtractor(RecognitionOptions o) { _o = o; }

        public sealed class Result
        {
            public List<WireNet> Nets = new List<WireNet>();
            public List<Segment> Demoted = new List<Segment>();
            public List<Pt> JunctionDots = new List<Pt>();
            public HashSet<int> JunctionDotPathIds = new HashSet<int>();
        }

        public static bool IsWireCandidate(Segment s) =>
            s.Axis != Axis.None && !s.FromClosedPath && !s.FromCurve && s.Length >= 0.5;

        public Result Extract(IList<Segment> candidates, IEnumerable<PathShape> shapes)
        {
            var res = new Result();
            foreach (var sh in shapes)
            {
                double w = sh.Bounds.Width, h = sh.Bounds.Height;
                if (sh.Closed && sh.Filled && w >= _o.JunctionDotMin && w <= _o.JunctionDotMax && Math.Abs(w - h) < 0.25 * w)
                {
                    res.JunctionDots.Add(sh.Bounds.Center);
                    res.JunctionDotPathIds.Add(sh.Id);
                }
            }

            double tol = _o.ConnectTolerance;
            var wires = DemotePlates(MergeCollinear(candidates), res.Demoted);
            int n = wires.Count;
            var uf = new UnionFind(n);
            var grid = new SpatialGrid(10);
            for (int i = 0; i < n; i++) grid.Add(i, wires[i].Bounds.Inflate(tol));

            for (int i = 0; i < n; i++)
            {
                foreach (var j in grid.Query(wires[i].Bounds.Inflate(tol)))
                {
                    if (j <= i) continue;
                    if (Connected(wires[i], wires[j], res.JunctionDots)) uf.Union(i, j);
                }
            }

            var groups = Enumerable.Range(0, n).GroupBy(uf.Find).ToList();
            int idx = 0;
            foreach (var g in groups)
            {
                var segs = g.Select(i => wires[i]).ToList();
                if (segs.Sum(s => s.Length) < _o.MinNetLength) { res.Demoted.AddRange(segs); continue; }
                var net = new WireNet { Index = idx++, Segments = segs };
                ComputeEnds(net, tol);
                res.Nets.Add(net);
            }
            return res;
        }

        /// <summary>
        /// Contact plates ("| |"), pushbutton bars and similar strokes are short, both their ends touch nothing,
        /// and a lead wire meets them mid-length. Treating that as a T-junction would short the two sides of
        /// every contact, so such pieces are handed to the symbol extractor instead.
        /// </summary>
        private List<Segment> DemotePlates(List<Segment> wires, List<Segment> demoted)
        {
            double tol = _o.ConnectTolerance;
            var grid = new SpatialGrid(10);
            for (int i = 0; i < wires.Count; i++) grid.Add(i, wires[i].Bounds.Inflate(tol));
            var keep = new List<Segment>(wires.Count);
            for (int i = 0; i < wires.Count; i++)
            {
                var w = wires[i];
                if (w.Length > _o.MinNetLength) { keep.Add(w); continue; }
                bool endTouched = false, midTouched = false;
                foreach (var j in grid.Query(w.Bounds.Inflate(tol)))
                {
                    if (j == i) continue;
                    var o = wires[j];
                    foreach (var p in new[] { w.A, w.B })
                        if (o.A.DistanceTo(p) <= tol || o.B.DistanceTo(p) <= tol || o.InteriorContains(p, tol)) endTouched = true;
                    if (w.InteriorContains(o.A, tol) || w.InteriorContains(o.B, tol)) midTouched = true;
                    if (endTouched) break;
                }
                if (midTouched && !endTouched) demoted.Add(w); else keep.Add(w);
            }
            return keep;
        }

        private bool Connected(Segment a, Segment b, List<Pt> dots)
        {
            double tol = _o.ConnectTolerance;
            foreach (var p in new[] { a.A, a.B })
                foreach (var q in new[] { b.A, b.B })
                    if (p.DistanceTo(q) <= tol) return true;
            if (b.InteriorContains(a.A, tol) || b.InteriorContains(a.B, tol)) return true;   // T-junction
            if (a.InteriorContains(b.A, tol) || a.InteriorContains(b.B, tol)) return true;

            // Crossing: connected only when a junction dot sits on the intersection.
            if (a.Axis != b.Axis && a.Axis != Axis.None && b.Axis != Axis.None)
            {
                var h = a.Axis == Axis.Horizontal ? a : b;
                var v = a.Axis == Axis.Horizontal ? b : a;
                var x = new Pt(v.A.X, h.A.Y);
                if (h.InteriorContains(x, tol) && v.InteriorContains(x, tol) && dots.Any(d => d.DistanceTo(x) < 1.0)) return true;
            }
            return false;
        }

        /// <summary>Free ends = endpoints touching no other piece of the same net. Junctions = points shared by 3+ pieces.</summary>
        public static void ComputeEnds(WireNet net, double tol)
        {
            net.FreeEnds.Clear();
            net.Junctions.Clear();
            foreach (var s in net.Segments)
            {
                foreach (var p in new[] { s.A, s.B })
                {
                    int touches = 0;
                    foreach (var o in net.Segments)
                    {
                        if (ReferenceEquals(o, s)) continue;
                        if (o.A.DistanceTo(p) <= tol || o.B.DistanceTo(p) <= tol) touches++;
                        else if (o.InteriorContains(p, tol)) touches += 2;
                    }
                    if (touches == 0) net.FreeEnds.Add(p);
                    else if (touches >= 2 && !net.Junctions.Any(j => j.DistanceTo(p) <= tol)) net.Junctions.Add(p);
                }
            }
        }

        public static List<Segment> MergeCollinear(IEnumerable<Segment> segs)
        {
            var output = new List<Segment>();
            foreach (var axis in new[] { Axis.Horizontal, Axis.Vertical })
            {
                // Pieces of different style (colour / pen width / dash) stay separate: a black symbol lead-in
                // continuing a coloured wire marks where the symbol starts.
                var buckets = new Dictionary<(long, int, int, bool), List<(double lo, double hi, Segment src)>>();
                foreach (var s in segs.Where(s => s.Axis == axis))
                {
                    double fixedCoord = axis == Axis.Horizontal ? s.A.Y : s.A.X;
                    var key = ((long)Math.Round(fixedCoord * 10), s.Color, (int)Math.Round(s.LineWidth * 100), s.Dashed); // 0.1 mm buckets
                    double lo = axis == Axis.Horizontal ? Math.Min(s.A.X, s.B.X) : Math.Min(s.A.Y, s.B.Y);
                    double hi = axis == Axis.Horizontal ? Math.Max(s.A.X, s.B.X) : Math.Max(s.A.Y, s.B.Y);
                    if (!buckets.TryGetValue(key, out var list)) buckets[key] = list = new List<(double, double, Segment)>();
                    list.Add((lo, hi, s));
                }

                foreach (var kv in buckets)
                {
                    double c = kv.Key.Item1 / 10.0;
                    var ivs = kv.Value.OrderBy(v => v.lo).ToList();
                    double curLo = ivs[0].lo, curHi = ivs[0].hi;
                    var src = ivs[0].src;
                    for (int i = 1; i <= ivs.Count; i++)
                    {
                        if (i < ivs.Count && ivs[i].lo <= curHi + 0.05) { curHi = Math.Max(curHi, ivs[i].hi); continue; }
                        output.Add(axis == Axis.Horizontal
                            ? new Segment(new Pt(curLo, c), new Pt(curHi, c)) { LineWidth = src.LineWidth, Color = src.Color, Dashed = src.Dashed }
                            : new Segment(new Pt(c, curLo), new Pt(c, curHi)) { LineWidth = src.LineWidth, Color = src.Color, Dashed = src.Dashed });
                        if (i < ivs.Count) { curLo = ivs[i].lo; curHi = ivs[i].hi; src = ivs[i].src; }
                    }
                }
            }
            return output;
        }
    }
}
