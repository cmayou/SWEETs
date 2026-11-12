using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataSheetMfgPartsImport.Model;
using DataSheetMfgPartsImport.Settings;

namespace DataSheetMfgPartsImport.Extraction
{
    public sealed class ExtractionException : Exception
    {
        public ExtractionException(string message) : base(message) { }
    }

    public sealed class ExtractionResult
    {
        public string Title = "";
        public readonly List<PartRecord> Parts = new List<PartRecord>();
        public int PageCount;
        public bool HasTextLayer;
        public readonly List<string> Messages = new List<string>();
        /// <summary>The sheet text as lines, when it has a text layer; used to find terminal marks and the product category.</summary>
        internal List<TextLine> Lines = new List<TextLine>();
    }

    internal interface IExtractor
    {
        Task<ExtractionResult> ExtractAsync(IList<string> files, AppSettings settings, CancellationToken ct);
    }

    internal static class Extractors
    {
        public static IExtractor Create(AppSettings settings)
        {
            return string.Equals(settings.Engine, "ollama", StringComparison.OrdinalIgnoreCase)
                ? (IExtractor)new OllamaExtractor()
                : new RulesExtractor();
        }
    }
}
