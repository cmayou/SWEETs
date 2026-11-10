using System;
using SWEETs.SwePdfSchematic.AddIn.Interop;
using SWEETs.SwePdfSchematic.Core.Infrastructure;
using C = SWEETs.SwePdfSchematic.AddIn.Interop.SweContract;

namespace SWEETs.SwePdfSchematic.AddIn.Entry
{
    /// <summary>
    /// SWEETs module entry for PDF to Schematic.
    ///
    /// This tool is no longer a stand-alone SOLIDWORKS Electrical add-in: the SWEETs host add-in
    /// (SWEETs.AddIn) gets the EwAPI application once, calls <see cref="Connect"/> to register this tool's
    /// command, and places the ribbon button on the SWEETs tab (Import Tools group). The command, importer window,
    /// recognition and build code are unchanged from the stand-alone version 1.0.0.
    /// </summary>
    public static class SweAddIn
    {
        public const string Title = "SWEETs PDF to Schematic";
        public const string Description = "Builds a SOLIDWORKS Electrical schematic project from a vector PDF schematic.";
        public const string ButtonCaption = "PDF to Schematic";

        /// <summary>EwAPI application object (IEwApplicationX), set by the SWEETs host.</summary>
        internal static object Application { get; private set; }

        private static object _commandManager;
        private static ImportPdfCommand _command;

        public static string CommandName => ImportPdfCommand.CommandName;

        /// <summary>Registers the command with SOLIDWORKS Electrical. Returns the command the ribbon button runs.</summary>
        public static string Connect(object application)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));
            Application = application;

            var problems = SweApi.CheckContract();
            FileLog.Write("addin", $"Connected through SWEETs. Interop: {SweApi.InteropDescription}. Contract issues: {(problems.Count == 0 ? "none" : string.Join(" / ", problems))}");

            _commandManager = SweApi.Call(application, C.AppGetCommandManager);
            _command = new ImportPdfCommand();
            RegisterCommand();
            return CommandName;
        }

        /// <summary>Removes the command (SWEETs host shutdown, or the tool was turned off in SWEETs Settings).</summary>
        public static void Disconnect()
        {
            try
            {
                if (_commandManager != null) SweApi.TryCall(_commandManager, C.CmdRemove, out _, CommandName);
            }
            catch (Exception ex) { FileLog.Write("addin", ex); }
            finally
            {
                _command = null; _commandManager = null; Application = null;
                FileLog.Write("addin", "Disconnected.");
            }
        }

        /// <summary>
        /// A leftover registration from an earlier connect is removed first; flag sets are tried in the order SWE's
        /// own add-ins use them, and every attempt is logged.
        /// </summary>
        private static void RegisterCommand()
        {
            SweApi.TryCall(_commandManager, C.CmdRemove, out _, CommandName);
            Exception last = null;
            foreach (var flags in ImportPdfCommand.FlagCandidates)
            {
                _command.Flags = flags;
                try
                {
                    SweApi.Call(_commandManager, C.CmdAdd, _command);
                    FileLog.Write("addin", $"Command '{CommandName}' added (flags 0x{flags:X}).");
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    FileLog.Write("addin", $"addCommand '{CommandName}' flags 0x{flags:X}: {ex.Message}");
                }
            }
            throw new SweApiException("addCommand rejected every flag set — see the lines above in this log.", last);
        }
    }
}
