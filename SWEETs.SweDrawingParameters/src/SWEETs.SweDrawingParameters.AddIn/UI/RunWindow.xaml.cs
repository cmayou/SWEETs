using System.ComponentModel;
using System.Windows;
using SWEETs.SweDrawingParameters.AddIn.UI.ViewModels;

namespace SWEETs.SweDrawingParameters.AddIn.UI
{
    /// <summary>Modeless progress/results window for a run (see <see cref="RunViewModel"/>).</summary>
    public partial class RunWindow : Window
    {
        private readonly RunViewModel _vm;

        public RunWindow(RunViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            if (!_vm.IsRunning) Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // Never close in the middle of a drawing update: SOLIDWORKS Electrical has the drawing open.
            if (_vm.IsRunning) e.Cancel = true;
            base.OnClosing(e);
        }
    }
}
