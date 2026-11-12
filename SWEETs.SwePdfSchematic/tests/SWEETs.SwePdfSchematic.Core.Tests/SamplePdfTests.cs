using System.IO;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Build;
using SWEETs.SwePdfSchematic.Core.Mapping;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Recognition;
using Xunit;

/// <summary>End-to-end against samples/sample-iec-2page.pdf (regenerate with samples/make_sample.py).</summary>
public class SamplePdfTests
{
    private static readonly string Pdf = Path.Combine(AppContext.BaseDirectory, "Fixtures", "sample-iec-2page.pdf");

    private static (ImportDocument doc, MappingStore store, SchematicRecognizer rec) Run()
    {
        var store = new MappingStore(Path.Combine(Path.GetTempPath(), "sweets-test-" + Guid.NewGuid() + ".json"));
        var rec = new SchematicRecognizer(new RecognitionOptions(), RuleLibrary.Load(), store);
        return (rec.Recognize(Pdf), store, rec);
    }

    [Fact]
    public void Reads_layout_and_title_block()
    {
        var (doc, _, _) = Run();
        Assert.Equal(2, doc.Pages.Count);
        var p1 = doc.Pages[0];
        Assert.Equal(8, p1.ColumnCount);
        Assert.Equal("TEW-1001", p1.TitleBlock.Get("DWGNO"));
        Assert.Equal("Power distribution", p1.TitleBlock.Get("TITLE"));
        Assert.Equal("B", p1.TitleBlock.Get("REV"));
        Assert.All(doc.Pages, p => Assert.Equal(SymbolStandard.IEC, p.Standard));
    }

    [Fact]
    public void Finds_symbols_tags_and_poles()
    {
        var (doc, _, _) = Run();
        var p1 = doc.Pages[0];
        // 3 breaker poles, 3 main contacts, 1 motor
        Assert.Equal(7, p1.Symbols.Count);
        Assert.Equal(3, p1.Symbols.Count(s => s.Tag == "-Q1"));
        Assert.Equal(3, p1.Symbols.Count(s => s.Tag == "-K1"));
        Assert.Contains(p1.Symbols, s => s.Tag == "-M1" && s.Kind == SymbolKind.Motor);
        Assert.All(p1.Symbols.Where(s => s.Tag == "-Q1"), s => Assert.Equal(SymbolKind.CircuitBreaker, s.Kind));
        Assert.All(p1.Symbols.Where(s => s.Tag == "-K1"), s => Assert.Equal(SymbolKind.NoContact, s.Kind));

        var p2 = doc.Pages[1];
        Assert.Contains(p2.Symbols, s => s.Tag == "-K1" && s.Kind == SymbolKind.Coil);
        Assert.Contains(p2.Symbols, s => s.Tag == "-S1" && s.Kind == SymbolKind.PushbuttonNo);
        Assert.Contains(p2.Symbols, s => s.Tag == "-H1" && s.Kind == SymbolKind.Lamp);
    }

    [Fact]
    public void Groups_components_across_pages()
    {
        var (doc, _, _) = Run();
        var k1 = doc.Components.Single(c => c.Tag == "-K1");
        Assert.Equal(new[] { 1, 2 }, k1.Pages.ToArray());
        Assert.Equal(SymbolKind.Coil, k1.Parent.Kind);
    }

    [Fact]
    public void Pairs_origin_destination_links_by_wire_number()
    {
        var (doc, _, _) = Run();
        var links = doc.Pages.SelectMany(p => p.OffPageLinks).Where(l => l.TargetPage.HasValue).ToList();
        Assert.Equal(4, links.Count);
        Assert.All(links, l => Assert.NotNull(l.PairedLinkId));
        foreach (var l in links)
        {
            var other = links.Single(x => x.Id == l.PairedLinkId);
            var wl = doc.Pages[l.PageNumber - 1].Nets.Single(n => n.Index == l.NetIndex).WireNumber;
            var wo = doc.Pages[other.PageNumber - 1].Nets.Single(n => n.Index == other.NetIndex).WireNumber;
            Assert.Equal(wl, wo);
        }
    }

    [Fact]
    public async Task Learned_mappings_make_the_second_import_build_ready()
    {
        var (doc, store, rec) = Run();
        Assert.False(doc.ReadyToBuild); // empty store: everything needs a mapping
        foreach (var s in doc.AllSymbols) store.Learn(s, "SYM_" + s.Kind);
        store.Save();

        var again = new SchematicRecognizer(new RecognitionOptions(), RuleLibrary.Load(), new MappingStore(store.FilePath)).Recognize(Pdf);
        Assert.True(again.ReadyToBuild);

        var target = new DryRunTarget();
        var plan = BuildPlanFactory.Create(again, new BuildOptions { TemplateName = "IEC" }, store);
        var result = await new BuildRunner(target).RunAsync(plan, null, default);
        Assert.Equal(0, result.Errors);
        Assert.Equal(2, result.Pages);
        Assert.Equal(again.AllSymbols.Count(), result.Symbols);
        Assert.Contains(target.Operations, o => o.StartsWith("component -K1"));
    }
}
