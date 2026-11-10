using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ChangeLog.AddIn.Host;
using ChangeLog.Core;
using ChangeLog.Core.Export;
using ChangeLog.Core.Integrity;
using ChangeLog.Core.Model;
using ChangeLog.Core.Query;

namespace ChangeLog.AddIn.UI
{
    public sealed class ChipViewModel : ViewModelBase
    {
        private readonly Action _changed;
        private bool _isChecked;
        private int _count;

        public ChipViewModel(string label, object value, int count, Action changed)
        {
            Label = label;
            Value = value;
            _count = count;
            _changed = changed;
        }

        public string Label { get; }
        public object Value { get; }

        public int Count
        {
            get { return _count; }
            set
            {
                if (Set(ref _count, value)) Raise(nameof(Text));
            }
        }

        public string Text => Label + "  " + Count.ToString("N0", CultureInfo.CurrentCulture);

        public bool IsChecked
        {
            get { return _isChecked; }
            set
            {
                if (Set(ref _isChecked, value)) _changed();
            }
        }

        /// <summary>Sets the state without triggering a refresh (bulk reset).</summary>
        public void Reset()
        {
            _isChecked = false;
            Raise(nameof(IsChecked));
        }
    }

    public sealed class BarViewModel
    {
        public string Label { get; set; }
        public int Value { get; set; }
        public GridLength Fill { get; set; }
        public GridLength Rest { get; set; }
    }

    public sealed class ChangeRowViewModel
    {
        public ChangeRowViewModel(ChangeRecord r, IntegrityReport integrity, DateTime nowUtc)
        {
            Record = r;
            var t = r.TimestampUtc.ToLocalTime();
            Time = t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture);
            Relative = RelativeTime(nowUtc - r.TimestampUtc, t);
            Integrity = ChangeLogExporter.EntryIntegrity(integrity, r);
            IsNew = nowUtc - r.TimestampUtc < TimeSpan.FromSeconds(15);
        }

        public ChangeRecord Record { get; }
        public ChangeAction Action => Record.Action;
        public string Kind => ChangeLogExporter.KindName(Record.Kind);
        public string Object => Record.ObjectLabel;
        public string Page => Record.PageLabel ?? "—";
        public string User => Record.User;
        public string Machine => Record.Machine;
        public string Time { get; }
        public string Relative { get; }
        public string Integrity { get; }
        public bool IsInferred => Record.Attribution == Attribution.Inferred;
        public bool IsNew { get; }
        public bool IsUntrusted => Integrity == "UNTRUSTED";
        public string FieldSummary => Record.FieldSummary;
        public IList<FieldChange> Fields => Record.Fields;
        public string BatchId => Record.BatchId;
        public string JournalRef => Record.JournalFile == null ? "pending write" : Record.JournalFile + " #" + Record.JournalSeq;
        public string Hash => string.IsNullOrEmpty(Record.EntryHash) ? "—" : Record.EntryHash.Substring(0, 16) + "…";
        public string AttributionText => Record.Attribution == Attribution.Confirmed
            ? "Confirmed — recorded live in " + Record.User + "'s session on " + (Record.Machine ?? "?")
            : "Inferred — change was made while the add-in was not recording; user is a best guess";

