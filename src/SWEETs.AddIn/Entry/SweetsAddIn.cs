using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using EwAPI;
using Microsoft.Win32;
using SWEETs.AddIn.Infrastructure;
using SWEETs.AddIn.Interop;
using SWEETs.AddIn.Settings;
using C = SWEETs.AddIn.Interop.SweContract;

namespace SWEETs.AddIn.Entry
{
    /// <summary>
    /// SWEETs: the one SOLIDWORKS Electrical 2026 add-in that hosts every SWEETs tool.
    ///
    /// On connect it gets the EwAPI application once, connects each tool that is turned on in SWEETs Settings
    /// (each tool registers its own command, as it did stand-alone) and builds a single "SWEETs" ribbon tab:
    ///   Drawing Tools : Drawing Parameters, Line Style Editor
    ///   Import Tools  : PDF to Schematic, Datasheet Importer
    ///   Export Tools  : VendorGrid (Vendor Search)
    ///   Project Tools : Change Log (Change Log, Compare, Capture Changes, Export to Excel, Verify, Settings)
    ///   Settings      : Settings (turns tools on and off)
    /// A tool that fails to connect is logged and left off the tab; the others still load.
    /// </summary>
    [ComVisible(true)]
    [Guid(AddInGuid)]
    [ClassInterface(ClassInterfaceType.None)]
    [ProgId("SWEETs.AddIn")]
    public class SweetsAddIn : EwAddIn
    {
        public const string AddInGuid = "3ed791fd-feac-4081-99b5-ef363b2fa108";
        public const string Title = "SWEETs";
        public const string Description = "SOLIDWORKS Electrical Efficiency Tools: drawing parameters, line styles, PDF to schematic, datasheet import, vendor search and change log.";
        public const string RibbonTab = "SWEETs";

        internal static object Application { get; private set; }
        internal static SweetsAddIn Active => _active;

        internal List<ToolDefinition> Tools { get; private set; }
        internal SweetsSettings Settings { get; private set; }

        private object _commandManager;
        private object _ribbon;
        private object _tab;
        private SettingsCommand _settingsCommand;
        private System.Windows.Forms.Timer _ribbonRetry;
        private int _ribbonTries;

        static SweetsAddIn()
        {
            // SWE is a native host: the CLR does not probe the add-in folder. Every tool's libraries (ACadSharp,
            // PdfPig, System.Text.Json, System.Memory, …) live next to this DLL.
            AppDomain.CurrentDomain.AssemblyResolve += ResolveFromAddInFolder;
        }

