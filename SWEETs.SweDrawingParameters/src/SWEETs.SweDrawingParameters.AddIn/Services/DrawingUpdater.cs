using System;
using System.IO;
using SWEETs.SweDrawingParameters.AddIn.Dwg;
using SWEETs.SweDrawingParameters.AddIn.Infrastructure;
using SWEETs.SweDrawingParameters.AddIn.Interop;
using SWEETs.SweDrawingParameters.AddIn.Model;
using C = SWEETs.SweDrawingParameters.AddIn.Interop.SweContract;

namespace SWEETs.SweDrawingParameters.AddIn.Services
{
    public enum UpdateOutcome { Verified, Unchanged, Skipped, Failed }

    public sealed class UpdateResult
    {
        public UpdateOutcome Outcome;
        public string Message;
        public DrawingParameters After;
    }

    /// <summary>
    /// Updates one drawing file directly with <see cref="DwgParameterPatcher"/> (the SOLIDWORKS Electrical API has no
    /// call for grid/snap):
    /// <list type="number">
    /// <item>skip drawings that are open in SOLIDWORKS Electrical or locked by another user;</item>
    /// <item>patch the bytes in memory;</item>
    /// <item>verify the patched bytes twice: with the patcher's own reader and with ACadSharp (independent);</item>
    /// <item>copy the original to the backup folder, write a temporary file next to the drawing, verify it again,
    /// then swap it in with File.Replace.</item>
    /// </list>
    /// If any check fails, the drawing on disk is left exactly as it was.
    /// </summary>
    public sealed class DrawingUpdater
    {
        private readonly string _backupFolder;

        public DrawingUpdater(string backupFolder) { _backupFolder = backupFolder; }

        /// <summary>
        /// SOLIDWORKS Electrical checks (COM: call on SWE's UI thread). Returns a skip result, or null when the drawing
        /// can be updated.
        /// </summary>
        public static UpdateResult CheckAccess(DrawingInfo d)
        {
            var log = $"#{d.Id} {Path.GetFileName(d.FilePath)}";
            if (SweApi.TryCall(d.File, C.FileIsOpen, out var open) && open is bool isOpen && isOpen)
                return Skip(log, "Open in SOLIDWORKS Electrical. Close the drawing and run again.");
            if (SweApi.TryCall(d.File, C.FileIsLockedByAnother, out var locked) && locked is bool isLocked && isLocked)
                return Skip(log, "Locked by another user.");
            return null;
        }

        /// <summary>File work only (no COM), safe to run on a worker thread. Call <see cref="CheckAccess"/> first.</summary>
        public UpdateResult Update(DrawingInfo d, DrawingParameters request)
        {
            var log = $"#{d.Id} {Path.GetFileName(d.FilePath)}";
            string temp = null;
            try
            {
                if (!File.Exists(d.FilePath)) return Fail(log, "Drawing file not found.");

                var original = File.ReadAllBytes(d.FilePath);
                var patch = DwgParameterPatcher.Patch(original, request);
                if (!patch.Changed)
                {
                    FileLog.Write("update", $"{log}: already had the requested values.");
                    return new UpdateResult { Outcome = UpdateOutcome.Unchanged, Message = "Already had these values", After = patch.Before };
                }

                // Check 1: the patcher's own reader on the new bytes.
                var mine = DwgParameterPatcher.ReadValues(patch.Bytes);
                var diff = request.Mismatches(mine);
                if (diff.Count > 0) return Fail(log, "Self-check failed: " + string.Join("; ", diff));

                // Write next to the drawing (same volume, so File.Replace is atomic), then check 2: ACadSharp.
                temp = d.FilePath + ".sweets-tmp";
                File.WriteAllBytes(temp, patch.Bytes);
                var independent = DwgParameterReader.Read(temp, useAcadSharp: true);
                if (!independent.Ok) return Fail(log, "Independent check could not read the patched file: " + independent.Error);
                diff = request.Mismatches(independent.Values);
                if (diff.Count > 0) return Fail(log, "Independent check failed: " + string.Join("; ", diff));

                // Backup, then swap.
                Directory.CreateDirectory(_backupFolder);
                File.WriteAllBytes(Path.Combine(_backupFolder, Path.GetFileName(d.FilePath)), original);
                File.Replace(temp, d.FilePath, null, true);
                temp = null;

                var detail = patch.GrownPages.Count > 0 ? $" ({patch.GrownPages.Count} page(s) enlarged)" : "";
                FileLog.Write("update", $"{log}: verified{detail}. Pages {string.Join(",", patch.RewrittenPages)}. " +
                                        $"Before: snap {patch.Before.SnapText}, grid {patch.Before.GridText}, zone {patch.Before.ZoneText}, ortho {DrawingParameters.OnOff(patch.Before.OrthoOn)}.");
                return new UpdateResult { Outcome = UpdateOutcome.Verified, Message = "Updated and verified", After = mine };
            }
            catch (DwgPatchException ex)
            {
                return Fail(log, ex.Message);
            }
            catch (Exception ex)
            {
                FileLog.Write("update", $"{log}: {ex}");
                return Fail(log, ex.Message);
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch { /* best effort */ }
            }
        }

        private static UpdateResult Skip(string log, string why)
        {
            FileLog.Write("update", $"{log}: skipped — {why}");
            return new UpdateResult { Outcome = UpdateOutcome.Skipped, Message = why };
        }

        private static UpdateResult Fail(string log, string why)
        {
            FileLog.Write("update", $"{log}: FAILED — {why}");
            return new UpdateResult { Outcome = UpdateOutcome.Failed, Message = why + " The drawing was not changed." };
        }
    }
}
