using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Recognition;
using Xunit;

public class TagParserTests
{
    [Theory]
    [InlineData("-K1", SymbolStandard.IEC, "K")]
    [InlineData("=A1+B2-Q12", SymbolStandard.IEC, "Q")]
    [InlineData("-X1A", SymbolStandard.IEC, "X")]
    [InlineData("CR1", SymbolStandard.ANSI, "CR")]
    [InlineData("PB10", SymbolStandard.ANSI, "PB")]
    [InlineData("MTR2", SymbolStandard.ANSI, "MTR")]
    [InlineData("1CR", SymbolStandard.ANSI, "CR")]
    public void Parses_device_tags(string text, SymbolStandard std, string prefix)
    {
        var t = TagParser.ParseDeviceTag(text);
        Assert.NotNull(t);
        Assert.Equal(std, t.Standard);
        Assert.Equal(prefix, t.Prefix);
    }

    [Theory]
    [InlineData("101")]
    [InlineData("L1-3")]
    [InlineData("TITLE:")]
    [InlineData("/2.3")]
    public void Rejects_non_tags(string text) => Assert.Null(TagParser.ParseDeviceTag(text));

    /// <summary>"L1", "T3", "A1" name pins far more often than devices: they never take a device mark from a symbol.</summary>
    [Theory]
    [InlineData("L1")]
    [InlineData("T3")]
    [InlineData("A2")]
    [InlineData("PE")]
    public void Pin_designations_are_conventional_pin_names(string text) => Assert.True(TagParser.IsConventionalPinName(text));

    [Theory]
    [InlineData("X1")]
    [InlineData("04K1")]
    [InlineData("-K1")]
    public void Device_marks_are_not_pin_names(string text) => Assert.False(TagParser.IsConventionalPinName(text));

    [Theory]
    [InlineData("/2.3", 2, 3)]
    [InlineData("12-4", 12, 4)]
    [InlineData("1.4", 1, 4)]
    public void Parses_cross_references(string text, int page, int col)
    {
        Assert.True(TagParser.TryParseXref(text, out var p, out var c));
        Assert.Equal(page, p);
        Assert.Equal(col, c);
    }

    [Fact]
    public void Classifies_roles()
    {
        Assert.Equal(TextRole.DeviceTag, TagParser.Classify("-S1"));
        Assert.Equal(TextRole.CrossReference, TagParser.Classify("/1.3"));
        Assert.Equal(TextRole.WireNumber, TagParser.Classify("102"));
        Assert.Equal(TextRole.Label, TagParser.Classify("Conveyor"));
    }
}
