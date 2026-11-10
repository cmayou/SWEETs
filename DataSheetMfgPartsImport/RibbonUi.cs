using System;
using System.Collections.Generic;
using System.IO;
using DataSheetMfgPartsImport.Interop;

namespace DataSheetMfgPartsImport
{
    /// <summary>Adds a "Data Sheet MFG Parts Import" tab with one button to the SOLIDWORKS Electrical ribbon.</summary>
    internal static class RibbonUi
    {
        const string TabCaption = "Data Sheet MFG Parts Import";
        const string GroupCaption = "Data sheets";
        const uint MagentaMask = 0x00FF00FF;   // COLORREF (BGR) of the transparent colour; not used by the icons, which carry real transparency

        static IEwRibbonTabX _tab;
        static bool _added;

        public static bool IsAdded { get { return _added; } }

        public static bool TryAdd(IEwApplicationX app, string command)
        {
            if (_added) return true;
            IEwRibbonTabX tab = null;
            try
            {
                EwErrorCode ec;
                var ribbon = app.getEwRibbon(out ec);
                if (ec != EwErrorCode.EW_NO_ERROR || ribbon == null)
                {
                    Log.Write("Ribbon not available yet: getEwRibbon returned " + ec + (ribbon == null ? " (null)" : ""));
                    return false;
                }

                tab = ribbon.addTab(TabCaption, out ec);
                if (ec != EwErrorCode.EW_NO_ERROR || tab == null)
                {
                    Log.Write("addTab returned " + ec);
                    return false;
                }

                var group = tab.addGroup(GroupCaption, out ec);
                if (ec != EwErrorCode.EW_NO_ERROR || group == null)
                {
                    Log.Write("addGroup returned " + ec);
                    Discard(tab);
                    return false;
                }

                var button = group.addControl(EwRibbonControlType.kControlButton, command, out ec);
                if (ec != EwErrorCode.EW_NO_ERROR || button == null)
                {
                    Log.Write("addControl returned " + ec + " for command '" + command + "'");
                    Discard(tab);
                    return false;
                }

                Note(button.setCaption("Add part from data sheet"), "setCaption");
                Note(button.setToolTip("Read a manufacturer data sheet and add its parts to the manufacturer parts manager"), "setToolTip");
                Note(button.setDescription("Reads a manufacturer data sheet (a PDF or a photo), lets you review the extracted fields, then adds the parts to the manufacturer parts manager under a library."), "setDescription");
                foreach (var icon in new[]
                {
                    new KeyValuePair<EwRibbonButtonSize, string>(EwRibbonButtonSize.kButtonSize16, "import16.png"),
                    new KeyValuePair<EwRibbonButtonSize, string>(EwRibbonButtonSize.kButtonSize32, "import32.png"),
                    new KeyValuePair<EwRibbonButtonSize, string>(EwRibbonButtonSize.kButtonSize64, "import64.png")
                })
                {
                    string file = AppIcons.PathOf(icon.Value);
                    if (!File.Exists(file)) { Log.Write("Ribbon icon not found: " + file); continue; }
                    try { Note(button.setIconFromImage(file, icon.Key, MagentaMask), "setIconFromImage " + icon.Value); }
                    catch (Exception ex) { Log.Write("Ribbon icon " + icon.Value + " skipped: " + ex.Message); }
                }

                Note(button.setStyle(EwRibbonButtonStyle.kButtonIconAndCaptionBelow), "setStyle");
                Note(ribbon.recalcLayout(), "recalcLayout");

                _tab = tab;
                _added = true;
                Log.Write("Ribbon tab '" + TabCaption + "' added. Button runs: " + command);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("Ribbon setup failed: " + ex.Message);
                Discard(tab);
                return false;
            }
        }

        public static void Remove()
        {
            Discard(_tab);
            _tab = null;
            _added = false;
        }

        static void Discard(IEwRibbonTabX tab)
        {
            if (tab == null) return;
            try { tab.remove(); } catch (Exception ex) { Log.Write("Removing the ribbon tab: " + ex.Message); }
        }

        static void Note(EwErrorCode ec, string what)
        {
            if (ec != EwErrorCode.EW_NO_ERROR) Log.Write("Ribbon " + what + " returned " + ec);
        }
    }
}
