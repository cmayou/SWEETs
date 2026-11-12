using System;
using SWEETs.SweLineStyles.AddIn.Infrastructure;
using SWEETs.SweLineStyles.AddIn.Interop;
using C = SWEETs.SweLineStyles.AddIn.Interop.SweContract;

namespace SWEETs.SweLineStyles.AddIn.Entry
{
    /// <summary>
    /// SWEETs module entry for the Line Style Editor.
    ///
    /// This tool is no longer a stand-alone SOLIDWORKS Electrical add-in: the SWEETs host add-in
    /// (SWEETs.AddIn) gets the EwAPI application once, calls <see cref="Connect"/> to register this tool's
    /// command, and places the ribbon button on the SWEETs tab (Drawing Tools group). The command, editor window
    /// and file code are unchanged from the stand-alone version 1.0.0.
    /// </summary>
    public static class SweAddIn
    {
        public const string Title = "SWEETs Line Style Editor";
        public const string Description = "Edit SOLIDWORKS Electrical line styles and the shapes they use.";
        public const string ButtonCaption = "Line Style Editor";

        /// <summary>EwAPI application object (IEwApplicationX), set by the SWEETs host.</summary>
        internal static object Application { get; private set; }

        private static object _commandManager;
        private static LineStyleEditorCommand _command;

        public static string CommandName => LineStyleEditorCommand.CommandName;

        /// <summary>Registers the command with SOLIDWORKS Electrical. Returns the command the ribbon button runs.</summary>
        public static string Connect(object application)
        {
            if (application == null) throw new ArgumentNullException(nameof(application));
            Application = application;

            var problems = SweApi.CheckContract();
            FileLog.Write("addin", $"Connected through SWEETs. Interop: {SweApi.InteropDescription}. Contract issues: {(problems.Count == 0 ? "none" : string.Join(" / ", problems))}");

            _commandManager = SweApi.Call(application, C.AppGetCommandManager);
            _command = new LineStyleEditorCommand();
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
            foreach (var flags in LineStyleEditorCommand.FlagCandidates)
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
