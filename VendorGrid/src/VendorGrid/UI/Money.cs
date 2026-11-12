using System.Globalization;
using VendorGrid.Core;

namespace VendorGrid.UI
{
    public static class Money
    {
        /// <summary>Unit prices: 4 decimals below 1.00, otherwise 2.</summary>
        public static string Format(decimal value, string currency) { return Render(value, currency, value < 1m ? "#,##0.0000" : "#,##0.00"); }

        /// <summary>Order totals: always 2 decimals.</summary>
        public static string Total(decimal value, string currency) { return Render(value, currency, "#,##0.00"); }

        static string Render(decimal value, string currency, string fmt)
        {
            return Symbol(currency) + value.ToString(fmt, Loc.Culture);
        }

        public static string Symbol(string currency)
        {
            switch ((currency ?? "").ToUpperInvariant())
            {
                case "USD": return "$";
                case "EUR": return "€";
                case "GBP": return "£";
                case "JPY": return "¥";
                case "CAD": return "CA$";
                case "AUD": return "A$";
                case "NZD": return "NZ$";
                case "CNY": return "CN¥";
                case "INR": return "₹";
                case "KRW": return "₩";
                default: return (currency ?? "") + " ";
            }
        }

        public static string Count(long n) { return n.ToString("#,##0", Loc.Culture); }
    }
}
