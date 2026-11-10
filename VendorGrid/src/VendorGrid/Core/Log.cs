using System;
using System.IO;

namespace VendorGrid.Core
{
    public static class Log
    {
        static readonly object Gate = new object();
        static readonly string PathName = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VendorGrid", "vendorgrid.log");

        public static void Info(string message) { Write("INFO ", message); }
        public static void Error(string message, Exception ex = null) { Write("ERROR", ex == null ? message : message + " :: " + ex); }

        static void Write(string level, string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName));
                    var fi = new FileInfo(PathName);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(PathName, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + message + Environment.NewLine);
                }
            }
            catch { /* logging must never break the host application */ }
        }
    }
}
