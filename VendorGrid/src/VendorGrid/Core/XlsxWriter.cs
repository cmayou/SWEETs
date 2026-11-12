using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace VendorGrid.Core
{
    /// <summary>One worksheet cell. Use the static factories.</summary>
    public sealed class XCell
    {
        public enum Kind { Text, Number, Formula }
        public Kind Type;
        public string Text;
        public double Number;
        public string Formula;
        public int Style;

        public static XCell T(string text, int style = 0) { return new XCell { Type = Kind.Text, Text = text ?? "", Style = style }; }
        public static XCell N(double value, int style = 0) { return new XCell { Type = Kind.Number, Number = value, Style = style }; }
        /// <summary>Formula with a cached value so viewers that do not recalculate still show a number.</summary>
        public static XCell F(string formula, double cached, int style = 0) { return new XCell { Type = Kind.Formula, Formula = formula, Number = cached, Style = style }; }
        public static readonly XCell Empty = null;
    }

    /// <summary>
    /// Minimal .xlsx (Office Open XML) writer: one worksheet, bold header row, frozen header, auto-filter, column widths,
    /// number formats and formulas. No third-party library and no Excel installation required.
    /// </summary>
    public static class XlsxWriter
    {
        // Style ids (see Styles())
        public const int StyleDefault = 0, StyleHeader = 1, StyleUnitPrice = 2, StyleMoney = 3, StyleInteger = 4, StyleBold = 5, StyleBoldMoney = 6;

        public static byte[] Build(string sheetName, string[] header, double[] widths, List<XCell[]> rows)
        {
            using (var ms = new MemoryStream())
            {
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                {
                    Add(zip, "[Content_Types].xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                        "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                        "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                        "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                        "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                        "</Types>");
                    Add(zip, "_rels/.rels",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                        "</Relationships>");
                    Add(zip, "xl/workbook.xml",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                        "<sheets><sheet name=\"" + Esc(SafeSheetName(sheetName)) + "\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                        "<calcPr fullCalcOnLoad=\"1\"/></workbook>");
                    Add(zip, "xl/_rels/workbook.xml.rels",
                        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                        "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                        "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                        "</Relationships>");
                    Add(zip, "xl/styles.xml", Styles());
                    Add(zip, "xl/worksheets/sheet1.xml", Sheet(header, widths, rows));
                }
                return ms.ToArray();
            }
        }

        static void Add(ZipArchive zip, string path, string xml)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            using (var s = entry.Open())
            {
                var bytes = new UTF8Encoding(false).GetBytes(xml);
                s.Write(bytes, 0, bytes.Length);
            }
        }

        static string Styles()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<numFmts count=\"2\"><numFmt numFmtId=\"164\" formatCode=\"#,##0.0000\"/><numFmt numFmtId=\"165\" formatCode=\"#,##0.00\"/></numFmts>" +
                "<fonts count=\"3\">" +
                "<font><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"Calibri\"/></font>" +
                "<font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font>" +
                "</fonts>" +
                "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>" +
                "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F2937\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"7\">" +
                "<xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>" +
                "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/>" +
                "<xf numFmtId=\"164\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"165\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/>" +
                "<xf numFmtId=\"0\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/>" +
                "<xf numFmtId=\"165\" fontId=\"2\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\" applyFont=\"1\"/>" +
                "</cellXfs>" +
                "<cellStyles count=\"1\"><cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
                "</styleSheet>";
        }

        static string Sheet(string[] header, double[] widths, List<XCell[]> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            sb.Append("<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews>");
            sb.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
            if (widths != null && widths.Length > 0)
            {
                sb.Append("<cols>");
                for (int i = 0; i < widths.Length; i++)
                    sb.Append("<col min=\"" + (i + 1) + "\" max=\"" + (i + 1) + "\" width=\"" + widths[i].ToString(CultureInfo.InvariantCulture) + "\" customWidth=\"1\"/>");
                sb.Append("</cols>");
            }
            sb.Append("<sheetData>");

            sb.Append("<row r=\"1\">");
            for (int c = 0; c < header.Length; c++) AppendCell(sb, Ref(c, 1), XCell.T(header[c], StyleHeader));
            sb.Append("</row>");

            for (int r = 0; r < rows.Count; r++)
            {
                int rowNo = r + 2;
                sb.Append("<row r=\"" + rowNo + "\">");
                var cells = rows[r];
                for (int c = 0; c < cells.Length; c++) if (cells[c] != null) AppendCell(sb, Ref(c, rowNo), cells[c]);
                sb.Append("</row>");
            }
            sb.Append("</sheetData>");
            if (header.Length > 0 && rows.Count > 0)
                sb.Append("<autoFilter ref=\"A1:" + Ref(header.Length - 1, 1 + rows.Count) + "\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        static void AppendCell(StringBuilder sb, string reference, XCell cell)
        {
            var style = cell.Style > 0 ? " s=\"" + cell.Style + "\"" : "";
            switch (cell.Type)
            {
                case XCell.Kind.Number:
                    sb.Append("<c r=\"" + reference + "\"" + style + "><v>" + cell.Number.ToString("R", CultureInfo.InvariantCulture) + "</v></c>");
                    break;
                case XCell.Kind.Formula:
                    sb.Append("<c r=\"" + reference + "\"" + style + "><f>" + Esc(cell.Formula) + "</f><v>" + cell.Number.ToString("R", CultureInfo.InvariantCulture) + "</v></c>");
                    break;
                default:
                    sb.Append("<c r=\"" + reference + "\" t=\"inlineStr\"" + style + "><is><t xml:space=\"preserve\">" + Esc(cell.Text) + "</t></is></c>");
                    break;
            }
        }

        /// <summary>0-based column index to A1 letters.</summary>
        public static string Col(int index)
        {
            var s = "";
            for (int n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
            return s;
        }

        static string Ref(int col, int row) { return Col(col) + row.ToString(CultureInfo.InvariantCulture); }

        static string SafeSheetName(string name)
        {
            var bad = new[] { '\\', '/', '?', '*', '[', ']', ':' };
            var clean = new string((name ?? "Sheet1").Where(ch => !bad.Contains(ch)).ToArray());
            return clean.Length == 0 ? "Sheet1" : (clean.Length > 31 ? clean.Substring(0, 31) : clean);
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (ch < 0x20 && ch != '\t' && ch != '\n' && ch != '\r') continue;   // illegal in XML 1.0
                switch (ch)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default: sb.Append(ch); break;
                }
            }
            return sb.ToString();
        }
    }
}
