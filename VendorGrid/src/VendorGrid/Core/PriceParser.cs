using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace VendorGrid.Core
{
    /// <summary>Parses vendor-formatted numbers such as "$1,234.50", "1.234,50 €" or "12,345 In Stock".</summary>
    public static class PriceParser
    {
        public static decimal? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var s = new string(text.Where(c => char.IsDigit(c) || c == '.' || c == ',').ToArray()).Trim('.', ',');
            if (s.Length == 0) return null;

            int lastDot = s.LastIndexOf('.'), lastComma = s.LastIndexOf(',');
            if (lastDot >= 0 && lastComma >= 0)
            {
                // The right-most separator is the decimal mark.
                if (lastComma > lastDot) s = s.Replace(".", "").Replace(',', '.');
                else s = s.Replace(",", "");
            }
            else if (lastComma >= 0)
            {
                // "1,234" => thousands; "1,23" / "0,1234" => decimal comma.
                bool thousands = s.Count(c => c == ',') > 1 || (s.Length - lastComma - 1) == 3;
                s = thousands ? s.Replace(",", "") : s.Replace(',', '.');
            }
            decimal d;
            return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out d) ? d : (decimal?)null;
        }

        public static long? ParseLong(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text, @"\d[\d,\.\s]*");
            if (!m.Success) return null;
            var digits = new string(m.Value.Where(char.IsDigit).ToArray());
            long v;
            return long.TryParse(digits, out v) ? v : (long?)null;
        }
    }
}
