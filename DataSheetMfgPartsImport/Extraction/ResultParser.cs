using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.Extraction
{
    /// <summary>Turns the JSON a language model returns into part records.</summary>
    internal static class ResultParser
    {
        public static ExtractionResult Parse(string text, string sourceFile, bool truncated)
        {
            if (truncated)
                throw new ExtractionException("The model ran out of room before finishing. Use fewer pages, or a model with a larger context.");

            text = text ?? "";
            int a = text.IndexOf('{');
            int z = text.LastIndexOf('}');
            if (a < 0 || z <= a) throw new ExtractionException("The model did not return data. Try again, or use a more capable model.");

            var ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };
            Dictionary<string, object> obj;
            try { obj = ser.DeserializeObject(text.Substring(a, z - a + 1)) as Dictionary<string, object>; }
            catch (Exception ex) { throw new ExtractionException("The model's answer could not be parsed. Try again. " + ex.Message); }
            if (obj == null) throw new ExtractionException("The model's answer could not be parsed. Try again.");

            var result = new ExtractionResult();
            result.Title = obj.ContainsKey("documentTitle") ? Str(obj["documentTitle"]) : "";

            // Some models return a single part object instead of {"parts": [...]}.
            IEnumerable parts = obj.ContainsKey("parts") ? obj["parts"] as IEnumerable : null;
            if (parts == null && obj.ContainsKey("partNumber")) parts = new object[] { obj };
            if (parts == null) throw new ExtractionException("No parts were found in the model's answer.");

            foreach (var p in parts)
            {
                var pd = p as Dictionary<string, object>;
                if (pd == null) continue;
                var rec = new PartRecord { SourceFile = sourceFile };
                bool any = false;
                foreach (var f in Schema.Fields)
                {
                    string v = pd.ContainsKey(f.Key) ? Str(pd[f.Key]) : "";
                    rec.Set(f.Key, v);
                    if (v.Length > 0) any = true;
                }
                if (!any) continue;

                var terms = pd.ContainsKey("terminals") ? pd["terminals"] as IEnumerable : null;
                if (terms != null)
                {
                    foreach (var t in terms)
                    {
                        var td = t as Dictionary<string, object>;
                        if (td == null) continue;
                        var tr = new TerminalRec
                        {
                            Designation = td.ContainsKey("designation") ? Str(td["designation"]) : "",
                            Function = td.ContainsKey("function") ? Str(td["function"]) : ""
                        };
                        if (tr.Designation.Length > 0 || tr.Function.Length > 0) rec.Terminals.Add(tr);
                        if (rec.Terminals.Count >= 200) break;
                    }
                }

                var flags = pd.ContainsKey("flags") ? pd["flags"] as Dictionary<string, object> : null;
                if (flags != null)
                {
                    foreach (var kv in flags)
                    {
                        string reason = Str(kv.Value);
                        if (reason.Length == 0) continue;
                        if (kv.Key == "terminals" ? rec.Terminals.Count > 0 : rec.Get(kv.Key).Length > 0)
                            rec.Flags[kv.Key] = reason.Length > 120 ? reason.Substring(0, 120) : reason;
                    }
                }
                result.Parts.Add(rec);
                if (result.Parts.Count >= 40) break;
            }

            if (result.Parts.Count == 0) throw new ExtractionException("No part data was found. Check that the file is a component data sheet.");
            return result;
        }

        public static string Str(object o)
        {
            if (o == null) return "";
            var s = o as string;
            if (s != null) return s.Trim();
            var f = o as IFormattable;
            return f != null ? f.ToString(null, CultureInfo.InvariantCulture).Trim() : o.ToString().Trim();
        }
    }
}
