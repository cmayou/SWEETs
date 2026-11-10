using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using ChangeLog.AddIn.Host;
using ChangeLog.AddIn.UI.Controls;
using ChangeLog.Core.Export;
using ChangeLog.Core.Model;
using ChangeLog.Core.State;

namespace ChangeLog.AddIn.UI
{
    public sealed class SnapshotOption
    {
        public SnapshotOption(BaselineInfo info)
        {
            Info = info;
        }

        /// <summary>null = the live, current project state.</summary>
        public BaselineInfo Info { get; }

        public string Display => Info == null ? "● Current project (live)" : Info.Display;
        public string Kind => Info == null ? "Live" : Info.Kind.ToString();
    }

    public sealed class PageRow
    {
        public PageDiffSummary Summary { get; set; }
        public string Label => Summary.PageLabel;
        public int Added => Summary.Added;
        public int Edited => Summary.Edited;
        public int Removed => Summary.Removed;
        public int Total => Summary.Total;
        public GridLength Fill { get; set; }
        public GridLength Rest { get; set; }
    }

    public sealed class KindRow
    {
        public string Kind { get; set; }
        public int Added { get; set; }
        public int Edited { get; set; }
        public int Removed { get; set; }
    }

    public sealed class DiffRow
    {
        public DiffRow(DiffItem item)
        {
            Item = item;
        }

        public DiffItem Item { get; }
        public ChangeAction Action => Item.Action;
        public string Kind => ChangeLogExporter.KindName(Item.Kind);
        public string Label => Item.Label;
        public string FieldSummary => Item.FieldSummary;
        public IList<FieldChange> Fields => Item.Fields;
        public bool HasGeometry => (Item.After ?? Item.Before)?.Geometry != null;
    }

    public sealed class CompareViewModel : ViewModelBase
    {
        private readonly ProjectMonitor _monitor;
        private SnapshotOption _left;
        private SnapshotOption _right;
        private bool _isBusy;
        private string _busyText;
        private string _error;
        private DiffResult _result;
        private PageRow _selectedPage;
        private IList<CanvasItem> _canvasItems;
        private DiffRow _selectedItem;
        private ObjectKey? _selectedKey;
        private bool _showUnchanged = true;

        public CompareViewModel(AddInHost host, ProjectMonitor monitor)
        {
            _monitor = monitor;
            Options = new ObservableCollection<SnapshotOption>();
            Pages = new ObservableCollection<PageRow>();
            Kinds = new ObservableCollection<KindRow>();
            PageItems = new ObservableCollection<DiffRow>();
            OtherItems = new ObservableCollection<DiffRow>();

            CompareCommand = new RelayCommand(async () => await CompareAsync(), () => !IsBusy && Left != null && Right != null && Left != Right);
            SwapCommand = new RelayCommand(() =>
            {
                var l = Left;
                Left = Right;
                Right = l;
            });
            CaptureCommand = new RelayCommand(() =>
            {
                IsBusy = true;
                BusyText = "Capturing changes…";
                _monitor.CaptureBaseline("Capture " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"), BaselineKind.Manual, null, info =>
                {
                    IsBusy = false;
                    LoadOptions();
                    Right = Options.FirstOrDefault(o => o.Info != null && o.Info.Id == info.Id) ?? Right;
                });
            }, () => !IsBusy && _monitor.State == MonitorState.Recording);

            _monitor.Session.BaselinesChanged += OnBaselinesChanged;
            LoadOptions();
            var stored = Options.Where(o => o.Info != null).ToList();
            Left = stored.Count > 0 ? stored[stored.Count - 1] : null;
            Right = Options.FirstOrDefault(o => o.Info == null);
        }

        public string Title => "Compare captures — " + _monitor.Name;
        public ObservableCollection<SnapshotOption> Options { get; }
        public ObservableCollection<PageRow> Pages { get; }
        public ObservableCollection<KindRow> Kinds { get; }
        public ObservableCollection<DiffRow> PageItems { get; }
        public ObservableCollection<DiffRow> OtherItems { get; }

        public ICommand CompareCommand { get; }
        public ICommand SwapCommand { get; }
        public ICommand CaptureCommand { get; }

        public SnapshotOption Left
        {
            get { return _left; }
            set { Set(ref _left, value); }
        }

