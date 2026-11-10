using System;
using System.Collections.Generic;
using System.Linq;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    /// <summary>
    /// One shape of a shape file (*.shp source / *.shx compiled): number, name and specification bytes
    /// (including the terminating 0).
    /// </summary>
    public sealed class ShapeDefinition
    {
        /// <summary>Shape numbers of a non-font shape file are 1-255.</summary>
        public const int MinNumber = 1, MaxNumber = 255;
        /// <summary>Upper limit for one shape's specification bytes (AutoCAD shape-file rule).</summary>
        public const int MaxBytes = 2000;

        public int Number { get; set; }
        public string Name { get; set; }
        public byte[] Bytes { get; set; } = { 0 };

        /// <summary>The definition's lines as read from the .shp (header line + spec lines), kept verbatim while unchanged.</summary>
        public string SourceText { get; set; }

        public ShapeDefinition Clone() => new ShapeDefinition { Number = Number, Name = Name, Bytes = (byte[])Bytes.Clone(), SourceText = SourceText };

        public bool SameContent(ShapeDefinition o) =>
            o != null && o.Number == Number && string.Equals(o.Name, Name, StringComparison.Ordinal) && o.Bytes.SequenceEqual(Bytes);

        /// <summary>Problems that would make the shape file invalid; empty when the shape is fine.</summary>
        public IEnumerable<string> Validate()
        {
            if (Number < MinNumber || Number > MaxNumber) yield return $"Shape number {Number} is outside {MinNumber}-{MaxNumber}.";
            if (string.IsNullOrWhiteSpace(Name)) yield return "Shape name is empty.";
            else
            {
                if (Name != Name.ToUpperInvariant()) yield return $"Shape name '{Name}' must be upper case.";
                if (Name.Any(c => c < 0x21 || c > 0x7E || c == ',' || c == ';' || c == '[' || c == ']' || c == '"'))
                    yield return $"Shape name '{Name}' may only contain printable ASCII characters without , ; [ ] \".";
            }
            if (Bytes == null || Bytes.Length == 0 || Bytes[Bytes.Length - 1] != 0) yield return "Specification must end with 0.";
            else if (Bytes.Length > MaxBytes) yield return $"Specification is {Bytes.Length} bytes (limit {MaxBytes}).";
        }
    }
}
