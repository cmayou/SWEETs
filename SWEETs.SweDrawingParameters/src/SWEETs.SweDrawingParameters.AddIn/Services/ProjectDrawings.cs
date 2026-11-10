using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SWEETs.SweDrawingParameters.AddIn.Infrastructure;
using SWEETs.SweDrawingParameters.AddIn.Interop;
using C = SWEETs.SweDrawingParameters.AddIn.Interop.SweContract;

namespace SWEETs.SweDrawingParameters.AddIn.Services
{
    /// <summary>A drawing (IEwProjectFileX) of the active project.</summary>
    public sealed class DrawingInfo
    {
        public object File;            // IEwProjectFileX
        public int Id;
        public int Position;
        public string FilePath;
        public string FileType;        // EwFileType name, e.g. kFileFolio
        public string TypeLabel;
        public string Description;
    }

    public sealed class ProjectInfo
    {
        public object Project;         // IEwProjectX
        public int Id;
        public string Name;
        public string Language;
        public List<DrawingInfo> Drawings = new List<DrawingInfo>();
    }

    /// <summary>Lists the drawings of the project open in SOLIDWORKS Electrical.</summary>
    public static class ProjectDrawings
    {
        /// <summary>
        /// EwFileType values whose files are drawings (DWG content saved as .ewg) with grid/snap settings.
        /// Folders, documents, 3D/SOLIDWORKS files, appendices and exported files are left out.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> DrawingTypes = new Dictionary<string, string>
        {
            ["kFileFolio"] = "Schematic",
            ["kFileLineDiagram"] = "Line diagram",
            ["kFileCoverPage"] = "Cover page",
            ["kFileMixed"] = "Mixed",
            ["kFilePID"] = "P&ID",
            ["kFileFluid"] = "Fluid",
            ["kFile2DCabinetLayout"] = "2D cabinet layout",
            ["kFileTerminal"] = "Terminal drawing",
            ["kFileBOM"] = "Report drawing",
            ["kFileDesignRule"] = "Design rule",
        };

        /// <summary>Returns null when no project is open.</summary>
        public static ProjectInfo LoadCurrent(object application)
        {
            if (!SweApi.TryCall(application, C.AppGetProjectCurrent, out var project) || project == null) return null;

            var info = new ProjectInfo
            {
                Project = project,
                Id = SweApi.Call<int>(project, C.ProjGetId),
                Name = SweApi.TryCall(project, C.ProjGetName, out var n) ? n as string : null,
                Language = "en",
            };
            if (SweApi.TryCall(project, C.ProjGetConfiguration, out var cfg) && cfg != null &&
                SweApi.TryCall(cfg, C.ConfigGetLanguage, out var lang) && lang is string l && l.Length > 0)
                info.Language = l;

            var fm = SweApi.Call(project, C.ProjGetFileManager);
            var files = SweApi.Call(fm, C.FileMgrGetArray) as IEnumerable;
            if (files == null) return info;

            foreach (var f in files)
            {
                if (f == null) continue;
                try
                {
                    var type = SweApi.Call(f, C.FileGetType)?.ToString();
                    if (type == null || !DrawingTypes.TryGetValue(type, out var label)) continue;
                    var path = SweApi.TryCall(f, C.FileGetPath, out var p) ? p as string : null;
                    if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetExtension(path), ".ewg", StringComparison.OrdinalIgnoreCase)) continue;

                    info.Drawings.Add(new DrawingInfo
                    {
                        File = f,
                        Id = SweApi.Call<int>(f, C.FileGetId),
                        Position = SweApi.TryCall(f, C.FileGetPosition, out var pos) && pos is int i ? i : 0,
                        FilePath = path,
                        FileType = type,
                        TypeLabel = label,
                        Description = SweApi.TryCall(f, C.FileGetDescription, out var d, info.Language) ? d as string : null,
                    });
                }
                catch (Exception ex) { FileLog.Write("drawings", "Skipped a project file: " + ex.Message); }
            }
            info.Drawings = info.Drawings.OrderBy(d => d.Position).ThenBy(d => d.Id).ToList();
            FileLog.Write("drawings", $"Project '{info.Name}' (#{info.Id}): {info.Drawings.Count} drawing(s).");
            return info;
        }
    }
}
