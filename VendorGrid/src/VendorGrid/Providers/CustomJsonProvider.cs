using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.Providers
{
    /// <summary>User-defined website that exposes a JSON search endpoint. Field mapping is configured in Settings › Vendors.</summary>
    internal sealed class CustomJsonProvider : IVendorProvider
    {
        readonly VendorConfig _v;
        public CustomJsonProvider(AppSettings s, VendorConfig v) { _v = v; }

        public string Id { get { return _v.Id; } }
        public string Name { get { return string.IsNullOrWhiteSpace(_v.Name) ? "Custom vendor" : _v.Name.Trim(); } }
        public bool IsLinkOnly { get { return false; } }
        public string BuildSearchUrl(string pn) { return string.IsNullOrWhiteSpace(_v.UrlTemplate) ? null : VendorCatalog.FillTemplate(_v.UrlTemplate, pn); }

        public bool IsConfigured(out string reason)
        {
            reason = Loc.T("Set the API URL (containing {pn}) for this vendor in Settings › Vendors.");
            return !string.IsNullOrWhiteSpace(_v.ApiUrlTemplate) && _v.ApiUrlTemplate.Contains("{pn}");
        }

        public async Task<List<PartMatch>> SearchAsync(string pn, CancellationToken ct)
        {
            var url = _v.ApiUrlTemplate.Replace("{pn}", Uri.EscapeDataString(pn));
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                throw new VendorException(Loc.T("The API URL is not a valid http(s) address."));

            using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                if (!string.IsNullOrWhiteSpace(_v.AuthHeaderName))
                    req.Headers.TryAddWithoutValidation(_v.AuthHeaderName.Trim(), (_v.AuthHeaderValue ?? "").Trim());

                var root = await Http.SendJsonAsync(req, ct).ConfigureAwait(false);
                var matches = new List<PartMatch>();

                foreach (var item in Json.List(Json.Get(root, _v.ItemsPath)))
                {
                    var m = new PartMatch
                    {
                        Vendor = Name,
                        Mpn = Json.Str(item, _v.MpnPath),
                        Sku = Json.Str(item, _v.SkuPath),
                        Manufacturer = Json.Str(item, _v.ManufacturerPath),
                        Description = Json.Str(item, _v.DescriptionPath),
                        Url = Json.Str(item, _v.UrlPath),
                        Stock = Json.Long(item, _v.StockPath) ?? -1,
                        Currency = string.IsNullOrWhiteSpace(_v.Currency) ? "USD" : _v.Currency.Trim()
                    };
                    foreach (var b in Json.List(Json.Get(item, _v.BreaksPath)))
                    {
                        var qty = Json.Long(b, _v.BreakQtyField);
                        var price = Json.Decimal(b, _v.BreakPriceField);
                        if (qty != null && price != null) m.Breaks.Add(new PriceBreak { Quantity = (int)qty.Value, UnitPrice = price.Value });
                    }
                    matches.Add(m);
                }
                return MatchFilter.Prefer(pn, matches);
            }
        }
    }
}
