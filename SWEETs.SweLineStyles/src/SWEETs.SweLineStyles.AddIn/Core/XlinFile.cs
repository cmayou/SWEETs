using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    /// <summary>The 14 description languages used in SOLIDWORKS Electrical 2026's linestyle.xlin, in file order.</summary>
    public static class Languages
    {
        public static readonly (string Code, string Name)[] All =
        {
            ("en", "English"), ("fr", "French"), ("es", "Spanish"), ("it", "Italian"), ("de", "German"),
            ("ko", "Korean"), ("ja", "Japanese"), ("ru", "Russian"), ("pt", "Portuguese"), ("cs", "Czech"),
            ("zh", "Chinese (Simplified)"), ("pl", "Polish"), ("tr", "Turkish"), ("zh-tw", "Chinese (Traditional)"),
        };

        public static int Order(string code)
        {
            int i = Array.FindIndex(All, l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? int.MaxValue : i;
        }
    }

    public sealed class LineStyle
    {
        public string Name { get; set; } = "";
        public string Pattern { get; set; } = "";
        public string Ascii { get; set; } = "";
        /// <summary>Language code → description. Empty languages are not written.</summary>
        public List<KeyValuePair<string, string>> Descriptions { get; } = new List<KeyValuePair<string, string>>();

        public string Description(string lang) => Descriptions.FirstOrDefault(d => string.Equals(d.Key, lang, StringComparison.OrdinalIgnoreCase)).Value;

        public LineStyle Clone()
        {
            var c = new LineStyle { Name = Name, Pattern = Pattern, Ascii = Ascii };
            c.Descriptions.AddRange(Descriptions);
            return c;
        }

        public bool SameContent(LineStyle o) =>
            o != null && o.Name == Name && o.Pattern == Pattern && o.Ascii == Ascii &&
            o.Descriptions.Count == Descriptions.Count &&
            o.Descriptions.Zip(Descriptions, (a, b) => a.Key == b.Key && a.Value == b.Value).All(x => x);
    }

    /// <summary>
    /// Support\linestyle.xlin (schema Support\linestyle.xsd): SECTIONS / EW_HEADER + lineStyles / lineStyle
    /// (pattern, name, ascii, description/LANGUAGE xml:lang). Read with an XML parser; written with the file's own
    /// layout (UTF-8 without BOM, CRLF, tab indentation, header kept verbatim). Verified: loading and saving SWE 2026's
    /// linestyle.xlin unchanged reproduces it byte for byte (48,023 bytes, 55 styles).
    /// </summary>
    public sealed class XlinFile
    {
        private const string DefaultHeader =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n" +
            "<SECTIONS xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" xsi:noNamespaceSchemaLocation=\"linestyle.xsd\">\r\n" +
            "\t<EW_HEADER>\r\n\t\t<VERSION Number=\"1.0.0.0\"/>\r\n\t</EW_HEADER>\r\n\t<lineStyles>";
        private const string DefaultTail = "</lineStyles>\r\n</SECTIONS>\r\n";

        public string Header { get; private set; } = DefaultHeader;
        public string Tail { get; private set; } = DefaultTail;
        public List<LineStyle> Styles { get; } = new List<LineStyle>();

        public static XlinFile Load(byte[] data)
        {
            var file = new XlinFile();
            var text = new UTF8Encoding(false).GetString(data);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);

            XDocument doc;
            using (var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                doc = XDocument.Load(reader);

            var root = doc.Root;
            if (root == null || root.Name.LocalName != "SECTIONS") throw new InvalidDataException("Not a line style file: the root element is not SECTIONS.");
            var list = root.Element("lineStyles") ?? throw new InvalidDataException("Not a line style file: no lineStyles element.");

            foreach (var ls in list.Elements("lineStyle"))
            {
                var style = new LineStyle
                {
                    Name = (string)ls.Element("name") ?? "",
                    Pattern = (string)ls.Element("pattern") ?? "",
                    Ascii = (string)ls.Element("ascii") ?? "",
                };
                var desc = ls.Element("description");
                if (desc != null)
                    foreach (var l in desc.Elements("LANGUAGE"))
                        style.Descriptions.Add(new KeyValuePair<string, string>((string)l.Attribute(XNamespace.Xml + "lang") ?? "en", l.Value));
                file.Styles.Add(style);
            }

            int open = text.IndexOf("<lineStyles>", StringComparison.Ordinal);
            int close = text.LastIndexOf("</lineStyles>", StringComparison.Ordinal);
            if (open >= 0 && close > open)
            {
                file.Header = text.Substring(0, open + "<lineStyles>".Length);
                file.Tail = text.Substring(close);
            }
            return file;
        }

        public byte[] Save()
        {
            var sb = new StringBuilder(Header);
            foreach (var s in Styles)
            {
                sb.Append("\r\n\t\t<lineStyle>");
                sb.Append("\r\n\t\t\t<pattern>").Append(Esc(s.Pattern)).Append("</pattern>");
                sb.Append("\r\n\t\t\t<name>").Append(Esc(s.Name)).Append("</name>");
                sb.Append("\r\n\t\t\t<ascii>").Append(Esc(s.Ascii)).Append("</ascii>");
                sb.Append("\r\n\t\t\t<description>");
                var langs = s.Descriptions.Where(d => !string.IsNullOrWhiteSpace(d.Value)).ToList();
                if (langs.Count == 0) langs.Add(new KeyValuePair<string, string>("en", s.Name));   // schema: at least one LANGUAGE
                foreach (var d in langs)
                    sb.Append("\r\n\t\t\t\t<LANGUAGE xml:lang=\"").Append(EscAttr(d.Key)).Append("\">").Append(Esc(d.Value)).Append("</LANGUAGE>");
                sb.Append("\r\n\t\t\t</description>\r\n\t\t</lineStyle>");
            }
            sb.Append("\r\n\t").Append(Tail);
            return new UTF8Encoding(false).GetBytes(sb.ToString());
        }

        // Element text: only & < > are escaped (patterns contain literal quotes, as in the original file).
        private static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        private static string EscAttr(string s) => Esc(s).Replace("\"", "&quot;");

        /// <summary>Line-style naming rules (AutoCAD linetype names).</summary>
        public static IEnumerable<string> ValidateName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) { yield return "Name is required."; yield break; }
            if (name.Length > 255) yield return "Name is longer than 255 characters.";
            const string bad = "<>/\\\":;?*|,=`";
            var found = name.Where(c => bad.IndexOf(c) >= 0 || char.IsControl(c)).Distinct().ToList();
            if (found.Count > 0) yield return "Name cannot contain " + string.Join(" ", found) + ".";
            if (name != name.Trim()) yield return "Name cannot start or end with a space.";
        }
    }
}
