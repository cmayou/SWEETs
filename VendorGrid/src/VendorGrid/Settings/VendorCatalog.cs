using System;
using System.Collections.Generic;
using System.Linq;

namespace VendorGrid.Settings
{
    /// <summary>Default vendor directory plus helpers to order it and migrate settings written by VendorGrid 1.x.</summary>
    public static class VendorCatalog
    {
        const string RsDefaultUrl = "https://us.rs-online.com/catalogsearch/result/?q={pn}";
        static VendorConfig Api(string id, string kind, string name, string code, string searchUrl, bool favorite, string notes)
        {
            return new VendorConfig { Id = id, Kind = kind, Name = name, Code = code, UrlTemplate = searchUrl, Favorite = favorite, Builtin = true, Notes = notes };
        }

        static VendorConfig Link(string id, string name, string code, string searchUrl, bool enabled, string notes)
        {
            return new VendorConfig { Id = id, Kind = VendorKinds.Link, Name = name, Code = code, UrlTemplate = searchUrl, Enabled = enabled, Builtin = true, Notes = notes };
        }

        public static List<VendorConfig> BuiltIn()
        {
            return new List<VendorConfig>
            {
                Api("digikey", VendorKinds.DigiKey, "Digi-Key", "DK", "https://www.digikey.com/en/products/result?keywords={pn}", true, "Live price, stock and lead time through the Digi-Key Product Information API."),
                Api("mouser", VendorKinds.Mouser, "Mouser", "MO", "https://www.mouser.com/c/?q={pn}", true, "Live price, stock and lead time through the Mouser Search API."),
                Api("newark", VendorKinds.Newark, "Newark / Farnell", "NW", "https://www.newark.com/search?st={pn}", false, "Live price and stock through the element14 Product Search API (regional store follows your country)."),
                Link("arrow", "Arrow Electronics", "AR", "https://www.arrow.com/en/products/search?q={pn}", true, "Opens the Arrow search page."),
                Link("rs", "RS Components", "RS", "https://us.rs-online.com/catalogsearch/result/?q={pn}", true, "Opens the RS portal for your country."),
                Link("te", "TE Connectivity", "TE", "https://www.te.com/en/search.html?q={pn}", true, "Opens the TE search page."),
                Link("automationdirect", "AutomationDirect", "AD", "https://www.automationdirect.com/adc/search/search?fkey=0&keyword={pn}", true, "Industrial control, PLCs, sensors, drives."),
                Link("mcmaster", "McMaster-Carr", "MC", "https://www.mcmaster.com/{pn}", false, "Mechanical hardware and enclosures (uses McMaster part numbers)."),
                Link("grainger", "Grainger", "GR", "https://www.grainger.com/search?searchQuery={pn}", false, "Industrial supplies."),
                Link("lcsc", "LCSC Electronics", "LC", "https://www.lcsc.com/search?q={pn}", false, "Components, China."),
                Link("tme", "TME Electronic Components", "TM", "https://www.tme.eu/en/katalog/#search={pn}", false, "Components, Europe.")
            };
        }

        /// <summary>Makes sure the settings hold a complete vendor list (first run, 1.x migration, new built-ins after an update).</summary>
        public static void Ensure(AppSettings s)
        {
            if (s.Vendors == null) s.Vendors = new List<VendorConfig>();
            bool firstRun = s.Vendors.Count == 0;

            foreach (var d in BuiltIn())
                if (!s.Vendors.Any(v => v.Id == d.Id))
                {
                    if (firstRun)
                    {
                        if (d.Id == "mouser") d.Enabled = s.MouserEnabled;
                        if (d.Id == "digikey") d.Enabled = s.DigiKeyEnabled;
                        if (d.Id == "newark") d.Enabled = s.NewarkEnabled;
                    }
                    s.Vendors.Add(d);
                }

            // 1.x custom websites become "Custom API" vendors.
            if (s.CustomVendors != null && s.CustomVendors.Count > 0)
            {
                foreach (var c in s.CustomVendors)
                {
                    c.Kind = VendorKinds.Json;
                    c.Builtin = false;
                    if (string.IsNullOrEmpty(c.Id)) c.Id = "custom_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    s.Vendors.Add(c);
                }
                s.CustomVendors = new List<VendorConfig>();
            }

            // 1.x stored explicit defaults for these; from 2.0 blank means "follow the country".
            if (s.Version < 2)
            {
                if (s.Currency == "USD") s.Currency = "";
                if (s.DigiKeySite == "US") s.DigiKeySite = "";
                if (s.NewarkStore == "www.newark.com") s.NewarkStore = "";
                s.Version = 2;
            }

            // 2.0 early builds: a JSON vendor stored its API endpoint in UrlTemplate and its website in PageUrlTemplate.
            // From now on both live in one entry: UrlTemplate = website, ApiUrlTemplate = API.
            if (s.Version < 3)
            {
                foreach (var v in s.Vendors.Where(v => v.Kind == VendorKinds.Json && string.IsNullOrWhiteSpace(v.ApiUrlTemplate)))
                {
                    v.ApiUrlTemplate = v.UrlTemplate;
                    v.UrlTemplate = v.PageUrlTemplate ?? "";
                    v.PageUrlTemplate = "";
                }
                s.Version = 3;
            }

            foreach (var v in s.Vendors)
            {
                if (string.IsNullOrWhiteSpace(v.Id)) v.Id = "custom_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                if (string.IsNullOrWhiteSpace(v.Code)) v.Code = MakeCode(v.Name);
            }
        }

        /// <summary>Favourites first, then the directory order.</summary>
        public static IEnumerable<VendorConfig> Ordered(AppSettings s)
        {
            return s.Vendors.Select((v, i) => new { v, i }).OrderByDescending(x => x.v.Favorite ? 1 : 0).ThenBy(x => x.i).Select(x => x.v);
        }

        public static string MakeCode(string name)
        {
            var letters = new string((name ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            if (letters.Length == 0) return "?";
            return letters.Length <= 2 ? letters : letters.Substring(0, 2);
        }

        /// <summary>Search page of a link-only vendor, honouring the selected country for RS Components.</summary>
        public static string ResolveSearchTemplate(VendorConfig v, AppSettings s)
        {
            if (v.Id == "rs" && (string.IsNullOrWhiteSpace(v.UrlTemplate) || v.UrlTemplate == RsDefaultUrl)) return s.Country.RsSearchUrl;
            return v.UrlTemplate;
        }

        public static string FillTemplate(string template, string pn)
        {
            return (template ?? "").Replace("{pn}", Uri.EscapeDataString(pn ?? ""));
        }
    }
}
