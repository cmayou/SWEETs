using System.Collections.Generic;
using System.IO;
using System.Windows;
using SWEETs.SweLineStyles.AddIn.Services;

namespace SWEETs.SweLineStyles.AddIn.UI
{
    public partial class BackupsWindow : Window
    {
        public LineStyleLibrary.BackupInfo Selected { get; private set; }

        public BackupsWindow(List<LineStyleLibrary.BackupInfo> backups)
        {
            InitializeComponent();
            BackupList.ItemsSource = backups;
            if (backups.Count > 0) BackupList.SelectedIndex = 0;
            EmptyText.Visibility = backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RootText.Text = LineStyleLibrary.BackupRoot;
        }

        private void OnRestore(object sender, RoutedEventArgs e)
        {
            if (!(BackupList.SelectedItem is LineStyleLibrary.BackupInfo b)) return;
            if (MessageBox.Show(this, $"Restore the files saved on {b.Display} into the Support folder?\n\nThe current files are backed up first.",
                    Title, MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Selected = b;
            DialogResult = true;
        }

        private void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            Directory.CreateDirectory(LineStyleLibrary.BackupRoot);
            System.Diagnostics.Process.Start("explorer.exe", "\"" + LineStyleLibrary.BackupRoot + "\"");
        }
    }
}
