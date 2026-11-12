using System.Windows;

namespace ChangeLog.AddIn.UI
{
    public partial class LicenseDialog : Window
    {
        public LicenseDialog(string message)
        {
            InitializeComponent();
            MessageText.Text = message;
            Loaded += (s, e) => KeyBox.FocusInput();
        }

        public string Key => KeyBox.Key;

        private void OnConnect(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(KeyBox.Key))
            {
                MessageText.Text = "Enter the key to continue.";
                return;
            }

            DialogResult = true;
        }
    }
}
