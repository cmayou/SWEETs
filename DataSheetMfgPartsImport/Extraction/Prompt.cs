using System.Text;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.Extraction
{
    internal static class Prompt
    {
        /// <summary>Instructions for a language model. Pass the PDF text layer when images are not the only input.</summary>
        public static string Build(string textLayer)
        {
            bool hasText = !string.IsNullOrWhiteSpace(textLayer);
            var sb = new StringBuilder();
            sb.AppendLine("You are a data-extraction engine for an electrical CAD parts library (SOLIDWORKS Electrical manufacturer parts).");
            sb.AppendLine(hasText
                ? "The input is the text of a manufacturer data sheet (extracted from a PDF; tables may be flattened and out of order)" 
                  + " and possibly photos of its pages."
                : "The input is one or more photos of a manufacturer data sheet.");
            sb.AppendLine();
            sb.AppendLine("Return only JSON in this shape: {\"documentTitle\": string, \"parts\": Part[]}");
            sb.AppendLine();
            sb.AppendLine("Rules:");
            sb.AppendLine("- Create one Part per distinct orderable part number (order code). If the sheet covers a family with an ordering table, list each variant (maximum 24), copying shared specifications into every variant and using variant-specific values (current, coil voltage, width, and so on) per variant. If the sheet describes one product, return one Part.");
            sb.AppendLine("- Extract only what is printed on the sheet. Never infer or invent. Use \"\" for anything not stated.");
            sb.AppendLine("- Numeric fields hold the number only, in the unit shown in the schema; convert units when needed (for example 0.5 kg becomes \"0.5\", 4.5 cm becomes \"45\"). Use ranges like \"24-48\" when the sheet gives one. If AC and DC values differ, write \"230 AC / 24 DC\".");
            sb.AppendLine("- \"category\" is the generic component type in plain words, for example \"Contactor\", \"Miniature circuit breaker\", \"Terminal block\", \"Push button\".");
            sb.AppendLine("- \"terminals\": only when terminal or pin markings are listed or labeled (for example A1, A2, 1/L1, 2/T1). \"function\" is a short phrase such as \"coil +\", \"line in\", \"NO contact\".");
            sb.AppendLine("- \"flags\": for each field whose value is uncertain (blurry, ambiguous, conflicting between pages, or read from a drawing), add the field key with a reason of at most eight words. Omit flags when confident.");
            sb.AppendLine("- No commentary outside the JSON.");
            sb.AppendLine();
            sb.AppendLine("Part schema (all keys required):");
            sb.AppendLine("{");
            foreach (var f in Schema.Fields)
            {
                sb.Append("  \"").Append(f.Key).Append("\": string,");
                if (!string.IsNullOrEmpty(f.Unit)) sb.Append(" // numeric value in ").Append(f.Unit);
                else if (!string.IsNullOrEmpty(f.Hint)) sb.Append(" // ").Append(f.Hint);
                sb.AppendLine();
            }
            sb.AppendLine("  \"terminals\": [{\"designation\": string, \"function\": string}],");
            sb.AppendLine("  \"flags\": {\"<fieldKey>\": \"short reason\"}");
            sb.AppendLine("}");
            if (hasText)
            {
                sb.AppendLine();
                sb.AppendLine("DATA SHEET TEXT:");
                sb.AppendLine("<<<");
                sb.AppendLine(textLayer);
                sb.AppendLine(">>>");
            }
            return sb.ToString();
        }
    }
}
