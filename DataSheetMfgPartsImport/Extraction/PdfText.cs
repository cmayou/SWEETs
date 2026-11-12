using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace DataSheetMfgPartsImport.Extraction
{
    /// <summary>One cell of a line: words that sit close together, with their horizontal extent.</summary>
    internal sealed class TextCell
    {
        public string Text;
        public double Left;
        public double Right;
    }

    internal sealed class TextLine
    {
        public string Text;
        public double FontSize;
        public int Page;
        /// <summary>Vertical position; larger means higher on the page.</summary>
        public double Y;
        public List<TextCell> Cells = new List<TextCell>();
    }

    internal sealed class PdfContent
    {
        public readonly List<TextLine> Lines = new List<TextLine>();
        public string Title = "";
        public string Author = "";
        public int PageCount;

        public int CharCount
        {
            get { return Lines.Sum(l => l.Text.Length); }
        }
    }

    /// <summary>Reads the selectable text of a PDF and rebuilds it as lines. Columns are separated by " | ".</summary>
    internal static class PdfText
    {
        public static PdfContent Read(string path, int maxPages)
        {
            var c = new PdfContent();
            try
            {
                using (var doc = PdfDocument.Open(path))
                {
                    c.PageCount = doc.NumberOfPages;
                    if (doc.Information != null)
                    {
                        c.Title = doc.Information.Title ?? "";
                        c.Author = doc.Information.Author ?? "";
                    }
                    int n = Math.Min(c.PageCount, maxPages);
                    for (int p = 1; p <= n; p++)
                        AddPage(c, doc.GetPage(p), p);
                }
            }
            catch (Exception ex)
            {
                throw new ExtractionException("This PDF could not be read: " + ex.Message);
            }
            return c;
        }

        static void AddPage(PdfContent c, Page page, int pageNo)
        {
            var ordered = page.GetWords()
                .Where(w => !string.IsNullOrWhiteSpace(w.Text))
                .OrderByDescending(w => w.BoundingBox.Bottom)
                .ThenBy(w => w.BoundingBox.Left)
                .ToList();

            var current = new List<Word>();
            double curY = 0;
            foreach (var w in ordered)
            {
                double y = w.BoundingBox.Bottom;
                double tol = Math.Max(2.0, w.BoundingBox.Height * 0.5);
                if (current.Count > 0 && Math.Abs(y - curY) > tol)
                {
                    Emit(c, current, pageNo);
                    current = new List<Word>();
                }
                if (current.Count == 0) curY = y;
                current.Add(w);
            }
            if (current.Count > 0) Emit(c, current, pageNo);
        }

        static void Emit(PdfContent c, List<Word> words, int pageNo)
        {
            var row = words.OrderBy(w => w.BoundingBox.Left).ToList();
            var cells = new List<TextCell>();
            Word prev = null;
            foreach (var w in row)
            {
                double gap = prev == null ? 0 : w.BoundingBox.Left - prev.BoundingBox.Right;
                double size = Math.Max(4.0, w.BoundingBox.Height);
                if (prev == null || gap > size * 1.6)
                {
                    cells.Add(new TextCell { Text = w.Text, Left = w.BoundingBox.Left, Right = w.BoundingBox.Right });
                }
                else
                {
                    var cell = cells[cells.Count - 1];
                    cell.Text += " " + w.Text;
                    cell.Right = w.BoundingBox.Right;
                }
                prev = w;
            }

            double fontSize = 0;
            try
            {
                var letters = row.SelectMany(w => w.Letters).ToList();
                if (letters.Count > 0) fontSize = letters.Average(l => l.PointSize);
            }
            catch { }

            string text = string.Join(" | ", cells.Select(x => x.Text)).Trim();
            if (text.Length > 0)
                c.Lines.Add(new TextLine
                {
                    Text = text,
                    FontSize = fontSize,
                    Page = pageNo,
                    Y = row.Average(w => w.BoundingBox.Bottom),
                    Cells = cells
                });
        }

        /// <summary>The text as one string with page markers, cut to maxChars.</summary>
        public static string ToPlain(PdfContent c, int maxChars)
        {
            var sb = new StringBuilder();
            int page = 0;
            foreach (var l in c.Lines)
            {
                if (l.Page != page)
                {
                    page = l.Page;
                    sb.Append("--- page ").Append(page).Append(" ---\n");
                }
                sb.Append(l.Text).Append('\n');
                if (sb.Length >= maxChars) break;
            }
            return sb.Length > maxChars ? sb.ToString(0, maxChars) : sb.ToString();
        }
    }
}
