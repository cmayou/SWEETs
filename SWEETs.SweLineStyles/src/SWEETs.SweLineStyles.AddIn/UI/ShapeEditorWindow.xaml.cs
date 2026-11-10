using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SWEETs.SweLineStyles.AddIn.Infrastructure;
using SWEETs.SweLineStyles.AddIn.UI.ViewModels;

namespace SWEETs.SweLineStyles.AddIn.UI
{
    public partial class ShapeEditorWindow : Window
    {
        private readonly ShapeEditorViewModel _vm;

        public ShapeEditorWindow(ShapeEditorViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            Loaded += (s, e) => { DrawArea.FitView(); DrawArea.Focus(); };
        }

        private void OnAccept(object sender, RoutedEventArgs e)
        {
            if (!_vm.CanAccept) return;
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

        private void OnFit(object sender, RoutedEventArgs e) => DrawArea.FitView();

        private void OnImportDxf(object sender, RoutedEventArgs e)
        {
            var settings = UserSettings.Load();
            var dlg = new OpenFileDialog
            {
                Title = "Import shape geometry from DXF",
                Filter = "DXF files (*.dxf)|*.dxf",
                InitialDirectory = Directory.Exists(settings.LastDxfFolder) ? settings.LastDxfFolder : "",
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                _vm.LoadDxf(dlg.FileName);
                settings.LastDxfFolder = Path.GetDirectoryName(dlg.FileName);
                settings.Save();
                DrawArea.FitView();
            }
            catch (Exception ex)
            {
                FileLog.Write("dxf", ex);
                MessageBox.Show(this, ex.Message, "Import DXF", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
