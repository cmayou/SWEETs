using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;
using SWEETs.SweLineStyles.AddIn.Core;
using SWEETs.SweLineStyles.AddIn.Infrastructure;
using SWEETs.SweLineStyles.AddIn.Services;
using SWEETs.SweLineStyles.AddIn.UI.Controls;

namespace SWEETs.SweLineStyles.AddIn.UI.ViewModels
{
    public enum MainTab { Styles, Shapes }
    public enum StyleFilter { All, Simple, Shape, Text, Modified }
    public enum BannerKind { None, Info, Success, Warning, Error }

    /// <summary>Hooks the window provides (dialogs stay out of the view model).</summary>
    public interface IEditorDialogs
    {
        bool Confirm(string title, string message);
        ShapeDefinition EditShape(ShapeEditorViewModel vm);
        string PickFolder(string current);
        LineStyleLibrary.BackupInfo PickBackup(List<LineStyleLibrary.BackupInfo> backups);
    }

    public sealed class MainViewModel : ObservableObject
    {
        private readonly IEditorDialogs _dialogs;
        private readonly UserSettings _settings = UserSettings.Load();
        private LibrarySnapshot _snap;

        public MainViewModel(IEditorDialogs dialogs)
        {
            _dialogs = dialogs;
            StylesView = CollectionViewSource.GetDefaultView(Styles);
            StylesView.Filter = o => Matches((LineStyleViewModel)o);

            NewStyle = new RelayCommand(DoNewStyle, () => _snap != null);
            DuplicateStyle = new RelayCommand(DoDuplicateStyle, () => SelectedStyle != null);
            DeleteStyle = new RelayCommand(DoDeleteStyle, () => SelectedStyle != null);
            NewShape = new RelayCommand(() => DoEditShape(null), () => _snap != null);
            EditShape = new RelayCommand(() => DoEditShape(SelectedShape), () => SelectedShape != null);
            DeleteShape = new RelayCommand(DoDeleteShape, () => SelectedShape != null);
            Save = new RelayCommand(DoSave, () => _snap != null && UnsavedCount > 0);
            Revert = new RelayCommand(() => { if (_dialogs.Confirm("Discard changes", $"Discard {UnsavedCount} unsaved change(s) and reload the files?")) Load(); }, () => UnsavedCount > 0);
            Reload = new RelayCommand(Load);
            Backups = new RelayCommand(DoBackups, () => _snap != null);
            ChangeFolder = new RelayCommand(DoChangeFolder);
            OpenFolder = new RelayCommand(() => { if (_snap != null) System.Diagnostics.Process.Start("explorer.exe", "\"" + _snap.Folder + "\""); });
            DismissBanner = new RelayCommand(() => Banner = BannerKind.None);
        }

        // ------------------------------------------------------------------ state

        public ObservableCollection<LineStyleViewModel> Styles { get; } = new ObservableCollection<LineStyleViewModel>();
        public ICollectionView StylesView { get; }
        public ObservableCollection<ShapeViewModel> Shapes { get; } = new ObservableCollection<ShapeViewModel>();
        public List<string> ShapeNames => Shapes.Select(s => s.Name).ToList();

        private readonly List<string> _deletedStyles = new List<string>();
        private readonly List<string> _deletedShapes = new List<string>();

        private MainTab _tab;
        public MainTab Tab { get => _tab; set { if (Set(ref _tab, value) && value == MainTab.Shapes) RefreshUsage(); } }

        private LineStyleViewModel _selectedStyle;
        public LineStyleViewModel SelectedStyle { get => _selectedStyle; set { if (Set(ref _selectedStyle, value)) RefreshValidation(); } }

        private ShapeViewModel _selectedShape;
        public ShapeViewModel SelectedShape { get => _selectedShape; set => Set(ref _selectedShape, value); }

        private string _search = "";
        public string Search { get => _search; set { if (Set(ref _search, value)) StylesView.Refresh(); } }

