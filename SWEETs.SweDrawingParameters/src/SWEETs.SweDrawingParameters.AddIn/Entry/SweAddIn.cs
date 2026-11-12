using System;
using SWEETs.SweDrawingParameters.AddIn.Infrastructure;
using SWEETs.SweDrawingParameters.AddIn.Interop;
using C = SWEETs.SweDrawingParameters.AddIn.Interop.SweContract;

namespace SWEETs.SweDrawingParameters.AddIn.Entry
{
    /// <summary>
    /// SWEETs module entry for "Update Project Drawing Parameters".
    ///
    /// This tool is no longer a stand-alone SOLIDWORKS Electrical add-in: the SWEETs host add-in
    /// (SWEETs.AddIn) gets the EwAPI application once, calls <see cref="Connect"/> to register this tool's
    /// command, and places the ribbon button on the SWEETs tab (Drawing Tools group). The command, dialog and
    /// DWG patching code are unchanged from the stand-alone version 1.1.1.
    /// </summary>
    public static class SweAddIn
    {
        public const string Title = "SWEETs Drawing Parameters";
        public const string Description = "Updates grid, snap, grid zone and ortho on all drawings of a SOLIDWORKS Electrical project.";
        public const string ButtonCaption = "Drawing Parameters";

        /// <summary>EwAPI application object (IEwApplicationX), set by the SWEETs host.</summary>
        internal static object Application { get; private set; }

        private static object _commandManager;
        private static UpdateDrawingParametersCommand _command;

        public static string CommandName => UpdateDrawingParametersCommand.CommandName;

        /// <summary>Registers the command with SOLIDWORKS Electrical. Returns the command the ribbon button runs.</summary>
        public static string Connect(object application)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));
            Application = application;

            var problems = SweApi.CheckContract();
            FileLog.Write("addin", $"Connected through SWEETs. Interop: {SweApi.InteropDescription}. Contract issues: {(problems.Count == 0 ? "none" : string.Join(" / ", problems))}");

            _commandManager = SweApi.Call(application, C.AppGetCommandManager);
            _command = new UpdateDrawingParametersCommand();
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

        private static void RegisterCommand()
        {
            SweApi.TryCall(_commandManager, C.CmdRemove, out _, CommandName);
            Exception last = null;
            foreach (var flags in UpdateDrawingParametersCommand.FlagCandidates)
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
                    FileLog.Write("addin", $"addCommand flags 0x{flags:X}: {ex.Message}");
                }
            }
            throw new SweApiException("addCommand rejected every flag set — see the lines above in this log.", last);
        }
    }
}
