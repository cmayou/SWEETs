using System.IO;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Mapping;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Recognition;
using Xunit;

/// <summary>
/// End-to-end against a 25-page PDF exported from SOLIDWORKS Electrical 2026 (ANSI template, project PRJ-10011):
/// two schematics (power, control) plus report, list and layout pages. Frame drawn as filled bars, title block as lines,
/// SWE default marks (04Q1), pin designations (L1/T1) beside contactor poles and dashed breaker links.
/// </summary>
public class SweExportTests
{
    private static readonly string Pdf = Path.Combine(AppContext.BaseDirectory, "Fixtures", "swe-ansi-hyperlink-test.pdf");
    private static readonly System.Lazy<ImportDocument> Doc = new System.Lazy<ImportDocument>(() =>
        new SchematicRecognizer(new RecognitionOptions(), RuleLibrary.Load(),
            new MappingStore(Path.Combine(Path.GetTempPath(), "sweets-test-" + Guid.NewGuid() + ".json"))).Recognize(Pdf));

    private static PageAnalysis Page(int n) => Doc.Value.Pages.Single(p => p.PageNumber == n);

    [Fact]
    public void Only_the_two_schematic_pages_are_rebuilt()
    {
        Assert.Equal(new[] { 4, 5 }, Doc.Value.SchematicPages.Select(p => p.PageNumber).ToArray());
        Assert.Equal(PageKind.Report, Page(13).Kind);   // "List of components tags": a table, not a drawing
        Assert.Equal(PageKind.Report, Page(25).Kind);   // cabinet layout
    }

    [Fact]
    public void Title_block_and_border_are_not_read_as_wiring()
    {
        var p4 = Page(4);
        Assert.Equal(12, p4.ColumnCount);
        Assert.True(p4.ZonesAreRows);
        Assert.Equal(SymbolStandard.ANSI, p4.Standard);
        Assert.DoesNotContain(p4.Nets, n => n.WireNumber == "02451");                // ZIP code in the title block
        Assert.All(p4.OffPageLinks, l => Assert.True(l.HasArrowSymbol || l.XrefText != null));
    }

    [Fact]
    public void Power_page_devices_have_marks_kinds_and_parts()
    {
        var p4 = Page(4);
        Assert.Equal(38, p4.Symbols.Count);
        Assert.All(p4.Symbols, s => Assert.NotNull(s.Tag));
        foreach (var q in new[] { "04Q1", "04Q2", "04Q3" })
            Assert.Equal(3, p4.Symbols.Count(s => s.Tag == q && s.Kind == SymbolKind.CircuitBreaker));
        foreach (var k in new[] { "04K1", "05K1", "04K2" })
            Assert.Equal(3, p4.Symbols.Count(s => s.Tag == k && s.Kind == SymbolKind.NoContact));
        Assert.Equal(4, p4.Symbols.Count(s => s.Tag == "04F1" && s.Kind == SymbolKind.Fuse));
        Assert.Equal(12, p4.Symbols.Count(s => s.Tag == "X1" && s.Kind == SymbolKind.Terminal));
        Assert.Equal(3, p4.Symbols.Count(s => s.Kind == SymbolKind.Motor));
        Assert.Single(p4.Symbols, s => s.Tag == "04PS1" && s.Kind == SymbolKind.BlackBox);
        Assert.DoesNotContain(p4.Symbols, s => s.Tag == "L1" || s.Tag == "T2" || s.Tag == "T3");   // pin designations
        Assert.All(p4.Symbols, s => Assert.NotNull(s.Part));
    }

    [Fact]
    public void Breaker_poles_are_grouped_by_their_mechanical_link()
    {
        var poles = Page(4).Symbols.Where(s => s.Tag == "04Q1").ToList();
        Assert.Single(poles.Select(s => s.PoleGroupId).Distinct());
        Assert.All(poles, s => Assert.Equal(2, s.Pins.Count));
    }

