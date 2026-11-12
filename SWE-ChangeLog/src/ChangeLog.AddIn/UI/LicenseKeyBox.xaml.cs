using System.Windows;
using System.Windows.Controls;

namespace ChangeLog.AddIn.UI
{
    /// <summary>
    /// License key input that is masked by default with an explicit show/hide toggle.
    /// PasswordBox.Password is not bindable, so the <see cref="Key"/> dependency property
    /// is synchronised in code-behind.
    /// </summary>
    public partial class LicenseKeyBox : UserControl
    {
        public static readonly DependencyProperty KeyProperty = DependencyProperty.Register(
            nameof(Key), typeof(string), typeof(LicenseKeyBox),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnKeyChanged));

        private bool _syncing;

        public LicenseKeyBox()
        {
            InitializeComponent();
        }

        public string Key
        {
            get { return (string)GetValue(KeyProperty); }
            set { SetValue(KeyProperty, value); }
        }

        public void FocusInput()
        {
            if (Eye.IsChecked == true) VisibleBox.Focus();
            else HiddenBox.Focus();
        }

        private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = (LicenseKeyBox)d;
            if (box._syncing) return;
            box._syncing = true;
            var v = (string)e.NewValue ?? string.Empty;
            if (box.HiddenBox.Password != v) box.HiddenBox.Password = v;
            if (box.VisibleBox.Text != v) box.VisibleBox.Text = v;
            box._syncing = false;
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e) => Push(HiddenBox.Password);

        private void OnTextChanged(object sender, TextChangedEventArgs e) => Push(VisibleBox.Text);

        private void Push(string value)
        {
            if (_syncing) return;
            _syncing = true;
            Key = value;
            if (HiddenBox.Password != value) HiddenBox.Password = value;
            if (VisibleBox.Text != value) VisibleBox.Text = value;
            _syncing = false;
        }

        private void OnReveal(object sender, RoutedEventArgs e)
        {
            VisibleBox.Visibility = Visibility.Visible;
            HiddenBox.Visibility = Visibility.Collapsed;
            EyeGlyph.Text = "";
            Eye.ToolTip = "Hide key";
        }

        private void OnConceal(object sender, RoutedEventArgs e)
        {
            VisibleBox.Visibility = Visibility.Collapsed;
            HiddenBox.Visibility = Visibility.Visible;
            EyeGlyph.Text = "";
            Eye.ToolTip = "Show key";
        }
    }
}
