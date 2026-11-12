using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using SWEETs.SweLineStyles.AddIn.Core;
using SWEETs.SweLineStyles.AddIn.Infrastructure;
using SWEETs.SweLineStyles.AddIn.Services;
using SWEETs.SweLineStyles.AddIn.UI.ViewModels;

namespace SWEETs.SweLineStyles.AddIn.UI
{
    public partial class MainWindow : Window, IEditorDialogs
    {
        private readonly MainViewModel _vm;

        public MainWindow()
        {
            InitializeComponent();
            _vm = new MainViewModel(this);
            DataContext = _vm;
            Loaded += (s, e) => _vm.Load();

            // A UI fault must not close the window: log it and show it.
            Dispatcher.UnhandledException += OnUiException;
            Closed += (s, e) => { Dispatcher.UnhandledException -= OnUiException; _vm.SaveSettings(); };
        }

        private void OnUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            FileLog.Write("ui", e.Exception);
            MessageBox.Show(this, "Unexpected error: " + e.Exception.Message + "\n\nDetails are in the log:\n" + FileLog.Folder, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void OnClose(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_vm.IsBusy) { e.Cancel = true; return; }
            if (_vm.UnsavedCount > 0 &&
                MessageBox.Show(this, $"Close without saving {_vm.UnsavedText.ToLowerInvariant()}?", Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                e.Cancel = true;
            base.OnClosing(e);
        }

        private void OnShapeDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_vm.EditShape.CanExecute(null)) _vm.EditShape.Execute(null);
        }

        // ---- IEditorDialogs

        public bool Confirm(string title, string message) =>
            MessageBox.Show(this, message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

        public ShapeDefinition EditShape(ShapeEditorViewModel vm)
        {
            var w = new ShapeEditorWindow(vm) { Owner = this };
            return w.ShowDialog() == true ? vm.Result() : null;
        }

        public string PickFolder(string current)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select the linestyle.xlin to edit",
                Filter = "SOLIDWORKS Electrical line styles (linestyle.xlin)|linestyle.xlin|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(current) ? current : "",
                CheckFileExists = true,
            };
            return dlg.ShowDialog(this) == true ? Path.GetDirectoryName(dlg.FileName) : null;
        }

        public LineStyleLibrary.BackupInfo PickBackup(List<LineStyleLibrary.BackupInfo> backups)
        {
            var w = new BackupsWindow(backups) { Owner = this };
            return w.ShowDialog() == true ? w.Selected : null;
        }
    }
}
