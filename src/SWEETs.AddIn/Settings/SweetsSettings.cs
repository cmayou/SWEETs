using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SWEETs.AddIn.Infrastructure;

namespace SWEETs.AddIn.Settings
{
    /// <summary>
    /// Which SWEETs tools are turned on, per Windows user, in %AppData%\SWEETs\settings.ini:
    /// <code>
    /// DrawingParameters=true
    /// LineStyles=true
    /// PdfToSchematic=false
    /// DatasheetImporter=true
    /// </code>
    /// A tool missing from the file is on.
    /// </summary>
    public sealed class SweetsSettings
    {
        private readonly Dictionary<string, bool> _enabled = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        public static string FilePath => Path.Combine(AppPaths.RoamingRoot, "settings.ini");

        public bool IsEnabled(string toolId) => !_enabled.TryGetValue(toolId, out var on) || on;

        public void SetEnabled(string toolId, bool on) => _enabled[toolId] = on;

        public SweetsSettings Clone()
        {
            var copy = new SweetsSettings();
            foreach (var kv in _enabled) copy._enabled[kv.Key] = kv.Value;
            return copy;
        }

        public static SweetsSettings Load()
        {
            var s = new SweetsSettings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (var raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    if (bool.TryParse(value, out var on)) s._enabled[key] = on;
                    else if (value == "1" || value == "0") s._enabled[key] = value == "1";
                }
            }
            catch (Exception ex) { FileLog.Write("settings", "Could not read " + FilePath + ": " + ex.Message); }
            return s;
        }

        public void Save(IEnumerable<string> toolIds)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# SWEETs for SOLIDWORKS Electrical: tools shown on the SWEETs ribbon tab (true = on).");
            foreach (var id in toolIds.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(id + "=" + (IsEnabled(id) ? "true" : "false"));
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
