using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VendorGrid.Core;

namespace VendorGrid.UI
{
    /// <summary>Masked key field with a Show/Hide toggle. Bind to Value (two-way).</summary>
    public partial class SecretBox : UserControl
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(string), typeof(SecretBox),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            "Label", typeof(string), typeof(SecretBox), new PropertyMetadata("", OnLabelChanged));

        bool _syncing;

        public SecretBox()
        {
            InitializeComponent();
        }

        public string Value { get { return (string)GetValue(ValueProperty); } set { SetValue(ValueProperty, value); } }
        public string Label { get { return (string)GetValue(LabelProperty); } set { SetValue(LabelProperty, value); } }

        static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = (SecretBox)d;
            if (box._syncing) return;
            box._syncing = true;
            var text = (string)e.NewValue ?? "";
            box.Masked.Password = text;
            box.Plain.Text = text;
            box._syncing = false;
        }

        static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = (SecretBox)d;
            var name = (string)e.NewValue;
            var shown = Loc.T(name);
            AutomationProperties.SetName(box.Masked, Loc.F("{0} (hidden)", shown));
            AutomationProperties.SetName(box.Plain, shown);
            AutomationProperties.SetName(box.Toggle, Loc.F("Show or hide {0}", shown));
        }

        void Masked_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncing) return;
            _syncing = true;
            Plain.Text = Masked.Password;
            Value = Masked.Password;
            _syncing = false;
        }

        void Plain_Changed(object sender, TextChangedEventArgs e)
        {
            if (_syncing) return;
            _syncing = true;
            Masked.Password = Plain.Text;
            Value = Plain.Text;
            _syncing = false;
        }

        void Toggle_Changed(object sender, RoutedEventArgs e)
        {
            bool show = Toggle.IsChecked == true;
            Plain.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            Masked.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            Toggle.Content = show ? Loc.T("Hide") : Loc.T("Show");
            (show ? (Control)Plain : Masked).Focus();
        }
    }
}
