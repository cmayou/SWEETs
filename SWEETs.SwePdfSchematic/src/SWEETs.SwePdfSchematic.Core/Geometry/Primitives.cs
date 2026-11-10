using System;
using System.Collections.Generic;

namespace SWEETs.SwePdfSchematic.Core.Geometry
{
    /// <summary>All geometry in the core is millimetres, origin bottom-left (same as PDF and SWE sheets).</summary>
    public struct Pt
    {
        public double X;
        public double Y;
        public Pt(double x, double y) { X = x; Y = y; }
        public double DistanceTo(Pt o) => Math.Sqrt((X - o.X) * (X - o.X) + (Y - o.Y) * (Y - o.Y));
        public static Pt operator +(Pt a, Pt b) => new Pt(a.X + b.X, a.Y + b.Y);
        public static Pt operator -(Pt a, Pt b) => new Pt(a.X - b.X, a.Y - b.Y);
        public override string ToString() => $"({X:0.##}, {Y:0.##})";
    }

    public struct Rect
    {
        public double Left, Bottom, Right, Top;
        public Rect(double l, double b, double r, double t) { Left = l; Bottom = b; Right = r; Top = t; }
        public double Width => Right - Left;
        public double Height => Top - Bottom;
        public double Area => Width * Height;
        public Pt Center => new Pt((Left + Right) / 2, (Bottom + Top) / 2);
        public bool IsEmpty => Width < 0 || Height < 0;
        public static readonly Rect Empty = new Rect(double.MaxValue, double.MaxValue, double.MinValue, double.MinValue);

        public Rect Union(Rect o) => new Rect(Math.Min(Left, o.Left), Math.Min(Bottom, o.Bottom), Math.Max(Right, o.Right), Math.Max(Top, o.Top));
        public Rect Union(Pt p) => new Rect(Math.Min(Left, p.X), Math.Min(Bottom, p.Y), Math.Max(Right, p.X), Math.Max(Top, p.Y));
        public Rect Inflate(double m) => new Rect(Left - m, Bottom - m, Right + m, Top + m);
        public bool Contains(Pt p, double margin = 0) =>
            p.X >= Left - margin && p.X <= Right + margin && p.Y >= Bottom - margin && p.Y <= Top + margin;
        public bool Intersects(Rect o, double gap = 0) =>
            Left - gap <= o.Right && o.Left - gap <= Right && Bottom - gap <= o.Top && o.Bottom - gap <= Top;

        /// <summary>Shortest distance between two rectangles (0 when they overlap).</summary>
        public double DistanceTo(Rect o)
        {
            double dx = Math.Max(0, Math.Max(o.Left - Right, Left - o.Right));
            double dy = Math.Max(0, Math.Max(o.Bottom - Top, Bottom - o.Top));
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static Rect FromPoints(IEnumerable<Pt> pts)
        {
            var r = Empty;
            foreach (var p in pts) r = r.Union(p);
            return r;
        }

        public override string ToString() => $"[{Left:0.#},{Bottom:0.#} → {Right:0.#},{Top:0.#}]";
    }

    public enum Axis { None, Horizontal, Vertical }

    /// <summary>A straight piece of vector linework. Curves are flattened into several segments sharing a PathId.</summary>
    public sealed class Segment
    {
        public Pt A;
        public Pt B;
        /// <summary>Source path id; segments of one PDF path share it. -1 = no path grouping.</summary>
        public int PathId = -1;
        public bool FromCurve;
        public bool FromClosedPath;
        public bool FromFilledPath;
        public double LineWidth;
        /// <summary>Colour as 0xRRGGBB (stroke colour, or fill colour for filled outlines); -1 when unknown.</summary>
        public int Color = -1;
        /// <summary>Drawn with a dash/linetype pattern (set by the reader or rebuilt by <c>LinetypeNormalizer</c>).</summary>
        public bool Dashed;

        public Segment(Pt a, Pt b) { A = a; B = b; }
        public Segment CopyWith(Pt a, Pt b) => new Segment(a, b)
        {
            PathId = PathId, FromCurve = FromCurve, FromClosedPath = FromClosedPath, FromFilledPath = FromFilledPath,
            LineWidth = LineWidth, Color = Color, Dashed = Dashed,
        };
        public double Length => A.DistanceTo(B);
        public Rect Bounds => Rect.FromPoints(new[] { A, B });

