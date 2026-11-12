using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Recognition;

namespace SWEETs.SwePdfSchematic.Core.Model
{
    public enum SymbolStandard { Unknown, IEC, ANSI }

    public enum SymbolKind
    {
        Unknown,
        NoContact,
        NcContact,
        PushbuttonNo,
        PushbuttonNc,
        Coil,
        CircuitBreaker,
        Fuse,
        Overload,
        Lamp,
        Motor,
        Terminal,
        Transformer,
        OffPageArrow,
        BlackBox,
    }

    public enum ReviewState { NeedsReview, Confirmed, Unmapped, Ignored }

    /// <summary>Schematic drawings are recognised; reports and layouts are kept as documentation only.</summary>
    public enum PageKind { Schematic, Report, Layout }

    /// <summary>Manufacturer data for one device mark, read from the PDF's own bill-of-material pages.</summary>
    public sealed class PartInfo
    {
        public string Description;
        public string Manufacturer;
        public string PartNumber;
        public int SourcePage;

        public PartInfo Clone() => (PartInfo)MemberwiseClone();
        public void Merge(PartInfo o)
        {
            if (string.IsNullOrEmpty(Description)) Description = o.Description;
            else if (!string.IsNullOrEmpty(o.Description) && !Description.Contains(o.Description)) Description += "; " + o.Description;
            if (string.IsNullOrEmpty(Manufacturer)) Manufacturer = o.Manufacturer;
            if (string.IsNullOrEmpty(PartNumber)) PartNumber = o.PartNumber;
        }
        public override string ToString() =>
            string.Join(" · ", new[] { Description, Manufacturer, PartNumber }.Where(x => !string.IsNullOrEmpty(x)));
    }

    /// <summary>A dashed location / function box drawn on a schematic ("L2 - Conveyors").</summary>
    public sealed class LocationBox
    {
        public Rect Bounds;
        public string Name;
        public string Description;
    }

    public enum PinSide { Top, Bottom, Left, Right }

    public sealed class SymbolPin
    {
        public Pt Location;
        public int NetIndex;
        public PinSide Side;
        /// <summary>Connection label printed at the pin ("13", "A1", "L1", "T2"), when found.</summary>
        public string Label;
    }

    public sealed class SymbolCandidate
    {
        public string SweSymbolName;
        public double Score;
        /// <summary>"learned-exact", "learned-similar", "kind-default" or "manual".</summary>
        public string Source;
        public override string ToString() => $"{SweSymbolName} ({Score:P0}, {Source})";
    }

    public sealed class DetectedSymbol
    {
        public string Id;
        public int PageNumber;
        public Rect Bounds;
        public List<Segment> Segments = new List<Segment>();
        public List<PathShape> Shapes = new List<PathShape>();
        public List<SymbolPin> Pins = new List<SymbolPin>();
        public List<string> InnerTexts = new List<string>();
        public List<string> Xrefs = new List<string>();
        public List<string> Attributes = new List<string>();

        public string Tag;
        /// <summary>True when the tag was inherited from a neighbouring pole of the same device.</summary>
        public bool TagInherited;
        public double TagDistance = double.MaxValue;
        /// <summary>The tag came from a bare letter+number text ("X1", "T3"); any full device mark replaces it.</summary>
        public bool TagWeak;
        public string PoleGroupId;
        /// <summary>Terminal of a strip: its number within the strip ("1", "12", "PE"), from the PDF or numbered in order.</summary>
        public string TerminalNumber;
        /// <summary>Mark the manufacturer parts are kept under: "X1.4" for a terminal of strip X1, else the tag.</summary>
        public string PartsKey => Kind == SymbolKind.Terminal && Tag != null && TerminalNumber != null ? Tag + "." + TerminalNumber : Tag;
        /// <summary>Manufacturer data for this device from the PDF's parts list, when it has one.</summary>
        public PartInfo Part;

        public SymbolKind Kind;
        public string Category;
        public SymbolStandard Standard;
        /// <summary>Rotation (degrees, CCW) to apply to the library symbol's native orientation.</summary>
        public int Rotation;
        public SymbolSignature Signature;

