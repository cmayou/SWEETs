using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace DataSheetMfgPartsImport.Extraction;

/// <summary>
/// Product categories: how to recognise them in a datasheet, and which words to look for in
/// SOLIDWORKS Electrical class names when suggesting a class. Order matters - more specific first.
/// </summary>
public sealed class CategoryRule
{
    public string Name { get; set; }
    public string[] TextKeywords { get; set; }
    public string[] ClassKeywords { get; set; }
}

public static class CategoryRules
{
    public static readonly CategoryRule[] All =
    {
        R("Overload relay",     new[] { "overload relay", "thermal overload", "overload protection relay" }, new[] { "overload", "thermal relay" }),
        R("Safety relay",       new[] { "safety relay", "safety module", "emergency stop relay" },           new[] { "safety relay", "safety" }),
        R("Motor starter",      new[] { "motor starter", "motor protection circuit breaker", "manual motor starter", "motor circuit protector", "MPCB" }, new[] { "motor starter", "motor protection", "starter" }),
        R("Contactor",          new[] { "contactor", "power contactor", "contactor relay" },                 new[] { "contactor" }),
        R("Circuit breaker",    new[] { "circuit breaker", "miniature circuit breaker", "MCB", "MCCB", "molded case" }, new[] { "circuit breaker", "breaker", "protection" }),
        R("Fuse",               new[] { "fuse holder", "fuse", "fuse carrier" },                             new[] { "fuse" }),
        R("Terminal block",     new[] { "terminal block", "feed-through terminal", "din rail terminal", "ground terminal" }, new[] { "terminal" }),
        R("Power supply",       new[] { "power supply", "switch mode power", "switching power", "SMPS", "PSU" }, new[] { "power supply", "supply" }),
        R("PLC",                new[] { "PLC", "programmable controller", "programmable logic", "I/O module", "CPU module" }, new[] { "plc", "controller" }),
        R("Relay",              new[] { "relay", "interface relay", "plug-in relay", "solid state relay", "time relay", "timer relay" }, new[] { "relay" }),
        R("Push button",        new[] { "push button", "pushbutton", "selector switch", "emergency stop", "e-stop" }, new[] { "push button", "pushbutton", "button", "control" }),
        R("Pilot light",        new[] { "pilot light", "indicator light", "signal lamp", "LED indicator" },  new[] { "light", "indicator", "signal" }),
        R("Disconnect switch",  new[] { "disconnect switch", "switch disconnector", "isolator", "load break switch", "main switch" }, new[] { "disconnect", "switch", "isolator" }),
        R("Sensor",             new[] { "proximity sensor", "photoelectric", "inductive sensor", "capacitive sensor", "sensor" }, new[] { "sensor", "detector" }),
        R("Transformer",        new[] { "transformer" },                                                     new[] { "transformer" }),
        R("Drive",              new[] { "AC drive", "variable frequency drive", "adjustable frequency drive", "frequency inverter", "VFD", "servo drive", "soft starter" }, new[] { "drive", "inverter", "variator", "speed" }),
        R("Connector",          new[] { "connector", "connector housing", "receptacle", "header", "plug" },  new[] { "connector", "plug" }),
        R("Motor",              new[] { "induction motor", "servo motor", "gear motor", "motor" },           new[] { "motor" }),
    };

    private static CategoryRule R(string n, string[] text, string[] cls) => new() { Name = n, TextKeywords = text, ClassKeywords = cls };

    /// <summary>Scores each category: title/heading hits count 5×, body hits 1× (capped).</summary>
    public static (CategoryRule rule, int score) Detect(string headingText, string bodyText)
    {
        CategoryRule best = null;
        var bestScore = 0;
        foreach (var rule in All)
        {
            var score = 0;
            foreach (var kw in rule.TextKeywords)
            {
                var opts = kw.All(c => !char.IsLetter(c) || char.IsUpper(c)) ? RegexOptions.None : RegexOptions.IgnoreCase;
                var rx = new Regex(@"(?<!\w)" + Regex.Escape(kw) + @"s?(?!\w)", opts | RegexOptions.CultureInvariant);
                score += 5 * Math.Min(rx.Matches(headingText ?? "").Count, 3);
                score += Math.Min(rx.Matches(bodyText ?? "").Count, 10);
            }
            // Earlier (more specific) rules win ties.
            if (score > bestScore) { best = rule; bestScore = score; }
        }
        return (best, bestScore);
    }

    /// <summary>The category named by free text such as "Connector Housings" or "Miniature circuit breaker", or null.</summary>
    public static CategoryRule FromText(string text) => Detect(text, "").rule;

    public static CategoryRule Find(string name) =>
        All.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A class from the SOLIDWORKS Electrical component classification, as seen by the matcher.</summary>
public sealed class ClassCandidate
{
    public int Id { get; set; }
    public string Name { get; set; }
    /// <summary>"Parent / Child" path used for display and matching.</summary>
    public string Path { get; set; }
}

public static class ClassMatcher
{
    /// <summary>Best class for a category, or null. Leaf classes whose own name matches score highest.</summary>
    public static ClassCandidate Suggest(string category, IEnumerable<ClassCandidate> classes)
    {
        var rule = CategoryRules.Find(category);
        if (rule == null) return null;

        ClassCandidate best = null;
        var bestScore = 0;
        foreach (var c in classes)
        {
            var score = 0;
            for (var i = 0; i < rule.ClassKeywords.Length; i++)
            {
                var kw = rule.ClassKeywords[i];
                var weight = rule.ClassKeywords.Length - i; // first keyword is the strongest
                if (Contains(c.Name, kw)) score += 10 * weight;
                else if (Contains(c.Path, kw)) score += 3 * weight;
            }
            if (Contains(c.Name, rule.Name)) score += 15;
            // Prefer deeper (more specific) classes on ties.
            score = score * 10 + Math.Min((c.Path ?? "").Count(ch => ch == '/'), 9);
            if (score > bestScore) { best = c; bestScore = score; }
        }
        return bestScore >= 30 ? best : null;
    }

    /// <summary>
    /// Fallback when the category has no keyword rule: the class whose name shares the most words with the
    /// printed component type, preferring short names. Null unless a word matches.
    /// </summary>
    public static ClassCandidate SuggestFromText(string text, IEnumerable<ClassCandidate> classes)
    {
        var want = Tokens(text);
        if (want.Count == 0 || classes == null) return null;
        ClassCandidate best = null;
        double bestScore = 0;
        foreach (var c in classes)
        {
            var name = Tokens(c.Name);
            var path = Tokens(c.Path);
            double score = want.Count(t => name.Contains(t)) * 3 + want.Count(t => path.Contains(t))
                           - 0.4 * name.Count(t => !want.Contains(t));
            if (score > bestScore) { bestScore = score; best = c; }
        }
        return bestScore >= 2.5 ? best : null;
    }

    private static HashSet<string> Tokens(string s)
    {
        var set = new HashSet<string>();
        foreach (Match m in Regex.Matches((s ?? "").ToLowerInvariant(), "[a-z0-9]+"))
        {
            var t = m.Value;
            if (t.Length > 3 && t.EndsWith("s")) t = t.Substring(0, t.Length - 1);
            if (t.Length > 1) set.Add(t);
        }
        return set;
    }

    private static bool Contains(string hay, string needle) =>
        !string.IsNullOrEmpty(hay) && hay.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
