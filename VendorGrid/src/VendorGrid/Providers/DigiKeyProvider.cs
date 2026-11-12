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
    /// <summary>DigiKey Product Information API v4 (OAuth2 client credentials). Register at developer.digikey.com.</summary>
    internal sealed class DigiKeyProvider : IVendorProvider
    {
        static readonly SemaphoreSlim TokenGate = new SemaphoreSlim(1, 1);
        static string _token, _tokenOwner;
        static DateTime _tokenExpiryUtc = DateTime.MinValue;

        readonly AppSettings _s;
        readonly VendorConfig _v;
        public DigiKeyProvider(AppSettings s, VendorConfig v) { _s = s; _v = v; }

        public string Id { get { return _v.Id; } }
        public string Name { get { return string.IsNullOrWhiteSpace(_v.Name) ? "Digi-Key" : _v.Name.Trim(); } }
        public bool IsLinkOnly { get { return false; } }
        public string BuildSearchUrl(string pn) { return VendorCatalog.FillTemplate(_v.UrlTemplate, pn); }

        public bool IsConfigured(out string reason)
        {
            reason = Loc.T("Add your Digi-Key Client ID and Client Secret in Settings › Vendors › Digi-Key.");
            return !string.IsNullOrWhiteSpace(_s.DigiKeyClientId) && !string.IsNullOrWhiteSpace(_s.DigiKeyClientSecret);
        }

        public async Task<List<PartMatch>> SearchAsync(string pn, CancellationToken ct)
        {
            var token = await GetTokenAsync(ct).ConfigureAwait(false);
            var body = Json.Serialize(new Dictionary<string, object> { { "Keywords", pn }, { "Limit", 10 }, { "Offset", 0 } });
            var currency = _s.EffectiveCurrency;

            using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.digikey.com/products/v4/search/keyword"))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                req.Headers.TryAddWithoutValidation("X-DIGIKEY-Client-Id", _s.DigiKeyClientId.Trim());
                req.Headers.TryAddWithoutValidation("X-DIGIKEY-Locale-Site", _s.EffectiveDigiKeySite);
                req.Headers.TryAddWithoutValidation("X-DIGIKEY-Locale-Language", "en");
                req.Headers.TryAddWithoutValidation("X-DIGIKEY-Locale-Currency", currency);
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");

                var root = await Http.SendJsonAsync(req, ct).ConfigureAwait(false);
                var matches = new List<PartMatch>();

                foreach (var p in Json.List(Json.Get(root, "Products")))
                {
                    var mpn = Json.Str(p, "ManufacturerProductNumber");
                    var mfr = Json.Str(p, "Manufacturer.Name");
                    var desc = Json.Str(p, "Description.ProductDescription");
                    var url = Json.Str(p, "ProductUrl");
                    var category = Json.Str(p, "Category.Name");
                    var lifecycle = Json.Str(p, "ProductStatus.Status");
                    var rohs = Json.Str(p, "Classifications.RohsStatus");
                    var datasheet = Json.Str(p, "DatasheetUrl");
                    var photo = Json.Str(p, "PhotoUrl");
                    var lead = MatchFilter.ParseLeadDays(Json.Str(p, "ManufacturerLeadWeeks"), true);

                    foreach (var v in Json.List(Json.Get(p, "ProductVariations")))
                    {
                        var m = new PartMatch
                        {
                            Vendor = Name, Mpn = mpn, Manufacturer = mfr, Description = desc, Url = url,
                            Category = category, Lifecycle = lifecycle, RoHS = rohs, DatasheetUrl = datasheet, ImageUrl = photo,
                            LeadTimeDays = lead,
                            Sku = Json.Str(v, "DigiKeyProductNumber"),
                            Packaging = Json.Str(v, "PackageType.Name"),
                            Stock = Json.Long(v, "QuantityAvailableforPackageType") ?? Json.Long(p, "QuantityAvailable") ?? -1,
                            MinOrder = (int)(Json.Long(v, "MinimumOrderQuantity") ?? 1),
                            Currency = currency
                        };
                        foreach (var b in Json.List(Json.Get(v, "StandardPricing")))
                        {
                            var qty = Json.Long(b, "BreakQuantity");
                            var price = Json.Decimal(b, "UnitPrice");
                            if (qty != null && price != null) m.Breaks.Add(new PriceBreak { Quantity = (int)qty.Value, UnitPrice = price.Value });
                        }
                        matches.Add(m);
                    }
                }
                return MatchFilter.Prefer(pn, matches, 8);
            }
        }

        async Task<string> GetTokenAsync(CancellationToken ct)
        {
            await TokenGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var owner = _s.DigiKeyClientId + "|" + _s.DigiKeyClientSecret;
                if (_token != null && _tokenOwner == owner && DateTime.UtcNow < _tokenExpiryUtc) return _token;

                using (var req = new HttpRequestMessage(HttpMethod.Post, "https://api.digikey.com/v1/oauth2/token"))
                {
                    req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        { "client_id", _s.DigiKeyClientId.Trim() },
                        { "client_secret", _s.DigiKeyClientSecret.Trim() },
                        { "grant_type", "client_credentials" }
                    });
                    var root = await Http.SendJsonAsync(req, ct).ConfigureAwait(false);
                    var token = Json.Str(root, "access_token");
                    if (string.IsNullOrEmpty(token)) throw new VendorException(Loc.T("Digi-Key did not return an access token."));
                    var seconds = Json.Long(root, "expires_in") ?? 540;
                    _token = token; _tokenOwner = owner; _tokenExpiryUtc = DateTime.UtcNow.AddSeconds(Math.Max(30, seconds - 30));
                    return _token;
                }
            }
            finally { TokenGate.Release(); }
        }
    }
}
