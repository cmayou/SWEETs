using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using VendorGrid.Core;
using VendorGrid.UI;

namespace VendorGrid.Settings
{
    public static class VendorKinds
    {
        public const string Mouser = "Mouser";     // built-in data API
        public const string DigiKey = "DigiKey";   // built-in data API
        public const string Newark = "Newark";     // built-in data API
        public const string Json = "Json";         // user-defined website with a JSON search endpoint
        public const string Link = "Link";         // website without an API: opens its search page
    }

    /// <summary>
    /// A vendor in the directory: built-in API vendors, link-only websites and user-defined JSON websites.
    /// Plain public properties so it round-trips through JSON. Field names of the JSON mapping match VendorGrid 1.x custom vendors.
    /// </summary>
    public sealed class VendorConfig : ObservableObject
    {
        bool _enabled = true;
        bool _favorite;
        string _name = "New vendor";

        public string Id { get; set; } = "";
        string _kind = VendorKinds.Link;
        /// <summary>Mouser / DigiKey / Newark (built-in API), Json (custom API) or Link (search page only). Link and Json can be switched freely.</summary>
        public string Kind
        {
            get { return _kind; }
            set
            {
                if (!Set(ref _kind, value ?? VendorKinds.Link)) return;
                OnPropertyChanged("IsJson"); OnPropertyChanged("IsLink"); OnPropertyChanged("IsApiBuiltin");
                OnPropertyChanged("IsEditable"); OnPropertyChanged("KindLabel"); OnPropertyChanged("UseApi");
                OnPropertyChanged("IsMouser"); OnPropertyChanged("IsDigiKey"); OnPropertyChanged("IsNewark");
            }
        }
        public string Code { get; set; } = "";
        public bool Builtin { get; set; }
        public string Notes { get; set; } = "";

        public bool Enabled { get { return _enabled; } set { Set(ref _enabled, value); } }
        public bool Favorite { get { return _favorite; } set { Set(ref _favorite, value); } }
        public string Name { get { return _name; } set { Set(ref _name, value); } }

        /// <summary>The vendor's website search page ({pn} = part number). Used for the "Open site" link and for link-only vendors.</summary>
        public string UrlTemplate { get; set; } = "https://example.com/search?q={pn}";
        /// <summary>Optional JSON data endpoint ({pn} = part number). Used when "live API" is switched on.</summary>
        public string ApiUrlTemplate { get; set; } = "";
        /// <summary>Settings written before the website link and the API were merged stored the website link here. Read once by VendorCatalog.Ensure.</summary>
        public string PageUrlTemplate { get; set; } = "";
        public string AuthHeaderName { get; set; } = "";
        public string AuthHeaderValue { get; set; } = "";
        public string Currency { get; set; } = "USD";

        public string ItemsPath { get; set; } = "products";
        public string MpnPath { get; set; } = "mpn";
        public string SkuPath { get; set; } = "sku";
        public string ManufacturerPath { get; set; } = "manufacturer";
        public string DescriptionPath { get; set; } = "description";
        public string StockPath { get; set; } = "stock";
        public string UrlPath { get; set; } = "url";
        public string BreaksPath { get; set; } = "prices";
        public string BreakQtyField { get; set; } = "quantity";
        public string BreakPriceField { get; set; } = "price";

