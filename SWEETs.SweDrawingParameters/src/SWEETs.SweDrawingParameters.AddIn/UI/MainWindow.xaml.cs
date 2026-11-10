using System.ComponentModel;
using System.Windows;
using SWEETs.SweDrawingParameters.AddIn.Infrastructure;
using SWEETs.SweDrawingParameters.AddIn.Model;
using SWEETs.SweDrawingParameters.AddIn.UI.ViewModels;

namespace SWEETs.SweDrawingParameters.AddIn.UI
{
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _vm;

        /// <summary>The update the user confirmed, or null when the dialog was closed without applying.</summary>
        public RunPlan Plan { get; private set; }

        /// <param name="sweApplication">EwAPI application object (IEwApplicationX).</param>
        public MainWindow(object sweApplication)
        {
            InitializeComponent();
            _vm = new MainViewModel(sweApplication);
            DataContext = _vm;
            Loaded += (s, e) => _vm.RefreshCommand.Execute(null);   // AsyncCommand reports failures in the banner
            _vm.PlanReady += (s, plan) => { Plan = plan; DialogResult = true; };

            // A UI fault must not close the window: log it and show it.
            Dispatcher.UnhandledException += OnUiException;
            Closed += (s, e) => Dispatcher.UnhandledException -= OnUiException;
        }

        private void OnUiException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            FileLog.Write("ui", e.Exception);
            MessageBox.Show(this, "Unexpected error: " + e.Exception.Message + "\n\nDetails are in the log.", Title, MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            if (!_vm.IsBusy) Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_vm.IsBusy && Plan == null) e.Cancel = true;   // still reading drawing files
            base.OnClosing(e);
        }
    }
}
