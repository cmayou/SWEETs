using System.Collections.Generic;
using System.Linq;

namespace DataSheetMfgPartsImport.Model;

/// <summary>One manufacturer part found in a datasheet, before the user reviews it.</summary>
public sealed class ExtractedPart
{
    public Dictionary<PartField, FieldValue> Fields { get; } = new();

    public List<CircuitSpec> Circuits { get; set; } = new();

    /// <summary>Product category keyword (e.g. "Contactor") used to suggest a SOLIDWORKS Electrical class.</summary>
    public string Category { get; set; }

    public string SourceFile { get; set; }

    /// <summary>"Labeled" for a part found by label, "Table row" for an ordering-table row, "File name" as fallback.</summary>
    public string Origin { get; set; }

    /// <summary>Variant keys read from tables: "poles", "frame", "phases", "pins".</summary>
    public Dictionary<string, string> Attributes { get; } = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the card starts selected (main product family, not accessories).</summary>
    public bool Preselect { get; set; } = true;

    public FieldValue this[PartField f]
    {
        get => Fields.TryGetValue(f, out var v) ? v : FieldValue.Empty;
        set => Fields[f] = value;
    }

    public string Get(PartField f) => this[f].Value ?? "";

    public void SetIfBetter(PartField f, FieldValue candidate)
    {
        if (candidate == null || !candidate.HasValue) return;
        var cur = this[f];
        if (!cur.HasValue || candidate.Confidence > cur.Confidence) Fields[f] = candidate;
    }

    /// <summary>Share of fields that have a value (0-100). Shown as the coverage bar.</summary>
    public int CoveragePercent
    {
        get
        {
            var total = PartFieldInfo.All.Length;
            var filled = PartFieldInfo.All.Count(i => this[i.Field].HasValue);
            return total == 0 ? 0 : (int)System.Math.Round(100.0 * filled / total);
        }
    }

    public ExtractedPart Clone()
    {
        var p = new ExtractedPart { Category = Category, SourceFile = SourceFile, Origin = Origin, Preselect = Preselect };
        foreach (var kv in Fields) p.Fields[kv.Key] = kv.Value.Clone();
        foreach (var kv in Attributes) p.Attributes[kv.Key] = kv.Value;
        p.Circuits = Circuits.Select(c => c.Clone()).ToList();
        return p;
    }
}

/// <summary>Everything extracted from one data sheet file, as the importer dialog shows it.</summary>
public sealed class ExtractedFile
{
    public string FilePath { get; set; }
    public int PageCount { get; set; }
    public bool HasTextLayer { get; set; }
    public List<ExtractedPart> Parts { get; } = new();
    public List<string> Messages { get; } = new();
}
