using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using SWEETs.SweDrawingParameters.AddIn.Infrastructure;
using SWEETs.SweDrawingParameters.AddIn.Interop;
using SWEETs.SweDrawingParameters.AddIn.Model;
using SWEETs.SweDrawingParameters.AddIn.Services;

namespace SWEETs.SweDrawingParameters.AddIn.UI.ViewModels
{
    public enum Banner { None, Info, Success, Warning, Error }

    public sealed class TypeFilterItem : ObservableObject
    {
        private readonly MainViewModel _owner;
        public TypeFilterItem(MainViewModel owner, string label, int count) { _owner = owner; Label = label; Count = count; }
        public string Label { get; }
        public int Count { get; }
        private bool _isChecked = true;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (Set(ref _isChecked, value)) _owner.ApplyTypeFilter(Label, value); }
        }

        /// <summary>Updates the chip without re-selecting rows (used by Select all / none).</summary>
        internal void SetSilently(bool value) { _isChecked = value; Raise(nameof(IsChecked)); }
    }

    public sealed class MainViewModel : ObservableObject
    {
        private readonly object _app;
        private ProjectInfo _project;

        public MainViewModel(object application)
        {
            _app = application;
            ApplyCommand = new RelayCommand(Apply, () => !IsBusy && ValidationMessage == null);
            RefreshCommand = new AsyncCommand(LoadAsync, () => !IsBusy, OnError);
            SelectAllCommand = new RelayCommand(() => SetAll(true), () => !IsBusy);
            SelectNoneCommand = new RelayCommand(() => SetAll(false), () => !IsBusy);
            UseRowValuesCommand = new RelayCommand(p => UseValuesFrom(p as DrawingRowViewModel), p => !IsBusy && (p as DrawingRowViewModel)?.Current != null);
            OpenBackupsCommand = new RelayCommand(() => OpenFolder(Path.Combine(AppPaths.LocalRoot, "backups")));
            OpenLogCommand = new RelayCommand(() => OpenFolder(FileLog.Folder));

            LoadSettings(UserSettings.Load());
        }

        // ------------------------------------------------------------------ header / banner

        private string _projectName = "No project";
        public string ProjectName { get => _projectName; private set => Set(ref _projectName, value); }

        private Banner _bannerKind;
        public Banner BannerKind { get => _bannerKind; private set => Set(ref _bannerKind, value); }
        private string _bannerText;
        public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }

        private void ShowBanner(Banner kind, string text) { BannerKind = kind; BannerText = text; }

        // ------------------------------------------------------------------ parameters

        private bool _applySnap = true, _snapOn = true, _applyGrid = true, _gridOn = true, _applyZone, _applyOrtho, _orthoOn;
        private string _snapSpacing, _gridSpacing, _zoneMinX, _zoneMinY, _zoneMaxX, _zoneMaxY;

        public bool ApplySnap { get => _applySnap; set { if (Set(ref _applySnap, value)) Revalidate(); } }
        public bool SnapOn { get => _snapOn; set => Set(ref _snapOn, value); }
        /// <summary>One value for X and Y, as in SOLIDWORKS Electrical's Drawing parameters dialog.</summary>
        public string SnapSpacing { get => _snapSpacing; set { if (Set(ref _snapSpacing, value)) Revalidate(); } }

        public bool ApplyGrid { get => _applyGrid; set { if (Set(ref _applyGrid, value)) Revalidate(); } }
        public bool GridOn { get => _gridOn; set => Set(ref _gridOn, value); }
        /// <summary>One value for X and Y, as in SOLIDWORKS Electrical's Drawing parameters dialog.</summary>
        public string GridSpacing { get => _gridSpacing; set { if (Set(ref _gridSpacing, value)) Revalidate(); } }

        public bool ApplyZone { get => _applyZone; set { if (Set(ref _applyZone, value)) Revalidate(); } }
        public string ZoneMinX { get => _zoneMinX; set { if (Set(ref _zoneMinX, value)) Revalidate(); } }
        public string ZoneMinY { get => _zoneMinY; set { if (Set(ref _zoneMinY, value)) Revalidate(); } }
        public string ZoneMaxX { get => _zoneMaxX; set { if (Set(ref _zoneMaxX, value)) Revalidate(); } }
        public string ZoneMaxY { get => _zoneMaxY; set { if (Set(ref _zoneMaxY, value)) Revalidate(); } }

        public bool ApplyOrtho { get => _applyOrtho; set { if (Set(ref _applyOrtho, value)) Revalidate(); } }
        public bool OrthoOn { get => _orthoOn; set => Set(ref _orthoOn, value); }


        private string _validationMessage = "Loading…";
        public string ValidationMessage { get => _validationMessage; private set => Set(ref _validationMessage, value); }

        // ------------------------------------------------------------------ drawings

        public ObservableCollection<DrawingRowViewModel> Rows { get; } = new ObservableCollection<DrawingRowViewModel>();
        public ObservableCollection<TypeFilterItem> TypeFilters { get; } = new ObservableCollection<TypeFilterItem>();

        public int TotalCount => Rows.Count;
        public int SelectedCount => Rows.Count(r => r.IsSelected);
        public int UnreadableCount => Rows.Count(r => r.State == RowState.Unreadable || r.State == RowState.NotSupported);
        public string ApplyCaption => SelectedCount == 1 ? "Apply to 1 drawing" : $"Apply to {SelectedCount} drawings";

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; private set { if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested(); } }
        private double _progress;
        public double Progress { get => _progress; private set => Set(ref _progress, value); }
        private string _progressText = "";
        public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

        public ICommand ApplyCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand SelectAllCommand { get; }
        public ICommand SelectNoneCommand { get; }
        public ICommand UseRowValuesCommand { get; }
        public ICommand OpenBackupsCommand { get; }
        public ICommand OpenLogCommand { get; }

        internal void ApplyTypeFilter(string label, bool selected)
        {
            foreach (var row in Rows.Where(x => x.TypeLabel == label && CanUpdate(x))) row.IsSelected = selected;
        }

        private void SetAll(bool value)
        {
            foreach (var r in Rows) r.IsSelected = value && CanUpdate(r);
            foreach (var f in TypeFilters) f.SetSilently(value);
        }

        private static bool CanUpdate(DrawingRowViewModel r) => r.State != RowState.NotSupported && r.State != RowState.Unreadable;

        private void RaiseCounts()
        {
            Raise(nameof(TotalCount)); Raise(nameof(SelectedCount)); Raise(nameof(UnreadableCount)); Raise(nameof(ApplyCaption));
        }

        // ------------------------------------------------------------------ load

        public async Task LoadAsync()
        {
            IsBusy = true;
            try
            {
                foreach (var r in Rows) r.PropertyChanged -= OnRowChanged;
                Rows.Clear(); TypeFilters.Clear(); RaiseCounts();
                ShowBanner(Banner.None, null);

                var problems = SweApi.CheckContract().Where(p => p.StartsWith("REQUIRED")).ToList();
                if (problems.Count > 0)
                {
                    ShowBanner(Banner.Error, "This SOLIDWORKS Electrical version does not expose: " + string.Join("; ", problems));
                    return;
                }

                _project = ProjectDrawings.LoadCurrent(_app);
                if (_project == null)
                {
                    ProjectName = "No project open";
                    ShowBanner(Banner.Warning, "Open a project in SOLIDWORKS Electrical, then click Refresh.");
                    return;
                }
                ProjectName = _project.Name ?? $"Project {_project.Id}";
                foreach (var d in _project.Drawings)
                {
                    var row = new DrawingRowViewModel(d);
                    row.PropertyChanged += OnRowChanged;
                    Rows.Add(row);
                }
                foreach (var g in Rows.GroupBy(r => r.TypeLabel))
                    TypeFilters.Add(new TypeFilterItem(this, g.Key, g.Count()));
                RaiseCounts();

                if (Rows.Count == 0) { ShowBanner(Banner.Warning, "This project has no drawings."); return; }

                // Current values, read from the drawing files in the background (read-only).
                int done = 0;
                foreach (var row in Rows.ToList())
                {
                    ProgressText = $"Reading drawing {++done} of {Rows.Count}";
                    Progress = (double)done / Rows.Count;
                    var path = row.Drawing.FilePath;
                    var read = await Task.Run(() => DwgParameterReader.Read(path));
                    if (read.Ok) row.Current = read.Values;
                    if (read.Ok && read.Patchable) row.State = RowState.Ready;
                    else
                    {
                        // Drawings the patcher cannot update are shown but not selected.
                        row.State = read.Ok ? RowState.NotSupported : RowState.Unreadable;
                        row.Detail = read.Reason ?? read.Error;
                        row.IsSelected = false;
                    }
                }
                ProgressText = ""; Progress = 0;

                if (!_hasSavedValues) UseValuesFrom(Rows.FirstOrDefault(r => r.Current != null));
                var unreadable = Rows.Count(r => r.State == RowState.Unreadable || r.State == RowState.NotSupported);
                if (unreadable > 0)
                    ShowBanner(Banner.Warning, $"{unreadable} drawing(s) cannot be updated (unreadable or unsupported DWG version). They are not selected; hover over the status for details.");
            }
            finally
            {
                IsBusy = false;
                Revalidate();
            }
        }

        private void OnRowChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DrawingRowViewModel.IsSelected)) { RaiseCounts(); Revalidate(); }
            else if (e.PropertyName == nameof(DrawingRowViewModel.State)) RaiseCounts();
        }

        private void UseValuesFrom(DrawingRowViewModel row)
        {
            var c = row?.Current;
            if (c == null) return;
            if (c.SnapOn.HasValue) SnapOn = c.SnapOn.Value;
            SnapSpacing = DrawingParameters.Fmt(c.SnapX);
            if (c.GridOn.HasValue) GridOn = c.GridOn.Value;
            GridSpacing = DrawingParameters.Fmt(c.GridX);
            ZoneMinX = DrawingParameters.Fmt(c.ZoneMinX); ZoneMinY = DrawingParameters.Fmt(c.ZoneMinY);
            ZoneMaxX = DrawingParameters.Fmt(c.ZoneMaxX); ZoneMaxY = DrawingParameters.Fmt(c.ZoneMaxY);
            if (c.OrthoOn.HasValue) OrthoOn = c.OrthoOn.Value;
            ShowBanner(Banner.Info, $"Values copied from {row.Name}. Edit them, then apply.");
        }

        // ------------------------------------------------------------------ validation

        private void Revalidate()
        {
            ValidationMessage = Validate(out _);
            CommandManager.InvalidateRequerySuggested();
        }

        private string Validate(out DrawingParameters request)
        {
            request = new DrawingParameters();
            if (_project == null) return "Open a project in SOLIDWORKS Electrical.";
            if (SelectedCount == 0) return "Select at least one drawing.";
            if (!ApplySnap && !ApplyGrid && !ApplyZone && !ApplyOrtho) return "Choose at least one parameter to apply.";

            if (ApplySnap)
            {
                if (!Positive(SnapSpacing, out var s)) return "Snap spacing must be a number greater than 0.";
                request.SnapOn = SnapOn; request.SnapX = s; request.SnapY = s;
            }
            if (ApplyGrid)
            {
                if (!Positive(GridSpacing, out var g)) return "Grid spacing must be a number greater than 0.";
                request.GridOn = GridOn; request.GridX = g; request.GridY = g;
            }
            if (ApplyZone)
            {
                if (!Number(ZoneMinX, out var a) || !Number(ZoneMinY, out var b) || !Number(ZoneMaxX, out var c) || !Number(ZoneMaxY, out var d))
                    return "Grid zone corners must be numbers.";
                if (c <= a || d <= b) return "Grid zone: the upper-right corner must be above and to the right of the lower-left corner.";
                request.ZoneMinX = a; request.ZoneMinY = b; request.ZoneMaxX = c; request.ZoneMaxY = d;
            }
            if (ApplyOrtho) request.OrthoOn = OrthoOn;
            return null;
        }


        private static bool Number(string s, out double v) =>
            double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ||
            double.TryParse(s?.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out v);
        private static bool Positive(string s, out double v) => Number(s, out v) && v > 0;
        private static bool NonNegative(string s, out double v) => Number(s, out v) && v >= 0;

        // ------------------------------------------------------------------ apply

        /// <summary>
        /// Raised when the user confirms an update. The dialog then closes and the run happens in the run window:
        /// SOLIDWORKS Electrical only executes runCommand commands from its own message loop, which is blocked while
        /// this modal dialog is open.
        /// </summary>
        public event EventHandler<RunPlan> PlanReady;

        private void Apply()
        {
            var error = Validate(out var request);
            if (error != null) { ShowBanner(Banner.Warning, error); return; }
            var targets = Rows.Where(r => r.IsSelected).Select(r => r.Drawing).ToList();

            var confirm = MessageBox.Show(
                $"Update {targets.Count} drawing(s) in \"{ProjectName}\"?\n\n" +
                "Each drawing file is updated directly: a copy is saved to the backup folder, the new file is checked twice, " +
                "then it replaces the original. Drawings that are open in SOLIDWORKS Electrical are skipped, so close them first.",
                "Update Project Drawing Parameters", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.OK) return;

            SaveSettings();
            PlanReady?.Invoke(this, new RunPlan { ProjectName = ProjectName, Drawings = targets, Request = request });
        }

        // ------------------------------------------------------------------ settings / misc

        private bool _hasSavedValues;

        private void LoadSettings(UserSettings s)
        {
            _hasSavedValues = s.HasValues;
            _applySnap = s.ApplySnap; _snapOn = s.SnapOn; _snapSpacing = s.SnapSpacing;
            _applyGrid = s.ApplyGrid; _gridOn = s.GridOn; _gridSpacing = s.GridSpacing;
            _applyZone = s.ApplyZone; _zoneMinX = s.ZoneMinX; _zoneMinY = s.ZoneMinY; _zoneMaxX = s.ZoneMaxX; _zoneMaxY = s.ZoneMaxY;
            _applyOrtho = s.ApplyOrtho; _orthoOn = s.OrthoOn;
            RaiseAll();
        }

        private void SaveSettings()
        {
            new UserSettings
            {
                HasValues = true,
                ApplySnap = ApplySnap, SnapOn = SnapOn, SnapSpacing = SnapSpacing,
                ApplyGrid = ApplyGrid, GridOn = GridOn, GridSpacing = GridSpacing,
                ApplyZone = ApplyZone, ZoneMinX = ZoneMinX, ZoneMinY = ZoneMinY, ZoneMaxX = ZoneMaxX, ZoneMaxY = ZoneMaxY,
                ApplyOrtho = ApplyOrtho, OrthoOn = OrthoOn,
            }.Save();
        }

        internal static void OpenFolder(string folder)
        {
            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex) { FileLog.Write("ui", ex); }
        }

        private void OnError(Exception ex)
        {
            FileLog.Write("ui", ex);
            IsBusy = false;
            ShowBanner(Banner.Error, ex.Message);
        }
    }
}
