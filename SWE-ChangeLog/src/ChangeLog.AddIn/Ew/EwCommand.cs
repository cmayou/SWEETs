using System;
using System.Runtime.InteropServices;
using ChangeLog.AddIn.Host;
using EwAPI;

namespace ChangeLog.AddIn.Ew
{
    /// <summary>
    /// A SWE command (what a ribbon button invokes), exposed to SWE through a COM-callable wrapper.
    /// Mirrors Dassault's ElectricalAddinSDK.Commands.AbstractCommand: MarshalByRefObject, flags
    /// 0x1200001 (Transparent | Session | NoUndoMarker), and a plain alphanumeric command name
    /// (SWE rejects other names in addCommand with EW_BAD_INPUTS).
    /// </summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    // tlbimp exposes the coclass interface EwCommandX in IEwCommandManagerX.addCommand(...), so implement that.
    public sealed class EwCommand : MarshalByRefObject, EwCommandX
    {
        private readonly string _name;
        private readonly string _description;
        private readonly int _flags;
        private readonly Action _action;

        /// <summary>Same flags as Dassault's SDK commands: kCommandTransparent | kCommandSession | kCommandNoUndoMarker.</summary>
        public const int DefaultFlags = 0x1200001;

        public EwCommand(string name, string description, Action action, int flags = DefaultFlags)
        {
            _name = name;
            _description = description;
            _action = action;
            _flags = flags;
        }

        /// <summary>Keep the CCW-backed object alive for the add-in's lifetime (no remoting lease expiry).</summary>
        public override object InitializeLifetimeService() => null;

        public string Name => _name;

        public string getName(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return _name;
        }

        public string getDescription(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return _description;
        }

        public int getFlags(out EwErrorCode errorCode)
        {
            errorCode = EwErrorCode.EW_NO_ERROR;
            return _flags;
        }

        public EwErrorCode execute(EwCommandContextX iCommandContextX)
        {
            try
            {
                UiThread.EnsureContext();
                _action();
                return EwErrorCode.EW_NO_ERROR;
            }
            catch (Exception ex)
            {
                Log.Error("Command " + _name + " failed", ex);
                AddInHost.Current?.Ui.Notify(ex.Message, NotificationKind.Error);
                return EwErrorCode.EW_UNDEFINED_ERROR;
            }
        }
    }
}
