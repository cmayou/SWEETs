using System;
using DataSheetMfgPartsImport.Interop;
using DataSheetMfgPartsImport.Settings;

namespace DataSheetMfgPartsImport
{
    /// <summary>
    /// SWEETs module entry for the Data Sheet MFG Parts Import tool.
    ///
    /// This tool is no longer a stand-alone SOLIDWORKS Electrical add-in: the SWEETs host add-in
    /// (SWEETs.AddIn) gets the EwAPI application once and calls <see cref="Connect"/>. The application object is
    /// a COM object, so it is cast to this project's own interop interfaces (Interop\EwInterop.cs, same IIDs as SWE's
    /// type library) and the command is registered through them, exactly as the stand-alone version did.
    /// The host places the ribbon button on the SWEETs tab (Import Tools group).
    /// </summary>
    public static class Addin
    {
        public const string Title = "Data Sheet MFG Parts Import";
        public const string Description = "Read a manufacturer data sheet and add its parts to the manufacturer parts manager.";
        public const string ButtonCaption = "Datasheet Importer";

        internal static IEwApplicationX App;
        static IEwCommandManagerX _commands;
        static ExtractCommand _command;

        public static string CommandName => ExtractCommand.CommandName;

        /// <summary>
        /// Registers the DATASHEETIMPORT command. Returns the command string the ribbon button runs
        /// (settings.json "RibbonCommand", default "^C^CDATASHEETIMPORT", as in the stand-alone version).
        /// </summary>
        public static string Connect(object application)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));

            // QueryInterface for IEwApplicationX (IID 83a173a9-…) on SWE's application object.
            var app = application as IEwApplicationX;
            if (app == null) throw new InvalidOperationException("The SOLIDWORKS Electrical application object does not expose IEwApplicationX.");

            EwErrorCode ec;
            var commands = app.getEwCommandManager(out ec);
            if (ec != EwErrorCode.EW_NO_ERROR || commands == null)
                throw new InvalidOperationException("getEwCommandManager returned " + ec);

            // A command left over from an earlier connect would make addCommand fail.
            try { commands.removeCommand(ExtractCommand.CommandName); } catch { }

            var command = new ExtractCommand();
            ec = commands.addCommand(command);
            if (ec != EwErrorCode.EW_NO_ERROR)
                throw new InvalidOperationException("addCommand returned " + ec);

            App = app;
            _commands = commands;
            _command = command;

            var settings = AppSettings.Load();
            Log.Write("Connected through SWEETs. Command " + ExtractCommand.CommandName + " registered. Ribbon runs: " + settings.RibbonCommand);
            return settings.RibbonCommand;
        }

        /// <summary>Removes the command (SWEETs host shutdown, or the tool was turned off in SWEETs Settings).</summary>
        public static void Disconnect()
        {
            try
            {
                // The docs warn that a command left registered after unload can raise an exception when called.
                if (_commands != null)
                {
                    var ec = _commands.removeCommand(ExtractCommand.CommandName);
                    if (ec != EwErrorCode.EW_NO_ERROR) Log.Write("removeCommand returned " + ec);
                }
            }
            catch (Exception ex) { Log.Write("Disconnect: " + ex.Message); }
            _command = null;
            _commands = null;
            App = null;
        }
    }
}
