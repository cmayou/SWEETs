using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.Providers
{
    /// <summary>Mouser Search API v1 (part number search). Key: https://www.mouser.com/api-search/</summary>
    internal sealed class MouserProvider : IVendorProvider
    {
        readonly AppSettings _s;
        readonly VendorConfig _v;
        public MouserProvider(AppSettings s, VendorConfig v) { _s = s; _v = v; }

        public string Id { get { return _v.Id; } }
        public string Name { get { return string.IsNullOrWhiteSpace(_v.Name) ? "Mouser" : _v.Name.Trim(); } }
        public bool IsLinkOnly { get { return false; } }
        public string BuildSearchUrl(string pn) { return VendorCatalog.FillTemplate(_v.UrlTemplate, pn); }

        public bool IsConfigured(out string reason)
        {
            reason = Loc.T("Add your Mouser API key in Settings › Vendors › Mouser.");
            return !string.IsNullOrWhiteSpace(_s.MouserApiKey);
        }

        public async Task<List<PartMatch>> SearchAsync(string pn, CancellationToken ct)
        {
            var parts = await Query(pn, "Exact", ct).ConfigureAwait(false);
            if (parts.Count == 0) parts = await Query(pn, null, ct).ConfigureAwait(false);

            var matches = new List<PartMatch>();
            foreach (var p in parts)
            {
                var m = new PartMatch
                {
                    Vendor = Name,
                    Sku = Json.Str(p, "MouserPartNumber"),
                    Mpn = Json.Str(p, "ManufacturerPartNumber"),
                    Manufacturer = Json.Str(p, "Manufacturer"),
                    Description = Json.Str(p, "Description"),
                    Url = Json.Str(p, "ProductDetailUrl"),
                    Category = Json.Str(p, "Category"),
                    Lifecycle = Json.Str(p, "LifecycleStatus"),
                    RoHS = Json.Str(p, "ROHSStatus"),
                    DatasheetUrl = Json.Str(p, "DataSheetUrl"),
                    ImageUrl = Json.Str(p, "ImagePath"),
                    MinOrder = (int)(Json.Long(p, "Min") ?? 1),
                    OrderMultiple = (int)(Json.Long(p, "Mult") ?? 1),
                    LeadTimeDays = MatchFilter.ParseLeadDays(Json.Str(p, "LeadTime"), false)
                };

                var inStock = Json.Long(p, "AvailabilityInStock");
                if (inStock == null)
                {
                    var avail = Json.Str(p, "Availability");
                    inStock = avail != null && avail.IndexOf("none", StringComparison.OrdinalIgnoreCase) >= 0 ? 0 : Json.Long(p, "Availability");
                }
                m.Stock = inStock ?? -1;

                foreach (var b in Json.List(Json.Get(p, "PriceBreaks")))
                {
                    var qty = Json.Long(b, "Quantity");
                    var price = Json.Decimal(b, "Price");
                    if (qty == null || price == null) continue;
                    m.Breaks.Add(new PriceBreak { Quantity = (int)qty.Value, UnitPrice = price.Value });
                    var cur = Json.Str(b, "Currency");
                    if (!string.IsNullOrEmpty(cur)) m.Currency = cur;
                }
                matches.Add(m);
            }
            return MatchFilter.Prefer(pn, matches);
        }

        async Task<List<object>> Query(string pn, string option, CancellationToken ct)
        {
            var request = new Dictionary<string, object> { { "mouserPartNumber", pn } };
            if (option != null) request["partSearchOptions"] = option;
            var body = Json.Serialize(new Dictionary<string, object> { { "SearchByPartRequest", request } });

            var url = "https://api.mouser.com/api/v1/search/partnumber?apiKey=" + Uri.EscapeDataString(_s.MouserApiKey.Trim());
            using (var req = new HttpRequestMessage(HttpMethod.Post, url))
            {
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                var root = await Http.SendJsonAsync(req, ct).ConfigureAwait(false);

                var errors = Json.List(Json.Get(root, "Errors"));
                if (errors.Count > 0 && option == null)
                    throw new VendorException(Json.Str(errors[0], "Message") ?? Loc.T("Mouser returned an error."));

                return Json.List(Json.Get(root, "SearchResults.Parts"));
            }
        }
    }
}
