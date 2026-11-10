using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VendorGrid.Core
{
    public interface IVendorProvider
    {
        string Id { get; }
        string Name { get; }
        /// <summary>True for websites without a data API: the card only offers a link to the vendor's search page.</summary>
        bool IsLinkOnly { get; }
        /// <summary>The vendor's own website search page for this part number (always available).</summary>
        string BuildSearchUrl(string partNumber);
        /// <summary>False when credentials/URL are missing; reason is shown on the vendor row.</summary>
        bool IsConfigured(out string reason);
        Task<List<PartMatch>> SearchAsync(string partNumber, CancellationToken ct);
    }

    public static class MatchFilter
    {
        public static string Norm(string s) { return Regex.Replace(s ?? "", "[^A-Za-z0-9]", "").ToUpperInvariant(); }

        /// <summary>
        /// Prefer listings whose MPN (or vendor SKU) equals the query. When none match exactly the closest results are
        /// kept and flagged as alternates so the UI never presents a different part as the requested one.
        /// </summary>
        public static List<PartMatch> Prefer(string query, List<PartMatch> all, int max = 6)
        {
            var q = Norm(query);
            var exact = all.Where(m => Norm(m.Mpn) == q || Norm(m.Sku) == q).ToList();
            var chosen = (exact.Count > 0 ? exact : all).Take(max).ToList();
            if (exact.Count == 0) foreach (var m in chosen) m.IsAlternate = true;
            return chosen;
        }

        /// <summary>Extracts a day count from strings such as "112 Days", "12 Weeks" or "20" (weeks when unit is missing and assumeWeeks).</summary>
        public static int? ParseLeadDays(string text, bool assumeWeeks)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var m = Regex.Match(text, @"(\d+(?:\.\d+)?)");
            if (!m.Success) return null;
            double n;
            if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out n)) return null;
            var t = text.ToLowerInvariant();
            if (t.Contains("week")) n *= 7;
            else if (t.Contains("month")) n *= 30;
            else if (!t.Contains("day") && assumeWeeks) n *= 7;
            return (int)System.Math.Round(n);
        }
    }
}