        public List<SymbolCandidate> Candidates = new List<SymbolCandidate>();
        public string SelectedSweSymbol;
        public double Confidence;
        public ReviewState Review;
        /// <summary>Set once a reviewer confirms, remaps or ignores the symbol; scoring never overrides it.</summary>
        public bool UserReviewed;
        public List<string> Notes = new List<string>();

        /// <summary>Poles merged into this symbol by the reviewer (or automatically from a learned mapping); empty otherwise.</summary>
        public List<DetectedSymbol> CombinedFrom = new List<DetectedSymbol>();
        public bool IsCombined => CombinedFrom.Count > 0;

        public Pt Anchor => Bounds.Center;
        public override string ToString() => $"{Id} {Tag ?? "(no tag)"} {Kind} → {SelectedSweSymbol ?? "?"} [{Review}]";
    }

    public sealed class WireNet
    {
        public int Index;
        public List<Segment> Segments = new List<Segment>();
        public List<Pt> FreeEnds = new List<Pt>();
        public List<Pt> Junctions = new List<Pt>();
        public string WireNumber;
        public double Length => Segments.Sum(s => s.Length);
    }

    public sealed class OffPageLink
    {
        public string Id;
        public int PageNumber;
        public Pt Location;
        public int NetIndex;
        public string XrefText;
        public int? TargetPage;
        public int? TargetColumn;
        public string Label;
        public bool HasArrowSymbol;
        public string PairedLinkId;
        /// <summary>"origin" when the signal leaves this page, "destination" when it arrives.</summary>
        public string Direction;
    }

    public sealed class TitleBlockInfo
    {
        public Dictionary<string, string> Fields = new Dictionary<string, string>();
        public List<string> RawText = new List<string>();
        public string Get(params string[] keys)
        {
            foreach (var k in keys)
                foreach (var kv in Fields)
                    if (kv.Key.Replace(" ", "").ToUpperInvariant().StartsWith(k)) return kv.Value;
            return null;
        }
    }

    public sealed class PageAnalysis
    {
        public int PageNumber;
        public PageKind Kind;
        /// <summary>Report title / header line for report pages.</summary>
        public string ReportTitle;
        /// <summary>Sheet number printed in the title block ("04"); cross-references use it, not the PDF page index.</summary>
        public string SheetNumber;
        public List<LocationBox> Locations = new List<LocationBox>();
        public double WidthMm;
        public double HeightMm;
        public Rect Frame;
        public List<Rect> ExcludedRegions = new List<Rect>();
        public int ColumnCount;
        /// <summary>Reference zones are numbered rows down the left edge instead of columns along the top.</summary>
        public bool ZonesAreRows;
        public bool IsRasterOnly;
        /// <summary>
        /// Rebuild this page in SOLIDWORKS Electrical. Defaults to true for drawings with symbols and false for reports,
        /// lists and layouts (SOLIDWORKS Electrical generates its own reports); the reviewer can change it.
        /// </summary>
        public bool Include;
        /// <summary>The reviewer asked for this page to be analysed as a schematic even though it looked like a report.</summary>
        public bool ForcedSchematic;
        public SymbolStandard Standard;
        public double StandardConfidence;
        public TitleBlockInfo TitleBlock = new TitleBlockInfo();

        public List<WireNet> Nets = new List<WireNet>();
        public List<DetectedSymbol> Symbols = new List<DetectedSymbol>();
        public List<OffPageLink> OffPageLinks = new List<OffPageLink>();
        public List<Pt> JunctionDots = new List<Pt>();
        public List<string> Warnings = new List<string>();

        /// <summary>Full vector content kept for the local preview renderer.</summary>
        public List<Segment> AllSegments = new List<Segment>();
        public List<TextItem> AllTexts = new List<TextItem>();

        /// <summary>Reference zone (column, or row when <see cref="ZonesAreRows"/>) containing a point.</summary>
        public int? ZoneAt(Pt p)
        {
            if (ColumnCount <= 0) return null;
            int c = ZonesAreRows
                ? (Frame.Height <= 0 ? 0 : (int)((Frame.Top - p.Y) / (Frame.Height / ColumnCount)) + 1)
                : (Frame.Width <= 0 ? 0 : (int)((p.X - Frame.Left) / (Frame.Width / ColumnCount)) + 1);
            if (c == 0) return null;
            return c < 1 ? 1 : (c > ColumnCount ? ColumnCount : c);
        }

