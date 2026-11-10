using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using VendorGrid.Core;
using VendorGrid.Settings;

namespace VendorGrid.UI
{
    /// <summary>Edit copy of the settings plus the derived text the General tab shows.</summary>
    public sealed class SettingsEditVM : ObservableObject
    {
        VendorConfig _selected;
        string _testPart = "";
        string _testResult = "";
        bool _testing;

        public SettingsEditVM(AppSettings settings)
        {
            Settings = settings;
            Vendors = new ObservableCollection<VendorConfig>(settings.Vendors);
            _selected = Vendors.FirstOrDefault();
            BuildLanguages();
        }

        public string TestPart { get { return _testPart; } set { Set(ref _testPart, value ?? ""); } }
        public string TestResult { get { return _testResult; } set { Set(ref _testResult, value ?? ""); } }
        public bool IsTesting { get { return _testing; } set { if (Set(ref _testing, value)) OnPropertyChanged("CanTest"); } }
        public bool CanTest { get { return !_testing; } }

        public AppSettings Settings { get; private set; }
        public ObservableCollection<VendorConfig> Vendors { get; private set; }
        public IList<Country> Countries { get { return Regions.All; } }

        public VendorConfig SelectedVendor { get { return _selected; } set { Set(ref _selected, value); } }

        public string CountryCode
        {
            get { return Settings.CountryCode; }
            set { Settings.CountryCode = value; OnPropertyChanged(); RaiseRegion(); }
        }
        // ---- language ----------------------------------------------------------------------------
        public IList<LanguageOption> Languages { get; private set; }

        /// <summary>"auto" = follow SOLIDWORKS Electrical.</summary>
        public string LanguageCode
        {
            get { return string.IsNullOrWhiteSpace(Settings.Language) ? "auto" : Settings.Language; }
            set { Settings.Language = value == "auto" ? "" : (value ?? ""); OnPropertyChanged(); }
        }

        public string LanguageStatus
        {
            get { return Loc.F("Currently using {0}, taken from {1}.", LanguageOption.NameOf(Loc.Language), Loc.T(Loc.Source)); }
        }

        void BuildLanguages()
        {
            var list = new List<LanguageOption> { new LanguageOption("auto", Loc.T("Automatic (match SOLIDWORKS Electrical)")) };
            foreach (var code in Loc.SupportedLanguages) list.Add(new LanguageOption(code, LanguageOption.NameOf(code)));
            Languages = list;
        }

        public string CurrencyOverride { get { return Settings.Currency; } set { Settings.Currency = value ?? ""; RaiseRegion(); } }
        public string DigiKeyOverride { get { return Settings.DigiKeySite; } set { Settings.DigiKeySite = value ?? ""; RaiseRegion(); } }
        public string NewarkOverride { get { return Settings.NewarkStore; } set { Settings.NewarkStore = value ?? ""; RaiseRegion(); } }

        public string RegionSummary
        {
            get
            {
                var c = Settings.Country;
                return Loc.F("Currency {0}  ·  Digi-Key locale {1}  ·  Newark store {2}  ·  RS portal {3}",
                    Settings.EffectiveCurrency, Settings.EffectiveDigiKeySite, Settings.EffectiveNewarkStore, c.RsDomain);
            }
        }

        void RaiseRegion() { OnPropertyChanged("RegionSummary"); }

        public VendorConfig AddVendor()
        {
            var v = new VendorConfig
            {
                Id = "custom_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Kind = VendorKinds.Link,
                Builtin = false,
                Name = Loc.T("New vendor"),
                UrlTemplate = "https://example.com/search?q={pn}",
                ApiUrlTemplate = "https://example.com/api/search?q={pn}"
            };
            Vendors.Add(v);
            SelectedVendor = v;
            return v;
        }

        /// <summary>Restores a built-in vendor (name, URL, connection type) to its shipped definition.</summary>
        public void ResetSelected()
        {
            var v = _selected;
            if (v == null || !v.Builtin) return;
            var d = VendorCatalog.BuiltIn().FirstOrDefault(x => x.Id == v.Id);
            if (d == null) return;
            v.Kind = d.Kind; v.Name = d.Name; v.UrlTemplate = d.UrlTemplate; v.ApiUrlTemplate = d.ApiUrlTemplate;
            v.AuthHeaderName = d.AuthHeaderName; v.AuthHeaderValue = d.AuthHeaderValue;
            SelectedVendor = null; SelectedVendor = v;   // refresh the editor
        }

        public void RemoveSelected()
        {
            var v = _selected;
            if (v == null || v.Builtin) return;
            var index = Vendors.IndexOf(v);
            Vendors.Remove(v);
            SelectedVendor = Vendors.Count == 0 ? null : Vendors[Math.Min(index, Vendors.Count - 1)];
        }

