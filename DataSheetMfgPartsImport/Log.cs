using System;
using System.IO;

namespace DataSheetMfgPartsImport
{
    internal static class Log
    {
        static readonly object Gate = new object();

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataSheetMfgPartsImport"); }
        }

        public static string FilePath { get { return Path.Combine(Dir, "addin.log"); } }

        /// <summary>Folder that holds the log, for the Settings window.</summary>
        public static string Folder { get { return Dir; } }

        public static void Info(string message) { Write("INFO  " + message); }
        public static void Warn(string message) { Write("WARN  " + message); }
        public static void Error(string message, Exception ex = null) { Write("ERROR " + (ex == null ? message : message + "\r\n" + ex)); }

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    File.AppendAllText(FilePath, DateTime.Now.ToString("s") + "  " + message + Environment.NewLine);
                }
            }
            catch { /* logging must never break the host application */ }
        }
    }
}
