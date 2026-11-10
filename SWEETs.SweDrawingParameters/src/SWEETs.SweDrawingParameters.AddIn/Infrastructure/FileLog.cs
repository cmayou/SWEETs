using System;
using System.IO;

namespace SWEETs.SweDrawingParameters.AddIn.Infrastructure
{
    /// <summary>Rolling daily log in %LocalAppData%\SWEETs\DrawingParameters\logs.</summary>
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWEETs", "DrawingParameters");

        public static string RoamingRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWEETs", "DrawingParameters");

        /// <summary>Per-run backup folder: …\backups\&lt;project&gt;\yyyyMMdd-HHmmss.</summary>
        public static string NewBackupFolder(string projectName)
        {
            var safe = string.Join("_", (projectName ?? "project").Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(LocalRoot, "backups", safe, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }
    }
}
