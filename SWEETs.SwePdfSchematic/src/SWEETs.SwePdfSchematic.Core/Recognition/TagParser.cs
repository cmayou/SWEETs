using System.Text.RegularExpressions;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    public enum TextRole { Other, DeviceTag, CrossReference, WireNumber, Label }

    public sealed class ParsedTag
    {
        public string Raw;
        public SymbolStandard Standard;
        public string Prefix;   // "K", "CR", "PB" ...
        public string Number;
        public string Function; // IEC "=A1" aspect, if present
        public string Location; // IEC "+B2" aspect, if present
        /// <summary>SOLIDWORKS Electrical default mark: sheet number + letter code + counter ("04Q1", "05PB1").</summary>
        public string Sheet;
        public bool IsSheetMark => !string.IsNullOrEmpty(Sheet);
        /// <summary>Bare letter code + number ("X1", "N1"): also how pin labels look, so only used when no pin claims it.</summary>
        public bool IsWeak;
    }

    /// <summary>Regex-based classification of schematic text. Patterns are deliberately conservative.</summary>
    public static class TagParser
    {
        // IEC 81346: optional =function and +location aspects, then -product aspect: -K1, =A1+B2-Q12, -X1A
        private static readonly Regex IecTag = new Regex(
            @"^(?<fn>=[A-Z0-9.]+)?(?<loc>\+[A-Z0-9.]+)?-(?<pre>[A-Z]{1,3})(?<num>\d{1,4}[A-Z]?)$",
            RegexOptions.Compiled);

        // NFPA 79 / JIC style device tags: CR1, M2, PB10, LS3A, 1CR (leading-number variant)
        private static readonly Regex AnsiTag = new Regex(
            @"^(?:(?<lead>\d{1,3})(?<pre>CR|M|PB|SS|LS|PS|TS|FS|FU|CB|OL|TR|TD|SOL|SV|LT|PL|MTR|TB|DS|T|R|K)|(?<pre>CR|M|PB|SS|LS|PS|TS|FS|FU|CB|OL|TR|TD|SOL|SV|LT|PL|MTR|TB|DS|T|R|K)(?<num>\d{1,4}[A-Z]?))$",
            RegexOptions.Compiled);

        // Cross refs: "/2.3" "2.3" "12-4" "/4-12" (sheet.column / sheet-column)
        private static readonly Regex Xref = new Regex(@"^/?(?<page>\d{1,3})[.\-](?<col>\d{1,2})$", RegexOptions.Compiled);

        // ANSI ladder rung references under coils: "12" "12,15" handled by caller context
        private static readonly Regex RungList = new Regex(@"^\d{1,4}(,\d{1,4})*$", RegexOptions.Compiled);

        // SOLIDWORKS Electrical default marks: <sheet><letter code><counter>: 04Q1, 05PB1, 04PS1, 12KA3
        private static readonly Regex SweMark = new Regex(@"^(?<sheet>\d{1,3})(?<pre>[A-Z]{1,3})(?<num>\d{1,4}[A-Z]?)$", RegexOptions.Compiled);

        private static readonly Regex BareTag = new Regex(@"^(?<pre>[A-Z]{1,2})(?<num>\d{1,3})$", RegexOptions.Compiled);

        private static readonly Regex WireNo = new Regex(@"^[A-Z]?\d{1,5}[A-Z]?$", RegexOptions.Compiled);
        // Potential / equipotential numbering: L1-3, G-9, GND-1, N-12, 24V-2, PE-1
        private static readonly Regex PotentialNo = new Regex(@"^(?:[A-Z][A-Z0-9]{0,3}|\d{1,3}V)-\d{1,4}[A-Z]?$", RegexOptions.Compiled);

        public static ParsedTag ParseDeviceTag(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var t = text.Trim().ToUpperInvariant();

            var m = IecTag.Match(t);
            if (m.Success)
                return new ParsedTag
                {
                    Raw = text.Trim(), Standard = SymbolStandard.IEC, Prefix = m.Groups["pre"].Value,
                    Number = m.Groups["num"].Value, Function = m.Groups["fn"].Value, Location = m.Groups["loc"].Value,
                };

            m = SweMark.Match(t);
            if (m.Success)
                return new ParsedTag
                {
                    Raw = text.Trim(), Standard = SymbolStandard.Unknown, Prefix = m.Groups["pre"].Value,
                    Number = m.Groups["num"].Value, Sheet = m.Groups["sheet"].Value,
                };

            m = AnsiTag.Match(t);
            if (m.Success)
                return new ParsedTag
                {
                    Raw = text.Trim(), Standard = SymbolStandard.ANSI, Prefix = m.Groups["pre"].Value,
                    Number = m.Groups["num"].Success && m.Groups["num"].Length > 0 ? m.Groups["num"].Value : m.Groups["lead"].Value,
                };

            m = BareTag.Match(t);
            if (m.Success)
                return new ParsedTag
                {
                    Raw = text.Trim(), Standard = SymbolStandard.Unknown, Prefix = m.Groups["pre"].Value,
                    Number = m.Groups["num"].Value, IsWeak = true,
                };
            return null;
        }

        public static bool TryParseXref(string text, out int page, out int column)
        {
            page = column = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var m = Xref.Match(text.Trim());
            if (!m.Success) return false;
            page = int.Parse(m.Groups["page"].Value);
            column = int.Parse(m.Groups["col"].Value);
            return true;
        }

        // Terminal designations printed beside pins: line/load poles, motor windings, coil and lamp terminals, supply pins.
        private static readonly Regex PinName = new Regex(@"^(?:L[1-3]|T[1-6]|[UVW][12]?|N|PE|A[12]|H[12]|GND|0V|\+?\d{1,2}V?)$", RegexOptions.Compiled);

        /// <summary>
        /// True for conventional pin designations ("L1", "T3", "A1", "U", "PE", "+24"). These can also parse as device
        /// tags (T3 = transformer 3), so they only name a device when no pin claims them.
        /// </summary>
        public static bool IsConventionalPinName(string text) => !string.IsNullOrWhiteSpace(text) && PinName.IsMatch(text.Trim().ToUpperInvariant());

        public static bool IsRungList(string text) => !string.IsNullOrEmpty(text) && RungList.IsMatch(text.Trim());

        public static bool LooksLikeWireNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var t = text.Trim().ToUpperInvariant();
            return WireNo.IsMatch(t) || (PotentialNo.IsMatch(t) && !Xref.IsMatch(t));
        }

        public static TextRole Classify(string text)
        {
            if (ParseDeviceTag(text) != null) return TextRole.DeviceTag;
            if (TryParseXref(text, out _, out _)) return TextRole.CrossReference;
            if (LooksLikeWireNumber(text)) return TextRole.WireNumber;
            return TextRole.Label;
        }
    }
}
