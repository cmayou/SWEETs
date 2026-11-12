using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SWEETs.SwePdfSchematic.AddIn.Interop;
using SWEETs.SwePdfSchematic.AddIn.UI;
using SWEETs.SwePdfSchematic.Core.Build;
using SWEETs.SwePdfSchematic.Core.Infrastructure;
using SWEETs.SwePdfSchematic.Core.Mapping;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Pdf;
using SWEETs.SwePdfSchematic.Core.Recognition;

namespace SWEETs.SwePdfSchematic.AddIn.UI.ViewModels
{
    public enum Step { Source = 1, Analyze = 2, Review = 3, Build = 4 }
    public enum ReviewFilter { All, NeedsAttention, Confirmed, Ignored }

    public sealed class Kpi
    {
        public string Label { get; set; }
        public string Value { get; set; }
        public string Hint { get; set; }
    }

    public sealed class Bar
    {
        public string Label { get; set; }
        public int Count { get; set; }
        public double Fraction { get; set; }
        public Brush Fill { get; set; }
        public string Text => $"{Count}";
    }

    public sealed class KeyValue
    {
        public string Key { get; set; }
        public string Value { get; set; }
    }

    public sealed class PageItem
    {
        public PageAnalysis Analysis { get; set; }
        public string Label => $"Page {Analysis.PageNumber}";
        public string Badge => Analysis.IsRasterOnly ? "Raster" : Analysis.Standard.ToString();
    }

    public sealed class LogRow
    {
        public BuildLogEntry Entry { get; set; }
        public string Time => Entry.Time.ToString("HH:mm:ss");
        public string Message => Entry.Message;
        public Brush Brush => Entry.Level == LogLevel.Error ? Palette.Danger : Entry.Level == LogLevel.Warning ? Palette.Warning : Entry.Level == LogLevel.Success ? Palette.Success : Palette.Ink;
    }

    /// <summary>State machine for the four-step flow: Source → Analyze → Review → Build.</summary>
    public sealed class MainViewModel : ObservableObject
    {
        private readonly object _sweApp;
        private readonly AppSettings _settings;
        private readonly MappingStore _mappings;
        private readonly RuleLibrary _rules;
        private SchematicRecognizer _recognizer;
        private CancellationTokenSource _cts;

        public MainViewModel(object sweApplication)
        {
            _sweApp = sweApplication;
            _settings = AppSettings.Load();
            _mappings = new MappingStore(_settings.MappingStorePath);
            _rules = RuleLibrary.Load(_settings.RulesOverridePath);
            _recognizer = new SchematicRecognizer(_settings.Recognition, _rules, _mappings);
            Catalog = new SymbolCatalog(_mappings);

            OpenCommand = new AsyncCommand(OpenAsync, () => !IsBusy, Fail);
            GoToStepCommand = new RelayCommand(p => { if (p is string str && Enum.TryParse(str, out Step s2) && CanEnter(s2)) GoTo(s2); });
            ConfirmCommand = new RelayCommand(p => Confirm(p as SymbolCardViewModel ?? SelectedSymbol), p => (p as SymbolCardViewModel ?? SelectedSymbol)?.IsMapped == true);
            IgnoreCommand = new RelayCommand(p => Ignore(p as SymbolCardViewModel ?? SelectedSymbol), p => (p as SymbolCardViewModel ?? SelectedSymbol) != null);
            MapCommand = new RelayCommand(p => Map(p as SymbolCardViewModel ?? SelectedSymbol), p => (p as SymbolCardViewModel ?? SelectedSymbol) != null);
            ConfirmHighConfidenceCommand = new RelayCommand(ConfirmHighConfidence, () => ActiveCards.Any(c => c.State == ReviewState.NeedsReview && c.Confidence >= 0.7));
            IncludeSchematicsCommand = new RelayCommand(() => SetIncludeAll(true), () => HasDocument && !IsBusy);
            ExcludeAllPagesCommand = new RelayCommand(() => SetIncludeAll(false), () => HasDocument && !IsBusy);
            ContinueToBuildCommand = new RelayCommand(() => GoTo(Step.Build), () => Document?.ReadyToBuild == true);
            CombineCommand = new RelayCommand(p => Combine(p as SymbolCardViewModel), p => CombineTargets(p as SymbolCardViewModel).Count >= 2);
            SplitCommand = new RelayCommand(p => Split(p as SymbolCardViewModel ?? SelectedSymbol), p => (p as SymbolCardViewModel ?? SelectedSymbol)?.Model.IsCombined == true);
            SimulateCommand = new AsyncCommand(() => BuildAsync(dryRun: true), () => CanBuild, Fail);
            BuildCommand = new AsyncCommand(() => BuildAsync(dryRun: false), () => CanBuild && _sweApp != null, Fail);
            CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
            BrowseTemplateCommand = new RelayCommand(BrowseTemplate);
            RefreshTemplatesCommand = new RelayCommand(LoadTemplates, () => !IsBusy);
            OpenLogFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(FileLog.Folder); Process.Start("explorer.exe", FileLog.Folder); });
            PartsCommand = new RelayCommand(p => EditParts(p as SymbolCardViewModel ?? SelectedSymbol), p => (p as SymbolCardViewModel ?? SelectedSymbol)?.HasTag == true && !IsBusy);
            EditItemCommand = new RelayCommand(p => EditItem(p as BomItemViewModel), p => p is BomItemViewModel && !IsBusy);
            CheckCatalogCommand = new RelayCommand(() => { CheckCatalog(); AttachBom(); RefreshBomItems(); }, () => _sweApp != null && Document != null && !IsBusy);

