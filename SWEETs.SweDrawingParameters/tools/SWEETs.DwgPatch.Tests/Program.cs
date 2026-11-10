using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SWEETs.SweDrawingParameters.AddIn.Dwg;
using SWEETs.SweDrawingParameters.AddIn.Model;

namespace SWEETs.DwgPatch.Tests
{
    /// <summary>
    ///   SWEETs.DwgPatch.Tests                 run the reference cases in testdata (expected SHA-256 from the prototype)
    ///   SWEETs.DwgPatch.Tests scan &lt;folder&gt;   read-only: for every .ewg/.dwg below the folder, read the values and patch
    ///                                     a copy IN MEMORY, then re-read it (nothing is written to disk)
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            try
            {
                return args.Length >= 2 && args[0] == "scan" ? Scan(args[1]) : Reference();
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL " + ex);
                return 2;
            }
        }

        private static DrawingParameters P(bool? snapOn = null, double? snap = null, bool? gridOn = null, double? grid = null,
                                           double[] zoneMin = null, double[] zoneMax = null, bool? ortho = null) =>
            new DrawingParameters
            {
                SnapOn = snapOn, SnapX = snap, SnapY = snap, GridOn = gridOn, GridX = grid, GridY = grid,
                ZoneMinX = zoneMin?[0], ZoneMinY = zoneMin?[1], ZoneMaxX = zoneMax?[0], ZoneMaxY = zoneMax?[1], OrthoOn = ortho,
            };

        private static int Reference()
        {
            var cases = new List<(string file, DrawingParameters request, string sha)>
            {
                ("orig_26.ewg", P(false, 0.1, true, 0.3, new[] { -1.5, 0 }, new[] { 21.25, 13.5 }, false),
                    "cb4503908ec564bf87d50d614431488c819d60906f42715855791c76cbc0f6e3"),
                ("orig_3214.ewg", P(true, 0.125, true, 0.25, new[] { 0.0, 0 }, new[] { 17.0, 11 }, false),
                    "e270a84ffc6877feca769d85436ca45d05a39ab193cd9bac3f878cf466573ea5"),
                ("orig_6672.ewg", P(true, 0.2, true, 0.4, new[] { 0.0, 0 }, new[] { 34.0, 22 }, true),
                    "37c234738ecc02c0c922c8a1205fa9d590c297edf04050ad740c146fc230766f"),
                ("orig_default.ewg", P(true, 0.25, false, 0.5),
                    "cf0d0e1c6b7306d5c3470325019875bc807d4535479a233438b3615e356ec5cd"),
                ("orig_tb.ewg", P(snap: 2.5, grid: 5, zoneMax: new[] { 420.0, 297 }),
                    "af5fe30284b8a041c3e5b20338b476ca00fed5c27dbd98b9fc6a4738dc026fab"),
                // page 7 needs 32 more bytes: exercises the "grow and move the rest of the file" path
                ("orig_3214.ewg", P(false, 0.123, true, 0.246, new[] { -1.0, -2.0 }, new[] { 33.5, 44.5 }, true),
                    "607f59edcaf812d6c6b63bd1e39382f10962e330d72088ef8eb4ead4cdaa0bb9"),
            };
            int failures = 0;
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testdata");
            foreach (var (file, request, sha) in cases)
            {
                var bytes = File.ReadAllBytes(Path.Combine(dir, file));
                var result = DwgParameterPatcher.Patch(bytes, request);
                var hash = Sha(result.Bytes);
                var after = DwgParameterPatcher.ReadValues(result.Bytes);
                var mismatches = request.Mismatches(after);
                bool ok = hash == sha && mismatches.Count == 0;
                if (!ok) failures++;
                Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {file}: pages [{string.Join(",", result.RewrittenPages)}] grown [{string.Join(",", result.GrownPages)}] sha {(hash == sha ? "identical to prototype" : "DIFFERENT " + hash)}" +
                                  (mismatches.Count > 0 ? " values: " + string.Join("; ", mismatches) : ""));
                Console.WriteLine($"     before: snap {result.Before.SnapText}, grid {result.Before.GridText}, zone {result.Before.ZoneText}, ortho {DrawingParameters.OnOff(result.Before.OrthoOn)}");
                Console.WriteLine($"     after : snap {after.SnapText}, grid {after.GridText}, zone {after.ZoneText}, ortho {DrawingParameters.OnOff(after.OrthoOn)}");
            }
            Console.WriteLine(failures == 0 ? "ALL REFERENCE CASES PASSED" : $"{failures} REFERENCE CASE(S) FAILED");
            return failures == 0 ? 0 : 1;
        }

        private static int Scan(string folder)
        {
            int ok = 0, failed = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "*.*", SearchOption.AllDirectories)
                                          .Where(p => p.EndsWith(".ewg", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    var before = DwgParameterPatcher.ReadValues(bytes);
                    var request = P(!(before.SnapOn ?? false), 0.123, !(before.GridOn ?? false), 0.246,
                                    new[] { -1.0, -2.0 }, new[] { 33.5, 44.5 }, !(before.OrthoOn ?? false));
                    var result = DwgParameterPatcher.Patch(bytes, request);
                    var after = DwgParameterPatcher.ReadValues(result.Bytes);
                    var mismatches = request.Mismatches(after);
                    // restoring the original values must give back the original values, and - when no page had to
                    // grow - the original file byte for byte
                    var back = DwgParameterPatcher.Patch(result.Bytes, before);
                    var restored = DwgParameterPatcher.ReadValues(back.Bytes);
                    bool identical = result.GrownPages.Count > 0 ? before.Mismatches(restored).Count == 0 : back.Bytes.SequenceEqual(bytes);
                    if (mismatches.Count == 0 && identical)
                    {
                        ok++;
                        Console.WriteLine($"OK   {path}  ({Version(bytes)}; snap {before.SnapText}; grid {before.GridText}; zone {before.ZoneText}" +
                                          (result.GrownPages.Count > 0 ? $"; grown pages {string.Join(",", result.GrownPages)})" : ")"));
                    }
                    else
                    {
                        failed++;
                        Console.WriteLine($"FAIL {path}: {(identical ? "" : "restore not byte-identical; ")}{string.Join("; ", mismatches)}");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"FAIL {path}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Console.WriteLine($"SCAN DONE: {ok} ok, {failed} failed (nothing was written)");
            return failed == 0 ? 0 : 1;
        }

        private static string Version(byte[] b) => System.Text.Encoding.ASCII.GetString(b, 0, 6);

        private static string Sha(byte[] b)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(b).Select(x => x.ToString("x2")));
        }
    }
}
