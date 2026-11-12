using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using SWEETs.SweLineStyles.AddIn.Core;
using SWEETs.SweLineStyles.AddIn.Infrastructure;

namespace SWEETs.SweLineStyles.AddIn.Services
{
    /// <summary>The three files the editor manages, all in SOLIDWORKS Electrical's Support folder.</summary>
    public static class SupportFiles
    {
        public const string LineStyles = "linestyle.xlin";
        public const string ShapeSource = "ltypeshp.shp";
        public const string ShapeCompiled = "ltypeshp.shx";
        public static readonly string[] All = { ShapeCompiled, ShapeSource, LineStyles };

        /// <summary>
        /// Finds the Support folder: the saved override, then next to the running SOLIDWORKS Electrical executable
        /// (…\SOLIDWORKS Electrical\bin\*.exe → …\SOLIDWORKS Electrical\Support), then the default install path.
        /// </summary>
        public static string Locate(string overrideFolder)
        {
            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(overrideFolder)) candidates.Add(overrideFolder);
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                var bin = Path.GetDirectoryName(exe);
                if (bin != null)
                {
                    candidates.Add(Path.Combine(Path.GetDirectoryName(bin) ?? bin, "Support"));
                    candidates.Add(Path.Combine(bin, "Support"));
                }
            }
            catch (Exception ex) { FileLog.Write("library", "Process path: " + ex.Message); }
            var pf = Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            candidates.Add(Path.Combine(pf, @"SOLIDWORKS Corp\SOLIDWORKS Electrical\Support"));

