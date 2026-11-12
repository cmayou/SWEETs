using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.UI
{
    /// <summary>One vendor as shown in the status bar (filter chips) and shared by all of that vendor's result rows.</summary>
    public sealed class VendorStatusVM : ObservableObject
    {
        VendorState _state = VendorState.Searching;
        string _message;
        string _searchUrl;
        long _latencyMs;
        long _stock;
        int _listings;
        bool _selected;
        bool _favorite;

        public VendorStatusVM(VendorConfig cfg)
        {
            Id = cfg.Id;
            Name = string.IsNullOrWhiteSpace(cfg.Name) ? cfg.Id : cfg.Name.Trim();
            Code = cfg.DisplayCode;
            Kind = cfg.Kind;
            LogoBrush = Palette.ForVendor(cfg.Id);
            LogoImage = VendorLogos.Get(cfg.Id);
            _favorite = cfg.Favorite;
        }

        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Code { get; private set; }
        public string Kind { get; private set; }
        public Brush LogoBrush { get; private set; }
        public System.Windows.Media.ImageSource LogoImage { get; private set; }
        public bool HasLogo { get { return LogoImage != null; } }
        public bool NoLogo { get { return LogoImage == null; } }

        public bool IsFavorite { get { return _favorite; } set { Set(ref _favorite, value); } }
        public bool IsSelected { get { return _selected; } set { Set(ref _selected, value); } }
        public string SearchUrl { get { return _searchUrl; } }
        public string Message { get { return _message; } }
        public long TotalStock { get { return _stock; } }
        public VendorState State { get { return _state; } }

        public bool IsBusy { get { return _state == VendorState.Searching; } }
        public bool NeedsSetup { get { return _state == VendorState.NotConfigured; } }
        public bool IsLinkOnly { get { return _state == VendorState.LinkOnly; } }
        public bool HasSearchUrl { get { return !string.IsNullOrEmpty(_searchUrl); } }

        public string StatusText
        {
            get
            {
                switch (_state)
                {
                    case VendorState.Searching: return Loc.T("Searching…");
                    case VendorState.Found: return Loc.N(_listings, "{0} listing", "{0} listings");
                    case VendorState.NotFound: return Loc.T("Not found");
                    case VendorState.NotConfigured: return Loc.T("Setup needed");
                    case VendorState.Error: return Loc.T("Error");
                    case VendorState.LinkOnly: return Loc.T("Link only");
                    default: return Loc.T("Idle");
                }
            }
        }

        /// <summary>Short text for the status-bar chip.</summary>
        public string ChipSummary
        {
            get
            {
                switch (_state)
                {
                    case VendorState.Found: return _stock > 0 ? Loc.F("{0} in stock", Money.Count(_stock)) : Loc.T("No stock");
                    default: return StatusText;
                }
            }
        }

        /// <summary>Shape shown next to the status colour. Searching and link-only keep their own recognisable marks.</summary>
        public string Glyph
        {
            get
            {
                switch (_state)
                {
                    case VendorState.Searching: return "…";
                    case VendorState.LinkOnly: return "↗";
                    default: return ToneFor.Symbol;
                }
            }
        }
        public string StatusDisplay { get { return Glyph + " " + StatusText; } }

        public string LatencyText { get { return _latencyMs > 0 && _state != VendorState.Searching ? _latencyMs + " ms" : ""; } }

        Tone ToneFor
        {
            get
            {
                switch (_state)
                {
                    case VendorState.Found: return _stock > 0 ? Tone.Good : Tone.Warn;
                    case VendorState.Searching: return Tone.Info;
                    case VendorState.NotConfigured: return Tone.Warn;
                    case VendorState.Error: return Tone.Bad;
                    case VendorState.LinkOnly: return Tone.Info;
                    default: return Tone.Neutral;
                }
            }
        }
        public Brush ChipBack { get { return ToneFor.Back; } }
        public Brush ChipFore { get { return ToneFor.Fore; } }

        public string ToolTipText
        {
            get
            {
                var t = Name + " — " + StatusText;
                if (_latencyMs > 0 && _state != VendorState.Searching) t += " · " + _latencyMs + " ms";
                if (!string.IsNullOrEmpty(_message)) t += "\n" + _message;
                return t;
            }
        }

        public void Apply(VendorOutcome o)
        {
            _state = o.State;
            _message = o.Message;
            _latencyMs = o.LatencyMs;
            if (!string.IsNullOrEmpty(o.SearchUrl)) _searchUrl = o.SearchUrl;
            _listings = o.Matches.Count;
            _stock = o.Matches.Where(m => m.Stock > 0).Sum(m => m.Stock);
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>One price-break line inside the "Tiers" expander / dialog.</summary>
    public sealed class TierRowVM
    {
        public string Quantity { get; set; }
        public string UnitPrice { get; set; }
        public string Extended { get; set; }
        public bool IsCurrent { get; set; }
        public string CurrentMark { get { return IsCurrent ? Loc.T("◀ applies") : ""; } }
    }

    /// <summary>
    /// One row of the results: either a vendor listing (SKU with stock/price) or, when a vendor has no listing,
    /// a status row ("Searching…", "Setup needed", "Not found", "Open on website"). Re-priced whenever the quantity changes.
    /// </summary>
    public sealed class ResultRowVM : ObservableObject
    {
        int _qty;
        bool _isBest, _isMaxStock, _showTiers;
        string _premiumText = "";

        public ResultRowVM(VendorStatusVM vendor, PartMatch model, int qty)
        {
            Vendor = vendor;
            Model = model;
            _qty = qty;
        }

        public VendorStatusVM Vendor { get; private set; }
        /// <summary>Null for status rows.</summary>
        public PartMatch Model { get; private set; }

        public bool IsListing { get { return Model != null; } }
        public bool IsStatusRow { get { return Model == null; } }
        public bool HasPricing { get { return Model != null && Model.HasPricing; } }
        public bool IsAlternate { get { return Model != null && Model.IsAlternate; } }

        public PriceQuote Quote { get { return Model == null ? null : Model.Quote(_qty); } }
        int Need { get { var q = Quote; return q != null ? q.OrderQuantity : _qty; } }
        public int OrderQuantity { get { return Need; } }
        public string Currency { get { return Model == null ? "USD" : Model.Currency; } }

        // ---- sorting ------------------------------------------------------------------------
        /// <summary>0 priced listing, 1 listing without price, 2 vendor status, 3 link-only website.</summary>
        public int SortGroup
        {
            get
            {
                if (Model != null) return Model.HasPricing ? 0 : 1;
                return Vendor.State == VendorState.LinkOnly ? 3 : 2;
            }
        }
        public int FavSort { get { return Vendor.IsFavorite ? 0 : 1; } }
        public string NameSort { get { return Vendor.Name; } }
        public decimal PriceSort { get { var q = Quote; return q == null ? decimal.MaxValue : q.UnitPrice; } }
        public decimal TotalSort { get { var q = Quote; return q == null ? decimal.MaxValue : q.Extended; } }
        public long StockSort { get { return Model == null ? -2 : Model.Stock; } }
        public int LeadSort { get { return Model == null || !Model.LeadTimeDays.HasValue ? int.MaxValue : Model.LeadTimeDays.Value; } }

        // ---- identity -----------------------------------------------------------------------
        public string Sku { get { return Model == null || string.IsNullOrEmpty(Model.Sku) ? "—" : Model.Sku; } }
        public string Mpn { get { return Model == null || string.IsNullOrEmpty(Model.Mpn) ? "—" : Model.Mpn; } }
        public string Manufacturer { get { return Model == null ? "" : Model.Manufacturer ?? ""; } }
        public string Description { get { return Model == null ? "" : Model.Description ?? ""; } }
        public string PartLine
        {
            get
            {
                if (Model == null) return "";
                var parts = new[] { Model.Mpn, Model.Manufacturer }.Where(s => !string.IsNullOrWhiteSpace(s));
                return string.Join(" · ", parts);
            }
        }
        public string Packaging { get { return Model == null ? "" : Model.Packaging ?? ""; } }
        public string Url { get { return Model != null && !string.IsNullOrEmpty(Model.Url) ? Model.Url : Vendor.SearchUrl; } }
        public bool HasUrl { get { return !string.IsNullOrEmpty(Url); } }
        public string AlternateText { get { return IsAlternate ? Loc.F("↳ Alternate: {0} — not an exact match for the number searched", Mpn) : ""; } }

        // ---- stock --------------------------------------------------------------------------
        public string StockText { get { return Model == null || Model.Stock < 0 ? Loc.T("Stock unknown") : Loc.F("{0} units", Money.Count(Model.Stock)); } }

        public string AvailabilityText
        {
            get
            {
                if (Model == null) return "";
                switch (Model.StockFor(Need))
                {
                    case StockLevel.InStock: return Loc.T("Ready to ship");
                    case StockLevel.Partial: return Loc.F("Short {0}", Money.Count(Need - Model.Stock));
                    case StockLevel.OutOfStock: return Loc.T("Out of stock");
                    default: return Loc.T("Availability unknown");
                }
            }
        }

        Tone StockTone
        {
            get
            {
                if (Model == null) return Tone.Neutral;
                switch (Model.StockFor(Need))
                {
                    case StockLevel.InStock: return Tone.Good;
                    case StockLevel.Partial: return Tone.Warn;
                    case StockLevel.OutOfStock: return Tone.Bad;
                    default: return Tone.Unsure;
                }
            }
        }
        public string AvailabilityDisplay { get { return Model == null ? "" : StockTone.Symbol + " " + AvailabilityText; } }
        public Brush ChipBack { get { return StockTone.Back; } }
        public Brush ChipFore { get { return StockTone.Fore; } }

        // ---- price --------------------------------------------------------------------------
        public string UnitPriceText { get { var q = Quote; return q == null ? Loc.T("No pricing") : Money.Format(q.UnitPrice, Model.Currency); } }
        public string TotalText { get { var q = Quote; return q == null ? "—" : Money.Total(q.Extended, Model.Currency); } }

        public string BasePriceText
        {
            get
            {
                if (!HasPricing) return "";
                var first = Model.Breaks.OrderBy(b => b.Quantity).First();
                return Loc.F("Base {0} @ {1}+", Money.Format(first.UnitPrice, Model.Currency), Money.Count(first.Quantity));
            }
        }

        public string SavingsText
        {
            get
            {
                var q = Quote;
                return q != null && q.SavingsPct >= 0.5 ? "-" + Math.Round(q.SavingsPct) + "%" : "—";
            }
        }
        public string SavingsNote { get { return Loc.F("Saves {0} vs the 1-unit price", SavingsText); } }
        public bool HasSavings { get { var q = Quote; return q != null && q.SavingsPct >= 0.5; } }

        public string OrderNote
        {
            get
            {
                var q = Quote;
                if (q == null) return "";
                var note = Loc.F("@ {0} pcs · break {1}+", Money.Count(q.OrderQuantity), Money.Count(q.BreakQuantity));
                if (q.RaisedToMinimum) note += " · " + Loc.T("order minimum applied");
                return note;
            }
        }
        public bool RaisedToMinimum { get { var q = Quote; return q != null && q.RaisedToMinimum; } }

        public string MoqText
        {
            get
            {
                if (Model == null) return "";
                return Loc.F("MOQ {0}", Money.Count(Model.MinOrder)) + (Model.OrderMultiple > 1 ? " · " + Loc.F("multiple of {0}", Money.Count(Model.OrderMultiple)) : "");
            }
        }

        // ---- lead time ----------------------------------------------------------------------
        static string Days(int d) { return d >= 14 ? Loc.F("{0} weeks", Math.Round(d / 7.0)) : Loc.N(d, "{0} day", "{0} days"); }

        public string LeadText
        {
            get
            {
                if (Model == null) return "";
                if (Model.StockFor(Need) == StockLevel.InStock) return Loc.T("Ships from stock");
                if (Model.LeadTimeDays.HasValue) return Days(Model.LeadTimeDays.Value);
                return Loc.T("Ask vendor");
            }
        }
        public string LeadSub
        {
            get
            {
                if (Model == null || !Model.LeadTimeDays.HasValue) return "";
                return Model.StockFor(Need) == StockLevel.InStock ? Loc.F("Factory lead {0}", Days(Model.LeadTimeDays.Value)) : Loc.T("Factory lead time");
            }
        }

        // ---- flags set by the view-model after each recompute -------------------------------
        public bool IsBestPrice { get { return _isBest; } set { Set(ref _isBest, value); } }
        public bool IsMaxStock { get { return _isMaxStock; } set { Set(ref _isMaxStock, value); } }
        public string PremiumText { get { return _premiumText; } set { Set(ref _premiumText, value ?? ""); } }

        public bool ShowTiers { get { return _showTiers; } set { Set(ref _showTiers, value); } }
        public string TiersToggleText { get { return HasPricing ? Loc.F("View all tier breakdowns ({0} levels)", Model.Breaks.Count) : ""; } }

        public List<TierRowVM> Tiers
        {
            get
            {
                if (!HasPricing) return new List<TierRowVM>();
                var q = Quote;
                return Model.Breaks.OrderBy(b => b.Quantity).Select(b => new TierRowVM
                {
                    Quantity = Money.Count(b.Quantity) + "+",
                    UnitPrice = Money.Format(b.UnitPrice, Model.Currency),
                    Extended = Money.Total(b.UnitPrice * b.Quantity, Model.Currency),
                    IsCurrent = q != null && b.Quantity == q.BreakQuantity
                }).ToList();
            }
        }

        public string Key { get { return Vendor.Id + "|" + (Model == null ? "" : Model.Sku); } }

        public void Reprice(int qty)
        {
            _qty = qty;
            OnPropertyChanged(string.Empty);
        }
    }

    /// <summary>Header card with the part's identity, taken from the vendor listings.</summary>
    public sealed class OverviewVM
    {
        public string Mpn { get; set; }
        public string Manufacturer { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Lifecycle { get; set; }
        public string RoHS { get; set; }
        public string DatasheetUrl { get; set; }
        public string ImageUrl { get; set; }
        public bool OnlyAlternates { get; set; }

        public bool HasImage { get { return !string.IsNullOrEmpty(ImageUrl); } }
        public bool HasDatasheet { get { return !string.IsNullOrEmpty(DatasheetUrl); } }
        public bool HasLifecycle { get { return !string.IsNullOrWhiteSpace(Lifecycle); } }
        public bool HasRoHS { get { return !string.IsNullOrWhiteSpace(RoHS); } }
        public bool HasCategory { get { return !string.IsNullOrWhiteSpace(Category); } }
        public string AlternateNote { get { return OnlyAlternates ? Loc.T("No exact match was found. Showing the closest parts the vendors returned.") : ""; } }

        Tone LifecycleTone
        {
            get
            {
                var t = (Lifecycle ?? "").ToLowerInvariant();
                if (t.Contains("obsolete") || t.Contains("end of life") || t.Contains("eol") || t.Contains("discontinued")) return Tone.Bad;
                if (t.Contains("nrnd") || t.Contains("not recommended") || t.Contains("last time") || t.Contains("not for new")) return Tone.Warn;
                return Tone.Good;
            }
        }
        public string LifecycleDisplay { get { return LifecycleTone.Symbol + " " + Lifecycle; } }
        public Brush LifecycleBack { get { return LifecycleTone.Back; } }
        public Brush LifecycleFore { get { return LifecycleTone.Fore; } }
    }

    /// <summary>Quantity preset chip (1, 10, 25 … 10k). IsActive highlights the one matching the current quantity.</summary>
    public sealed class QtyPresetVM : ObservableObject
    {
        bool _active;
        public QtyPresetVM(int value) { Value = value; }
        public int Value { get; private set; }
        public string Label { get { return Value >= 1000 && Value % 1000 == 0 ? (Value / 1000) + "k" : Value >= 1000 ? (Value / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "k" : Value.ToString(); } }
        public bool IsActive { get { return _active; } set { Set(ref _active, value); } }
    }
}
