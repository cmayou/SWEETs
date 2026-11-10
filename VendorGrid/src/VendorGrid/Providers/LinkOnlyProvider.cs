using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.Providers
{
    /// <summary>
    /// A website without a data API (or one the user has not connected). It never returns prices; the result row offers a
    /// one-click link to the vendor's own search page for the part number.
    /// </summary>
    internal sealed class LinkOnlyProvider : IVendorProvider
    {
        readonly AppSettings _s;
        readonly VendorConfig _v;
        public LinkOnlyProvider(AppSettings s, VendorConfig v) { _s = s; _v = v; }

        public string Id { get { return _v.Id; } }
        public string Name { get { return string.IsNullOrWhiteSpace(_v.Name) ? "Website" : _v.Name.Trim(); } }
        public bool IsLinkOnly { get { return true; } }

        public string BuildSearchUrl(string pn)
        {
            return VendorCatalog.FillTemplate(VendorCatalog.ResolveSearchTemplate(_v, _s), pn);
        }

        public bool IsConfigured(out string reason) { reason = null; return true; }

        public Task<List<PartMatch>> SearchAsync(string pn, CancellationToken ct)
        {
            return Task.FromResult(new List<PartMatch>());
        }
    }
}
