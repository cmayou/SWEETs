using System.Collections.Generic;
using System.Linq;

namespace SWEETs.AddIn.Interop
{
    /// <summary>
    /// Every EwAPI member the SWEETs host calls. All of them are already used by one of the four tools and were
    /// checked there against the SOLIDWORKS Electrical 2026 type library (EwAPIX.dll):
    ///   - application, command manager and ribbon tab/group/button members: SWEETs Drawing Parameters (SweContract.cs);
    ///   - setDescription, setStyle and IEwRibbonX.recalcLayout: Data Sheet MFG Parts Import (Interop\EwInterop.cs,
    ///     generated from the type library).
    /// <see cref="SweApi.CheckContract"/> re-checks them at startup and writes any mismatch to the SWEETs log.
    /// </summary>
    public static class SweContract
    {
        public sealed class Member
        {
            public string Iface;
            public string[] Names;
            public bool Optional;
            public override string ToString() => $"{Iface}.{Names[0]}";
        }

        private static readonly List<Member> All = new List<Member>();
        private static Member M(string iface, bool optional, params string[] names)
        {
            var m = new Member { Iface = iface, Names = names, Optional = optional };
            All.Add(m);
            return m;
        }

        public static IEnumerable<(string iface, string[] names, bool optional)> Members => All.Select(m => (m.Iface, m.Names, m.Optional));

        // ---- Application
        public static readonly Member AppGetCommandManager = M("IEwApplicationX", false, "getEwCommandManager");
        public static readonly Member AppGetRibbon = M("IEwApplicationX", true, "getEwRibbon");

        // ---- Commands
        public static readonly Member CmdAdd = M("IEwCommandManagerX", false, "addCommand");
        public static readonly Member CmdRemove = M("IEwCommandManagerX", true, "removeCommand");

        // ---- Ribbon
        public static readonly Member RibbonAddTab = M("IEwRibbonX", true, "addTab");
        public static readonly Member RibbonRecalcLayout = M("IEwRibbonX", true, "recalcLayout");
        public static readonly Member RibbonTabAddGroup = M("IEwRibbonTabX", true, "addGroup");
        public static readonly Member RibbonTabRemove = M("IEwRibbonTabX", true, "remove");
        public static readonly Member RibbonGroupAddControl = M("IEwRibbonGroupX", true, "addControl");
        public static readonly Member ControlSetCaption = M("IEwRibbonControlX", true, "setCaption");
        public static readonly Member ControlSetToolTip = M("IEwRibbonControlX", true, "setToolTip");
        public static readonly Member ControlSetDescription = M("IEwRibbonControlX", true, "setDescription");
        public static readonly Member ControlSetIcon = M("IEwRibbonControlX", true, "setIconFromImage");
        public static readonly Member ControlSetStyle = M("IEwRibbonControlX", true, "setStyle");
    }
}