        public Axis Axis
        {
            get
            {
                const double eps = 0.05;
                if (Math.Abs(A.Y - B.Y) < eps && Math.Abs(A.X - B.X) > eps) return Axis.Horizontal;
                if (Math.Abs(A.X - B.X) < eps && Math.Abs(A.Y - B.Y) > eps) return Axis.Vertical;
                return Axis.None;
            }
        }

        /// <summary>True when p lies on the segment strictly away from both ends.</summary>
        public bool InteriorContains(Pt p, double tol)
        {
            double len = Length;
            if (len < 2 * tol) return false;
            double t = ((p.X - A.X) * (B.X - A.X) + (p.Y - A.Y) * (B.Y - A.Y)) / (len * len);
            if (t * len <= tol || (1 - t) * len <= tol) return false;
            var proj = new Pt(A.X + t * (B.X - A.X), A.Y + t * (B.Y - A.Y));
            return proj.DistanceTo(p) <= tol;
        }

        public double DistanceTo(Pt p)
        {
            double len2 = (B.X - A.X) * (B.X - A.X) + (B.Y - A.Y) * (B.Y - A.Y);
            if (len2 < 1e-12) return A.DistanceTo(p);
            double t = Math.Max(0, Math.Min(1, ((p.X - A.X) * (B.X - A.X) + (p.Y - A.Y) * (B.Y - A.Y)) / len2));
            return new Pt(A.X + t * (B.X - A.X), A.Y + t * (B.Y - A.Y)).DistanceTo(p);
        }
    }

    /// <summary>Closed or open source path, kept for shape-level features (circles, rectangles, dots, arrows).</summary>
    public sealed class PathShape
    {
        public int Id;
        public bool Closed;
        public bool Filled;
        public bool Stroked;
        public bool HasCurves;
        public int VertexCount;
        public Rect Bounds;
        public int Color = -1;
        /// <summary>Round-capped zero-length stroke (how CAD exporters draw dots); Bounds is the dot's extent.</summary>
        public bool IsDot;

        public bool IsAxisRectangle => Closed && !HasCurves && VertexCount == 4;
        public bool IsRoundish => Closed && HasCurves && Bounds.Width > 0 && Math.Abs(Bounds.Width - Bounds.Height) < 0.15 * Bounds.Width;
        public bool IsTriangle => Closed && !HasCurves && VertexCount == 3;
    }

    public enum TextDirection { Horizontal, Rotate90, Rotate180, Rotate270, Other }

    public sealed class TextItem
    {
        public string Text;
        public Rect Bounds;
        public TextDirection Direction;
        public double FontSize;
        public Pt Center => Bounds.Center;
        public override string ToString() => $"\"{Text}\" {Bounds}";
    }

    /// <summary>Simple disjoint-set used by wire netting and symbol clustering.</summary>
    public sealed class UnionFind
    {
        private readonly int[] _p;
        public UnionFind(int n) { _p = new int[n]; for (int i = 0; i < n; i++) _p[i] = i; }
        public int Find(int a) { while (_p[a] != a) { _p[a] = _p[_p[a]]; a = _p[a]; } return a; }
        public void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) _p[a] = b; }
    }

    /// <summary>Uniform grid bucket index to keep pairwise proximity tests near-linear on dense pages.</summary>
    public sealed class SpatialGrid
    {
        private readonly double _cell;
        private readonly Dictionary<long, List<int>> _cells = new Dictionary<long, List<int>>();
        public SpatialGrid(double cell) { _cell = cell; }

        private long Key(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;

        public void Add(int id, Rect r)
        {
            for (int cx = (int)Math.Floor(r.Left / _cell); cx <= (int)Math.Floor(r.Right / _cell); cx++)
                for (int cy = (int)Math.Floor(r.Bottom / _cell); cy <= (int)Math.Floor(r.Top / _cell); cy++)
                {
                    var k = Key(cx, cy);
                    if (!_cells.TryGetValue(k, out var list)) _cells[k] = list = new List<int>();
                    list.Add(id);
                }
        }

        public IEnumerable<int> Query(Rect r)
        {
            var seen = new HashSet<int>();
            for (int cx = (int)Math.Floor(r.Left / _cell); cx <= (int)Math.Floor(r.Right / _cell); cx++)
                for (int cy = (int)Math.Floor(r.Bottom / _cell); cy <= (int)Math.Floor(r.Top / _cell); cy++)
                    if (_cells.TryGetValue(Key(cx, cy), out var list))
                        foreach (var id in list)
                            if (seen.Add(id)) yield return id;
        }
    }
}
