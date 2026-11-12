using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataSheetMfgPartsImport.Model;
using DataSheetMfgPartsImport.Settings;

namespace DataSheetMfgPartsImport.Extraction;

/// <summary>Runs the chosen extraction engine on one file and shapes the result for the importer dialog.</summary>
internal static class ExtractionService
{
    public static async Task<ExtractedFile> ExtractFileAsync(string path, AppSettings settings, CancellationToken ct)
    {
        var result = await Extractors.Create(settings).ExtractAsync(new[] { path }, settings, ct).ConfigureAwait(false);
        // Shaping the parts (category, circuits) is plain computing, so it stays off the UI thread.
        return await Task.Run(() => PartConverter.ToFile(path, result, settings), ct).ConfigureAwait(false);
    }
}

/// <summary>Turns the extractors' <see cref="PartRecord"/>s into <see cref="ExtractedPart"/>s with confidence, category and circuits.</summary>
internal static class PartConverter
{
    public static ExtractedFile ToFile(string path, ExtractionResult r, AppSettings settings)
    {
        var file = new ExtractedFile { FilePath = path, PageCount = r.PageCount, HasTextLayer = r.HasTextLayer };
        foreach (var m in r.Messages) file.Messages.Add(m);

        bool fromModel = string.Equals(settings.Engine, AppSettings.EngineOllama, StringComparison.OrdinalIgnoreCase);
        var lines = r.Lines ?? new List<TextLine>();
        string heading = string.Join("\n", lines.Take(8).Select(l => l.Text));
        string body = string.Join("\n", lines.Select(l => l.Text));
        string docCategory = lines.Count > 0 ? CategoryRules.Detect(heading, body).rule?.Name : null;

        // A sheet with a few parts: trust its own terminal marks (A1-A2, 13-14...). A catalog with many parts
        // would attribute marks from unrelated products to every row, so there the circuits come from each part's
        // own pole count, category and phases instead.
        var circuitLines = r.Parts.Count <= 12 ? lines : new List<TextLine>();

        foreach (var rec in r.Parts)
            file.Parts.Add(ToPart(rec, path, circuitLines, docCategory, fromModel));

        file.Messages.Add(fromModel ? "Read with the local AI model." : "Read with the offline rules.");
        return file;
    }

    private static ExtractedPart ToPart(PartRecord rec, string path, List<TextLine> lines, string docCategory, bool fromModel)
    {
        var part = new ExtractedPart { SourceFile = path, Origin = fromModel ? "Local AI model" : "Offline rules" };

        foreach (var (field, key) in PartKeys.All)
        {
            string value = rec.Get(key);
            if (field == PartField.WeightG) value = KilogramsToGrams(value);
            if (value.Length == 0) continue;

            var confidence = fromModel ? Confidence.Medium : Confidence.High;
            string source = null;
            if (rec.Flags.TryGetValue(key, out var reason))
            {
                confidence = Regex.IsMatch(reason, "guess|file name", RegexOptions.IgnoreCase) ? Confidence.Low : Confidence.Medium;
                source = reason;
            }
            part[field] = FieldValue.Of(value, confidence, source);
        }

        string printed = rec.Get("category");
        bool accessory = printed.IndexOf("accessor", StringComparison.OrdinalIgnoreCase) >= 0;
        var rule = CategoryRules.FromText(printed + " " + rec.Get("description"));
        part.Category = rule?.Name ?? docCategory;
        part.Preselect = !accessory;

        // Circuits: the data sheet's own terminal marks where it has them, otherwise standard markings for the device.
        if (!accessory)
        {
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string poles = rec.Get("poles");
            if (int.TryParse(poles, out var n) && n > 0)
            {
                if (part.Category == "Connector") attributes["pins"] = n.ToString();
                else attributes["poles"] = n.ToString();
            }
            var phases = Regex.Match(rec.Get("otherSpecs"), @"Input:\s*(\d)-phase", RegexOptions.IgnoreCase);
            if (phases.Success) attributes["phases"] = phases.Groups[1].Value;

            part.Circuits = CircuitDetector.Detect(lines, part.Category, rec.Get("coilVoltage").Length > 0, attributes);
        }
        return part;
    }

    /// <summary>The extractors report weight in kilograms; SOLIDWORKS Electrical stores grams.</summary>
    private static string KilogramsToGrams(string kg)
    {
        if (string.IsNullOrWhiteSpace(kg)) return "";
        return Units.TryParseNumber(kg, out var v) ? Units.Format(v * 1000) : kg;
    }
}