        [ScriptIgnore] public bool IsJson { get { return Kind == VendorKinds.Json; } }
        [ScriptIgnore] public bool IsLink { get { return Kind == VendorKinds.Link; } }
        [ScriptIgnore] public bool IsMouser { get { return Kind == VendorKinds.Mouser; } }
        [ScriptIgnore] public bool IsDigiKey { get { return Kind == VendorKinds.DigiKey; } }
        [ScriptIgnore] public bool IsNewark { get { return Kind == VendorKinds.Newark; } }
        /// <summary>For editable vendors: true = pull live price/stock from the data API, false = website link only. One vendor, one entry.</summary>
        [ScriptIgnore] public bool UseApi { get { return Kind == VendorKinds.Json; } set { Kind = value ? VendorKinds.Json : VendorKinds.Link; } }
        [ScriptIgnore] public bool IsApiBuiltin { get { return Kind == VendorKinds.Mouser || Kind == VendorKinds.DigiKey || Kind == VendorKinds.Newark; } }
        [ScriptIgnore] public bool CanDelete { get { return !Builtin; } }
        [ScriptIgnore] public bool IsCustom { get { return !Builtin; } }
        /// <summary>Everything except the three built-in API vendors can be edited (name, URL, connection type).</summary>
        [ScriptIgnore] public bool IsEditable { get { return !IsApiBuiltin; } }
        [ScriptIgnore] public bool IsBuiltinVendor { get { return Builtin; } }
        [ScriptIgnore]
        public string KindLabel
        {
            get
            {
                if (IsApiBuiltin) return Loc.T("Built-in API");
                return IsJson ? Loc.T("Website + live API") : Loc.T("Website link");
            }
        }
        [ScriptIgnore] public string NotesText { get { return Loc.T(Notes ?? ""); } }
        [ScriptIgnore] public string DisplayCode { get { return string.IsNullOrWhiteSpace(Code) ? VendorCatalog.MakeCode(Name) : Code; } }
    }

    /// <summary>A part staged for purchase. Keeps a snapshot of the listing so the price can be re-quoted when the quantity changes.</summary>
    public sealed class BomLine
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Quantity { get; set; } = 1;
        public PartMatch Part { get; set; }
        public string AddedUtc { get; set; } = DateTime.UtcNow.ToString("o");
    }

    /// <summary>Everything persisted by the add-in (encrypted per Windows user).</summary>
    public sealed class AppSettings
    {
        public int Version { get; set; } = 1;   // 1 = file written by VendorGrid 1.x (or brand new); VendorCatalog.Ensure upgrades it to 2

        // Not used since VendorGrid became part of SWEETs (the SWEETs add-in connects to SOLIDWORKS Electrical with its own
        // key). Kept so settings files written by VendorGrid 2.0 keep loading and saving unchanged.
        public string SolidWorksLicenseKey { get; set; } = "";

        /// <summary>"" = automatic (match SOLIDWORKS Electrical), otherwise a language code such as "fr".</summary>
        public string Language { get; set; } = "";

        public string CountryCode { get; set; } = "US";
        // Optional regional overrides. Blank = taken from the selected country.
        public string Currency { get; set; } = "";
        public string DigiKeySite { get; set; } = "";
        public string NewarkStore { get; set; } = "";

        public string MouserApiKey { get; set; } = "";
        public string DigiKeyClientId { get; set; } = "";
        public string DigiKeyClientSecret { get; set; } = "";
        public string NewarkApiKey { get; set; } = "";

        public bool ShowStatusBar { get; set; } = true;
        public int LastQuantity { get; set; } = 100;

        public List<VendorConfig> Vendors { get; set; } = new List<VendorConfig>();
        public List<string> RecentParts { get; set; } = new List<string>();
        public List<BomLine> Bom { get; set; } = new List<BomLine>();

        // ---- 1.x fields, read once to migrate old settings files -------------------------------
        public bool MouserEnabled { get; set; } = true;
        public bool DigiKeyEnabled { get; set; } = true;
        public bool NewarkEnabled { get; set; } = true;
        public List<VendorConfig> CustomVendors { get; set; } = new List<VendorConfig>();

        [ScriptIgnore] public Country Country { get { return Regions.Find(CountryCode); } }
        [ScriptIgnore] public string EffectiveCurrency { get { return string.IsNullOrWhiteSpace(Currency) ? Country.Currency : Currency.Trim().ToUpperInvariant(); } }
        [ScriptIgnore] public string EffectiveDigiKeySite { get { return string.IsNullOrWhiteSpace(DigiKeySite) ? Country.DigiKeySite : DigiKeySite.Trim().ToUpperInvariant(); } }
        [ScriptIgnore] public string EffectiveNewarkStore { get { return string.IsNullOrWhiteSpace(NewarkStore) ? Country.NewarkStore : NewarkStore.Trim(); } }
    }
}