    [Fact]
    public void Control_page_and_cross_page_links()
    {
        var p5 = Page(5);
        Assert.Contains(p5.Symbols, s => s.Tag == "04K1" && s.Kind == SymbolKind.Coil);
        Assert.Contains(p5.Symbols, s => s.Tag == "04K1" && s.Kind == SymbolKind.NoContact);
        Assert.Contains(p5.Symbols, s => s.Tag == "05S1" && s.Kind == SymbolKind.NcContact);
        Assert.Contains(p5.Symbols, s => s.Tag == "05PB1" && s.Kind == SymbolKind.PushbuttonNo);
        Assert.Contains(p5.Symbols, s => s.Tag == "05PB1" && s.Kind == SymbolKind.Lamp);
        var links = Doc.Value.Pages.SelectMany(p => p.OffPageLinks).ToList();
        Assert.Equal(4, links.Count);
        Assert.All(links, l => Assert.NotNull(l.PairedLinkId));
    }
}

/// <summary>Combining detected poles into one multi-circuit symbol, splitting, and learned auto-combine.</summary>
public class CombineTests
{
    private static readonly string Pdf = Path.Combine(AppContext.BaseDirectory, "Fixtures", "swe-ansi-hyperlink-test.pdf");

    private static (ImportDocument doc, MappingStore store, SchematicRecognizer rec) Run(MappingStore store = null)
    {
        store = store ?? new MappingStore(Path.Combine(Path.GetTempPath(), "sweets-test-" + Guid.NewGuid() + ".json"));
        var rec = new SchematicRecognizer(new RecognitionOptions(), RuleLibrary.Load(), store);
        return (rec.Recognize(Pdf), store, rec);
    }

    [Fact]
    public void Four_fuse_poles_combine_into_one_symbol_and_split_back()
    {
        var (doc, _, rec) = Run();
        var p4 = doc.Pages.Single(p => p.PageNumber == 4);
        var poles = p4.Symbols.Where(s => s.Tag == "04F1").ToList();
        Assert.Equal(4, poles.Count);
        Assert.Contains(SymbolCombiner.Suggestions(p4), g => g.Count == 4 && g.All(s => s.Tag == "04F1"));

        var c = rec.Combine(doc, poles);
        Assert.Equal(35, p4.Symbols.Count);
        Assert.Equal(8, c.Pins.Count);
        Assert.Equal(SymbolKind.Fuse, c.Kind);
        Assert.Equal("04F1", c.Tag);
        Assert.Equal(4, c.Signature.Poles);
        Assert.True(c.Bounds.Left <= poles.Min(p => p.Bounds.Left) && c.Bounds.Right >= poles.Max(p => p.Bounds.Right));

        var back = rec.Split(doc, c);
        Assert.Equal(4, back.Count);
        Assert.Equal(38, p4.Symbols.Count);
    }

    [Fact]
    public void Mapped_combination_is_recombined_on_the_next_import_without_changing_single_pole_defaults()
    {
        var (doc, store, rec) = Run();
        var p4 = doc.Pages.Single(p => p.PageNumber == 4);
        var c = rec.Combine(doc, p4.Symbols.Where(s => s.Tag == "04F1").ToList());
        store.Learn(c, "ANSI_FUSE_4P");

        var (doc2, _, _) = Run(store);
        var p4b = doc2.Pages.Single(p => p.PageNumber == 4);
        var fuse = Assert.Single(p4b.Symbols.Where(s => s.Tag == "04F1"));
        Assert.True(fuse.IsCombined);
        Assert.Equal("ANSI_FUSE_4P", fuse.SelectedSweSymbol);
        // a single-pole fuse elsewhere must not default to the 4-pole symbol
        Assert.DoesNotContain(store.All, m => m.SignatureKey == null && m.SweSymbolName == "ANSI_FUSE_4P");
    }
}

/// <summary>Only the pages the reviewer imports are reviewed, grouped into components and built.</summary>
public class PageSelectionTests
{
    private static readonly string Pdf = Path.Combine(AppContext.BaseDirectory, "Fixtures", "swe-ansi-hyperlink-test.pdf");

    private static ImportDocument Run(ISet<int> force = null) =>
        new SchematicRecognizer(new RecognitionOptions(), RuleLibrary.Load(),
            new MappingStore(Path.Combine(Path.GetTempPath(), "sweets-test-" + Guid.NewGuid() + ".json"))).Recognize(Pdf, null, default, force);

    private static void MapEverything(ImportDocument doc)
    {
        foreach (var s in doc.AllSymbols) { s.SelectedSweSymbol = "SYM_" + s.Kind; s.Review = ReviewState.Confirmed; s.UserReviewed = true; }
    }

