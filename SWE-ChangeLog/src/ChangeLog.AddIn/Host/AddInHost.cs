using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using ChangeLog.AddIn.Ew;
using ChangeLog.Core.Integrity;
using ChangeLog.Core.Model;
using EwAPI;

namespace ChangeLog.AddIn.Host
{
    /// <summary>
    /// Runtime root of Change Log (one per SWE process). Uses the application object the SWEETs host obtained, owns
    /// the SWE commands behind the ribbon buttons, identity, signing key and one <see cref="ProjectMonitor"/> per open
    /// project. SWEETs places the buttons on its tab. All members run on the SWE UI thread.
    /// </summary>
    public sealed class AddInHost : IDisposable
    {
        public const string TabCaption = "Change Log";
        private readonly Dictionary<int, ProjectMonitor> _monitors = new Dictionary<int, ProjectMonitor>();

        private _IEwAppXEvents_Event _appEvents;
        private RibbonBuilder _ribbon;
        private ProjectMonitor _active;

        public AddInHost(IUiService ui)
        {
            Ui = ui;
            Settings = AddInSettings.Load();
        }

        public static AddInHost Current { get; private set; }

        public static string AddInVersion => typeof(AddInHost).Assembly.GetName().Version.ToString();

        public static string InstallFolder => Path.GetDirectoryName(new Uri(typeof(AddInHost).Assembly.CodeBase).LocalPath);

        public IUiService Ui { get; }
        public AddInSettings Settings { get; private set; }
        public IEwApplicationX App { get; private set; }
        public UserIdentity Who { get; private set; }
        public ISigner Signer { get; private set; }
        public string AppVersion { get; private set; }
        /// <summary>The ribbon buttons whose commands were registered with SWE (SWEETs adds these to its tab).</summary>
        public IList<RibbonButton> Commands => _ribbon?.Registered ?? (IList<RibbonButton>)new RibbonButton[0];

        public IEnumerable<ProjectMonitor> Monitors => _monitors.Values;

