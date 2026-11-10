using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.UI
{
    /// <summary>
    /// State and behaviour of the VendorGrid dialog. Layout of responsibilities:
    ///   search flow + results ......... this file
    ///   project parts (SOLIDWORKS Electrical) ... ProjectVM
    ///   BOM staging ................... BomVM
    ///   cost curve + matrix ........... CurveVM (filled here by RebuildCurve)
    /// </summary>
    public sealed class MainViewModel : ObservableObject
    {
        const int MaxQuantity = 10000000;
        const int MaxRecent = 12;
        static readonly int[] Volumes = { 1, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000 };

        readonly Dispatcher _ui;
        readonly SearchService _service = new SearchService();
        readonly HashSet<string> _hiddenSeries = new HashSet<string>();
        readonly DispatcherTimer _toastTimer;
        CancellationTokenSource _cts;
        AppSettings _settings;
        VendorStatusVM _filter;
        string _sortKey = "favorite";
        bool _sortDesc;

        string _partNumber = "";
        string _searchedPart = "";
        string _quantityText = "100";
        bool _isSearching, _hasSearched;
        string _status = Loc.T("Ready");
        int _done, _total;
        int _selectedTab;
        bool _tableView = true;
        bool _calcExpanded = true;
        string _notification = "";
        OverviewVM _overview;

        string _kpiPrice = "—", _kpiPriceSub = "", _kpiStock = "—", _kpiStockSub = "", _kpiSavings = "—", _kpiSavingsSub = "", _kpiLead = "—", _kpiLeadSub = "";
        string _calcSummary = "";

        public MainViewModel(Dispatcher ui)
        {
            _ui = ui;
            _settings = SettingsStore.Load();
            _quantityText = Math.Max(1, _settings.LastQuantity).ToString();

            Rows = new ObservableCollection<ResultRowVM>();
            RowsView = CollectionViewSource.GetDefaultView(Rows);
            RowsView.Filter = FilterRow;
            var calc = new ListCollectionView(Rows) { Filter = o => ((ResultRowVM)o).HasPricing };
            calc.SortDescriptions.Add(new SortDescription("TotalSort", ListSortDirection.Ascending));
            CalcView = calc;
            ApplySort();

            Presets = Volumes.Select(v => new QtyPresetVM(v)).ToList();
            UpdatePresets();

            Recent = new ObservableCollection<string>((_settings.RecentParts ?? new List<string>()).Take(MaxRecent));

            Bom = new BomVM();
            Bom.Load(_settings.Bom ?? new List<BomLine>());
            Bom.Changed += () => { _settings.Bom = Bom.Snapshot(); SaveSettings(); OnPropertyChanged("BomTabText"); ExportBomCommand.Raise(); };

            Project = new ProjectVM();
            Project.PartChosen += OnProjectPartChosen;

            _toastTimer = new DispatcherTimer(DispatcherPriority.Normal, ui) { Interval = TimeSpan.FromSeconds(3.5) };
            _toastTimer.Tick += (s, e) => { _toastTimer.Stop(); Notification = ""; };

            SearchCommand = new RelayCommand(_ => StartSearch(), _ => !IsSearching && PartNumber.Trim().Length > 0);
            CancelCommand = new RelayCommand(_ => Cancel(), _ => IsSearching);
            ClearPartCommand = new RelayCommand(_ => PartNumber = "");
            SettingsCommand = new RelayCommand(_ => { var h = RequestSettings; if (h != null) h(); });
            QuickQuantityCommand = new RelayCommand(p => { int q; if (TryInt(p, out q)) QuantityText = q.ToString(); });
            SearchPartCommand = new RelayCommand(p => { var s = p as string; if (!string.IsNullOrWhiteSpace(s)) SearchPart(s, null); });
            ClearRecentCommand = new RelayCommand(_ => ClearRecent());
            OpenUrlCommand = new RelayCommand(p => OpenUrl(p as string));
            CopyCommand = new RelayCommand(p => CopyText(p as string));
            AddToBomCommand = new RelayCommand(p => AddToBom(p as ResultRowVM));
            TiersCommand = new RelayCommand(p => { var r = p as ResultRowVM; var h = RequestTiers; if (r != null && r.HasPricing && h != null) h(r); });
            ToggleFavoriteCommand = new RelayCommand(p => ToggleFavorite(p as VendorStatusVM));
            FilterVendorCommand = new RelayCommand(p => SetFilter(p as VendorStatusVM));
            ClearFilterCommand = new RelayCommand(_ => SetFilter(null));
            SortCommand = new RelayCommand(p => Sort(p as string));
            SetTableViewCommand = new RelayCommand(_ => TableView = true);
            SetCardViewCommand = new RelayCommand(_ => TableView = false);
            ToggleCalcCommand = new RelayCommand(_ => CalcExpanded = !CalcExpanded);
            ExportQuoteCommand = new RelayCommand(_ => ExportQuote(), _ => Rows.Any(r => r.IsListing));
            RemoveBomCommand = new RelayCommand(p => { var l = p as BomLineVM; if (l != null) Bom.Remove(l); });
            ClearBomCommand = new RelayCommand(_ => { if (Confirm(Loc.T("Remove all staged parts from the BOM?"))) Bom.Clear(); });
            ExportBomCommand = new RelayCommand(_ => ExportBom(), _ => Bom.HasLines);
            ExportBomCommand.Raise();   // BOM restored from the last session
        }

        // ---- events raised towards the window ------------------------------------------------------

        public event Action RequestSettings;
        public event Action<ResultRowVM> RequestTiers;
        public event Action<string, string, string> RequestQuote;     // text, csv, suggested file name
        public event Action<string, byte[]> RequestSave;               // suggested file name (.xlsx), file bytes
        public Func<string, bool> ConfirmHandler { get; set; }

        bool Confirm(string message) { var h = ConfirmHandler; return h == null || h(message); }

        // ---- collections and sub view-models ------------------------------------------------------

        public ObservableCollection<VendorStatusVM> Vendors { get; } = new ObservableCollection<VendorStatusVM>();
        public ObservableCollection<ResultRowVM> Rows { get; private set; }
        public ICollectionView RowsView { get; private set; }
        public ICollectionView CalcView { get; private set; }
        public ObservableCollection<string> Recent { get; private set; }
        public List<QtyPresetVM> Presets { get; private set; }
        public CurveVM Curve { get; } = new CurveVM();
        public BomVM Bom { get; private set; }
        public ProjectVM Project { get; private set; }

        // ---- commands ------------------------------------------------------------------------------

        public RelayCommand SearchCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }
        public RelayCommand ClearPartCommand { get; private set; }
        public RelayCommand SettingsCommand { get; private set; }
        public RelayCommand QuickQuantityCommand { get; private set; }
        public RelayCommand SearchPartCommand { get; private set; }
        public RelayCommand ClearRecentCommand { get; private set; }
        public RelayCommand OpenUrlCommand { get; private set; }
        public RelayCommand CopyCommand { get; private set; }
        public RelayCommand AddToBomCommand { get; private set; }
        public RelayCommand TiersCommand { get; private set; }
        public RelayCommand ToggleFavoriteCommand { get; private set; }
        public RelayCommand FilterVendorCommand { get; private set; }
        public RelayCommand ClearFilterCommand { get; private set; }
        public RelayCommand SortCommand { get; private set; }
        public RelayCommand SetTableViewCommand { get; private set; }
        public RelayCommand SetCardViewCommand { get; private set; }
        public RelayCommand ToggleCalcCommand { get; private set; }
        public RelayCommand ExportQuoteCommand { get; private set; }
        public RelayCommand RemoveBomCommand { get; private set; }
        public RelayCommand ClearBomCommand { get; private set; }
        public RelayCommand ExportBomCommand { get; private set; }

        // ---- search bar ---------------------------------------------------------------------------

        public string PartNumber
        {
            get { return _partNumber; }
            set { if (Set(ref _partNumber, value ?? "")) { SearchCommand.Raise(); OnPropertyChanged("HasPartText"); } }
        }
        public bool HasPartText { get { return _partNumber.Length > 0; } }

        public string QuantityText
        {
            get { return _quantityText; }
            set
            {
                if (!Set(ref _quantityText, value ?? "")) return;
                OnPropertyChanged("Quantity");
                Reprice();
            }
        }

        public int Quantity
        {
            get
            {
                int q;
                if (!int.TryParse((_quantityText ?? "").Replace(",", "").Trim(), out q) || q < 1) return 1;
                return Math.Min(q, MaxQuantity);
            }
        }

        public bool IsSearching
        {
            get { return _isSearching; }
            private set { if (Set(ref _isSearching, value)) { SearchCommand.Raise(); CancelCommand.Raise(); } }
        }

        public bool HasSearched
        {
            get { return _hasSearched; }
            private set { if (Set(ref _hasSearched, value)) { OnPropertyChanged("ShowEmptyState"); OnPropertyChanged("StatusBarVisible"); } }
        }
        public bool ShowEmptyState { get { return !_hasSearched; } }
        public bool StatusBarVisible { get { return _hasSearched && _settings.ShowStatusBar; } }
        public string Status { get { return _status; } private set { Set(ref _status, value); } }
        public int ProgressDone { get { return _done; } private set { Set(ref _done, value); } }
        public int ProgressTotal { get { return _total; } private set { Set(ref _total, value); } }
        public string SearchedPart { get { return _searchedPart; } private set { Set(ref _searchedPart, value); } }

        public string Notification
        {
            get { return _notification; }
            private set { if (Set(ref _notification, value ?? "")) OnPropertyChanged("HasNotification"); }
        }
        public bool HasNotification { get { return _notification.Length > 0; } }

        public bool HasRecent { get { return Recent.Count > 0; } }

        // ---- navigation ---------------------------------------------------------------------------

        public int SelectedTab
        {
            get { return _selectedTab; }
            set
            {
                if (!Set(ref _selectedTab, value)) return;
                OnPropertyChanged("IsSourcingTab"); OnPropertyChanged("IsCurveTab"); OnPropertyChanged("IsBomTab");
            }
        }
        public bool IsSourcingTab { get { return _selectedTab == 0; } set { if (value) SelectedTab = 0; } }
        public bool IsCurveTab { get { return _selectedTab == 1; } set { if (value) SelectedTab = 1; } }
        public bool IsBomTab { get { return _selectedTab == 2; } set { if (value) SelectedTab = 2; } }
        public string BomTabText { get { return Bom.Count > 0 ? Loc.F("BOM Staging  ({0})", Bom.Count) : Loc.T("BOM Staging"); } }

        public bool TableView
        {
            get { return _tableView; }
            set { if (Set(ref _tableView, value)) { OnPropertyChanged("CardView"); } }
        }
        public bool CardView { get { return !_tableView; } }

        public bool CalcExpanded
        {
            get { return _calcExpanded; }
            set { if (Set(ref _calcExpanded, value)) { OnPropertyChanged("CalcCollapsed"); OnPropertyChanged("CalcGlyph"); } }
        }
        public bool CalcCollapsed { get { return !_calcExpanded; } }
        public string CalcGlyph { get { return _calcExpanded ? "▾" : "▸"; } }
        public string CalcSummary { get { return _calcSummary; } private set { Set(ref _calcSummary, value); } }

        // ---- results header / sorting -------------------------------------------------------------

        public bool HasListings { get { return Rows.Any(r => r.IsListing); } }
        public bool HasResults { get { return _hasSearched; } }
        public string ResultsTitle { get { return string.IsNullOrEmpty(_searchedPart) ? Loc.T("Distributor availability") : Loc.F("Distributor availability for {0}", _searchedPart); } }
        public string ResultsSubtitle
        {
            get
            {
                var listings = Rows.Count(r => r.IsListing);
                var shown = RowsView.Cast<object>().Count();
                return Loc.F("{0} · {1} rows shown · starred vendors first", Loc.N(listings, "{0} listing", "{0} listings"), shown);
            }
        }
        public string AllVendorsText { get { return Loc.F("All vendors ({0})", Vendors.Count); } }
        public bool AllSelected { get { return _filter == null; } }

        string Arrow(string key) { return _sortKey == key ? (_sortDesc ? "  ▼" : "  ▲") : ""; }
        public string HeaderVendor { get { return Loc.T("DISTRIBUTOR") + Arrow("vendor"); } }
        public string HeaderStock { get { return Loc.T("IN-STOCK INVENTORY") + Arrow("stock"); } }
        public string HeaderPrice { get { return Loc.F("UNIT PRICE @ {0}", Money.Count(Quantity)) + Arrow("price"); } }
        public string HeaderTotal { get { return Loc.T("ORDER TOTAL") + Arrow("total"); } }
        public string HeaderLead { get { return Loc.T("LEAD TIME") + Arrow("lead"); } }

        // ---- overview and KPI strip ---------------------------------------------------------------

        public OverviewVM Overview { get { return _overview; } private set { if (Set(ref _overview, value)) OnPropertyChanged("HasOverview"); } }
        public bool HasOverview { get { return _overview != null; } }

        public string KpiPrice { get { return _kpiPrice; } private set { Set(ref _kpiPrice, value); } }
        public string KpiPriceSub { get { return _kpiPriceSub; } private set { Set(ref _kpiPriceSub, value); } }
        public string KpiStock { get { return _kpiStock; } private set { Set(ref _kpiStock, value); } }
        public string KpiStockSub { get { return _kpiStockSub; } private set { Set(ref _kpiStockSub, value); } }
        public string KpiSavings { get { return _kpiSavings; } private set { Set(ref _kpiSavings, value); } }
        public string KpiSavingsSub { get { return _kpiSavingsSub; } private set { Set(ref _kpiSavingsSub, value); } }
        public string KpiLead { get { return _kpiLead; } private set { Set(ref _kpiLead, value); } }
        public string KpiLeadSub { get { return _kpiLeadSub; } private set { Set(ref _kpiLeadSub, value); } }
        public string CalcTitle { get { return Loc.F("Costing and volume discount analysis for {0} units", Money.Count(Quantity)); } }
        public string CalcPriceHeader { get { return Loc.F("UNIT PRICE @ {0}", Money.Count(Quantity)); } }
        public string KpiPriceTitle { get { return Loc.F("BEST PRICE @ {0} PCS", Money.Count(Quantity)); } }

        // =============================================================================================
        // Search flow
        // =============================================================================================

        public void SearchPart(string part, int? quantity)
        {
            PartNumber = part;
            if (quantity.HasValue) QuantityText = quantity.Value.ToString();
            StartSearch();
        }

        void StartSearch()
        {
            var pn = PartNumber.Trim();
            if (pn.Length == 0) return;

            if (_cts != null) _cts.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _settings = SettingsStore.Load();   // picks up keys/vendors changed in Settings
            try { _settings.LastQuantity = Quantity; SaveSettings(); } catch (Exception ex) { Log.Error("Could not save quantity", ex); }
            AddRecent(pn);

            var providers = _service.BuildProviders(_settings);
            Vendors.Clear();
            Rows.Clear();
            _filter = null;
            OnPropertyChanged("AllSelected");
            OnPropertyChanged("AllVendorsText");

            foreach (var p in providers)
            {
                var cfg = _settings.Vendors.FirstOrDefault(v => v.Id == p.Id);
                if (cfg == null) continue;
                var vm = new VendorStatusVM(cfg);
                Vendors.Add(vm);
                Rows.Add(new ResultRowVM(vm, null, Quantity));      // "Searching…" row until the vendor answers
            }

            SearchedPart = pn;
            ResetResults();
            HasSearched = true;
            if (SelectedTab == 2) SelectedTab = 0;
            OnPropertyChanged("StatusBarVisible");

            if (providers.Count == 0)
            {
                Status = Loc.T("No vendors are enabled. Open Settings › Vendors to enable or add vendors.");
                RefreshViews();
                return;
            }

            ProgressTotal = providers.Count;
            ProgressDone = 0;
            IsSearching = true;
            Status = Loc.N(providers.Count, "Searching {0} vendor for “{1}”…", "Searching {0} vendors for “{1}”…", pn);
            Log.Info("Search: " + pn);
            RefreshViews();

            // NOTE: the host (SOLIDWORKS Electrical) owns the message loop, so we never rely on a
            // SynchronizationContext; worker results are marshalled explicitly through the Dispatcher.
            Task.Run(() => _service.RunAsync(pn, providers, token, outcome => _ui.BeginInvoke(new Action(() => Apply(outcome, token)))))
                .ContinueWith(t => _ui.BeginInvoke(new Action(() => Finish(token, t.Exception))));
        }

        void Apply(VendorOutcome o, CancellationToken token)
        {
            if (token.IsCancellationRequested && o.State != VendorState.Idle) return;
            ApplyOutcome(o);
        }

        void ApplyOutcome(VendorOutcome o)
        {
            var vendor = Vendors.FirstOrDefault(v => v.Id == o.VendorId);
            if (vendor == null) return;

            vendor.Apply(o);

            foreach (var r in Rows.Where(r => ReferenceEquals(r.Vendor, vendor)).ToList()) Rows.Remove(r);
            if (o.State == VendorState.Found) foreach (var m in o.Matches) Rows.Add(new ResultRowVM(vendor, m, Quantity));
            else Rows.Add(new ResultRowVM(vendor, null, Quantity));

            ProgressDone = Vendors.Count(v => v.State != VendorState.Searching);
            if (ProgressDone < ProgressTotal) Status = Loc.F("Searching… {0} of {1} vendors responded", ProgressDone, ProgressTotal);
            Recompute();
        }

        void Finish(CancellationToken token, AggregateException error)
        {
            if (error != null) Log.Error("Search task failed", error);
            if (_cts != null && _cts.Token != token) return;      // a newer search replaced this one

            IsSearching = false;
            if (token.IsCancellationRequested) { Status = Loc.T("Search cancelled."); return; }
            var listings = Rows.Count(r => r.IsListing);
            var found = Vendors.Count(v => v.State == VendorState.Found);
            var linkOnly = Vendors.Count(v => v.State == VendorState.LinkOnly);
            var done = Loc.F("Done — {0} from {1} of {2} vendors", Loc.N(listings, "{0} listing", "{0} listings"), found, Vendors.Count);
            if (linkOnly > 0) done += Loc.F(" ({0} link-only)", linkOnly);
            Status = done + ".";
            Recompute();
        }

        void Cancel()
        {
            if (_cts != null) _cts.Cancel();
            foreach (var v in Vendors.Where(v => v.State == VendorState.Searching).ToList())
                ApplyOutcome(new VendorOutcome { VendorId = v.Id, Vendor = v.Name, State = VendorState.Idle, Message = Loc.T("Cancelled.") });
            IsSearching = false;
            Status = Loc.T("Search cancelled.");
        }

        /// <summary>Called by the window after Settings were saved: re-run the current search with the new keys, country and vendors.</summary>
        public void OnSettingsSaved()
        {
            _settings = SettingsStore.Load();
            OnPropertyChanged("StatusBarVisible");
            Project.Reload(false);
            if (HasSearched && !IsSearching && _searchedPart.Length > 0)
            {
                PartNumber = _searchedPart;
                StartSearch();
            }
        }

        // =============================================================================================
        // Results: sorting, filtering, recompute
        // =============================================================================================

        bool FilterRow(object o)
        {
            var r = (ResultRowVM)o;
            return _filter == null || ReferenceEquals(r.Vendor, _filter);
        }

        void SetFilter(VendorStatusVM vendor)
        {
            _filter = (vendor == null || ReferenceEquals(vendor, _filter)) ? null : vendor;
            foreach (var v in Vendors) v.IsSelected = ReferenceEquals(v, _filter);
            OnPropertyChanged("AllSelected");
            RefreshViews();
        }

        void Sort(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (_sortKey == key) _sortDesc = !_sortDesc;
            else { _sortKey = key; _sortDesc = key == "stock"; }   // stock reads best largest-first
            ApplySort();
            RefreshHeaders();
        }

        void ApplySort()
        {
            var dir = _sortDesc ? ListSortDirection.Descending : ListSortDirection.Ascending;
            using (RowsView.DeferRefresh())
            {
                RowsView.SortDescriptions.Clear();
                RowsView.SortDescriptions.Add(new SortDescription("SortGroup", ListSortDirection.Ascending));
                switch (_sortKey)
                {
                    case "vendor": RowsView.SortDescriptions.Add(new SortDescription("NameSort", dir)); break;
                    case "stock": RowsView.SortDescriptions.Add(new SortDescription("StockSort", dir)); break;
                    case "price": RowsView.SortDescriptions.Add(new SortDescription("PriceSort", dir)); break;
                    case "total": RowsView.SortDescriptions.Add(new SortDescription("TotalSort", dir)); break;
                    case "lead": RowsView.SortDescriptions.Add(new SortDescription("LeadSort", dir)); break;
                    default:
                        RowsView.SortDescriptions.Add(new SortDescription("FavSort", ListSortDirection.Ascending));
                        RowsView.SortDescriptions.Add(new SortDescription("PriceSort", ListSortDirection.Ascending));
                        break;
                }
                RowsView.SortDescriptions.Add(new SortDescription("NameSort", ListSortDirection.Ascending));
            }
        }

        void RefreshHeaders()
        {
            OnPropertyChanged("HeaderVendor"); OnPropertyChanged("HeaderStock"); OnPropertyChanged("HeaderPrice");
            OnPropertyChanged("HeaderTotal"); OnPropertyChanged("HeaderLead");
        }

        void RefreshViews()
        {
            RowsView.Refresh();
            CalcView.Refresh();
            OnPropertyChanged("ResultsSubtitle");
            OnPropertyChanged("HasListings");
            ExportQuoteCommand.Raise();
        }

        void Reprice()
        {
            int q = Quantity;
            foreach (var r in Rows) r.Reprice(q);
            UpdatePresets();
            RefreshHeaders();
            OnPropertyChanged("KpiPriceTitle"); OnPropertyChanged("CalcTitle"); OnPropertyChanged("CalcPriceHeader");
            Recompute();
        }

        void UpdatePresets()
        {
            int q = Quantity;
            foreach (var p in Presets) p.IsActive = p.Value == q;
        }

        void ResetResults()
        {
            Overview = null;
            KpiPrice = KpiStock = KpiSavings = KpiLead = "—";
            KpiPriceSub = KpiStockSub = KpiSavingsSub = KpiLeadSub = "";
            CalcSummary = "";
            Curve.Series.Clear();
            Curve.MatrixRows.Clear();
            Curve.HasData = false;
            Curve.Note = "";
        }

        /// <summary>Recomputes badges, KPI strip, overview, calculator summary and the cost-curve tab from the current rows.</summary>
        void Recompute()
        {
            int qty = Quantity;
            var listings = Rows.Where(r => r.IsListing).ToList();
            var priced = listings.Where(r => r.HasPricing).ToList();

            // Prices are only compared within one currency (the most common one among the results) and, when possible,
            // among exact matches only, so a similar-but-different part never claims "best price".
            var exact = priced.Where(r => !r.IsAlternate).ToList();
            var pool = exact.Count > 0 ? exact : priced;
            var currency = pool.GroupBy(r => r.Currency).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();
            var comparable = pool.Where(r => r.Currency == currency).ToList();

            var fillable = comparable.Where(r => r.Model.StockFor(r.OrderQuantity) == StockLevel.InStock).ToList();
            var best = (fillable.Count > 0 ? fillable : comparable).OrderBy(r => r.Quote.Extended).FirstOrDefault();

            long maxStock = listings.Count == 0 ? 0 : listings.Max(r => r.Model.Stock);
            foreach (var r in Rows)
            {
                r.IsBestPrice = best != null && ReferenceEquals(r, best);
                r.IsMaxStock = r.IsListing && maxStock > 0 && r.Model.Stock == maxStock;
                if (best != null && r.HasPricing && r.Currency == currency && !ReferenceEquals(r, best) && r.Quote.Extended > best.Quote.Extended)
                    r.PremiumText = Loc.F("+{0} vs best", Money.Total(r.Quote.Extended - best.Quote.Extended, currency));
                else r.PremiumText = "";
            }

            // ---- KPI strip -------------------------------------------------------------------------
            if (best != null)
            {
                KpiPrice = Money.Format(best.Quote.UnitPrice, currency);
                KpiPriceSub = Loc.F("{0} · {1} · total {2}", best.Vendor.Name, best.Sku, Money.Total(best.Quote.Extended, currency));
            }
            else
            {
                KpiPrice = "—";
                KpiPriceSub = listings.Count > 0 && priced.Count == 0 ? Loc.T("No pricing returned") : "";
            }

            var withStock = listings.Where(r => r.Model.Stock > 0).ToList();
            if (listings.Count > 0)
            {
                KpiStock = Loc.F("{0} units", Money.Count(withStock.Sum(r => r.Model.Stock)));
                var depot = withStock.OrderByDescending(r => r.Model.Stock).FirstOrDefault();
                KpiStockSub = depot == null ? Loc.T("No vendor reports stock") : Loc.F("Largest: {0} ({1})", depot.Vendor.Name, Money.Count(depot.Model.Stock));
            }
            else { KpiStock = "—"; KpiStockSub = ""; }

            if (comparable.Count > 0)
            {
                double maxSave = comparable.Max(r => r.Quote.SavingsPct);
                KpiSavings = maxSave >= 0.5 ? "-" + Math.Round(maxSave) + "%" : Loc.T("Base tier");
                var lo = comparable.Min(r => r.Quote.UnitPrice);
                var hi = comparable.Max(r => r.Quote.UnitPrice);
                KpiSavingsSub = comparable.Count > 1 && lo > 0
                    ? Loc.F("vs single-unit · vendor spread {0}%", Math.Round((double)((hi - lo) / lo * 100m)))
                    : Loc.T("vs single-unit price");
            }
            else { KpiSavings = "—"; KpiSavingsSub = ""; }

            var ready = listings.Where(r => r.Model.StockFor(r.OrderQuantity) == StockLevel.InStock).ToList();
            var leads = listings.Where(r => r.Model.LeadTimeDays.HasValue).ToList();
            if (ready.Count > 0)
            {
                KpiLead = Loc.T("From stock");
                KpiLeadSub = Loc.N(ready.Count, "{0} listing covers {1} pcs", "{0} listings cover {1} pcs", Money.Count(qty));
            }
            else if (leads.Count > 0)
            {
                var fastest = leads.OrderBy(r => r.Model.LeadTimeDays.Value).First();
                KpiLead = fastest.LeadText;
                KpiLeadSub = Loc.F("Factory lead · {0}", fastest.Vendor.Name);
            }
            else { KpiLead = "—"; KpiLeadSub = listings.Count > 0 ? Loc.T("No lead time reported") : ""; }

            // ---- calculator summary line (collapsed state) -------------------------------------------
            CalcSummary = best == null ? Loc.T("No priced listings yet.")
                : Loc.F("{0} pcs · lowest total {1} with {2} ({3} each)", Money.Count(qty), Money.Total(best.Quote.Extended, currency), best.Vendor.Name, Money.Format(best.Quote.UnitPrice, currency));

            UpdateOverview(listings);
            RebuildCurve(comparable, currency);
            RefreshViews();
        }

        void UpdateOverview(List<ResultRowVM> listings)
        {
            if (listings.Count == 0) { Overview = null; return; }
            var all = listings.Select(r => r.Model).ToList();
            var exact = all.Where(m => !m.IsAlternate).ToList();
            var pool = exact.Count > 0 ? exact : all;

            Func<Func<PartMatch, string>, string> first = pick => pool.Select(pick).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
            Overview = new OverviewVM
            {
                Mpn = first(m => m.Mpn) ?? _searchedPart,
                Manufacturer = first(m => m.Manufacturer),
                Description = pool.Select(m => m.Description).Where(s => !string.IsNullOrWhiteSpace(s)).OrderByDescending(s => s.Length).FirstOrDefault(),
                Category = first(m => m.Category),
                Lifecycle = first(m => m.Lifecycle),
                RoHS = first(m => m.RoHS),
                DatasheetUrl = first(m => m.DatasheetUrl),
                ImageUrl = first(m => m.ImageUrl),
                OnlyAlternates = exact.Count == 0
            };
        }

        void RebuildCurve(List<ResultRowVM> comparable, string currency)
        {
            // Series
            var rows = comparable.Take(10).ToList();
            Curve.Series.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var pts = r.Model.Breaks.GroupBy(b => b.Quantity).Select(g => new ChartPoint(g.Key, g.First().UnitPrice)).OrderBy(p => p.Quantity).ToList();
                var name = r.Vendor.Name + " · " + r.Sku + (string.IsNullOrEmpty(r.Packaging) ? "" : " (" + r.Packaging + ")");
                var s = new ChartSeriesVM(r.Key, name, r.Currency, Palette.ForIndex(i), pts, i) { IsVisible = !_hiddenSeries.Contains(r.Key) };
                var key = r.Key;
                s.PropertyChanged += (o, e) => { if (e.PropertyName == "IsVisible") { var vm = (ChartSeriesVM)o; if (vm.IsVisible) _hiddenSeries.Remove(key); else _hiddenSeries.Add(key); } };
                Curve.Series.Add(s);
            }

            // Matrix: unit price at standard volumes, lowest per column highlighted.
            Curve.MatrixRows.Clear();
            Curve.SetHeaders(Volumes.Select(v => Money.Count(v) + "+").ToList());
            var table = rows.Select(r => new
            {
                Row = r,
                Prices = Volumes.Select(v => { var q = r.Model.Quote(v); return q == null ? (decimal?)null : q.UnitPrice; }).ToList()
            }).ToList();
            for (int i = 0; i < table.Count; i++)
            {
                var t = table[i];
                var m = new MatrixRowVM { Vendor = t.Row.Vendor.Name, Detail = t.Row.Sku, Code = t.Row.Vendor.Code, LogoBrush = t.Row.Vendor.LogoBrush, LogoImage = t.Row.Vendor.LogoImage };
                for (int c = 0; c < Volumes.Length; c++)
                {
                    var price = t.Prices[c];
                    var colMin = table.Select(x => x.Prices[c]).Where(p => p.HasValue).Select(p => p.Value).DefaultIfEmpty(-1m).Min();
                    m.Cells.Add(new MatrixCellVM
                    {
                        Text = price.HasValue ? (price.HasValue && price.Value == colMin ? "● " : "") + Money.Format(price.Value, currency) : "—",
                        IsBest = price.HasValue && price.Value == colMin
                    });
                }
                Curve.MatrixRows.Add(m);
            }
            Curve.HasData = rows.Count > 0;
            var others = comparable.Count - rows.Count;
            Curve.Note = rows.Count == 0 ? ""
                : Loc.F("Prices in {0}", currency) + (others > 0 ? " · " + Loc.F("showing the first 10 of {0} listings", comparable.Count) : "") + " · " + Loc.T("click the chart to use that quantity");
        }

        // =============================================================================================
        // Actions
        // =============================================================================================

        void OnProjectPartChosen(ProjectPartVM p)
        {
            SearchPart(p.Mpn, p.HasQty ? (int?)p.Quantity : null);
        }

        void ToggleFavorite(VendorStatusVM vendor)
        {
            if (vendor == null) return;
            var cfg = _settings.Vendors.FirstOrDefault(v => v.Id == vendor.Id);
            if (cfg == null) return;
            cfg.Favorite = !cfg.Favorite;
            vendor.IsFavorite = cfg.Favorite;
            SaveSettings();
            RefreshViews();
            Toast(cfg.Favorite ? Loc.F("“{0}” starred. It is listed first.", vendor.Name) : Loc.F("“{0}” removed from favourites.", vendor.Name));
        }

        void AddToBom(ResultRowVM row)
        {
            if (row == null || !row.IsListing) return;
            var q = row.Quote;
            Bom.Add(row.Model, q != null ? q.OrderQuantity : Quantity);
            OnPropertyChanged("BomTabText");
            Toast(Loc.F("Added {0} ({1} pcs) from {2} to the BOM", row.Mpn, Money.Count(q != null ? q.OrderQuantity : Quantity), row.Vendor.Name));
        }

        void ExportQuote()
        {
            var h = RequestQuote;
            if (h == null) return;
            var rows = RowsView.Cast<ResultRowVM>().ToList();
            var safe = new string(_searchedPart.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
            h(Export.QuoteText(_searchedPart, Quantity, rows), Export.QuoteCsv(_searchedPart, Quantity, rows), "VendorGrid_Quote_" + safe + "_qty" + Quantity + ".csv");
        }

        void ExportBom()
        {
            var h = RequestSave;
            if (h == null) return;
            byte[] workbook;
            try { workbook = Export.BomXlsx(Bom.Lines); }
            catch (Exception ex) { Log.Error("Building the BOM workbook failed", ex); Toast(Loc.F("The BOM could not be exported: {0}", ex.Message)); return; }
            h("VendorGrid_BOM_" + DateTime.Now.ToString("yyyy-MM-dd") + ".xlsx", workbook);
        }

        void AddRecent(string pn)
        {
            var existing = Recent.FirstOrDefault(r => string.Equals(r, pn, StringComparison.OrdinalIgnoreCase));
            if (existing != null) Recent.Remove(existing);
            Recent.Insert(0, pn);
            while (Recent.Count > MaxRecent) Recent.RemoveAt(Recent.Count - 1);
            _settings.RecentParts = Recent.ToList();
            SaveSettings();
            OnPropertyChanged("HasRecent");
        }

        void ClearRecent()
        {
            if (Recent.Count == 0 || !Confirm(Loc.T("Clear the list of recently searched parts?"))) return;
            Recent.Clear();
            _settings.RecentParts = new List<string>();
            SaveSettings();
            OnPropertyChanged("HasRecent");
        }

        void SaveSettings()
        {
            try { SettingsStore.Save(_settings); }
            catch (Exception ex) { Log.Error("Could not save settings", ex); }
        }

        void Toast(string message)
        {
            Notification = message;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        void CopyText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); Toast(Loc.F("Copied {0}", text)); }
            catch (Exception ex) { Log.Error("Clipboard unavailable", ex); }
        }

        static bool TryInt(object value, out int result)
        {
            result = 0;
            if (value == null) return false;
            if (value is int) { result = (int)value; return true; }
            return int.TryParse(Convert.ToString(value), out result);
        }

        static void OpenUrl(string url)
        {
            Uri uri;
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri)) return;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("Could not open " + url, ex); }
        }

        /// <summary>Called when the window closes.</summary>
        public void Shutdown()
        {
            if (_cts != null) _cts.Cancel();
            _toastTimer.Stop();
        }
    }
}
