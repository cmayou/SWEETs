using System;
using System.IO;
using System.Xml.Serialization;

namespace SWEETs.SweLineStyles.AddIn.Infrastructure
{
    /// <summary>Rolling daily log in %LocalAppData%\SWEETs\LineStyles\logs.</summary>
    public static class FileLog
    {
        private static readonly object Lock = new object();

        public static string Folder => Path.Combine(AppPaths.LocalRoot, "logs");

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

    public static class AppPaths
    {
        public static string LocalRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWEETs", "LineStyles");

        public static string RoamingRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWEETs", "LineStyles");
    }

    /// <summary>Per-user settings (%AppData%\SWEETs\LineStyles\settings.xml).</summary>
    public sealed class UserSettings
    {
        /// <summary>Support folder chosen by the user; empty = locate automatically.</summary>
        public string SupportFolderOverride { get; set; } = "";
        public double PreviewZoom { get; set; } = 1.0;
        public int ShapeGridDivisor { get; set; } = 4;
        public int ShapeGridExtent { get; set; } = 8;
        public string LastDxfFolder { get; set; } = "";

        private static string FilePath => Path.Combine(AppPaths.RoamingRoot, "settings.xml");

        public static UserSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var s = File.OpenRead(FilePath))
                        return (UserSettings)new XmlSerializer(typeof(UserSettings)).Deserialize(s);
            }
            catch (Exception ex) { FileLog.Write("settings", "Settings unreadable, using defaults: " + ex.Message); }
            return new UserSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                using (var s = File.Create(FilePath))
                    new XmlSerializer(typeof(UserSettings)).Serialize(s, this);
            }
            catch (Exception ex) { FileLog.Write("settings", "Settings not saved: " + ex.Message); }
        }
    }
}
