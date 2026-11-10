using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using ChangeLog.AddIn.Ew;
using ChangeLog.Core;
using ChangeLog.Core.Integrity;
using ChangeLog.Core.Model;
using ChangeLog.Core.Recording;
using ChangeLog.Core.State;
using ChangeLog.Core.Util;
using EwAPI;

namespace ChangeLog.AddIn.Host
{
    public enum MonitorState
    {
        Indexing = 0,
        Recording = 1,
        Failed = 2,
        Closed = 3
    }

    /// <summary>
    /// Watches one open SWE project: subscribes to its object events, indexes it in
    /// time-sliced chunks on the UI thread, and feeds the <see cref="ChangeLogSession"/>.
    /// </summary>
    public sealed class ProjectMonitor : IDisposable
    {
        private readonly AddInHost _host;
        private readonly _IEwProjectXEvents_Event _events;
        private readonly DispatcherTimer _pump;
        private DispatcherTimer _indexTimer;
        private IEnumerator<ObjectState> _indexing;
        private List<ObjectState> _indexed;
        private Action<ProjectState> _indexDone;

        public ProjectMonitor(AddInHost host, IEwProjectX project)
        {
            _host = host;
            Project = project;
            UiThread.EnsureContext(); // ChangeLogSession captures it to marshal I/O results back to this thread
            EwErrorCode e;
            Id = project.getID();
            Name = Com.Try(() => project.getName(out e)) ?? "Project " + Id;
            Reader = new EwObjectReader(project, host.Settings.Language);

            var recorder = new RecorderOptions { QuietWindow = TimeSpan.FromSeconds(Math.Max(0.3, host.Settings.QuietWindowSeconds)) };
            if (host.Settings.LogWireSegments) recorder.SilentKinds.Clear();

            Session = ChangeLogSession.Open(new SessionOptions
            {
                StoreRoot = ResolveStoreRoot(project, host.Settings),
                ProjectName = Name,
                SweProjectId = Id,
                Who = host.Who,
                Signer = host.Signer,
                Verifier = new CngEcdsaVerifier(),
                Anchor = string.IsNullOrWhiteSpace(host.Settings.AnchorFolder) ? (IAnchorSink)NullAnchorSink.Instance : new FileAnchorSink(host.Settings.AnchorFolder),
                Recorder = recorder,
                AppVersion = host.AppVersion,
                AddInVersion = AddInHost.AddInVersion
            });
            Session.Diagnostic += (s, m) => Log.Info("[" + Name + "] " + m);

            _events = project as _IEwProjectXEvents_Event;
            if (_events != null)
            {
                _events.onProjectObjectAdd += OnAdd;
                _events.onProjectObjectUpdate += OnUpdate;
                _events.onProjectObjectDelete += OnDelete;
            }
            else
            {
                Log.Warn("Project " + Name + " does not expose _IEwProjectXEvents; live recording unavailable.");
            }

            _pump = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
            _pump.Tick += (s, a) => SafePump();
            _pump.Start();

            State = MonitorState.Indexing;
            StartIndex(actual =>
            {
                var modifiedBy = Com.Try(() => Project.getModifiedBy(out e));
                var reconciled = Session.InitializeState(actual, string.IsNullOrWhiteSpace(modifiedBy) ? "Unknown (changed without add-in)" : modifiedBy + " (inferred)");
                SetState(MonitorState.Recording);
                if (reconciled.Count > 0)
                    _host.Ui.Notify(reconciled.Count + " change(s) were made to '" + Name + "' while the add-in was not running. Logged as inferred.", NotificationKind.Warning);
            });
        }

        public IEwProjectX Project { get; }
        public int Id { get; }
        public string Name { get; }
        public EwObjectReader Reader { get; }
        public ChangeLogSession Session { get; }
        public MonitorState State { get; private set; }
        public int IndexedCount { get; private set; }
        public bool IsBusy => _indexTimer != null;
        public event EventHandler StateChanged;

        /// <summary>Re-reads the whole project and captures a baseline (drift is logged first).</summary>
        public void CaptureBaseline(string name, BaselineKind kind, int? nativeSnapshotId, Action<BaselineInfo> done)
        {
            if (IsBusy || State != MonitorState.Recording)
            {
                _host.Ui.Notify("Project is still being indexed. Try again in a moment.", NotificationKind.Warning);
                return;
            }

            SetState(MonitorState.Indexing);
            StartIndex(actual =>
            {
                var drift = Session.Resync(actual, _host.Who.DisplayName + " (detected at baseline)");
                if (drift.Count > 0) Log.Warn(drift.Count + " change(s) were not raised as SWE events and were reconciled.");
                var info = Session.CaptureBaseline(actual, name, kind, nativeSnapshotId);
                SetState(MonitorState.Recording);
                done?.Invoke(info);
            });
        }