            CardsView = CollectionViewSource.GetDefaultView(Cards);
            CardsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SymbolCardViewModel.GroupKey)));
            CardsView.SortDescriptions.Add(new SortDescription(nameof(SymbolCardViewModel.GroupKey), ListSortDirection.Ascending));
            CardsView.SortDescriptions.Add(new SortDescription(nameof(SymbolCardViewModel.Page), ListSortDirection.Ascending));
            CardsView.Filter = o => FilterCard((SymbolCardViewModel)o);

            CheckEnvironment();
            Status = _sweApp == null ? "Running without SOLIDWORKS Electrical — simulate only." : "Open a vector PDF schematic to begin.";
        }

        // ================= shared state
        public SymbolCatalog Catalog { get; }
        public ImportDocument Document { get => _document; private set { Set(ref _document, value); Raise(nameof(HasDocument)); } }
        private ImportDocument _document;
        public bool HasDocument => Document != null;

        /// <summary>Bound two-way to the step rail; user navigation is validated, internal transitions use <see cref="GoTo"/>.</summary>
        public Step CurrentStep
        {
            get => _step;
            set
            {
                if (value == _step) return;
                if (!CanEnter(value)) { Raise(); return; } // revert the rail selection
                GoTo(value);
            }
        }
        private Step _step = Step.Source;

        private void GoTo(Step s)
        {
            if (Set(ref _step, s, nameof(CurrentStep))) { Raise(nameof(StepTitle)); Raise(nameof(StepHint)); }
            if (s == Step.Build && !_templatesLoaded) LoadTemplates();
        }

        public string StepTitle => CurrentStep == Step.Source ? "Source PDF" : CurrentStep == Step.Analyze ? "Analysis" : CurrentStep == Step.Review ? "Review symbols" : "Build project";
        public string StepHint =>
            CurrentStep == Step.Source ? "Vector PDFs exported from a CAD tool give the best results. Everything is read locally." :
            CurrentStep == Step.Analyze ? "Wires, symbols, tags, cross-references and title-block data detected in the PDF." :
            CurrentStep == Step.Review ? "Confirm each symbol's SOLIDWORKS Electrical library symbol. Choices are remembered for next time." :
            "Create a new project from a template and build every page.";

        public bool IsBusy { get => _busy; private set => Set(ref _busy, value); }
        private bool _busy;
        public string Status { get => _status; set => Set(ref _status, value); }
        private string _status;

        public bool CanEnter(Step s) =>
            !IsBusy && (s == Step.Source || (s == Step.Analyze && HasDocument) || (s == Step.Review && HasDocument) || (s == Step.Build && Document?.ReadyToBuild == true));

        public ICommand OpenCommand { get; }
        public ICommand GoToStepCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand OpenLogFolderCommand { get; }

        // ================= Source
        public string PdfPath { get => _pdfPath; private set { Set(ref _pdfPath, value); Raise(nameof(FileName)); Raise(nameof(HasFile)); } }
        private string _pdfPath;
        public string FileName => PdfPath == null ? null : Path.GetFileName(PdfPath);
        public bool HasFile => PdfPath != null;
        public string FileFacts { get => _fileFacts; private set => Set(ref _fileFacts, value); }
        private string _fileFacts;

        private async Task OpenAsync()
        {
            var dlg = new OpenFileDialog
            {
                Title = "Open schematic PDF",
                Filter = "PDF schematics (*.pdf)|*.pdf",
                InitialDirectory = Directory.Exists(_settings.LastPdfFolder ?? "") ? _settings.LastPdfFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            };
            if (dlg.ShowDialog() != true) return;
            await LoadAsync(dlg.FileName);
        }

        public async Task LoadAsync(string path)
        {
            _settings.LastPdfFolder = Path.GetDirectoryName(path);
            TrySaveSettings();
            PdfPath = path;
            var fi = new FileInfo(path);
            int pages = await Task.Run(() => VectorPdfReader.PageCount(path));
            FileFacts = $"{pages} page{(pages == 1 ? "" : "s")} · {fi.Length / 1024.0:0} KB · modified {fi.LastWriteTime:d}";
            await AnalyzeAsync();
        }

        // ================= Analyze
        public double AnalyzeFraction { get => _progress; private set => Set(ref _progress, value); }
        private double _progress;
        public string ProgressStage { get => _progressStage; private set => Set(ref _progressStage, value); }
        private string _progressStage;
        public ObservableCollection<Kpi> Kpis { get; } = new ObservableCollection<Kpi>();
        public ObservableCollection<Bar> StateBars { get; } = new ObservableCollection<Bar>();
        public ObservableCollection<KeyValue> Documentation { get; } = new ObservableCollection<KeyValue>();
        public ObservableCollection<string> Warnings { get; } = new ObservableCollection<string>();
        public bool HasWarnings => Warnings.Count > 0;

        /// <summary>Pages the reviewer asked to analyse as schematics although they looked like reports or layouts.</summary>
        private readonly HashSet<int> _forced = new HashSet<int>();
        /// <summary>Schematic pages the reviewer left out; kept across a re-analysis of the same PDF.</summary>
        private readonly HashSet<int> _excluded = new HashSet<int>();

        private async Task AnalyzeAsync(bool keepPageChoices = false)
        {
            if (!keepPageChoices) { _forced.Clear(); _excluded.Clear(); }
            var editedParts = keepPageChoices && Document != null ? Document.Bom.Where(b => b.Edited).ToList() : new List<BomTarget>();
            IsBusy = true;
            GoTo(Step.Analyze);
            _templatesLoaded = false; // re-pick the template for the new PDF's standard on the Build step
            Document = null; Cards.Clear(); Kpis.Clear(); StateBars.Clear(); Documentation.Clear(); Warnings.Clear();
            Status = "Analyzing…";
            _cts = new CancellationTokenSource();
            try
            {
                var progress = new Progress<RecognitionProgress>(p => { AnalyzeFraction = p.Fraction; ProgressStage = $"Page {p.Page} of {p.PageCount} · {p.Stage}"; });
                var path = PdfPath;
                var forced = new HashSet<int>(_forced);
                var doc = await Task.Run(() => _recognizer.Recognize(path, progress, _cts.Token, forced));
                foreach (var p in doc.Pages.Where(p => _excluded.Contains(p.PageNumber))) p.Include = false;
                CrossReferenceResolver.BuildComponents(doc);
                foreach (var b in editedParts) { doc.Bom.RemoveAll(x => x.Key == b.Key); doc.Bom.Add(b); }
                Document = doc;
                foreach (var s in doc.AllSymbols) Cards.Add(new SymbolCardViewModel(s));
                CheckCatalog();
                AttachBom();
                RefreshBomItems();
                UpdateCombineHints();
                PageRows.Clear();
                foreach (var p in doc.Pages) PageRows.Add(new PageRowViewModel(p, OnPageIncludeChanged));
                RefreshIncludedPages();
                BuildSummary();
                ProjectName = _settings.Build.ProjectName ?? doc.DocumentInfo.Get("DWGNO", "DRAWINGNO", "PROJECT") ?? Path.GetFileNameWithoutExtension(doc.SourceFileName);
                Status = doc.ReadyToBuild ? "All symbols mapped — ready to build." : $"{ActiveCards.Count(c => c.State != ReviewState.Confirmed && c.State != ReviewState.Ignored)} symbols need review.";
                FileLog.Write("analyze", $"{doc.SourceFileName}: {doc.Pages.Count} pages, {Cards.Count} symbols, sha256 {doc.SourceSha256}");
            }
            catch (OperationCanceledException) { Status = "Analysis cancelled."; GoTo(Step.Source); }
            finally { IsBusy = false; AnalyzeFraction = 1; }
        }

        private void BuildSummary()
        {
            var d = Document;
            Kpis.Clear();
            Kpis.Add(new Kpi { Label = "Pages", Value = d.Pages.Count.ToString(), Hint = d.Pages.Any(p => p.IsRasterOnly) ? $"{d.Pages.Count(p => p.IsRasterOnly)} raster (skipped)" : "all vector" });
            Kpis.Add(new Kpi { Label = "Symbols", Value = Cards.Count.ToString(), Hint = $"{d.Pages.Sum(p => p.Symbols.Count(s => s.Tag != null))} tagged" });
            Kpis.Add(new Kpi { Label = "Wire nets", Value = d.Pages.Sum(p => p.Nets.Count).ToString(), Hint = $"{d.Pages.Sum(p => p.Nets.Count(n => n.WireNumber != null))} numbered" });
            Kpis.Add(new Kpi { Label = "Components", Value = d.Components.Count.ToString(), Hint = $"{d.Components.Count(c => c.Pages.Count() > 1)} span pages" });
            Kpis.Add(new Kpi { Label = "Parts lists", Value = d.Bom.Count(b => b.Kind != BomItemKind.Wire && b.Parts.Count > 0).ToString(), Hint = $"{d.BomLines.Count} table rows read" });
            Kpis.Add(new Kpi { Label = "Off-page links", Value = d.Pages.Sum(p => p.OffPageLinks.Count(l => l.TargetPage.HasValue)).ToString(), Hint = $"{d.Pages.Sum(p => p.OffPageLinks.Count(l => l.PairedLinkId != null)) / 2} pairs matched" });
            RefreshBars();

            Documentation.Clear();
            foreach (var kv in d.DocumentInfo.Fields) Documentation.Add(new KeyValue { Key = kv.Key, Value = kv.Value });
            Documentation.Add(new KeyValue { Key = "Source file", Value = d.SourceFileName });
            Documentation.Add(new KeyValue { Key = "SHA-256", Value = d.SourceSha256.Substring(0, 16) + "…" });
            Raise(nameof(HasDocumentation));

            Warnings.Clear();
            foreach (var w in d.Warnings) Warnings.Add(w);
            foreach (var p in d.Pages) foreach (var w in p.Warnings) Warnings.Add($"Page {p.PageNumber}: {w}");
            Raise(nameof(HasWarnings));
        }

        public bool HasDocumentation => Documentation.Count > 0;

        // ================= Pages to import
        public ObservableCollection<PageRowViewModel> PageRows { get; } = new ObservableCollection<PageRowViewModel>();
        public ICommand IncludeSchematicsCommand { get; }
        public ICommand ExcludeAllPagesCommand { get; }
        public string PageSelectionSummary => Document == null ? "" :
            $"{Document.IncludedPages.Count()} of {Document.Pages.Count} pages will be built · {Document.Pages.Count(p => p.Kind != PageKind.Schematic)} report, list or layout pages are skipped by default — SOLIDWORKS Electrical generates its own reports.";

        private bool IsIncluded(int page) => Document?.Pages.FirstOrDefault(p => p.PageNumber == page)?.Include == true
                                            && Document.Pages.First(p => p.PageNumber == page).Kind == PageKind.Schematic;
        /// <summary>Cards of the pages being imported; review counts and readiness use only these.</summary>
        private IEnumerable<SymbolCardViewModel> ActiveCards => Cards.Where(c => IsIncluded(c.Page));

        private async void OnPageIncludeChanged(PageRowViewModel row, bool include)
        {
            var page = row.Model;
            if (include && page.Kind != PageKind.Schematic)
            {
                var answer = MessageBox.Show(OwnerWindow,
                    $"Page {page.PageNumber} was read as a {page.Kind.ToString().ToLowerInvariant()} ({page.ReportTitle ?? "no title"}).\n\n" +
                    "Analyze it as a schematic and import it? The PDF is analysed again; mappings already learned are kept, " +
                    "unconfirmed review decisions on other pages are reset.",
                    "Import page as schematic", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) { row.SetSilently(false); return; }
                _forced.Add(page.PageNumber);
                _excluded.Remove(page.PageNumber);
                try { await AnalyzeAsync(keepPageChoices: true); }
                catch (Exception ex) { Fail(ex); }
                return;
            }
            page.Include = include;
            if (include) _excluded.Remove(page.PageNumber); else _excluded.Add(page.PageNumber);
            RefreshIncludedPages();
        }

        /// <summary>Schematics only (true) or nothing (false); reports are never switched on in bulk.</summary>
        private void SetIncludeAll(bool include)
        {
            foreach (var row in PageRows.Where(r => r.Model.Kind == PageKind.Schematic && !r.Model.IsRasterOnly))
            {
                row.Model.Include = include;
                if (include) _excluded.Remove(row.Number); else _excluded.Add(row.Number);
                row.SetSilently(include);
            }
            RefreshIncludedPages();
        }

        /// <summary>Re-derives everything that depends on which pages are imported.</summary>
        private void RefreshIncludedPages()
        {
            var doc = Document;
            if (doc == null) return;
            CrossReferenceResolver.BuildComponents(doc);
            Pages = new ObservableCollection<PageItem>(doc.IncludedPages.Select(p => new PageItem { Analysis = p }));
            Raise(nameof(Pages));
            if (SelectedPage == null || !Pages.Any(p => p.Analysis == SelectedPage.Analysis)) SelectedPage = Pages.FirstOrDefault();
            foreach (var r in PageRows) r.Refresh();
            CardsView.Refresh();
            RefreshBars();
            Raise(nameof(PageSelectionSummary));
            Raise(nameof(PageCards));
            CommandManager.InvalidateRequerySuggested();
        }

        private void RefreshBars()
        {
            var active = ActiveCards.ToList();
            int total = Math.Max(1, active.Count);
            StateBars.Clear();
            foreach (var (label, state) in new[] { ("Confirmed", ReviewState.Confirmed), ("Needs review", ReviewState.NeedsReview), ("Unmapped", ReviewState.Unmapped), ("Ignored", ReviewState.Ignored) })
            {
                int n = active.Count(c => c.State == state);
                StateBars.Add(new Bar { Label = label, Count = n, Fraction = (double)n / total, Fill = Palette.ForState(state) });
            }
            Raise(nameof(ReviewSummary));
            Raise(nameof(ReadyToBuild));
        }

        // ================= Review
        public ObservableCollection<SymbolCardViewModel> Cards { get; } = new ObservableCollection<SymbolCardViewModel>();
        public ICollectionView CardsView { get; }
        public ObservableCollection<PageItem> Pages { get; private set; } = new ObservableCollection<PageItem>();

        public PageItem SelectedPage { get => _selectedPage; set { if (Set(ref _selectedPage, value)) Raise(nameof(PageCards)); } }
        private PageItem _selectedPage;
        public IEnumerable<SymbolCardViewModel> PageCards => Cards.Where(c => SelectedPage != null && c.Page == SelectedPage.Analysis.PageNumber);

        public SymbolCardViewModel SelectedSymbol
        {
            get => _selectedSymbol;
            set
            {
                if (!Set(ref _selectedSymbol, value) || value == null) return;
                if (SelectedPage?.Analysis.PageNumber != value.Page) SelectedPage = Pages.FirstOrDefault(p => p.Analysis.PageNumber == value.Page);
            }
        }
        private SymbolCardViewModel _selectedSymbol;

        public ReviewFilter Filter { get => _filter; set { if (Set(ref _filter, value)) CardsView.Refresh(); } }
        private ReviewFilter _filter = ReviewFilter.NeedsAttention;

        public string ReviewSummary => Document == null ? "" :
            $"{ActiveCards.Count(c => c.State == ReviewState.Confirmed)} confirmed · {ActiveCards.Count(c => c.State == ReviewState.NeedsReview)} to review · {ActiveCards.Count(c => c.State == ReviewState.Unmapped)} unmapped · {ActiveCards.Count(c => c.State == ReviewState.Ignored)} ignored";
        public bool ReadyToBuild => Document?.ReadyToBuild == true;

        public ICommand ConfirmCommand { get; }
        public ICommand IgnoreCommand { get; }
        public ICommand MapCommand { get; }
        public ICommand ConfirmHighConfidenceCommand { get; }
        public ICommand ContinueToBuildCommand { get; }
        public ICommand CombineCommand { get; }
        public ICommand SplitCommand { get; }

        /// <summary>Cards selected together in the list (Ctrl/Shift+click); set by the view.</summary>
        public IList<SymbolCardViewModel> SelectedCards
        {
            get => _selectedCards;
            set { _selectedCards = value ?? new List<SymbolCardViewModel>(); Raise(); Raise(nameof(CombineSelectionText)); Raise(nameof(HasMultiSelection)); }
        }
        private IList<SymbolCardViewModel> _selectedCards = new List<SymbolCardViewModel>();
        public bool HasMultiSelection => _selectedCards.Count >= 2;
        public string CombineSelectionText => $"Combine {_selectedCards.Count} selected";

        private bool FilterCard(SymbolCardViewModel c)
        {
            if (!IsIncluded(c.Page)) return false; // pages left out of the import are not reviewed
            switch (Filter)
            {
                case ReviewFilter.NeedsAttention: return c.State == ReviewState.NeedsReview || c.State == ReviewState.Unmapped;
                case ReviewFilter.Confirmed: return c.State == ReviewState.Confirmed;
                case ReviewFilter.Ignored: return c.State == ReviewState.Ignored;
                default: return true;
            }
        }

        private void Confirm(SymbolCardViewModel c)
        {
            if (c == null || !c.IsMapped) return;
            c.Model.Review = ReviewState.Confirmed;
            c.Model.UserReviewed = true;
            _mappings.Learn(c.Model, c.Model.SelectedSweSymbol, _mappings.RotationOffset(c.Model, c.Model.SelectedSweSymbol));
            SaveMappings();
            AfterReviewChange(c);
        }

        private void Ignore(SymbolCardViewModel c)
        {
            if (c == null) return;
            c.Model.Review = c.Model.Review == ReviewState.Ignored ? ReviewState.NeedsReview : ReviewState.Ignored;
            c.Model.UserReviewed = c.Model.Review == ReviewState.Ignored;
            AfterReviewChange(c);
        }

        private void Map(SymbolCardViewModel c)
        {
            if (c == null) return;
            var similar = Cards.Where(x => x != c && x.Model.Standard == c.Model.Standard && x.Model.Signature?.Key == c.Model.Signature?.Key && x.State != ReviewState.Ignored).ToList();
            var sameKind = Cards.Where(x => x != c && x.Model.Standard == c.Model.Standard && x.Model.Kind == c.Model.Kind && x.Model.Kind != SymbolKind.Unknown && !x.Model.UserReviewed).ToList();
            LoadLibrary(reload: false);
            int poles = Math.Max(c.Model.Signature?.Poles ?? 1, c.Model.IsCombined ? c.Model.CombinedFrom.Count : 1);
            var dlg = new MappingDialog(c, Catalog, _rules.SearchHint(c.Model.Kind), similar.Count, sameKind.Count, poles,
                                        () => { if (_sweApp == null) return false; LoadLibrary(reload: true); return true; }) { Owner = OwnerWindow };
            if (dlg.ShowDialog() != true) return;

            var chosen = dlg.Result;
            var targets = new List<SymbolCardViewModel> { c };
            if (dlg.ApplyToSimilar) targets.AddRange(similar);
            foreach (var t in targets)
            {
                t.Model.SelectedSweSymbol = chosen.SweSymbol;
                t.Model.Review = ReviewState.Confirmed;
                t.Model.UserReviewed = true;
                _mappings.Learn(t.Model, chosen.SweSymbol, chosen.RotationOffset);
            }
            SaveMappings();
            Catalog.Remember(chosen.SweSymbol);

            // Re-score everything not yet decided: the new kind default may lift other symbols.
            foreach (var x in Cards.Where(x => !x.Model.UserReviewed))
            {
                if (x.Model.Kind == c.Model.Kind && x.Model.Standard == c.Model.Standard) x.Model.SelectedSweSymbol = null; // let the new default apply
                _recognizer.Score(x.Model);
            }
            AfterReviewChange(null);
            Status = $"Mapped {targets.Count} symbol{(targets.Count == 1 ? "" : "s")} to \"{chosen.SweSymbol}\".";
        }

        /// <summary>Reads the SOLIDWORKS Electrical symbol library once per session (UI thread; a second or two).</summary>
        private void LoadLibrary(bool reload)
        {
            if (_sweApp == null || (Catalog.LibraryCount > 0 && !reload)) return;
            var previous = Mouse.OverrideCursor;
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                Catalog.EnsureLibrary(_sweApp, msg => Status = msg, reload);
                Status = Catalog.LibraryCount > 0
                    ? $"{Catalog.LibraryCount:N0} library symbols loaded from SOLIDWORKS Electrical."
                    : "Symbol library not read: " + (Catalog.LibraryError ?? "no symbols returned") + " — see the log.";
            }
            finally { Mouse.OverrideCursor = previous; }
        }

        // ---------------- combine / split

        /// <summary>
        /// What Combine acts on: the card's detected pole group (same tag, linked or stacked poles) when invoked from a
        /// card, otherwise the multi-selection.
        /// </summary>
        private List<SymbolCardViewModel> CombineTargets(SymbolCardViewModel card)
        {
            if (card != null && card.CombineGroup.Count >= 2) return card.CombineGroup.ToList();
            return _selectedCards.Where(c => !c.Model.IsCombined).ToList();
        }

        private void Combine(SymbolCardViewModel card)
        {
            var targets = CombineTargets(card);
            if (targets.Count < 2) return;
            try
            {
                var combined = _recognizer.Combine(Document, targets.Select(t => t.Model).ToList());
                int at = targets.Select(t => Cards.IndexOf(t)).Min();
                foreach (var t in targets) Cards.Remove(t);
                var vm = new SymbolCardViewModel(combined);
                Cards.Insert(Math.Min(at, Cards.Count), vm);
                UpdateCombineHints();
                AfterReviewChange(null);
                SelectedSymbol = vm;
                Status = $"Combined {targets.Count} poles into one {vm.KindText.ToLowerInvariant()} ({vm.Tag}). Choose its multi-circuit library symbol.";
            }
            catch (ArgumentException ex) { Status = ex.Message; }
        }

        private void Split(SymbolCardViewModel card)
        {
            if (card?.Model.IsCombined != true) return;
            var poles = _recognizer.Split(Document, card.Model);
            int at = Math.Max(0, Cards.IndexOf(card));
            Cards.Remove(card);
            foreach (var p in poles.AsEnumerable().Reverse()) Cards.Insert(Math.Min(at, Cards.Count), new SymbolCardViewModel(p));
            UpdateCombineHints();
            AfterReviewChange(null);
            SelectedSymbol = Cards.FirstOrDefault(c => c.Model == poles[0]);
            Status = $"Split {card.Tag} back into {poles.Count} poles.";
        }

        /// <summary>Gives every card its suggested pole group so the card can offer "Combine N poles".</summary>
        private void UpdateCombineHints()
        {
            if (Document == null) return;
            var byModel = Cards.ToDictionary(c => c.Model);
            foreach (var c in Cards) c.CombineGroup = new List<SymbolCardViewModel>();
            foreach (var page in Document.SchematicPages)
                foreach (var group in SymbolCombiner.Suggestions(page))
                {
                    var cards = group.Where(byModel.ContainsKey).Select(m => byModel[m]).Where(c => c.State != ReviewState.Ignored).ToList();
                    if (cards.Count < 2) continue;
                    foreach (var c in cards) c.CombineGroup = cards;
                }
        }

        private void ConfirmHighConfidence()
        {
            foreach (var c in ActiveCards.Where(c => c.State == ReviewState.NeedsReview && c.Confidence >= 0.7 && c.IsMapped).ToList()) Confirm(c);
        }

        private void AfterReviewChange(SymbolCardViewModel changed)
        {
            CrossReferenceResolver.BuildComponents(Document);
            AttachBom();
            RefreshBomItems();
            foreach (var x in Cards) x.Refresh();
            CardsView.Refresh();
            RefreshBars();
            Raise(nameof(PageCards));
            ReviewChanged?.Invoke(this, EventArgs.Empty);
            CommandManager.InvalidateRequerySuggested();
            if (changed != null && !FilterCard(changed))
                SelectedSymbol = CardsView.Cast<SymbolCardViewModel>().FirstOrDefault();
        }

        // ================= manufacturer parts
        public ICommand PartsCommand { get; }
        public ICommand EditItemCommand { get; }
        public ICommand CheckCatalogCommand { get; }
        public ObservableCollection<BomItemViewModel> BomItems { get; } = new ObservableCollection<BomItemViewModel>();
        public bool HasBomItems => BomItems.Count > 0;
        private SwePartCatalog _partCatalog;

        public string PartsSummary
        {
            get
            {
                var d = Document;
                if (d == null) return "";
                var withParts = d.Components.Select(c => d.BomFor(c.Tag)).Where(b => b != null && b.Include && b.Parts.Any(p => p.Include)).ToList();
                var parts = withParts.SelectMany(b => b.Parts.Where(p => p.Include)).ToList();
                if (!AssignParts) return "Parts are not assigned (turned off).";
                int missing = d.Components.Count - withParts.Count;
                return $"{withParts.Count} of {d.Components.Count} components have parts ({parts.Sum(p => p.Quantity)} parts"
                       + $"{(parts.Any(p => p.Catalog == CatalogState.WillCreate) ? $", {parts.Count(p => p.Catalog == CatalogState.WillCreate)} new to the catalog" : "")})."
                       + (missing > 0 ? $" {missing} without parts." : "");
            }
        }

        /// <summary>Links every card to its component's parts entry (tags can change during review).</summary>
        private void AttachBom()
        {
            if (Document == null) return;
            foreach (var c in Cards) c.Bom = c.HasTag ? Document.BomFor(c.Model.PartsKey) : null;
            Raise(nameof(PartsSummary));
        }

        private void RefreshBomItems()
        {
            BomItems.Clear();
            if (Document != null)
                foreach (var b in Document.BomWithoutSymbols.Where(b => b.Parts.Count > 0)) BomItems.Add(new BomItemViewModel(b));
            Raise(nameof(HasBomItems));
            Raise(nameof(PartsSummary));
        }

        /// <summary>Catalog lookup for one part (SWE UI thread); returns an error text or null.</summary>
        private string CheckPart(PartAssignment p, bool cable)
        {
            if (_sweApp == null) return "SOLIDWORKS Electrical is not connected.";
            try
            {
                if (_partCatalog == null) _partCatalog = new SwePartCatalog(_sweApp);
                if (!_partCatalog.Available) return "The manufacturer part catalog is not available through the API.";
                _partCatalog.Check(p, cable);
                return null;
            }
            catch (Exception ex)
            {
                FileLog.Write("parts", ex);
                return "Catalog not checked: " + ex.Message;
            }
        }

        private void CheckCatalog()
        {
            if (_sweApp == null || Document == null) return;
            _partCatalog = null; // fresh lookups: parts may have been added in SWE meanwhile
            foreach (var b in Document.Bom.Where(b => b.Kind != BomItemKind.Wire))
                foreach (var p in b.Parts.Where(p => !string.IsNullOrWhiteSpace(p.Reference)))
                {
                    var error = CheckPart(p, b.Kind == BomItemKind.Cable);
                    if (error != null) { Status = error; return; }
                }
        }

        private void EditParts(SymbolCardViewModel c)
        {
            if (c == null || !c.HasTag || Document == null) return;
            var bom = BomReader.Ensure(Document, c.Model.PartsKey);
            var comp = Document.Components.FirstOrDefault(x => BomText.MarkKey(x.Tag) == bom.Key);
            var pages = comp != null ? string.Join(", ", comp.Pages) : c.Page.ToString();
            var symbols = comp?.Symbols ?? new List<DetectedSymbol> { c.Model };
            var terminals = symbols.Select(s => string.Join("/", s.Pins.Select(p => string.IsNullOrWhiteSpace(p.Label) ? "?" : p.Label.Trim())))
                                   .Where(t => t.Length > 0).ToList();
            var facts = $"{c.KindText} · {symbols.Count} symbol(s) on page {pages}"
                        + (bom.Parts.Count == 0 ? " · the PDF lists no parts for this mark" : $" · read from {string.Join("; ", bom.Parts.Select(p => p.Origin).Distinct())}")
                        + Environment.NewLine
                        + "Circuits come from the mapped symbols (" + string.Join(", ", symbols.Select(s => s.SelectedSweSymbol ?? "not mapped").Distinct()) + ")"
                        + (terminals.Any(t => t.Any(char.IsLetterOrDigit))
                            ? "; terminal designations from the PDF: " + string.Join(", ", terminals) + "."
                            : "; the PDF shows no terminal designations for these symbols.")
                        + " Tick \"Circuits\" on the part that should carry them (the others become accessories).";
            var dlg = new PartsDialog(bom, $"{c.Model.PartsKey} — manufacturer parts", facts, true, _sweApp == null ? null : (Func<PartAssignment, bool, string>)CheckPart) { Owner = OwnerWindow };
            if (dlg.ShowDialog() != true) return;
            AttachBom();
            RefreshBomItems();
            Status = $"{c.Model.PartsKey}: {PartsText.Summary(bom.Parts)}.";
        }

        private void EditItem(BomItemViewModel item)
        {
            if (item == null || Document == null) return;
            var b = item.Model;
            var facts = $"{PartsText.Kind(b.Kind)} without a symbol on the drawings"
                        + (b.LocationTag != null ? $" · created in location {b.LocationTag}" : "")
                        + (b.Kind == BomItemKind.Terminal ? " · terminals are reported only: add them to their strip in SOLIDWORKS Electrical" : "");
            var dlg = new PartsDialog(b, $"{b.Mark} — manufacturer parts", facts, false, _sweApp == null ? null : (Func<PartAssignment, bool, string>)CheckPart) { Owner = OwnerWindow };
            if (dlg.ShowDialog() != true) return;
            item.Refresh();
            Raise(nameof(PartsSummary));
        }

        public event EventHandler ReviewChanged;
        internal Window OwnerWindow { get; set; }

        private void SaveMappings()
        {
            try { _mappings.Save(); }
            catch (Exception ex) { Status = "Could not save mappings: " + ex.Message; FileLog.Write("mappings", ex); }
        }

        // ================= Build
        public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }
        private string _projectName;
        public string TemplateName
        {
            get => _settings.Build.TemplateName;
            set
            {
                _settings.Build.TemplateName = value;
                // a typed name that is not the picked template must not fall back to the picked template's file
                if (!string.Equals(value, _selectedTemplate?.Name, StringComparison.OrdinalIgnoreCase)) _settings.Build.TemplatePath = null;
                Raise();
            }
        }
        public string TemplateArchivePath { get => _settings.Build.TemplateArchivePath; set { _settings.Build.TemplateArchivePath = value; Raise(); Raise(nameof(UsingArchive)); } }
        public bool UsingArchive => !string.IsNullOrWhiteSpace(TemplateArchivePath);

        // ---- project templates of the SWE environment
        public ObservableCollection<ProjectTemplate> Templates { get; } = new ObservableCollection<ProjectTemplate>();
        public ProjectTemplate SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                if (!Set(ref _selectedTemplate, value) || value == null) return;
                TemplateName = value.Name;
                _settings.Build.TemplatePath = value.Path;
                // ANSI templates are drawn in inches, IEC / GB / JIS in millimetres; the reviewer can still override
                if (value.Standard == "ANSI" || value.Standard == "JIC" || value.Standard == "NFPA") SetUnits(DrawingUnits.Inches);
                else if (value.Standard != null) SetUnits(DrawingUnits.Millimeters);
                TemplateArchivePath = null; // a picked template replaces an archive
                TrySaveSettings();
            }
        }
        private ProjectTemplate _selectedTemplate;
        public string TemplateFolderText { get => _templateFolderText; private set => Set(ref _templateFolderText, value); }
        private string _templateFolderText;
        private bool _templatesLoaded;
        public ICommand RefreshTemplatesCommand { get; }

        /// <summary>
        /// Lists the environment's project templates and preselects the saved one, or else one whose name matches the
        /// PDF's standard (ANSI PDF → ANSI template).
        /// </summary>
        private void LoadTemplates()
        {
            _templatesLoaded = true;
            var r = SweTemplates.List(_sweApp);
            Templates.Clear();
            foreach (var t in r.Templates) Templates.Add(t);
            TemplateFolderText = r.Error != null
                ? r.Error + " — type a template name or choose a .tewzip archive."
                : $"{Templates.Count} template{(Templates.Count == 1 ? "" : "s")} in {r.Folder}";

            var docStandard = Document?.SchematicPages.GroupBy(p => p.Standard).OrderByDescending(g => g.Count()).Select(g => g.Key.ToString()).FirstOrDefault();
            var saved = Templates.FirstOrDefault(t => t.Name.Equals(TemplateName ?? "", StringComparison.OrdinalIgnoreCase));
            bool savedFits = saved != null && (saved.Standard == null || docStandard == null || saved.Standard.Equals(docStandard, StringComparison.OrdinalIgnoreCase));
            var pick = savedFits ? saved
                     : Templates.FirstOrDefault(t => docStandard != null && docStandard.Equals(t.Standard, StringComparison.OrdinalIgnoreCase))
                       ?? saved ?? Templates.FirstOrDefault();
            if (pick != null && !UsingArchive) SelectedTemplate = pick;
            else Raise(nameof(SelectedTemplate));
        }
        public bool FitToSheet { get => _settings.Build.FitToSheet; set { _settings.Build.FitToSheet = value; Raise(); } }
        public double GridSnap { get => _settings.Build.GridSnap; set { _settings.Build.GridSnap = value; Raise(); } }
        public double SheetWidth { get => _settings.Build.SheetDrawingWidth; set { _settings.Build.SheetDrawingWidth = value; Raise(); } }
        public double SheetHeight { get => _settings.Build.SheetDrawingHeight; set { _settings.Build.SheetDrawingHeight = value; Raise(); } }

        // ---- unit of measure of the template drawing
        public bool UseInches { get => _settings.Build.Units == DrawingUnits.Inches; set => SetUnits(value ? DrawingUnits.Inches : DrawingUnits.Millimeters); }
        public bool UseMillimeters { get => !UseInches; set { if (value) SetUnits(DrawingUnits.Millimeters); } }
        public string UnitLabel => UnitConversion.Label(_settings.Build.Units);
        public string DrawingAreaLabel => $"Template drawing area ({UnitLabel})";

        /// <summary>
        /// Switches units and converts the drawing area, origin and grid so they still describe the same sheet
        /// (400 × 277 mm ↔ 15.75 × 10.91 in; 2.5 mm grid ↔ 0.1 in).
        /// </summary>
        private void SetUnits(DrawingUnits u)
        {
            var b = _settings.Build;
            if (b.Units == u) return;
            double f = u == DrawingUnits.Inches ? 1 / 25.4 : 25.4;
            int digits = u == DrawingUnits.Inches ? 3 : 1;
            b.SheetDrawingWidth = Math.Round(b.SheetDrawingWidth * f, digits);
            b.SheetDrawingHeight = Math.Round(b.SheetDrawingHeight * f, digits);
            b.SheetDrawingLeft = Math.Round(b.SheetDrawingLeft * f, digits);
            b.SheetDrawingBottom = Math.Round(b.SheetDrawingBottom * f, digits);
            b.Units = u;
            var grid = GridChoices(u);
            b.GridSnap = b.GridSnap <= 0 ? 0 : grid.Where(g => g.Value > 0).OrderBy(g => Math.Abs(g.Value - b.GridSnap * f)).First().Value;
            TrySaveSettings();
            foreach (var n in new[] { nameof(UseInches), nameof(UseMillimeters), nameof(UnitLabel), nameof(DrawingAreaLabel), nameof(GridOptions),
                                      nameof(GridSnap), nameof(SheetWidth), nameof(SheetHeight) }) Raise(n);
            Status = $"Drawing units set to {(u == DrawingUnits.Inches ? "inches" : "millimetres")}.";
        }

        public sealed class GridChoice
        {
            public double Value { get; set; }
            public string Text { get; set; }
        }

        private static List<GridChoice> GridChoices(DrawingUnits u)
        {
            var values = u == DrawingUnits.Inches ? new[] { 0.0, 0.05, 0.1, 0.125, 0.25 } : new[] { 0.0, 1.25, 2.5, 5.0 };
            var label = UnitConversion.Label(u);
            return values.Select(v => new GridChoice { Value = v, Text = v <= 0 ? "Off" : $"{v:0.###} {label}" }).ToList();
        }
        public bool AttachSourcePdf { get => _settings.Build.AttachSourcePdf; set { _settings.Build.AttachSourcePdf = value; Raise(); } }
        public bool ApplyWireNumbers { get => _settings.Build.ApplyWireNumbers; set { _settings.Build.ApplyWireNumbers = value; Raise(); } }
        public bool AssignParts { get => _settings.Build.AssignParts; set { _settings.Build.AssignParts = value; Raise(); Raise(nameof(PartsSummary)); } }
        public bool CreateItemsWithoutSymbols { get => _settings.Build.CreateItemsWithoutSymbols; set { _settings.Build.CreateItemsWithoutSymbols = value; Raise(); Raise(nameof(PartsSummary)); } }
        public bool CreateOriginDestination { get => _settings.Build.CreateOriginDestinationArrows; set { _settings.Build.CreateOriginDestinationArrows = value; Raise(); } }
        public IReadOnlyList<GridChoice> GridOptions => GridChoices(_settings.Build.Units);

        public ObservableCollection<LogRow> Log { get; } = new ObservableCollection<LogRow>();
        public ObservableCollection<Kpi> ResultKpis { get; } = new ObservableCollection<Kpi>();
        public double BuildFraction { get => _buildProgress; private set => Set(ref _buildProgress, value); }
        private double _buildProgress;
        public string BuildStage { get => _buildStage; private set => Set(ref _buildStage, value); }
        private string _buildStage;
        public bool HasResult => ResultKpis.Count > 0;

        public ObservableCollection<string> EnvironmentIssues { get; } = new ObservableCollection<string>();
        public string EnvironmentSummary { get => _envSummary; private set => Set(ref _envSummary, value); }
        private string _envSummary;
        public bool EnvironmentOk => _sweApp != null && EnvironmentIssues.All(i => i.StartsWith("optional"));

        private bool CanBuild => !IsBusy && Document?.ReadyToBuild == true && !string.IsNullOrWhiteSpace(ProjectName)
                                 && (!string.IsNullOrWhiteSpace(TemplateName) || File.Exists(TemplateArchivePath ?? ""));

        public ICommand SimulateCommand { get; }
        public ICommand BuildCommand { get; }
        public ICommand BrowseTemplateCommand { get; }

        private void CheckEnvironment()
        {
            EnvironmentIssues.Clear();
            try
            {
                foreach (var p in SweApi.CheckContract()) EnvironmentIssues.Add(p);
                EnvironmentSummary = (_sweApp == null ? "SOLIDWORKS Electrical not connected" : "SOLIDWORKS Electrical connected")
                                     + " · " + SweApi.InteropDescription.Split('(')[0].Trim()
                                     + (EnvironmentIssues.Count == 0 ? " · API contract OK" : $" · {EnvironmentIssues.Count} API issue(s)");
            }
            catch (Exception ex) { EnvironmentSummary = "EwAPI interop not loaded: " + ex.Message; }
            Raise(nameof(EnvironmentOk));
        }

        private void BrowseTemplate()
        {
            var dlg = new OpenFileDialog { Title = "Choose project template archive", Filter = "SOLIDWORKS Electrical archives (*.tewzip;*.proj.tewzip)|*.tewzip|All files|*.*" };
            if (dlg.ShowDialog() == true) TemplateArchivePath = dlg.FileName;
        }

        private async Task BuildAsync(bool dryRun)
        {
            IsBusy = true;
            Log.Clear(); ResultKpis.Clear(); Raise(nameof(HasResult));
            BuildFraction = 0;
            _cts = new CancellationTokenSource();
            _settings.Build.ProjectName = null; // per-run value, not persisted
            TrySaveSettings();
            try
            {
                var options = Clone(_settings.Build);
                options.ProjectName = ProjectName;
                var plan = BuildPlanFactory.Create(Document, options, _mappings);

                ISchematicTarget target = dryRun ? (ISchematicTarget)new DryRunTarget() : new SweTarget(_sweApp);
                var runner = new BuildRunner(target, async () => await Dispatcher.Yield(DispatcherPriority.Background));
                var progress = new Progress<BuildProgress>(p =>
                {
                    BuildFraction = p.Fraction;
                    if (p.Stage != null) BuildStage = p.Stage;
                    if (p.Entry != null) Log.Add(new LogRow { Entry = p.Entry });
                });

                Status = dryRun ? "Simulating build…" : "Building project in SOLIDWORKS Electrical…";
                var result = await runner.RunAsync(plan, progress, _cts.Token);

                ResultKpis.Add(new Kpi { Label = "Pages", Value = result.Pages.ToString() });
                ResultKpis.Add(new Kpi { Label = "Symbols", Value = result.Symbols.ToString() });
                ResultKpis.Add(new Kpi { Label = "Wire nets", Value = result.Wires.ToString() });
                ResultKpis.Add(new Kpi { Label = "Components", Value = result.Components.ToString() });
                ResultKpis.Add(new Kpi { Label = "Parts", Value = result.Parts.ToString(), Hint = result.Items > 0 ? $"{result.Items} items without symbols" : null });
                ResultKpis.Add(new Kpi { Label = "Errors", Value = result.Errors.ToString() });
                Raise(nameof(HasResult));

                if (target is SweTarget st && st.DeferredReportPath != null)
                    Log.Add(new LogRow { Entry = new BuildLogEntry { Level = LogLevel.Info, Message = "Wire numbers and origin-destination pairs to apply: " + st.DeferredReportPath } });
                if (dryRun && target is DryRunTarget dt)
                    FileLog.Write("simulate", string.Join(Environment.NewLine, dt.Operations));

                foreach (var e in result.Log) FileLog.Write(dryRun ? "simulate" : "build", e.ToString());
                Status = result.Cancelled ? "Build cancelled." : dryRun
                    ? $"Simulation complete: {result.Errors} problem(s). Nothing was changed in SOLIDWORKS Electrical."
                    : result.Errors == 0 ? $"Project \"{ProjectName}\" built." : $"Project built with {result.Errors} error(s) — see log.";
            }
            finally { IsBusy = false; }
        }

        private static BuildOptions Clone(BuildOptions o) =>
            System.Text.Json.JsonSerializer.Deserialize<BuildOptions>(System.Text.Json.JsonSerializer.Serialize(o));

        private void TrySaveSettings()
        {
            try { _settings.Save(); } catch (Exception ex) { FileLog.Write("settings", ex); }
        }

        private void Fail(Exception ex)
        {
            FileLog.Write("ui", ex);
            Status = "Error: " + ex.Message;
            IsBusy = false;
            if (CurrentStep == Step.Analyze && Document == null) GoTo(Step.Source);
        }
    }
}
