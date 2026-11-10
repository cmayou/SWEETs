using System;
using System.Collections.Generic;

namespace DataSheetMfgPartsImport.Model
{
    public sealed class FieldDef
    {
        public string Key;
        public string Label;
        public string Unit;
        public string Section;
        public bool Required;
        public string Hint;

        public string Caption
        {
            get { return string.IsNullOrEmpty(Unit) ? Label : Label + " (" + Unit + ")"; }
        }
    }

    public static class Schema
    {
        public static readonly FieldDef[] Fields = new FieldDef[]
        {
            new FieldDef { Key = "manufacturer", Label = "Manufacturer", Section = "Identification", Required = true },
            new FieldDef { Key = "partNumber", Label = "Manufacturer part number", Section = "Identification", Required = true },
            new FieldDef { Key = "description", Label = "Description", Section = "Identification" },
            new FieldDef { Key = "series", Label = "Series or family", Section = "Identification" },
            new FieldDef { Key = "category", Label = "Component type as printed", Section = "Identification" },
            new FieldDef { Key = "supplierRef", Label = "Supplier reference", Section = "Identification" },
            new FieldDef { Key = "ean", Label = "EAN or GTIN", Section = "Identification" },

            new FieldDef { Key = "ratedVoltage", Label = "Rated voltage", Unit = "V", Section = "Electrical" },
            new FieldDef { Key = "ratedCurrent", Label = "Rated current", Unit = "A", Section = "Electrical" },
            new FieldDef { Key = "ratedPower", Label = "Rated power", Unit = "W", Section = "Electrical" },
            new FieldDef { Key = "frequency", Label = "Frequency", Unit = "Hz", Section = "Electrical" },
            new FieldDef { Key = "coilVoltage", Label = "Coil or control voltage", Unit = "V", Section = "Electrical" },
            new FieldDef { Key = "poles", Label = "Poles", Section = "Electrical" },
            new FieldDef { Key = "contacts", Label = "Contact configuration", Section = "Electrical" },
            new FieldDef { Key = "ipRating", Label = "Protection rating", Section = "Electrical" },
            new FieldDef { Key = "tempRange", Label = "Operating temperature", Unit = "°C", Section = "Electrical" },

            new FieldDef { Key = "width", Label = "Width", Unit = "mm", Section = "Mechanical" },
            new FieldDef { Key = "height", Label = "Height", Unit = "mm", Section = "Mechanical" },
            new FieldDef { Key = "depth", Label = "Depth", Unit = "mm", Section = "Mechanical" },
            new FieldDef { Key = "weight", Label = "Weight", Unit = "kg", Section = "Mechanical" },
            new FieldDef { Key = "mounting", Label = "Mounting", Section = "Mechanical" },
            new FieldDef { Key = "otherSpecs", Label = "Other specifications", Section = "Mechanical",
                Hint = "semicolon-separated \"Label: value\" pairs for other notable specifications such as pitch, colour, material, gender, rows, flammability" },

            new FieldDef { Key = "unitPrice", Label = "Unit price", Section = "Commercial" },
            new FieldDef { Key = "currency", Label = "Currency", Section = "Commercial" }
        };

        /// <summary>Fields written by dedicated logic, not through the user-editable mapping.</summary>
        public static bool IsFixed(string key)
        {
            return key == "manufacturer" || key == "partNumber" || key == "category";
        }
    }

    public sealed class TerminalRec
    {
        public string Designation = "";
        public string Function = "";
    }

    public sealed class PartRecord
    {
        public readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly List<TerminalRec> Terminals = new List<TerminalRec>();
        public readonly Dictionary<string, string> Flags = new Dictionary<string, string>(StringComparer.Ordinal);
        public bool Written;
        public string SourceFile = "";
        public int ClassNodeId = -1;

        public string Get(string key)
        {
            string v;
            return Values.TryGetValue(key, out v) && v != null ? v.Trim() : "";
        }

        public void Set(string key, string value)
        {
            Values[key] = value ?? "";
        }
    }
}
