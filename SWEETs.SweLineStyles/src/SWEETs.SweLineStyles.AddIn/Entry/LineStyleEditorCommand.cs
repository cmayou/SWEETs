using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using EwAPI;
using SWEETs.SweLineStyles.AddIn.Infrastructure;
using SWEETs.SweLineStyles.AddIn.UI;

namespace SWEETs.SweLineStyles.AddIn.Entry
{
    /// <summary>
    /// Ribbon command. Opens the Line Style Editor modally over the SOLIDWORKS Electrical main window.
    /// Implements IEwCommandX as declared in the SWE 2026 type library (same pattern as SWEETs Drawing Parameters).
    /// </summary>
    [ComVisible(true)]
    [Guid("66f5514f-b55f-49bf-af2d-5fe8868f8dbc")]
    [ClassInterface(ClassInterfaceType.None)]
    public class LineStyleEditorCommand : EwCommandX
    {
        public const string CommandName = "SweetsLineStyleEditorCommand";

        /// <summary>
        /// EwCommandType flag sets accepted by SWE's addCommand, as used by the SWEETs add-ins:
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

        private static bool _open;

        public EwErrorCode execute(EwCommandContextX commandContext)
        {
            if (_open) return EwErrorCode.EW_NO_ERROR;
            try
            {
                _open = true;
                EnsureWpfApplication();
                var owner = Process.GetCurrentProcess().MainWindowHandle;
                var window = new MainWindow();
                if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
                window.ShowDialog();
                return EwErrorCode.EW_NO_ERROR;
            }
            catch (Exception ex)
            {
                FileLog.Write("command", ex);
                System.Windows.MessageBox.Show(ex.Message, SweAddIn.Title, System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return EwErrorCode.EW_UNDEFINED_ERROR;
            }
            finally { _open = false; }
        }
    }
}
