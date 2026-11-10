using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using DataSheetMfgPartsImport.Interop;

namespace DataSheetMfgPartsImport.Archive
{
    public sealed class ClassNode
    {
        public int Id;
        public int ParentId;
        public string Name = "";
        public string Path = "";
        public bool IsLeaf = true;
        public override string ToString() { return Path; }
    }

    public static class ArchiveInfo
    {
        /// <summary>Languages defined in the archive (codes accepted by the description setters).</summary>
        public static List<string> GetLanguages(IEwApplicationX app)
        {
            var result = new List<string>();
            try
            {
                EwErrorCode ec;
                var settings = app.getEwApplicationSettings(out ec);
                if (settings == null) return result;
                var arr = settings.getLanguageArray(out ec) as Array;
                if (arr == null) return result;
                foreach (var o in arr)
                {
                    string s = o as string;
                    if (!string.IsNullOrEmpty(s)) result.Add(s);
                }
            }
            catch (Exception ex) { Log.Write("GetLanguages failed: " + ex.Message); }
            return result;
        }

        public static string PickLanguage(string preferred, List<string> available)
        {
            if (available == null || available.Count == 0) return preferred ?? "";
            if (!string.IsNullOrEmpty(preferred) && available.Contains(preferred)) return preferred;
            var en = available.FirstOrDefault(l => l.StartsWith("en", StringComparison.OrdinalIgnoreCase));
            return en ?? available[0];
        }

        /// <summary>Component classes of the archive, flattened with their full path.</summary>
        public static List<ClassNode> LoadClasses(IEwApplicationX app, string language, List<string> allLanguages)
        {
            EwErrorCode ec;
            var env = app.getEwEnvironment(out ec);
            if (env == null) throw new InvalidOperationException("No archive environment (" + ec + ").");
            var mgr = env.getEwClassificationManager(out ec);
            if (mgr == null) throw new InvalidOperationException("No classification manager (" + ec + ").");
            var classification = mgr.getEwClassification(EwClassificationType.kComponentType, out ec);
            if (classification == null) throw new InvalidOperationException("No component classification (" + ec + ").");
            var arr = classification.getEwClassArray(out ec) as Array;
            var nodes = new Dictionary<int, ClassNode>();
            if (arr == null) return new List<ClassNode>();

            foreach (var o in arr)
            {
                var c = o as IEwClassX;
                if (c == null) continue;
                try
                {
                    var n = new ClassNode();
                    n.Id = c.getID();
                    EwErrorCode e2;
                    n.ParentId = c.getParentID(out e2);
                    string name = SafeDescription(c, language);
                    if (string.IsNullOrWhiteSpace(name) && allLanguages != null)
                    {
                        foreach (var lg in allLanguages)
                        {
                            name = SafeDescription(c, lg);
                            if (!string.IsNullOrWhiteSpace(name)) break;
                        }
                    }
                    n.Name = string.IsNullOrWhiteSpace(name) ? "(unnamed " + n.Id + ")" : name.Trim();
                    nodes[n.Id] = n;
                }
                catch (Exception ex) { Log.Write("Skipped a class: " + ex.Message); }
            }

            foreach (var n in nodes.Values)
            {
                ClassNode parent;
                if (n.ParentId != n.Id && nodes.TryGetValue(n.ParentId, out parent)) parent.IsLeaf = false;
            }
            foreach (var n in nodes.Values) n.Path = BuildPath(n, nodes);
            return nodes.Values.OrderBy(n => n.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        static string SafeDescription(IEwClassX c, string lang)
        {
            try
            {
                EwErrorCode ec;
                return c.getDescription(lang ?? "", out ec) ?? "";
            }
            catch { return ""; }
        }

        static string BuildPath(ClassNode n, Dictionary<int, ClassNode> all)
        {
            var parts = new List<string> { n.Name };
            var cur = n;
            for (int depth = 0; depth < 20; depth++)
            {
                ClassNode p;
                if (cur.ParentId == cur.Id || !all.TryGetValue(cur.ParentId, out p)) break;
                parts.Insert(0, p.Name);
                cur = p;
            }
            return string.Join(" / ", parts);
        }

        // ───────── matching the printed component type to a class ─────────

        static HashSet<string> Tokens(string s)
        {
            var set = new HashSet<string>();
            foreach (Match m in Regex.Matches((s ?? "").ToLowerInvariant(), "[a-z0-9]+"))
            {
                string t = m.Value;
                if (t.Length > 3 && t.EndsWith("s")) t = t.Substring(0, t.Length - 1);
                if (t.Length > 1) set.Add(t);
            }
            return set;
        }

        public static ClassNode BestMatch(List<ClassNode> nodes, string text)
        {
            var want = Tokens(text);
            if (want.Count == 0 || nodes == null) return null;
            ClassNode best = null;
            double bestScore = 0;
            foreach (var n in nodes)
            {
                var nameTokens = Tokens(n.Name);
                var pathTokens = Tokens(n.Path);
                double score = want.Count(t => nameTokens.Contains(t)) * 3 + want.Count(t => pathTokens.Contains(t))
                               - 0.4 * nameTokens.Count(t => !want.Contains(t));
                if (n.IsLeaf) score += 0.5;
                if (score > bestScore) { bestScore = score; best = n; }
            }
            return bestScore >= 2.5 ? best : null;
        }
    }
}
