using System;
using System.Collections.Generic;
using ChangeLog.AddIn.Host;
using EwAPI;

namespace ChangeLog.AddIn.Ew
{
    /// <summary>Declarative description of one ribbon button.</summary>
    public sealed class RibbonButton
    {
        public string Group { get; set; }
        public string Command { get; set; }
        public string Caption { get; set; }
        public string Tooltip { get; set; }
        public string Icon { get; set; }
        public bool Large { get; set; }
        public Action Action { get; set; }
    }

    /// <summary>
    /// Registers Change Log's commands with SWE's command manager and removes them on unload. The buttons themselves
    /// are placed by the SWEETs host, in the "Project Tools" group of the SWEETs tab.
    /// </summary>
    public sealed class RibbonBuilder
    {
        private readonly IEwApplicationX _app;
        private readonly List<RibbonButton> _registered = new List<RibbonButton>();

        public RibbonBuilder(IEwApplicationX app)
        {
            _app = app;
        }

        /// <summary>The buttons whose command SWE accepted, in the order given.</summary>
        public IList<RibbonButton> Registered => _registered;

        public void RegisterCommands(IEnumerable<RibbonButton> buttons)
        {
            EwErrorCode e;
            var commands = _app.getEwCommandManager(out e);
            Com.Check(e, "getEwCommandManager");
            foreach (var b in buttons)
            {
                commands.removeCommand(b.Command);   // left over from an earlier connect in this session
                var add = commands.addCommand(new EwCommand(b.Command, b.Tooltip, b.Action));
                if (add != EwErrorCode.EW_NO_ERROR)
                {
                    Log.Error("addCommand '" + b.Command + "' rejected by SWE: " + add + " (button left off the ribbon)", null);
                    continue;
                }

                _registered.Add(b);
                Log.Info("Registered command '" + b.Command + "'");
            }
        }

        public void Remove()
        {
            EwErrorCode e;
            try
            {
                var commands = _app.getEwCommandManager(out e);
                foreach (var b in _registered) commands.removeCommand(b.Command);
            }
            catch (Exception ex)
            {
                Log.Warn("Command cleanup failed: " + ex.Message);
            }

            _registered.Clear();
        }
    }
}