        private StyleFilter _filter;
        public StyleFilter Filter { get => _filter; set { if (Set(ref _filter, value)) StylesView.Refresh(); } }

        public string Folder => _snap?.Folder ?? "";
        public int StyleCount => Styles.Count;
        public int ShapeCount => Shapes.Count;

        private int _unsaved;
        public int UnsavedCount { get => _unsaved; private set { if (Set(ref _unsaved, value)) Raise(nameof(UnsavedText)); } }
        public string UnsavedText => UnsavedCount == 0 ? "No unsaved changes" : UnsavedCount == 1 ? "1 unsaved change" : $"{UnsavedCount} unsaved changes";

        private int _shapeRevision;
        /// <summary>Bound by every LinePreview so they redraw when shapes change.</summary>
        public int ShapeRevision { get => _shapeRevision; private set => Set(ref _shapeRevision, value); }

        private double _zoom = 1;
        public double Zoom { get => _zoom; set { if (Set(ref _zoom, value)) _settings.PreviewZoom = value; } }

        // Selected-style validation
        private List<string> _errors = new List<string>(), _warnings = new List<string>();
        public List<string> StyleErrors { get => _errors; private set => Set(ref _errors, value); }
        public List<string> StyleWarnings { get => _warnings; private set => Set(ref _warnings, value); }

        // Banner
        private BannerKind _banner;
        private string _bannerText = "";
        public BannerKind Banner { get => _banner; private set => Set(ref _banner, value); }
        public string BannerText { get => _bannerText; private set => Set(ref _bannerText, value); }
        private void Show(BannerKind kind, string text) { BannerText = text; Banner = kind; }

        private bool _busy;
        public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }

        public ICommand NewStyle { get; }
        public ICommand DuplicateStyle { get; }
        public ICommand DeleteStyle { get; }
        public ICommand NewShape { get; }
        public ICommand EditShape { get; }
        public ICommand DeleteShape { get; }
        public ICommand Save { get; }
        public ICommand Revert { get; }
        public ICommand Reload { get; }
        public ICommand Backups { get; }
        public ICommand ChangeFolder { get; }
        public ICommand OpenFolder { get; }
        public ICommand DismissBanner { get; }

        // ------------------------------------------------------------------ load

        public void Load()
        {
            Zoom = _settings.PreviewZoom <= 0 ? 1 : _settings.PreviewZoom;
            string keep = SelectedStyle?.Name;
            try
            {
                var folder = SupportFiles.Locate(_settings.SupportFolderOverride);
                _snap = LineStyleLibrary.Load(folder);
            }
            catch (Exception ex)
            {
                FileLog.Write("load", ex);
                _snap = null;
                Styles.Clear(); Shapes.Clear();
                Show(BannerKind.Error, "Line styles could not be loaded: " + ex.Message);
                RaiseAll();
                return;
            }

            _deletedStyles.Clear(); _deletedShapes.Clear();
            Shapes.Clear();
            foreach (var s in _snap.Shp.Shapes) Shapes.Add(new ShapeViewModel(s, false));
            Styles.Clear();
            foreach (var s in _snap.Xlin.Styles) Styles.Add(Hook(new LineStyleViewModel(s, false)));
            ShapesChanged();
            SelectedStyle = Styles.FirstOrDefault(s => s.Name == keep) ?? Styles.FirstOrDefault();
            SelectedShape = Shapes.FirstOrDefault();
            Recount();
            RaiseAll();
            FileLog.Write("load", $"{Styles.Count} line styles, {Shapes.Count} shapes from {_snap.Folder}");

            if (_snap.Notes.Count > 0) Show(BannerKind.Warning, string.Join("  ", _snap.Notes));
            else Banner = BannerKind.None;
        }

        private LineStyleViewModel Hook(LineStyleViewModel vm)
        {
            vm.DefaultShape = () => Shapes.FirstOrDefault()?.Name;
            vm.Changed += () => { Recount(); if (ReferenceEquals(vm, SelectedStyle)) RefreshValidation(); };
            return vm;
        }

