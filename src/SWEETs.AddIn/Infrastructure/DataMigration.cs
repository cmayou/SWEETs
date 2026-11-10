using System;
using System.IO;
using System.Text;

namespace SWEETs.AddIn.Infrastructure
{
    /// <summary>
    /// One-time copy of the tools' data from the TEW folders used before the TEW → SWEETs rename:
    /// <code>
    /// TEW\SweDrawingParameters → SWEETs\DrawingParameters   (settings, logs, drawing backups)
    /// TEW\SweLineStyles        → SWEETs\LineStyles          (settings, logs, Support-file backups)
    /// TEW\SwePdfSchematic      → SWEETs\PdfSchematic        (settings, logs, learned mappings, symbol catalog)
    /// TEW\SWEETs               → SWEETs                     (SWEETs settings.ini, logs)
    /// </code>
    /// under %LocalAppData%, %AppData% and %ProgramData% (the installer copies %ProgramData% too, with admin rights).
    /// Only files the new folder does not already have are copied; the TEW folders are left untouched.
    /// Runs once per Windows user (marker file %LocalAppData%\SWEETs\.migrated-from-tew).
    /// </summary>
    public static class DataMigration
    {
        private static readonly (string oldRel, string newRel)[] Moves =
        {
            (@"TEW\SweDrawingParameters", @"SWEETs\DrawingParameters"),
            (@"TEW\SweLineStyles",        @"SWEETs\LineStyles"),
            (@"TEW\SwePdfSchematic",      @"SWEETs\PdfSchematic"),
            (@"TEW\SWEETs",               @"SWEETs"),
        };

        private static readonly Environment.SpecialFolder[] Roots =
        {
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.CommonApplicationData,
        };

        private static string MarkerPath => Path.Combine(AppPaths.LocalRoot, ".migrated-from-tew");

        /// <summary>Copies what is missing and returns a summary for the log, or null when there was nothing to do.</summary>
        public static string Run()
        {
            try
            {
                if (File.Exists(MarkerPath)) return null;

                var report = new StringBuilder();
                int copied = 0, failed = 0;
                foreach (var root in Roots)
                {
                    var rootPath = Environment.GetFolderPath(root);
                    if (string.IsNullOrEmpty(rootPath)) continue;
                    foreach (var (oldRel, newRel) in Moves)
                    {
                        var src = Path.Combine(rootPath, oldRel);
                        if (!Directory.Exists(src)) continue;
                        var dst = Path.Combine(rootPath, newRel);
                        int c = 0, f = 0;
                        // A folder SWEETs cannot write to (e.g. %ProgramData% without the installer) must not stop the rest.
                        try { CopyMissing(src, dst, ref c, ref f, report); }
                        catch (Exception ex) { f++; report.AppendLine($"  {dst}: {ex.Message}"); }
                        copied += c; failed += f;
                        report.AppendLine($"{src} → {dst}: {c} file(s) copied{(f > 0 ? $", {f} failed" : "")}.");
                    }
                }

                Directory.CreateDirectory(AppPaths.LocalRoot);
                File.WriteAllText(MarkerPath, $"{DateTime.Now:s}{Environment.NewLine}{report}", Encoding.UTF8);
                return copied == 0 && failed == 0 ? null
                     : $"Copied {copied} file(s) from the TEW data folders{(failed > 0 ? $", {failed} could not be copied" : "")}.{Environment.NewLine}{report}";
            }
            catch (Exception ex)
            {
                // Never stop SWEETs from loading; the next start tries again (no marker written).
                return "Data migration from the TEW folders failed: " + ex.Message;
            }
        }

        private static void CopyMissing(string src, string dst, ref int copied, ref int failed, StringBuilder report)
        {
            Directory.CreateDirectory(dst);
            foreach (var file in Directory.GetFiles(src))
            {
                var target = Path.Combine(dst, Path.GetFileName(file));
                if (File.Exists(target)) continue;
                try { File.Copy(file, target); copied++; }
                catch (Exception ex) { failed++; report.AppendLine($"  {file}: {ex.Message}"); }
            }
            foreach (var dir in Directory.GetDirectories(src))
                CopyMissing(dir, Path.Combine(dst, Path.GetFileName(dir)), ref copied, ref failed, report);
        }
    }
}
