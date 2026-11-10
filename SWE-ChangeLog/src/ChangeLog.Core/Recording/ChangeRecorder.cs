using System;
using System.Collections.Generic;
using System.Linq;
using ChangeLog.Core.Model;
using ChangeLog.Core.State;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Recording
{
    public enum RawEventType
    {
        Add = 0,
        Update = 1,
        Delete = 2
    }

    /// <summary>An event from SWE, already converted from COM to plain state.</summary>
    public sealed class RawEvent
    {
        public RawEvent(RawEventType type, ObjectKey key, ObjectState state, DateTime utc)
        {
            Type = type;
            Key = key;
            State = state;
            Utc = utc;
        }

        public RawEventType Type { get; }
        public ObjectKey Key { get; }

        /// <summary>State read at event time; may be null when the object is unreadable (deleted).</summary>
        public ObjectState State { get; }

        public DateTime Utc { get; }
    }

    public sealed class RecorderOptions
    {
        /// <summary>Events are coalesced until no new event arrives for this long.</summary>
        public TimeSpan QuietWindow { get; set; } = TimeSpan.FromSeconds(1.5);

        /// <summary>Force a flush even during continuous activity (bulk operations).</summary>
        public TimeSpan MaxLatency { get; set; } = TimeSpan.FromSeconds(10);

        /// <summary>Kinds tracked in state (for diffs/visuals) but not written to the log.</summary>
        public HashSet<ObjectKind> SilentKinds { get; set; } = new HashSet<ObjectKind> { ObjectKind.WireSegment };
    }

    /// <summary>
    /// Turns the noisy SWE event stream into meaningful change records:
    ///  * coalesces bursts per object (add+update = one "Added"; add+delete = nothing),
    ///  * drops updates that changed nothing (SWE raises many no-op updates),
    ///  * computes field-level before/after from a live state index,
    ///  * groups records from one burst (e.g. a macro insertion) under a batch id,
    ///  * applies other users' records first so their edits are not attributed to us.
    /// Pure logic, no COM, no I/O: fully unit-testable. Not thread-safe; call from one thread.
    /// </summary>
    public sealed class ChangeRecorder
    {
        private readonly Dictionary<ObjectKey, ObjectState> _live = new Dictionary<ObjectKey, ObjectState>();
        private readonly List<RawEvent> _pending = new List<RawEvent>();
        private readonly UserIdentity _who;
        private readonly IClock _clock;
        private readonly RecorderOptions _options;
        private DateTime _firstPendingUtc;
        private DateTime _lastPendingUtc;

        public ChangeRecorder(UserIdentity who, IClock clock, RecorderOptions options)
        {
            _who = who;
            _clock = clock;
            _options = options ?? new RecorderOptions();
        }

        public int PendingCount => _pending.Count;
        public int LiveObjectCount => _live.Count;

        /// <summary>True once a full project state has been loaded; before that, unknown objects are not reported as "Added".</summary>
        public bool StateComplete { get; private set; }

        public IEnumerable<ObjectState> LiveObjects => _live.Values;

        public void LoadState(IEnumerable<ObjectState> objects, bool complete)
        {
            _live.Clear();
            foreach (var o in objects) _live[o.Key] = o.Clone();
            StateComplete = complete;
        }

        public ProjectState Snapshot(string projectName)
        {
            return ProjectState.FromIndex(_live.Values, projectName, Iso.Format(_clock.UtcNow), _who.DisplayName);
        }

        public void Observe(RawEvent e)
        {
            if (_pending.Count == 0) _firstPendingUtc = e.Utc;
            _lastPendingUtc = e.Utc;
            _pending.Add(e);
        }

        public bool IsDue(DateTime utcNow)
        {
            return _pending.Count > 0 &&
                   (utcNow - _lastPendingUtc >= _options.QuietWindow || utcNow - _firstPendingUtc >= _options.MaxLatency);
        }

        /// <summary>Applies a record written by another session (keeps our state index current).</summary>
        public void ApplyExternal(ChangeRecord r)
        {
            if (r.Action == ChangeAction.Removed) _live.Remove(r.Key);
            else if (r.After != null) _live[r.Key] = r.After.Clone();
        }

        /// <summary>Converts pending events into records. Returns an empty list when nothing meaningful changed.</summary>
        public IList<ChangeRecord> Flush()
        {
            var records = new List<ChangeRecord>();
            if (_pending.Count == 0) return records;

            var events = _pending.ToList();
            _pending.Clear();
            var ts = Iso.Format(events[events.Count - 1].Utc);

            foreach (var group in events.GroupBy(e => e.Key))
            {
                var list = group.ToList();
                var last = list[list.Count - 1];
                ObjectState cached;
                var existed = _live.TryGetValue(group.Key, out cached);
                var latestState = list.Where(e => e.State != null).Select(e => e.State).LastOrDefault();
                var hadAdd = list.Any(e => e.Type == RawEventType.Add);

                if (last.Type == RawEventType.Delete)
                {
                    if (!existed && hadAdd) continue; // created and deleted inside the window: transient
                    var before = cached ?? latestState;
                    if (before == null) continue; // never seen and unreadable: nothing meaningful to log
                    records.Add(Make(ChangeAction.Removed, group.Key, before, null, ObjectDiffer.Diff(before, null), ts));
                    _live.Remove(group.Key); // after Make so a removed page still resolves its label
                    continue;
                }

                if (latestState == null) continue; // unreadable update; nothing to compare
                var after = latestState.Clone();

                if (!existed)
                {
                    _live[group.Key] = after;
                    if (hadAdd || StateComplete)
                        records.Add(Make(ChangeAction.Added, group.Key, null, after, ObjectDiffer.Diff(null, after), ts));
                    else
                        records.Add(Make(ChangeAction.Edited, group.Key, null, after, ObjectDiffer.Diff(null, after), ts));
                    continue;
                }

                var fields = ObjectDiffer.Diff(cached, after);
                _live[group.Key] = after;
                if (fields.Count > 0) records.Add(Make(ChangeAction.Edited, group.Key, cached, after, fields, ts));
            }

            records.RemoveAll(r => _options.SilentKinds.Contains(r.Kind));
            if (records.Count > 1)
            {
                var batch = Guid.NewGuid().ToString("N").Substring(0, 10);
                foreach (var r in records) r.BatchId = batch;
            }

            return records;
        }

        /// <summary>
        /// Compares the expected state (our index) with the actual project state read on open.
        /// Differences were made without the add-in running and are logged as Reconciled/Inferred.
        /// </summary>
        public IList<ChangeRecord> Reconcile(ProjectState actual, string inferredUser)
        {
            var expected = Snapshot(actual.ProjectName);
            var diff = DiffEngine.Compare(expected, actual);
            var ts = Iso.Format(_clock.UtcNow);
            var records = new List<ChangeRecord>();
            LoadState(actual.Objects, true);
            foreach (var item in diff.Items.Where(i => !_options.SilentKinds.Contains(i.Kind)))
            {
                var r = Make(item.Action, item.Key, item.Before, item.After, item.Fields, ts);
                r.Source = ChangeSource.Reconciled;
                r.Attribution = Attribution.Inferred;
                r.User = string.IsNullOrWhiteSpace(inferredUser) ? "Unknown" : inferredUser;
                r.WindowsUser = null;
                r.Machine = null;
                records.Add(r);
            }

            if (records.Count > 1)
            {
                var batch = "rec-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                foreach (var r in records) r.BatchId = batch;
            }

            return records;
        }

        public string PageLabel(int? pageId)
        {
            if (!pageId.HasValue) return null;
            ObjectState page;
            return _live.TryGetValue(new ObjectKey(ObjectKind.Page, pageId.Value), out page) ? page.DisplayLabel : "Page #" + pageId.Value;
        }

        private ChangeRecord Make(ChangeAction action, ObjectKey key, ObjectState before, ObjectState after, List<FieldChange> fields, string ts)
        {
            var s = after ?? before;
            int? pageId = key.Kind == ObjectKind.Page ? key.Id : s?.PageId;
            foreach (var f in fields.Where(f => f.Field == "Page"))
            {
                f.Before = PageLabelFromText(f.Before);
                f.After = PageLabelFromText(f.After);
            }

            return new ChangeRecord
            {
                RecordId = Guid.NewGuid().ToString("N").Substring(0, 16),
                TimestampIso = ts,
                Action = action,
                Kind = key.Kind,
                ObjectId = key.Id,
                ObjectLabel = s?.DisplayLabel ?? key.ToString(),
                PageId = pageId,
                PageLabel = PageLabel(pageId),
                User = _who.DisplayName,
                WindowsUser = _who.WindowsUser,
                Machine = _who.Machine,
                Source = ChangeSource.Live,
                Attribution = Attribution.Confirmed,
                Fields = fields,
                After = after?.Clone()
            };
        }

        private string PageLabelFromText(string idText)
        {
            int id;
            return int.TryParse(idText, out id) ? PageLabel(id) : idText;
        }
    }
}