        public ProjectMonitor Active
        {
            get { return _active; }
            private set
            {
                if (_active == value) return;
                _active = value;
                ActiveChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        public event EventHandler ActiveChanged;
        public event EventHandler MonitorsChanged;

        /// <summary>
        /// Called by the SWEETs host with the EwAPI application it obtained (SWEETs holds the API key, so Change Log no
        /// longer asks for one). Throws when the object is not a SOLIDWORKS Electrical application.
        /// </summary>
        public void Connect(object application)
        {
            Current = this;
            UiThread.EnsureContext();
            Ui.Initialize(this);

            App = application as IEwApplicationX;
            if (App == null) throw new InvalidOperationException("SWEETs did not pass a SOLIDWORKS Electrical application object.");

            AppVersion = Ew.Com.Try(() => App.getApplicationVersion());
            Who = ResolveIdentity(App);
            Signer = new CngEcdsaSigner();
            Log.Info("Connected to SWE " + AppVersion + " as " + Who.DisplayName + " (" + Who.WindowsUser + "@" + Who.Machine + "), signing key " + Signer.KeyId);

            _ribbon = new RibbonBuilder(App);
            _ribbon.RegisterCommands(Buttons());

            _appEvents = App as _IEwAppXEvents_Event;
            if (_appEvents != null)
            {
                _appEvents.openProjectNotify += OnProjectOpened;
                _appEvents.onSetActiveProjectNotify += OnProjectActivated;
                _appEvents.onAfterCloseProjectNotify += OnProjectClosed;
                _appEvents.onCloseApplication += OnApplicationClosing;
            }
            else
            {
                Log.Warn("Application does not expose _IEwAppXEvents; projects opened later will not be monitored.");
            }

            // A project may already be open when the add-in loads.
            EwErrorCode e;
            var current = Ew.Com.Try(() => App.getEwProjectCurrent(out e));
            if (current != null) OnProjectOpened(current);
        }

        public void ApplySettings(AddInSettings s)
        {
            s.Save();
            Settings = s;
            Ui.Notify("Settings saved. Storage and logging options apply to projects opened from now on.", NotificationKind.Success);
        }

        private IEnumerable<RibbonButton> Buttons()
        {
            return new[]
            {
                new RibbonButton { Group = "Review", Command = "changelogShow", Caption = "Change Log", Icon = "changelog", Large = true,
                    Tooltip = "Open the change log for the active project", Action = () => Ui.ShowChangeLog() },
                new RibbonButton { Group = "Review", Command = "changelogCompare", Caption = "Compare", Icon = "compare", Large = true,
                    Tooltip = "Visually compare two captures (or a capture and the live project)", Action = () => Ui.ShowCompare() },
                new RibbonButton { Group = "Capture", Command = "changelogCapture", Caption = "Capture Changes", Icon = "baseline", Large = true,
                    Tooltip = "Store the current project state so it can be compared later", Action = CaptureChanges },
                new RibbonButton { Group = "Export", Command = "changelogExport", Caption = "Export to Excel", Icon = "export", Large = true,
                    Tooltip = "Export the (filtered) change log to .xlsx", Action = () => Ui.ExportToExcel() },
                new RibbonButton { Group = "Integrity", Command = "changelogVerify", Caption = "Verify", Icon = "verify", Large = true,
                    Tooltip = "Verify that no log entry was altered, removed or forged", Action = VerifyActive },
                new RibbonButton { Group = "Integrity", Command = "changelogSettings", Caption = "Settings", Icon = "settings", Large = true,
                    Tooltip = "Recording, storage and integrity options", Action = () => Ui.ShowSettings() }
            };
        }

        /// <summary>
        /// Stores a comparable copy of the current project state ("capture"). Deliberately NOT a native SWE
        /// Snapshot: SWE only snapshots closed projects, and closing a project behind the user's back can
        /// break other tools attached to it (PDM, SOLIDWORKS 3D, other add-ins).
        /// </summary>
        public void CaptureChanges()
        {
            var m = RequireActive();
            if (m == null) return;
            Ui.Notify("Capturing '" + m.Name + "'…", NotificationKind.Info);
            m.CaptureBaseline("Capture " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), BaselineKind.Manual, null,
                b => Ui.Notify("Captured '" + b.Name + "' (" + b.Payload.ObjectCount + " objects).", NotificationKind.Success));
        }

        public void VerifyActive()
        {
            var m = RequireActive();
            if (m == null) return;
            var r = m.Session.VerifyNow();
            Ui.Notify(r.Summary, r.Status == IntegrityStatus.Tampered ? NotificationKind.Error
                : r.Status == IntegrityStatus.Warning ? NotificationKind.Warning : NotificationKind.Success);
            Ui.ShowChangeLog();
        }

        public ProjectMonitor RequireActive()
        {
            if (Active != null) return Active;
            Ui.Notify("Open a project first.", NotificationKind.Warning);
            return null;
        }

        // ------------------------------------------------------------ app events

        private void OnProjectOpened(IEwProjectX project)
        {
            try
            {
                var id = project.getID();
                ProjectMonitor m;
                if (!_monitors.TryGetValue(id, out m))
                {
                    m = new ProjectMonitor(this, project);
                    _monitors[id] = m;
                    MonitorsChanged?.Invoke(this, EventArgs.Empty);
                }

                Active = m;
            }
            catch (Exception ex)
            {
                Log.Error("Starting change log for opened project failed", ex);
                Ui.Notify("Change log could not start for this project: " + ex.Message, NotificationKind.Error);
            }
        }

        private void OnProjectActivated(IEwProjectX project)
        {
            ProjectMonitor m;
            var id = Ew.Com.Try(() => project.getID(), -1);
            if (_monitors.TryGetValue(id, out m)) Active = m;
            else OnProjectOpened(project);
        }

        private void OnProjectClosed(int projectId)
        {
            ProjectMonitor m;
            if (!_monitors.TryGetValue(projectId, out m)) return;
            _monitors.Remove(projectId);
            m.Dispose();
            if (Active == m) Active = _monitors.Values.FirstOrDefault();
            MonitorsChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnApplicationClosing() => Dispose();

        private static UserIdentity ResolveIdentity(IEwApplicationX app)
        {
            EwErrorCode e;
            var windows = WindowsIdentity.GetCurrent().Name;
            // When SWE user rights are enabled this is the SWE login; otherwise empty.
            var swe = Ew.Com.Try(() => app.getEwRightsManager(out e).getCurrentUserDescription());
            return new UserIdentity(string.IsNullOrWhiteSpace(swe) ? windows : swe.Trim(), windows, Environment.MachineName);
        }

        public void Dispose()
        {
            foreach (var m in _monitors.Values.ToList())
            {
                try { m.Dispose(); }
                catch (Exception ex) { Log.Error("Monitor dispose failed", ex); }
            }

            _monitors.Clear();
            if (_appEvents != null)
            {
                try
                {
                    _appEvents.openProjectNotify -= OnProjectOpened;
                    _appEvents.onSetActiveProjectNotify -= OnProjectActivated;
                    _appEvents.onAfterCloseProjectNotify -= OnProjectClosed;
                    _appEvents.onCloseApplication -= OnApplicationClosing;
                }
                catch (Exception ex)
                {
                    Log.Warn("Unsubscribing app events failed: " + ex.Message);
                }

                _appEvents = null;
            }

            _ribbon?.Remove();
            _ribbon = null;
            (Signer as IDisposable)?.Dispose();
            Ui.Shutdown();
            if (Current == this) Current = null;
        }
    }
}
