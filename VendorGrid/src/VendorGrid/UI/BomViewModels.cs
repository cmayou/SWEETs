using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.UI
{
    /// <summary>A staged purchase line: a vendor listing plus an editable quantity, re-quoted from the stored price breaks.</summary>
    public sealed class BomLineVM : ObservableObject
    {
        string _qtyText;

        public BomLineVM(BomLine line)
        {
            Line = line;
            _qtyText = line.Quantity.ToString();
        }

        public BomLine Line { get; private set; }
        public PartMatch Part { get { return Line.Part; } }
        public event Action Changed;

        public string QtyText
        {
            get { return _qtyText; }
            set
            {
                if (!Set(ref _qtyText, value ?? "")) return;
                int q;
                if (!int.TryParse(_qtyText.Replace(",", "").Trim(), out q) || q < 1) return;
                Line.Quantity = Math.Min(q, 10000000);
                OnPropertyChanged("UnitText");
                OnPropertyChanged("TotalText");
                OnPropertyChanged("StockText");
                OnPropertyChanged("StockBack");
                OnPropertyChanged("StockFore");
                OnPropertyChanged("OrderNote");
                var h = Changed;
                if (h != null) h();
            }
        }

        public PriceQuote Quote { get { return Part.Quote(Line.Quantity); } }
        public decimal ExtendedValue { get { var q = Quote; return q == null ? 0m : q.Extended; } }
        public string Currency { get { return Part.Currency; } }

        public string VendorName { get { return Part.Vendor; } }
        public string Code { get { return VendorCatalog.MakeCode(Part.Vendor); } }
        public Brush LogoBrush { get { return Palette.ForVendor(Part.VendorId); } }
        public System.Windows.Media.ImageSource LogoImage { get { return VendorLogos.Get(Part.VendorId); } }
        public bool HasLogo { get { return LogoImage != null; } }
        public bool NoLogo { get { return LogoImage == null; } }
        public string Sku { get { return string.IsNullOrEmpty(Part.Sku) ? "—" : Part.Sku; } }
        public string Mpn { get { return string.IsNullOrEmpty(Part.Mpn) ? "—" : Part.Mpn; } }
        public string Manufacturer { get { return Part.Manufacturer ?? ""; } }
        public string Description { get { return Part.Description ?? ""; } }
        public string Url { get { return Part.Url; } }
        public bool HasUrl { get { return !string.IsNullOrEmpty(Part.Url); } }

        public string UnitText { get { var q = Quote; return q == null ? Loc.T("No pricing") : Money.Format(q.UnitPrice, Part.Currency); } }
        public string TotalText { get { var q = Quote; return q == null ? "—" : Money.Total(q.Extended, Part.Currency); } }
        public string OrderNote
        {
            get
            {
                var q = Quote;
                return q != null && q.OrderQuantity != Line.Quantity ? Loc.F("Quoted at {0} (order minimum / multiple)", Money.Count(q.OrderQuantity)) : "";
            }
        }

        public string StockText
        {
            get
            {
                switch (Part.StockFor(Line.Quantity))
                {
                    case StockLevel.InStock: return Loc.F("Ready · {0} in stock", Money.Count(Part.Stock));
                    case StockLevel.Partial: return Loc.F("Partial · {0} in stock", Money.Count(Part.Stock));
                    case StockLevel.OutOfStock: return Loc.T("Out of stock");
                    default: return Loc.T("Stock unknown");
                }
            }
        }
        Tone StockTone
        {
            get
            {
                switch (Part.StockFor(Line.Quantity))
                {
                    case StockLevel.InStock: return Tone.Good;
                    case StockLevel.Partial: return Tone.Warn;
                    case StockLevel.OutOfStock: return Tone.Bad;
                    default: return Tone.Unsure;
                }
            }
        }
        public string StockDisplay { get { return StockTone.Symbol + " " + StockText; } }
        public Brush StockBack { get { return StockTone.Back; } }
        public Brush StockFore { get { return StockTone.Fore; } }
    }

    public sealed class BomVendorTotalVM
    {
        public string Vendor { get; set; }
        public string Total { get; set; }
        public string Lines { get; set; }
    }

    /// <summary>State of the "BOM staging" tab: lines, totals per vendor purchase order.</summary>
    public sealed class BomVM : ObservableObject
    {
        public ObservableCollection<BomLineVM> Lines { get; } = new ObservableCollection<BomLineVM>();
        public ObservableCollection<BomVendorTotalVM> VendorTotals { get; } = new ObservableCollection<BomVendorTotalVM>();

        string _totalText = "—";
        string _summaryText = "";
        public string TotalText { get { return _totalText; } private set { Set(ref _totalText, value); } }
        public string SummaryText { get { return _summaryText; } private set { Set(ref _summaryText, value); } }
        public string TitleText { get { return Loc.N(Lines.Count, "Procurement cart and staged components ({0} line)", "Procurement cart and staged components ({0} lines)"); } }
        public bool HasLines { get { return Lines.Count > 0; } }
        public bool IsEmpty { get { return Lines.Count == 0; } }
        public int Count { get { return Lines.Count; } }

        /// <summary>Raised after any change so the owner can persist the BOM.</summary>
        public event Action Changed;

        public void Load(IEnumerable<BomLine> lines)
        {
            Lines.Clear();
            foreach (var l in lines.Where(l => l != null && l.Part != null)) Attach(new BomLineVM(l));
            Refresh(false);
        }

        public void Add(PartMatch part, int quantity)
        {
            var existing = Lines.FirstOrDefault(l => l.Part.VendorId == part.VendorId && l.Part.Sku == part.Sku);
            if (existing != null)
            {
                existing.QtyText = (existing.Line.Quantity + quantity).ToString();
                return;
            }
            Attach(new BomLineVM(new BomLine { Part = part, Quantity = Math.Max(1, quantity) }));
            Refresh(true);
        }

        public void Remove(BomLineVM line) { if (Lines.Remove(line)) Refresh(true); }
        public void Clear() { Lines.Clear(); Refresh(true); }

        public List<BomLine> Snapshot() { return Lines.Select(l => l.Line).ToList(); }

        void Attach(BomLineVM vm)
        {
            vm.Changed += () => Refresh(true);
            Lines.Add(vm);
        }

        void Refresh(bool notify)
        {
            VendorTotals.Clear();
            foreach (var g in Lines.GroupBy(l => l.VendorName + "|" + l.Currency))
            {
                var first = g.First();
                VendorTotals.Add(new BomVendorTotalVM
                {
                    Vendor = Loc.F("{0} PO", first.VendorName),
                    Total = Money.Total(g.Sum(l => l.ExtendedValue), first.Currency),
                    Lines = Loc.N(g.Count(), "{0} line", "{0} lines")
                });
            }

            var byCurrency = Lines.GroupBy(l => l.Currency).Select(g => Money.Total(g.Sum(l => l.ExtendedValue), g.Key)).ToList();
            TotalText = byCurrency.Count == 0 ? "—" : string.Join("  +  ", byCurrency);
            SummaryText = Lines.Count == 0 ? "" : Loc.F("Across {0} components on {1}", Money.Count(Lines.Sum(l => (long)l.Line.Quantity)), Loc.N(Lines.Count, "{0} line", "{0} lines"));

            OnPropertyChanged("TitleText");
            OnPropertyChanged("HasLines");
            OnPropertyChanged("IsEmpty");
            OnPropertyChanged("Count");
            if (notify) { var h = Changed; if (h != null) h(); }
        }
    }
}