        private static string RelativeTime(TimeSpan d, DateTime local)
        {
            if (d.TotalSeconds < 60) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + " min ago";
            if (d.TotalHours < 24) return (int)d.TotalHours + " h ago";
            if (d.TotalDays < 7) return (int)d.TotalDays + " d ago";
            return local.ToString("d MMM yyyy", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>State of the docked Change Log pane.</summary>
    public sealed class ChangeLogViewModel : ViewModelBase
    {
        public const int MaxRows = 5000;
        public static readonly string[] Ranges = { "All time", "Today", "Last 7 days", "Last 30 days" };

        private readonly AddInHost _host;
        private readonly DispatcherTimer _refresh;
        private readonly DispatcherTimer _toastTimer;
        private ProjectMonitor _monitor;
        private string _search;
        private string _range = Ranges[0];
        private bool _includeReconciled = true;
        private string _batchFilter;
        private ChangeRowViewModel _selected;
        private IList<DayBucket> _days;
        private bool _isCompact;
        private string _toastText;
        private NotificationKind _toastKind;
        private bool _isToastVisible;
        private List<ChangeRecord> _filtered = new List<ChangeRecord>();

        public ChangeLogViewModel(AddInHost host)
        {
            _host = host;
            Rows = new ObservableCollection<ChangeRowViewModel>();
            KindChips = new ObservableCollection<ChipViewModel>();
            UserChips = new ObservableCollection<ChipViewModel>();
            ActionChips = new ObservableCollection<ChipViewModel>
            {
                new ChipViewModel("Added", ChangeAction.Added, 0, QueueRefresh),
                new ChipViewModel("Edited", ChangeAction.Edited, 0, QueueRefresh),
                new ChipViewModel("Removed", ChangeAction.Removed, 0, QueueRefresh)
            };
            KindBars = new ObservableCollection<BarViewModel>();

            _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _refresh.Tick += (s, e) =>
            {
                _refresh.Stop();
                Rebuild();
            };
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _toastTimer.Tick += (s, e) =>
            {
                _toastTimer.Stop();
                IsToastVisible = false;
            };

            ExportCommand = new RelayCommand(() => _host.Ui.ExportToExcel(), () => HasProject);
            CompareCommand = new RelayCommand(() => _host.Ui.ShowCompare(), () => HasProject);
            CaptureCommand = new RelayCommand(() => _host.CaptureChanges(), () => HasProject && _monitor.State == MonitorState.Recording);
            VerifyCommand = new RelayCommand(() => _host.VerifyActive(), () => HasProject);
            SettingsCommand = new RelayCommand(() => _host.Ui.ShowSettings());
            ClearFiltersCommand = new RelayCommand(ClearFilters);
            FilterBatchCommand = new RelayCommand(() =>
            {
                _batchFilter = Selected?.BatchId;
                Raise(nameof(BatchFilterText));
                Rebuild();
            }, () => Selected?.BatchId != null);
            ClearBatchCommand = new RelayCommand(() =>
            {
                _batchFilter = null;
                Raise(nameof(BatchFilterText));
                Rebuild();
            });
            CloseDetailCommand = new RelayCommand(() => Selected = null);

            _host.ActiveChanged += (s, e) => Attach(_host.Active);
            Attach(_host.Active);
        }

        // ---------------------------------------------------------------- bindable state

        public ObservableCollection<ChangeRowViewModel> Rows { get; private set; }
        public ObservableCollection<ChipViewModel> KindChips { get; }
        public ObservableCollection<ChipViewModel> UserChips { get; }
        public ObservableCollection<ChipViewModel> ActionChips { get; }
        public ObservableCollection<BarViewModel> KindBars { get; }
        public IEnumerable<string> RangeOptions => Ranges;

        public bool HasProject => _monitor != null;
        public string ProjectName => _monitor?.Name ?? "No project open";

        public string StatusText
        {
            get
            {
                if (_monitor == null) return "Open a project to start recording";
                switch (_monitor.State)
                {
                    case MonitorState.Indexing:
                        return "Indexing project… " + _monitor.IndexedCount.ToString("N0", CultureInfo.CurrentCulture) + " objects";
                    case MonitorState.Recording:
                        return "Recording as " + _host.Who.DisplayName;
                    case MonitorState.Failed:
                        return "Not recording — indexing failed (see log)";
                    default:
                        return "Closed";
                }
            }
        }

        public string StatusKind => _monitor == null ? "Idle" : _monitor.State.ToString();

        public string StorageText
        {
            get
            {
                var s = _monitor?.Session;
                if (s == null) return string.Empty;
                switch (s.Health)
                {
                    case StorageHealth.Ok: return "All changes saved";
                    case StorageHealth.Pending: return "Saving…";
                    default: return s.LastError;
                }
            }
        }

        public bool StorageError => _monitor?.Session.Health == StorageHealth.Error;

        public IntegrityStatus IntegrityStatus => _monitor?.Session.Integrity?.Status ?? IntegrityStatus.Empty;
        public string IntegrityText => _monitor?.Session.Integrity?.Summary ?? "Not verified";
        public IEnumerable<IntegrityIssue> IntegrityIssues =>
            _monitor?.Session.Integrity?.Issues.Where(i => i.Severity != IssueSeverity.Info).Take(20) ?? Enumerable.Empty<IntegrityIssue>();

        public int Total { get; private set; }
        public int Added { get; private set; }
        public int Edited { get; private set; }
        public int Removed { get; private set; }
        public int Contributors { get; private set; }
        public string ResultText { get; private set; }

        public IList<DayBucket> Days
        {
            get { return _days; }
            private set { Set(ref _days, value); }
        }

        public string Search
        {
            get { return _search; }
            set
            {
                if (Set(ref _search, value)) QueueRefresh();
            }
        }

        public string Range
        {
            get { return _range; }
            set
            {
                if (Set(ref _range, value)) QueueRefresh();
            }
        }

        public bool IncludeReconciled
        {
            get { return _includeReconciled; }
            set
            {
                if (Set(ref _includeReconciled, value)) QueueRefresh();
            }
        }

        public string BatchFilterText => _batchFilter == null ? null : "Showing one batch of related changes";

        public ChangeRowViewModel Selected
        {
            get { return _selected; }
            set
            {
                if (Set(ref _selected, value)) Raise(nameof(HasSelection));
            }
        }

        public bool HasSelection => _selected != null;

        public bool IsCompact
        {
            get { return _isCompact; }
            set { Set(ref _isCompact, value); }
        }

        public string ToastText
        {
            get { return _toastText; }
            private set { Set(ref _toastText, value); }
        }

        public NotificationKind ToastKind
        {
            get { return _toastKind; }
            private set { Set(ref _toastKind, value); }
        }

        public bool IsToastVisible
        {
            get { return _isToastVisible; }
            set { Set(ref _isToastVisible, value); }
        }

        public ICommand ExportCommand { get; }
        public ICommand CompareCommand { get; }
        public ICommand CaptureCommand { get; }
        public ICommand VerifyCommand { get; }
        public ICommand SettingsCommand { get; }
        public ICommand ClearFiltersCommand { get; }
        public ICommand FilterBatchCommand { get; }
        public ICommand ClearBatchCommand { get; }
        public ICommand CloseDetailCommand { get; }

        /// <summary>Records matching the current filters (all of them, not just the displayed rows).</summary>
        public IList<ChangeRecord> FilteredRecords => _filtered;

        public string FilterDescription
        {
            get
            {
                var parts = new List<string>();
                var kinds = KindChips.Where(c => c.IsChecked).Select(c => c.Label).ToList();
                var users = UserChips.Where(c => c.IsChecked).Select(c => c.Label).ToList();
                var acts = ActionChips.Where(c => c.IsChecked).Select(c => c.Label).ToList();
                if (kinds.Count > 0) parts.Add("Type: " + string.Join(", ", kinds));
                if (users.Count > 0) parts.Add("User: " + string.Join(", ", users));
                if (acts.Count > 0) parts.Add("Action: " + string.Join(", ", acts));
                if (Range != Ranges[0]) parts.Add(Range);
                if (!string.IsNullOrWhiteSpace(Search)) parts.Add("Search: \"" + Search + "\"");
                if (!IncludeReconciled) parts.Add("Live changes only");
                if (_batchFilter != null) parts.Add("Batch " + _batchFilter);
                return string.Join(" · ", parts);
            }
        }

        public void Notify(string message, NotificationKind kind)
        {
            ToastText = message;
            ToastKind = kind;
            IsToastVisible = true;
            _toastTimer.Stop();
            if (kind != NotificationKind.Error) _toastTimer.Start();
        }

        // ---------------------------------------------------------------- wiring

        private void Attach(ProjectMonitor m)
        {
            if (_monitor != null)
            {
                _monitor.StateChanged -= OnMonitorState;
                _monitor.Session.RecordsAdded -= OnRecords;
                _monitor.Session.HealthChanged -= OnHealth;
                _monitor.Session.IntegrityChanged -= OnIntegrity;
            }

            _monitor = m;
            if (_monitor != null)
            {
                _monitor.StateChanged += OnMonitorState;
                _monitor.Session.RecordsAdded += OnRecords;
                _monitor.Session.HealthChanged += OnHealth;
                _monitor.Session.IntegrityChanged += OnIntegrity;
            }

            Selected = null;
            _batchFilter = null;
            KindChips.Clear(); // chips are per project (different users and object types)
            UserChips.Clear();
            foreach (var c in ActionChips) c.Reset();
            Raise(nameof(HasProject));
            Raise(nameof(ProjectName));
            OnMonitorState(null, EventArgs.Empty);
            OnHealth(null, EventArgs.Empty);
            OnIntegrity(null, EventArgs.Empty);
            Rebuild();
        }

        private void OnMonitorState(object s, EventArgs e)
        {
            Raise(nameof(StatusText));
            Raise(nameof(StatusKind));
            CommandManager.InvalidateRequerySuggested();
        }

        private void OnRecords(object s, RecordsEventArgs e) => QueueRefresh();

        private void OnHealth(object s, EventArgs e)
        {
            Raise(nameof(StorageText));
            Raise(nameof(StorageError));
        }

        private void OnIntegrity(object s, EventArgs e)
        {
            Raise(nameof(IntegrityStatus));
            Raise(nameof(IntegrityText));
            Raise(nameof(IntegrityIssues));
            QueueRefresh();
        }

        private void QueueRefresh()
        {
            _refresh.Stop();
            _refresh.Start();
        }

        private void ClearFilters()
        {
            foreach (var c in KindChips.Concat(UserChips).Concat(ActionChips)) c.Reset();
            _search = null;
            _range = Ranges[0];
            _includeReconciled = true;
            _batchFilter = null;
            Raise(nameof(Search));
            Raise(nameof(Range));
            Raise(nameof(IncludeReconciled));
            Raise(nameof(BatchFilterText));
            Rebuild();
        }

        // ---------------------------------------------------------------- filtering

        private ChangeQuery BuildQuery()
        {
            var q = new ChangeQuery { Text = Search, IncludeReconciled = IncludeReconciled };
            foreach (var c in KindChips.Where(c => c.IsChecked)) q.Kinds.Add((ObjectKind)c.Value);
            foreach (var c in UserChips.Where(c => c.IsChecked)) q.Users.Add((string)c.Value);
            foreach (var c in ActionChips.Where(c => c.IsChecked)) q.Actions.Add((ChangeAction)c.Value);
            var today = DateTime.Today;
            switch (Array.IndexOf(Ranges, Range))
            {
                case 1: q.FromUtc = today.ToUniversalTime(); break;
                case 2: q.FromUtc = today.AddDays(-6).ToUniversalTime(); break;
                case 3: q.FromUtc = today.AddDays(-29).ToUniversalTime(); break;
            }

            return q;
        }

        private void Rebuild()
        {
            var all = _monitor?.Session.Records ?? (IReadOnlyList<ChangeRecord>)new ChangeRecord[0];
            SyncChips(KindChips, all.GroupBy(r => r.Kind).ToDictionary(g => (object)g.Key, g => g.Count()), k => ChangeLogExporter.KindName((ObjectKind)k));
            SyncChips(UserChips, all.GroupBy(r => r.User ?? "Unknown", StringComparer.OrdinalIgnoreCase).ToDictionary(g => (object)g.Key, g => g.Count()), k => (string)k);
            foreach (var a in ActionChips) a.Count = all.Count(r => r.Action == (ChangeAction)a.Value);

            var q = BuildQuery();
            var filtered = q.Apply(all);
            if (_batchFilter != null) filtered = filtered.Where(r => r.BatchId == _batchFilter);
            _filtered = filtered.OrderByDescending(r => r.TimestampIso, StringComparer.Ordinal).ToList();

            var integrity = _monitor?.Session.Integrity;
            var now = DateTime.UtcNow;
            var keep = Selected?.Record;
            Rows = new ObservableCollection<ChangeRowViewModel>(_filtered.Take(MaxRows).Select(r => new ChangeRowViewModel(r, integrity, now)));
            Raise(nameof(Rows));
            if (keep != null) Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Record, keep));

            var stats = ChangeStats.Compute(_filtered, DateTime.Today, 14);
            Total = stats.Total;
            Added = stats.Added;
            Edited = stats.Edited;
            Removed = stats.Removed;
            Contributors = stats.Contributors;
            Days = stats.ByDay;
            ResultText = _filtered.Count > MaxRows
                ? "Showing newest " + MaxRows.ToString("N0", CultureInfo.CurrentCulture) + " of " + _filtered.Count.ToString("N0", CultureInfo.CurrentCulture) + " — refine filters or export"
                : _filtered.Count.ToString("N0", CultureInfo.CurrentCulture) + " of " + all.Count.ToString("N0", CultureInfo.CurrentCulture) + " changes";

            var max = Math.Max(1, stats.ByKind.Select(k => k.Value).DefaultIfEmpty(0).Max());
            KindBars.Clear();
            foreach (var k in stats.ByKind.Take(8))
                KindBars.Add(new BarViewModel
                {
                    Label = ChangeLogExporter.KindName(k.Key), Value = k.Value,
                    Fill = new GridLength(k.Value, GridUnitType.Star), Rest = new GridLength(max - k.Value, GridUnitType.Star)
                });

            foreach (var p in new[] { nameof(Total), nameof(Added), nameof(Edited), nameof(Removed), nameof(Contributors), nameof(ResultText), nameof(FilterDescription), nameof(HasFilters) })
                Raise(p);
        }

        public bool HasFilters => !string.IsNullOrEmpty(FilterDescription);

        private void SyncChips(ObservableCollection<ChipViewModel> chips, Dictionary<object, int> counts, Func<object, string> label)
        {
            foreach (var c in chips) c.Count = counts.ContainsKey(c.Value) ? counts[c.Value] : 0;
            foreach (var kv in counts.OrderByDescending(k => k.Value))
                if (chips.All(c => !Equals(c.Value, kv.Key)))
                    chips.Add(new ChipViewModel(label(kv.Key), kv.Key, kv.Value, QueueRefresh));
        }
    }
}
