using System.Collections.Generic;

namespace DataSheetMfgPartsImport.Model;

/// <summary>Links the importer's fields to the keys of a <see cref="PartRecord"/> (the extractors' output).</summary>
public static class PartKeys
{
    private static readonly (PartField Field, string Key)[] Pairs =
    {
        (PartField.Reference,            "partNumber"),
        (PartField.Manufacturer,         "manufacturer"),
        (PartField.Description,          "description"),
        (PartField.Series,               "series"),
        (PartField.ComponentType,        "category"),
        (PartField.UseVoltage,           "ratedVoltage"),
        (PartField.ControlVoltage,       "coilVoltage"),
        (PartField.UseFrequency,         "frequency"),
        (PartField.RatedCurrent,         "ratedCurrent"),
        (PartField.RatedPower,           "ratedPower"),
        (PartField.Poles,                "poles"),
        (PartField.ContactConfiguration, "contacts"),
        (PartField.ProtectionRating,     "ipRating"),
        (PartField.OperatingTemperature, "tempRange"),
        (PartField.WidthMm,              "width"),
        (PartField.HeightMm,             "height"),
        (PartField.DepthMm,              "depth"),
        (PartField.WeightG,              "weight"),
        (PartField.Mounting,             "mounting"),
        (PartField.SupplierReference,    "supplierRef"),
        (PartField.EanGtin,              "ean"),
        (PartField.UnitPrice,            "unitPrice"),
        (PartField.Currency,             "currency"),
        (PartField.OtherSpecifications,  "otherSpecs"),
    };

    public static IEnumerable<(PartField Field, string Key)> All => Pairs;

    public static string KeyOf(PartField f)
    {
        foreach (var p in Pairs) if (p.Field == f) return p.Key;
        return null;
    }

    /// <summary>Fields written by dedicated code, never through the mapping table in Settings.</summary>
    public static bool IsFixed(PartField f) => f is PartField.Reference or PartField.Manufacturer or PartField.ComponentType;
}
