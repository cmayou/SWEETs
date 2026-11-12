using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SWEETs.SwePdfSchematic.Core.Infrastructure;
using C = SWEETs.SwePdfSchematic.AddIn.Interop.SweContract;

namespace SWEETs.SwePdfSchematic.AddIn.Interop
{
    /// <summary>One schematic symbol of the SOLIDWORKS Electrical environment library.</summary>
    public sealed class LibrarySymbol
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Standard { get; set; }
        public string Library { get; set; }
        public string Category { get; set; }
        public string Type { get; set; }
        /// <summary>EwSymbolType name ("kSymbolComponent", "kSymbolConnection"…): a project symbol must be created with it.</summary>
        public string TypeCode { get; set; }
        public int Circuits { get; set; }
        public int Points { get; set; }
        public string Manufacturer { get; set; }
        /// <summary>Classification of the library symbol (node in its classification tree, and class id); 0 when unknown.</summary>
        public int ClassNodeId { get; set; }
        public int ClassId { get; set; }
        /// <summary>Units the symbol is drawn in: Metric, Imperial or null when unknown.</summary>
        public string Measurement { get; set; }
    }

    /// <summary>
    /// Reads the symbol library of the connected SOLIDWORKS Electrical environment through EwAPI
    /// (environment → symbol manager → every symbol's name, description, standard, library, class and circuit count)
    /// and indexes the symbol drawings on disk for previews. Loaded once per session on SWE's UI thread (EwAPI is
    /// apartment-bound); <see cref="Reload"/> refreshes after the library changes.
    /// </summary>
    public sealed class SweSymbolLibrary
    {
        private static SweSymbolLibrary _current;
        private Dictionary<string, string> _drawings;
        private Task _indexing;

        public IReadOnlyList<LibrarySymbol> Symbols { get; private set; } = new List<LibrarySymbol>();
        public string SymbolFolder { get; private set; }
        public string Error { get; private set; }
        public TimeSpan LoadTime { get; private set; }
        public bool IsLoaded => Symbols.Count > 0;

        private Dictionary<string, LibrarySymbol> _byName;
        /// <summary>The library symbol with this name, or null.</summary>
        public LibrarySymbol Find(string name)
        {
            if (name == null) return null;
            if (_byName == null)
            {
                _byName = new Dictionary<string, LibrarySymbol>(StringComparer.OrdinalIgnoreCase);
                foreach (var s in Symbols) if (!_byName.ContainsKey(s.Name)) _byName[s.Name] = s;
            }
            return _byName.TryGetValue(name, out var hit) ? hit : null;
        }

        /// <summary>Symbol types worth offering for a schematic symbol (labels, footprints, report and drawing-only types excluded).</summary>
        private static readonly HashSet<string> Wanted = new HashSet<string>
        {
            "kSymbolComponent", "kSymbolBlackbox", "kSymbolConnection", "kSymbolLink", "kSymbolPassive", "kSymbolSynoptic", "kSymbolUndefined",
        };

        /// <summary>Session cache: the library of the given application, loaded on first use.</summary>
        public static SweSymbolLibrary Get(object sweApplication, Action<string> progress = null)
        {
            if (_current == null || !_current.IsLoaded) _current = Load(sweApplication, progress);
            return _current;
        }

        public static SweSymbolLibrary Reload(object sweApplication, Action<string> progress = null) => _current = Load(sweApplication, progress);

        private static SweSymbolLibrary Load(object app, Action<string> progress)
        {
            var lib = new SweSymbolLibrary();
            if (app == null) { lib.Error = "SOLIDWORKS Electrical is not connected."; return lib; }
            var sw = Stopwatch.StartNew();
            try
            {
                progress?.Invoke("Reading SOLIDWORKS Electrical symbol library…");
                var env = SweApi.Call(app, C.AppGetEnvironment);
                var lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                var trees = new ClassTrees(env, lang);
                var mgr = SweApi.Call(env, C.EnvGetSymbolManager);
                var array = SweApi.Call(mgr, C.SymMgrGetArray) as Array;
                var list = new List<LibrarySymbol>(array?.Length ?? 0);
                var pending = new List<(LibrarySymbol sym, ClassTree tree, int node)>();
                int i = 0;
                foreach (var sym in array ?? new object[0])
                {
                    if (sym == null) continue;
                    if (++i % 250 == 0) progress?.Invoke($"Reading SOLIDWORKS Electrical symbol library… {i:N0} of {array.Length:N0}");
                    var type = Str(sym, C.SymGetType);
                    if (type != null && !Wanted.Contains(type)) continue;
                    var name = Str(sym, C.SymGetName);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    int nodeId = Int(sym, C.SymGetClassNodeId);
                    var tree = trees.For(sym);
                    var item = new LibrarySymbol
                    {
                        Name = name,
                        Description = Description(sym, lang),
                        Standard = Str(sym, C.SymGetStandard),
                        Library = Str(sym, C.SymGetLibraryCode),
                        Manufacturer = Str(sym, C.SymGetManufacturer),
                        Type = Humanize(type),
                        TypeCode = type,
                        Circuits = Int(sym, C.SymGetCircuitCount),
                        Points = Int(sym, C.SymGetPointCount),
                        Measurement = Measure(Str(sym, C.SymGetMeasurementType)),
                        ClassNodeId = nodeId,
                        ClassId = Int(sym, C.SymGetClassId),
                    };
                    list.Add(item);
                    pending.Add((item, tree, nodeId));
                }

                // Which class identifier a symbol's node id refers to is not documented: calibrate each tree against the
                // symbol descriptions ("Relay coil" ↔ "Coils"), then resolve.
                foreach (var g in pending.GroupBy(x => x.tree))
                    g.Key.Calibrate(g.Select(x => (x.sym, x.node)).ToList(), trees.Global);
                foreach (var x in pending) x.sym.Category = x.tree.Path(x.node);
                foreach (var x in pending.Where(x => x.sym.Name.StartsWith("EW_ANSI_REL", StringComparison.OrdinalIgnoreCase)
                                                     || x.sym.Name.Equals("EW_ANSI_TERMINAL", StringComparison.OrdinalIgnoreCase)
                                                     || x.sym.Name.Equals("EW_ANSI_MOTOR3P", StringComparison.OrdinalIgnoreCase)).Take(8))
                    FileLog.Write("library", $"sample: {x.sym.Name} \"{x.sym.Description}\" node={x.node} tree={x.tree.Id} → {x.sym.Category ?? "(not found)"}");
                lib.Symbols = list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                try
                {
                    if (SweApi.TryCall(env, C.EnvGetFolderPath, out var folder, SweApi.EnumValue("EwEnvironmentFolderPathValue", "kFolderPathSymbol")))
                        lib.SymbolFolder = folder as string;
                }
                catch (SweApiException ex) { FileLog.Write("library", "Symbol folder not available: " + ex.Message); }
                lib._indexing = Task.Run(() => lib.IndexDrawings());
                FileLog.Write("library", $"{lib.Symbols.Count} symbols read in {sw.ElapsedMilliseconds} ms; symbol folder {lib.SymbolFolder ?? "(unknown)"}");
            }
            catch (Exception ex)
            {
                lib.Error = ex.Message;
                FileLog.Write("library", ex);
            }
            lib.LoadTime = sw.Elapsed;
            return lib;
        }

        /// <summary>
        /// SOLIDWORKS Electrical keeps several classification trees (components, fluid/P&amp;ID, macros, cables, title
        /// blocks) whose node ids overlap, so a node id only means something inside the symbol's own tree. Each tree is
        /// read once from the classification the symbol reports (IEwSymbolX.getClassification), falling back to the
        /// component classification.
        /// </summary>
        private sealed class ClassTrees
        {
            private readonly object _env;
            private readonly string _lang;
            private readonly Dictionary<int, ClassTree> _byId = new Dictionary<int, ClassTree>();
            private ClassTree _fallback;

            public ClassTrees(object env, string lang) { _env = env; _lang = lang; }

            /// <summary>Every class of every classification, as the classification manager lists them (another id candidate).</summary>
            public ClassTree Global => _global ?? (_global = ReadGlobal());
            private ClassTree _global;

            private ClassTree ReadGlobal()
            {
                try
                {
                    var mgr = SweApi.Call(_env, C.EnvGetClassificationManager);
                    return ClassTree.Read(mgr, 0, _lang, C.ClsMgrGetArray);
                }
                catch (Exception ex) { FileLog.Write("library", "Class list not read: " + ex.Message); return null; }
            }

            public ClassTree For(object symbol)
            {
                if (SweApi.TryCall(symbol, C.SymGetClassification, out var clsf) && clsf != null)
                {
                    int id = Int(clsf, C.ClassificationGetId);
                    if (!_byId.TryGetValue(id, out var tree)) _byId[id] = tree = ClassTree.Read(clsf, id, _lang);
                    if (tree.Count > 0) return tree;
                }
                return _fallback ?? (_fallback = Component());
            }

            private ClassTree Component()
            {
                try
                {
                    var mgr = SweApi.Call(_env, C.EnvGetClassificationManager);
                    var clsf = SweApi.Call(mgr, C.ClsMgrGetClassification, SweApi.EnumValue("EwClassificationType", "kComponentType"));
                    return ClassTree.Read(clsf, Int(clsf, C.ClassificationGetId), _lang);
                }
                catch (Exception ex)
                {
                    FileLog.Write("library", "Component classification not read: " + ex.Message);
                    return new ClassTree(-1);
                }
            }
        }

        private sealed class ClassNode
        {
            public int Id, ClassId, Index, Parent;
            public string Name, Path;
        }

        /// <summary>
        /// One classification tree. The node id a symbol reports is matched against each identifier a class exposes
        /// (getID, getClassID, position); <see cref="Calibrate"/> picks the one whose class names agree best with the
        /// symbols' own descriptions.
        /// </summary>
        private sealed class ClassTree
        {
            private readonly List<ClassNode> _nodes = new List<ClassNode>();
            private readonly Dictionary<string, Dictionary<int, string>> _maps = new Dictionary<string, Dictionary<int, string>>();
            private string _space = "id";
            private Dictionary<int, string> _chosen;
            public int Id { get; }
            public int Count => _nodes.Count;
            public ClassTree(int id) { Id = id; }

            public string Path(int key)
            {
                var m = _chosen ?? (_maps.TryGetValue(_space, out var own) ? own : null);
                return m != null && m.TryGetValue(key, out var p) ? p : null;
            }

            public static ClassTree Read(object classification, int id, string lang) => Read(classification, id, lang, C.ClassificationGetClassArray);

            public static ClassTree Read(object source, int id, string lang, C.Member arrayMember)
            {
                var t = new ClassTree(id);
                try
                {
                    var arr = SweApi.Call(source, arrayMember) as Array;
                    int index = 0;
                    foreach (var cls in arr ?? new object[0])
                    {
                        if (cls == null) { index++; continue; }
                        string d = null;
                        foreach (var l in new[] { lang, "en" }.Distinct())
                            if (SweApi.TryCall(cls, C.ClassGetDescription, out var v, l) && !string.IsNullOrWhiteSpace(v as string)) { d = ((string)v).Trim(); break; }
                        t._nodes.Add(new ClassNode
                        {
                            Id = Int(cls, C.ClassGetId), ClassId = Int(cls, C.ClassGetClassId), Index = index++,
                            Parent = Int(cls, C.ClassGetParentId), Name = d,
                        });
                    }
                    t.BuildPaths();
                    FileLog.Write("library", $"classification {id}: {t._nodes.Count} classes; first: " +
                        string.Join("; ", t._nodes.Take(5).Select(n => $"id={n.Id} classId={n.ClassId} parent={n.Parent} {n.Name}")));
                }
                catch (Exception ex) { FileLog.Write("library", $"Classification {id} not read: " + ex.Message); }
                return t;
            }

            private void BuildPaths()
            {
                // parents refer to whichever identifier space contains them
                var byId = _nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
                var byClassId = _nodes.GroupBy(n => n.ClassId).ToDictionary(g => g.Key, g => g.First());
                var parents = byId.Count >= _nodes.Count * 0.9 && _nodes.Count(n => byId.ContainsKey(n.Parent)) >= _nodes.Count(n => byClassId.ContainsKey(n.Parent))
                    ? byId : byClassId;

                var tops = _nodes.Where(n => !parents.TryGetValue(n.Parent, out var p) || ReferenceEquals(p, n)).ToList();
                var singleRoot = tops.Count == 1 ? tops[0] : null;
                foreach (var n in _nodes)
                {
                    var parts = new List<string>();
                    var seen = new HashSet<ClassNode>();
                    var cur = n; ClassNode top = n;
                    while (cur != null && seen.Add(cur) && parts.Count < 8)
                    {
                        top = cur;
                        if (!string.IsNullOrEmpty(cur.Name)) parts.Insert(0, cur.Name);
                        cur = parents.TryGetValue(cur.Parent, out var p) && !ReferenceEquals(p, cur) ? p : null;
                    }
                    if (parts.Count > 1 && ReferenceEquals(top, singleRoot) && !string.IsNullOrEmpty(top.Name)) parts.RemoveAt(0);
                    n.Path = parts.Count > 0 ? string.Join(" › ", parts) : null;
                }

                Dictionary<int, string> Map(Func<ClassNode, int> key) =>
                    _nodes.Where(n => n.Path != null).GroupBy(key).ToDictionary(g => g.Key, g => g.First().Path);
                _maps["id"] = Map(n => n.Id);
                _maps["classId"] = Map(n => n.ClassId);
                _maps["index0"] = Map(n => n.Index);
                _maps["index1"] = Map(n => n.Index + 1);
            }

            /// <summary>Chooses the identifier space whose class paths share the most words with the symbol descriptions.</summary>
            public void Calibrate(IList<(LibrarySymbol sym, int node)> items, ClassTree global)
            {
                var candidates = new Dictionary<string, Dictionary<int, string>>(_maps);
                if (global != null) foreach (var kv in global._maps) candidates["all-" + kv.Key] = kv.Value;
                if (candidates.Count == 0 || items.Count == 0) return;
                var scores = new Dictionary<string, int>();
                foreach (var kv in candidates)
                {
                    int score = 0;
                    foreach (var (sym, node) in items)
                        if (kv.Value.TryGetValue(node, out var path) && Words(sym.Description + " " + sym.Name).Overlaps(Words(path))) score++;
                    scores[kv.Key] = score;
                }
                var best = scores.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key == "id" ? 0 : 1).First();
                if (best.Value > 0) { _space = best.Key; _chosen = candidates[best.Key]; }
                FileLog.Write("library", $"classification {Id}: node ids resolved by {_space} (agreement with descriptions: " +
                    string.Join(", ", scores.Select(kv => $"{kv.Key}={kv.Value}")) + $" of {items.Count} symbols)");
            }

            private static readonly HashSet<string> Stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "with", "without", "other", "type", "symbol", "single", "double", "normally", "open", "closed", "pole", "poles", "phase", "ansi", "iec" };

            private static HashSet<string> Words(string text)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in System.Text.RegularExpressions.Regex.Split((text ?? "").ToLowerInvariant(), "[^a-z]+"))
                {
                    if (raw.Length < 4 || Stop.Contains(raw)) continue;
                    var w = raw;
                    if (w.EndsWith("ies")) w = w.Substring(0, w.Length - 3) + "y";
                    else if (w.EndsWith("ches") || w.EndsWith("shes") || w.EndsWith("xes") || w.EndsWith("sses")) w = w.Substring(0, w.Length - 2);
                    else if (w.EndsWith("s") && !w.EndsWith("ss")) w = w.Substring(0, w.Length - 1);
                    if (w.Length >= 4) set.Add(w);
                }
                return set;
            }
        }

        private static string Description(object sym, string lang)
        {
            foreach (var l in new[] { lang, "en" }.Distinct())
                if (SweApi.TryCall(sym, C.SymGetDescription, out var v, l) && !string.IsNullOrWhiteSpace(v as string)) return ((string)v).Trim();
            return null;
        }

        private static string Measure(string t) =>
            t == null ? null : t.IndexOf("Metric", StringComparison.OrdinalIgnoreCase) >= 0 ? "Metric"
            : t.IndexOf("Imperial", StringComparison.OrdinalIgnoreCase) >= 0 ? "Imperial" : null;

        private static string Str(object o, C.Member m) => SweApi.TryCall(o, m, out var v) ? v?.ToString() : null;
        private static int Int(object o, C.Member m) => SweApi.TryCall(o, m, out var v) && v != null ? Convert.ToInt32(v) : 0;

        private static string Humanize(string type) =>
            type == null ? null : type.StartsWith("kSymbol") ? type.Substring(7) : type;

        // ---------------------------------------------------------------- previews

        /// <summary>Symbol drawings (*.dwg) under the environment's symbol folder, by file name. Built in the background.</summary>
        private void IndexDrawings()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!string.IsNullOrEmpty(SymbolFolder) && Directory.Exists(SymbolFolder))
                    foreach (var f in Directory.EnumerateFiles(SymbolFolder, "*.dwg", SearchOption.AllDirectories))
                    {
                        var key = Path.GetFileNameWithoutExtension(f);
                        if (!map.ContainsKey(key)) map[key] = f;
                    }
            }
            catch (Exception ex) { FileLog.Write("library", "Symbol drawings not indexed: " + ex.Message); }
            _drawings = map;
        }

        /// <summary>Path of the symbol's drawing, or null when it is not (yet) known.</summary>
        public async Task<string> DrawingPathAsync(string symbolName)
        {
            if (_indexing != null) await _indexing.ConfigureAwait(false);
            return _drawings != null && symbolName != null && _drawings.TryGetValue(symbolName, out var p) ? p : null;
        }
    }
}
