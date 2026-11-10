using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Threading;
using EwAPI;
using SWEETs.SweDrawingParameters.AddIn.Infrastructure;
using SWEETs.SweDrawingParameters.AddIn.Model;
using SWEETs.SweDrawingParameters.AddIn.UI;
using SWEETs.SweDrawingParameters.AddIn.UI.ViewModels;

namespace SWEETs.SweDrawingParameters.AddIn.Entry
{
    /// <summary>
    /// Ribbon command. Opens the dialog modally over the SOLIDWORKS Electrical main window, on SWE's UI thread
    /// (every EwAPI call made while updating drawings must come from this thread).
    /// Implements IEwCommandX as declared in the SWE 2026 type library.
    /// </summary>
    [ComVisible(true)]
    [Guid("dc5849bc-304c-41df-9b0b-46d021fce334")]
    [ClassInterface(ClassInterfaceType.None)]
    public class UpdateDrawingParametersCommand : EwCommandX
    {
        public const string CommandName = "SweetsUpdateProjectDrawingParametersCommand";

        /// <summary>
        /// EwCommandType flag sets accepted by SWE's addCommand, as used by the SWEETs PDF-to-Schematic add-in:
        /// 0x1200000 = kCommandSession | kCommandNoUndoMarker; 0x1000001 = kCommandTransparent | kCommandNoUndoMarker.
        /// </summary>
        public static readonly int[] FlagCandidates = { 0x1200000, 0x1000001, 0x1200001 };

        public int Flags { get; set; } = FlagCandidates[0];

        public string getName(out EwErrorCode errorCode) { errorCode = EwErrorCode.EW_NO_ERROR; return CommandName; }

        public string getDescription(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return SweAddIn.Description;
        }

        public int getFlags(out EwErrorCode errorCode) { errorCode = EwErrorCode.EW_NO_ERROR; return Flags; }

        /// <summary>SWE is not a WPF application: create a hidden WPF Application once so pack:// URIs resolve.</summary>
        private static void EnsureWpfApplication()
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            if (System.Windows.Application.Current == null)
                new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        }

        /// <summary>A run in progress (one at a time).</summary>
        private static RunWindow _runWindow;

        public EwErrorCode execute(EwCommandContextX commandContext)
        {
            try
            {
                EnsureWpfApplication();
                if (_runWindow != null)
                {
                    _runWindow.Activate();   // a run is still in progress or its results are still open
                    return EwErrorCode.EW_NO_ERROR;
                }

                var owner = Process.GetCurrentProcess().MainWindowHandle;
                var window = new MainWindow(SweAddIn.Application);
                if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
                window.ShowDialog();

                if (window.Plan != null) StartRun(window.Plan, owner);
                return EwErrorCode.EW_NO_ERROR;
            }
            catch (Exception ex)
            {
                FileLog.Write("command", ex);
                System.Windows.MessageBox.Show(ex.Message, SweAddIn.Title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return EwErrorCode.EW_UNDEFINED_ERROR;
            }
        }

        /// <summary>
        /// Shows the modeless run window and starts the run. execute() then returns to SOLIDWORKS Electrical and the run
        /// continues from SWE's message loop: its awaits resume through a DispatcherSynchronizationContext, so the
        /// SolidWorks Electrical (COM) checks stay on this UI thread while the file work runs on a worker thread.
        /// </summary>
        private static void StartRun(RunPlan plan, IntPtr owner)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

            var vm = new RunViewModel(plan);
            _runWindow = new RunWindow(vm);
            if (owner != IntPtr.Zero) new WindowInteropHelper(_runWindow).Owner = owner;
            _runWindow.Closed += (s, e) => _runWindow = null;
            _runWindow.Show();

            vm.RunAsync().ContinueWith(t =>
            {
                if (t.Exception != null) FileLog.Write("command", t.Exception);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
