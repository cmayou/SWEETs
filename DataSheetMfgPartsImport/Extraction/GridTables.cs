using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DataSheetMfgPartsImport.Extraction
{
    /// <summary>A price-list or selection table: one part per row, columns found from the header's position.</summary>
    internal sealed class GridTable
    {
        public string Title = "";
        public readonly List<string> Labels = new List<string>();
        public readonly List<string> PartNumbers = new List<string>();
        /// <summary>Per part: column index to the text in that column, for columns that have a value on most rows.</summary>
        public readonly List<Dictionary<int, string>> Rows = new List<Dictionary<int, string>>();
        /// <summary>Columns with merged cells (a value spanning several rows): column index to the values from top to bottom.</summary>
        public readonly Dictionary<int, List<string>> Groups = new Dictionary<int, List<string>>();
    }

    /// <summary>A specification table with one value column per variant, identified by a "Number of Poles" row.</summary>
    internal sealed class SpecColumns
    {
        public string Series = "";
        public readonly Dictionary<int, Dictionary<string, string>> ByPoles = new Dictionary<int, Dictionary<string, string>>();
    }

    internal static class GridTables
    {
        const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        static readonly Regex PnCell = new Regex(@"^[A-Za-z0-9][A-Za-z0-9\-\./_]{3,28}$");

        sealed class Col
        {
            public double Left, Right;
            public string Label;
        }

        static bool IsPn(string t)
        {
            return PnCell.IsMatch(t) && RulesExtractor.IsPartNumber(t.TrimEnd('.', '-', '/', '_'), false);
        }

        static string CommonPrefix(List<string> xs)
        {
            string p = xs[0];
            foreach (var x in xs.Skip(1))
                while (!x.StartsWith(p, StringComparison.Ordinal)) p = p.Substring(0, p.Length - 1);
            return p;
        }

        /// <summary>The series name: the title up to the first bracket, without a pole count.</summary>
        public static string CleanSeries(string title)
        {
            if (string.IsNullOrEmpty(title) || title.IndexOf('(') < 0) return "";
            string t = title.Substring(0, title.IndexOf('('));
            string u = Regex.Replace(t, @"\s+\d-Pole\b.*$", "", Ci);
            return (u.Trim().Length > 0 ? u : t).Trim(' ', '–', '-');
        }

        public static List<GridTable> Read(List<TextLine> L)
        {
            var result = new List<GridTable>();
            var prefixes = new List<string>();

            for (int h = 0; h < L.Count; h++)
            {
                var hl = L[h];
                if (hl.Cells.Count < 3 || !Regex.IsMatch(hl.Text, @"Part\s+Number", Ci)) continue;
                if (hl.Cells.Any(c => c.Text.StartsWith("$", StringComparison.Ordinal))) continue;
                int page = hl.Page;

                // The header can wrap onto the lines just above and below ("Interrupt" / "Capacity (kA)").
                var blk = new List<TextLine> { hl };
                foreach (int k in new[] { h - 1, h + 1 })
                {
                    if (k < 0 || k >= L.Count) continue;
                    var n = L[k];
                    if (n.Page == page && Math.Abs(n.Y - hl.Y) <= 16 && Math.Abs(n.FontSize - hl.FontSize) <= 1.0
                        && n.Cells.All(c => c.Text.Length <= 22) && !n.Cells.Any(c => IsPn(c.Text)))
                        blk.Add(n);
                }

                var cols = new List<Col>();
                foreach (var ln in blk.OrderByDescending(x => x.Y))
                {
                    foreach (var c in ln.Cells)
                    {
                        double cx = (c.Left + c.Right) / 2;
                        var m = cols.FirstOrDefault(col => col.Left - 4 <= cx && cx <= col.Right + 4);
                        if (m == null) cols.Add(new Col { Left = c.Left, Right = c.Right, Label = c.Text });
                        else
                        {
                            m.Label += " " + c.Text;
                            m.Left = Math.Min(m.Left, c.Left);
                            m.Right = Math.Max(m.Right, c.Right);
                        }
                    }
                }
                cols = cols.OrderBy(c => c.Left).ToList();
                int partCol = cols.FindIndex(c => Regex.IsMatch(c.Label, @"Part\s+Number", Ci));
                if (partCol < 0) continue;

                var bounds = new List<double>();
                for (int i = 0; i + 1 < cols.Count; i++) bounds.Add((cols[i].Right + cols[i + 1].Left) / 2);
                double rightLimit = cols[cols.Count - 1].Right + 25;   // picture captions beyond the table are ignored
                int colCount = cols.Count;
                Func<double, int> colIdx = x =>
                {
                    if (x > rightLimit) return -1;
                    for (int i = 0; i < bounds.Count; i++) if (x < bounds[i]) return i;
                    return colCount - 1;
                };

                double hy = blk.Min(x => x.Y);

                // Title: the nearest large-font line above, joined with a wrapped second line.
                string title = null;
                double left = cols[0].Left;
                for (int k = h - 1; k >= Math.Max(0, h - 7); k--)
                {
                    if (L[k].Page != page || L[k].FontSize < hl.FontSize + 2) continue;
                    var tparts = new List<string>();
                    int q = k;
                    while (q >= 0 && L[q].Page == page && L[q].FontSize >= hl.FontSize + 2 && tparts.Count < 2
                           && Math.Abs(L[q].FontSize - L[k].FontSize) <= 1.0
                           && (q == k || L[q + 1].Y - L[q].Y < 22))
                    {
                        string txt = string.Join(" ", L[q].Cells.Where(c => c.Left >= left - 6 && c.Left <= rightLimit).Select(c => c.Text));
                        if (txt.Length > 0) tparts.Insert(0, txt);
                        q--;
                    }
                    title = string.Join(" ", tparts);
                    break;
                }

                // The table body: until the next title, footer or header.
                var region = new List<TextLine>();
                for (int k = h + 1; k < L.Count; k++)
                {
                    var l = L[k];
                    if (l.Page != page || l.FontSize >= hl.FontSize + 2 || l.Text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) break;
                    if (l.Cells.Count >= 3 && Regex.IsMatch(l.Text, @"Part\s+Number", Ci)) break;
                    if (blk.Contains(l) || l.Y >= hy + 0.1) continue;
                    region.Add(l);
                }

                var anchors = new List<TextLine>();
                var pns = new List<string>();
                foreach (var l in region)
                {
                    var cell = l.Cells.FirstOrDefault(c => colIdx((c.Left + c.Right) / 2) == partCol && IsPn(c.Text));
                    if (cell != null) { anchors.Add(l); pns.Add(cell.Text); }
                }
                if (pns.Count == 0) continue;
                if (!cols.Any(c => Regex.IsMatch(c.Label, @"price|description", Ci))) continue;

                // Two or more rows must share a prefix; a lone row must share one with a table already accepted.
                bool ok;
                if (pns.Count >= 2)
                {
                    string pre = CommonPrefix(pns);
                    ok = pre.Length >= 4;
                    if (ok) prefixes.Add(pre.Substring(0, 4));
                }
                else ok = prefixes.Any(p => pns[0].StartsWith(p, StringComparison.Ordinal));
                if (!ok) continue;

                // Each row owns the lines halfway to its neighbours, so a description wrapped around the part number stays together.
                int nA = anchors.Count;
                var ys = anchors.Select(a => a.Y).ToList();
                Func<int, double> topOf = i => i == 0 ? double.PositiveInfinity : (ys[i - 1] + ys[i]) / 2;
                Func<int, double> botOf = i => i == nA - 1 ? double.NegativeInfinity : (ys[i] + ys[i + 1]) / 2;

                var perRow = new Dictionary<int, List<KeyValuePair<double, string>>[]>();
                var allCol = new Dictionary<int, List<KeyValuePair<double, string>>>();
                foreach (var l in region)
                {
                    foreach (var c in l.Cells)
                    {
                        int j = colIdx((c.Left + c.Right) / 2);
                        if (j == partCol || j < 0) continue;
                        if (!allCol.ContainsKey(j)) allCol[j] = new List<KeyValuePair<double, string>>();
                        allCol[j].Add(new KeyValuePair<double, string>(l.Y, c.Text));
                        for (int i = 0; i < nA; i++)
                        {
                            if (botOf(i) <= l.Y && l.Y < topOf(i))
                            {
                                if (!perRow.ContainsKey(j))
                                {
                                    var arr = new List<KeyValuePair<double, string>>[nA];
                                    for (int q = 0; q < nA; q++) arr[q] = new List<KeyValuePair<double, string>>();
                                    perRow[j] = arr;
                                }
                                perRow[j][i].Add(new KeyValuePair<double, string>(l.Y, c.Text));
                                break;
                            }
                        }
                    }
                }

                // A column filled on most rows is per part; a sparse one holds merged cells shared by the whole table.
                var isRow = new Dictionary<int, bool>();
                for (int j = 0; j < cols.Count; j++)
                {
                    if (j == partCol) continue;
                    int filled = 0;
                    if (perRow.ContainsKey(j))
                        for (int i = 0; i < nA; i++) if (perRow[j][i].Count > 0) filled++;
                    isRow[j] = filled >= 0.8 * nA || Regex.IsMatch(cols[j].Label, "description", Ci);
                }

                var t = new GridTable { Title = title ?? "" };
                foreach (var c in cols) t.Labels.Add(c.Label);
                for (int i = 0; i < nA; i++)
                {
                    t.PartNumbers.Add(pns[i]);
                    var row = new Dictionary<int, string>();
                    foreach (var kv in isRow.Where(x => x.Value))
                        row[kv.Key] = perRow.ContainsKey(kv.Key)
                            ? string.Join(" ", perRow[kv.Key][i].OrderByDescending(p => p.Key).Select(p => p.Value))
                            : "";
                    t.Rows.Add(row);
                }
                foreach (var kv in isRow.Where(x => !x.Value))
                    if (allCol.ContainsKey(kv.Key))
                        t.Groups[kv.Key] = allCol[kv.Key].OrderByDescending(p => p.Key).Select(p => p.Value).ToList();
                result.Add(t);
            }
            return result;
        }

        /// <summary>Tables with one value column per pole count, such as "Number of Poles | 2 | 3".</summary>
        public static List<SpecColumns> ReadSpecs(List<TextLine> L)
        {
            var result = new List<SpecColumns>();
            for (int i = 0; i < L.Count; i++)
            {
                var l = L[i];
                var c = l.Cells;
                if (c.Count < 3 || !Regex.IsMatch(c[0].Text, @"^Number of Poles$", Ci)) continue;
                if (!c.Skip(1).All(x => Regex.IsMatch(x.Text, @"^\d$"))) continue;
                var poles = c.Skip(1).Select(x => int.Parse(x.Text)).ToList();

                string title = null;
                for (int k = i - 1; k >= Math.Max(0, i - 5); k--)
                {
                    if (L[k].Page == l.Page && L[k].FontSize >= l.FontSize + 2)
                    {
                        title = string.Join(" ", L[k].Cells.Select(x => x.Text));
                        break;
                    }
                }

                var sc = new SpecColumns { Series = CleanSeries(title) };
                foreach (int p in poles) sc.ByPoles[p] = new Dictionary<string, string>();
                for (int k = i + 1; k < L.Count; k++)
                {
                    var r = L[k];
                    if (r.Page != l.Page || r.FontSize >= l.FontSize + 2) break;
                    if (r.Cells.Count != poles.Count + 1) continue;
                    string label = r.Cells[0].Text;
                    for (int q = 0; q < poles.Count; q++)
                    {
                        string v = r.Cells[q + 1].Text;
                        var d = sc.ByPoles[poles[q]];
                        if (Regex.IsMatch(label, "^Weight", Ci))
                        {
                            var m = Regex.Match(v, @"\[(\d+(?:\.\d+)?)\]");   // "1.64 [0.74]": pounds then [kilograms]
                            if (m.Success) d["weight"] = m.Groups[1].Value;
                        }
                        else if (Regex.IsMatch(label, "Insulation Voltage", Ci)) d["Ui"] = v;
                        else if (Regex.IsMatch(label, "Impulse Withstand", Ci)) d["Uimp"] = v;
                        else if (Regex.IsMatch(label, "Utilization Category", Ci)) d["cat"] = v;
                        else if (Regex.IsMatch(label, "^Service Breaking", Ci)) d["Ics"] = v;
                    }
                }
                result.Add(sc);
            }
            return result;
        }
    }
}
