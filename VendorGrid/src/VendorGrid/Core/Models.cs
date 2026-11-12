using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace VendorGrid.Core
{
    public sealed class PriceBreak
    {
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    public enum StockLevel { Unknown, InStock, Partial, OutOfStock }

    public sealed class PriceQuote
    {
        public int OrderQuantity { get; set; }       // quantity actually priced (respects MOQ and order multiple)
        public int BreakQuantity { get; set; }       // price break that applies
        public decimal UnitPrice { get; set; }
        public decimal Extended { get; set; }
        public bool RaisedToMinimum { get; set; }
        public double SavingsPct { get; set; }       // vs the lowest-quantity break
    }

    /// <summary>One purchasable listing (a vendor SKU) for the searched part. Serialisable (BOM lines store a snapshot).</summary>
    public sealed class PartMatch
    {
        public string VendorId { get; set; }
        public string Vendor { get; set; }
        public string Sku { get; set; }
        public string Mpn { get; set; }
        public string Manufacturer { get; set; }
        public string Description { get; set; }
        public string Packaging { get; set; }
        public string Url { get; set; }
        public string Currency { get; set; } = "USD";
        public string Category { get; set; }
        public string Lifecycle { get; set; }
        public string RoHS { get; set; }
        public string DatasheetUrl { get; set; }
        public string ImageUrl { get; set; }
        public long Stock { get; set; } = -1;          // -1 = unknown
        public int MinOrder { get; set; } = 1;
        public int OrderMultiple { get; set; } = 1;
        public int? LeadTimeDays { get; set; }
        public bool IsAlternate { get; set; }          // vendor returned a similar part, not the exact number searched
        public List<PriceBreak> Breaks { get; set; } = new List<PriceBreak>();

        [ScriptIgnore]
        public bool HasPricing { get { return Breaks != null && Breaks.Count > 0; } }

        public StockLevel StockFor(int qty)
        {
            if (Stock < 0) return StockLevel.Unknown;
            if (Stock == 0) return StockLevel.OutOfStock;
            return Stock >= qty ? StockLevel.InStock : StockLevel.Partial;
        }

        public PriceQuote Quote(int qty)
        {
            if (!HasPricing) return null;
            var sorted = Breaks.OrderBy(b => b.Quantity).ToList();
            int floor = Math.Max(Math.Max(MinOrder, 1), sorted[0].Quantity);
            int order = Math.Max(qty, floor);
            if (OrderMultiple > 1)
            {
                int rest = order % OrderMultiple;
                if (rest != 0) order += OrderMultiple - rest;
            }
            var applicable = sorted.LastOrDefault(b => b.Quantity <= order) ?? sorted[0];
            decimal baseUnit = sorted[0].UnitPrice;
            return new PriceQuote
            {
                OrderQuantity = order,
                BreakQuantity = applicable.Quantity,
                UnitPrice = applicable.UnitPrice,
                Extended = applicable.UnitPrice * order,
                RaisedToMinimum = order > qty,
                SavingsPct = baseUnit > 0 ? (double)((baseUnit - applicable.UnitPrice) / baseUnit * 100m) : 0
            };
        }
    }

    public enum VendorState { Idle, Searching, Found, NotFound, NotConfigured, Error, LinkOnly }

    public sealed class VendorOutcome
    {
        public string VendorId { get; set; }
        public string Vendor { get; set; }
        public VendorState State { get; set; }
        public string Message { get; set; }
        public string SearchUrl { get; set; }          // vendor website search page for this part number
        public long LatencyMs { get; set; }
        public List<PartMatch> Matches { get; set; } = new List<PartMatch>();
        public bool IsTerminal { get { return State != VendorState.Searching && State != VendorState.Idle; } }
    }
}
