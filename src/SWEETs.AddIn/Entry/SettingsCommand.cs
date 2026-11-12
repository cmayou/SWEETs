using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using EwAPI;
using SWEETs.AddIn.Infrastructure;
using SWEETs.AddIn.UI;

namespace SWEETs.AddIn.Entry
{
    /// <summary>
    /// SWEETs › Settings. Opens the Settings window modally over SOLIDWORKS Electrical. When the user saves, the
    /// change is applied after this command has returned to SWE (queued on the UI thread's dispatcher, which SWE's
    /// message loop pumps, as the Drawing Parameters run window relies on), because applying it rebuilds the tab
    /// that holds this button.
    /// Implements IEwCommandX as declared in the SWE 2026 type library (same pattern as the SWEETs tools).
    /// </summary>
    [ComVisible(true)]
    [Guid("7f9793bc-b597-42d3-9006-bcc967ed3178")]
    [ClassInterface(ClassInterfaceType.None)]
    public class SettingsCommand : EwCommandX
    {
        public const string CommandName = "SweetsSettingsCommand";

        /// <summary>
        /// EwCommandType flag sets accepted by SWE's addCommand, as used by the SWEETs tools:
        /// 0x1200000 = kCommandSession | kCommandNoUndoMarker; 0x1000001 = kCommandTransparent | kCommandNoUndoMarker.
        /// </summary>
        public static readonly int[] FlagCandidates = { 0x1200000, 0x1000001, 0x1200001 };

        public int Flags { get; set; } = FlagCandidates[0];

        public string getName(out EwErrorCode errorCode) { errorCode = EwErrorCode.EW_NO_ERROR; return CommandName; }

        public string getDescription(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return "Choose which SWEETs tools appear on the SWEETs tab.";
        }

        public int getFlags(out EwErrorCode errorCode) { errorCode = EwErrorCode.EW_NO_ERROR; return Flags; }

        /// <summary>SWE is not a WPF application: create a hidden WPF Application once so pack:// URIs resolve.</summary>
        private static void EnsureWpfApplication()
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            if (System.Windows.Application.Current == null)
                new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        }

        private static bool _open;

        public EwErrorCode execute(EwCommandContextX commandContext)
        {
            if (_open) return EwErrorCode.EW_NO_ERROR;
            var host = SweetsAddIn.Active;
            if (host == null) return EwErrorCode.EW_UNDEFINED_ERROR;
            try
            {
                _open = true;
                EnsureWpfApplication();
                var owner = Process.GetCurrentProcess().MainWindowHandle;
                var window = new SettingsWindow(host.Tools, host.Settings);
                if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
                if (window.ShowDialog() == true && window.Result != null)
                {
                    var settings = window.Result;
                    Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        try { host.ApplySettings(settings); }
                        catch (Exception ex) { FileLog.Write("settings", ex); }
                    }));
                }
                return EwErrorCode.EW_NO_ERROR;
            }
            catch (Exception ex)
            {
                FileLog.Write("command", ex);
                System.Windows.MessageBox.Show(ex.Message, SweetsAddIn.Title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return EwErrorCode.EW_UNDEFINED_ERROR;
            }
            finally { _open = false; }
        }
    }
}
