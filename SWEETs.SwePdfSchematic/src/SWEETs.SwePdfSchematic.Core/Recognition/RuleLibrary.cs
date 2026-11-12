using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    public sealed class PrefixRule
    {
        [JsonPropertyName("standard")] public string Standard { get; set; }
        [JsonPropertyName("prefix")] public string Prefix { get; set; }
        [JsonPropertyName("category")] public string Category { get; set; }
        [JsonPropertyName("kinds")] public List<string> Kinds { get; set; } = new List<string>();
    }

    public sealed class RuleFile
    {
        [JsonPropertyName("prefixes")] public List<PrefixRule> Prefixes { get; set; } = new List<PrefixRule>();
        [JsonPropertyName("kindSearch")] public Dictionary<string, string> KindSearch { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Tag-prefix rules. Ships with embedded defaults; a rules.json next to the mapping store
    /// (if present) replaces them so a team can tune prefixes without a rebuild.
    /// </summary>
    public sealed class RuleLibrary
    {
        private readonly RuleFile _rules;

        private RuleLibrary(RuleFile rules) { _rules = rules; }

        public static RuleLibrary Load(string overridePath = null)
        {
            string json = null;
            if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath))
                json = File.ReadAllText(overridePath);
            if (json == null)
            {
                var asm = typeof(RuleLibrary).Assembly;
                var name = asm.GetManifestResourceNames().First(n => n.EndsWith("default-rules.json", StringComparison.OrdinalIgnoreCase));
                using (var s = asm.GetManifestResourceStream(name))
                using (var r = new StreamReader(s)) json = r.ReadToEnd();
            }
            var file = JsonSerializer.Deserialize<RuleFile>(json, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip });
            return new RuleLibrary(file ?? new RuleFile());
        }

        public PrefixRule Find(ParsedTag tag)
        {
            if (tag == null) return null;
            // Standard-neutral marks (SOLIDWORKS Electrical "04Q1", bare "X1") use the SWE rules, then IEC letter codes.
            var order = tag.Standard == SymbolStandard.Unknown ? new[] { "SWE", "IEC" } : new[] { tag.Standard.ToString() };
            foreach (var std in order)
            {
                var hit = _rules.Prefixes.FirstOrDefault(p =>
                    string.Equals(p.Standard, std, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(p.Prefix, tag.Prefix, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }
            return null;
        }

        public IEnumerable<SymbolKind> AllowedKinds(ParsedTag tag)
        {
            var rule = Find(tag);
            if (rule == null) yield break;
            foreach (var k in rule.Kinds)
                if (Enum.TryParse(k, out SymbolKind kind)) yield return kind;
        }

        public string SearchHint(SymbolKind kind) =>
            _rules.KindSearch.TryGetValue(kind.ToString(), out var s) ? s : kind.ToString();
    }
}
