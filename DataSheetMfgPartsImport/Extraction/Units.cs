using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DataSheetMfgPartsImport.Extraction;

/// <summary>Number parsing and unit conversion. SOLIDWORKS Electrical stores sizes in mm and weight in grams.</summary>
public static class Units
{
    public static bool TryParseNumber(string s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().Replace(" ", "");
        // "1.234,5" -> European thousands + decimal comma; "0,32" -> decimal comma.
        if (s.Contains(",") && s.Contains("."))
            s = s.LastIndexOf(',') > s.LastIndexOf('.') ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        else if (s.Contains(","))
            s = s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static double? ToMillimeters(string number, string unit)
    {
        if (!TryParseNumber(number, out var v)) return null;
        switch ((unit ?? "mm").Trim().ToLowerInvariant())
        {
            case "mm": return v;
            case "cm": return v * 10;
            case "m": return v * 1000;
            case "in":
            case "inch":
            case "inches":
            case "\"":
            case "″": return v * 25.4;
            default: return v;
        }
    }

    public static double? ToGrams(string number, string unit)
    {
        if (!TryParseNumber(number, out var v)) return null;
        switch ((unit ?? "g").Trim().ToLowerInvariant())
        {
            case "g": return v;
            case "kg": return v * 1000;
            case "lb":
            case "lbs": return v * 453.59237;
            case "oz": return v * 28.349523125;
            default: return v;
        }
    }

    /// <summary>Formats a number for SOLIDWORKS Electrical (invariant culture, max 2 decimals).</summary>
    public static string Format(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>Normalises "24 V DC", "24VDC", "24 V =" to "24 V DC"; ranges stay as written.</summary>
    public static string NormalizeVoltage(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var s = Regex.Replace(raw.Trim(), @"\s+", " ");
        s = Regex.Replace(s, @"(?i)(\d)\s*(k?V)\s*(AC/DC|AC|DC|UC)\b", m => $"{m.Groups[1].Value} {m.Groups[2].Value} {m.Groups[3].Value.ToUpperInvariant()}");
        s = Regex.Replace(s, @"(?i)(\d)\s*(k?V)\b(?!\s*(?:AC|DC|UC))", "$1 $2");
        s = Regex.Replace(s, @"(\d)\s*(k?V)\s*[=⎓]", "$1 $2 DC");
        s = Regex.Replace(s, @"(\d)\s*(k?V)\s*~", "$1 $2 AC");
        return s.Trim();
    }
}
