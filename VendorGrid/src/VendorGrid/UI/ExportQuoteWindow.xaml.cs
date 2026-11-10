using System;
using System.IO;
using System.Text;
using System.Windows;
using VendorGrid.Core;

namespace VendorGrid.UI
{
    public partial class ExportQuoteWindow : Window
    {
        readonly string _text, _csv, _fileName;

        public ExportQuoteWindow(string part, int quantity, string text, string csv, string fileName)
        {
            InitializeComponent();
            _text = text; _csv = csv; _fileName = fileName;
            SubText.Text = Loc.F("Part {0} · {1} pcs. Copy the table or save it as a CSV file for Excel.", part, Money.Count(quantity));
            PreviewBox.Text = text;
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            try { Clipboard.SetText(_text); ResultText.Text = Loc.T("Copied to clipboard."); }
            catch (Exception ex) { Log.Error("Clipboard unavailable", ex); ResultText.Text = Loc.T("Clipboard is busy. Try again."); }
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = _fileName, DefaultExt = ".csv", Filter = Loc.T("CSV file") + " (*.csv)|*.csv" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                File.WriteAllText(dlg.FileName, _csv, new UTF8Encoding(true));   // BOM so Excel reads € and £ correctly
                ResultText.Text = Loc.F("Saved {0}", Path.GetFileName(dlg.FileName));
            }
            catch (Exception ex)
            {
                Log.Error("Saving CSV failed", ex);
                MessageBox.Show(this, Loc.F("The file could not be saved:\n{0}", ex.Message), "VendorGrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
