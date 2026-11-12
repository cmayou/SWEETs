using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using DataSheetMfgPartsImport.Services;
using DataSheetMfgPartsImport.UI.Converters;
using DataSheetMfgPartsImport.UI.ViewModels;

namespace DataSheetMfgPartsImport.UI;

/// <summary>Importer dialog. All logic lives in <see cref="ImporterViewModel"/>; this file only handles drag and drop and window chrome.</summary>
public partial class ImporterWindow : Window
{
    private readonly ImporterViewModel _vm;

    public ImporterWindow(ICatalogService catalog)
    {
        InitializeComponent();
        ThemeHost.Resources = Resources;
        _vm = new ImporterViewModel(catalog, Dispatcher);
        DataContext = _vm;

        // Read classes / circuit types after the window is painted so it opens instantly.
        Loaded += (_, __) => Dispatcher.BeginInvoke(new Action(_vm.Initialize), DispatcherPriority.Background);
    }

    /// <summary>Opens the dialog with files already queued.</summary>
    public void QueueFiles(string[] paths)
    {
        Loaded += async (_, __) => await _vm.LoadFilesAsync(paths);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = !_vm.IsBusy && PdfPaths(e).Length > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropOutline.Stroke = (System.Windows.Media.Brush)FindResource(ok ? "AccentBrush" : "BorderStrongBrush");
        DropZone.Background = (System.Windows.Media.Brush)FindResource(ok ? "AccentSoftBrush" : "CardBrush");
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => ResetDropZone();

    private async void OnDrop(object sender, DragEventArgs e)
    {
        ResetDropZone();
        var paths = PdfPaths(e);
        if (paths.Length > 0) await _vm.LoadFilesAsync(paths);
    }

    private void ResetDropZone()
    {
        DropOutline.Stroke = (System.Windows.Media.Brush)FindResource("BorderStrongBrush");
        DropZone.Background = (System.Windows.Media.Brush)FindResource("CardBrush");
    }

    private static string[] PdfPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(ImporterViewModel.IsSupportedFile).ToArray()
            : Array.Empty<string>();

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow { Owner = this };
        if (dlg.ShowDialog() == true) _vm.ReloadSettings();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsBusy)
        {
            var r = MessageBox.Show(this, "An operation is still running. Close anyway?", "Data Sheet MFG Parts Import",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
            _vm.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
