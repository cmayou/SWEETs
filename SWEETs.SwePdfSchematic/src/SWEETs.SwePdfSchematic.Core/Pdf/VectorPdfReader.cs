using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SWEETs.SwePdfSchematic.Core.Geometry;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics;

namespace SWEETs.SwePdfSchematic.Core.Pdf
{
    public sealed class RawPage
    {
        public int Number;
        public double WidthMm;
        public double HeightMm;
        public int Rotation;
        public int ImageCount;
        public List<Segment> Segments = new List<Segment>();
        public List<PathShape> Shapes = new List<PathShape>();
        public List<TextItem> Texts = new List<TextItem>();
        /// <summary>Dashed boxes (SOLIDWORKS Electrical location / function outlines), removed from the linework.</summary>
        public List<RegionBox> Regions = new List<RegionBox>();
        public bool IsRasterOnly => Segments.Count < 5 && Texts.Count < 3 && ImageCount > 0;
    }

    /// <summary>
    /// Local, offline PDF reader. Extracts vector linework (lines, rectangles, flattened curves) and
    /// positioned words from CAD-exported PDFs using PdfPig. Nothing leaves the machine.
    /// </summary>
    public static class VectorPdfReader
    {
        private const double PtToMm = 25.4 / 72.0;
        private const int CurveSteps = 8;

        public static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
        }

        public static int PageCount(string path)
        {
            using (var doc = PdfDocument.Open(path)) return doc.NumberOfPages;
        }

        public static IEnumerable<RawPage> ReadPages(string path)
        {
            using (var doc = PdfDocument.Open(path))
            {
                for (int i = 1; i <= doc.NumberOfPages; i++)
                    yield return ReadPage(doc.GetPage(i));
            }
        }

        private static RawPage ReadPage(Page page)
        {
            var raw = new RawPage
            {
                Number = page.Number,
                WidthMm = page.Width * PtToMm,
                HeightMm = page.Height * PtToMm,
                Rotation = page.Rotation.Value,
            };

            try { raw.ImageCount = page.GetImages().Count(); } catch { raw.ImageCount = 0; }

            int pathId = 0;
            foreach (var path in GetPaths(page))
            {
                if (!path.IsStroked && !path.IsFilled) continue; // clipping paths
                double lw = Convert.ToDouble(path.LineWidth) * PtToMm;
                var style = new Style
                {
                    Color = ToRgb(path.IsStroked ? path.StrokeColor : path.FillColor),
                    Dashed = path.IsStroked && path.LineDashPattern.HasValue && path.LineDashPattern.Value.Array != null && path.LineDashPattern.Value.Array.Count > 0
                             && path.LineDashPattern.Value.Array.Any(v => v > 0),
                    RoundCap = path.LineCapStyle == UglyToad.PdfPig.Graphics.Core.LineCapStyle.Round,
                };
                foreach (var sub in path)
                {
                    pathId++;
                    ReadSubpath(sub, pathId, path.IsFilled, path.IsStroked, lw, style, raw);
                }
            }
            LinetypeNormalizer.Normalize(raw);

            foreach (var word in GetWords(page))
            {
                if (string.IsNullOrWhiteSpace(word.Text)) continue;
                var b = word.BoundingBox;
                raw.Texts.Add(new TextItem
                {
                    Text = word.Text.Trim(),
                    Bounds = new Rect(
                        Math.Min(b.Left, b.Right) * PtToMm, Math.Min(b.Bottom, b.Top) * PtToMm,
                        Math.Max(b.Left, b.Right) * PtToMm, Math.Max(b.Bottom, b.Top) * PtToMm),
                    Direction = MapDirection(word.TextOrientation),
                    FontSize = word.Letters.Count > 0 ? word.Letters[0].PointSize * PtToMm : 0,
                });
            }
            return raw;
        }

