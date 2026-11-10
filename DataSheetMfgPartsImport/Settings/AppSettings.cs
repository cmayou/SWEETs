using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.Settings
{
    public sealed class Target
    {
        public string Id { get; set; }
        public string Label { get; set; }
    }

    public static class Targets
    {
        public static readonly List<Target> All = Build();

        static List<Target> Build()
        {
            var l = new List<Target>
            {
                new Target { Id = "none", Label = "Not written" },
                new Target { Id = "description", Label = "Description" },
                new Target { Id = "serie", Label = "Series" },
                new Target { Id = "commercialRef", Label = "Commercial reference" },
                new Target { Id = "stock", Label = "Stock number" },
                new Target { Id = "article", Label = "Article number" },
                new Target { Id = "supplier", Label = "Supplier name" },
                new Target { Id = "useVoltage", Label = "Use voltage" },
                new Target { Id = "useFrequency", Label = "Use frequency" },
                new Target { Id = "controlVoltage", Label = "Control voltage" },
                new Target { Id = "controlFrequency", Label = "Control frequency" },
                new Target { Id = "width", Label = "Width (mm)" },
                new Target { Id = "height", Label = "Height (mm)" },
                new Target { Id = "depth", Label = "Depth (mm)" },
                new Target { Id = "weight", Label = "Weight (kg)" }
            };
            for (int i = 1; i <= 7; i++) l.Add(new Target { Id = "value" + i, Label = "Value " + i });
            for (int i = 1; i <= 20; i++) l.Add(new Target { Id = "user" + i, Label = "User data " + i });
            return l;
        }
    }

    public sealed class AppSettings
    {
        public const string EngineRules = "rules";
        public const string EngineOllama = "ollama";

        /// <summary>"rules" (offline, PDFs with text) or "ollama" (local AI model).</summary>
        public string Engine { get; set; }
        public string OllamaUrl { get; set; }
        public string OllamaModel { get; set; }
        /// <summary>Optional override of the built-in API license code. Normally empty and not shown in Settings.</summary>
        public string LicenseCode { get; set; }
        /// <summary>Language code for descriptions and class names; blank picks one from the archive.</summary>
        public string Language { get; set; }
        /// <summary>Library the new manufacturer parts are put in. Created if missing. Blank means no library.</summary>
        public string LibraryCode { get; set; }
        /// <summary>When set, the data sheet is copied here and the copy is linked as the part's data sheet.</summary>
        public string CopyDatasheetToFolder { get; set; }
        /// <summary>User data number that receives an "Imported from ..." note; 0 = do not write.</summary>
        public int ImportNoteSlot { get; set; }
        /// <summary>Pin a circuit kind (Coil, MainPole, AuxNO, AuxNC, Changeover, Pin, PowerInput, PowerOutput, Generic) to a circuit type key code.</summary>
        public Dictionary<string, string> CircuitTypeKeyCodes { get; set; }
        /// <summary>Command string the ribbon button runs. Built-in buttons use the '^C^C' prefix.</summary>
        public string RibbonCommand { get; set; }
        public Dictionary<string, string> Mapping { get; set; }

        public AppSettings()
        {
            Engine = EngineRules;
            OllamaUrl = "http://localhost:11434";
            OllamaModel = "qwen2.5vl:7b";
            LicenseCode = "";
            Language = "";
            LibraryCode = "DSI";
            CopyDatasheetToFolder = "";
            ImportNoteSlot = 3;
            CircuitTypeKeyCodes = new Dictionary<string, string>();
            RibbonCommand = "^C^CDATASHEETIMPORT";
            Mapping = DefaultMapping();
        }

        public static Dictionary<string, string> DefaultMapping()
        {
            var m = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in Schema.Fields)
            {
                if (!Schema.IsFixed(f.Key)) m[f.Key] = "none";
            }
            m["description"] = "description";
            m["series"] = "serie";
            m["supplierRef"] = "commercialRef";
            m["ratedVoltage"] = "useVoltage";
            m["frequency"] = "useFrequency";
            m["coilVoltage"] = "controlVoltage";
            m["width"] = "width";
            m["height"] = "height";
            m["depth"] = "depth";
            m["weight"] = "weight";
            m["ratedCurrent"] = "user1";
            m["ratedPower"] = "user2";
            return m;
        }

        public string MapTarget(string fieldKey)
        {
            string t;
            return Mapping != null && Mapping.TryGetValue(fieldKey, out t) && !string.IsNullOrEmpty(t) ? t : "none";
        }

        public string EngineLabel
        {
            get { return string.Equals(Engine, EngineOllama, StringComparison.OrdinalIgnoreCase) ? "local AI model (Ollama)" : "offline rules"; }
        }

        static string PathOnDisk { get { return Path.Combine(Log.Dir, "settings.json"); } }

        /// <summary>Where the per-user settings file lives.</summary>
        public static string FilePath { get { return PathOnDisk; } }

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(PathOnDisk))
                {
                    // Older settings files (with an API key and model name) still load; unknown entries are ignored.
                    var s = new JavaScriptSerializer().Deserialize<AppSettings>(File.ReadAllText(PathOnDisk, Encoding.UTF8));
                    if (s != null)
                    {
                        if (s.Mapping == null) s.Mapping = new Dictionary<string, string>();
                        foreach (var kv in DefaultMapping())
                        {
                            if (!s.Mapping.ContainsKey(kv.Key)) s.Mapping[kv.Key] = kv.Value;
                        }
                        if (string.IsNullOrWhiteSpace(s.Engine)) s.Engine = EngineRules;
                        if (string.IsNullOrWhiteSpace(s.OllamaUrl)) s.OllamaUrl = "http://localhost:11434";
                        if (string.IsNullOrWhiteSpace(s.OllamaModel)) s.OllamaModel = "qwen2.5vl:7b";
                        if (s.LicenseCode == null) s.LicenseCode = "";
                        if (s.Language == null) s.Language = "";
                        if (s.CircuitTypeKeyCodes == null) s.CircuitTypeKeyCodes = new Dictionary<string, string>();
                        if (s.CopyDatasheetToFolder == null) s.CopyDatasheetToFolder = "";
                        if (s.LibraryCode == null) s.LibraryCode = "DSI";
                        if (string.IsNullOrWhiteSpace(s.RibbonCommand)) s.RibbonCommand = "^C^CDATASHEETIMPORT";
                        return s;
                    }
                }
            }
            catch (Exception ex) { Log.Write("Could not read settings: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                File.WriteAllText(PathOnDisk, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
            }
            catch (Exception ex) { Log.Write("Could not save settings: " + ex.Message); }
        }
    }
}
