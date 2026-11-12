using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DataSheetMfgPartsImport.Interop;
using DataSheetMfgPartsImport.Services;
using DataSheetMfgPartsImport.Settings;
using DataSheetMfgPartsImport.UI;

namespace DataSheetMfgPartsImport
{
    /// <summary>
    /// The command SOLIDWORKS Electrical runs when the user starts "DATASHEETIMPORT".
    /// </summary>
    [ComVisible(true)]
    [Guid("5BE2F177-F64D-4418-8C4A-D4F1102CA4F2")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ExtractCommand : IEwCommandX
    {
        public const string CommandName = "DATASHEETIMPORT";

        public string getName(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return CommandName;
        }

        public string getDescription(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return "Read a manufacturer data sheet and add the part to the manufacturer parts manager.";
        }

        public int getFlags(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            // Modal, and only one importer window at a time.
            return (int)(EwCommandType.kCommandModal | EwCommandType.kCommandNoMultiple);
        }

        public EwErrorCode execute(IEwCommandContextX iCommandContextX)
        {
            try
            {
                var app = Addin.App;
                if (app == null)
                {
                    MessageBox.Show("The add-in is not connected to SOLIDWORKS Electrical.", "Data Sheet MFG Parts Import");
                    return EwErrorCode.EW_ADDIN_NOT_LOADED;
                }

                // The window runs on SOLIDWORKS Electrical's UI thread, which is where every API call must happen.
                var window = new ImporterWindow(new ElectricalCatalogService(app, AppSettings.Load()));
                try
                {
                    var owner = Process.GetCurrentProcess().MainWindowHandle;
                    if (owner != IntPtr.Zero) new WindowInteropHelper(window).Owner = owner;
                }
                catch { /* no owner window: the dialog is simply not parented */ }

                window.ShowDialog();
                return EwErrorCode.EW_NO_ERROR;
            }
            catch (Exception ex)
            {
                Log.Error("execute failed", ex);
                MessageBox.Show("Data Sheet MFG Parts Import could not start: " + ex.Message + "\r\nDetails: " + Log.FilePath,
                    "Data Sheet MFG Parts Import");
                return EwErrorCode.EW_UNDEFINED_ERROR;
            }
        }
    }
}
