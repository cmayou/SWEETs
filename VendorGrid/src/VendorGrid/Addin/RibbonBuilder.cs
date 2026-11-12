using System;
using EwAPI;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.Addin
{
    /// <summary>
    /// Registers (and removes) the VendorGrid command with SOLIDWORKS Electrical's command manager. The ribbon button is
    /// created by the SWEETs host on the SWEETs tab ("Export Tools" group).
    /// Uses the typed interop interfaces when the host object supports them and falls back to late binding otherwise.
    /// </summary>
    internal static class RibbonBuilder
    {
        public static void RegisterCommand(object app)
        {
            var typed = app as IEwApplicationX;
            if (typed != null)
            {
                Log.Info("Command: typed interop");
                EwErrorCode err;
                var commands = typed.getEwCommandManager(out err);
                Check(err, "getEwCommandManager");
                commands.removeCommand(Defaults.CommandName);   // left over from an earlier connect in this session
                Check(commands.addCommand(new SearchCommand()), "addCommand");
                return;
            }

            Log.Info("Command: late binding");
            int code;
            var late = EwLate.CallOut(app, "getEwCommandManager", out code);
            Check(code, "getEwCommandManager");
            try { EwLate.Call(late, "removeCommand", Defaults.CommandName); } catch (Exception ex) { Log.Info("removeCommand: " + ex.Message); }
            Check(Convert.ToInt32(EwLate.Call(late, "addCommand", new SearchCommand())), "addCommand");
        }

        public static void RemoveCommand(object app)
        {
            var typed = app as IEwApplicationX;
            if (typed != null)
            {
                EwErrorCode err;
                var commands = typed.getEwCommandManager(out err);
                if (err == EwErrorCode.EW_NO_ERROR && commands != null) commands.removeCommand(Defaults.CommandName);
                return;
            }

            int code;
            var late = EwLate.CallOut(app, "getEwCommandManager", out code);
            if (EwLate.Ok(code) && late != null) EwLate.Call(late, "removeCommand", Defaults.CommandName);
        }

        static void Check(EwErrorCode code, string what)
        {
            if (code != EwErrorCode.EW_NO_ERROR) throw new InvalidOperationException(what + " failed: " + code);
        }

        static void Check(int code, string what)
        {
            if (!EwLate.Ok(code)) throw new InvalidOperationException(what + " failed: error " + code);
        }
    }
}
