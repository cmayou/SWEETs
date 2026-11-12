using System.Text.RegularExpressions;

namespace DataSheetMfgPartsImport.Extraction;

/// <summary>The regular expressions the circuit detector uses to find coils, poles, contacts and terminal marks.</summary>
internal static class CircuitPatterns
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    /// <summary>Contact configuration such as "3NO+1NC", "1 NO + 1 NC", "2 CO", "1 x NO".</summary>
    public static readonly Regex ContactCount = new(
        @"(?<n>\d)\s*(?:x\s*)?(?<k>N\.?O\.?|N/O|N\.?C\.?|N/C|C\.?O\.?|changeover|SPDT|DPDT|make|break)\b", O);

    public static readonly Regex PoleCount = new(@"(?:(?<n>\d)\s*[- ]?\s*pole\b|\b(?<n>\d)\s*P\b(?!\s*[,.]?\d)|number\s+of\s+poles\s*[:|]?\s*(?<n>\d))", O);

    /// <summary>Explicit auxiliary pair like "13-14", "21 - 22", "13/14".</summary>
    public static readonly Regex TerminalPair = new(@"(?<![\d.,])(?<a>[1-9][1-4])\s*[-–/]\s*(?<b>[1-9][1-4])(?![\d.,]|\s*(?:mm|V|A|Hz|%))", O);

    /// <summary>Changeover triple like "11-12-14".</summary>
    public static readonly Regex TerminalTriple = new(@"(?<![\d.])(?<s>[1-9])1\s*[-–/]\s*\k<s>2\s*[-–/]\s*\k<s>4(?!\d)", O);

    public static readonly Regex CoilTerminals = new(@"\bA1\b[^A]{0,30}\bA2\b", O);

    public static readonly Regex MainPoleTerminals = new(@"\b(?<a>[135])\s*/\s*L(?<l>[123])\b", O);

    /// <summary>Lines where explicit terminal pairs are trusted.</summary>
    public static readonly Regex TerminalContext = new(@"(?:terminal|marking|contact|aux|auxiliary|\bNO\b|\bNC\b|connection|wiring|diagram)", O);
}
