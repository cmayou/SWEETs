using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using SWEETs.SwePdfSchematic.AddIn.Interop;
using SWEETs.SwePdfSchematic.Core.Infrastructure;
using SWEETs.SwePdfSchematic.Core.Mapping;

namespace SWEETs.SwePdfSchematic.AddIn.UI
{
    /// <summary>One choice in the symbol picker: a library symbol, a learned name or a catalog-file entry.</summary>
    public sealed class CatalogEntry : INotifyPropertyChanged
    {
        public string Name { get; set; }
        public string Description { get; set; }
        /// <summary>"Library" (read from SOLIDWORKS Electrical), "Learned" (used before) or "Catalog" (symbol-catalog.txt).</summary>
        public string Source { get; set; }
        public string Standard { get; set; }
        public string Category { get; set; }
        public string Library { get; set; }
        public string Type { get; set; }
        public int Circuits { get; set; }
        public int Points { get; set; }
        public int Uses { get; set; }

        public string Meta => string.Join("  ·  ", new[]
        {
            Category, Standard,
            Circuits > 0 ? $"{Circuits} circuit{(Circuits == 1 ? "" : "s")}" : null,
            Points > 0 ? $"{Points} pts" : null,
            Library,
            Uses > 0 ? $"used {Uses}×" : null,
        }.Where(x => !string.IsNullOrWhiteSpace(x)));

        internal SymbolCatalog Owner;
        private BitmapSource _preview;
        private bool _requested;

        /// <summary>Thumbnail from the symbol's DWG; loaded in the background the first time it is shown.</summary>
        public BitmapSource Preview
        {
            get
            {
                if (!_requested) { _requested = true; Owner?.RequestPreview(this); }
                return _preview;
            }
        }

