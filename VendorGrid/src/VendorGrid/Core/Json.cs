using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace VendorGrid.Core
{
    /// <summary>Tiny JSON helpers (no NuGet dependency). Parsed nodes are Dictionary/IList/primitive.</summary>
    public static class Json
    {
        static JavaScriptSerializer Make() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }; }

        public static object Parse(string json) { return Make().DeserializeObject(json); }
        public static string Serialize(object o) { return Make().Serialize(o); }
        public static T Deserialize<T>(string json) { return Make().Deserialize<T>(json); }

        /// <summary>Navigate "a.b.0.c". Keys are case-insensitive; numeric tokens index lists. Empty path returns node.</summary>
        public static object Get(object node, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return node;
            foreach (var raw in path.Split('.'))
            {
                var token = raw.Trim();
                if (node == null || token.Length == 0) return node;
                var dict = node as IDictionary<string, object>;
                if (dict != null)
                {
                    object next = null; bool found = false;
                    foreach (var kv in dict)
                        if (string.Equals(kv.Key, token, StringComparison.OrdinalIgnoreCase)) { next = kv.Value; found = true; break; }
                    if (!found) return null;
                    node = next;
                    continue;
                }
                var list = node as IList;
                int idx;
                if (list != null && int.TryParse(token, out idx) && idx >= 0 && idx < list.Count) { node = list[idx]; continue; }
                return null;
            }
            return node;
        }

        public static List<object> List(object node)
        {
            var result = new List<object>();
            if (node == null || node is string) return result;
            var list = node as IList;
            if (list != null) { foreach (var o in list) result.Add(o); return result; }
            if (node is IDictionary<string, object>) result.Add(node);   // single object treated as 1-item list
            return result;
        }

        public static string Str(object node, string path = null)
        {
            var v = Get(node, path);
            if (v == null || v is IDictionary<string, object> || (v is IList)) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static long? Long(object node, string path = null)
        {
            var v = Get(node, path);
            if (v == null) return null;
            if (v is string) return PriceParser.ParseLong((string)v);
            try { return Convert.ToInt64(Math.Round(Convert.ToDecimal(v, CultureInfo.InvariantCulture))); }
            catch { return null; }
        }

        public static decimal? Decimal(object node, string path = null)
        {
            var v = Get(node, path);
            if (v == null) return null;
            if (v is string) return PriceParser.Parse((string)v);
            try { return Convert.ToDecimal(v, CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }
}
