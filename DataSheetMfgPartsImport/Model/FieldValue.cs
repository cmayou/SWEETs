namespace DataSheetMfgPartsImport.Model;

/// <summary>How sure the extractor is about a value. Drives the coloured dot in the preview.</summary>
public enum Confidence
{
    Missing = 0,
    Low = 1,     // weak heuristic (e.g. guessed from file name)
    Medium = 2,  // heuristic with context (e.g. largest heading, table row)
    High = 3,    // explicit label + value ("Rated operational voltage Ue: 400 V")
    Edited = 4,  // typed by the user in the preview
}

public sealed class FieldValue
{
    public string Value { get; set; }
    public Confidence Confidence { get; set; }

    /// <summary>Text snippet the value came from, shown as a tooltip ("p2: Coil voltage 24 V DC").</summary>
    public string Source { get; set; }

    public static FieldValue Empty => new() { Value = "", Confidence = Confidence.Missing };

    public static FieldValue Of(string value, Confidence c, string source = null)
        => new() { Value = value ?? "", Confidence = string.IsNullOrWhiteSpace(value) ? Confidence.Missing : c, Source = source };

    public bool HasValue => !string.IsNullOrWhiteSpace(Value);

    public FieldValue Clone() => new() { Value = Value, Confidence = Confidence, Source = Source };

    public override string ToString() => $"{Value} ({Confidence})";
}