    [Fact]
    public void Reports_are_not_built_and_left_out_pages_are_dropped()
    {
        var doc = Run();
        MapEverything(doc);
        Assert.Equal(new[] { 4, 5 }, doc.IncludedPages.Select(p => p.PageNumber).ToArray());
        var plan = SWEETs.SwePdfSchematic.Core.Build.BuildPlanFactory.Create(doc, new SWEETs.SwePdfSchematic.Core.Build.BuildOptions { ProjectName = "T", TemplateName = "ANSI" }, new MappingStore(Path.GetTempFileName()));
        Assert.Equal(new[] { 4, 5 }, plan.Pages.Select(p => p.SourcePage).ToArray());

        doc.Pages.Single(p => p.PageNumber == 5).Include = false;
        CrossReferenceResolver.BuildComponents(doc);
        var plan2 = SWEETs.SwePdfSchematic.Core.Build.BuildPlanFactory.Create(doc, new SWEETs.SwePdfSchematic.Core.Build.BuildOptions { ProjectName = "T", TemplateName = "ANSI" }, new MappingStore(Path.GetTempFileName()));
        Assert.Equal(new[] { 4 }, plan2.Pages.Select(p => p.SourcePage).ToArray());
        Assert.DoesNotContain(plan2.Components, c => c.Tag == "05S1" || c.Tag == "05PB1"); // only on page 5
    }

    [Fact]
    public void A_report_page_can_be_forced_to_schematic()
    {
        var doc = Run(new HashSet<int> { 13 });
        var p13 = doc.Pages.Single(p => p.PageNumber == 13);
        Assert.Equal(PageKind.Schematic, p13.Kind);
        Assert.True(p13.ForcedSchematic);
        Assert.True(p13.Include);
    }
}

public class UnitTests
{
    [Fact]
    public void Inch_templates_get_coordinates_in_inches()
    {
        var page = new PageAnalysis { Frame = new SWEETs.SwePdfSchematic.Core.Geometry.Rect(0, 0, 254, 254) };
        var mm = SWEETs.SwePdfSchematic.Core.Build.BuildPlanFactory.Transform(page, new SWEETs.SwePdfSchematic.Core.Build.BuildOptions { FitToSheet = false, SheetDrawingLeft = 0, SheetDrawingBottom = 0 });
        var inch = SWEETs.SwePdfSchematic.Core.Build.BuildPlanFactory.Transform(page, new SWEETs.SwePdfSchematic.Core.Build.BuildOptions { FitToSheet = false, SheetDrawingLeft = 0, SheetDrawingBottom = 0, Units = SWEETs.SwePdfSchematic.Core.Build.DrawingUnits.Inches });
        Assert.Equal(254.0, mm(new SWEETs.SwePdfSchematic.Core.Geometry.Pt(254, 0)).X, 6);
        Assert.Equal(10.0, inch(new SWEETs.SwePdfSchematic.Core.Geometry.Pt(254, 0)).X, 6);
        // fit to a 10 x 10 in drawing area: a 254 mm frame fills it
        var fit = SWEETs.SwePdfSchematic.Core.Build.BuildPlanFactory.Transform(page, new SWEETs.SwePdfSchematic.Core.Build.BuildOptions { FitToSheet = true, SheetDrawingLeft = 0, SheetDrawingBottom = 0, SheetDrawingWidth = 10, SheetDrawingHeight = 10, Units = SWEETs.SwePdfSchematic.Core.Build.DrawingUnits.Inches });
        Assert.Equal(10.0, fit(new SWEETs.SwePdfSchematic.Core.Geometry.Pt(254, 254)).Y, 6);
    }
}

/// <summary>Manufacturer parts read from the bill-of-material reports of the same PDF.</summary>
public class BomTests
{
    private static readonly string Pdf = Path.Combine(AppContext.BaseDirectory, "Fixtures", "swe-ansi-hyperlink-test.pdf");
    private static readonly System.Lazy<ImportDocument> Doc = new System.Lazy<ImportDocument>(() =>
        new SchematicRecognizer(new RecognitionOptions(), RuleLibrary.Load(),
            new MappingStore(Path.Combine(Path.GetTempPath(), "sweets-test-" + Guid.NewGuid() + ".json"))).Recognize(Pdf));

