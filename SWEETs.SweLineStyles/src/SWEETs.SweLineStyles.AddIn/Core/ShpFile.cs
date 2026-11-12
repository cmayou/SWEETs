using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    /// <summary>
    /// Shape-file source (*.shp). Reads and writes the AutoCAD shape-definition syntax:
    /// <code>
    /// *130,6,TRACK1
    /// 014,002,01C,001,01C,0
    /// </code>
    /// Values with a leading 0 are hexadecimal (014 = 0x14), others decimal. Parentheses are cosmetic.
    /// A negative value is a two's-complement byte, except the octant specification of codes 10 and 11 where
    /// the sign sets bit 7 (-044 → 0xC4) — this matches SOLIDWORKS Electrical's own ltypeshp.shx byte for byte.
    ///
    /// Unchanged shapes are written back verbatim (comments and layout kept); edited or new shapes are generated.
    /// </summary>
    public sealed class ShpFile
    {
        public string Header { get; set; } = "";
        public List<ShapeDefinition> Shapes { get; } = new List<ShapeDefinition>();
        public List<string> Warnings { get; } = new List<string>();

        // ------------------------------------------------------------------ read

        public static ShpFile Parse(string text)
        {
            var file = new ShpFile();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            int first = Array.FindIndex(lines, l => l.TrimStart().StartsWith("*", StringComparison.Ordinal));
            if (first < 0) { file.Header = text ?? ""; return file; }

            // Header and blocks are taken from the raw text so they round-trip exactly.
            var rawLines = SplitKeepEndings(text);
            file.Header = string.Concat(rawLines.Take(first));

            int i = first;
            while (i < lines.Length)
            {
                int next = i + 1;
                while (next < lines.Length && !lines[next].TrimStart().StartsWith("*", StringComparison.Ordinal)) next++;
                var block = string.Concat(rawLines.Skip(i).Take(next - i));
                try { file.Shapes.Add(ParseBlock(block, i + 1, file.Warnings)); }
                catch (FormatException ex) { throw new FormatException($"Line {i + 1}: {ex.Message}", ex); }
                i = next;
            }
            return file;
        }

        private static List<string> SplitKeepEndings(string text)
        {
            var result = new List<string>();
            int start = 0;
            for (int k = 0; k < text.Length; k++)
                if (text[k] == '\n') { result.Add(text.Substring(start, k - start + 1)); start = k + 1; }
            if (start < text.Length) result.Add(text.Substring(start));
            return result;
        }

        private static ShapeDefinition ParseBlock(string block, int lineNo, List<string> warnings)
        {
            var lines = block.Replace("\r\n", "\n").Split('\n').Select(StripComment).ToList();
            var head = lines[0].Trim().Substring(1).Split(new[] { ',' }, 3);
            if (head.Length < 3) throw new FormatException("expected *number,bytes,NAME");
            int number = ParseInt(head[0].Trim());
            int declared = ParseInt(head[1].Trim());
            string name = head[2].Trim();

            var tokens = new List<string>();
            foreach (var l in lines.Skip(1))
                foreach (var t in l.Replace("(", " ").Replace(")", " ").Split(','))
                    if (t.Trim().Length > 0) tokens.Add(t.Trim());

            var bytes = Encode(tokens);
            if (bytes.Length != declared)
                warnings.Add($"Shape {number} {name} (line {lineNo}): header says {declared} bytes, specification has {bytes.Length}.");
            return new ShapeDefinition { Number = number, Name = name, Bytes = bytes, SourceText = block };
        }

        private static string StripComment(string line)
        {
            int c = line.IndexOf(';');
            return c >= 0 ? line.Substring(0, c) : line;
        }

        private static int ParseInt(string s) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : throw new FormatException($"'{s}' is not a number");

        /// <summary>Parses one value: leading 0 = hexadecimal. Returns sign and magnitude.</summary>
        private static (bool neg, int mag) Value(string tok)
        {
            bool neg = tok.StartsWith("-", StringComparison.Ordinal);
            var t = tok.TrimStart('+', '-');
            int v;
            bool ok = t.Length > 1 && t[0] == '0'
                ? int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)
                : int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
            if (!ok) throw new FormatException($"'{tok}' is not a valid shape value");
            if (v > 255) throw new FormatException($"'{tok}' does not fit in one byte");
            return (neg, v);
        }

        private static byte Tc((bool neg, int mag) v) => (byte)((v.neg ? -v.mag : v.mag) & 0xFF);
        private static byte Sm((bool neg, int mag) v) => (byte)(v.neg ? (v.mag | 0x80) : v.mag);

        /// <summary>Converts specification tokens to bytes, walking the command structure.</summary>
        public static byte[] Encode(IList<string> tokens)
        {
            var vals = tokens.Select(Value).ToList();
            var o = new List<byte>();
            int i = 0;
            (bool, int) Next()
            {
                if (i >= vals.Count) throw new FormatException("specification ends in the middle of a command");
                return vals[i++];
            }
            while (i < vals.Count)
            {
                var cmd = Next();
                int c = cmd.Item2;
                o.Add(Tc(cmd));
                if (cmd.Item1 && c != 0) throw new FormatException($"command code -{c} cannot be negative");
                switch (c)
                {
                    case 0: return FinishAt(o, i, vals.Count);
                    case 3: case 4: case 7: o.Add(Tc(Next())); break;
                    case 8: o.Add(Tc(Next())); o.Add(Tc(Next())); break;
                    case 9:
                        while (true)
                        {
                            var a = Next(); var b = Next();
                            o.Add(Tc(a)); o.Add(Tc(b));
                            if (a.Item2 == 0 && b.Item2 == 0) break;
                        }
                        break;
                    case 10: o.Add(Tc(Next())); o.Add(Sm(Next())); break;
                    case 11: for (int k = 0; k < 4; k++) o.Add(Tc(Next())); o.Add(Sm(Next())); break;
                    case 12: for (int k = 0; k < 3; k++) o.Add(Tc(Next())); break;
                    case 13:
                        while (true)
                        {
                            var a = Next(); var b = Next();
                            o.Add(Tc(a)); o.Add(Tc(b));
                            if (a.Item2 == 0 && b.Item2 == 0) break;
                            o.Add(Tc(Next()));
                        }
                        break;
                }
            }
            throw new FormatException("specification does not end with 0");
        }

        private static byte[] FinishAt(List<byte> o, int consumed, int total)
        {
            if (consumed != total) throw new FormatException($"{total - consumed} value(s) after the terminating 0");
            return o.ToArray();
        }

        // ------------------------------------------------------------------ write

        public string ToText()
        {
            var sb = new StringBuilder(Header);
            foreach (var s in Shapes)
            {
                // Verbatim blocks already carry their own separators; only a generated block (or a block after one
                // that lacks a final line break) needs one.
                if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append("\r\n");
                if (s.SourceText == null && sb.Length > 0 && !EndsWithBlankLine(sb)) sb.Append("\r\n");
                sb.Append(s.SourceText ?? Generate(s));
            }
            return sb.ToString();
        }

        private static bool EndsWithBlankLine(StringBuilder sb)
        {
            var t = sb.ToString();
            return t.EndsWith("\n\n", StringComparison.Ordinal) || t.EndsWith("\n\r\n", StringComparison.Ordinal);
        }

        /// <summary>Source text for a shape whose bytes were produced by the editor or the DXF import.</summary>
        public static string Generate(ShapeDefinition s)
        {
            var sb = new StringBuilder();
            sb.Append('*').Append(s.Number.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(s.Bytes.Length.ToString(CultureInfo.InvariantCulture)).Append(',').Append(s.Name).Append("\r\n");
            var groups = FormatGroups(s.Bytes);
            int col = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                var piece = groups[g] + (g < groups.Count - 1 ? "," : "");
                if (col > 0 && col + piece.Length > 76) { sb.Append("\r\n"); col = 0; }
                sb.Append(piece);
                col += piece.Length;
            }
            sb.Append("\r\n\r\n");
            return sb.ToString();
        }

        /// <summary>Human-readable specification groups, one per command (e.g. "8,(-3,7)", "014", "0").</summary>
        public static List<string> FormatGroups(byte[] b)
        {
            var g = new List<string>();
            int i = 0;
            string S(int k) => ((sbyte)b[k]).ToString(CultureInfo.InvariantCulture);
            string U(int k) => b[k].ToString(CultureInfo.InvariantCulture);
            string Oct(int k) => ((b[k] & 0x80) != 0 ? "-" : "") + "0" + (b[k] & 0x7F).ToString("X2", CultureInfo.InvariantCulture);
            bool Has(int n) => i + n <= b.Length;
            while (i < b.Length)
            {
                int c = b[i];
                int need = c == 3 || c == 4 || c == 7 ? 2 : c == 8 || c == 10 ? 3 : c == 12 ? 4 : c == 11 ? 6 : 1;
                if (!Has(need))
                {
                    // Truncated command (malformed input): show the remaining bytes as they are.
                    for (; i < b.Length; i++) g.Add(U(i));
                    break;
                }
                switch (c)
                {
                    case 0: g.Add("0"); i++; break;
                    case 1: case 2: case 5: case 6: case 14: g.Add(U(i)); i++; break;
                    case 3: case 4: case 7: g.Add($"{c},{U(i + 1)}"); i += 2; break;
                    case 8: g.Add($"8,({S(i + 1)},{S(i + 2)})"); i += 3; break;
                    case 9:
                    case 13:
                    {
                        var parts = new List<string> { c.ToString(CultureInfo.InvariantCulture) };
                        i++;
                        while (i + 1 < b.Length)
                        {
                            bool end = b[i] == 0 && b[i + 1] == 0;
                            if (c == 13 && !end && i + 2 < b.Length) { parts.Add($"({S(i)},{S(i + 1)},{S(i + 2)})"); i += 3; }
                            else { parts.Add($"({S(i)},{S(i + 1)})"); i += 2; }
                            if (end) break;
                        }
                        g.Add(string.Join(",", parts));
                        break;
                    }
                    case 10: g.Add($"10,({U(i + 1)},{Oct(i + 2)})"); i += 3; break;
                    case 11: g.Add($"11,({U(i + 1)},{U(i + 2)},{U(i + 3)},{U(i + 4)},{Oct(i + 5)})"); i += 6; break;
                    case 12: g.Add($"12,({S(i + 1)},{S(i + 2)},{S(i + 3)})"); i += 4; break;
                    default:
                        // Vector byte: length (high nibble) and direction (low nibble), written as 3-digit hex.
                        g.Add("0" + b[i].ToString("X2", CultureInfo.InvariantCulture)); i++; break;
                }
            }
            return g;
        }
    }
}