        internal void SetPreview(BitmapSource b)
        {
            _preview = b;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Preview)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPreview)));
        }

        public bool HasPreview => _preview != null;
        public event PropertyChangedEventHandler PropertyChanged;
    }

    /// <summary>What the reviewer is looking for.</summary>
    public sealed class CatalogQuery
    {
        public string Text;
        /// <summary>Words describing the detected kind ("fuse", "circuit breaker"): rank, never filter.</summary>
        public string Hint;
        public string Standard;   // null = any
        public string Category;   // null = any
        public int Poles;         // detected pole count; > 1 favours multi-circuit symbols
        public bool MatchPoles;   // filter to symbols with exactly Poles circuits
    }

    /// <summary>
    /// Symbols offered in the mapping picker, best first:
    ///  - the schematic symbol library of the connected SOLIDWORKS Electrical environment (via EwAPI);
    ///  - every name already learned in the mapping store;
    ///  - symbol-catalog.txt next to the mapping store ("name|description" per line) for names outside the library.
    /// Free text is still accepted, so the picker never blocks.
    /// </summary>
    public sealed class SymbolCatalog
    {
        private readonly MappingStore _store;
        private readonly string _path;
        private List<CatalogEntry> _entries = new List<CatalogEntry>();
        private SweSymbolLibrary _library;

        public SymbolCatalog(MappingStore store)
        {
            _store = store;
            _path = Path.Combine(Path.GetDirectoryName(store.FilePath), "symbol-catalog.txt");
            Rebuild();
        }

        public string CatalogPath => _path;
        public SweSymbolLibrary Library => _library;
        public int LibraryCount => _library?.Symbols.Count ?? 0;
        public string LibraryError => _library?.Error;

        /// <summary>Loads the SWE library once (UI thread: EwAPI is apartment-bound). Cheap after the first call.</summary>
        public void EnsureLibrary(object sweApplication, Action<string> progress, bool reload = false)
        {
            if (sweApplication == null) return;
            if (_library != null && _library.IsLoaded && !reload) return;
            _library = reload ? SweSymbolLibrary.Reload(sweApplication, progress) : SweSymbolLibrary.Get(sweApplication, progress);
            Rebuild();
        }

        public void Rebuild()
        {
            var byName = new Dictionary<string, CatalogEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in _library?.Symbols ?? Enumerable.Empty<LibrarySymbol>())
                if (!byName.ContainsKey(s.Name))
                    byName[s.Name] = new CatalogEntry
                    {
                        Name = s.Name, Description = s.Description, Source = "Library", Standard = s.Standard, Category = s.Category,
                        Library = s.Library, Type = s.Type, Circuits = s.Circuits, Points = s.Points, Owner = this,
                    };

            if (File.Exists(_path))
                foreach (var line in File.ReadAllLines(_path))
                {
                    var t = line.Trim();
                    if (t.Length == 0 || t.StartsWith("#")) continue;
                    var parts = t.Split('|');
                    var name = parts[0].Trim();
                    if (name.Length == 0 || byName.ContainsKey(name)) continue;
                    byName[name] = new CatalogEntry { Name = name, Description = parts.Length > 1 ? parts[1].Trim() : "", Source = "Catalog", Owner = this };
                }

            foreach (var g in _store.All.GroupBy(m => m.SweSymbolName, StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(g.Key)) continue;
                if (!byName.TryGetValue(g.Key, out var e))
                    byName[g.Key] = e = new CatalogEntry { Name = g.Key, Source = "Learned", Owner = this,
                                                          Description = $"Used for {string.Join(", ", g.Select(x => x.Kind).Distinct())}" };
                e.Uses = g.Sum(x => x.Uses);
                e.Standard = e.Standard ?? g.Select(x => x.Standard).FirstOrDefault();
            }
            _entries = byName.Values.ToList();
        }

        public IEnumerable<string> Standards =>
            _entries.Select(e => e.Standard).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s);

        /// <summary>Every class path and its ancestors ("Protection", "Protection › Fuses"), so a parent class can be picked.</summary>
        public IEnumerable<string> Categories =>
            _entries.Select(e => e.Category).Where(s => !string.IsNullOrWhiteSpace(s))
                    .SelectMany(Ancestors).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase);

        private const string PathSep = " › ";

        private static IEnumerable<string> Ancestors(string path)
        {
            var parts = path.Split(new[] { PathSep }, StringSplitOptions.None);
            for (int i = 1; i <= parts.Length; i++) yield return string.Join(PathSep, parts.Take(i));
        }

        private static bool InClass(string category, string filter) =>
            category != null && (category.Equals(filter, StringComparison.OrdinalIgnoreCase)
                                 || category.StartsWith(filter + PathSep, StringComparison.OrdinalIgnoreCase));

        public int Count => _entries.Count;

        public CatalogEntry Find(string name) =>
            _entries.FirstOrDefault(e => e.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Filters by text (every word must appear in name, description, category or library), standard, category and
        /// circuit count, then ranks: learned names, matching standard, matching circuit count and kind hint first.
        /// </summary>
        public List<CatalogEntry> Search(CatalogQuery q, int max = 400)
        {
            var words = (q.Text ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var hint = (q.Hint ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2).ToArray();
            var hits = _entries.Where(e =>
                words.All(w => Has(e.Name, w) || Has(e.Description, w) || Has(e.Category, w) || Has(e.Library, w))
                && (q.Standard == null || e.Standard == null || e.Standard.Equals(q.Standard, StringComparison.OrdinalIgnoreCase))
                && (q.Category == null || InClass(e.Category, q.Category))
                && (!q.MatchPoles || q.Poles <= 1 || e.Circuits == q.Poles));

            double Score(CatalogEntry e)
            {
                double s = 0;
                if (e.Uses > 0) s += 4 + Math.Min(3, e.Uses / 5.0);
                if (q.Standard == null && e.Standard != null) s += 0; // no preference
                s += 1.5 * hint.Count(w => Has(e.Category, w) || Has(e.Description, w) || Has(e.Name, w));
                if (q.Poles > 1) s += e.Circuits == q.Poles ? 3 : e.Circuits > 0 ? -1 : 0;
                else if (e.Circuits == 1) s += 0.5;
                if (words.Length > 0 && e.Name.StartsWith(words[0], StringComparison.OrdinalIgnoreCase)) s += 2;
                return s;
            }

            return hits.Select(e => (e, s: Score(e))).OrderByDescending(x => x.s).ThenBy(x => x.e.Name, StringComparer.OrdinalIgnoreCase)
                       .Take(max).Select(x => x.e).ToList();
        }

        /// <summary>Adds a free-typed name so it is offered next time (written to symbol-catalog.txt).</summary>
        public void Remember(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || Find(name) != null) return;
            _entries.Add(new CatalogEntry { Name = name, Description = "Added during review", Source = "Catalog", Owner = this });
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.AppendAllText(_path, name + "|Added during review" + Environment.NewLine);
            }
            catch (Exception ex) { FileLog.Write("catalog", ex); }
        }

        internal void RequestPreview(CatalogEntry e)
        {
            var lib = _library;
            if (lib == null || e.Source != "Library") return;
            var dispatcher = Application.Current?.Dispatcher;
            Task.Run(async () =>
            {
                var path = await lib.DrawingPathAsync(e.Name).ConfigureAwait(false);
                var img = path == null ? null : DwgThumbnail.Read(path);
                if (img == null) return;
                if (dispatcher != null) _ = dispatcher.BeginInvoke(new Action(() => e.SetPreview(img)));
                else e.SetPreview(img);
            });
        }

        private static bool Has(string s, string q) => s != null && s.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
