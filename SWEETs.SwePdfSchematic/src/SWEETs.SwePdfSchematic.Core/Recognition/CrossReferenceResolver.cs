using System;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Document-level linking:
    ///  - groups symbols by device tag into components (parent coil + child contacts across pages);
    ///  - pairs origin/destination links between pages using target page, column and wire number agreement.
    /// </summary>
    public static class CrossReferenceResolver
    {
        public static void Resolve(ImportDocument doc)
        {
            BuildComponents(doc);
            PairLinks(doc);
        }

        /// <summary>
        /// Terminals of a strip share the strip tag ("X1"); each also needs its own number. The number printed beside the
        /// terminal is used ("1" … "12"); terminals without one get the next free number in drawing order.
        /// </summary>
        public static void NumberTerminals(ImportDocument doc)
        {
            var terminals = doc.AllSymbols.Where(s => s.Kind == SymbolKind.Terminal).ToList();
            foreach (var t in terminals) t.TerminalNumber = null;
            foreach (var strip in terminals.Where(s => !string.IsNullOrEmpty(s.Tag)).GroupBy(s => s.Tag, StringComparer.OrdinalIgnoreCase))
            {
                var ordered = strip.OrderBy(s => s.PageNumber).ThenByDescending(s => Math.Round(s.Anchor.Y, 1)).ThenBy(s => s.Anchor.X).ToList();
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in ordered)
                {
                    var label = s.Pins.Select(p => p.Label).Concat(s.InnerTexts)
                                 .Select(x => x?.Trim())
                                 .FirstOrDefault(x => !string.IsNullOrEmpty(x) && System.Text.RegularExpressions.Regex.IsMatch(x, @"^[A-Za-z0-9+\-]{1,4}$")
                                                      && !x.Equals(strip.Key, StringComparison.OrdinalIgnoreCase) && !used.Contains(x));
                    if (label == null) continue;
                    s.TerminalNumber = label;
                    used.Add(label);
                }
                int next = 1;
                foreach (var s in ordered.Where(s => s.TerminalNumber == null))
                {
                    while (used.Contains(next.ToString())) next++;
                    s.TerminalNumber = next.ToString();
                    used.Add(s.TerminalNumber);
                }
            }
        }

        public static void BuildComponents(ImportDocument doc)
        {
            doc.Components = doc.IncludedSymbols
                .Where(s => !string.IsNullOrEmpty(s.Tag) && s.Review != ReviewState.Ignored)
                .GroupBy(s => s.Tag, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ComponentGroup { Tag = g.Key, Symbols = g.ToList(), Category = g.First().Category })
                .OrderBy(c => c.Tag, StringComparer.OrdinalIgnoreCase)
                .ToList();
            NumberTerminals(doc);

            doc.Warnings.RemoveAll(w => w.StartsWith("Component "));
            foreach (var c in doc.Components)
            {
                int coils = c.Symbols.Count(s => s.Kind == SymbolKind.Coil);
                int contacts = c.Symbols.Count(s => s.Kind == SymbolKind.NoContact || s.Kind == SymbolKind.NcContact);
                if (coils > 1 && c.Symbols.Select(s => s.PoleGroupId).Distinct().Count() > 1)
                    doc.Warnings.Add($"Component {c.Tag}: {coils} coils found — check for a duplicated tag.");
                if (coils == 0 && contacts > 0 && (c.Tag.StartsWith("-K") || c.Tag.StartsWith("CR")))
                    doc.Warnings.Add($"Component {c.Tag}: contacts found but no coil in this PDF; cross-references will point to a component without a parent symbol.");
            }
        }

        private static void PairLinks(ImportDocument doc)
        {
            var links = doc.Pages.SelectMany(p => p.OffPageLinks).Where(l => l.TargetPage.HasValue).ToList();
            foreach (var l in links) l.PairedLinkId = null;

            var scored = new List<(OffPageLink a, OffPageLink b, int score)>();
            foreach (var a in links)
                foreach (var b in links.Where(b => b.PageNumber == a.TargetPage && b != a))
                {
                    int score = 0;
                    if (b.TargetPage == a.PageNumber) score += 2;
                    var pa = doc.Pages.First(p => p.PageNumber == a.PageNumber);
                    var pb = doc.Pages.First(p => p.PageNumber == b.PageNumber);
                    if (a.TargetColumn.HasValue && pb.ZoneAt(b.Location) == a.TargetColumn) score += 1;
                    if (b.TargetColumn.HasValue && pa.ZoneAt(a.Location) == b.TargetColumn) score += 1;
                    var wa = pa.Nets.FirstOrDefault(n => n.Index == a.NetIndex)?.WireNumber;
                    var wb = pb.Nets.FirstOrDefault(n => n.Index == b.NetIndex)?.WireNumber;
                    if (!string.IsNullOrEmpty(wa) && wa == wb) score += 3;
                    else if (!string.IsNullOrEmpty(wa) && !string.IsNullOrEmpty(wb)) score -= 3;
                    if (score >= 2) scored.Add((a, b, score));
                }

            // greedy best-first, each link used once
            foreach (var (a, b, _) in scored.OrderByDescending(x => x.score))
            {
                if (a.PairedLinkId != null || b.PairedLinkId != null) continue;
                a.PairedLinkId = b.Id;
                b.PairedLinkId = a.Id;
            }

            foreach (var p in doc.Pages)
                foreach (var l in p.OffPageLinks.Where(l => l.TargetPage.HasValue && l.PairedLinkId == null))
                    p.Warnings.Add($"Off-page link {l.XrefText} at {l.Location} has no matching end on page {l.TargetPage}.");
        }
    }
}
