using System;
using System.Diagnostics;
using VendorGrid.Core;
using VendorGrid.Settings;
using VendorGrid.UI;

namespace VendorGrid.Addin
{
    /// <summary>
    /// VendorGrid as a SWEETs module. The SWEETs host (the one COM add-in for every SWEETs tool) connects to SOLIDWORKS
    /// Electrical with its own API key and calls <see cref="Connect"/>; VendorGrid registers its command with SWE's command
    /// manager and SWEETs puts the "Vendor Search" button in the "Export Tools" group of the SWEETs tab.
    /// </summary>
    public static class VendorGridAddIn
    {
        public const string ButtonCaption = "Vendor Search";
        public const string ToolTip = "Search vendor websites for a part number";
        public const string Description = "Compare availability, vendor SKU and pricing by quantity across vendors. Reads parts from the open project.";

        static object _app;   // IEwApplicationX (typed or late bound)

        /// <summary>Registers the VendorGrid command; returns the command name the ribbon button runs.</summary>
        public static string Connect(object application)
        {
            if (application == null) throw new ArgumentNullException("application");
            Log.Info("SWEETs connect");

            _app = application;
            UiHost.OwnerHandleProvider = () => Process.GetCurrentProcess().MainWindowHandle;
            ProjectReader.App = _app;
            Loc.HostLanguageProvider = ProjectReader.GetProjectLanguage;

            try
            {
                RibbonBuilder.RegisterCommand(_app);
            }
            catch
            {
                ProjectReader.App = null;
                _app = null;
                throw;
            }

            Log.Info("Command '" + Defaults.CommandName + "' registered");
            return Defaults.CommandName;
        }

        public static void Disconnect()
        {
            try
            {
                Log.Info("SWEETs disconnect");
                UiHost.Shutdown();
                if (_app != null) RibbonBuilder.RemoveCommand(_app);
            }
            catch (Exception ex)
            {
                Log.Error("Disconnect failed", ex);
            }
            finally
            {
                ProjectReader.App = null;
                _app = null;
            }
        }
    }
}
