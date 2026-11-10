using System.Collections.Generic;
using System.Linq;

namespace SWEETs.SweLineStyles.AddIn.Interop
{
    /// <summary>
    /// Every EwAPI member this add-in calls: only the command manager and the ribbon. The same members are used by
    /// the SWEETs Drawing Parameters add-in (checked against the SOLIDWORKS Electrical 2026 interop, bin\Interop.EwAPI.dll);
    /// <see cref="SweApi.CheckContract"/> re-checks them at startup and logs any mismatch.
    /// Line styles themselves are files, not API objects: see Services\LineStyleLibrary.cs.
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

        // ---- Commands and ribbon
        public static readonly Member CmdAdd = M("IEwCommandManagerX", false, "addCommand");
        public static readonly Member CmdRemove = M("IEwCommandManagerX", true, "removeCommand");
        public static readonly Member RibbonAddTab = M("IEwRibbonX", true, "addTab");
        public static readonly Member RibbonTabCount = M("IEwRibbonX", true, "getTabCount");
        public static readonly Member RibbonTabAt = M("IEwRibbonX", true, "getTabAt");
        public static readonly Member RibbonTabCaption = M("IEwRibbonTabX", true, "getCaption");
        public static readonly Member RibbonTabAddGroup = M("IEwRibbonTabX", true, "addGroup");
        public static readonly Member RibbonTabRemove = M("IEwRibbonTabX", true, "remove");
        public static readonly Member RibbonTabGroupCount = M("IEwRibbonTabX", true, "getGroupCount");
        public static readonly Member RibbonTabRemoveGroup = M("IEwRibbonTabX", true, "removeGroup");
        public static readonly Member RibbonTabGroupAt = M("IEwRibbonTabX", true, "getGroupAt");
        public static readonly Member RibbonGroupCaption = M("IEwRibbonGroupX", true, "getCaption");
        public static readonly Member RibbonGroupAddControl = M("IEwRibbonGroupX", true, "addControl");
        public static readonly Member ControlSetCaption = M("IEwRibbonControlX", true, "setCaption");
        public static readonly Member ControlSetToolTip = M("IEwRibbonControlX", true, "setToolTip");
        public static readonly Member ControlSetIcon = M("IEwRibbonControlX", true, "setIconFromImage");
    }
}