        /// <summary>
        /// Nearest-neighbour word grouping keeps rotated labels whole ("L1-1" up a vertical wire, "04F1" beside a fuse);
        /// PdfPig's default extractor returns rotated text one letter per word.
        /// </summary>
        private static IEnumerable<Word> GetWords(Page page)
        {
            try { return page.GetWords(UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor.NearestNeighbourWordExtractor.Instance).ToList(); }
            catch { return page.GetWords(); }
        }

        // Isolated so a PdfPig API move (ExperimentalAccess.Paths → Page.Paths in newer releases) is a one-line change.
#pragma warning disable CS0618 // may be marked obsolete in newer PdfPig versions
        private static IEnumerable<PdfPath> GetPaths(Page page) => page.ExperimentalAccess.Paths;
#pragma warning restore CS0618

        private struct Style { public int Color; public bool Dashed; public bool RoundCap; }

        private static int ToRgb(UglyToad.PdfPig.Graphics.Colors.IColor c)
        {
            if (c == null) return -1;
            try
            {
                var (r, g, b) = c.ToRGBValues();
                int C(double v) => Math.Max(0, Math.Min(255, (int)Math.Round(v * 255)));
                return (C(r) << 16) | (C(g) << 8) | C(b);
            }
            catch { return -1; }
        }

        private static void ReadSubpath(PdfSubpath sub, int id, bool filled, bool stroked, double lw, Style style, RawPage raw)
        {
            var pts = new List<Pt>();
            bool curved = false;
            bool closed = false;
            Pt cur = default, start = default;
            bool hasCur = false;

            void Emit(Pt a, Pt b, bool fromCurve)
            {
                if (a.DistanceTo(b) < 1e-4) return;
                raw.Segments.Add(new Segment(a, b) { PathId = id, FromCurve = fromCurve, FromFilledPath = filled, LineWidth = lw, Color = style.Color, Dashed = style.Dashed });
            }

            int firstSeg = raw.Segments.Count;
            foreach (var cmd in sub.Commands)
            {
                switch (cmd)
                {
                    case PdfSubpath.Move m:
                        cur = start = Mm(m.Location); hasCur = true; pts.Add(cur);
                        break;
                    case PdfSubpath.Line l:
                        {
                            var a = Mm(l.From); var b = Mm(l.To);
                            if (!hasCur) { start = a; hasCur = true; pts.Add(a); }
                            Emit(a, b, false); cur = b; pts.Add(b);
                            break;
                        }
                    case PdfSubpath.BezierCurve c:
                        {
                            curved = true;
                            var p0 = Mm(c.StartPoint); var p3 = Mm(c.EndPoint);
                            ControlPoints(c, p0, p3, out var c1, out var c2);
                            if (!hasCur) { start = p0; hasCur = true; pts.Add(p0); }
                            var prev = p0;
                            for (int i = 1; i <= CurveSteps; i++)
                            {
                                double t = (double)i / CurveSteps, u = 1 - t;
                                var p = new Pt(
                                    u * u * u * p0.X + 3 * u * u * t * c1.X + 3 * u * t * t * c2.X + t * t * t * p3.X,
                                    u * u * u * p0.Y + 3 * u * u * t * c1.Y + 3 * u * t * t * c2.Y + t * t * t * p3.Y);
                                Emit(prev, p, true); prev = p; pts.Add(p);
                            }
                            cur = p3;
                            break;
                        }
                    case PdfSubpath.Close _:
                        if (hasCur) Emit(cur, start, curved);
                        closed = true; cur = start;
                        break;
                }
            }

            if (pts.Count == 0) return;

            // A stroked path with no extent and a wide pen is a dot (round cap) — junction dots, dash-dot linetypes.
            var ext = Rect.FromPoints(pts);
            if (stroked && !filled && raw.Segments.Count - firstSeg <= 32 && Math.Max(ext.Width, ext.Height) < 0.05)
            {
                raw.Segments.RemoveRange(firstSeg, raw.Segments.Count - firstSeg);
                if (lw >= 0.3)
                {
                    var c = ext.Center; double r = lw / 2;
                    raw.Shapes.Add(new PathShape { Id = id, Closed = true, Filled = true, Stroked = false, HasCurves = true, VertexCount = 8,
                                                   Bounds = new Rect(c.X - r, c.Y - r, c.X + r, c.Y + r), Color = style.Color, IsDot = true });
                }
                return;
            }
            if (!closed && pts.Count > 2 && pts[0].DistanceTo(pts[pts.Count - 1]) < 0.05) closed = true;
            if (filled) closed = true; // fill implicitly closes

            for (int i = firstSeg; i < raw.Segments.Count; i++) raw.Segments[i].FromClosedPath = closed;

            // distinct vertices (curves count as one "vertex" family; we only need polygon vertex counts)
            int vertices = curved ? pts.Count : CountDistinct(pts);
            raw.Shapes.Add(new PathShape
            {
                Id = id,
                Closed = closed,
                Filled = filled,
                Stroked = stroked,
                HasCurves = curved,
                VertexCount = vertices,
                Bounds = Rect.FromPoints(pts),
                Color = style.Color,
            });
        }

