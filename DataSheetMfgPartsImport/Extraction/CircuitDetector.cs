using System.Collections.Generic;
using System.Linq;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.Extraction;

/// <summary>
/// Builds the circuit/terminal list for a part.
/// 1) Explicit terminal marks in the PDF (A1-A2, 13-14, 21-22, 11-12-14, 1/L1...) are used as-is.
/// 2) Otherwise the contact configuration text ("3NO+1NC", "2 CO", "3-pole") is turned into
///    IEC 60947 / EN 50005 standard markings.
/// </summary>
internal static class CircuitDetector
{
    /// <param name="attributes">Per-part keys from catalog tables: "poles", "phases", "pins".</param>
    public static List<CircuitSpec> Detect(List<TextLine> lines, string category, bool hasControlVoltage,
        IDictionary<string, string> attributes = null)
    {
        lines ??= new List<TextLine>();
        var text = string.Join("\n", lines.Select(l => l.Text));
        var result = new List<CircuitSpec>();
        string Attr(string key) => attributes != null && attributes.TryGetValue(key, out var v) ? v : null;

        // ---------- connectors: one circuit per pin ----------
        if (int.TryParse(Attr("pins"), out var pinCount) && pinCount > 0)
        {
            for (var i = 1; i <= pinCount; i++)
                result.Add(new CircuitSpec { Kind = CircuitKind.Pin, Terminals = new() { i.ToString() }, Explicit = false, Source = $"Derived: {pinCount} circuits/positions" });
            return result;
        }

        // ---------- drives: power input and motor output ----------
        if (category == "Drive")
        {
            var single = Attr("phases") == "1";
            result.Add(new CircuitSpec
            {
                Kind = CircuitKind.PowerInput,
                Terminals = single ? new() { "R/L1", "S/L2" } : new() { "R/L1", "S/L2", "T/L3" },
                Explicit = false,
                Source = single ? "Derived: 1-phase input" : "Derived: 3-phase input",
            });
            result.Add(new CircuitSpec { Kind = CircuitKind.PowerOutput, Terminals = new() { "U/T1", "V/T2", "W/T3" }, Explicit = false, Source = "Derived: 3-phase motor output" });
            return result;
        }

        // ---------- coil ----------
        var coilExplicit = CircuitPatterns.CoilTerminals.Match(text);
        var needsCoil = category is "Contactor" or "Relay" or "Safety relay" ||
                        (hasControlVoltage && category is not ("Circuit breaker" or "Fuse" or "Terminal block" or "Power supply" or "Connector"));
        if (coilExplicit.Success || needsCoil)
        {
            result.Add(new CircuitSpec
            {
                Kind = CircuitKind.Coil,
                Terminals = new() { "A1", "A2" },
                Explicit = coilExplicit.Success,
                Source = coilExplicit.Success ? "Marks A1/A2 found in PDF" : "Derived: coil-operated device",
            });
        }

        // ---------- main poles ----------
        var poles = int.TryParse(Attr("poles"), out var tablePoles) && tablePoles is >= 1 and <= 4
            ? tablePoles                       // from the part's own table row / caption
            : CountPoles(text, category);
        var explicitPoles = CircuitPatterns.MainPoleTerminals.Matches(text).Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups["l"].Value).Distinct().Count();
        if (explicitPoles > 0 && Attr("poles") == null) poles = System.Math.Max(poles, explicitPoles);
        var lt = category is "Contactor" or "Motor starter" or "Overload relay" or "Disconnect switch";
        for (var i = 0; i < poles; i++)
        {
            var a = (2 * i + 1).ToString();
            var b = (2 * i + 2).ToString();
            result.Add(new CircuitSpec
            {
                Kind = CircuitKind.MainPole,
                Terminals = lt ? new() { $"{a}/L{i + 1}", $"{b}/T{i + 1}" } : new() { a, b },
                Explicit = explicitPoles > 0,
                Source = explicitPoles > 0 ? "Marks n/Lx found in PDF" : $"Derived: {poles}-pole",
            });
        }

        // ---------- auxiliary / relay contacts ----------
        var explicitAux = FindExplicitAux(lines);
        if (explicitAux.Count > 0)
        {
            result.AddRange(explicitAux);
        }
        else
        {
            var (no, nc, co) = CountContacts(text, category);
            var seq = 1;
            for (var i = 0; i < co; i++, seq++)
                result.Add(Derived(CircuitKind.Changeover, $"{seq}1", $"{seq}2", $"{seq}4", $"Derived: {co} CO"));
            for (var i = 0; i < no; i++, seq++)
                result.Add(Derived(CircuitKind.AuxNO, $"{seq}3", $"{seq}4", null, $"Derived: {no} NO"));
            for (var i = 0; i < nc; i++, seq++)
                result.Add(Derived(CircuitKind.AuxNC, $"{seq}1", $"{seq}2", null, $"Derived: {nc} NC"));
        }