            foreach (var c in candidates)
                if (File.Exists(Path.Combine(c, LineStyles))) return Path.GetFullPath(c);
            throw new FileNotFoundException("linestyle.xlin was not found. Checked:\n" + string.Join("\n", candidates.Distinct()));
        }
    }

    /// <summary>Everything read from the Support folder at one moment.</summary>
    public sealed class LibrarySnapshot
    {
        public string Folder;
        public XlinFile Xlin;
        public ShpFile Shp;
        public string ShxSignature = ShxFile.TraceCadSignature;
        public readonly List<string> Notes = new List<string>();
        public readonly Dictionary<string, byte[]> Original = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class SaveResult
    {
        public bool Cancelled;
        public bool Elevated;
        public string BackupFolder;
        public List<string> Written = new List<string>();
    }

    public static class LineStyleLibrary
    {
        /// <summary>The .shp is read as Windows-1252 (ASCII content; comments may hold accented characters).</summary>
        private static readonly Encoding ShpEncoding = Encoding.GetEncoding(1252);

        public static LibrarySnapshot Load(string folder)
        {
            var snap = new LibrarySnapshot { Folder = folder };
            foreach (var f in SupportFiles.All)
            {
                var p = Path.Combine(folder, f);
                if (File.Exists(p)) snap.Original[f] = File.ReadAllBytes(p);
            }

            snap.Xlin = XlinFile.Load(snap.Original[SupportFiles.LineStyles]);

            List<ShapeDefinition> shx = null;
            if (snap.Original.TryGetValue(SupportFiles.ShapeCompiled, out var shxBytes))
            {
                try { shx = ShxFile.Read(shxBytes, out snap.ShxSignature); }
                catch (Exception ex) { snap.Notes.Add($"{SupportFiles.ShapeCompiled} could not be read ({ex.Message}); it will be rebuilt from {SupportFiles.ShapeSource} on save."); }
            }

            if (snap.Original.TryGetValue(SupportFiles.ShapeSource, out var shpBytes))
            {
                snap.Shp = ShpFile.Parse(ShpEncoding.GetString(shpBytes));
                snap.Notes.AddRange(snap.Shp.Warnings);
                if (shx != null && !SameShapes(snap.Shp.Shapes, shx))
                    snap.Notes.Add($"{SupportFiles.ShapeSource} and {SupportFiles.ShapeCompiled} do not match. The editor shows {SupportFiles.ShapeSource}; saving rebuilds {SupportFiles.ShapeCompiled} from it.");
            }
            else
            {
                snap.Shp = new ShpFile { Header = ";;;\r\n;;;  ltypeshp.SHP - shapes for complex linetypes\r\n;;;\r\n\r\n" };
                if (shx != null) snap.Shp.Shapes.AddRange(shx);
                snap.Notes.Add($"{SupportFiles.ShapeSource} is missing; shapes were read from {SupportFiles.ShapeCompiled} and the source file will be created on save.");
            }
            return snap;
        }

        private static bool SameShapes(List<ShapeDefinition> a, List<ShapeDefinition> b)
        {
            if (a.Count != b.Count) return false;
            var bb = b.ToDictionary(s => s.Number);
            return a.All(s => bb.TryGetValue(s.Number, out var o) && o.Name == s.Name && o.Bytes.SequenceEqual(s.Bytes));
        }

        public static byte[] EncodeShp(ShpFile shp) => ShpEncoding.GetBytes(shp.ToText());

        // ------------------------------------------------------------------ save

        /// <summary>
        /// Writes changed files to the Support folder. The current files are first copied to a time-stamped backup
        /// (%LocalAppData%\SWEETs\LineStyles\backups). If the folder is not writable (Program Files), the copy runs in an
        /// elevated PowerShell (one UAC prompt). Shape files are written before linestyle.xlin, and every file is read
        /// back and compared afterwards.
        /// </summary>
        public static SaveResult Save(string folder, IDictionary<string, byte[]> files, string reason)
        {
            var result = new SaveResult();
            var changed = files.Where(kv => !(File.Exists(Path.Combine(folder, kv.Key)) && File.ReadAllBytes(Path.Combine(folder, kv.Key)).SequenceEqual(kv.Value)))
                               .OrderBy(kv => Array.IndexOf(SupportFiles.All, kv.Key)).ToList();
            if (changed.Count == 0) return result;

            result.BackupFolder = Backup(folder, reason);

            var stage = Path.Combine(AppPaths.LocalRoot, "staging", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(stage);
            var copies = new List<(string src, string dst)>();
            foreach (var kv in changed)
            {
                var src = Path.Combine(stage, kv.Key);
                File.WriteAllBytes(src, kv.Value);
                copies.Add((src, Path.Combine(folder, kv.Key)));
            }

            try
            {
                foreach (var (src, dst) in copies) CopyKeepingAttributes(src, dst);
                FileLog.Write("save", "Written directly: " + string.Join(", ", changed.Select(c => c.Key)));
            }
            catch (UnauthorizedAccessException ex)
            {
                FileLog.Write("save", "Direct write refused (" + ex.Message + "); elevating.");
                if (!ElevatedCopy(copies, stage)) { result.Cancelled = true; return result; }
                result.Elevated = true;
            }

            foreach (var kv in changed)
            {
                var written = File.ReadAllBytes(Path.Combine(folder, kv.Key));
                if (!written.SequenceEqual(kv.Value))
                    throw new IOException($"{kv.Key} was not written correctly. The previous files are in {result.BackupFolder}.");
                result.Written.Add(kv.Key);
            }
            TryDelete(stage);
            return result;
        }

        private static void CopyKeepingAttributes(string src, string dst)
        {
            var attrs = File.Exists(dst) ? File.GetAttributes(dst) : FileAttributes.Normal;
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, attrs & ~FileAttributes.ReadOnly);
            try { File.Copy(src, dst, true); }
            finally { if ((attrs & FileAttributes.ReadOnly) != 0 && File.Exists(dst)) File.SetAttributes(dst, attrs); }
        }

        /// <summary>Runs the copies in an elevated PowerShell. Returns false when the user declined the UAC prompt.</summary>
        private static bool ElevatedCopy(List<(string src, string dst)> copies, string stage)
        {
            string Q(string s) => "'" + s.Replace("'", "''") + "'";
            var log = Path.Combine(stage, "elevated.log");
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("try {");
            foreach (var (src, dst) in copies)
            {
                sb.AppendLine($"  $d = {Q(dst)}; $ro = $false");
                sb.AppendLine("  if (Test-Path -LiteralPath $d) { $i = Get-Item -LiteralPath $d; $ro = $i.IsReadOnly; if ($ro) { $i.IsReadOnly = $false } }");
                sb.AppendLine($"  Copy-Item -LiteralPath {Q(src)} -Destination $d -Force");
                sb.AppendLine("  if ($ro) { (Get-Item -LiteralPath $d).IsReadOnly = $true }");
            }
            sb.AppendLine("  exit 0");
            sb.AppendLine($"}} catch {{ $_ | Out-String | Set-Content -LiteralPath {Q(log)}; exit 1 }}");
            var script = Path.Combine(stage, "copy.ps1");
            File.WriteAllText(script, sb.ToString(), new UTF8Encoding(true));

            var psi = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            try
            {
                using (var p = Process.Start(psi))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                        throw new IOException("The elevated copy failed: " + (File.Exists(log) ? File.ReadAllText(log).Trim() : $"exit code {p.ExitCode}"));
                }
                FileLog.Write("save", "Elevated copy done: " + string.Join(", ", copies.Select(c => Path.GetFileName(c.dst))));
                return true;
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                FileLog.Write("save", "UAC prompt declined.");
                return false;
            }
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch { /* staging is in %LocalAppData%; leaving it is harmless */ }
        }

        // ------------------------------------------------------------------ backups

        public static string BackupRoot => Path.Combine(AppPaths.LocalRoot, "backups");

        private static string Backup(string folder, string reason)
        {
            var dir = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            foreach (var f in SupportFiles.All)
            {
                var p = Path.Combine(folder, f);
                if (File.Exists(p)) File.Copy(p, Path.Combine(dir, f), true);
            }
            File.WriteAllText(Path.Combine(dir, "backup.txt"),
                $"Folder: {folder}\r\nCreated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\nUser: {Environment.UserName}\r\nReason: {reason}\r\n");
            return dir;
        }

        public sealed class BackupInfo
        {
            public string Folder { get; set; }
            public DateTime Created { get; set; }
            public string Reason { get; set; }
            public string Files { get; set; }
            public string Display => Created.ToString("yyyy-MM-dd  HH:mm:ss");
        }

        public static List<BackupInfo> ListBackups()
        {
            var list = new List<BackupInfo>();
            if (!Directory.Exists(BackupRoot)) return list;
            foreach (var d in Directory.GetDirectories(BackupRoot))
            {
                var info = new BackupInfo { Folder = d, Created = Directory.GetCreationTime(d), Reason = "" };
                var txt = Path.Combine(d, "backup.txt");
                if (File.Exists(txt))
                    foreach (var line in File.ReadAllLines(txt))
                        if (line.StartsWith("Reason: ", StringComparison.Ordinal)) info.Reason = line.Substring(8);
                info.Files = string.Join(", ", SupportFiles.All.Where(f => File.Exists(Path.Combine(d, f))));
                list.Add(info);
            }
            return list.OrderByDescending(b => b.Created).ToList();
        }

        /// <summary>Copies a backup's files back into the Support folder (itself backed up first).</summary>
        public static SaveResult Restore(string folder, BackupInfo backup)
        {
            var files = SupportFiles.All.Where(f => File.Exists(Path.Combine(backup.Folder, f)))
                                        .ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(backup.Folder, f)));
            return Save(folder, files, $"Before restoring the backup of {backup.Display}");
        }
    }
}
