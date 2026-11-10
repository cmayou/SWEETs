using System.Windows;
using System.Windows.Controls;

namespace ChangeLog.AddIn.UI
{
    public partial class ChangeLogView : UserControl
    {
        /// <summary>Below this width the pane switches to the compact card layout.</summary>
        public const double CompactWidth = 720;

        public ChangeLogView(ChangeLogViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        private ChangeLogViewModel Vm => (ChangeLogViewModel)DataContext;

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (DataContext != null) Vm.IsCompact = e.NewSize.Width < CompactWidth;
        }

        private void OnDismissToast(object sender, RoutedEventArgs e) => Vm.IsToastVisible = false;
    }
}
