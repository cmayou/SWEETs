using System;
using System.IO;
using System.Text.Json;
using SWEETs.SwePdfSchematic.Core.Build;
using SWEETs.SwePdfSchematic.Core.Mapping;
using SWEETs.SwePdfSchematic.Core.Recognition;

namespace SWEETs.SwePdfSchematic.Core.Infrastructure
{
    /// <summary>Per-user settings (%AppData%\SWEETs\PdfSchematic\settings.json). Mappings are shared via <see cref="MappingStorePath"/>.</summary>
    public sealed class AppSettings
    {
        public string MappingStorePath { get; set; } = MappingStore.DefaultPath;
        public string RulesOverridePath { get; set; } = Path.Combine(Path.GetDirectoryName(MappingStore.DefaultPath), "rules.json");
        public string LastPdfFolder { get; set; }
        public RecognitionOptions Recognition { get; set; } = new RecognitionOptions();
        public BuildOptions Build { get; set; } = new BuildOptions { TemplateName = "IEC" };

        private static readonly string FilePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWEETs", "PdfSchematic", "settings.json");

        public static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath)) return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
            catch (Exception ex) { FileLog.Write("settings", "Settings unreadable, using defaults: " + ex.Message); }
            return new AppSettings();
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Rolling daily log in %LocalAppData%\SWEETs\PdfSchematic\logs.</summary>
    public static class FileLog
    {
        private static readonly object Lock = new object();
        public static string Folder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWEETs", "PdfSchematic", "logs");

        public static void Write(string area, string message)
        {
            try
            {
                lock (Lock)
                {
                    Directory.CreateDirectory(Folder);
                    File.AppendAllText(Path.Combine(Folder, DateTime.Now.ToString("yyyyMMdd") + ".log"),
                        $"{DateTime.Now:HH:mm:ss.fff} [{area}] {message}{Environment.NewLine}");
                }
            }
            catch { /* logging must never break the host application */ }
        }

        public static void Write(string area, Exception ex) => Write(area, ex.ToString());
    }
}
