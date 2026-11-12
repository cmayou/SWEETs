using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Recognition;
using Xunit;

public class GeometryTests
{
    private static Segment S(double x1, double y1, double x2, double y2) => new Segment(new Pt(x1, y1), new Pt(x2, y2));

    [Fact]
    public void Merges_collinear_overlapping_pieces()
    {
        var merged = WireExtractor.MergeCollinear(new[] { S(0, 0, 10, 0), S(10, 0, 25, 0), S(30, 0, 40, 0) });
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, m => m.Length > 24.9 && m.Length < 25.1);
    }

    [Fact]
    public void T_junction_connects_but_plain_crossing_does_not()
    {
        var o = new RecognitionOptions { MinNetLength = 1 };
        var tee = new WireExtractor(o).Extract(new[] { S(0, 0, 20, 0), S(10, 0, 10, 15) }, new PathShape[0]);
        Assert.Single(tee.Nets);
        Assert.Single(tee.Nets[0].Junctions);

        var cross = new WireExtractor(o).Extract(new[] { S(0, 5, 20, 5), S(10, 0, 10, 15) }, new PathShape[0]);
        Assert.Equal(2, cross.Nets.Count);
    }

    [Fact]
    public void Crossing_with_junction_dot_connects()
    {
        var o = new RecognitionOptions { MinNetLength = 1 };
        var dot = new PathShape { Id = 99, Closed = true, Filled = true, HasCurves = true, Bounds = new Rect(9.4, 4.4, 10.6, 5.6) };
        var r = new WireExtractor(o).Extract(new[] { S(0, 5, 20, 5), S(10, 0, 10, 15) }, new[] { dot });
        Assert.Single(r.Nets);
    }

    [Fact]
    public void Short_isolated_lines_are_demoted_to_symbol_linework()
    {
        var r = new WireExtractor(new RecognitionOptions()).Extract(new[] { S(0, 0, 4.5, 0), S(0, 10, 50, 10) }, new PathShape[0]);
        Assert.Single(r.Nets);
        Assert.Single(r.Demoted);
    }

    [Fact]
    public void Signature_is_rotation_and_translation_invariant()
    {
        var pinsV = new[] { new SymbolPin { Location = new Pt(0, 0), Side = PinSide.Bottom }, new SymbolPin { Location = new Pt(0, 12), Side = PinSide.Top } };
        var segsV = new[] { S(0, 4, -3, 9) };
        var pinsH = new[] { new SymbolPin { Location = new Pt(100, 50), Side = PinSide.Left }, new SymbolPin { Location = new Pt(112, 50), Side = PinSide.Right } };
        var segsH = new[] { S(104, 50, 109, 53) };
        var a = SymbolSignature.Compute(segsV, new PathShape[0], pinsV, new string[0], 1);
        var b = SymbolSignature.Compute(segsH, new PathShape[0], pinsH, new string[0], 1);
        Assert.Equal(a.Key, b.Key);
    }
}