    private static PartAssignment Part(string mark, string reference) =>
        Doc.Value.BomFor(mark)?.Parts.SingleOrDefault(p => p.Key == BomText.Key(reference));

    [Fact]
    public void Fuse_holder_carries_its_disconnector_and_four_fuse_links()
    {
        var links = Part("04F1", "JKS-60");
        Assert.NotNull(links);
        Assert.Equal("Bussmann", links.Manufacturer);
        Assert.Equal(4, links.Quantity);
        Assert.Equal("37946", links.ArticleNumber);
        var holder = Part("04F1", "SWST_FD4-2352798");
        Assert.NotNull(holder);
        Assert.Equal("SWST_FD4-2352798", holder.Reference);   // the unwrapped spelling wins over "SWST_FD4-23527 98"
        Assert.Equal("SWST", holder.Manufacturer);
    }

    [Fact]
    public void The_single_part_carries_the_circuits_and_fuse_links_are_accessories()
    {
        var f1 = Doc.Value.BomFor("04F1");
        Assert.True(Part("04F1", "SWST_FD4-2352798")?.CarriesCircuits,
            "04F1 parts: " + string.Join(" | ", f1.Parts.Select(p => $"{p.Reference} ×{p.Quantity} {p.Origin} base={p.CarriesCircuits}")));
        Assert.False(Part("04F1", "JKS-60")?.CarriesCircuits);
        Assert.True(Part("04Q2", "SWST-CB-3P-30A")?.CarriesCircuits);
    }

    [Fact]
    public void Strip_terminals_are_numbered_and_their_centre_line_is_not_a_wire()
    {
        var d = Doc.Value;
        var terms = d.AllSymbols.Where(s => s.PageNumber == 4 && s.Kind == SymbolKind.Terminal && s.Tag == "X1").ToList();
        Assert.True(terms.Count >= 8, $"{terms.Count} X1 terminals");
        Assert.Equal(terms.Count, terms.Select(t => t.TerminalNumber).Distinct().Count());
        Assert.Contains(terms, t => t.TerminalNumber == "1");
        Assert.True(d.HasSymbol(d.BomFor("X1 1")));                 // terminal X1 1 is drawn: not an item without symbol
        var p4 = d.Pages.Single(p => p.PageNumber == 4);
        Assert.DoesNotContain(p4.Nets, n => n.Segments.All(s => s.Dashed) && terms.Count(t => n.Segments.Any(s => s.InteriorContains(t.Anchor, 0.6))) >= 2);
    }

    [Fact]
    public void Shared_rows_give_each_mark_one_part()
    {
        foreach (var q in new[] { "04Q1", "04Q2", "04Q3" })
            Assert.Equal(1, Part(q, "SWST-CB-3P-30A")?.Quantity);
        Assert.NotNull(Part("N1", "SWSTPLC023I3O"));
        Assert.NotNull(Part("N2", "SWSTPLC023I3O"));          // "N2 N1" in one cell
        Assert.Equal("Leroy Somer", Part("04M2", "LS112M-4P(4)")?.Manufacturer);
    }

    [Fact]
    public void Items_without_symbols_are_classified()
    {
        var d = Doc.Value;
        Assert.Equal(BomItemKind.Cable, d.BomFor("W1")?.Kind);
        Assert.Equal("1000 R2V-RH 4X4 M", d.BomFor("W1")?.Parts.Single().Reference);
        Assert.Equal(BomItemKind.Location, d.BomFor("L1")?.Kind);
        Assert.Equal(3, d.BomFor("L1")?.Parts.Count);              // enclosure, mounting plate, glands
        Assert.Equal(BomItemKind.Component, d.BomFor("R2")?.Kind);  // DIN rail
        Assert.Equal("L1", d.BomFor("D4")?.LocationTag);
        Assert.Equal(BomItemKind.Terminal, d.BomFor("X1 7")?.Kind);
        Assert.Equal(d.BomFor("X1 7"), d.BomFor("X1.7"));
        Assert.All(d.Bom.Where(b => b.Mark.StartsWith("L3-")), b => Assert.Equal(BomItemKind.Wire, b.Kind));
    }
}
