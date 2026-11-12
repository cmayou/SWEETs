using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Pdf;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Reads the manufacturer parts out of the bill-of-material tables a CAD export appends to the drawings, and
    /// organises them per mark so they can be put on the SOLIDWORKS Electrical components.
    ///
    /// Tables differ per report (column order, wrapped headers such as "Article Number (Internal PN)", wrapped cells,
    /// with or without an item number column), so columns are found from the header text: a header is a line with a
    /// Mark/Tag column plus a Reference/Part number or Article number column. Header words stacked on several lines
    /// form one column; words close together on a line form one title ("Part Number").
    /// Rows of the different reports are merged per mark and part reference.
    /// </summary>
    public static class BomReader
    {
        private enum Role { None, Tag, Article, Reference, Manufacturer, Description, Quantity }

        private sealed class Line
        {
            public double Y;
            public List<TextItem> Words = new List<TextItem>();
            public string Text => string.Join(" ", Words.Select(w => w.Text));
        }

        private sealed class Column
        {
            public string Title;
            public double Left, Right;
            public Role Role;
        }

        private const double PhraseGap = 2.0;   // mm between words of one header title
        private const double HeaderBand = 6.0;  // mm: wrapped header lines above/below the main header line
        private const double CellSlack = 2.0;   // mm: cell text may start a little left of its column title

        private static readonly Regex FooterWords = new Regex(@"\bREVISION\b|\bREV\.|LOCATION:|CONTRACT:|DESIGN OFFICE|\bSCHEME\b", RegexOptions.IgnoreCase);
        private static readonly Regex ItemNumber = new Regex(@"^\d{1,4}$");

        // ------------------------------------------------------------------ reading tables

        /// <summary>Adds every parts-table row of the page to <paramref name="lines"/>.</summary>
        public static void Harvest(RawPage page, List<BomLine> lines)
        {
            var all = Lines(page);
            for (int h = 0; h < all.Count; h++)
            {
                if (!Phrases(all[h]).Any(p => RoleOf(p.Title) == Role.Tag)) continue;

                // header band: the tag line plus wrapped title lines close above/below it (not data rows)
                var band = new List<Line> { all[h] };
                for (int j = 0; j < all.Count; j++)
                {
                    if (j == h || Math.Abs(all[j].Y - all[h].Y) > HeaderBand) continue;
                    if (ItemNumber.IsMatch(all[j].Words[0].Text)) continue;
                    band.Add(all[j]);
                }
                var cols = new List<Column>();
                foreach (var ln in band.OrderByDescending(l => l.Y))
                    foreach (var ph in Phrases(ln))
                    {
                        var c = cols.FirstOrDefault(x => ph.Left < x.Right + 0.5 && ph.Right > x.Left - 0.5);
                        if (c != null) { c.Title += " " + ph.Title; c.Left = Math.Min(c.Left, ph.Left); c.Right = Math.Max(c.Right, ph.Right); }
                        else cols.Add(ph);
                    }
                cols = cols.OrderBy(c => c.Left).ToList();
                foreach (var c in cols) c.Role = RoleOf(c.Title);
                Column Col(Role r) => cols.FirstOrDefault(c => c.Role == r);
                var tag = Col(Role.Tag);
                var reference = Col(Role.Reference);
                var article = Col(Role.Article);
                if (tag == null || (reference == null && article == null)) continue;

                // body: lines under the header until the next header or the title block
                double top = band.Min(l => l.Y);
                var body = new List<Line>();
                foreach (var ln in all)
                {
                    if (ln.Y >= top - 0.5) continue;
                    if (Phrases(ln).Count(p => RoleOf(p.Title) != Role.None) >= 3) break;
                    if (FooterWords.IsMatch(ln.Text)) break;
                    body.Add(ln);
                }
                if (body.Count == 0) continue;

                List<TextItem> In(Column c, Line ln)
                {
                    int i = cols.IndexOf(c);
                    double lo = c.Left - CellSlack, hi = i + 1 < cols.Count ? cols[i + 1].Left - CellSlack : double.MaxValue;
                    return ln.Words.Where(w => w.Bounds.Left >= lo && w.Bounds.Left < hi).ToList();
                }
                double ColumnEnd(Column c) { int i = cols.IndexOf(c); return i + 1 < cols.Count ? cols[i + 1].Left - CellSlack : double.MaxValue; }

                // rows: an item number column anchors rows (cells may wrap above and below it); otherwise every line
                // with a reference starts a row and the lines after it continue that row
                // item numbers increase down the table and share one column; a number that skips ahead a little is
                // still an item (a row may be unreadable), a stray number elsewhere (address, sheet number) is not
                var anchors = new List<Line>();
                int? prev = null; double? itemX = null;
                foreach (var ln in body)
                {
                    var n = ln.Words.FirstOrDefault(w => w.Bounds.Right < tag.Left - 1 && ItemNumber.IsMatch(w.Text));
                    if (n == null) continue;
                    int v = int.Parse(n.Text, CultureInfo.InvariantCulture);
                    if (prev.HasValue && (v <= prev.Value || v > prev.Value + 3)) continue;
                    if (itemX.HasValue && Math.Abs(n.Bounds.Right - itemX.Value) > 4) continue;
                    anchors.Add(ln); prev = v; itemX = itemX ?? n.Bounds.Right;
                }
                var rows = new List<List<Line>>();
                if (anchors.Count >= 2)
                {
                    for (int a = 0; a < anchors.Count; a++)
                    {
                        double hiY = a == 0 ? double.MaxValue : (anchors[a - 1].Y + anchors[a].Y) / 2;
                        double loY = a == anchors.Count - 1 ? double.MinValue : (anchors[a].Y + anchors[a + 1].Y) / 2;
                        rows.Add(body.Where(l => l.Y <= hiY && l.Y > loY).ToList());
                    }
                }
                else
                {
                    foreach (var ln in body)
                    {
                        bool starts = (reference != null && In(reference, ln).Any()) || (article != null && In(article, ln).Any());
                        if (starts || rows.Count == 0) rows.Add(new List<Line> { ln });
                        else rows[rows.Count - 1].Add(ln);
                    }
                }

                foreach (var row in rows)
                {
                    string Cell(Column c, bool mark, out bool wrapped)
                    {
                        wrapped = false;
                        if (c == null) return null;
                        string s = null; double prevRight = 0;
                        foreach (var ln in row.OrderByDescending(l => l.Y))
                        {
                            var ws = In(c, ln);
                            if (ws.Count == 0) continue;
                            var frag = string.Join(" ", ws.Select(w => w.Text));
                            if (s == null) s = frag;
                            else
                            {
                                bool full = ColumnEnd(c) - prevRight < 6; // the previous line ran to the column edge
                                string glue = mark ? (s.EndsWith("-") || frag.StartsWith("-") || frag.StartsWith(";") || frag.StartsWith(",") ? "" : " ")
                                                   : (full || !s.Contains(" ") ? "" : " ");
                                if (!mark) wrapped = true;
                                s += glue + frag;
                            }
                            prevRight = ws.Max(w => w.Bounds.Right);
                        }
                        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
                    }
                    string Text(Column c)
                    {
                        if (c == null) return null;
                        var parts = row.OrderByDescending(l => l.Y).Select(l => string.Join(" ", In(c, l).Select(w => w.Text))).Where(t => t.Length > 0);
                        var s = string.Join(" ", parts).Trim();
                        return s.Length == 0 ? null : s;
                    }

                    var marks = Cell(tag, true, out _);
                    if (marks == null) continue;
                    var refText = Cell(reference, false, out var refWrapped);
                    var art = Cell(article, false, out _);
                    if (refText == null && art == null) continue;
                    lines.Add(new BomLine
                    {
                        MarksText = marks,
                        Reference = refText,
                        ReferenceWrapped = refWrapped,
                        ArticleNumber = art,
                        Manufacturer = Text(Col(Role.Manufacturer)),
                        Description = Text(Col(Role.Description)),
                        QuantityText = Text(Col(Role.Quantity)),
                        SourcePage = page.Number,
                    });
                }
            }
        }

        private static Role RoleOf(string title)
        {
            var n = Regex.Replace(title ?? "", "[^A-Za-z/]", "").ToUpperInvariant();
            if (n.Contains("MARK") || n.StartsWith("TAG")) return Role.Tag;
            if (n.Contains("ARTICLE") || n.Contains("INTERNAL")) return Role.Article;
            if (n.Contains("REFERENCE") || n.Contains("PARTNUMBER") || n == "P/N" || n == "PN" || n == "PART" || n == "PARTNO") return Role.Reference;
            if (n.Contains("MFG") || n.Contains("MANUFACTURER")) return Role.Manufacturer;
            if (n.StartsWith("DESC")) return Role.Description;
            if (n.Contains("QUANTITY") || n == "QTY") return Role.Quantity;
            return Role.None;
        }

        private static List<Column> Phrases(Line line)
        {
            var list = new List<Column>();
            foreach (var w in line.Words)
            {
                var last = list.LastOrDefault();
                if (last != null && w.Bounds.Left - last.Right < PhraseGap) { last.Title += " " + w.Text; last.Right = w.Bounds.Right; }
                else list.Add(new Column { Title = w.Text, Left = w.Bounds.Left, Right = w.Bounds.Right });
            }
            return list;
        }

        private static List<Line> Lines(RawPage page)
        {
            var lines = new List<Line>();
            foreach (var w in page.Texts.Where(t => t.Direction == TextDirection.Horizontal && !string.IsNullOrWhiteSpace(t.Text)).OrderByDescending(t => t.Center.Y))
            {
                var line = lines.FirstOrDefault(l => Math.Abs(l.Y - w.Center.Y) < 1.2);
                if (line == null) lines.Add(line = new Line { Y = w.Center.Y });
                line.Words.Add(w);
            }
            foreach (var l in lines) l.Words.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
            return lines.OrderByDescending(l => l.Y).ToList();
        }

        // ------------------------------------------------------------------ organising per mark

        private static readonly Regex WireWords = new Regex(@"\b(CONDUCTOR|AWG|HOOK\s*UP\s*WIRE|WIRE)\b", RegexOptions.IgnoreCase);
        private static readonly Regex LocationWords = new Regex(@"ENCLOS|MOUNTING PLATE|GLAND|CABINET|PANEL", RegexOptions.IgnoreCase);
        private static readonly Regex CableMark = new Regex(@"^W\d+[A-Z]?$", RegexOptions.IgnoreCase);
        private static readonly Regex TerminalMark = new Regex(@"^(\S+)\s+(\d{1,4})$");

        /// <summary>
        /// Merges the table rows of all reports into one parts list per mark (<see cref="ImportDocument.Bom"/>).
        /// Reviewer edits are kept when the document is organised again.
        /// </summary>
        public static void Organize(ImportDocument doc)
        {
            var previous = doc.Bom.GroupBy(b => b.Key).ToDictionary(g => g.Key, g => g.Last());
            var known = new HashSet<string>(doc.AllSymbols.Where(s => s.Tag != null).Select(s => BomText.MarkKey(s.Tag)));
            var locationTags = LocationTags(doc);

            // manufacturers seen anywhere: fills reports without a manufacturer column and splits "Lapp_1015-18-1-4"
            var makers = doc.BomLines.Where(l => !string.IsNullOrWhiteSpace(l.Manufacturer)).Select(l => l.Manufacturer.Trim())
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var makerByRef = new Dictionary<string, string>();
            foreach (var l in doc.BomLines.Where(l => !string.IsNullOrWhiteSpace(l.Manufacturer) && l.Reference != null))
                if (!makerByRef.ContainsKey(BomText.Key(l.Reference))) makerByRef[BomText.Key(l.Reference)] = l.Manufacturer.Trim();

            var targets = new Dictionary<string, BomTarget>();
            var variants = new Dictionary<(string mark, string part), List<BomLine>>();
            foreach (var line in doc.BomLines)
            {
                var (reference, maker) = SplitFormatted(line.Reference, line.Manufacturer, makers);
                if (string.IsNullOrWhiteSpace(reference)) reference = line.ArticleNumber;
                if (string.IsNullOrWhiteSpace(reference)) continue;
                bool wire = WireWords.IsMatch(line.Description ?? "") && !Regex.IsMatch(line.Description ?? "", "TERMINAL", RegexOptions.IgnoreCase);

                var marks = SplitMarks(line.MarksText).ToList();
                if (marks.Count == 0) continue;
                foreach (var (mark, strip, number) in marks)
                {
                    var kind = wire ? BomItemKind.Wire
                             : strip != null ? BomItemKind.Terminal
                             : known.Contains(BomText.MarkKey(mark)) ? BomItemKind.Component
                             : CableMark.IsMatch(mark) ? BomItemKind.Cable
                             : locationTags.Contains(mark.ToUpperInvariant()) || (Regex.IsMatch(mark, @"^L\d+$", RegexOptions.IgnoreCase) && LocationWords.IsMatch(line.Description ?? "")) ? BomItemKind.Location
                             : BomItemKind.Component;
                    var key = BomText.MarkKey(mark);
                    if (!targets.TryGetValue(key, out var t))
                        targets[key] = t = new BomTarget { Mark = mark, Kind = kind, Strip = strip, TerminalNumber = number };
                    else if (t.Kind == BomItemKind.Wire && kind != BomItemKind.Wire) t.Kind = kind;
                    var pk = (key, BomText.Key(reference));
                    if (!variants.TryGetValue(pk, out var list)) variants[pk] = list = new List<BomLine>();
                    list.Add(new BomLine
                    {
                        MarksText = line.MarksText, Reference = reference, ReferenceWrapped = line.ReferenceWrapped,
                        Manufacturer = maker ?? (makerByRef.TryGetValue(BomText.Key(reference), out var mk) ? mk : null),
                        ArticleNumber = line.ArticleNumber, Description = line.Description,
                        QuantityText = PerMark(line.QuantityText, marks.Count), SourcePage = line.SourcePage,
                    });
                }
            }

            foreach (var kv in variants)
            {
                var t = targets[kv.Key.mark];
                var rows = kv.Value;
                t.Parts.Add(new PartAssignment
                {
                    Reference = rows.OrderBy(r => r.ReferenceWrapped).ThenByDescending(r => r.Reference.Length).First().Reference.Trim(),
                    Manufacturer = rows.Select(r => r.Manufacturer).FirstOrDefault(m => !string.IsNullOrWhiteSpace(m))?.Trim(),
                    ArticleNumber = rows.Select(r => r.ArticleNumber).FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim(),
                    Description = rows.Select(r => r.Description).Where(d => !string.IsNullOrWhiteSpace(d)).OrderByDescending(d => d.Length).FirstOrDefault()?.Trim(),
                    Quantity = Math.Max(1, rows.Select(r => int.TryParse(r.QuantityText, out var q) ? q : 1).DefaultIfEmpty(1).Max()),
                    Origin = "PDF p." + string.Join(", ", rows.Select(r => r.SourcePage).Distinct().OrderBy(p => p)),
                    Sources = rows.Select(r => r.SourcePage).Distinct().Count(),
                });
            }

            foreach (var t in targets.Values) t.EnsureBasePart();

            // the single location the drawings belong to receives the cabinet items (rails, ducts, enclosure)
            var location = targets.Values.FirstOrDefault(t => t.Kind == BomItemKind.Location)?.Mark ?? locationTags.FirstOrDefault();
            foreach (var t in targets.Values)
                if (t.Kind != BomItemKind.Wire && !known.Contains(t.Key)) t.LocationTag = t.Kind == BomItemKind.Location ? t.Mark : location;

            // keep what the reviewer changed
            foreach (var t in targets.Values)
                if (previous.TryGetValue(t.Key, out var old) && old.Edited) { t.Include = old.Include; t.Parts = old.Parts; t.Edited = true; }
            foreach (var old in previous.Values.Where(o => o.Edited && !targets.ContainsKey(o.Key)))
                targets[old.Key] = old;

            doc.Bom = targets.Values.OrderBy(t => t.Kind).ThenBy(t => t.Mark, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public const string ReviewOrigin = "Added in review";

        /// <summary>Parts entry for a component, created empty when the PDF lists none (so the reviewer can add parts).</summary>
        public static BomTarget Ensure(ImportDocument doc, string tag)
        {
            var t = doc.BomFor(tag);
            if (t != null) return t;
            var key = BomText.MarkKey(tag);
            int colon = key.IndexOf(':');
            t = colon > 0
                ? new BomTarget { Mark = tag, Kind = BomItemKind.Terminal, Strip = tag.Substring(0, tag.LastIndexOfAny(new[] { '.', ':', ' ' })), TerminalNumber = key.Substring(colon + 1) }
                : new BomTarget { Mark = tag, Kind = BomItemKind.Component };
            doc.Bom.Add(t);
            return t;
        }

        /// <summary>"X1 1, X1 2" → terminals; "04Q1;04Q2" and "N2 N1" → marks; bare terminal numbers ("1, 2") are skipped.</summary>
        public static IEnumerable<(string mark, string strip, string number)> SplitMarks(string cell)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in Regex.Split(cell ?? "", @"[,;]"))
            {
                var piece = raw.Trim();
                if (piece.Length == 0) continue;
                var tm = TerminalMark.Match(piece);
                if (tm.Success && Regex.IsMatch(tm.Groups[1].Value, "[A-Za-z]"))
                {
                    var mark = tm.Groups[1].Value + " " + tm.Groups[2].Value;
                    if (seen.Add(mark)) yield return (mark, tm.Groups[1].Value, tm.Groups[2].Value);
                    continue;
                }
                foreach (var token in piece.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (ItemNumber.IsMatch(token) || token.Length > 20 || !Regex.IsMatch(token, "[A-Za-z]") || !Regex.IsMatch(token, @"\d")) continue;
                    if (seen.Add(token)) yield return (token, null, null);
                }
            }
        }

        /// <summary>A quantity column holds a count, or a length for rails, ducts and wires; per-mark count is what a component carries.</summary>
        private static string PerMark(string qty, int marks)
        {
            if (string.IsNullOrWhiteSpace(qty) || !int.TryParse(qty.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var q)) return "1";
            if (q <= 0 || q > 200) return "1";
            int per = (int)Math.Round((double)q / Math.Max(1, marks));
            return Math.Max(1, per).ToString(CultureInfo.InvariantCulture);
        }

        private static (string reference, string maker) SplitFormatted(string reference, string maker, List<string> makers)
        {
            if (string.IsNullOrWhiteSpace(reference)) return (reference, maker);
            reference = reference.Trim();
            if (string.IsNullOrWhiteSpace(maker))
            {
                foreach (var m in makers)
                    if (reference.StartsWith(m + "_", StringComparison.OrdinalIgnoreCase) && reference.Length > m.Length + 1)
                        return (reference.Substring(m.Length + 1), m);
                return (reference, null);
            }
            return (reference, maker.Trim());
        }

        private static HashSet<string> LocationTags(ImportDocument doc)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in doc.Pages)
            {
                foreach (var b in p.Locations) if (!string.IsNullOrWhiteSpace(b.Name)) set.Add(b.Name.Trim().ToUpperInvariant());
                var loc = p.TitleBlock?.Get("LOCATION");
                var first = loc?.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (first != null && first.Length <= 12 && Regex.IsMatch(first, @"[A-Za-z]") && Regex.IsMatch(first, @"\d")) set.Add(first.ToUpperInvariant());
            }
            return set;
        }
    }
}
