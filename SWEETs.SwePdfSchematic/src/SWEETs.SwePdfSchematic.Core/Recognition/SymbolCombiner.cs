using System;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    /// <summary>
    /// Merges separately detected poles into one multi-circuit symbol (4-pole fuse disconnector, 3-pole breaker,
    /// contactor main poles) so it maps to a single library symbol, and splits it back. Pure geometry/model work:
    /// classification, signature and scoring are done by <see cref="SchematicRecognizer"/>.
    /// </summary>
    public static class SymbolCombiner
    {
        /// <summary>Builds the combined symbol without touching the page. Members must be on one page.</summary>
        public static DetectedSymbol Merge(IList<DetectedSymbol> members)
        {
            if (members == null || members.Count < 2) throw new ArgumentException("Select at least two symbols to combine.");
            if (members.Select(m => m.PageNumber).Distinct().Count() > 1) throw new ArgumentException("Only symbols on the same page can be combined.");
            if (members.Any(m => m.IsCombined)) throw new ArgumentException("Split combined symbols before combining them again.");

            var ordered = OrderPoles(members);
            var first = ordered[0];
            var tagged = ordered.Where(m => m.Tag != null).OrderBy(m => m.TagInherited).ThenBy(m => m.TagWeak).FirstOrDefault();
            var c = new DetectedSymbol
            {
                Id = $"{first.Id}+{ordered.Count - 1}",
                PageNumber = first.PageNumber,
                Bounds = ordered.Select(m => m.Bounds).Aggregate((a, b) => a.Union(b)),
                Tag = tagged?.Tag,
                TagWeak = tagged?.TagWeak ?? false,
                TagDistance = tagged?.TagDistance ?? double.MaxValue,
                Part = ordered.Select(m => m.Part).FirstOrDefault(p => p != null),
                PoleGroupId = first.PoleGroupId,
                Kind = MostCommon(ordered.Select(m => m.Kind).Where(k => k != SymbolKind.Unknown)) ?? first.Kind,
                Standard = first.Standard,
                CombinedFrom = ordered.ToList(),
            };
            foreach (var m in ordered)
            {
                c.Segments.AddRange(m.Segments);
                c.Shapes.AddRange(m.Shapes.Where(s => !c.Shapes.Contains(s)));
                c.Pins.AddRange(m.Pins);
                c.InnerTexts.AddRange(m.InnerTexts);
                c.Xrefs.AddRange(m.Xrefs.Where(x => !c.Xrefs.Contains(x)));
                c.Attributes.AddRange(m.Attributes.Where(x => !c.Attributes.Contains(x)));
            }
            return c;
        }

        /// <summary>Replaces the members with the combined symbol, at the position of the first member.</summary>
        public static void Commit(PageAnalysis page, DetectedSymbol combined)
        {
            int at = combined.CombinedFrom.Select(m => page.Symbols.IndexOf(m)).Where(i => i >= 0).DefaultIfEmpty(page.Symbols.Count).Min();
            foreach (var m in combined.CombinedFrom) page.Symbols.Remove(m);
            page.Symbols.Insert(Math.Min(at, page.Symbols.Count), combined);
        }

        /// <summary>Puts the original poles back in place of a combined symbol and returns them.</summary>
        public static List<DetectedSymbol> Split(PageAnalysis page, DetectedSymbol combined)
        {
            if (!combined.IsCombined) return new List<DetectedSymbol> { combined };
            int at = Math.Max(0, page.Symbols.IndexOf(combined));
            page.Symbols.Remove(combined);
            page.Symbols.InsertRange(Math.Min(at, page.Symbols.Count), combined.CombinedFrom);
            return combined.CombinedFrom;
        }

        /// <summary>
        /// Pole groups worth offering for combining: two or more symbols on one page sharing a device tag and a pole
        /// group (mechanical link, stacked or side-by-side identical poles).
        /// </summary>
        public static IEnumerable<List<DetectedSymbol>> Suggestions(PageAnalysis page) =>
            page.Symbols.Where(s => !s.IsCombined && s.Tag != null && s.PoleGroupId != null)
                .GroupBy(s => (s.PoleGroupId, Tag: s.Tag.ToUpperInvariant()))
                .Where(g => g.Count() >= 2)
                .Select(g => g.ToList());

        /// <summary>Poles in drawing order: stacked poles top to bottom, side-by-side poles left to right.</summary>
        public static List<DetectedSymbol> OrderPoles(IEnumerable<DetectedSymbol> members)
        {
            var list = members.ToList();
            double spreadX = list.Max(m => m.Anchor.X) - list.Min(m => m.Anchor.X);
            double spreadY = list.Max(m => m.Anchor.Y) - list.Min(m => m.Anchor.Y);
            return spreadX >= spreadY
                ? list.OrderBy(m => m.Anchor.X).ThenByDescending(m => m.Anchor.Y).ToList()
                : list.OrderByDescending(m => m.Anchor.Y).ThenBy(m => m.Anchor.X).ToList();
        }

        private static SymbolKind? MostCommon(IEnumerable<SymbolKind> kinds) =>
            kinds.GroupBy(k => k).OrderByDescending(g => g.Count()).Select(g => (SymbolKind?)g.Key).FirstOrDefault();
    }
}