        /// <summary>
        /// Control points of a Bézier segment. PdfPig renamed/split these between releases
        /// (FirstControlPoint/SecondControlPoint on a cubic curve, a single ControlPoint on a quadratic one),
        /// so they are read by reflection to keep this code independent of the PdfPig version.
        /// A quadratic curve is raised to cubic; with no control points the curve degrades to a straight line.
        /// </summary>
        private static void ControlPoints(object curve, Pt p0, Pt p3, out Pt c1, out Pt c2)
        {
            var props = curve.GetType().GetProperties()
                .Where(pi => pi.PropertyType == typeof(PdfPoint) && pi.Name != "StartPoint" && pi.Name != "EndPoint")
                .ToList();
            PdfPoint? Get(params string[] names)
            {
                foreach (var n in names)
                {
                    var pi = props.FirstOrDefault(x => x.Name == n);
                    if (pi != null) return (PdfPoint)pi.GetValue(curve);
                }
                return null;
            }

            var first = Get("FirstControlPoint", "ControlPoint1", "Control1");
            var second = Get("SecondControlPoint", "ControlPoint2", "Control2");
            if (first.HasValue && second.HasValue) { c1 = Mm(first.Value); c2 = Mm(second.Value); return; }

            var single = Get("ControlPoint", "Control") ?? (props.Count == 1 ? (PdfPoint?)props[0].GetValue(curve) : null);
            if (single.HasValue)
            {
                var q = Mm(single.Value); // quadratic → cubic
                c1 = new Pt(p0.X + 2.0 / 3 * (q.X - p0.X), p0.Y + 2.0 / 3 * (q.Y - p0.Y));
                c2 = new Pt(p3.X + 2.0 / 3 * (q.X - p3.X), p3.Y + 2.0 / 3 * (q.Y - p3.Y));
                return;
            }
            if (props.Count >= 2) { c1 = Mm((PdfPoint)props[0].GetValue(curve)); c2 = Mm((PdfPoint)props[1].GetValue(curve)); return; }
            c1 = p0; c2 = p3;
        }

        private static int CountDistinct(List<Pt> pts)
        {
            var list = new List<Pt>();
            foreach (var p in pts)
                if (!list.Any(q => q.DistanceTo(p) < 0.05)) list.Add(p);
            return list.Count;
        }

        private static Pt Mm(PdfPoint p) => new Pt(p.X * PtToMm, p.Y * PtToMm);

        private static Geometry.TextDirection MapDirection(TextOrientation o)
        {
            switch (o)
            {
                case TextOrientation.Horizontal: return Geometry.TextDirection.Horizontal;
                case TextOrientation.Rotate90: return Geometry.TextDirection.Rotate90;
                case TextOrientation.Rotate180: return Geometry.TextDirection.Rotate180;
                case TextOrientation.Rotate270: return Geometry.TextDirection.Rotate270;
                default: return Geometry.TextDirection.Other;
            }
        }
    }
}