        private bool Matches(LineStyleViewModel s)
        {
            if (Filter == StyleFilter.Modified && !s.IsModified) return false;
            if ((Filter == StyleFilter.Simple || Filter == StyleFilter.Shape || Filter == StyleFilter.Text) && s.Kind != Filter.ToString()) return false;
            if (string.IsNullOrWhiteSpace(Search)) return true;
            var q = Search.Trim();
            return (s.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || s.English.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                || (s.Pattern ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void Recount()
        {
            UnsavedCount = Styles.Count(s => s.IsModified) + _deletedStyles.Count + Shapes.Count(s => s.IsModified) + _deletedShapes.Count;
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Shapes added, edited or removed: refresh the preview catalog and the "used by" lists.</summary>
        private void ShapesChanged()
        {
            ShapeCatalog.Set(Shapes.Select(s => s.Model));
            ShapeRevision = ShapeCatalog.Revision;
            RefreshUsage();
            Raise(nameof(ShapeNames)); Raise(nameof(ShapeCount));
        }

        private void RefreshUsage()
        {
            foreach (var sh in Shapes)
                sh.UsedBy = Styles.Where(st => UsesShape(st.Pattern, sh.Name)).Select(st => st.Name).ToList();
        }

        private static bool UsesShape(string pattern, string shape)
        {
            try
            {
                return LinePattern.Parse(pattern).Any(e => e.Kind == PatternKind.Shape && string.Equals(e.Content, shape, StringComparison.OrdinalIgnoreCase)
                                                           && IsManagedFile(e.Source));
            }
            catch (FormatException) { return false; }
        }

        private static bool IsManagedFile(string source) =>
            string.Equals(Path.GetFileName((source ?? "").Trim()), SupportFiles.ShapeCompiled, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension((source ?? "").Trim()), Path.GetFileNameWithoutExtension(SupportFiles.ShapeCompiled), StringComparison.OrdinalIgnoreCase);

        private bool ShapeExists(string name, string source) =>
            !IsManagedFile(source) || Shapes.Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        private void RefreshValidation()
        {
            var errors = new List<string>(); var warnings = new List<string>();
            var s = SelectedStyle;
            if (s != null)
            {
                s.Validate(ShapeExists, Styles.Where(x => !ReferenceEquals(x, s)).Select(x => x.Name), errors, warnings);
                try
                {
                    foreach (var e in LinePattern.Parse(s.Pattern).Where(e => e.Kind == PatternKind.Shape && !IsManagedFile(e.Source)))
                        warnings.Add($"Shape file '{e.Source}' is not managed by this editor; its shapes are not previewed.");
                }
                catch (FormatException) { }
            }
            StyleErrors = errors;
            StyleWarnings = warnings;
        }

        // ------------------------------------------------------------------ styles

        private void DoNewStyle()
        {
            var name = UniqueStyleName("NEW_STYLE");
            var m = new LineStyle { Name = name, Pattern = ".5,-.25", Ascii = "" };
            m.Descriptions.Add(new KeyValuePair<string, string>("en", "New line style"));
            var vm = Hook(new LineStyleViewModel(m, true));
            vm.Ascii = LinePattern.ToAscii(LinePattern.Parse(m.Pattern));
            AddStyle(vm);
        }

        private void DoDuplicateStyle()
        {
            var src = SelectedStyle.ToModel();
            src.Name = UniqueStyleName(src.Name + "_COPY");
            AddStyle(Hook(new LineStyleViewModel(src, true)));
        }

        private void AddStyle(LineStyleViewModel vm)
        {
            Styles.Add(vm);
            Search = ""; Filter = StyleFilter.All;
            SelectedStyle = vm;
            Tab = MainTab.Styles;
            Recount(); RefreshUsage(); Raise(nameof(StyleCount));
        }

        private string UniqueStyleName(string baseName)
        {
            var names = new HashSet<string>(Styles.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            if (!names.Contains(baseName)) return baseName;
            for (int i = 2; ; i++) if (!names.Contains(baseName + i)) return baseName + i;
        }

        private void DoDeleteStyle()
        {
            var s = SelectedStyle;
            if (!_dialogs.Confirm("Delete line style", $"Delete line style {s.Name}?\n\nDrawings that already use it keep their copy; it is no longer offered for new elements. The file is changed only when you save.")) return;
            int i = Styles.IndexOf(s);
            Styles.Remove(s);
            if (!s.IsNew) _deletedStyles.Add(s.OriginalName);
            SelectedStyle = Styles.Count == 0 ? null : Styles[Math.Min(i, Styles.Count - 1)];
            Recount(); RefreshUsage(); Raise(nameof(StyleCount));
        }

        // ------------------------------------------------------------------ shapes

        private void DoEditShape(ShapeViewModel target)
        {
            var others = Shapes.Where(s => !ReferenceEquals(s, target)).Select(s => s.Model);
            var vm = new ShapeEditorViewModel(target?.Model, others, _settings.ShapeGridDivisor, _settings.ShapeGridExtent);
            var result = _dialogs.EditShape(vm);
            if (result == null) { ShapeRevision = ShapeCatalog.Revision; return; }
            _settings.ShapeGridDivisor = vm.Divisor;

            if (target == null)
            {
                var added = new ShapeViewModel(result, true);
                Shapes.Add(added);
                SelectedShape = added;
            }
            else
            {
                string oldName = target.Name;
                target.Update(result);
                if (!string.Equals(oldName, result.Name, StringComparison.OrdinalIgnoreCase)) RenameReferences(oldName, result.Name);
            }
            ShapesChanged();
            Recount();
            RefreshValidation();
        }

        /// <summary>A shape was renamed: point every line style that used the old name at the new one.</summary>
        private void RenameReferences(string oldName, string newName)
        {
            int n = 0;
            foreach (var st in Styles)
            {
                List<PatternElement> list;
                try { list = LinePattern.Parse(st.Pattern); } catch (FormatException) { continue; }
                bool hit = false;
                foreach (var e in list.Where(e => e.Kind == PatternKind.Shape && IsManagedFile(e.Source) && string.Equals(e.Content, oldName, StringComparison.OrdinalIgnoreCase)))
                { e.Content = newName; hit = true; }
                if (hit) { st.Pattern = LinePattern.Format(list); n++; }
            }
            if (n > 0) Show(BannerKind.Info, $"Shape {oldName} was renamed to {newName}; {n} line style(s) were updated to the new name.");
        }

        private void DoDeleteShape()
        {
            var s = SelectedShape;
            if (s.IsUsed)
            {
                Show(BannerKind.Warning, $"Shape {s.Name} is used by: {string.Join(", ", s.UsedBy)}. Change or delete those line styles first.");
                return;
            }
            if (!_dialogs.Confirm("Delete shape", $"Delete shape {s.Name} (#{s.Number}) from {SupportFiles.ShapeSource}? The file is changed only when you save.")) return;
            int i = Shapes.IndexOf(s);
            Shapes.Remove(s);
            if (!s.IsNew) _deletedShapes.Add(s.OriginalName);
            SelectedShape = Shapes.Count == 0 ? null : Shapes[Math.Min(i, Shapes.Count - 1)];
            ShapesChanged();
            Recount();
        }

        // ------------------------------------------------------------------ save, backups, folder

        private void DoSave()
        {
            // 1. Validate everything; select the first style with an error.
            var problems = new List<(LineStyleViewModel style, string error)>();
            foreach (var s in Styles)
            {
                var errors = new List<string>();
                s.Validate(ShapeExists, Styles.Where(x => !ReferenceEquals(x, s)).Select(x => x.Name), errors, new List<string>());
                problems.AddRange(errors.Select(e => (s, e)));
            }
            var shapeProblems = Shapes.SelectMany(sh => sh.Model.Validate().Select(e => $"{sh.Name}: {e}")).ToList();
            if (problems.Count > 0 || shapeProblems.Count > 0)
            {
                if (problems.Count > 0)
                {
                    Tab = MainTab.Styles; Search = ""; Filter = StyleFilter.All;
                    SelectedStyle = problems[0].style;
                }
                var first = problems.Select(p => $"{p.style.Name}: {p.error}").Concat(shapeProblems).Take(3);
                Show(BannerKind.Error, $"Not saved. Fix {problems.Count + shapeProblems.Count} problem(s) first. " + string.Join("  ·  ", first));
                return;
            }

            // 2. Build the three files.
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            try
            {
                _snap.Shp.Shapes.Clear();
                _snap.Shp.Shapes.AddRange(Shapes.Select(s => s.Model.Clone()));
                files[SupportFiles.ShapeSource] = LineStyleLibrary.EncodeShp(_snap.Shp);
                files[SupportFiles.ShapeCompiled] = ShxFile.Compile(_snap.Shp.Shapes, _snap.ShxSignature);
                _snap.Xlin.Styles.Clear();
                _snap.Xlin.Styles.AddRange(Styles.Select(s => s.ToModel()));
                files[SupportFiles.LineStyles] = _snap.Xlin.Save();
            }
            catch (Exception ex)
            {
                FileLog.Write("save", ex);
                Show(BannerKind.Error, "Not saved: " + ex.Message);
                return;
            }

            // 3. Write (backup, elevation, verification).
            try
            {
                IsBusy = true;
                var summary = $"{UnsavedCount} change(s): " + string.Join(", ", Styles.Where(s => s.IsModified).Select(s => s.Name).Concat(Shapes.Where(s => s.IsModified).Select(s => s.Name)).Take(10));
                var result = LineStyleLibrary.Save(_snap.Folder, files, summary);
                if (result.Cancelled) { Show(BannerKind.Warning, "Not saved: administrator permission was declined. Your changes are still here."); return; }
                Load();
                Show(BannerKind.Success, result.Written.Count == 0
                    ? "Nothing to write: the files already match."
                    : $"Saved {string.Join(", ", result.Written)}{(result.Elevated ? " (with administrator permission)" : "")}. Backup: {result.BackupFolder}. Restart SOLIDWORKS Electrical if the changes do not appear.");
            }
            catch (Exception ex)
            {
                FileLog.Write("save", ex);
                Show(BannerKind.Error, "Save failed: " + ex.Message);
            }
            finally { IsBusy = false; }
        }

        private void DoBackups()
        {
            var list = LineStyleLibrary.ListBackups();
            var pick = _dialogs.PickBackup(list);
            if (pick == null) return;
            if (UnsavedCount > 0 && !_dialogs.Confirm("Restore backup", $"Restoring discards {UnsavedCount} unsaved change(s). Continue?")) return;
            try
            {
                IsBusy = true;
                var r = LineStyleLibrary.Restore(_snap.Folder, pick);
                if (r.Cancelled) { Show(BannerKind.Warning, "Not restored: administrator permission was declined."); return; }
                Load();
                Show(BannerKind.Success, $"Restored the backup of {pick.Display}. The files it replaced were backed up to {r.BackupFolder}.");
            }
            catch (Exception ex)
            {
                FileLog.Write("restore", ex);
                Show(BannerKind.Error, "Restore failed: " + ex.Message);
            }
            finally { IsBusy = false; }
        }

        private void DoChangeFolder()
        {
            var folder = _dialogs.PickFolder(Folder);
            if (folder == null) return;
            if (!File.Exists(Path.Combine(folder, SupportFiles.LineStyles)))
            {
                Show(BannerKind.Error, $"{SupportFiles.LineStyles} is not in {folder}.");
                return;
            }
            if (UnsavedCount > 0 && !_dialogs.Confirm("Change folder", $"Switching folders discards {UnsavedCount} unsaved change(s). Continue?")) return;
            _settings.SupportFolderOverride = folder;
            _settings.Save();
            Load();
        }

        public void SaveSettings() => _settings.Save();
    }
}
