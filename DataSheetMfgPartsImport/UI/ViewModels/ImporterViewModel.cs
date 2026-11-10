using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using DataSheetMfgPartsImport.Services;
using DataSheetMfgPartsImport.Settings;
using DataSheetMfgPartsImport.Extraction;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.UI.ViewModels;

public enum ImportStage { Upload = 0, Extract = 1, Review = 2, Build = 3 }

public sealed class StepViewModel : ObservableObject
{
    private string _state = "pending"; // pending | active | done | error
    private string _detail;

    public StepViewModel(int number, string title) { Number = number; Title = title; }

    public int Number { get; }
    public string Title { get; }

    public string State { get => _state; set => Set(ref _state, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
}

public sealed class ActivityItem
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Kind { get; set; } // info | success | warning | error
    public string Text { get; set; }
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed class LoadedFile
{
    public string Path { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
    public int Pages { get; set; }
    public int Parts { get; set; }
    public string SizeText { get; set; }
    public bool HasText { get; set; }
    public string Summary => HasText ? $"{Pages} page(s) · {Parts} part(s) · {SizeText}" : $"{Pages} page(s) · no text layer · {SizeText}";
}

/// <summary>
/// State for the importer dialog: Upload → Extract → Review → Build.
/// PDF parsing runs on a worker thread; every catalog (COM) call stays on the UI thread.
/// </summary>
public sealed class ImporterViewModel : ObservableObject
{
    private readonly ICatalogService _catalog;
    private readonly Dispatcher _dispatcher;
    private AppSettings _settings;
    private IReadOnlyList<ClassCandidate> _classes = Array.Empty<ClassCandidate>();
    private IReadOnlyList<CircuitTypeInfo> _circuitTypes = Array.Empty<CircuitTypeInfo>();
    private CancellationTokenSource _cts;
    private bool _initialized;

    private bool _isBusy;
    private string _statusText = "Drop a data sheet to start.";
    private double _progress;
    private bool _isProgressIndeterminate;
    private string _resultBanner;
    private string _resultKind;

    public ImporterViewModel(ICatalogService catalog, Dispatcher dispatcher)
    {
        _catalog = catalog;
        _dispatcher = dispatcher;
        _settings = AppSettings.Load();

        Steps = new ObservableCollection<StepViewModel>
        {
            new(1, "Upload datasheet"),
            new(2, "Extract data"),
            new(3, "Review parts"),
            new(4, "Save to Library"),
        };
        Steps[0].State = "active";

        BuildCommand = new AsyncRelayCommand(BuildAsync, () => CanBuild);
        BrowseCommand = new AsyncRelayCommand(BrowseAsync, () => !IsBusy);
        SelectAllCommand = new RelayCommand(() => SetAll(true), () => Parts.Count > 0 && !IsBusy);
        SelectNoneCommand = new RelayCommand(() => SetAll(false), () => Parts.Count > 0 && !IsBusy);
        ClearCommand = new RelayCommand(Clear, () => Parts.Count > 0 && !IsBusy);
        ApplyClassToAllCommand = new RelayCommand(ApplyClassToAll,
            () => BulkClassCandidate != null && Parts.Any(p => !p.ClassConfirmed) && !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        DismissBannerCommand = new RelayCommand(() => ResultBanner = null);
    }

    // ---------------- bindable state ----------------

    public string ConnectionLabel => _catalog.ConnectionLabel;
    public bool IsLive => _catalog.IsLive;

    public ObservableCollection<StepViewModel> Steps { get; }
    public ObservableCollection<PartCardViewModel> Parts { get; } = new();
    public ObservableCollection<LoadedFile> Files { get; } = new();
    public ObservableCollection<ActivityItem> Activity { get; } = new();

    public bool HasParts => Parts.Count > 0;
    public bool HasFiles => Files.Count > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (Set(ref _isBusy, value)) { OnPropertyChanged(nameof(CanBuild)); CommandManager.InvalidateRequerySuggested(); } }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool IsProgressIndeterminate { get => _isProgressIndeterminate; private set => Set(ref _isProgressIndeterminate, value); }

    public string ResultBanner
    {
        get => _resultBanner;
        private set { if (Set(ref _resultBanner, value)) OnPropertyChanged(nameof(HasResultBanner)); }
    }
    public string ResultKind { get => _resultKind; private set => Set(ref _resultKind, value); }
    public bool HasResultBanner => !string.IsNullOrEmpty(_resultBanner);

    // Summary chips
    public int SelectedCount => Parts.Count(p => p.IsSelected);
    public int ToWriteCount => Parts.Count(p => p.WillWrite);
    public int ConflictCount => Parts.Count(p => p.IsSelected && p.IsDuplicate);
    public int AttentionCount => Parts.Count(p => p.IsSelected && !p.IsValid);
    public int AverageCoverage => Parts.Count == 0 ? 0 : (int)Math.Round(Parts.Average(p => p.CoveragePercent));

    public bool CanBuild => !IsBusy && Parts.Any(p => p.IsSelected) && Parts.Where(p => p.IsSelected).All(p => p.IsValid);

    public string BuildButtonText => ToWriteCount switch
    {
        0 when SelectedCount > 0 => "Nothing to save",
        0 => "Save to Library",
        1 => "Save 1 part to Library",
        var n => $"Save {n} parts to Library",
    };

    public string BuildHint
    {
        get
        {
            if (Parts.Count == 0) return "Add a data sheet to begin.";
            if (SelectedCount == 0) return "Select at least one part.";
            if (AttentionCount > 0) return $"{AttentionCount} selected part(s) need attention.";
            if (ConflictCount > 0) return $"{ConflictCount} part(s) already exist — choose Skip or Overwrite on each card.";
            return "Ready to save.";
        }
    }

    // ---------------- commands ----------------

    public ICommand BuildCommand { get; }
    public ICommand BrowseCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand SelectNoneCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ApplyClassToAllCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DismissBannerCommand { get; }

    // ---------------- lifecycle ----------------

    /// <summary>Loads classes and circuit types once (UI thread, COM).</summary>
    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            _classes = _catalog.GetComponentClasses();
            _circuitTypes = _catalog.GetCircuitTypes();
            AddActivity("info", $"{ConnectionLabel}: {_classes.Count} classes, {_circuitTypes.Count} circuit types available.");
            if (_circuitTypes.Count == 0) AddActivity("warning", "No circuit types were returned. Type the circuit type key code in each circuit row, and send the log to support.");
            if (_classes.Count == 0) AddActivity("warning", "No component classes were returned. Check the log for details.");
        }
        catch (Exception ex)
        {
            Log.Error("Initialize failed", ex);
            AddActivity("error", $"Could not read the catalog: {ex.Message}");
        }
    }

    public void ReloadSettings() => _settings = AppSettings.Load();

    public async Task LoadFilesAsync(IEnumerable<string> paths)
    {
        var pdfs = paths.Where(IsSupportedFile).Distinct().ToList();
        if (pdfs.Count == 0)
        {
            AddActivity("warning", "Add a PDF data sheet, or a photo or screenshot of one (photos need the local AI model).");
            return;
        }
        if (IsBusy) return;
        Initialize(); // classes are needed for suggestions

        IsBusy = true;
        ResultBanner = null;
        _cts = new CancellationTokenSource();
        SetStep(ImportStage.Upload, "done", $"{pdfs.Count} file(s)");
        SetStep(ImportStage.Extract, "active", null);
        IsProgressIndeterminate = true;

        var newParts = 0;
        try
        {
            foreach (var path in pdfs)
            {
                if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    AddActivity("info", $"{Path.GetFileName(path)} is already loaded.");
                    continue;
                }

                StatusText = $"Reading {Path.GetFileName(path)}…";
                var ct = _cts.Token;
                var result = await ExtractionService.ExtractFileAsync(path, _settings, ct);

                foreach (var m in result.Messages) AddActivity(result.HasTextLayer ? "info" : "warning", $"{Path.GetFileName(path)}: {m}");

                Files.Add(new LoadedFile
                {
                    Path = path,
                    Pages = result.PageCount,
                    Parts = result.Parts.Count,
                    HasText = result.HasTextLayer,
                    SizeText = FormatSize(new FileInfo(path).Length),
                });

                // The extractor decides which cards start selected (main family; accessories unselected).
                foreach (var part in result.Parts)
                {
                    var card = new PartCardViewModel(part, _classes, _circuitTypes, _settings.CircuitTypeKeyCodes, part.Preselect, OnCardChanged);
                    card.IsExpanded = part.Preselect && Parts.Count(c => c.IsExpanded) < 2;   // keep large catalogs scannable
                    card.Existing = _catalog.FindExisting(card.Manufacturer, card.Reference); // COM: UI thread
                    card.Refresh();
                    Parts.Add(card);
                    newParts++;
                }
                AddActivity("success", $"{Path.GetFileName(path)}: {result.Parts.Count} part(s) extracted, {result.Parts.Count(p => p.Preselect)} selected.");
            }

            SetStep(ImportStage.Extract, "done", $"{newParts} part(s)");
            SetStep(ImportStage.Review, "active", "Check values, class and circuits");
            StatusText = newParts > 0 ? "Review the extracted parts, then save them to the library." : StatusText;
        }
        catch (OperationCanceledException)
        {
            SetStep(ImportStage.Extract, "error", "Cancelled");
            StatusText = "Extraction cancelled.";
        }
        catch (Exception ex)
        {
            Log.Error("Extraction failed", ex);
            SetStep(ImportStage.Extract, "error", "Failed");
            StatusText = "Extraction failed.";
            AddActivity("error", $"Could not read the data sheet: {ex.Message}");
        }
        finally
        {
            IsProgressIndeterminate = false;
            IsBusy = false;
            RaiseSummary();
        }
    }

