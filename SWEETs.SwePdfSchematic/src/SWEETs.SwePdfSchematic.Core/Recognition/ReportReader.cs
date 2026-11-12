using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Pdf;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Recognises report pages (bills of material, wire / cable / terminal lists, drawing lists) that CAD
    /// exports append to the schematic, so they are not mistaken for drawings, and harvests the parts list:
    /// device mark → description, manufacturer, part number. The descriptions then narrow each symbol's kind
    /// ("3 Pole, 30A CB" → circuit breaker) and become the component documentation in the SWE project.
    /// </summary>
    public static class ReportReader
    {
        private static readonly string[] HeaderWords =
        {
            "MARK", "TAG", "REFERENCE", "DESCRIPTION", "DESCTIPTION", "QUANTITY", "QTY", "MANUFACTURER", "MFG",
            "ORIGIN", "DESTINATION", "SECTION", "LENGTH", "ARTICLE", "DRAWING", "FOLDER", "PART", "WIRE", "CABLE",
            "TERMINAL", "COMPONENT", "FUNCTION", "LOCATION", "REVISION", "DATE", "NUMBER",
        };

        private sealed class Line
        {
            public double Y;
            public List<TextItem> Words = new List<TextItem>();
            public string Text => string.Join(" ", Words.Select(w => w.Text));
        }

        /// <summary>A page whose text contains a table header row (three or more column titles on one line) is a report.</summary>
        public static bool IsReportPage(RawPage page, out string title)
        {
            title = null;
            foreach (var line in Lines(page))
            {
                int hits = line.Words.Count(w => HeaderWords.Contains(Norm(w.Text)));
                if (hits >= 3) { title = line.Text; return true; }
            }
            return false;
        }

        /// <summary>Reads every parts table (a header with a Mark/Tag column and a Description column) on a report page.</summary>
        public static void Harvest(RawPage page, Dictionary<string, PartInfo> parts)
        {
            var lines = Lines(page);
            for (int h = 0; h < lines.Count; h++)
            {
                var header = lines[h];
                // header titles may wrap: also look at the next line within 6 mm
                var headerWords = header.Words.Concat(h + 1 < lines.Count && header.Y - lines[h + 1].Y < 6 ? lines[h + 1].Words : Enumerable.Empty<TextItem>()).ToList();
                var tagCol = headerWords.FirstOrDefault(w => Norm(w.Text) == "MARK" || Norm(w.Text) == "TAG");
                var descCol = headerWords.FirstOrDefault(w => Norm(w.Text) == "DESCRIPTION" || Norm(w.Text) == "DESCTIPTION");
                if (tagCol == null || descCol == null) continue;
                var mfgCol = headerWords.FirstOrDefault(w => Norm(w.Text) == "MFG" || Norm(w.Text) == "MANUFACTURER");
                var refCol = headerWords.FirstOrDefault(w => Norm(w.Text) == "REFERENCE" || Norm(w.Text) == "PART");

                // column starts, left to right
                var cols = headerWords.Where(w => HeaderWords.Contains(Norm(w.Text)) || w == tagCol || w == descCol)
                                      .GroupBy(w => Math.Round(w.Bounds.Left / 3)).Select(g => g.OrderBy(w => w.Bounds.Left).First())
                                      .OrderBy(w => w.Bounds.Left).ToList();
                // body: lines below the header until the next header or the title block
                var body = new List<Line>();
                for (int i = h + 1; i < lines.Count; i++)
                {
                    if (lines[i].Words.Count(w => HeaderWords.Contains(Norm(w.Text))) >= 3) break;
                    if (header.Y - lines[i].Y < 3) continue; // wrapped header line
                    body.Add(lines[i]);
                }
                if (body.Count == 0) continue;

                // rows: anchored on the item number column when there is one, else one line per row
                double firstColRight = cols.First().Bounds.Left - 1;
                var anchors = body.Where(l => l.Words.Any(w => w.Bounds.Right < tagCol.Bounds.Left - 1 && Regex.IsMatch(w.Text, @"^\d{1,4}$"))).ToList();
                var rows = new List<List<TextItem>>();
                if (anchors.Count >= 2)
                {
                    for (int a = 0; a < anchors.Count; a++)
                    {
                        double top = a == 0 ? anchors[a].Y + 8 : (anchors[a - 1].Y + anchors[a].Y) / 2;
                        double bottom = a == anchors.Count - 1 ? anchors[a].Y - 8 : (anchors[a].Y + anchors[a + 1].Y) / 2;
                        rows.Add(body.Where(l => l.Y <= top && l.Y > bottom).SelectMany(l => l.Words).ToList());
                    }
                }
                else rows.AddRange(body.Select(l => l.Words));

                foreach (var row in rows)
                {
                    string Cell(TextItem col)
                    {
                        if (col == null) return null;
                        int ci = cols.IndexOf(col);
                        double lo = col.Bounds.Left - 2, hi = ci >= 0 && ci + 1 < cols.Count ? cols[ci + 1].Bounds.Left - 2 : double.MaxValue;
                        var words = row.Where(w => w.Bounds.Left >= lo && w.Bounds.Left < hi)
                                       .OrderByDescending(w => Math.Round(w.Center.Y, 0)).ThenBy(w => w.Bounds.Left).Select(w => w.Text);
                        var s = string.Join(" ", words).Trim();
                        return s.Length == 0 ? null : s;
                    }
                    var tags = Cell(tagCol);
                    if (tags == null) continue;
                    var info = new PartInfo
                    {
                        Description = Cell(descCol), Manufacturer = Cell(mfgCol), PartNumber = Cell(refCol), SourcePage = page.Number,
                    };
                    foreach (var tag in SplitTags(tags))
                    {
                        if (!parts.TryGetValue(tag, out var existing)) { parts[tag] = info.Clone(); continue; }
                        existing.Merge(info);
                    }
                }
            }
        }

        /// <summary>"04Q3, 04Q2, 04Q1" / "403;502" / "X1 1, X1 2" → device marks (terminal numbers dropped).</summary>
        public static IEnumerable<string> SplitTags(string cell)
        {
            foreach (var raw in Regex.Split(cell, @"[,;]"))
            {
                var t = raw.Trim();
                if (t.Length == 0) continue;
                var m = Regex.Match(t, @"^(\S+)\s+\d{1,4}$"); // "X1 12": terminal strip + terminal number
                if (m.Success) t = m.Groups[1].Value;
                if (t.Contains(" ") || t.Length > 16) continue;
                yield return t;
            }
        }

        /// <summary>Symbol kinds a part description allows; empty when the description says nothing usable.</summary>
        public static List<SymbolKind> KindsFromDescription(PartInfo p)
        {
            var kinds = new List<SymbolKind>();
            if (p == null) return kinds;
            var d = " " + Regex.Replace($"{p.Description} {p.PartNumber}".ToUpperInvariant(), @"[^A-Z0-9/]+", " ") + " ";
            bool Has(params string[] words) => words.Any(w => d.Contains(" " + w + " ") || (w.EndsWith("*") && d.Contains(" " + w.TrimEnd('*'))));
            void Add(params SymbolKind[] k) { foreach (var x in k) if (!kinds.Contains(x)) kinds.Add(x); }

            if (Has("FUSE*")) Add(SymbolKind.Fuse);
            if (Has("BREAKER*", "CB", "MCB", "MCCB")) Add(SymbolKind.CircuitBreaker);
            if (Has("MOTOR*")) Add(SymbolKind.Motor);
            if (Has("TERMINAL*") && !Has("POWER", "SUPPLY")) Add(SymbolKind.Terminal);
            if (Has("CONTACTOR*", "RELAY*", "RELEAY*", "STARTER*")) Add(SymbolKind.Coil, SymbolKind.NoContact, SymbolKind.NcContact);
            if (Has("OVERLOAD*")) Add(SymbolKind.Overload, SymbolKind.NcContact);
            if (Has("ESTOP", "E STOP", "EMERGENCY")) Add(SymbolKind.PushbuttonNc, SymbolKind.NcContact);
            if (Has("PB", "PUSHBUTTON*", "PUSH", "BUTTON*")) Add(SymbolKind.PushbuttonNo, SymbolKind.PushbuttonNc);
            if (Has("SELECTOR*", "SWITCH*", "DISCONNECT*") && !Has("FUSE*")) Add(SymbolKind.NoContact, SymbolKind.NcContact);
            if (Has("LAMP*", "LIGHT*", "PILOT", "INDICATOR*", "ILLUMINATED", "BEACON*")) Add(SymbolKind.Lamp);
            if (Has("PSU*", "SUPPLY", "PLC*", "CONTROLLER*", "MODULE*", "DRIVE*", "VFD", "INVERTER*")) Add(SymbolKind.BlackBox);
            if (Has("TRANSFORMER*")) Add(SymbolKind.Transformer);
            return kinds;
        }

        private static string Norm(string s) => Regex.Replace(s ?? "", @"[^A-Za-z]", "").ToUpperInvariant();

        private static List<Line> Lines(RawPage page)
        {
            var lines = new List<Line>();
            foreach (var w in page.Texts.Where(t => t.Direction == TextDirection.Horizontal).OrderByDescending(t => t.Center.Y))
            {
                var line = lines.FirstOrDefault(l => Math.Abs(l.Y - w.Center.Y) < 1.2);
                if (line == null) lines.Add(line = new Line { Y = w.Center.Y });
                line.Words.Add(w);
            }
            foreach (var l in lines) l.Words.Sort((a, b) => a.Bounds.Left.CompareTo(b.Bounds.Left));
            return lines.OrderByDescending(l => l.Y).ToList();
        }
    }
}
