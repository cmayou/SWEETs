using System;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Groups non-wire linework into symbol clusters and binds wire free-ends to them as pins.
    /// Short leaf stubs that re-enter a symbol already on the same net (NC ticks drawn as open lines)
    /// are folded back into the symbol.
    /// </summary>
    public sealed class SymbolExtractor
    {
        private readonly RecognitionOptions _o;
        public SymbolExtractor(RecognitionOptions o) { _o = o; }

        public sealed class Cluster
        {
            public List<Segment> Segments = new List<Segment>();
            public List<PathShape> Shapes = new List<PathShape>();
            public Rect Bounds = Rect.Empty;
            public List<SymbolPin> Pins = new List<SymbolPin>();
            /// <summary>Index of the dashed mechanical link joining this cluster to other poles of one device; -1 if none.</summary>
            public int LinkGroup = -1;
            public bool IsArrow => Shapes.Count(s => s.IsTriangle) == 1 && Bounds.Width <= 6 && Bounds.Height <= 6 && Pins.Count <= 1;
        }

        public List<Cluster> Extract(List<Segment> symbolSegments, IList<PathShape> shapes, List<WireNet> nets, HashSet<int> skipPathIds)
        {
            var segs = symbolSegments.Where(s => !skipPathIds.Contains(s.PathId)).ToList();
            var raw = ClusterSegments(segs, shapes);
            var links = TakeMechanicalLinks(raw, nets);
            var clusters = MergeSharedEnds(raw, nets);
            AttachPins(clusters, nets);
            if (PruneStubs(clusters, nets))
            {
                foreach (var c in clusters) c.Pins.Clear();
                AttachPins(clusters, nets);
            }
            var result = MergeOpposedHalves(clusters);
            for (int k = 0; k < links.Count; k++)
            {
                var members = result.Where(c => c.Pins.Count > 0 && links[k].Any(l => c.Bounds.Intersects(l.Bounds, 0.3))).ToList();
                if (members.Count < 2) continue;
                foreach (var c in members) c.LinkGroup = k;
            }
            return result;
        }

        /// <summary>
        /// A short dashed line whose ends both sit on symbols and which crosses two or more of them is the mechanical
        /// link of a multi-pole device (3-pole breaker, contactor main poles, ganged switch), not a wire: it is removed
        /// from the nets so its ends do not become pins, and kept to group the poles.
        /// </summary>
        private List<List<Segment>> TakeMechanicalLinks(List<Cluster> clusters, List<WireNet> nets)
        {
            var links = new List<List<Segment>>();
            foreach (var net in nets.ToList())
            {
                // A dashed line running straight through the middle of two or more symbols is a centre line (the dash-dot
                // line through the terminals of a strip) or a link, never a wire: a wire ends at a pin, it does not cross a symbol.
                if (net.Segments.Count > 0 && net.Segments.All(s => s.Dashed)
                    && clusters.Count(c => net.Segments.Any(l => l.InteriorContains(c.Bounds.Center, 0.6))) >= 2
                    && !(net.FreeEnds.Count > 0 && net.FreeEnds.All(e => clusters.Any(c => c.Bounds.Contains(e, _o.PinCapture))) && net.Length <= _o.MechanicalLinkMaxLength))
                {
                    nets.Remove(net);
                    continue;
                }
                if (net.Segments.Count == 0 || !net.Segments.All(s => s.Dashed) || net.Length > _o.MechanicalLinkMaxLength) continue;
                if (net.FreeEnds.Count == 0 || !net.FreeEnds.All(e => clusters.Any(c => c.Bounds.Contains(e, _o.PinCapture)))) continue;
                int touched = clusters.Count(c => net.Segments.Any(l => c.Bounds.Intersects(l.Bounds, 0.3)));
                if (touched < 2) continue;
                links.Add(net.Segments.ToList());
                nets.Remove(net);
            }
            return links;
        }

        private List<Cluster> ClusterSegments(List<Segment> segs, IList<PathShape> shapes)
        {
            int n = segs.Count;
            var uf = new UnionFind(n);
            var grid = new SpatialGrid(5);
            for (int i = 0; i < n; i++) grid.Add(i, segs[i].Bounds.Inflate(_o.ClusterGap));

            for (int i = 0; i < n; i++)
            {
                foreach (var j in grid.Query(segs[i].Bounds.Inflate(_o.ClusterGap)))
                {
                    if (j <= i) continue;
                    bool samePath = segs[i].PathId > 0 && segs[i].PathId == segs[j].PathId;
                    if (samePath || Touch(segs[i], segs[j], _o.ClusterGap)) uf.Union(i, j);
                }
            }

            var byPath = shapes.ToDictionary(s => s.Id);
            var result = new List<Cluster>();
            foreach (var g in Enumerable.Range(0, n).GroupBy(uf.Find))
            {
                var c = new Cluster();
                foreach (var i in g)
                {
                    c.Segments.Add(segs[i]);
                    c.Bounds = c.Bounds.Union(segs[i].Bounds);
                    if (segs[i].PathId > 0 && byPath.TryGetValue(segs[i].PathId, out var sh) && !c.Shapes.Contains(sh)) c.Shapes.Add(sh);
                }
                result.Add(c);
            }
            return result;
        }

        /// <summary>
        /// Clusters that both capture the same wire end are parts of one symbol (e.g. a breaker's X mark sits on
        /// the fixed contact, millimetres from the blade).
        /// </summary>
        private List<Cluster> MergeSharedEnds(List<Cluster> clusters, List<WireNet> nets)
        {
            var uf = new UnionFind(clusters.Count);
            foreach (var net in nets)
                foreach (var end in net.FreeEnds)
                {
                    int first = -1;
                    for (int i = 0; i < clusters.Count; i++)
                    {
                        if (!clusters[i].Bounds.Contains(end, _o.PinCapture)) continue;
                        if (first < 0) first = i; else uf.Union(first, i);
                    }
                }
            var merged = new List<Cluster>();
            foreach (var g in Enumerable.Range(0, clusters.Count).GroupBy(uf.Find))
            {
                var c = new Cluster();
                foreach (var i in g)
                {
                    c.Segments.AddRange(clusters[i].Segments);
                    foreach (var sh in clusters[i].Shapes) if (!c.Shapes.Contains(sh)) c.Shapes.Add(sh);
                    c.Bounds = c.Bounds.Union(clusters[i].Bounds);
                }
                merged.Add(c);
            }
            return merged;
        }

        /// <summary>
        /// Open contacts are drawn as two halves with an air gap: ANSI "| |" plates, "o  o" pushbutton studs,
        /// selector cams. Two small clusters with one pin each, on the same pin line, pins pointing away from each
        /// other and facing across a gap of at most <see cref="RecognitionOptions.MaxContactGap"/>, are one symbol.
        /// </summary>
        private List<Cluster> MergeOpposedHalves(List<Cluster> clusters)
        {
            const double maxHalf = 12.0, line = 0.5;
            var halves = clusters.Where(c => c.Pins.Count == 1 && c.Bounds.Width <= maxHalf && c.Bounds.Height <= maxHalf).ToList();
            var merged = new HashSet<Cluster>();
            var result = clusters.Where(c => !halves.Contains(c)).ToList();
            foreach (var a in halves.OrderBy(h => h.Bounds.Left).ThenBy(h => h.Bounds.Bottom))
            {
                if (merged.Contains(a)) continue;
                Cluster best = null; double bestGap = _o.MaxContactGap;
                foreach (var b in halves)
                {
                    if (ReferenceEquals(a, b) || merged.Contains(b)) continue;
                    var pa = a.Pins[0]; var pb = b.Pins[0];
                    double gap;
                    if (pa.Side == PinSide.Left && pb.Side == PinSide.Right && Math.Abs(pa.Location.Y - pb.Location.Y) < line)
                        gap = b.Bounds.Left - a.Bounds.Right;
                    else if (pa.Side == PinSide.Bottom && pb.Side == PinSide.Top && Math.Abs(pa.Location.X - pb.Location.X) < line)
                        gap = b.Bounds.Bottom - a.Bounds.Top;
                    else if (pa.Side == PinSide.Top && pb.Side == PinSide.Bottom && Math.Abs(pa.Location.X - pb.Location.X) < line)
                        gap = a.Bounds.Bottom - b.Bounds.Top;
                    else continue;
                    if (gap < -0.2 || gap > bestGap) continue;
                    bestGap = gap; best = b;
                }
                if (best == null) { result.Add(a); continue; }
                merged.Add(a); merged.Add(best);
                var c = new Cluster { Bounds = a.Bounds.Union(best.Bounds) };
                c.Segments.AddRange(a.Segments); c.Segments.AddRange(best.Segments);
                c.Shapes.AddRange(a.Shapes); c.Shapes.AddRange(best.Shapes.Where(x => !c.Shapes.Contains(x)));
                c.Pins.AddRange(a.Pins); c.Pins.AddRange(best.Pins);
                result.Add(c);
            }
            return result;
        }

        /// <summary>Two pieces belong together when their closest approach is within the gap.</summary>
        private static bool Touch(Segment a, Segment b, double gap)
        {
            if (!a.Bounds.Intersects(b.Bounds, gap)) return false;
            return a.DistanceTo(b.A) <= gap || a.DistanceTo(b.B) <= gap || b.DistanceTo(a.A) <= gap || b.DistanceTo(a.B) <= gap
                   || SegmentsIntersect(a, b);
        }

        private static bool SegmentsIntersect(Segment p, Segment q)
        {
            double d(Pt a, Pt b, Pt c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
            double d1 = d(q.A, q.B, p.A), d2 = d(q.A, q.B, p.B), d3 = d(p.A, p.B, q.A), d4 = d(p.A, p.B, q.B);
            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private void AttachPins(List<Cluster> clusters, List<WireNet> nets)
        {
            foreach (var net in nets)
                foreach (var end in net.FreeEnds)
                {
                    Cluster best = null;
                    double bestD = double.MaxValue;
                    foreach (var c in clusters)
                    {
                        if (!c.Bounds.Contains(end, _o.PinCapture)) continue;
                        double dd = c.Segments.Min(s => s.DistanceTo(end));
                        if (dd < bestD) { bestD = dd; best = c; }
                    }
                    if (best == null) continue; // no distance-to-linework limit: open contact gaps are 2–3 mm from the blade
                    best.Pins.Add(new SymbolPin { Location = end, NetIndex = net.Index, Side = SideFromWire(net, end, best.Bounds) });
                }
        }

        /// <summary>
        /// Folds wire pieces that are really symbol detail back into the symbol:
        ///  - a whole net lying inside one symbol's bounds;
        ///  - a short leaf piece whose both ends sit inside the symbol (NC ticks, internal jogs) —
        ///    the net's pin then moves to the inner end of the remaining wire.
        /// Returns true if anything moved.
        /// </summary>
        private bool PruneStubs(List<Cluster> clusters, List<WireNet> nets)
        {
            bool changed = false;
            double cap = _o.PinCapture, tol = _o.ConnectTolerance;
            foreach (var c in clusters.Where(c => c.Pins.Count > 0))
            {
                foreach (var net in nets.Where(n => c.Pins.Any(p => p.NetIndex == n.Index)).ToList())
                {
                    if (net.Segments.All(s => c.Bounds.Contains(s.A, cap) && c.Bounds.Contains(s.B, cap)))
                    {
                        Absorb(c, net, net.Segments.ToList());
                        changed = true;
                        continue;
                    }
                    bool again = true;
                    while (again)
                    {
                        again = false;
                        foreach (var end in net.FreeEnds.Where(e => c.Bounds.Contains(e, cap)).ToList())
                        {
                            var leaf = net.Segments.FirstOrDefault(s => s.A.DistanceTo(end) <= tol || s.B.DistanceTo(end) <= tol);
                            if (leaf == null || leaf.Length > _o.StubMaxLength || net.Segments.Count < 2) continue;
                            var inner = leaf.A.DistanceTo(end) <= tol ? leaf.B : leaf.A;
                            // inside the symbol, or a lead-in drawn in the symbol's style rather than the wire's
                            if (!c.Bounds.Contains(inner, cap) && !(DiffersFromWire(leaf, net) && c.Bounds.Contains(end, cap))) continue;
                            Absorb(c, net, new List<Segment> { leaf });
                            changed = again = true;
                            break;
                        }
                    }
                }
            }
            if (changed) nets.RemoveAll(n => n.Segments.Count == 0);
            return changed;
        }

        /// <summary>True when the piece's colour or pen width differs from the rest of its net (symbol lead-in on a coloured wire).</summary>
        private static bool DiffersFromWire(Segment leaf, WireNet net)
        {
            var rest = net.Segments.Where(s => !ReferenceEquals(s, leaf)).ToList();
            if (rest.Count == 0 || leaf.Color < 0) return false;
            var main = rest.OrderByDescending(s => s.Length).First();
            return main.Color != leaf.Color || Math.Abs(main.LineWidth - leaf.LineWidth) > 0.05;
        }

        private void Absorb(Cluster c, WireNet net, List<Segment> pieces)
        {
            foreach (var s in pieces)
            {
                net.Segments.Remove(s);
                c.Segments.Add(s);
                c.Bounds = c.Bounds.Union(s.Bounds);
            }
            WireExtractor.ComputeEnds(net, _o.ConnectTolerance);
        }

        /// <summary>Pin side from the direction the wire leaves the symbol; falls back to geometry.</summary>
        public static PinSide SideFromWire(WireNet net, Pt end, Rect symbolBounds)
        {
            foreach (var s in net.Segments)
            {
                Pt other;
                if (s.A.DistanceTo(end) < 1e-3) other = s.B;
                else if (s.B.DistanceTo(end) < 1e-3) other = s.A;
                else continue;
                double dx = other.X - end.X, dy = other.Y - end.Y;
                if (Math.Abs(dy) >= Math.Abs(dx)) return dy > 0 ? PinSide.Top : PinSide.Bottom;
                return dx > 0 ? PinSide.Right : PinSide.Left;
            }
            return SideOf(symbolBounds, end);
        }

        public static PinSide SideOf(Rect r, Pt p)
        {
            var c = r.Center;
            double hx = Math.Max(r.Width / 2, 0.01), hy = Math.Max(r.Height / 2, 0.01);
            double nx = (p.X - c.X) / hx, ny = (p.Y - c.Y) / hy;
            if (Math.Abs(ny) >= Math.Abs(nx)) return ny >= 0 ? PinSide.Top : PinSide.Bottom;
            return nx >= 0 ? PinSide.Right : PinSide.Left;
        }
    }
}
