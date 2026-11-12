using System;
using System.IO;

namespace SWEETs.AddIn.Infrastructure
{
    /// <summary>
    /// Rolling daily log for the SWEETs host in %LocalAppData%\SWEETs\logs. Each tool keeps writing its own log
    /// where it always did (see the Settings window for the folders).
    /// </summary>
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SWEETs");

        public static string RoamingRoot =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SWEETs");

        public static string AddInFolder => Path.GetDirectoryName(typeof(AppPaths).Assembly.Location);
    }
}
