using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using ChangeLog.Core.Integrity;
using ChangeLog.Core.Model;
using ChangeLog.Core.Recording;
using ChangeLog.Core.State;
using ChangeLog.Core.Storage;
using ChangeLog.Core.Util;

namespace ChangeLog.Core
{
    public sealed class SessionOptions
    {
        public string StoreRoot { get; set; }
        public string ProjectName { get; set; }
        public int SweProjectId { get; set; }
        public UserIdentity Who { get; set; }
        public ISigner Signer { get; set; }
        public ISignatureVerifier Verifier { get; set; }
        public IAnchorSink Anchor { get; set; } = NullAnchorSink.Instance;
        public IClock Clock { get; set; } = SystemClock.Instance;
        public RecorderOptions Recorder { get; set; } = new RecorderOptions();
        public string AppVersion { get; set; }
        public string AddInVersion { get; set; }

        /// <summary>Run disk I/O inline instead of on a worker thread (tests).</summary>
        public bool Synchronous { get; set; }

        public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);
        public TimeSpan WitnessInterval { get; set; } = TimeSpan.FromMinutes(5);
    }

    public enum StorageHealth
    {
        Ok = 0,
        Pending = 1,
        Error = 2
    }

    /// <summary>
    /// One project's change log for one SWE session. Owns the recorder, this session's
    /// journal writer, the tailer for other users' journals, baselines and integrity.
    /// Call every public member from a single thread (the SWE UI thread); disk I/O runs on
    /// an internal worker and results are marshalled back via the captured SynchronizationContext.
    /// </summary>
    public sealed class ChangeLogSession : IDisposable
    {
        private readonly SessionOptions _o;
        private readonly StoreLayout _layout;
        private readonly BaselineStore _baselines;
        private readonly JournalTailer _tailer;
        private readonly SerialWorker _worker;
        private readonly SynchronizationContext _ui;
        private readonly List<ChangeRecord> _records = new List<ChangeRecord>();
        private readonly List<BaselineInfo> _baselineInfos = new List<BaselineInfo>();
        private readonly Dictionary<string, WitnessHead> _otherHeads = new Dictionary<string, WitnessHead>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentQueue<KeyValuePair<string, JournalEntry>> _incoming = new ConcurrentQueue<KeyValuePair<string, JournalEntry>>();
        private readonly ConcurrentQueue<IList<ChangeRecord>> _unsaved = new ConcurrentQueue<IList<ChangeRecord>>();
        private JournalWriter _writer;
        private DateTime _lastPollUtc;
        private DateTime _lastWitnessUtc;
        private DateTime _lastRetryUtc;
        private bool _witnessDirty;
        private int _pollInFlight;
        private bool _stateReady;
        private bool _closed;

        private ChangeLogSession(SessionOptions o)
        {
            _o = o;
            _layout = new StoreLayout(o.StoreRoot);
            _baselines = new BaselineStore(_layout);
            _tailer = new JournalTailer(_layout);
            _ui = SynchronizationContext.Current;
            _worker = new SerialWorker(o.Synchronous, e => Raise(Diagnostic, "Background I/O failed: " + e.Message));
            Recorder = new ChangeRecorder(o.Who, o.Clock, o.Recorder);
        }

        public ProjectInfo Project { get; private set; }
        public ChangeRecorder Recorder { get; }
        public IReadOnlyList<ChangeRecord> Records => _records;
        public IReadOnlyList<BaselineInfo> Baselines => _baselineInfos;
        public IntegrityReport Integrity { get; private set; }
        public string ActiveJournal => _writer?.FileName;
        public bool StateReady => _stateReady;
        public StorageHealth Health { get; private set; }
        public string LastError { get; private set; }
        public UserIdentity Who => _o.Who;

        public event EventHandler<RecordsEventArgs> RecordsAdded;
        public event EventHandler BaselinesChanged;
        public event EventHandler IntegrityChanged;
        public event EventHandler HealthChanged;
        public event EventHandler<string> Diagnostic;

        public static ChangeLogSession Open(SessionOptions o)
        {
            var s = new ChangeLogSession(o);
            s.OpenCore();
            return s;
        }

        private void OpenCore()
        {
            _layout.EnsureCreated();
            Project = new ProjectInfo
            {
                ProjectGuid = _layout.GetOrCreateProjectGuid(),
                ProjectName = _o.ProjectName,
                SweProjectId = _o.SweProjectId,
                StoreRoot = _o.StoreRoot
            };

            var files = _layout.JournalFiles().Select(JournalReader.Read).ToList();
            foreach (var f in files) Ingest(f.Name, f.Entries);
            foreach (var f in files)
            {
                _tailer.Skip(f.Path);
                if (f.Last != null) _otherHeads[f.Name] = new WitnessHead { File = f.Name, Seq = f.Last.Seq, Hash = f.Last.Hash };
            }

            _records.Sort(CompareRecords);
            Integrity = new JournalVerifier(_o.Verifier).Verify(files, SafeAnchors(), new HashSet<string>(), _o.Clock);

            // Link to this user+machine's previous session so deleting/truncating it is detectable.
            var prev = files.Where(f => f.Header != null &&
                                        string.Equals(f.Header.WindowsUser, _o.Who.WindowsUser, StringComparison.OrdinalIgnoreCase) &&
                                        string.Equals(f.Header.Machine, _o.Who.Machine, StringComparison.OrdinalIgnoreCase) &&
                                        f.Last != null)
                .OrderBy(f => f.Name, StringComparer.Ordinal).LastOrDefault();

            var header = new HeaderPayload
            {
                ProjectGuid = Project.ProjectGuid,
                ProjectName = _o.ProjectName,
                SweProjectId = _o.SweProjectId,
                User = _o.Who.DisplayName,
                WindowsUser = _o.Who.WindowsUser,
                Machine = _o.Who.Machine,
                SessionId = Guid.NewGuid().ToString("N"),
                AppVersion = _o.AppVersion,
                AddInVersion = _o.AddInVersion,
                PrevSessionFile = prev?.Name,
                PrevSessionSeq = prev?.Last.Seq ?? -1,
                PrevSessionHead = prev?.Last.Hash
            };
            _writer = JournalWriter.Create(_layout, header, _o.Signer, _o.Clock);
            _tailer.Skip(_writer.Path);
            WriteWitness();
            Seal();
        }

        // ------------------------------------------------------------------ state

        /// <summary>
        /// Called once the add-in has read the full project. Rebuilds the expected state
        /// (latest baseline + logged changes since), logs any differences as Reconciled,
        /// then starts live recording against the actual state.
        /// </summary>
        public IList<ChangeRecord> InitializeState(ProjectState actual, string inferredUser)
        {
            IList<ChangeRecord> reconciled = new List<ChangeRecord>();
            var expected = BuildExpectedState();
            if (expected == null)
            {
                Recorder.LoadState(actual.Objects, true);
                Raise(Diagnostic, "No previous baseline: recording starts from the current project state.");
            }
            else
            {
                Recorder.LoadState(expected.Objects, true);
                reconciled = Recorder.Reconcile(actual, inferredUser);
                if (reconciled.Count > 0) Commit(reconciled);
            }

            _stateReady = true;
            if (expected == null || reconciled.Count > 0)
                CaptureBaseline(actual, expected == null ? "Initial state" : "Session start (reconciled)", BaselineKind.SessionStart, null);
            return reconciled;
        }

        private ProjectState BuildExpectedState()
        {
            foreach (var b in _baselineInfos.OrderByDescending(b => b.CapturedUtc))
            {
                ProjectState state;
                try
                {
                    state = _baselines.Load(b);
                }
                catch (Exception e) when (e is System.IO.IOException || e is SerializationException || e is System.IO.InvalidDataException)
                {
                    Raise(Diagnostic, "Baseline '" + b.Name + "' unusable: " + e.Message);
                    continue;
                }

                var replay = new ChangeRecorder(_o.Who, _o.Clock, _o.Recorder);
                replay.LoadState(state.Objects, true);
                foreach (var r in _records.Where(r => r.TimestampUtc > b.CapturedUtc && r.After != null || r.TimestampUtc > b.CapturedUtc && r.Action == ChangeAction.Removed))
                    replay.ApplyExternal(r);
                return replay.Snapshot(_o.ProjectName);
            }

            return null;
        }

        public ProjectState CurrentState() => Recorder.Snapshot(_o.ProjectName);

        /// <summary>
        /// Compares a freshly read project state with the live index (drift detection, e.g.
        /// changes SWE did not raise events for). Differences are logged as Reconciled.
        /// </summary>
        public IList<ChangeRecord> Resync(ProjectState actual, string inferredUser)
        {
            FlushNow();
            var records = Recorder.Reconcile(actual, inferredUser);
            if (records.Count > 0) Commit(records);
            return records;
        }

        // ------------------------------------------------------------------ live

        public void Observe(RawEvent e)
        {
            if (!_closed) Recorder.Observe(e);
        }

        /// <summary>Drive from a UI timer (~500 ms). Applies other users' changes, then flushes ours.</summary>
        public void Pump()
        {
            if (_closed) return;
            var now = _o.Clock.UtcNow;

            ApplyIncoming();

            if (_stateReady && Recorder.IsDue(now))
            {
                var records = Recorder.Flush();
                if (records.Count > 0) Commit(records);
            }

            RetryUnsaved();

            if (now - _lastPollUtc >= _o.PollInterval && Interlocked.CompareExchange(ref _pollInFlight, 1, 0) == 0)
            {
                _lastPollUtc = now;
                var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _writer.FileName };
                _worker.Post(() =>
                {
                    try
                    {
                        foreach (var kv in _tailer.Poll(own)) _incoming.Enqueue(kv);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _pollInFlight, 0);
                    }
                });
            }

            if (_witnessDirty && now - _lastWitnessUtc >= _o.WitnessInterval)
            {
                WriteWitness();
                Seal();
            }
        }

        /// <summary>Flushes everything pending immediately (before export, compare or close).</summary>
        public void FlushNow()
        {
            ApplyIncoming();
            if (_stateReady && Recorder.PendingCount > 0)
            {
                var records = Recorder.Flush();
                if (records.Count > 0) Commit(records);
            }

            _worker.Drain();
        }

        private void ApplyIncoming()
        {
            KeyValuePair<string, JournalEntry> kv;
            var added = new List<ChangeRecord>();
            while (_incoming.TryDequeue(out kv))
            {
                _otherHeads[kv.Key] = new WitnessHead { File = kv.Key, Seq = kv.Value.Seq, Hash = kv.Value.Hash };
                _witnessDirty = true;
                var before = _records.Count;
                Ingest(kv.Key, new[] { kv.Value });
                for (var i = before; i < _records.Count; i++)
                {
                    Recorder.ApplyExternal(_records[i]);
                    added.Add(_records[i]);
                }
            }

            if (added.Count > 0)
            {
                _records.Sort(CompareRecords);
                Raise(RecordsAdded, new RecordsEventArgs(added, false));
            }
        }

        private void Commit(IList<ChangeRecord> records)
        {
            _records.AddRange(records);
            _records.Sort(CompareRecords);
            Raise(RecordsAdded, new RecordsEventArgs(records, true));
            Write(records);
        }

        private void Write(IList<ChangeRecord> records)
        {
            SetHealth(StorageHealth.Pending, null);
            var bodies = records.Select(r => Json.Serialize(r)).ToList();
            _worker.Post(() =>
            {
                try
                {
                    var entries = _writer.AppendBatch(EntryType.Change, bodies);
                    var cp = _writer.Checkpoint();
                    if (cp != null) SafeAnchor(cp);
                    Post(() =>
                    {
                        for (var i = 0; i < records.Count; i++)
                        {
                            records[i].JournalFile = _writer.FileName;
                            records[i].JournalSeq = entries[i].Seq;
                            records[i].EntryHash = entries[i].Hash;
                        }

                        SetHealth(_unsaved.IsEmpty ? StorageHealth.Ok : StorageHealth.Pending, null);
                    });
                }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
                {
                    _unsaved.Enqueue(records);
                    Post(() => SetHealth(StorageHealth.Error, "Cannot write change log: " + e.Message + " (changes kept in memory; retrying)."));
                }
            });
        }

        private void RetryUnsaved()
        {
            if (_unsaved.IsEmpty || Health != StorageHealth.Error) return;
            // Back off: each retry opens a new linked journal file; don't spin on a flaky share.
            var now = _o.Clock.UtcNow;
            if (now - _lastRetryUtc < TimeSpan.FromSeconds(30)) return;
            _lastRetryUtc = now;
            IList<ChangeRecord> batch;
            var batches = new List<IList<ChangeRecord>>();
            while (_unsaved.TryDequeue(out batch)) batches.Add(batch);
            _worker.Post(() =>
            {
                try
                {
                    // The previous file may hold a torn line; continue in a fresh, linked journal.
                    var oldName = _writer.FileName;
                    try { _writer.Close(); } catch (Exception) { /* best effort */ }
                    var header = new HeaderPayload
                    {
                        ProjectGuid = Project.ProjectGuid, ProjectName = _o.ProjectName, SweProjectId = _o.SweProjectId,
                        User = _o.Who.DisplayName, WindowsUser = _o.Who.WindowsUser, Machine = _o.Who.Machine,
                        SessionId = Guid.NewGuid().ToString("N"), AppVersion = _o.AppVersion, AddInVersion = _o.AddInVersion,
                        PrevSessionFile = oldName, PrevSessionSeq = _writer.LastSeq, PrevSessionHead = _writer.LastHash
                    };
                    _writer = JournalWriter.Create(_layout, header, _o.Signer, _o.Clock);
                    _tailer.Skip(_writer.Path);
                    Post(() =>
                    {
                        foreach (var b in batches) Write(b);
                    });
                }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
                {
                    foreach (var b in batches) _unsaved.Enqueue(b);
                }
            });
        }

        // ------------------------------------------------------------------ baselines

        public BaselineInfo CaptureBaseline(ProjectState state, string name, BaselineKind kind, int? nativeSnapshotId)
        {
            var payload = _baselines.Save(state, name, kind, nativeSnapshotId);
            var entry = _writer.Append(EntryType.Baseline, Json.Serialize(payload));
            Seal();
            var info = new BaselineInfo { Payload = payload, JournalFile = _writer.FileName, JournalSeq = entry.Seq };
            _baselineInfos.Add(info);
            _baselineInfos.Sort((a, b) => a.CapturedUtc.CompareTo(b.CapturedUtc));
            Raise(BaselinesChanged, EventArgs.Empty);
            return info;
        }

        public ProjectState LoadBaseline(BaselineInfo info) => _baselines.Load(info);

        // ------------------------------------------------------------------ integrity

        public IntegrityReport VerifyNow()
        {
            _worker.Drain();
            var files = _layout.JournalFiles().Select(JournalReader.Read).ToList();
            Integrity = new JournalVerifier(_o.Verifier).Verify(files, SafeAnchors(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { _writer.FileName }, _o.Clock);
            Raise(IntegrityChanged, EventArgs.Empty);
            return Integrity;
        }

        // ------------------------------------------------------------------ lifecycle

        /// <summary>Flushes, optionally captures a session-end baseline, seals and closes the journal.</summary>
        public void Close(bool captureEndBaseline)
        {
            if (_closed) return;
            FlushNow();
            if (captureEndBaseline && _stateReady && _records.Any(r => r.JournalFile == _writer.FileName))
                CaptureBaseline(CurrentState(), "Session end", BaselineKind.SessionEnd, null);
            _closed = true;
            _worker.Post(() =>
            {
                _writer.Close();
                SafeAnchor(new JournalEntry { Seq = _writer.LastSeq, Hash = _writer.LastHash });
            });
            _worker.Drain();
            _worker.Dispose();
        }

        public void Dispose() => Close(false);

        // ------------------------------------------------------------------ helpers

        private void Ingest(string file, IEnumerable<JournalEntry> entries)
        {
            foreach (var e in entries)
            {
                try
                {
                    if (e.Type == EntryType.Change)
                    {
                        var r = e.Payload<ChangeRecord>();
                        r.JournalFile = file;
                        r.JournalSeq = e.Seq;
                        r.EntryHash = e.Hash;
                        if (r.Fields == null) r.Fields = new List<FieldChange>();
                        _records.Add(r);
                    }
                    else if (e.Type == EntryType.Baseline)
                    {
                        _baselineInfos.Add(new BaselineInfo { Payload = e.Payload<BaselinePayload>(), JournalFile = file, JournalSeq = e.Seq });
                        _baselineInfos.Sort((a, b) => a.CapturedUtc.CompareTo(b.CapturedUtc));
                        Raise(BaselinesChanged, EventArgs.Empty);
                    }
                }
                catch (SerializationException ex)
                {
                    Raise(Diagnostic, "Unreadable entry " + file + "#" + e.Seq + ": " + ex.Message);
                }
            }
        }

        private void WriteWitness()
        {
            var p = new WitnessPayload();
            p.Heads.AddRange(_otherHeads.Values.Where(h => !string.Equals(h.File, _writer.FileName, StringComparison.OrdinalIgnoreCase)).OrderBy(h => h.File, StringComparer.Ordinal));
            if (p.Heads.Count > 0) _writer.Append(EntryType.Witness, Json.Serialize(p));
            _lastWitnessUtc = _o.Clock.UtcNow;
            _witnessDirty = false;
        }

        private void Seal()
        {
            var cp = _writer.Checkpoint();
            if (cp != null) SafeAnchor(cp);
        }

        private void SafeAnchor(JournalEntry head)
        {
            try
            {
                _o.Anchor.Anchor(Project.ProjectGuid, _writer.FileName, head.Seq, head.Hash);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                Post(() => Raise(Diagnostic, "External anchor unavailable: " + e.Message));
            }
        }

        private IEnumerable<WitnessHead> SafeAnchors()
        {
            try
            {
                return _o.Anchor.ReadAnchors(Project.ProjectGuid).ToList();
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                Raise(Diagnostic, "External anchor unavailable: " + e.Message);
                return new WitnessHead[0];
            }
        }

        private void SetHealth(StorageHealth h, string error)
        {
            if (Health == h && LastError == error) return;
            Health = h;
            LastError = error;
            Raise(HealthChanged, EventArgs.Empty);
        }

        private void Post(Action a)
        {
            if (_ui == null || _o.Synchronous) a();
            else _ui.Post(_ => a(), null);
        }

        private void Raise<T>(EventHandler<T> h, T args) => h?.Invoke(this, args);

        private void Raise(EventHandler h, EventArgs args) => h?.Invoke(this, args);

        private static int CompareRecords(ChangeRecord a, ChangeRecord b)
        {
            var c = string.CompareOrdinal(a.TimestampIso, b.TimestampIso);
            if (c != 0) return c;
            c = string.CompareOrdinal(a.JournalFile, b.JournalFile);
            return c != 0 ? c : a.JournalSeq.CompareTo(b.JournalSeq);
        }
    }

    public sealed class RecordsEventArgs : EventArgs
    {
        public RecordsEventArgs(IList<ChangeRecord> records, bool local)
        {
            Records = records;
            IsLocal = local;
        }

        public IList<ChangeRecord> Records { get; }

        /// <summary>True for this session's own changes; false for other users' (live collaboration).</summary>
        public bool IsLocal { get; }
    }

    /// <summary>Single background thread executing actions in order. Synchronous mode runs inline.</summary>
    internal sealed class SerialWorker : IDisposable
    {
        private readonly bool _sync;
        private readonly Action<Exception> _onError;
        private readonly BlockingCollection<Action> _queue;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);
        private int _outstanding;

        public SerialWorker(bool synchronous, Action<Exception> onError)
        {
            _sync = synchronous;
            _onError = onError;
            if (_sync) return;
            _queue = new BlockingCollection<Action>();
            _thread = new Thread(Run) { IsBackground = true, Name = "ChangeLog I/O" };
            _thread.Start();
        }

        public void Post(Action a)
        {
            if (_sync)
            {
                Execute(a);
                return;
            }

            if (Interlocked.Increment(ref _outstanding) == 1) _idle.Reset();
            _queue.Add(a);
        }

        /// <summary>Blocks until all queued work has run.</summary>
        public void Drain()
        {
            if (!_sync) _idle.Wait(TimeSpan.FromSeconds(30));
        }

        private void Run()
        {
            foreach (var a in _queue.GetConsumingEnumerable())
            {
                Execute(a);
                if (Interlocked.Decrement(ref _outstanding) == 0) _idle.Set();
            }
        }

        private void Execute(Action a)
        {
            try { a(); }
            catch (Exception e) { _onError(e); }
        }

        public void Dispose()
        {
            if (_sync) return;
            _queue.CompleteAdding();
            _thread.Join(TimeSpan.FromSeconds(10));
        }
    }
}
