using System;
using System.Runtime.InteropServices;
using EwAPI;
using VendorGrid.Core;
using VendorGrid.Settings;
using VendorGrid.UI;

namespace VendorGrid.Addin
{
    /// <summary>Command invoked by the ribbon button. Opens (or re-activates) the VendorGrid dialog.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    internal sealed class SearchCommand : IEwCommandX, EwCommandX
    {
        public string getName(out EwErrorCode errorCode) { errorCode = EwErrorCode.EW_NO_ERROR; return Defaults.CommandName; }

        public string getDescription(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return "Search vendor websites for a part number";
        }

        public int getFlags(out EwErrorCode errorCode) { errorCode = EwErrorCode.EW_NO_ERROR; return 0; }

        public EwErrorCode execute(EwCommandContextX commandContext)
        {
            try
            {
                UiHost.ShowMainWindow();
                return EwErrorCode.EW_NO_ERROR;
            }
            catch (Exception ex)
            {
                Log.Error("Search command failed", ex);
                return EwErrorCode.EW_UNDEFINED_ERROR;
            }
        }
    }
}
