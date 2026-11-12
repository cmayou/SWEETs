namespace DataSheetMfgPartsImport.Model;

/// <summary>
/// Every scalar value the importer shows and can save. Each one maps to a field of a <see cref="PartRecord"/>
/// (see <see cref="PartKeys"/>) and, when the part is saved, to a manufacturer part setter chosen in Settings.
/// To add a field: add it here, in <see cref="PartFieldInfo.All"/> and in <see cref="PartKeys"/>.
/// </summary>
public enum PartField
{
    Reference,
    Manufacturer,
    Description,
    Series,
    ComponentType,

    UseVoltage,
    ControlVoltage,
    UseFrequency,
    RatedCurrent,
    RatedPower,
    Poles,
    ContactConfiguration,
    ProtectionRating,
    OperatingTemperature,

    WidthMm,
    HeightMm,
    DepthMm,
    WeightG,
    Mounting,

    SupplierReference,
    EanGtin,
    UnitPrice,
    Currency,
    OtherSpecifications,
}

public enum FieldGroup { Identity, Ratings, Dimensions, Other }

public sealed class PartFieldInfo
{
    public PartField Field { get; }
    public string Label { get; }
    public string Unit { get; }
    public FieldGroup Group { get; }
    public bool Required { get; }

    private PartFieldInfo(PartField field, string label, string unit, FieldGroup group, bool required = false)
    {
        Field = field; Label = label; Unit = unit; Group = group; Required = required;
    }

    public static readonly PartFieldInfo[] All =
    {
        new(PartField.Reference,            "Reference",              "",    FieldGroup.Identity, required: true),
        new(PartField.Manufacturer,         "Manufacturer",           "",    FieldGroup.Identity, required: true),
        new(PartField.Description,          "Description",            "",    FieldGroup.Identity),
        new(PartField.Series,               "Series",                 "",    FieldGroup.Identity),
        new(PartField.ComponentType,        "Component type",         "",    FieldGroup.Identity),

        new(PartField.UseVoltage,           "Use voltage",            "V",   FieldGroup.Ratings),
        new(PartField.ControlVoltage,       "Control voltage",        "V",   FieldGroup.Ratings),
        new(PartField.UseFrequency,         "Use frequency",          "Hz",  FieldGroup.Ratings),
        new(PartField.RatedCurrent,         "Rated current",          "A",   FieldGroup.Ratings),
        new(PartField.RatedPower,           "Rated power",            "W",   FieldGroup.Ratings),
        new(PartField.Poles,                "Poles",                  "",    FieldGroup.Ratings),
        new(PartField.ContactConfiguration, "Contact configuration",  "",    FieldGroup.Ratings),
        new(PartField.ProtectionRating,     "Protection rating",      "",    FieldGroup.Ratings),
        new(PartField.OperatingTemperature, "Operating temperature",  "°C",  FieldGroup.Ratings),

        new(PartField.WidthMm,              "Width",                  "mm",  FieldGroup.Dimensions),
        new(PartField.HeightMm,             "Height",                 "mm",  FieldGroup.Dimensions),
        new(PartField.DepthMm,              "Depth",                  "mm",  FieldGroup.Dimensions),
        new(PartField.WeightG,              "Weight",                 "g",   FieldGroup.Dimensions),
        new(PartField.Mounting,             "Mounting",               "",    FieldGroup.Dimensions),

        new(PartField.SupplierReference,    "Supplier reference",     "",    FieldGroup.Other),
        new(PartField.EanGtin,              "EAN / GTIN",             "",    FieldGroup.Other),
        new(PartField.UnitPrice,            "Unit price",             "",    FieldGroup.Other),
        new(PartField.Currency,             "Currency",               "",    FieldGroup.Other),
        new(PartField.OtherSpecifications,  "Other specifications",   "",    FieldGroup.Other),
    };

    public static PartFieldInfo Get(PartField f)
    {
        foreach (var i in All) if (i.Field == f) return i;
        return null;
    }
}