    private async Task BuildAsync()
    {
        var targets = Parts.Where(p => p.IsSelected).ToList();
        if (targets.Count == 0) return;

        IsBusy = true;
        ResultBanner = null;
        SetStep(ImportStage.Review, "done", null);
        SetStep(ImportStage.Build, "active", null);
        Progress = 0;
        _cts = new CancellationTokenSource();

        int created = 0, updated = 0, skipped = 0, failed = 0, warnings = 0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                if (_cts.IsCancellationRequested) break;
                var card = targets[i];
                card.State = CardState.Building;
                StatusText = $"Saving {card.Title} ({i + 1}/{targets.Count})…";
                // Let the UI repaint between COM calls.
                await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

                var outcome = _catalog.Build(card.ToDraft()); // COM: UI thread
                card.ResultMessage = outcome.Message;
                card.ResultDetails = outcome.Warnings.Count == 0 ? null : string.Join("\n", outcome.Warnings);
                warnings += outcome.Warnings.Count;
                switch (outcome.Status)
                {
                    case BuildStatus.Created: created++; card.State = CardState.Created; card.Existing = new ExistingPartInfo { Reference = card.Reference, Manufacturer = card.Manufacturer }; break;
                    case BuildStatus.Updated: updated++; card.State = CardState.Updated; break;
                    case BuildStatus.Skipped: skipped++; card.State = CardState.Skipped; break;
                    default: failed++; card.State = CardState.Failed; break;
                }
                AddActivity(outcome.Status == BuildStatus.Failed ? "error" : outcome.Warnings.Count > 0 ? "warning" : "success",
                    $"{card.Title}: {outcome.Message}");
                Progress = 100.0 * (i + 1) / targets.Count;
            }

