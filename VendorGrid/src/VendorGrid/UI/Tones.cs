using System.Windows.Media;

namespace VendorGrid.UI
{
    /// <summary>Foreground/background pairs for status chips. Text always accompanies colour (never colour alone).</summary>
    public sealed class Tone
    {
        public Brush Back { get; private set; }
        public Brush Fore { get; private set; }
        /// <summary>Shape that always accompanies the colour, so meaning never depends on colour alone.</summary>
        public string Symbol { get; private set; }
        Tone(string back, string fore, string symbol) { Back = Make(back); Fore = Make(fore); Symbol = symbol; }

        public static Brush Make(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        // Colour-blind-safe set (teal / amber / violet / blue / rose) with a different shape for each. Avoids red-vs-green.
        public static readonly Tone Good = new Tone("#CFFAFE", "#155E75", "●");      // teal, filled circle: ready, best, good
        public static readonly Tone Warn = new Tone("#FEF3C7", "#78350F", "◐");      // dark amber, half circle: partial, worth a look
        public static readonly Tone Unsure = new Tone("#EDE9FE", "#5B21B6", "▲");    // violet, triangle: alternate, unknown, check it
        public static readonly Tone Bad = new Tone("#FCE7F3", "#9D174D", "✕");       // rose, cross: out of stock, error
        public static readonly Tone Info = new Tone("#DBEAFE", "#1E3A8A", "◆");      // blue, diamond: information, link
        public static readonly Tone Neutral = new Tone("#E5E7EB", "#1F2937", "○");   // grey, hollow circle: nothing to report
    }

    /// <summary>Brand-ish colours for the small vendor badges and the chart series.</summary>
    public static class Palette
    {
        static readonly string[] Series =
        {
            // Okabe-Ito colour-blind-safe palette. The chart also varies marker shape and line dash per series.
            "#0072B2", "#D55E00", "#009E73", "#CC79A7", "#56B4E9", "#E69F00", "#7C3AED", "#4B5563", "#0E7490", "#92400E"
        };

        public static Brush ForIndex(int i) { return Tone.Make(Series[((i % Series.Length) + Series.Length) % Series.Length]); }

        /// <summary>Stable colour per vendor id; well-known distributors keep their familiar colour.</summary>
        public static Brush ForVendor(string id)
        {
            switch ((id ?? "").ToLowerInvariant())
            {
                case "digikey": return Tone.Make("#CC0000");
                case "mouser": return Tone.Make("#0B5CAD");
                case "newark": return Tone.Make("#E87722");
                case "arrow": return Tone.Make("#E4572E");
                case "rs": return Tone.Make("#D4002A");
                case "te": return Tone.Make("#F28D00");
                case "automationdirect": return Tone.Make("#00539B");
            }
            int h = 17;
            foreach (var c in id ?? "") h = unchecked(h * 31 + c);
            return Tone.Make(Series[(h & 0x7fffffff) % Series.Length]);
        }
    }
}
