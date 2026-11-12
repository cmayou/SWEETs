using System;
using System.IO;
using ACadSharp.IO;
using SWEETs.SweDrawingParameters.AddIn.Dwg;
using SWEETs.SweDrawingParameters.AddIn.Model;

namespace SWEETs.SweDrawingParameters.AddIn.Services
{
    /// <summary>
    /// Reads the grid/snap/zone/ortho values of a drawing file (.ewg is a DWG file), read-only.
    /// By default it uses the patcher's own reader (the same code that writes the values) and falls back to ACadSharp;
    /// with useAcadSharp it uses ACadSharp only, as an independent check of a patched file.
    /// </summary>
    public static class DwgParameterReader
    {
        public sealed class Result
        {
            public DrawingParameters Values;
            public string Error;
            /// <summary>False when the patcher cannot update this file (for example an unsupported DWG version).</summary>
            public bool Patchable;
            /// <summary>Why the drawing cannot be updated (when Patchable is false).</summary>
            public string Reason;
            public bool Ok => Error == null;
        }

        public static Result Read(string path, bool useAcadSharp = false)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new Result { Error = "File not found" };
            if (!useAcadSharp)
            {
                try
                {
                    return new Result { Values = DwgParameterPatcher.ReadValues(ReadShared(path)), Patchable = true };
                }
                catch (DwgPatchException ex)
                {
                    // Show the values if ACadSharp can read them, but mark the drawing as not updatable.
                    var fallback = ReadWithAcadSharp(path);
                    return new Result { Values = fallback.Values, Error = fallback.Ok ? null : ex.Message, Patchable = false, Reason = ex.Message };
                }
                catch (Exception ex)
                {
                    return new Result { Error = ex.GetType().Name + ": " + ex.Message };
                }
            }
            return ReadWithAcadSharp(path);
        }

        private static byte[] ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static Result ReadWithAcadSharp(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var doc = DwgReader.Read(fs, new DwgReaderConfiguration { ReadSummaryInfo = false });
                    var p = new DrawingParameters
                    {
                        OrthoOn = doc.Header.OrthoMode,
                        ZoneMinX = doc.Header.ModelSpaceLimitsMin.X,
                        ZoneMinY = doc.Header.ModelSpaceLimitsMin.Y,
                        ZoneMaxX = doc.Header.ModelSpaceLimitsMax.X,
                        ZoneMaxY = doc.Header.ModelSpaceLimitsMax.Y,
                    };
                    if (doc.VPorts.TryGetValue("*Active", out var vp) && vp != null)
                    {
                        p.SnapOn = vp.SnapOn;
                        p.SnapX = vp.SnapSpacing.X;
                        p.SnapY = vp.SnapSpacing.Y;
                        p.GridOn = vp.ShowGrid;
                        p.GridX = vp.GridSpacing.X;
                        p.GridY = vp.GridSpacing.Y;
                    }
                    return new Result { Values = p };
                }
            }
            catch (Exception ex)
            {
                return new Result { Error = ex.GetType().Name + ": " + ex.Message };
            }
        }
    }
}