        public SnapshotOption Right
        {
            get { return _right; }
            set { Set(ref _right, value); }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            private set
            {
                Set(ref _isBusy, value);
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public string BusyText
        {
            get { return _busyText; }
            private set { Set(ref _busyText, value); }
        }

        public string Error
        {
            get { return _error; }
            private set { Set(ref _error, value); }
        }

        public bool HasResult => _result != null;
        public bool HasNoSnapshots => Options.Count(o => o.Info != null) == 0;
        public int Added => _result?.Count(ChangeAction.Added) ?? 0;
        public int Edited => _result?.Count(ChangeAction.Edited) ?? 0;
        public int Removed => _result?.Count(ChangeAction.Removed) ?? 0;
        public int PagesChanged => _result?.Pages.Count(p => p.PageId.HasValue) ?? 0;
        public string ResultCaption { get; private set; }

        public PageRow SelectedPage
        {
            get { return _selectedPage; }
            set
            {
                if (Set(ref _selectedPage, value)) BuildPage();
            }
        }

        public IList<CanvasItem> CanvasItems
        {
            get { return _canvasItems; }
            private set { Set(ref _canvasItems, value); }
        }

        public bool ShowUnchanged
        {
            get { return _showUnchanged; }
            set { Set(ref _showUnchanged, value); }
        }

        public DiffRow SelectedItem
        {
            get { return _selectedItem; }
            set
            {
                if (!Set(ref _selectedItem, value)) return;
                var key = value?.Item.Key;
                if (!Equals(_selectedKey, key))
                {
                    _selectedKey = key;
                    Raise(nameof(SelectedKey));
                }

                Raise(nameof(HasSelectedItem));
            }
        }

        public bool HasSelectedItem => _selectedItem != null;

        /// <summary>Two-way with the canvas: clicking a shape selects its row.</summary>
        public ObjectKey? SelectedKey
        {
            get { return _selectedKey; }
            set
            {
                if (Equals(_selectedKey, value)) return;
                _selectedKey = value;
                Raise();
                SelectedItem = value.HasValue ? PageItems.FirstOrDefault(r => r.Item.Key.Equals(value.Value)) : null;
            }
        }

        /// <summary>Call when the window closes so the session does not keep this view model alive.</summary>
        public void Detach() => _monitor.Session.BaselinesChanged -= OnBaselinesChanged;

        private void OnBaselinesChanged(object sender, EventArgs e) => LoadOptions();

        private void LoadOptions()
        {
            var l = Left?.Info?.Id;
            var r = Right?.Info?.Id;
            var leftLive = Left != null && Left.Info == null;
            var rightLive = Right != null && Right.Info == null;
            Options.Clear();
            foreach (var b in _monitor.Session.Baselines) Options.Add(new SnapshotOption(b));
            Options.Add(new SnapshotOption(null));
            _left = leftLive ? Options.Last() : Options.FirstOrDefault(o => o.Info != null && o.Info.Id == l) ?? _left;
            _right = rightLive ? Options.Last() : Options.FirstOrDefault(o => o.Info != null && o.Info.Id == r) ?? _right;
            Raise(nameof(Left));
            Raise(nameof(Right));
            Raise(nameof(HasNoSnapshots));
        }

        private async Task CompareAsync()
        {
            Error = null;
            IsBusy = true;
            BusyText = "Loading captures…";
            try
            {
                _monitor.Session.FlushNow();
                var live = _monitor.Session.CurrentState(); // UI thread: reads the in-memory index only
                var left = Left.Info;
                var right = Right.Info;
                var session = _monitor.Session;
                var states = await Task.Run(() => new[]
                {
                    left == null ? live : session.LoadBaseline(left),
                    right == null ? live : session.LoadBaseline(right)
                });
                BusyText = "Comparing…";
                var result = await Task.Run(() => DiffEngine.Compare(states[0], states[1]));
                Show(result);
            }
            catch (Exception ex)
            {
                Log.Error("Compare failed", ex);
                Error = ex.Message;
            }
            finally
            {
                IsBusy = false;
            }
        }

        private void Show(DiffResult result)
        {
            _result = result;
            Kinds.Clear();
            foreach (var g in result.Items.GroupBy(i => i.Kind).OrderBy(g => g.Key))
                Kinds.Add(new KindRow
                {
                    Kind = ChangeLogExporter.KindName(g.Key),
                    Added = g.Count(i => i.Action == ChangeAction.Added),
                    Edited = g.Count(i => i.Action == ChangeAction.Edited),
                    Removed = g.Count(i => i.Action == ChangeAction.Removed)
                });

            Pages.Clear();
            var max = Math.Max(1, result.Pages.Where(p => p.PageId.HasValue).Select(p => p.Total).DefaultIfEmpty(0).Max());
            foreach (var p in result.Pages.Where(p => p.PageId.HasValue))
                Pages.Add(new PageRow { Summary = p, Fill = new GridLength(p.Total, GridUnitType.Star), Rest = new GridLength(max - p.Total, GridUnitType.Star) });

            OtherItems.Clear();
            foreach (var i in result.Items.Where(i => !i.PageId.HasValue)) OtherItems.Add(new DiffRow(i));

            ResultCaption = Left.Display + "   →   " + Right.Display;
            foreach (var n in new[] { nameof(HasResult), nameof(Added), nameof(Edited), nameof(Removed), nameof(PagesChanged), nameof(ResultCaption) }) Raise(n);
            SelectedPage = Pages.FirstOrDefault();
            if (SelectedPage == null) BuildPage();
        }

        private void BuildPage()
        {
            PageItems.Clear();
            SelectedItem = null;
            if (_result == null || _selectedPage == null)
            {
                CanvasItems = null;
                return;
            }

            var pageId = _selectedPage.Summary.PageId;
            var diff = _result.Items.Where(i => i.PageId == pageId).ToDictionary(i => i.Key);
            foreach (var d in diff.Values.OrderBy(d => d.Action).ThenBy(d => d.Kind)) PageItems.Add(new DiffRow(d));

            var before = _result.Before.Objects.Where(o => o.PageId == pageId && o.Geometry != null).ToDictionary(o => o.Key);
            var after = _result.After.Objects.Where(o => o.PageId == pageId && o.Geometry != null).ToDictionary(o => o.Key);
            var items = new List<CanvasItem>();
            foreach (var key in before.Keys.Union(after.Keys))
            {
                DiffItem d;
                diff.TryGetValue(key, out d);
                ObjectState b, a;
                before.TryGetValue(key, out b);
                after.TryGetValue(key, out a);
                var main = a ?? b;
                items.Add(new CanvasItem { Key = key, Shape = main.Geometry, Action = d?.Action, Label = d == null ? null : main.DisplayLabel });
                if (d != null && d.Action == ChangeAction.Edited && b != null && a != null && !ObjectDiffer.GeometryEqual(b.Geometry, a.Geometry))
                    items.Add(new CanvasItem { Key = key, Shape = b.Geometry, Action = ChangeAction.Edited, IsGhost = true });
            }

            CanvasItems = items;
        }
    }
}
