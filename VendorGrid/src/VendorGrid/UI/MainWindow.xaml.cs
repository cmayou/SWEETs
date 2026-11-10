using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.UI
{
    public partial class MainWindow : Window
    {
        readonly MainViewModel _vm;

        public MainWindow()
        {
            Loc.Initialize(SettingsStore.Load().Language);   // before InitializeComponent so labels load translated
            InitializeComponent();
            _vm = new MainViewModel(Dispatcher);
            DataContext = _vm;

            _vm.RequestSettings += OpenSettings;
            _vm.RequestTiers += row => new PriceBreaksWindow(row) { Owner = this }.ShowDialog();
            _vm.RequestQuote += (text, csv, name) => new ExportQuoteWindow(_vm.SearchedPart, _vm.Quantity, text, csv, name) { Owner = this }.ShowDialog();
            _vm.RequestSave += SaveFile;
            _vm.ConfirmHandler = message => MessageBox.Show(this, message, "VendorGrid", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

            Loaded += (s, e) =>
            {
                PartBox.Focus();
                // Read the project after the window has painted; COM calls run on this (the host's UI) thread.
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _vm.Project.Reload(true)));
            };
            Activated += (s, e) => _vm.Project.RefreshIfStale();
            Closed += (s, e) => _vm.Shutdown();
        }

        void OpenSettings()
        {
            var dlg = new SettingsWindow { Owner = this };
            if (dlg.ShowDialog() == true) _vm.OnSettingsSaved();
        }

        void SaveFile(string suggestedName, byte[] content)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = suggestedName, DefaultExt = ".xlsx", Filter = Loc.T("Excel workbook") + " (*.xlsx)|*.xlsx" };
            if (dlg.ShowDialog(this) != true) return;
            try { File.WriteAllBytes(dlg.FileName, content); }
            catch (Exception ex)
            {
                Log.Error("Saving file failed", ex);
                MessageBox.Show(this, Loc.F("The file could not be saved. If it is open in Excel, close it and try again.\n{0}", ex.Message), "VendorGrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>The project tile list scrolls itself; hand the wheel to the page when it cannot scroll further.</summary>
        void InnerScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            var sv = (System.Windows.Controls.ScrollViewer)sender;
            bool atTop = sv.VerticalOffset <= 0 && e.Delta > 0;
            bool atBottom = sv.VerticalOffset >= sv.ScrollableHeight && e.Delta < 0;
            if (sv.ScrollableHeight > 0 && !atTop && !atBottom) return;
            e.Handled = true;
            var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender };
            MainScroll.RaiseEvent(args);
        }
    }
}
