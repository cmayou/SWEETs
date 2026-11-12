using System.Windows;

namespace ChangeLog.AddIn.UI
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow(SettingsViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            ((SettingsViewModel)DataContext).Save();
            DialogResult = true;
        }
    }
}
