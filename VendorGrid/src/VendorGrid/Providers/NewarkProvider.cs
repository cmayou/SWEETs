using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.Providers
{
    /// <summary>Newark / Farnell / element14 Product Search API (manufacturer part number search). The regional store follows the selected country.</summary>
    internal sealed class NewarkProvider : IVendorProvider
    {
        readonly AppSettings _s;
        readonly VendorConfig _v;
        public NewarkProvider(AppSettings s, VendorConfig v) { _s = s; _v = v; }

        public string Id { get { return _v.Id; } }
        public string Name { get { return string.IsNullOrWhiteSpace(_v.Name) ? "Newark" : _v.Name.Trim(); } }
        public bool IsLinkOnly { get { return false; } }
        public string BuildSearchUrl(string pn) { return "https://" + _s.EffectiveNewarkStore + "/search?st=" + Uri.EscapeDataString(pn ?? ""); }

        public bool IsConfigured(out string reason)
        {
            reason = Loc.T("Add your Newark (element14) API key in Settings › Vendors › Newark / Farnell.");
            return !string.IsNullOrWhiteSpace(_s.NewarkApiKey);
        }

        public async Task<List<PartMatch>> SearchAsync(string pn, CancellationToken ct)
        {
            var store = _s.EffectiveNewarkStore;
            var url = "https://api.element14.com/catalog/products"
                + "?term=" + Uri.EscapeDataString("manuPartNum:" + pn)
                + "&storeInfo.id=" + Uri.EscapeDataString(store)
                + "&resultsSettings.offset=0&resultsSettings.numberOfResults=8"
                + "&resultsSettings.responseGroup=large&callInfo.responseDataFormat=json"
                + "&callInfo.apiKey=" + Uri.EscapeDataString(_s.NewarkApiKey.Trim());

            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var root = await Http.SendJsonAsync(req, ct).ConfigureAwait(false);

                var fault = Json.Str(root, "Fault.Reason");
                if (!string.IsNullOrEmpty(fault)) throw new VendorException(Loc.F("Newark: {0}", fault));

                var currency = _s.EffectiveCurrency;
                var matches = new List<PartMatch>();
                foreach (var p in Json.List(Json.Get(root, "manufacturerPartNumberSearchReturn.products")))
                {
                    var mpn = Json.Str(p, "translatedManufacturerPartNumber");
                    var m = new PartMatch
                    {
                        Vendor = Name,
                        Sku = Json.Str(p, "sku"),
                        Mpn = mpn,
                        Manufacturer = Json.Str(p, "brandName"),
                        Description = Json.Str(p, "displayName"),
                        Stock = Json.Long(p, "stock.level") ?? Json.Long(p, "inv") ?? -1,
                        MinOrder = (int)(Json.Long(p, "translatedMinimumOrderQuality") ?? 1),
                        OrderMultiple = (int)(Json.Long(p, "packSize") ?? 1),
                        LeadTimeDays = (int?)Json.Long(p, "stock.leastLeadTime"),
                        DatasheetUrl = Json.Str(p, "datasheets.0.url"),
                        RoHS = Json.Str(p, "rohsStatusCode"),
                        Url = Json.Str(p, "productURL") ?? BuildSearchUrl(mpn ?? pn),
                        Currency = currency
                    };
                    foreach (var b in Json.List(Json.Get(p, "prices")))
                    {
                        var qty = Json.Long(b, "from");
                        var price = Json.Decimal(b, "cost");
                        if (qty != null && price != null) m.Breaks.Add(new PriceBreak { Quantity = (int)qty.Value, UnitPrice = price.Value });
                    }
                    matches.Add(m);
                }
                return MatchFilter.Prefer(pn, matches);
            }
        }
    }
}