            var parts = new List<string>();
            if (created > 0) parts.Add($"{created} created");
            if (updated > 0) parts.Add($"{updated} updated");
            if (skipped > 0) parts.Add($"{skipped} skipped");
            if (failed > 0) parts.Add($"{failed} failed");
            ResultBanner = $"Save finished: {string.Join(", ", parts)}" + (warnings > 0 ? $" · {warnings} warning(s), see cards" : "");
            ResultKind = failed > 0 ? "error" : warnings > 0 ? "warning" : "success";
            SetStep(ImportStage.Build, failed > 0 ? "error" : "done", string.Join(", ", parts));
            StatusText = "Done. The parts are in the manufacturer parts manager.";
        }
        catch (Exception ex)
        {
            Log.Error("Build failed", ex);
            ResultBanner = $"Save stopped: {ex.Message}";
            ResultKind = "error";
            SetStep(ImportStage.Build, "error", "Stopped");
        }
        finally
        {
            IsBusy = false;
            RaiseSummary();
        }
    }

    // ---------------- helpers ----------------

    private Task BrowseAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select manufacturer data sheet(s)",
            Filter = "Data sheets (PDF, images)|*.pdf;*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.gif;*.webp|All files|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        return dlg.ShowDialog() == true ? LoadFilesAsync(dlg.FileNames) : Task.CompletedTask;
    }

    // ---- bulk class assignment ----

    private ClassCandidate _bulkClassCandidate;

    /// <summary>
    /// Class chosen in the 'Set class for all' bar. Null until the user picks one.
    /// </summary>
    public ClassCandidate BulkClassCandidate
    {
        get => _bulkClassCandidate;
        set
        {
            if (!Set(ref _bulkClassCandidate, value)) return;
            OnPropertyChanged(nameof(ApplyClassLabel));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>All archive classes, taken from the first card (they all share the same list).</summary>
    public IReadOnlyList<ClassCandidate> AllClasses =>
        Parts.FirstOrDefault()?.Classes ?? System.Array.Empty<ClassCandidate>();

    public string ApplyClassLabel
    {
        get
        {
            if (BulkClassCandidate == null) return "Apply class to unconfirmed parts";
            int n = Parts.Count(p => !p.ClassConfirmed);
            return n == 0
                ? "All parts already have a confirmed class"
                : "Apply to " + n + " unconfirmed part" + (n == 1 ? "" : "s");
        }
    }

    private void ApplyClassToAll()
    {
        if (BulkClassCandidate == null) return;
        int applied = 0;
        foreach (var card in Parts.Where(p => !p.ClassConfirmed))
        {
            card.SelectedClass = BulkClassCandidate;
            card.ClassConfirmed = true;
            applied++;
        }
        if (applied > 0)
        {
            AddActivity("info", "Class '" + BulkClassCandidate.Name + "' applied to " + applied
                + (applied == 1 ? " part." : " parts."));
            OnPropertyChanged(nameof(ApplyClassLabel));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private void SetAll(bool selected)
    {
        foreach (var p in Parts.Where(p => !p.IsFinished)) p.IsSelected = selected;
    }

    private void Clear()
    {
        Parts.Clear();
        Files.Clear();
        ResultBanner = null;
        foreach (var s in Steps) { s.State = "pending"; s.Detail = null; }
        Steps[0].State = "active";
        Progress = 0;
        StatusText = "Drop a data sheet to start.";
        RaiseSummary();
    }

    private void OnCardChanged() => RaiseSummary();

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(HasParts));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ToWriteCount));
        OnPropertyChanged(nameof(ConflictCount));
        OnPropertyChanged(nameof(AttentionCount));
        OnPropertyChanged(nameof(AverageCoverage));
        OnPropertyChanged(nameof(AllClasses));
        OnPropertyChanged(nameof(ApplyClassLabel));
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(BuildButtonText));
        OnPropertyChanged(nameof(BuildHint));
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetStep(ImportStage stage, string state, string detail)
    {
        var s = Steps[(int)stage];
        s.State = state;
        if (detail != null) s.Detail = detail;
    }

    private void AddActivity(string kind, string text)
    {
        Activity.Insert(0, new ActivityItem { Kind = kind, Text = text });
        while (Activity.Count > 60) Activity.RemoveAt(Activity.Count - 1);
        if (kind == "error") Log.Error(text); else Log.Info(text);
    }

    internal static readonly string[] SupportedExtensions = { ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff", ".gif", ".webp" };

    internal static bool IsSupportedFile(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path ?? "").ToLowerInvariant());

    private static string FormatSize(long bytes) =>
        bytes >= 1 << 20 ? $"{bytes / 1048576.0:0.0} MB" : $"{Math.Max(1, bytes / 1024)} KB";
}
