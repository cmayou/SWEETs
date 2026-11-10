using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Recognition;

namespace SWEETs.SwePdfSchematic.Core.Mapping
{
    public sealed class LearnedMapping
    {
        [JsonPropertyName("standard")] public string Standard { get; set; }
        /// <summary>Exact signature key, or null for a kind-level default.</summary>
        [JsonPropertyName("signature")] public string SignatureKey { get; set; }
        [JsonPropertyName("grid")] public string GridHex { get; set; }
        [JsonPropertyName("kind")] public string Kind { get; set; }
        [JsonPropertyName("sweSymbol")] public string SweSymbolName { get; set; }
        /// <summary>Extra rotation (deg) to apply on top of the computed rotation for this library symbol.</summary>
        [JsonPropertyName("rotationOffset")] public int RotationOffset { get; set; }
        [JsonPropertyName("uses")] public int Uses { get; set; }
        [JsonPropertyName("updated")] public DateTime Updated { get; set; }
        [JsonPropertyName("by")] public string UpdatedBy { get; set; }
    }

    public sealed class MappingFile
    {
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("mappings")] public List<LearnedMapping> Mappings { get; set; } = new List<LearnedMapping>();
    }

    /// <summary>
    /// Local, file-based memory of reviewer decisions. Exact signature hits win; then similar shapes;
    /// then the kind-level default for the standard. Point <see cref="FilePath"/> at a network share
    /// to let a team learn together. Writes are atomic (temp file + replace).
    /// </summary>
    public sealed class MappingStore
    {
        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions { WriteIndented = true };
        private MappingFile _file = new MappingFile();
        private readonly object _lock = new object();

        public string FilePath { get; }
        public int Count => _file.Mappings.Count;

        public MappingStore(string filePath)
        {
            FilePath = filePath;
            Reload();
        }

        public static string DefaultPath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SWEETs", "PdfSchematic", "mappings.json");

        public void Reload()
        {
            lock (_lock)
            {
                if (File.Exists(FilePath))
                {
                    try { _file = JsonSerializer.Deserialize<MappingFile>(File.ReadAllText(FilePath)) ?? new MappingFile(); }
                    catch (JsonException) { File.Copy(FilePath, FilePath + ".corrupt-" + DateTime.Now.ToString("yyyyMMddHHmmss"), true); _file = new MappingFile(); }
                }
            }
        }

        public void Save()
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_file, Json));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
        }

        public List<SymbolCandidate> Candidates(DetectedSymbol s, double similarThreshold)
        {
            var std = s.Standard.ToString();
            var list = new List<SymbolCandidate>();
            lock (_lock)
            {
                var key = s.Signature?.Key;
                foreach (var m in _file.Mappings.Where(m => m.Standard == std && m.SignatureKey != null && m.SignatureKey == key))
                    Add(list, m.SweSymbolName, 0.97, "learned-exact");

                if (s.Signature != null)
                    foreach (var m in _file.Mappings.Where(m => m.Standard == std && m.SignatureKey != null && m.SignatureKey != key))
                    {
                        var other = ParseKey(m.SignatureKey);
                        double sim = other == null ? 0 : s.Signature.Similarity(other);
                        if (sim >= similarThreshold) Add(list, m.SweSymbolName, 0.6 + 0.3 * sim, "learned-similar");
                    }

                // kind defaults are single-pole choices: never offer them for a combined multi-pole symbol
                if (!s.IsCombined)
                    foreach (var m in _file.Mappings.Where(m => m.Standard == std && m.SignatureKey == null && m.Kind == s.Kind.ToString()))
                        Add(list, m.SweSymbolName, 0.75, "kind-default");
            }
            return list.OrderByDescending(c => c.Score).ToList();
        }

        public int RotationOffset(DetectedSymbol s, string sweSymbol)
        {
            lock (_lock)
                return _file.Mappings.FirstOrDefault(m => m.SweSymbolName == sweSymbol && m.Standard == s.Standard.ToString()
                                                          && (m.SignatureKey == s.Signature?.Key || m.SignatureKey == null))?.RotationOffset ?? 0;
        }

        /// <summary>Records a reviewer decision both for the exact shape and as the kind default.</summary>
        public void Learn(DetectedSymbol s, string sweSymbol, int rotationOffset = 0)
        {
            if (string.IsNullOrWhiteSpace(sweSymbol)) return;
            lock (_lock)
            {
                Upsert(s.Standard.ToString(), s.Signature?.Key, s.Signature?.Grid.ToString("X16"), s.Kind.ToString(), sweSymbol, rotationOffset);
                if (s.Kind != SymbolKind.Unknown && !s.IsCombined)
                    Upsert(s.Standard.ToString(), null, null, s.Kind.ToString(), sweSymbol, rotationOffset);
            }
        }

        public IReadOnlyList<LearnedMapping> All { get { lock (_lock) return _file.Mappings.ToList(); } }

        public void Remove(LearnedMapping m) { lock (_lock) _file.Mappings.Remove(m); }

        public void ImportFrom(string path)
        {
            var other = JsonSerializer.Deserialize<MappingFile>(File.ReadAllText(path)) ?? new MappingFile();
            lock (_lock)
                foreach (var m in other.Mappings)
                    Upsert(m.Standard, m.SignatureKey, m.GridHex, m.Kind, m.SweSymbolName, m.RotationOffset);
        }

        public void ExportTo(string path) { lock (_lock) File.WriteAllText(path, JsonSerializer.Serialize(_file, Json)); }

        private void Upsert(string std, string sigKey, string grid, string kind, string swe, int rot)
        {
            var existing = _file.Mappings.FirstOrDefault(m => m.Standard == std && m.SignatureKey == sigKey && (sigKey != null || m.Kind == kind));
            if (existing == null) _file.Mappings.Add(existing = new LearnedMapping { Standard = std, SignatureKey = sigKey, GridHex = grid, Kind = kind });
            existing.SweSymbolName = swe;
            existing.RotationOffset = rot;
            existing.Uses++;
            existing.Updated = DateTime.UtcNow;
            existing.UpdatedBy = Environment.UserName;
        }

        private static void Add(List<SymbolCandidate> list, string name, double score, string source)
        {
            var e = list.FirstOrDefault(c => c.SweSymbolName == name);
            if (e == null) list.Add(new SymbolCandidate { SweSymbolName = name, Score = score, Source = source });
            else if (score > e.Score) { e.Score = score; e.Source = source; }
        }

        /// <summary>Rebuilds a comparable signature from its key (for fuzzy matching against stored shapes).</summary>
        public static SymbolSignature ParseKey(string key)
        {
            try
            {
                var parts = key.Split('|').ToDictionary(p => p.Substring(0, 1), p => p.Substring(1));
                var pins = parts["P"].Split('.');
                return new SymbolSignature
                {
                    Pins = int.Parse(pins[0]),
                    Poles = int.Parse(pins[1]),
                    Circles = int.Parse(parts["C"]),
                    Rectangles = int.Parse(parts["R"]),
                    Triangles = int.Parse(parts["T"]),
                    Diagonals = int.Parse(parts["D"]),
                    Filled = int.Parse(parts["F"]),
                    AspectBucket = int.Parse(parts["A"]),
                    InnerText = parts["I"],
                    Grid = Convert.ToUInt64(parts["G"], 16),
                };
            }
            catch { return null; }
        }
    }
}
