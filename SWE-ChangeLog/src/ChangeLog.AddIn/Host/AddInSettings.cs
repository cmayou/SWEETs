using System;
using System.IO;
using System.Runtime.Serialization;
using ChangeLog.Core.Util;

namespace ChangeLog.AddIn.Host
{
    /// <summary>
    /// Per-user settings (%APPDATA%\SWE ChangeLog\settings.json). If absent, machine defaults
    /// from %ProgramData%\SWE ChangeLog\settings.json (deployed by IT) are used.
    /// </summary>
    [DataContract]
    public sealed class AddInSettings
    {
        public static string UserFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWE ChangeLog");
        public static string MachineFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SWE ChangeLog");
        public static string LocalFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWE ChangeLog");

        /// <summary>Language code passed to SWE getDescription(...) calls.</summary>
        [DataMember(Name = "language", Order = 0)] public string Language { get; set; } = "en";

        /// <summary>Log individual wire segments (very noisy). They are always tracked for the visual compare.</summary>
        [DataMember(Name = "logWireSegments", Order = 1)] public bool LogWireSegments { get; set; }

        /// <summary>Optional root folder for logs instead of "&lt;project folder&gt;\ChangeLog".</summary>
        [DataMember(Name = "storeRootOverride", Order = 2)] public string StoreRootOverride { get; set; }

        /// <summary>Optional append-only folder for external integrity anchors.</summary>
        [DataMember(Name = "anchorFolder", Order = 3)] public string AnchorFolder { get; set; }

        [DataMember(Name = "quietWindowSeconds", Order = 4)] public double QuietWindowSeconds { get; set; } = 1.5;

        /// <summary>Milliseconds of UI-thread time per indexing slice (keeps SWE responsive).</summary>
        [DataMember(Name = "indexSliceMs", Order = 5)] public int IndexSliceMs { get; set; } = 40;

        public static AddInSettings Load()
        {
            foreach (var folder in new[] { UserFolder, MachineFolder })
            {
                var p = Path.Combine(folder, "settings.json");
                if (!File.Exists(p)) continue;
                try
                {
                    return Json.Deserialize<AddInSettings>(File.ReadAllText(p)) ?? new AddInSettings();
                }
                catch (SerializationException e)
                {
                    Log.Warn("Settings file unreadable, using defaults: " + p + " (" + e.Message + ")");
                }
            }

            return new AddInSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(UserFolder);
            var p = Path.Combine(UserFolder, "settings.json");
            var tmp = p + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(this));
            if (File.Exists(p)) File.Replace(tmp, p, null);
            else File.Move(tmp, p);
        }

        public AddInSettings Clone() => Json.Deserialize<AddInSettings>(Json.Serialize(this));
    }

    /// <summary>Minimal diagnostics log (%LOCALAPPDATA%\SWE ChangeLog\addin.log, rolled at 2 MB).</summary>
    public static class Log
    {
        private static readonly object Gate = new object();

        public static string FilePath => Path.Combine(AddInSettings.LocalFolder, "addin.log");

        public static void Info(string m) => Write("INFO ", m);
        public static void Warn(string m) => Write("WARN ", m);
        public static void Error(string m, Exception e) => Write("ERROR", m + (e == null ? string.Empty : Environment.NewLine + e));

        private static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AddInSettings.LocalFolder);
                    var fi = new FileInfo(FilePath);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Copy(FilePath, FilePath + ".1", true);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024) File.Delete(FilePath);
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message + Environment.NewLine);
                }
            }
            catch (IOException)
            {
                // logging must never break the host
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
