using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SWEETs.SwePdfSchematic.AddIn.UI.ViewModels;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.AddIn.UI
{
    public sealed class MappingChoice
    {
        public string SweSymbol;
        public int RotationOffset;
    }

    /// <summary>
    /// Library browser for the SOLIDWORKS Electrical symbol a detected shape should become: search, standard and
    /// class filters, circuit-count match for combined poles, DWG previews. Typing a name not in the list still works.
    /// </summary>
    public partial class MappingDialog : Window
    {
        private const string Any = "All";
        private readonly SymbolCatalog _catalog;
        private readonly string _hint;
        private readonly int _poles;
        private readonly Func<bool> _reload;
        private CatalogEntry _shown;
        private bool _ready;

        public MappingChoice Result { get; private set; }
        public bool ApplyToSimilar => ApplySimilar.IsChecked == true;

        /// <param name="reloadLibrary">Re-reads the SWE library; returns false when SWE is not connected.</param>
        public MappingDialog(SymbolCardViewModel card, SymbolCatalog catalog, string searchHint, int similarCount, int sameKindCount, int poles, Func<bool> reloadLibrary)
        {
            InitializeComponent();
            _catalog = catalog;
            _hint = searchHint;
            _poles = poles;
            _reload = reloadLibrary;

            Heading.Text = $"{card.Tag} — {card.KindText}" + (card.Model.IsCombined ? $" · {card.Model.CombinedFrom.Count} poles combined" : "");
            var cands = card.Model.Candidates.Any() ? "  Suggestions: " + string.Join(", ", card.Model.Candidates.Take(3).Select(c => $"{c.SweSymbolName} ({c.Score:P0})")) + "." : "";
            var part = card.Model.Part != null ? $"  Part: {card.Model.Part}." : "";
            Facts.Text = $"Page {card.Page} · {card.Standard} · {card.Model.Pins.Count} pins · {poles} pole{(poles == 1 ? "" : "s")} · {card.Category}.{part}{cands}";

            MatchPoles.Content = poles > 1 ? $"Only symbols with {poles} circuits" : "Only single-circuit symbols";
            MatchPoles.IsChecked = false;
            MatchPoles.Visibility = poles > 1 ? Visibility.Visible : Visibility.Collapsed;

            ApplySimilar.Content = similarCount > 0 ? $"Also apply to {similarCount} identical shape{(similarCount == 1 ? "" : "s")} in this PDF" : "No identical shapes elsewhere in this PDF";
            ApplySimilar.IsEnabled = similarCount > 0;
            ApplySimilar.IsChecked = similarCount > 0;
            LearnNote.Text = card.Model.IsCombined
                ? "This combined shape is remembered: the same poles are combined and mapped automatically on future imports."
                : card.Model.Kind == SymbolKind.Unknown
                    ? "This exact shape is remembered for future imports."
                    : $"Remembered for this shape, and the default for {card.Standard} {card.KindText.ToLowerInvariant()} symbols ({sameKindCount} others in this PDF are re-scored).";

            FillFilters(card.Standard);
            Search.Text = "";
            _ready = true;
            Refresh();

            // preselect the current mapping, if any
            var current = catalog.Find(card.Model.SelectedSweSymbol);
            if (current != null) { Results.SelectedItem = current; Results.ScrollIntoView(current); }
            else if (card.Model.SelectedSweSymbol != null) Search.Text = card.Model.SelectedSweSymbol;
        }

        private void FillFilters(string standard)
        {
            var standards = new[] { Any }.Concat(_catalog.Standards).ToList();
            StandardFilter.ItemsSource = standards;
            StandardFilter.SelectedItem = standards.FirstOrDefault(s => s.Equals(standard, StringComparison.OrdinalIgnoreCase)) ?? Any;
            CategoryFilter.ItemsSource = new[] { Any }.Concat(_catalog.Categories).ToList();
            CategoryFilter.SelectedIndex = 0;
        }

        private void Refresh()
        {
            if (!_ready) return;
            var q = new CatalogQuery
            {
                Text = Search.Text,
                Hint = _hint,
                Standard = StandardFilter.SelectedItem as string == Any ? null : StandardFilter.SelectedItem as string,
                Category = CategoryFilter.SelectedItem as string == Any ? null : CategoryFilter.SelectedItem as string,
                Poles = _poles,
                MatchPoles = MatchPoles.IsChecked == true,
            };
            var list = _catalog.Search(q);
            Results.ItemsSource = list;
            Count.Text = $"{list.Count:N0}{(list.Count >= 400 ? "+" : "")} shown · {_catalog.Count:N0} available";

            if (_catalog.LibraryCount == 0)
                Hint.Text = _catalog.LibraryError != null
                    ? "SOLIDWORKS Electrical library not read: " + _catalog.LibraryError
                    : "SOLIDWORKS Electrical library not available — showing learned and catalog names.";
            else Hint.Text = $"{_catalog.LibraryCount:N0} symbols in the SOLIDWORKS Electrical library.";

            Empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            Empty.Text = string.IsNullOrWhiteSpace(Search.Text)
                ? "No symbols match these filters."
                : $"No symbol matches \"{Search.Text.Trim()}\". Press Use symbol to use the name as typed.";
            UpdateSelection();
        }

        private void OnFilterChanged(object sender, RoutedEventArgs e) => Refresh();

        private void OnSearchKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down && Results.Items.Count > 0)
            {
                Results.SelectedIndex = 0;
                (Results.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
            }
        }

        private void OnPick(object sender, SelectionChangedEventArgs e) => UpdateSelection();

        private void UpdateSelection()
        {
            if (_shown != null) _shown.PropertyChanged -= OnShownChanged;
            _shown = Results.SelectedItem as CatalogEntry;
            var typed = Search.Text.Trim();
            if (_shown != null)
            {
                _shown.PropertyChanged += OnShownChanged;
                SelName.Text = _shown.Name;
                SelDescription.Text = _shown.Description ?? "";
                SelMeta.Text = _shown.Meta + (string.IsNullOrEmpty(_shown.Type) ? "" : $"  ·  {_shown.Type}");
                ShowPreview();
                Ok.IsEnabled = true;
            }
            else
            {
                SelName.Text = typed.Length > 0 ? typed : "Select a symbol";
                SelDescription.Text = typed.Length > 0 ? "Not in the library list — this name is used as typed." : "";
                SelMeta.Text = "";
                BigPreview.Source = null; NoPreview.Visibility = Visibility.Visible;
                Ok.IsEnabled = typed.Length > 0;
            }
        }

        private void OnShownChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CatalogEntry.Preview)) ShowPreview();
        }

        private void ShowPreview()
        {
            var img = _shown?.Preview;
            BigPreview.Source = img;
            NoPreview.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Results.SelectedItem != null) OnOk(sender, e);
        }

        private void OnReload(object sender, RoutedEventArgs e)
        {
            if (_reload == null || !_reload()) { Hint.Text = "SOLIDWORKS Electrical is not connected."; return; }
            _ready = false;
            FillFilters(StandardFilter.SelectedItem as string);
            _ready = true;
            Refresh();
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            var name = (Results.SelectedItem as CatalogEntry)?.Name ?? Search.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            int rot = R90.IsChecked == true ? 90 : R180.IsChecked == true ? 180 : R270.IsChecked == true ? 270 : 0;
            Result = new MappingChoice { SweSymbol = name, RotationOffset = rot };
            DialogResult = true;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_shown != null) _shown.PropertyChanged -= OnShownChanged;
            base.OnClosed(e);
        }
    }
}
