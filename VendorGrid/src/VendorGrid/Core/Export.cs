using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using VendorGrid.Settings;
using VendorGrid.UI;

namespace VendorGrid.Core
{
    /// <summary>Builds the quote text (clipboard), the quote CSV and the BOM Excel workbook.</summary>
    public static class Export
    {
        static string Csv(string s)
        {
            s = s ?? "";
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        static string Num(decimal d, string fmt) { return d.ToString(fmt, CultureInfo.InvariantCulture); }

        static string Row(IEnumerable<string> cells) { return string.Join(",", cells); }

        // ---- quote for one searched part ---------------------------------------------------------

        public static string QuoteText(string part, int qty, IEnumerable<ResultRowVM> rows)
        {
            var list = rows.Where(r => r.IsListing).ToList();
            var sb = new StringBuilder();
            sb.AppendLine(Loc.T("VendorGrid quote") + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine(Loc.T("Part number") + " : " + part);
            sb.AppendLine(Loc.T("Quantity") + " : " + Loc.F("{0} pcs", Money.Count(qty)));
            sb.AppendLine(new string('=', 108));
            sb.AppendLine(Pad(Loc.T("Vendor"), 20) + Pad(Loc.T("Vendor SKU"), 24) + PadL(Loc.T("Stock"), 11) + PadL(Loc.T("Unit price"), 14) + PadL(Loc.T("Extended"), 16) + "  " + Loc.T("Packaging"));
            sb.AppendLine(new string('-', 108));
            foreach (var r in list)
            {
                sb.AppendLine(Pad(r.Vendor.Name, 20) + Pad(r.Sku, 24)
                    + PadL(r.Model.Stock < 0 ? "?" : Money.Count(r.Model.Stock), 11)
                    + PadL(r.UnitPriceText, 14) + PadL(r.TotalText, 16) + "  " + r.Packaging
                    + (r.IsAlternate ? "  " + Loc.F("[alternate: {0}]", r.Mpn) : ""));
            }
            sb.AppendLine(new string('=', 108));
            return sb.ToString();
        }

        static string Pad(string s, int n) { s = s ?? ""; return (s.Length > n - 1 ? s.Substring(0, n - 1) : s).PadRight(n); }
        static string PadL(string s, int n) { s = s ?? ""; return (s.Length > n ? s.Substring(0, n) : s).PadLeft(n); }

        public static string QuoteCsv(string part, int qty, IEnumerable<ResultRowVM> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Row(new[] { Loc.T("Searched part"), Loc.T("Quantity"), Loc.T("Vendor"), Loc.T("Vendor SKU"), Loc.T("Manufacturer part"), Loc.T("Manufacturer"), Loc.T("Match"), Loc.T("Stock"), Loc.T("Order quantity"), Loc.T("Unit price"), Loc.T("Extended"), Loc.T("Currency"), Loc.T("Packaging"), Loc.T("MOQ"), Loc.T("Lead time (days)"), Loc.T("Link") }));
            foreach (var r in rows.Where(x => x.IsListing))
            {
                var q = r.Quote;
                sb.AppendLine(Row(new[]
                {
                    Csv(part), qty.ToString(CultureInfo.InvariantCulture), Csv(r.Vendor.Name), Csv(r.Model.Sku), Csv(r.Model.Mpn), Csv(r.Model.Manufacturer),
                    Csv(r.IsAlternate ? Loc.T("Alternate") : Loc.T("Exact")),
                    r.Model.Stock < 0 ? "" : r.Model.Stock.ToString(CultureInfo.InvariantCulture),
                    q == null ? "" : q.OrderQuantity.ToString(CultureInfo.InvariantCulture),
                    q == null ? "" : Num(q.UnitPrice, "0.####"),
                    q == null ? "" : Num(q.Extended, "0.00"),
                    Csv(r.Model.Currency), Csv(r.Model.Packaging),
                    r.Model.MinOrder.ToString(CultureInfo.InvariantCulture),
                    r.Model.LeadTimeDays.HasValue ? r.Model.LeadTimeDays.Value.ToString(CultureInfo.InvariantCulture) : "",
                    Csv(r.Model.Url)
                }));
            }
            return sb.ToString();
        }

        // ---- BOM ---------------------------------------------------------------------------------

        public static byte[] BomXlsx(IEnumerable<BomLineVM> lines)
        {
            var header = new[] { Loc.T("Line"), Loc.T("Manufacturer part"), Loc.T("Manufacturer"), Loc.T("Description"), Loc.T("Vendor"), Loc.T("Vendor SKU"), Loc.T("Quantity"), Loc.T("Unit price"), Loc.T("Extended"), Loc.T("Currency"), Loc.T("Stock"), Loc.T("Lead time (days)"), Loc.T("Link") };
            var widths = new double[] { 6, 24, 22, 44, 18, 22, 11, 12, 13, 10, 11, 15, 50 };
            var rows = new List<XCell[]>();
            var list = lines.ToList();

            for (int i = 0; i < list.Count; i++)
            {
                var l = list[i];
                var q = l.Quote;
                int excelRow = i + 2;
                double qty = q == null ? l.Line.Quantity : q.OrderQuantity;
                rows.Add(new[]
                {
                    XCell.N(i + 1),
                    XCell.T(l.Part.Mpn), XCell.T(l.Manufacturer), XCell.T(l.Description), XCell.T(l.VendorName), XCell.T(l.Part.Sku),
                    XCell.N(qty, XlsxWriter.StyleInteger),
                    q == null ? null : XCell.N((double)q.UnitPrice, XlsxWriter.StyleUnitPrice),
                    q == null ? null : XCell.F("G" + excelRow + "*H" + excelRow, (double)q.Extended, XlsxWriter.StyleMoney),   // recalculates if you edit quantity or price in Excel
                    XCell.T(l.Currency),
                    l.Part.Stock < 0 ? null : XCell.N(l.Part.Stock, XlsxWriter.StyleInteger),
                    l.Part.LeadTimeDays.HasValue ? XCell.N(l.Part.LeadTimeDays.Value) : null,
                    XCell.T(l.Part.Url)
                });
            }

            // One total per currency, so mixed-currency BOMs are never added together.
            int last = list.Count + 1;
            rows.Add(new XCell[0]);
            foreach (var g in list.GroupBy(l => l.Currency))
            {
                var cells = new XCell[10];
                cells[7] = XCell.T(Loc.F("Total {0}", g.Key), XlsxWriter.StyleBold);
                cells[8] = XCell.F("SUMIF(J2:J" + last + ",\"" + g.Key + "\",I2:I" + last + ")", (double)g.Sum(l => l.ExtendedValue), XlsxWriter.StyleBoldMoney);
                cells[9] = XCell.T(g.Key, XlsxWriter.StyleBold);
                rows.Add(cells);
            }
            return XlsxWriter.Build(Loc.T("BOM"), header, widths, rows);
        }
    }
}
