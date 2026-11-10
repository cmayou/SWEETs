using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using EwAPI;
using SWEETs.SwePdfSchematic.AddIn.UI;
using SWEETs.SwePdfSchematic.Core.Infrastructure;

namespace SWEETs.SwePdfSchematic.AddIn.Entry
{
    /// <summary>
    /// Ribbon command. Opens the importer modally over the SOLIDWORKS Electrical main window, on SWE's UI thread
    /// (required: every EwAPI call during the build must come from this thread).
    /// Implements IEwCommandX exactly as declared in the SWE 2026 type library:
    /// getName / getDescription / getFlags (each with out EwErrorCode) and execute(context) → EwErrorCode.
    /// </summary>
    [ComVisible(true)]
    [Guid("3a59357d-16a4-4ca4-ae85-8e94c063a6bf")]
    [ClassInterface(ClassInterfaceType.None)]
    public class ImportPdfCommand : EwCommandX
    {
        /// <summary>Alphanumeric CamelCase, like SWE's own add-in commands (e.g. "EwExportSchemeCommand").</summary>
        public const string CommandName = "SweetsImportPdfSchematicCommand";

        /// <summary>
        /// EwCommandType flag sets used by SWE's own add-ins (ExportControlBuildAddin / ElectricalAddinSDK):
        /// 0x1200000 = kCommandSession | kCommandNoUndoMarker (application-context command, e.g. EwExportSchemeCommand);
        /// 0x1000001 = kCommandTransparent | kCommandNoUndoMarker. 0 is rejected by addCommand (EW_BAD_INPUTS).
        /// </summary>
        public static readonly int[] FlagCandidates = { 0x1200000, 0x1000001, 0x1200001 };

        /// <summary>Flags reported to SWE; SweAddIn tries each candidate until addCommand accepts one.</summary>
        public int Flags { get; set; } = FlagCandidates[0];

        public string getName(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return CommandName;
        }

        public string getDescription(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return "Import a vector PDF schematic and build a SOLIDWORKS Electrical project from it.";
        }

        public int getFlags(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return Flags;
        }

        /// <summary>
        /// SWE is not a WPF application: create a hidden WPF Application once so pack:// resource URIs,
        /// merged dictionaries and the dispatcher behave normally. It never shuts the host down.
        /// </summary>
        private static void EnsureWpfApplication()
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            if (System.Windows.Application.Current == null)
                new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
        }

        public EwErrorCode execute(EwCommandContextX commandContext)
        {
            try
            {
                EnsureWpfApplication();
                var window = new MainWindow(SweAddIn.Application);
                var owner = Process.GetCurrentProcess().MainWindowHandle;
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
        }
    }
}