        /// <summary>
        /// Returns an already-loaded assembly with the requested simple name (XAML pack URIs ask for the tool
        /// assemblies by name, and two tools may have been compiled against different versions of a shared library),
        /// otherwise loads it from the add-in folder, ignoring the version.
        /// </summary>
        private static Assembly ResolveFromAddInFolder(object sender, ResolveEventArgs args)
        {
            try
            {
                var name = new AssemblyName(args.Name).Name;
                if (name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                    if (string.Equals(loaded.GetName().Name, name, StringComparison.OrdinalIgnoreCase)) return loaded;
                var file = Path.Combine(AppPaths.AddInFolder, name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            }
            catch { return null; }
        }

        // ---- EwAddIn (connectToEwAPI / disconnectFromEwApi in the SWE 2026 type library)

        private static SweetsAddIn _active;

        public bool connectToEwAPI(object ewInteropFactory)
        {
            // A duplicate registration makes SWE create a second instance: keep it inert (lesson from PDF to Schematic:
            // two instances fight over the command names and SWE disconnects the same add-in twice on exit).
            if (_active != null && !ReferenceEquals(_active, this))
            {
                FileLog.Write("addin", "Second add-in instance ignored (duplicate registration). Re-run the SWEETs installer to clean the registry.");
                return true;
            }
            try
            {
                _active = this;
                var migrated = DataMigration.Run();   // before anything writes to the new SWEETs folders
                FileLog.Write("addin", $"SWEETs {typeof(SweetsAddIn).Assembly.GetName().Version} connecting. Folder: {AppPaths.AddInFolder}");
                if (migrated != null) FileLog.Write("migration", migrated);
                Application = GetApplication(ewInteropFactory);

                var problems = SweApi.CheckContract();
                FileLog.Write("addin", $"Interop: {SweApi.InteropDescription}. Contract issues: {(problems.Count == 0 ? "none" : string.Join(" / ", problems))}");

                _commandManager = SweApi.Call(Application, C.AppGetCommandManager);
                _settingsCommand = new SettingsCommand();
                RegisterSettingsCommand();

                Tools = ToolCatalog.Create();
                Settings = SweetsSettings.Load();
                foreach (var tool in Tools.Where(t => Settings.IsEnabled(t.Id))) ConnectTool(tool);

                if (!TryBuildRibbon()) StartRibbonRetry();
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Write("addin", ex);
                _active = null;
                return false;
            }
        }

        public bool disconnectFromEwApi()
        {
            if (!ReferenceEquals(_active, this)) return true;   // inert duplicate: nothing to undo
            try
            {
                StopRibbonRetry();
                RemoveRibbon();
                if (Tools != null)
                    foreach (var tool in Tools.Where(t => t.Connected)) DisconnectTool(tool);
                if (_commandManager != null) SweApi.TryCall(_commandManager, C.CmdRemove, out _, SettingsCommand.CommandName);
            }
            catch (Exception ex) { FileLog.Write("addin", ex); }
            finally
            {
                _tab = null; _ribbon = null; _commandManager = null; _settingsCommand = null; Application = null; _active = null;
                FileLog.Write("addin", "Disconnected.");
            }
            return true;
        }

        // ---- Application

        /// <summary>
        /// SOLIDWORKS Electrical API keys, passed to IEwInteropFactoryX.getEwApplication, tried in order (every attempt is
        /// logged). They are not in the repository: they come from Secrets\SweApiKeys.local.cs, which git ignores (see
        /// Secrets\SweApiKeys.template.cs and README "API key").
        /// </summary>
        private static readonly string[] SweApiKeys = Secrets.SweApiKeys.Values;

        private static object GetApplication(object factory)
        {
            FileLog.Write("addin", $"Factory object: {factory?.GetType().FullName ?? "null"} (COM: {(factory != null && Marshal.IsComObject(factory))})");
            var keys = SweApiKeys.Concat(new[] { "" }).ToArray();
            for (int k = 0; k < keys.Length; k++)
            {
                var key = keys[k];
                string label = key.Length == 0 ? "empty key" : $"API key {k + 1}";
                try
                {
                    if (factory is IEwInteropFactoryX ef)
                    {
                        var app = ef.getEwApplication(key, out EwErrorCode err);
                        FileLog.Write("addin", $"IEwInteropFactoryX.getEwApplication ({label}) → {err}, app {(app == null ? "null" : "ok")}");
                        if (app != null && err == EwErrorCode.EW_NO_ERROR) return app;
                    }
                    else FileLog.Write("addin", "Factory does not implement IEwInteropFactoryX.");
                }
                catch (Exception ex) { FileLog.Write("addin", $"IEwInteropFactoryX.getEwApplication ({label}) threw {ex.GetType().Name}: {ex.Message}"); }

                try
                {
                    dynamic f = factory;
                    EwErrorCode err = EwErrorCode.EW_NO_ERROR;
                    object app = f.getEwApplication(key, out err);
                    FileLog.Write("addin", $"IDispatch getEwApplication ({label}) → {err}, app {(app == null ? "null" : "ok")}");
                    if (app != null && err == EwErrorCode.EW_NO_ERROR) return app;
                }
                catch (Exception ex) { FileLog.Write("addin", $"IDispatch getEwApplication ({label}) threw {ex.GetType().Name}: {ex.Message}"); }
            }
            throw new SweApiException("getEwApplication failed with every key and strategy — see the lines above in this log.");
        }

        // ---- Tools

        private static void ConnectTool(ToolDefinition tool)
        {
            try
            {
                if (tool.ConnectButtons != null)
                    tool.Buttons = tool.ConnectButtons(Application) ?? new List<ToolButton>();
                else
                    tool.Buttons = new List<ToolButton>
                    {
                        new ToolButton
                        {
                            Command = tool.Connect(Application),
                            Caption = tool.Caption,
                            ToolTip = tool.ToolTip,
                            Description = tool.Description,
                            Icons = tool.Icons,
                        },
                    };
                tool.Connected = true;
                tool.LastError = null;
                FileLog.Write("tools", $"{tool.Id}: connected, button(s) run {string.Join(", ", tool.Buttons.Select(b => "'" + b.Command + "'"))}.");
            }
            catch (Exception ex)
            {
                tool.Connected = false;
                tool.LastError = (ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex).Message;
                FileLog.Write("tools", $"{tool.Id}: could not connect — {ex}");
            }
        }

        private static void DisconnectTool(ToolDefinition tool)
        {
            try { tool.Disconnect(); }
            catch (Exception ex) { FileLog.Write("tools", $"{tool.Id}: disconnect — {ex.Message}"); }
            tool.Connected = false;
            tool.Buttons = new List<ToolButton>();
            FileLog.Write("tools", $"{tool.Id}: disconnected.");
        }

        /// <summary>
        /// Applies the settings saved in the Settings window: connects tools that were turned on, disconnects tools
        /// that were turned off, then rebuilds the SWEETs tab. Called after the Settings command has returned to
        /// SOLIDWORKS Electrical (see <see cref="SettingsCommand"/>), so the tab that holds the Settings button is
        /// not removed while that button's command is still running.
        /// </summary>
        internal void ApplySettings(SweetsSettings settings)
        {
            Settings = settings;
            foreach (var tool in Tools)
            {
                bool on = settings.IsEnabled(tool.Id);
                if (on && !tool.Connected) ConnectTool(tool);
                else if (!on && tool.Connected) DisconnectTool(tool);
            }
            StopRibbonRetry();
            RemoveRibbon();
            if (!TryBuildRibbon()) StartRibbonRetry();
        }

        // ---- Commands

        private void RegisterSettingsCommand()
        {
            SweApi.TryCall(_commandManager, C.CmdRemove, out _, SettingsCommand.CommandName);
            Exception last = null;
            foreach (var flags in SettingsCommand.FlagCandidates)
            {
                _settingsCommand.Flags = flags;
                try
                {
                    SweApi.Call(_commandManager, C.CmdAdd, _settingsCommand);
                    FileLog.Write("addin", $"Command '{SettingsCommand.CommandName}' added (flags 0x{flags:X}).");
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    FileLog.Write("addin", $"addCommand '{SettingsCommand.CommandName}' flags 0x{flags:X}: {ex.Message}");
                }
            }
            throw new SweApiException("addCommand rejected every flag set — see the lines above in this log.", last);
        }

        // ---- Ribbon

        /// <summary>
        /// Builds the SWEETs tab. The ribbon can be missing while SOLIDWORKS Electrical is still starting
        /// (seen with Data Sheet MFG Parts Import), so false means "try again shortly".
        /// </summary>
        private bool TryBuildRibbon()
        {
            object ribbon;
            try
            {
                if (!SweApi.TryCall(Application, C.AppGetRibbon, out ribbon) || ribbon == null)
                {
                    FileLog.Write("ribbon", "Ribbon not available yet.");
                    return false;
                }
            }
            catch (Exception ex) { FileLog.Write("ribbon", "getEwRibbon: " + ex.Message); return false; }

            try
            {
                _tab = SweApi.Call(ribbon, C.RibbonAddTab, RibbonTab);
                _ribbon = ribbon;

                foreach (var groupCaption in ToolCatalog.Groups)
                {
                    var tools = Tools.Where(t => t.Group == groupCaption && t.Connected && Settings.IsEnabled(t.Id)).ToList();
                    if (tools.Count == 0) continue;   // a group with every tool turned off is left out
                    var group = SweApi.Call(_tab, C.RibbonTabAddGroup, groupCaption);
                    foreach (var tool in tools)
                        foreach (var button in tool.Buttons)
                        {
                            try { AddButton(group, button.Command, button.Caption, button.ToolTip, button.Description, button.Icons ?? new (string, string)[0], tool.IconMask); }
                            catch (Exception ex) { FileLog.Write("ribbon", $"{tool.Id}: button '{button.Caption}' not added — {ex.Message}"); }
                        }
                }

                var settingsGroup = SweApi.Call(_tab, C.RibbonTabAddGroup, ToolCatalog.SettingsGroup);
                AddButton(settingsGroup, SettingsCommand.CommandName, "Settings", "Turn SWEETs tools on or off",
                    "Choose which SWEETs tools appear on this tab.",
                    new[] { ("kButtonSize16", @"Resources\settings-16.png"), ("kButtonSize32", @"Resources\settings-32.png") }, 0x00FF00FFu);

                SweApi.TryCall(ribbon, C.RibbonRecalcLayout, out _);
                var shown = Tools.Where(t => t.Connected && Settings.IsEnabled(t.Id)).SelectMany(t => t.Buttons.Select(b => b.Caption)).Concat(new[] { "Settings" });
                FileLog.Write("ribbon", $"Tab '{RibbonTab}' built: {string.Join(", ", shown)}.");
                return true;
            }
            catch (Exception ex)
            {
                FileLog.Write("ribbon", "Building the tab failed: " + ex.Message);
                RemoveRibbon();
                return false;
            }
        }

        private static void AddButton(object group, string command, string caption, string toolTip, string description,
                                      (string size, string file)[] icons, uint mask)
        {
            var control = SweApi.Call(group, C.RibbonGroupAddControl, SweApi.EnumValue("EwRibbonControlType", "kControlButton"), command);
            SweApi.TryCall(control, C.ControlSetCaption, out _, caption);
            SweApi.TryCall(control, C.ControlSetToolTip, out _, toolTip);
            SweApi.TryCall(control, C.ControlSetDescription, out _, description);
            foreach (var (size, file) in icons)
            {
                var path = Path.Combine(AppPaths.AddInFolder, file);
                if (!File.Exists(path)) { FileLog.Write("ribbon", "Icon not found: " + path); continue; }
                try
                {
                    if (!SweApi.TryCall(control, C.ControlSetIcon, out _, path, SweApi.EnumValue("EwRibbonButtonSize", size), mask))
                        FileLog.Write("ribbon", $"setIconFromImage {file} ({size}) failed.");
                }
                catch (Exception ex) { FileLog.Write("ribbon", $"Icon {file} skipped: {ex.Message}"); }
            }
            try { SweApi.TryCall(control, C.ControlSetStyle, out _, SweApi.EnumValue("EwRibbonButtonStyle", "kButtonIconAndCaptionBelow")); }
            catch (Exception ex) { FileLog.Write("ribbon", "setStyle skipped: " + ex.Message); }
        }

        private void RemoveRibbon()
        {
            if (_tab != null) SweApi.TryCall(_tab, C.RibbonTabRemove, out _);
            if (_ribbon != null) SweApi.TryCall(_ribbon, C.RibbonRecalcLayout, out _);
            _tab = null;
        }

        private void StartRibbonRetry()
        {
            try
            {
                _ribbonTries = 0;
                _ribbonRetry = new System.Windows.Forms.Timer { Interval = 2000 };
                _ribbonRetry.Tick += (s, e) =>
                {
                    _ribbonTries++;
                    bool done = Application == null || TryBuildRibbon() || _ribbonTries >= 30;
                    if (!done) return;
                    if (_tab == null) FileLog.Write("ribbon", $"Gave up adding the SWEETs tab after {_ribbonTries} tries.");
                    StopRibbonRetry();
                };
                _ribbonRetry.Start();
            }
            catch (Exception ex) { FileLog.Write("ribbon", "Could not start the ribbon retry timer: " + ex.Message); }
        }

        private void StopRibbonRetry()
        {
            if (_ribbonRetry == null) return;
            _ribbonRetry.Stop();
            _ribbonRetry.Dispose();
            _ribbonRetry = null;
        }

        // ---- COM registration (RegAsm /codebase for development; the installer writes the same keys itself)

        internal static readonly string[] DiscoveryKeys =
        {
            @"SOFTWARE\Trace Software\Electrical\Addins",
            @"SOFTWARE\SolidWorks\SOLIDWORKS Electrical\AddIns",
        };

        /// <summary>Scanned by SWE's add-in manager as well; registering there too loads the add-in twice. Always removed.</summary>
        internal static readonly string[] StaleKeys = { @"SOFTWARE\Trace Software\SOLIDWORKS Electrical\Addins" };
        private const string UserStateKey = @"Software\SolidWorks\SOLIDWORKS Electrical\AddIns";

        [ComRegisterFunction]
        public static void RegisterFunction(Type t)
        {
            var guid = "{" + t.GUID.ToString() + "}";
            foreach (var root in StaleKeys) Registry.LocalMachine.DeleteSubKeyTree(root + "\\" + guid, false);
            foreach (var root in DiscoveryKeys)
                using (var key = Registry.LocalMachine.CreateSubKey(root + "\\" + guid))
                {
                    key.SetValue("", "0");
                    key.SetValue("Title", Title);
                    key.SetValue("Description", Description);
                    key.SetValue("Path", t.Assembly.Location);
                    key.SetValue("StartUp", 1, RegistryValueKind.DWord);
                    key.SetValue("StartShowMsg", 0, RegistryValueKind.DWord);
                }
            using (var key = Registry.CurrentUser.CreateSubKey(UserStateKey + "\\" + guid))
            {
                if (key.GetValue("StartUp") == null) key.SetValue("StartUp", 1, RegistryValueKind.DWord);
                if (key.GetValue("StartShowMsg") == null) key.SetValue("StartShowMsg", 0, RegistryValueKind.DWord);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type t)
        {
            var guid = "{" + t.GUID.ToString() + "}";
            foreach (var root in DiscoveryKeys.Concat(StaleKeys)) Registry.LocalMachine.DeleteSubKeyTree(root + "\\" + guid, false);
            Registry.CurrentUser.DeleteSubKeyTree(UserStateKey + "\\" + guid, false);
        }
    }
}