        static bool IsHttp(string template)
        {
            Uri uri;
            return Uri.TryCreate((template ?? "").Replace("{pn}", "x"), UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        /// <summary>Returns an error message, or null when the settings can be saved.</summary>
        public string Validate()
        {
            foreach (var v in Vendors.Where(v => v.Enabled && v.IsEditable))
            {
                var name = string.IsNullOrWhiteSpace(v.Name) ? Loc.T("(unnamed vendor)") : v.Name;
                if (string.IsNullOrWhiteSpace(v.Name)) return Loc.T("Every vendor needs a name.");

                bool hasSite = !string.IsNullOrWhiteSpace(v.UrlTemplate);
                if (hasSite && (!v.UrlTemplate.Contains("{pn}") || !IsHttp(v.UrlTemplate)))
                    return Loc.F("“{0}”: the website search page must start with http:// or https:// and contain {{pn}}.", name);
                if (!hasSite && !v.UseApi) return Loc.F("“{0}”: enter the website search page, or switch on the data API.", name);
                if (v.UseApi && (!(v.ApiUrlTemplate ?? "").Contains("{pn}") || !IsHttp(v.ApiUrlTemplate)))
                    return Loc.F("“{0}”: the API URL must start with http:// or https:// and contain {{pn}}.", name);
            }
            return null;
        }

        public AppSettings Commit()
        {
            Settings.Vendors = Vendors.ToList();
            return Settings;
        }
    }

    /// <summary>One entry of the language drop-down. Language names are shown in their own language so they are always recognisable.</summary>
    public sealed class LanguageOption
    {
        public LanguageOption(string code, string name) { Code = code; Name = name; }
        public string Code { get; private set; }
        public string Name { get; private set; }

        public static string NameOf(string code)
        {
            switch (code)
            {
                case "en": return "English";
                case "fr": return "Français";
                case "de": return "Deutsch";
                case "es": return "Español";
                case "it": return "Italiano";
                case "pt": return "Português";
                case "ja": return "日本語";
                case "zh": return "中文(简体)";
                case "ko": return "한국어";
                case "ru": return "Русский";
                case "pl": return "Polski";
                case "cs": return "Čeština";
                case "tr": return "Türkçe";
                case "nl": return "Nederlands";
                default: return code;
            }
        }
    }

    public partial class SettingsWindow : Window
    {
        readonly SettingsEditVM _vm;

        public SettingsWindow()
        {
            InitializeComponent();
            _vm = new SettingsEditVM(SettingsStore.Clone(SettingsStore.Load()));
            DataContext = _vm;
        }

        void Add_Click(object sender, RoutedEventArgs e) { _vm.AddVendor(); VendorList.ScrollIntoView(_vm.SelectedVendor); }
        void Remove_Click(object sender, RoutedEventArgs e) { _vm.RemoveSelected(); }
        void Reset_Click(object sender, RoutedEventArgs e) { _vm.ResetSelected(); }

        /// <summary>Runs one real search against the selected JSON website with the entered part number and reports what came back.</summary>
        async void Test_Click(object sender, RoutedEventArgs e)
        {
            var v = _vm.SelectedVendor;
            if (v == null || !v.UseApi) return;
            var pn = (_vm.TestPart ?? "").Trim();
            if (pn.Length == 0) { _vm.TestResult = Loc.T("Enter a part number to test with."); return; }
            if (!(v.ApiUrlTemplate ?? "").Contains("{pn}")) { _vm.TestResult = Loc.T("The API URL must contain {pn}."); return; }

            _vm.IsTesting = true;
            _vm.TestResult = Loc.T("Testing…");
            try
            {
                var provider = new VendorGrid.Providers.CustomJsonProvider(_vm.Settings, v);
                using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20)))
                {
                    var matches = await provider.SearchAsync(pn, cts.Token);
                    if (matches.Count == 0) _vm.TestResult = Loc.T("Connected, but no listings were mapped. Check the items path and field paths.");
                    else
                    {
                        var m = matches[0];
                        _vm.TestResult = Loc.F("OK — {0} listing(s). First: {1} · {2} · stock {3} · {4} price break(s)",
                                matches.Count, m.Sku ?? Loc.T("(no SKU)"), m.Mpn ?? Loc.T("(no part number)"), m.Stock < 0 ? Loc.T("unknown") : m.Stock.ToString(), m.Breaks.Count)
                            + (m.Breaks.Count == 0 ? " — " + Loc.T("check the price breaks path.") : ".");
                    }
                }
            }
            catch (OperationCanceledException) { _vm.TestResult = Loc.T("Timed out after 20 s."); }
            catch (VendorException ex) { _vm.TestResult = ex.Message; }
            catch (Exception ex) { _vm.TestResult = Loc.F("Failed: {0}", ex.Message); }
            finally { _vm.IsTesting = false; }
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            var problem = _vm.Validate();
            if (problem != null)
            {
                MessageBox.Show(this, problem, "VendorGrid", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            try
            {
                SettingsStore.Save(_vm.Commit());
                DialogResult = true;
            }
            catch (Exception ex)
            {
                Log.Error("Saving settings failed", ex);
                MessageBox.Show(this, Loc.F("Settings could not be saved:\n{0}", ex.Message), "VendorGrid", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
