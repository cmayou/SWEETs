using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ChangeLog.AddIn.Host;
using ChangeLog.AddIn.UI;

[assembly: ComVisible(false)]

namespace ChangeLog.AddIn
{
    /// <summary>
    /// Identity of the stand-alone Change Log add-in (before it became part of SWEETs). Kept so the SWEETs installer
    /// and build\Register-Dev.ps1 can find and remove an old stand-alone registration.
    /// </summary>
    public static class AddInIds
    {
        public const string Clsid = "D4C1A7E2-6B3F-4E58-9A21-7C0E5F3B8A64";
        public const string ProgId = "ChangeLog.SWE.AddIn";
    }

    /// <summary>
    /// Change Log as a SWEETs module. The SWEETs host (one COM add-in for every SWEETs tool) gets the EwAPI application
    /// with its own API key and calls <see cref="Connect"/>; Change Log registers its six commands with SWE's command
    /// manager and starts recording, and SWEETs puts the buttons in its "Project Tools" group.
    /// Exceptions never escape into SWE: Connect throws only to tell SWEETs the tool did not load (SWEETs logs it).
    /// </summary>
    public static class ChangeLogAddIn
    {
        public const string ButtonCaption = "Change Log";
        public const string Description =
            "Keeps a tamper-evident log of every change to the open project, compares captured project states and exports the log to Excel.";

        private static AddInHost _host;

        /// <summary>
        /// Starts Change Log with the application object SWEETs obtained. Returns one entry per ribbon button, in ribbon
        /// order: the SWE command name, caption, tooltip and the 16/32 px icon files (relative to the add-in folder).
        /// </summary>
        public static IList<(string Command, string Caption, string ToolTip, string Icon16, string Icon32)> Connect(object application)
        {
            if (_host != null) Disconnect();
            Log.Info("SWEETs connect (Change Log " + AddInHost.AddInVersion + ")");
            var host = new AddInHost(new WpfUiService());
            try
            {
                host.Connect(application);
            }
            catch (Exception)
            {
                try { host.Dispose(); }
                catch (Exception ex) { Log.Error("Cleanup after failed connect", ex); }
                throw;
            }

            _host = host;
            var buttons = new List<(string Command, string Caption, string ToolTip, string Icon16, string Icon32)>();
            foreach (var b in host.Commands)
                buttons.Add((b.Command, b.Caption, b.Tooltip, @"Resources\" + b.Icon + "_16.png", @"Resources\" + b.Icon + "_32.png"));
            return buttons;
        }

        public static void Disconnect()
        {
            try
            {
                _host?.Dispose();
                Log.Info("SWEETs disconnect");
            }
            catch (Exception ex)
            {
                Log.Error("Disconnect failed", ex);
            }

            _host = null;
        }
    }
}
