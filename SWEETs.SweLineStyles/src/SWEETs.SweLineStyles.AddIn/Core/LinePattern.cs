using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    public enum PatternKind { Dash, Gap, Dot, Shape, Text }

    /// <summary>
    /// One element of a line-style pattern, as written in linestyle.xlin (AutoCAD linetype syntax):
    /// a length (positive = dash, negative = gap, 0 = dot), an embedded shape
    /// <c>[CIRC1,ltypeshp.shx,x=-.1,s=.1]</c> or embedded text <c>["GAS",STANDARD,S=.1,R=0.0,X=-0.1,Y=-.05]</c>.
    /// Shapes and text take no length of their own: they sit at the current position along the pattern.
    /// </summary>
    public sealed class PatternElement
    {
        public PatternKind Kind { get; set; }
        /// <summary>Dash/gap length (always ≥ 0; the sign comes from <see cref="Kind"/>).</summary>
        public double Length { get; set; }
        /// <summary>Shape name or text string.</summary>
        public string Content { get; set; } = "";
        /// <summary>Shape file (e.g. ltypeshp.shx) or text style (e.g. STANDARD).</summary>
        public string Source { get; set; } = "";
        /// <summary>Transform parameters in their original order and key case (S, R, A, X, Y, U ...).</summary>
        public List<PatternParam> Params { get; } = new List<PatternParam>();

        public bool IsComplex => Kind == PatternKind.Shape || Kind == PatternKind.Text;
        /// <summary>Signed length as stored in the pattern (gap negative). 0 for dots and complex elements.</summary>
        public double SignedLength => Kind == PatternKind.Dash ? Length : Kind == PatternKind.Gap ? -Length : 0;

        public double? Get(string key) => Params.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase))?.Value;

        public void Set(string key, double? value)
        {
            var p = Params.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
            if (value == null) { if (p != null) Params.Remove(p); return; }
            if (p != null) p.Value = value.Value;
            else Params.Add(new PatternParam(key.ToUpperInvariant(), value.Value));
        }

        public double Scale => Get("S") ?? 1;
        public double X => Get("X") ?? 0;
        public double Y => Get("Y") ?? 0;
        /// <summary>Rotation in degrees: A = absolute (to the X axis), R = relative to the line (default).</summary>
        public double Rotation => Get("A") ?? Get("R") ?? 0;
        public bool RotationAbsolute => Get("A") != null;

        public PatternElement Clone()
        {
            var e = new PatternElement { Kind = Kind, Length = Length, Content = Content, Source = Source };
            e.Params.AddRange(Params.Select(p => new PatternParam(p.Key, p.Value)));
            return e;
        }

        public static PatternElement Dash(double len) => new PatternElement { Kind = PatternKind.Dash, Length = Math.Abs(len) };
        public static PatternElement Gap(double len) => new PatternElement { Kind = PatternKind.Gap, Length = Math.Abs(len) };
        public static PatternElement Dot() => new PatternElement { Kind = PatternKind.Dot };
    }

    public sealed class PatternParam
    {
        public string Key { get; set; }
        public double Value { get; set; }
        public PatternParam(string key, double value) { Key = key; Value = value; }
    }

    public static class LinePattern
    {
        // ------------------------------------------------------------------ parse

        public static List<PatternElement> Parse(string text)
        {
            var result = new List<PatternElement>();
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException("The pattern is empty.");
            foreach (var tok in Split(text, ',', topLevelOnly: true))
            {
                var t = tok.Trim();
                if (t.Length == 0) throw new FormatException("The pattern has an empty element (two commas in a row).");
                if (t[0] == '[')
                {
                    if (t[t.Length - 1] != ']') throw new FormatException($"'{t}': missing ].");
                    result.Add(ParseComplex(t.Substring(1, t.Length - 2)));
                    continue;
                }
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new FormatException($"'{t}' is not a length.");
                result.Add(v > 0 ? PatternElement.Dash(v) : v < 0 ? PatternElement.Gap(v) : PatternElement.Dot());
            }
            return result;
        }

        private static PatternElement ParseComplex(string body)
        {
            var parts = Split(body, ',', topLevelOnly: false).Select(p => p.Trim()).ToList();
            if (parts.Count < 2) throw new FormatException($"[{body}]: expected [name,file,...] or [\"text\",style,...].");
            var e = new PatternElement();
            if (parts[0].StartsWith("\"", StringComparison.Ordinal))
            {
                if (parts[0].Length < 2 || !parts[0].EndsWith("\"", StringComparison.Ordinal)) throw new FormatException($"[{body}]: unterminated text.");
                e.Kind = PatternKind.Text;
                e.Content = parts[0].Substring(1, parts[0].Length - 2);
            }
            else
            {
                e.Kind = PatternKind.Shape;
                e.Content = parts[0];
            }
            e.Source = parts[1];
            foreach (var p in parts.Skip(2))
            {
                int eq = p.IndexOf('=');
                if (eq <= 0) throw new FormatException($"[{body}]: '{p}' should be KEY=value.");
                var key = p.Substring(0, eq).Trim();
                if (!double.TryParse(p.Substring(eq + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    throw new FormatException($"[{body}]: '{p}' has no numeric value.");
                e.Params.Add(new PatternParam(key, v));
            }
            return e;
        }

        /// <summary>Splits on commas that are outside [...] (top level) and outside "...".</summary>
        private static IEnumerable<string> Split(string s, char sep, bool topLevelOnly)
        {
            int depth = 0; bool quoted = false; int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') quoted = !quoted;
                else if (!quoted && c == '[') depth++;
                else if (!quoted && c == ']') depth--;
                else if (!quoted && c == sep && (!topLevelOnly || depth == 0))
                {
                    yield return s.Substring(start, i - start);
                    start = i + 1;
                }
            }
            if (quoted) throw new FormatException("A text element has no closing quote.");
            if (depth != 0) throw new FormatException("Brackets [ ] are not balanced.");
            yield return s.Substring(start);
        }

        // ------------------------------------------------------------------ write

        public static string Format(IEnumerable<PatternElement> elements)
        {
            var parts = new List<string>();
            foreach (var e in elements)
            {
                switch (e.Kind)
                {
                    case PatternKind.Dash: parts.Add(Num(e.Length)); break;
                    case PatternKind.Gap: parts.Add(Num(-e.Length)); break;
                    case PatternKind.Dot: parts.Add("0"); break;
                    default:
                        var sb = new StringBuilder("[");
                        sb.Append(e.Kind == PatternKind.Text ? "\"" + e.Content + "\"" : e.Content);
                        sb.Append(',').Append(e.Source);
                        foreach (var p in e.Params) sb.Append(',').Append(p.Key).Append('=').Append(Num(p.Value));
                        parts.Add(sb.Append(']').ToString());
                        break;
                }
            }
            return string.Join(",", parts);
        }

        /// <summary>Invariant number in the file's own style: no trailing zeros, no leading zero (.5, -.25).</summary>
        public static string Num(double v)
        {
            var s = Math.Round(v, 6).ToString("0.######", CultureInfo.InvariantCulture);
            if (s.StartsWith("0.", StringComparison.Ordinal)) s = s.Substring(1);
            else if (s.StartsWith("-0.", StringComparison.Ordinal)) s = "-" + s.Substring(2);
            return s == "-0" ? "0" : s;
        }

        // ------------------------------------------------------------------ analysis

        public static double Period(IEnumerable<PatternElement> elements) => elements.Sum(e => e.IsComplex ? 0 : e.Length);

        /// <summary>Rule violations that would make the line style unusable (errors) or unusual (warnings).</summary>
        public static void Check(IList<PatternElement> elements, Func<string, bool> shapeExists, List<string> errors, List<string> warnings)
        {
            if (elements.Count == 0) { errors.Add("The pattern has no elements."); return; }
            if (elements[0].IsComplex) errors.Add("The pattern must start with a dash, gap or dot, not a shape or text (use a tiny dash such as .0001).");
            if (Period(elements) <= 0) errors.Add("The pattern has no length: add at least one dash or gap.");
            int lengths = elements.Count(e => !e.IsComplex);
            if (lengths > 12) warnings.Add($"{lengths} dash/gap/dot values: AutoCAD-style line types allow 12 per pattern.");
            foreach (var e in elements.Where(x => x.IsComplex))
            {
                if (string.IsNullOrWhiteSpace(e.Content)) errors.Add(e.Kind == PatternKind.Shape ? "A shape element has no shape name." : "A text element has no text.");
                if (string.IsNullOrWhiteSpace(e.Source)) errors.Add(e.Kind == PatternKind.Shape ? "A shape element has no shape file." : "A text element has no text style.");
                if (e.Kind == PatternKind.Shape && !string.IsNullOrWhiteSpace(e.Content) && shapeExists != null && !shapeExists(e.Content))
                    errors.Add($"Shape '{e.Content}' does not exist in the shape file.");
                if (e.Kind == PatternKind.Text && (e.Content.Contains("\"") || e.Content.Contains(",") || e.Content.Contains("]")))
                    errors.Add($"Text '{e.Content}' cannot contain \" , or ].");
                if (e.Get("S") is double s && s <= 0) errors.Add($"Scale S={Num(s)} must be greater than 0.");
            }
        }

        public static string Classify(IEnumerable<PatternElement> elements)
        {
            var list = elements.ToList();
            if (list.Any(e => e.Kind == PatternKind.Shape)) return "Shape";
            if (list.Any(e => e.Kind == PatternKind.Text)) return "Text";
            return "Simple";
        }

        /// <summary>Approximate character rendering for the &lt;ascii&gt; field (about 40 characters).</summary>
        public static string ToAscii(IList<PatternElement> elements, int width = 40)
        {
            double period = Period(elements);
            if (period <= 0) return "";
            // Characters per unit: the shortest non-zero length gets at least one character.
            double shortest = elements.Where(e => !e.IsComplex && e.Length > 0).Select(e => e.Length).DefaultIfEmpty(period).Min();
            double perUnit = Math.Max(1 / shortest, 8 / period);
            var sb = new StringBuilder();
            while (sb.Length < width)
                foreach (var e in elements)
                {
                    int n = Math.Max(1, (int)Math.Round(e.Length * perUnit));
                    switch (e.Kind)
                    {
                        case PatternKind.Dash: sb.Append('_', n); break;
                        case PatternKind.Gap: sb.Append(' ', n); break;
                        case PatternKind.Dot: sb.Append('.'); break;
                        case PatternKind.Text: sb.Append(e.Content); break;
                        case PatternKind.Shape: sb.Append('o'); break;
                    }
                }
            return sb.ToString(0, Math.Min(sb.Length, width + 8)).TrimEnd();
        }
    }
}