        // -------------------------------------------------------------- events (UI thread)

        private void OnAdd(object o) => Observe(RawEventType.Add, o);
        private void OnUpdate(object o) => Observe(RawEventType.Update, o);
        private void OnDelete(object o) => Observe(RawEventType.Delete, o);

        private void Observe(RawEventType type, object com)
        {
            try
            {
                if (type == RawEventType.Delete)
                {
                    // The object may already be gone: identify it only; the recorder supplies label/page from its cache.
                    ObjectKey key;
                    if (Reader.TryIdentify(com, out key)) Session.Observe(new RawEvent(type, key, null, DateTime.UtcNow));
                    else Log.Warn("Delete event for an unidentifiable object (" + Com.TypeName(com) + ") ignored.");
                    return;
                }

                var state = Reader.Read(com);
                if (state == null) return;
                Session.Observe(new RawEvent(type, state.Key, state, DateTime.UtcNow));
            }
            catch (Exception ex)
            {
                // Never let an exception escape into SWE's event dispatch.
                Log.Error("Event handling failed (" + type + ")", ex);
            }
        }

        private void SafePump()
        {
            try
            {
                Session.Pump();
            }
            catch (Exception ex)
            {
                Log.Error("Pump failed", ex);
            }
        }

        // -------------------------------------------------------------- indexing

        private void StartIndex(Action<ProjectState> done)
        {
            _indexing = new StateCollector(Project, Reader).Collect().GetEnumerator();
            _indexed = new List<ObjectState>();
            _indexDone = done;
            IndexedCount = 0;
            _indexTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1) };
            _indexTimer.Tick += IndexTick;
            _indexTimer.Start();
        }

        private void IndexTick(object sender, EventArgs args)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.ElapsedMilliseconds < _host.Settings.IndexSliceMs)
                {
                    if (!_indexing.MoveNext())
                    {
                        StopIndex();
                        var actual = ProjectState.FromIndex(_indexed, Name, Iso.Format(DateTime.UtcNow), _host.Who.DisplayName);
                        _indexed = null;
                        _indexDone(actual);
                        return;
                    }

                    if (_indexing.Current != null) _indexed.Add(_indexing.Current);
                }

                IndexedCount = _indexed.Count;
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                StopIndex();
                Log.Error("Indexing failed for " + Name, ex);
                SetState(MonitorState.Failed);
                _host.Ui.Notify("Indexing '" + Name + "' failed: " + ex.Message, NotificationKind.Error);
            }
        }

        private void StopIndex()
        {
            if (_indexTimer == null) return;
            _indexTimer.Stop();
            _indexTimer.Tick -= IndexTick;
            _indexTimer = null;
            _indexing?.Dispose();
            _indexing = null;
        }

        private void SetState(MonitorState s)
        {
            State = s;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        // -------------------------------------------------------------- lifecycle

        public void Dispose()
        {
            if (State == MonitorState.Closed) return;
            StopIndex();
            _pump.Stop();
            if (_events != null)
            {
                try
                {
                    _events.onProjectObjectAdd -= OnAdd;
                    _events.onProjectObjectUpdate -= OnUpdate;
                    _events.onProjectObjectDelete -= OnDelete;
                }
                catch (Exception ex)
                {
                    Log.Warn("Unsubscribing project events failed: " + ex.Message);
                }
            }

            try
            {
                Session.Close(captureEndBaseline: true);
            }
            catch (Exception ex)
            {
                Log.Error("Closing change log for " + Name + " failed", ex);
            }

            SetState(MonitorState.Closed);
            Com.Release(Project);
        }

        private static string ResolveStoreRoot(IEwProjectX project, AddInSettings settings)
        {
            EwErrorCode e;
            var id = project.getID();
            var name = Com.Try(() => project.getName(out e)) ?? "project";
            if (!string.IsNullOrWhiteSpace(settings.StoreRootOverride))
                return Path.Combine(settings.StoreRootOverride, Names.FileSafe(name) + "_" + id);

            var folder = Com.Try(() => project.getFolderPath(EwProjectFolderPathValue.kProjectFolderPath, out e));
            if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
                return Path.Combine(folder, "ChangeLog");

            Log.Warn("Project folder unavailable for " + name + "; using local storage (not shared with other users).");
            return Path.Combine(AddInSettings.LocalFolder, "projects", Names.FileSafe(name) + "_" + id);
        }
    }
}
