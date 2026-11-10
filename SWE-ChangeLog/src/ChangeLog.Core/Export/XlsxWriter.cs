using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace ChangeLog.Core.Export
{
    /// <summary>Cell with an explicit style.</summary>
    public sealed class XlsxCell
    {
        public XlsxCell(object value, XlsxStyle style)
        {
            Value = value;
            Style = style;
        }

        public object Value { get; }
        public XlsxStyle Style { get; }

        public static XlsxCell Bold(object v) => new XlsxCell(v, XlsxStyle.Bold);
    }

    public enum XlsxStyle
    {
        Default = 0,
        Header = 1,
        DateTime = 2,
        Wrap = 3,
        Bold = 4
    }

    public sealed class XlsxSheet
    {
        internal XlsxSheet(string name)
        {
            Name = name;
            Rows = new List<object[]>();
            ColumnWidths = new List<double>();
        }

        public string Name { get; }
        public List<object[]> Rows { get; }
        public List<double> ColumnWidths { get; }

        /// <summary>First row is a styled, frozen header with an auto-filter.</summary>
        public bool HasHeader { get; set; }

        public XlsxSheet Row(params object[] cells)
        {
            Rows.Add(cells ?? new object[0]);
            return this;
        }

        public XlsxSheet Widths(params double[] widths)
        {
            ColumnWidths.Clear();
            ColumnWidths.AddRange(widths);
            return this;
        }
    }

    /// <summary>
    /// Minimal SpreadsheetML writer (inline strings, numbers, dates, 5 styles). Avoids a
    /// third-party dependency inside a host process where assembly conflicts are a real risk.
    /// </summary>
    public sealed class XlsxWriter
    {
        private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private readonly List<XlsxSheet> _sheets = new List<XlsxSheet>();

        public XlsxSheet AddSheet(string name)
        {
            var clean = new string((name ?? "Sheet").Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray());
            if (clean.Length == 0) clean = "Sheet" + (_sheets.Count + 1);
            if (clean.Length > 31) clean = clean.Substring(0, 31);
            var s = new XlsxSheet(clean);
            _sheets.Add(s);
            return s;
        }

        public void Save(Stream output)
        {
            if (_sheets.Count == 0) AddSheet("Sheet1");
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                Add(zip, "[Content_Types].xml", ContentTypes());
                Add(zip, "_rels/.rels",
                    "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                    "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Add(zip, "xl/workbook.xml", Workbook());
                Add(zip, "xl/_rels/workbook.xml.rels", WorkbookRels());
                Add(zip, "xl/styles.xml", Styles());
                for (var i = 0; i < _sheets.Count; i++)
                    Add(zip, "xl/worksheets/sheet" + (i + 1) + ".xml", Sheet(_sheets[i]));
            }
        }

        public void Save(string path)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Save(fs);
            }
        }

        private static void Add(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = entry.Open())
            {
                var bytes = new UTF8Encoding(false).GetBytes(content);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        private string ContentTypes()
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                                       "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                                       "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                                       "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                                       "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (var i = 0; i < _sheets.Count; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet" + (i + 1) + ".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            return sb.Append("</Types>").ToString();
        }

        private string Workbook()
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"" + MainNs + "\" xmlns:r=\"" + RelNs + "\"><sheets>");
            for (var i = 0; i < _sheets.Count; i++)
                sb.Append("<sheet name=\"" + Esc(_sheets[i].Name) + "\" sheetId=\"" + (i + 1) + "\" r:id=\"rId" + (i + 1) + "\"/>");
            sb.Append("</sheets>");
            var filters = _sheets.Select((s, i) => new { s, i }).Where(x => x.s.HasHeader && x.s.Rows.Count > 0).ToList();
            if (filters.Count > 0)
            {
                sb.Append("<definedNames>");
                foreach (var f in filters)
                    sb.Append("<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"" + f.i + "\" hidden=\"1\">'" +
                              Esc(f.s.Name.Replace("'", "''")) + "'!" + AbsRange(f.s) + "</definedName>");
                sb.Append("</definedNames>");
            }

            return sb.Append("</workbook>").ToString();
        }

        private string WorkbookRels()
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (var i = 0; i < _sheets.Count; i++)
                sb.Append("<Relationship Id=\"rId" + (i + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet" + (i + 1) + ".xml\"/>");
            sb.Append("<Relationship Id=\"rId" + (_sheets.Count + 1) + "\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            return sb.Append("</Relationships>").ToString();
        }

        private static string Styles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><styleSheet xmlns=\"" + MainNs + "\">" +
                   "<numFmts count=\"1\"><numFmt numFmtId=\"164\" formatCode=\"yyyy-mm-dd hh:mm:ss\"/></numFmts>" +
                   "<fonts count=\"3\"><font><sz val=\"10\"/><name val=\"Segoe UI\"/></font>" +
                   "<font><b/><sz val=\"10\"/><color rgb=\"FFFFFFFF\"/><name val=\"Segoe UI\"/></font>" +
                   "<font><b/><sz val=\"10\"/><name val=\"Segoe UI\"/></font></fonts>" +
                   "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                   "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF0B4EA2\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                   "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                   "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                   "<cellXfs count=\"5\">" +
                   "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                   "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
                   "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                   "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyAlignment=\"1\"><alignment wrapText=\"1\" vertical=\"top\"/></xf>" +
                   "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                   "</cellXfs><cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles></styleSheet>";
        }

        private static string Sheet(XlsxSheet s)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"" + MainNs + "\" xmlns:r=\"" + RelNs + "\">");
            if (s.HasHeader)
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            else
                sb.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
            if (s.ColumnWidths.Count > 0)
            {
                sb.Append("<cols>");
                for (var i = 0; i < s.ColumnWidths.Count; i++)
                    sb.Append(string.Format(CultureInfo.InvariantCulture, "<col min=\"{0}\" max=\"{0}\" width=\"{1:0.##}\" customWidth=\"1\"/>", i + 1, s.ColumnWidths[i]));
                sb.Append("</cols>");
            }

            sb.Append("<sheetData>");
            for (var r = 0; r < s.Rows.Count; r++)
            {
                sb.Append("<row r=\"" + (r + 1) + "\">");
                var row = s.Rows[r];
                for (var c = 0; c < row.Length; c++)
                {
                    var style = s.HasHeader && r == 0 ? XlsxStyle.Header : (XlsxStyle?)null;
                    Cell(sb, Col(c) + (r + 1), row[c], style);
                }

                sb.Append("</row>");
            }

            sb.Append("</sheetData>");
            if (s.HasHeader && s.Rows.Count > 0) sb.Append("<autoFilter ref=\"" + Range(s) + "\"/>");
            return sb.Append("</worksheet>").ToString();
        }

        private static void Cell(StringBuilder sb, string reference, object value, XlsxStyle? forced)
        {
            var styled = value as XlsxCell;
            var style = forced ?? styled?.Style ?? XlsxStyle.Default;
            var v = styled != null ? styled.Value : value;
            if (v == null) return;

            string number = null;
            if (v is DateTime)
            {
                number = ((DateTime)v).ToOADate().ToString("R", CultureInfo.InvariantCulture);
                if (forced == null && styled == null) style = XlsxStyle.DateTime;
            }
            else if (v is int || v is long || v is double || v is float || v is decimal)
            {
                number = Convert.ToDouble(v, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture);
            }

            var sAttr = style == XlsxStyle.Default ? string.Empty : " s=\"" + (int)style + "\"";
            if (number != null)
            {
                sb.Append("<c r=\"" + reference + "\"" + sAttr + "><v>" + number + "</v></c>");
                return;
            }

            var text = Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty;
            if (text.Length > 32767) text = text.Substring(0, 32767);
            if (style == XlsxStyle.Default && text.IndexOf('\n') >= 0) sAttr = " s=\"" + (int)XlsxStyle.Wrap + "\"";
            sb.Append("<c r=\"" + reference + "\" t=\"inlineStr\"" + sAttr + "><is><t xml:space=\"preserve\">" + Esc(text) + "</t></is></c>");
        }

        private static string Range(XlsxSheet s)
        {
            var cols = Math.Max(1, s.Rows.Max(r => r.Length));
            return "A1:" + Col(cols - 1) + s.Rows.Count;
        }

        private static string AbsRange(XlsxSheet s)
        {
            var cols = Math.Max(1, s.Rows.Max(r => r.Length));
            return "$A$1:$" + Col(cols - 1) + "$" + s.Rows.Count;
        }

        public static string Col(int index)
        {
            var name = string.Empty;
            index++;
            while (index > 0)
            {
                var m = (index - 1) % 26;
                name = (char)('A' + m) + name;
                index = (index - m) / 26;
            }

            return name;
        }

        private static string Esc(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default:
                        if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= 0x20) sb.Append(ch); // drop invalid XML control chars
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