        return result;
    }

    private static string Src(TextLine line) => $"p{line.Page}: {line.Text}";

    private static CircuitSpec Derived(CircuitKind k, string a, string b, string c, string src)
    {
        var t = new List<string> { a, b };
        if (c != null) t.Add(c);
        return new CircuitSpec { Kind = k, Terminals = t, Explicit = false, Source = src };
    }

    private static int CountPoles(string text, string category)
    {
        var counts = CircuitPatterns.PoleCount.Matches(text).Cast<System.Text.RegularExpressions.Match>()
            .Select(m => int.TryParse(m.Groups["n"].Value, out var n) ? n : 0)
            .Where(n => n is >= 1 and <= 4)
            .ToList();
        if (counts.Count > 0)
            return counts.GroupBy(n => n).OrderByDescending(g => g.Count()).First().Key;

        // Sensible default for three-phase power devices when the PDF does not say.
        return category is "Contactor" or "Motor starter" or "Overload relay" ? 3 : 0;
    }

    /// <summary>Most frequent NO/NC/CO configuration in the text ("1NO+1NC" counted as a whole).</summary>
    private static (int no, int nc, int co) CountContacts(string text, string category)
    {
        int no = 0, nc = 0, co = 0;
        foreach (System.Text.RegularExpressions.Match m in CircuitPatterns.ContactCount.Matches(text))
        {
            if (!int.TryParse(m.Groups["n"].Value, out var n) || n > 8) continue;
            var k = m.Groups["k"].Value.ToUpperInvariant().Replace(".", "").Replace("/", "");
            if (k is "NO" or "MAKE") no = System.Math.Max(no, n);
            else if (k is "NC" or "BREAK") nc = System.Math.Max(nc, n);
            else if (k is "CO" or "CHANGEOVER") co = System.Math.Max(co, n);
            else if (k == "SPDT") co = System.Math.Max(co, 1);
            else if (k == "DPDT") co = System.Math.Max(co, 2);
        }
        if (no + nc + co == 0 && category == "Relay") co = 1;
        return (no, nc, co);
    }

    /// <summary>Explicit aux contacts: needs a terminal-ish context on the line or >= 2 distinct pairs in the document.</summary>
    private static List<CircuitSpec> FindExplicitAux(List<TextLine> lines)
    {
        var found = new List<CircuitSpec>();
        var seen = new HashSet<string>();

        foreach (var line in lines)
        {
            foreach (System.Text.RegularExpressions.Match m in CircuitPatterns.TerminalTriple.Matches(line.Text))
            {
                var s = m.Groups["s"].Value;
                var key = $"CO{s}";
                if (seen.Add(key))
                    found.Add(new CircuitSpec
                    {
                        Kind = CircuitKind.Changeover,
                        Terminals = new() { $"{s}1", $"{s}2", $"{s}4" },
                        Explicit = true,
                        Source = Src(line),
                    });
            }
        }

        var pairs = new List<(TextLine line, string a, string b, bool context)>();
        foreach (var line in lines)
        {
            var ctx = CircuitPatterns.TerminalContext.IsMatch(line.Text);
            foreach (System.Text.RegularExpressions.Match m in CircuitPatterns.TerminalPair.Matches(line.Text))
            {
                var a = m.Groups["a"].Value;
                var b = m.Groups["b"].Value;
                if (a[0] != b[0]) continue;                       // same sequence digit
                var fa = a[1]; var fb = b[1];
                var isNo = fa == '3' && fb == '4';
                var isNc = fa == '1' && fb == '2';
                if (!isNo && !isNc) continue;
                if (seen.Contains($"CO{a[0]}")) continue;         // already part of a changeover
                pairs.Add((line, a, b, ctx));
            }
        }

        var distinct = pairs.Select(p => p.a).Distinct().Count();
        foreach (var p in pairs.Where(p => p.context || distinct >= 2))
        {
            if (!seen.Add(p.a)) continue;
            found.Add(new CircuitSpec
            {
                Kind = p.a[1] == '3' ? CircuitKind.AuxNO : CircuitKind.AuxNC,
                Terminals = new() { p.a, p.b },
                Explicit = true,
                Source = Src(p.line),
            });
        }

        return found.OrderBy(c => c.Terminals[0]).ToList();
    }
}
