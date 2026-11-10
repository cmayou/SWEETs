using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace SWEETs.SwePdfSchematic.Core.Model
{
    /// <summary>What a bill-of-material mark stands for in SOLIDWORKS Electrical.</summary>
    public enum BomItemKind
    {
        /// <summary>A device (usually drawn on the schematic; rails, ducts and similar cabinet parts are not).</summary>
        Component,
        /// <summary>One terminal of a terminal strip ("X1 4").</summary>
        Terminal,
        /// <summary>A cable ("W1").</summary>
        Cable,
        /// <summary>Parts listed against a location (enclosure, mounting plate, cable glands).</summary>
        Location,
        /// <summary>Wire styles and conductors: SOLIDWORKS Electrical manages these through wire styles, not parts.</summary>
        Wire,
    }

    /// <summary>Whether a part is already in the SOLIDWORKS Electrical manufacturer part catalog.</summary>
    public enum CatalogState { Unknown, InCatalog, WillCreate, Failed }

    /// <summary>One row of a parts table as printed in the PDF (before rows of different reports are merged).</summary>
    public sealed class BomLine
    {
        public string MarksText;
        public string Reference;
        public bool ReferenceWrapped;
        public string Manufacturer;
        public string ArticleNumber;
        public string Description;
        public string QuantityText;
        public int SourcePage;
        public override string ToString() => $"p{SourcePage} [{MarksText}] {Manufacturer} {Reference} ×{QuantityText}";
    }

    /// <summary>A manufacturer part to put on a component (from the PDF, or added by the reviewer).</summary>
    public sealed class PartAssignment
    {
        public string Manufacturer;
        public string Reference;
        public string Description;
        public string ArticleNumber;
        /// <summary>How many of this part the component carries (e.g. 4 fuse links in one fuse holder).</summary>
        public int Quantity = 1;
        public bool Include = true;
        /// <summary>
        /// The base part: it carries the circuits and terminals of the component's symbols (other parts, such as the four
        /// fuse links of a fuse holder, are accessories). Circuits come from the mapped library symbols, terminal texts from
        /// the pin designations printed in the PDF.
        /// </summary>
        public bool CarriesCircuits;
        /// <summary>"PDF p.6, 10" or "Added in review".</summary>
        public string Origin;
        /// <summary>Number of report pages listing this part for the mark (a row misread on one page is listed once).</summary>
        public int Sources = 1;
        public CatalogState Catalog;
        public string CatalogNote;

        /// <summary>Upper-case letters and digits only: "SWST_FD4-23527 98" and "SWST_FD4-2352798" are the same part.</summary>
        public string Key => BomText.Key(Reference);
        public PartAssignment Clone() => (PartAssignment)MemberwiseClone();
        public override string ToString() => $"{Manufacturer} {Reference}" + (Quantity > 1 ? $" ×{Quantity}" : "");
    }

    /// <summary>Everything the parts lists say about one mark: a component, terminal, cable or location.</summary>
    public sealed class BomTarget
    {
        public string Mark;
        public BomItemKind Kind;
        /// <summary>Terminal strip of a terminal ("X1"), else null.</summary>
        public string Strip;
        public string TerminalNumber;
        /// <summary>Location these items are placed in when they are created without a symbol ("L1").</summary>
        public string LocationTag;
        public bool Include = true;
        /// <summary>The reviewer changed this entry; analysing again keeps it as edited.</summary>
        public bool Edited;
        public List<PartAssignment> Parts = new List<PartAssignment>();
        public string Key => BomText.MarkKey(Mark);
        public string Description => Parts.Select(p => p.Description).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d));

        /// <summary>Makes sure exactly one included part carries the circuits (the first single part unless the reviewer chose).</summary>
        public void EnsureBasePart()
        {
            var included = Parts.Where(p => p.Include).ToList();
            foreach (var p in Parts.Where(p => !p.Include)) p.CarriesCircuits = false;
            if (included.Count(p => p.CarriesCircuits) == 1) return;
            var keep = included.FirstOrDefault(p => p.CarriesCircuits)
                       ?? included.Where(p => p.Quantity == 1).OrderByDescending(p => p.Sources).FirstOrDefault()
                       ?? included.OrderBy(p => p.Quantity).ThenByDescending(p => p.Sources).FirstOrDefault();
            foreach (var p in Parts) p.CarriesCircuits = p == keep;
        }
        public override string ToString() => $"{Kind} {Mark}: {string.Join(", ", Parts)}";
    }

    public static class BomText
    {
        public static string Key(string reference) => Regex.Replace(reference ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant();

        /// <summary>Mark comparison key: case-insensitive, and "X1 4", "X1.4", "X1:4", "X1-4" are the same terminal.</summary>
        public static string MarkKey(string mark)
        {
            var t = (mark ?? "").Trim().ToUpperInvariant();
            var m = Regex.Match(t, @"^(.*[A-Z].*?)[\s.:]+(\d{1,4})$");
            return m.Success ? m.Groups[1].Value.TrimEnd('-') + ":" + m.Groups[2].Value : t;
        }
    }
}
