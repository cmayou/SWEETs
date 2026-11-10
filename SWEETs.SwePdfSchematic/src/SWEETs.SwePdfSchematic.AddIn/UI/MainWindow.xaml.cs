using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SWEETs.SwePdfSchematic.AddIn.UI.ViewModels;
using SWEETs.SwePdfSchematic.Core.Infrastructure;

namespace SWEETs.SwePdfSchematic.AddIn.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        /// <param name="sweApplication">EwAPI application object, or null to run in simulate-only mode.</param>
        public MainWindow(object sweApplication)
        {
            InitializeComponent();
            _vm = new MainViewModel(sweApplication) { OwnerWindow = this };
            DataContext = _vm;
            _vm.ReviewChanged += (s, e) => PageCanvas.InvalidateVisual();
            // Scroll after the list has processed the change: scrolling inside CollectionChanged forces a layout before the
            // ListBox has seen the new item ("ItemsControl is inconsistent with its items source").
            _vm.Log.CollectionChanged += (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogList.Items.Count > 0) LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
            }), System.Windows.Threading.DispatcherPriority.Background);

            // A UI fault must not close the window (and lose the review): log it and keep going.
            Dispatcher.UnhandledException += OnUiException;
            Closed += (s, e) => Dispatcher.UnhandledException -= OnUiException;
        }

        private void OnUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            FileLog.Write("ui", e.Exception);
            _vm.Status = "Unexpected error: " + e.Exception.Message + " — details in the log.";
            e.Handled = true;
        }

        private void OnDragOver(object sender, DragEventArgs e)
        {
            e.Effects = PdfFrom(e) != null && !_vm.IsBusy ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void OnDrop(object sender, DragEventArgs e)
        {
            var path = PdfFrom(e);
            if (path == null || _vm.IsBusy) return;
            try { await _vm.LoadAsync(path); }
            catch (Exception ex) { FileLog.Write("ui", ex); _vm.Status = "Could not open the PDF: " + ex.Message; }
        }

        private static string PdfFrom(DragEventArgs e) =>
            (e.Data.GetData(DataFormats.FileDrop) as string[])?.FirstOrDefault(f => string.Equals(Path.GetExtension(f), ".pdf", StringComparison.OrdinalIgnoreCase));

        private void OnFit(object sender, RoutedEventArgs e) => PageCanvas.Fit();

        private void OnCardSelected(object sender, SelectionChangedEventArgs e)
        {
            if (CardList.SelectedItem != null) CardList.ScrollIntoView(CardList.SelectedItem);
            _vm.SelectedCards = CardList.SelectedItems.Cast<SymbolCardViewModel>().ToList(); // Ctrl/Shift+click to combine
        }
    }
}
