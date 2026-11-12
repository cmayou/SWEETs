using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SWEETs.SwePdfSchematic.Core.Infrastructure;
using C = SWEETs.SwePdfSchematic.AddIn.Interop.SweContract;

namespace SWEETs.SwePdfSchematic.AddIn.Interop
{
    /// <summary>A project template of the SOLIDWORKS Electrical environment.</summary>
    public sealed class ProjectTemplate
    {
        /// <summary>Name passed to IEwProjectX.insertFromTemplate (file name without the template extension).</summary>
        public string Name { get; set; }
        public string Path { get; set; }
        public DateTime Modified { get; set; }
        public long Bytes { get; set; }
        /// <summary>Standard guessed from the name (IEC, ANSI, JIC, GB…); null when the name does not say.</summary>
        public string Standard { get; set; }
        public string Details => string.Join("  ·  ", new[]
        {
            Standard, Modified == default ? null : Modified.ToString("d"),
            Bytes > 0 ? $"{Bytes / 1024.0:N0} KB" : null,
        }.Where(x => !string.IsNullOrEmpty(x)));
        public override string ToString() => Name;
    }

    /// <summary>
    /// Lists the environment's project templates. EwAPI has no template enumeration, so the files in the folder it
    /// reports for project templates (IEwEnvironmentX.getFolderPath(kFolderPathProjectTemplate)) are listed: template
    /// archives (*.proj.tewzip / *.tewzip), or one sub-folder per template where they are stored unpacked.
    /// </summary>
    public static class SweTemplates
    {
        public sealed class Result
        {
            public List<ProjectTemplate> Templates = new List<ProjectTemplate>();
            public string Folder;
            public string Error;
        }

        private static readonly string[] Extensions = { ".proj.tewzip", ".tewzip" };

        public static Result List(object sweApplication)
        {
            var r = new Result();
            if (sweApplication == null) { r.Error = "SOLIDWORKS Electrical is not connected."; return r; }
            try
            {
                var env = SweApi.Call(sweApplication, C.AppGetEnvironment);
                r.Folder = SweApi.Call(env, C.EnvGetFolderPath, SweApi.EnumValue("EwEnvironmentFolderPathValue", "kFolderPathProjectTemplate")) as string;
                if (string.IsNullOrEmpty(r.Folder) || !Directory.Exists(r.Folder))
                {
                    r.Error = $"Template folder not found: {r.Folder ?? "(not reported)"}";
                    return r;
                }
                foreach (var f in Directory.EnumerateFiles(r.Folder, "*.tewzip", SearchOption.TopDirectoryOnly))
                {
                    var fi = new FileInfo(f);
                    var ext = Extensions.First(e => fi.Name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
                    var name = fi.Name.Substring(0, fi.Name.Length - ext.Length);
                    r.Templates.Add(new ProjectTemplate { Name = name, Path = f, Modified = fi.LastWriteTime, Bytes = fi.Length, Standard = GuessStandard(name) });
                }
                if (r.Templates.Count == 0)
                    foreach (var d in Directory.EnumerateDirectories(r.Folder))
                    {
                        var di = new DirectoryInfo(d);
                        r.Templates.Add(new ProjectTemplate { Name = di.Name, Path = d, Modified = di.LastWriteTime, Standard = GuessStandard(di.Name) });
                    }
                r.Templates = r.Templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
                FileLog.Write("templates", $"{r.Templates.Count} project templates in {r.Folder}: {string.Join(", ", r.Templates.Select(t => t.Name))}");
            }
            catch (Exception ex)
            {
                r.Error = ex.Message;
                FileLog.Write("templates", ex);
            }
            return r;
        }

        public static string GuessStandard(string name)
        {
            var m = Regex.Match(name ?? "", @"(?<![A-Za-z])(IEC|ANSI|JIC|NFPA|GB|DIN|JIS|AS|CSA|NF|UNE)(?![A-Za-z])", RegexOptions.IgnoreCase);
            return m.Success ? m.Value.ToUpperInvariant() : null;
        }
    }
}