        public int? ColumnAt(double xMm) => ZonesAreRows ? null : ZoneAt(new Pt(xMm, Frame.Center.Y));
    }

    /// <summary>All symbols sharing one device tag. In SWE they become one component, which is what produces cross-references.</summary>
    public sealed class ComponentGroup
    {
        public string Tag;
        public string Category;
        public List<DetectedSymbol> Symbols = new List<DetectedSymbol>();
        public IEnumerable<int> Pages => Symbols.Select(s => s.PageNumber).Distinct().OrderBy(p => p);
        public DetectedSymbol Parent => Symbols.FirstOrDefault(s => s.Kind == SymbolKind.Coil) ?? Symbols.FirstOrDefault();
    }

    public sealed class ImportDocument
    {
        public List<ComponentGroup> Components = new List<ComponentGroup>();
        public List<string> Warnings = new List<string>();
        public string SourcePath;
        public string SourceFileName;
        public string SourceSha256;
        public List<PageAnalysis> Pages = new List<PageAnalysis>();
        /// <summary>Device mark → manufacturer data harvested from the PDF's bill-of-material pages.</summary>
        public Dictionary<string, PartInfo> Parts = new Dictionary<string, PartInfo>(System.StringComparer.OrdinalIgnoreCase);
        public IEnumerable<PageAnalysis> SchematicPages => Pages.Where(p => p.Kind == PageKind.Schematic);
        /// <summary>Title block of the first drawing that has one (cover and report pages often have none).</summary>
        public TitleBlockInfo DocumentInfo =>
            SchematicPages.Select(p => p.TitleBlock).FirstOrDefault(t => t != null && t.Fields.Count > 0)
            ?? Pages.Select(p => p.TitleBlock).FirstOrDefault(t => t != null && t.Fields.Count > 0)
            ?? new TitleBlockInfo();

        public IEnumerable<DetectedSymbol> AllSymbols => Pages.SelectMany(p => p.Symbols);
        /// <summary>Schematic pages the reviewer chose to import; only these are reviewed and built.</summary>
        public IEnumerable<PageAnalysis> IncludedPages => Pages.Where(p => p.Include && p.Kind == PageKind.Schematic && !p.IsRasterOnly);
        public IEnumerable<DetectedSymbol> IncludedSymbols => IncludedPages.SelectMany(p => p.Symbols);
        public PartInfo PartFor(string tag) => tag != null && Parts.TryGetValue(tag, out var p) ? p : null;

        /// <summary>Every row of every parts table in the PDF, as printed.</summary>
        public List<BomLine> BomLines = new List<BomLine>();
        /// <summary>Parts per mark (merged across reports), including marks without a schematic symbol.</summary>
        public List<BomTarget> Bom = new List<BomTarget>();
        /// <summary>Parts entry for a component tag, or null.</summary>
        public BomTarget BomFor(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            var k = BomText.MarkKey(tag);
            return Bom.FirstOrDefault(b => b.Key == k);
        }
        /// <summary>True when a parts entry is served by a component drawn on an included page.</summary>
        public bool HasSymbol(BomTarget b) =>
            Components.Any(c => BomText.MarkKey(c.Tag) == b.Key)
            || IncludedSymbols.Any(s => s.Kind == SymbolKind.Terminal && s.TerminalNumber != null && s.Review != ReviewState.Ignored && BomText.MarkKey(s.PartsKey) == b.Key);
        /// <summary>Parts entries with no drawn component: cables, rails, ducts, enclosure, terminals not on the drawings.</summary>
        public IEnumerable<BomTarget> BomWithoutSymbols => Bom.Where(b => b.Kind != BomItemKind.Wire && !HasSymbol(b));
        public int CountByState(ReviewState s) => AllSymbols.Count(x => x.Review == s);
        public bool ReadyToBuild => IncludedPages.Any() && !IncludedSymbols.Any(s => s.Review == ReviewState.NeedsReview || s.Review == ReviewState.Unmapped);
    }
}
