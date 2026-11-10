using System;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Pdf;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Finds the drawing frame, border bands (column numbering) and title block, and captures the
    /// title-block text as documentation (drawing no., revision, title, sheet...) for the SWE project.
    /// </summary>
    public sealed class PageLayoutAnalyzer
    {
        private readonly RecognitionOptions _o;
        public PageLayoutAnalyzer(RecognitionOptions o) { _o = o; }

        public sealed class Layout
        {
            public Rect Frame;
            public List<Rect> Excluded = new List<Rect>();
            public Rect? TitleBlock;
            public HashSet<int> BorderPathIds = new HashSet<int>();
            public int ColumnCount;
            /// <summary>Zones are numbered rows down the left edge (SWE ANSI templates) instead of columns.</summary>
            public bool ZonesAreRows;
            public string SheetNumber;
            /// <summary>Scaled panel / cabinet layout drawing, not a schematic.</summary>
            public bool IsLayoutDrawing;
        }

        public Layout Analyze(RawPage page)
        {
            var layout = new Layout();
            double pageArea = page.WidthMm * page.HeightMm;

            var rects = page.Shapes.Where(s => s.IsAxisRectangle && s.Stroked).ToList();
            var frameShape = rects.Where(r => r.Bounds.Area >= 0.5 * pageArea).OrderByDescending(r => r.Bounds.Area).FirstOrDefault();
            if (frameShape != null)
            {
                layout.Frame = frameShape.Bounds;
                layout.BorderPathIds.Add(frameShape.Id);
            }
            else layout.Frame = FrameFromEdges(page, layout.BorderPathIds) ?? new Rect(5, 5, page.WidthMm - 5, page.HeightMm - 5);
            var f = layout.Frame;

            // Title block: a rectangle docked to the frame bottom-right, a full-width bottom strip or a full-height right strip.
            foreach (var r in rects)
            {
                if (frameShape != null && r.Id == frameShape.Id) continue;
                var b = r.Bounds;
                bool onBottom = Math.Abs(b.Bottom - f.Bottom) < 1.0;
                bool onRight = Math.Abs(b.Right - f.Right) < 1.0;
                bool corner = onBottom && onRight && b.Width > 40 && b.Area < 0.25 * f.Area;
                bool bottomStrip = onBottom && b.Width > 0.9 * f.Width && b.Height < 0.2 * f.Height;
                bool rightStrip = onRight && b.Height > 0.9 * f.Height && b.Width < 0.2 * f.Width;
                if (corner || bottomStrip || rightStrip)
                {
                    layout.Excluded.Add(b);
                    layout.BorderPathIds.Add(r.Id);
                    layout.TitleBlock = layout.TitleBlock.HasValue ? layout.TitleBlock.Value.Union(b) : b;
                }
            }

            // Border bands: full-width/height dividers close to the frame edges (column/row numbering).
            foreach (var s in page.Segments)
            {
                if (s.Axis == Axis.Horizontal && s.Length > 0.9 * f.Width)
                {
                    double y = s.A.Y;
                    if (f.Top - y > 0.5 && f.Top - y < _o.BorderBandFraction * f.Height) layout.Excluded.Add(new Rect(f.Left, y, f.Right, f.Top));
                    else if (y - f.Bottom > 0.5 && y - f.Bottom < _o.TitleBandFraction * f.Height && !s.Dashed)
                    {
                        // Title block drawn as lines (no rectangle): a full-width rule with text below it.
                        var band = new Rect(f.Left, f.Bottom, f.Right, y);
                        if (y - f.Bottom >= _o.BorderBandFraction * f.Height && page.Texts.Count(t => band.Contains(t.Center)) < 3) continue;
                        layout.Excluded.Add(band);
                        layout.BorderPathIds.Add(s.PathId);
                        if (!layout.TitleBlock.HasValue || layout.TitleBlock.Value.Top < y) layout.TitleBlock = band;
                    }
                }
                else if (s.Axis == Axis.Vertical && s.Length > 0.9 * f.Height)
                {
                    double x = s.A.X;
                    if (x - f.Left > 0.5 && x - f.Left < 0.08 * f.Width) layout.Excluded.Add(new Rect(f.Left, f.Bottom, x, f.Top));
                    else if (f.Right - x > 0.5 && f.Right - x < 0.08 * f.Width) layout.Excluded.Add(new Rect(x, f.Bottom, f.Right, f.Top));
                }
            }

            layout.ColumnCount = DetectColumns(page, f);
            if (layout.ColumnCount == 0)
            {
                layout.ColumnCount = DetectRows(page, f);
                layout.ZonesAreRows = layout.ColumnCount > 0;
            }
            layout.SheetNumber = FindSheetNumber(page);
            layout.IsLayoutDrawing = HasScale(page);
            return layout;
        }

        /// <summary>
        /// Scaled drawings (panel / cabinet layouts) print a "SCALE" label with a ratio such as "1/10" in or beside it.
        /// </summary>
        private static bool HasScale(RawPage page)
        {
            var rx = new System.Text.RegularExpressions.Regex(@"\bSCALE\b\s*:?\s*1\s*[/:]\s*\d+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (page.Texts.Any(t => rx.IsMatch(t.Text))) return true;
            var ratio = new System.Text.RegularExpressions.Regex(@"^1\s*[/:]\s*\d+$");
            foreach (var label in page.Texts.Where(t => t.Text.Trim().TrimEnd(':').Equals("SCALE", StringComparison.OrdinalIgnoreCase)))
                if (page.Texts.Any(t => ratio.IsMatch(t.Text.Trim()) && t.Bounds.DistanceTo(label.Bounds) < 12)) return true;
            return false;
        }

        /// <summary>
        /// Frame drawn without a rectangle: border lines or thin filled bars spanning most of the sheet. Returns their
        /// bounding box when both a horizontal and a vertical edge are found.
        /// </summary>
        private static Rect? FrameFromEdges(RawPage page, HashSet<int> borderIds)
        {
            double w = page.WidthMm, h = page.HeightMm;
            var edges = new List<(Rect r, int id)>();
            foreach (var s in page.Segments)
            {
                if (s.Dashed) continue;
                if (s.Axis == Axis.Horizontal && s.Length >= 0.85 * w) edges.Add((s.Bounds, s.PathId));
                else if (s.Axis == Axis.Vertical && s.Length >= 0.85 * h) edges.Add((s.Bounds, s.PathId));
            }
            foreach (var sh in page.Shapes.Where(x => x.Filled && x.IsAxisRectangle))
            {
                var b = sh.Bounds;
                if ((b.Width >= 0.85 * w && b.Height < 2.5) || (b.Height >= 0.85 * h && b.Width < 2.5)) edges.Add((b, sh.Id));
            }
            bool hasH = edges.Any(e => e.r.Width >= 0.85 * w), hasV = edges.Any(e => e.r.Height >= 0.85 * h);
            if (!hasH || !hasV) return null;
            var frame = edges.Select(e => e.r).Aggregate((a, b) => a.Union(b));
            // keep only the outer edges as border paths (inner full-width rules are title-block lines, handled later)
            foreach (var e in edges)
            {
                var r = e.r;
                bool outer = Math.Abs(r.Left - frame.Left) < 1.5 || Math.Abs(r.Right - frame.Right) < 1.5
                             || Math.Abs(r.Bottom - frame.Bottom) < 1.5 || Math.Abs(r.Top - frame.Top) < 1.5;
                if (outer && e.id > 0) borderIds.Add(e.id);
            }
            return frame;
        }

        public bool IsExcluded(Layout l, Pt p) =>
            !l.Frame.Contains(p, -0.5) || l.Excluded.Any(r => r.Contains(p, 0.2));

        /// <summary>Column numbers are small sequential integers in the top band, spread across the sheet.</summary>
        private static int DetectColumns(RawPage page, Rect f)
        {
            var top = page.Texts
                .Where(t => t.Center.Y > f.Top - 0.12 * f.Height && t.Center.Y <= f.Top + 12 && int.TryParse(t.Text, out var n) && n >= 1 && n <= 40)
                .OrderBy(t => t.Center.X).ToList();
            return LongestRun(top, t => t.Center.X, f.Width);
        }

        /// <summary>Zone numbers down the left edge: small sequential integers, top to bottom, spread down the sheet.</summary>
        private static int DetectRows(RawPage page, Rect f)
        {
            var left = page.Texts
                .Where(t => t.Center.X < f.Left + 0.06 * f.Width && t.Center.X > f.Left - 12 && int.TryParse(t.Text, out var n) && n >= 1 && n <= 40)
                .OrderByDescending(t => t.Center.Y).ToList();
            return LongestRun(left, t => -t.Center.Y, f.Height);
        }

        /// <summary>
        /// Longest run 1,2,3… in reading order whose labels span at least half the frame: pin numbers printed near
        /// the border (1-2-3 on a fuse) form short runs packed into a few millimetres and are rejected.
        /// </summary>
        private static int LongestRun(List<TextItem> items, Func<TextItem, double> pos, double extent)
        {
            int best = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Text.Trim() != "1") continue;
                int expect = 2, len = 1; double start = pos(items[i]), end = start;
                for (int j = i + 1; j < items.Count; j++)
                    if (int.Parse(items[j].Text) == expect) { expect++; len++; end = pos(items[j]); }
                if (len >= 3 && end - start >= 0.5 * extent) best = Math.Max(best, len);
            }
            return best;
        }

        /// <summary>Sheet number: the number printed under or beside a SHEET / SCHEME / FOLIO / PAGE label.</summary>
        private static string FindSheetNumber(RawPage page)
        {
            var labels = page.Texts.Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.Text.Trim().TrimEnd(':', '.'), @"^(SHEET|SCHEME|FOLIO|PAGE|SHT|BLATT)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).ToList();
            foreach (var label in labels)
            {
                var hit = page.Texts
                    .Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.Text.Trim(), @"^\d{1,4}[A-Z]?$")
                                && t.Center.Y <= label.Center.Y + 1 && label.Center.Y - t.Center.Y < 15
                                && t.Bounds.Left < label.Bounds.Right + 25 && t.Bounds.Right > label.Bounds.Left - 5)
                    .OrderBy(t => t.Bounds.DistanceTo(label.Bounds)).FirstOrDefault();
                if (hit != null) return hit.Text.Trim();
            }
            return null;
        }

        /// <summary>A location box is labelled just above (or inside) its top-left corner: "L2 - Conveyors".</summary>
        public LocationBox LabelRegion(RawPage page, RegionBox r)
        {
            var b = r.Bounds;
            var near = page.Texts.Where(t => t.Direction == TextDirection.Horizontal
                                            && t.Center.Y > b.Top - 6 && t.Center.Y < b.Top + 10
                                            && t.Bounds.Left > b.Left - 5 && t.Bounds.Left < b.Left + 0.6 * b.Width)
                                 .OrderBy(t => Math.Abs(t.Center.Y - b.Top)).ToList();
            var box = new LocationBox { Bounds = b };
            if (near.Count == 0) return box;
            double y = near[0].Center.Y;
            var words = near.Where(t => Math.Abs(t.Center.Y - y) < 1.0).OrderBy(t => t.Bounds.Left).Select(t => t.Text).ToList();
            var label = string.Join(" ", words);
            r.Label = label;
            var parts = label.Split(new[] { " - ", " – " }, 2, StringSplitOptions.None);
            box.Name = parts[0].Trim();
            box.Description = parts.Length > 1 ? parts[1].Trim() : null;
            return box;
        }

        public TitleBlockInfo ReadTitleBlock(RawPage page, Layout l)
        {
            var info = new TitleBlockInfo();
            if (!l.TitleBlock.HasValue) return info;
            var tb = l.TitleBlock.Value.Inflate(0.5);
            var words = page.Texts.Where(t => tb.Contains(t.Center) && t.Direction == TextDirection.Horizontal)
                                  .OrderByDescending(t => t.Center.Y).ThenBy(t => t.Bounds.Left).ToList();
            info.RawText.AddRange(words.Select(w => w.Text));

            // Group into baseline rows, then read "LABEL: value" pairs left to right.
            var rows = new List<List<TextItem>>();
            foreach (var w in words)
            {
                var row = rows.FirstOrDefault(r => Math.Abs(r[0].Center.Y - w.Center.Y) < 1.5);
                if (row == null) rows.Add(row = new List<TextItem>());
                row.Add(w);
            }

            foreach (var row in rows)
            {
                row.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
                string pendingKey = null;
                var buf = new List<TextItem>();
                foreach (var w in row)
                {
                    if (!w.Text.EndsWith(":")) { buf.Add(w); continue; }
                    // words chained tightly before the colon word belong to the label ("DWG NO:")
                    var keyWords = new List<TextItem> { w };
                    int j = buf.Count - 1;
                    while (j >= 0 && keyWords[0].Bounds.Left - buf[j].Bounds.Right < 2.5) { keyWords.Insert(0, buf[j]); j--; }
                    var value = buf.Take(j + 1).ToList();
                    Commit(info, pendingKey, value);
                    pendingKey = string.Join(" ", keyWords.Select(k => k.Text)).TrimEnd(':').Trim();
                    buf.Clear();
                }
                Commit(info, pendingKey, buf);
            }
            return info;
        }

        private static void Commit(TitleBlockInfo info, string key, List<TextItem> value)
        {
            if (string.IsNullOrEmpty(key) || value.Count == 0 || info.Fields.ContainsKey(key)) return;
            info.Fields[key] = string.Join(" ", value.Select(v => v.Text));
        }
    }
}
