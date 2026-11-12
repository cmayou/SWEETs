using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using SWEETs.SwePdfSchematic.AddIn.UI.ViewModels;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Recognition;

namespace SWEETs.SwePdfSchematic.AddIn.UI
{
    /// <summary>
    /// Edits the manufacturer parts of one component, cable or cabinet item: parts read from the PDF can be excluded,
    /// corrected or added. Parts missing from the catalog are created when the project is built.
    /// </summary>
    public partial class PartsDialog : Window
    {
        private readonly BomTarget _target;
        private readonly Func<PartAssignment, bool, string> _check;
        private readonly ObservableCollection<PartRowViewModel> _rows;

        /// <param name="check">Looks one part up in the catalog (sets its state); returns an error text, or null. Null when SWE is not connected.</param>
        public PartsDialog(BomTarget target, string heading, string facts, bool hasSymbol, Func<PartAssignment, bool, string> check)
        {
            InitializeComponent();
            _target = target;
            _check = check;
            _rows = new ObservableCollection<PartRowViewModel>(target.Parts.Select(p => new PartRowViewModel(p)));
            foreach (var r in _rows) r.PropertyChanged += OnRowChanged;
            _rows.CollectionChanged += (s, e) => { if (e.NewItems != null) foreach (PartRowViewModel r in e.NewItems) r.PropertyChanged += OnRowChanged; };
            PartsGrid.ItemsSource = _rows;
            Heading.Text = heading;
            Facts.Text = facts;
            IncludeItem.Content = hasSymbol ? "Assign these parts to the component" : $"Create this {PartsText.Kind(target.Kind).ToLowerInvariant()} in the project";
            IncludeItem.IsChecked = target.Include;
            CheckButton.IsEnabled = check != null;
            Hint.Text = check == null ? "SOLIDWORKS Electrical is not connected: the catalog is checked when the project is built."
                                      : "Parts not in the catalog are added to it when the project is built.";
        }

        /// <summary>One base part per component: ticking "Circuits" on a row clears it on the others.</summary>
        private void OnRowChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PartRowViewModel.CarriesCircuits) || !(sender is PartRowViewModel row) || !row.CarriesCircuits) return;
            foreach (var other in _rows.Where(r => r != row && r.CarriesCircuits)) other.CarriesCircuits = false;
        }

        private void OnAdd(object sender, RoutedEventArgs e)
        {
            var row = new PartRowViewModel(new PartAssignment { Origin = BomReader.ReviewOrigin, Quantity = 1, CatalogNote = "Not checked yet" });
            _rows.Add(row);
            PartsGrid.SelectedItem = row;
            PartsGrid.ScrollIntoView(row);
            PartsGrid.CurrentCell = new System.Windows.Controls.DataGridCellInfo(row, PartsGrid.Columns[2]);
            PartsGrid.BeginEdit();
        }

        private void OnRemove(object sender, RoutedEventArgs e)
        {
            if (PartsGrid.SelectedItem is PartRowViewModel row) _rows.Remove(row);
        }

        private void OnCheck(object sender, RoutedEventArgs e)
        {
            if (_check == null) return;
            PartsGrid.CommitEdit(); PartsGrid.CommitEdit();
            string error = null;
            foreach (var row in _rows.Where(r => !string.IsNullOrWhiteSpace(r.Reference)))
            {
                var m = row.ToModel();
                error = _check(m, _target.Kind == BomItemKind.Cable) ?? error;
                row.Catalog = m.Catalog;
                row.CatalogNote = m.CatalogNote;
            }
            Hint.Text = error ?? $"{_rows.Count(r => r.Catalog == CatalogState.InCatalog)} in catalog, {_rows.Count(r => r.Catalog == CatalogState.WillCreate)} will be added.";
        }

        private void OnOk(object sender, RoutedEventArgs e)
        {
            PartsGrid.CommitEdit(); PartsGrid.CommitEdit();
            _target.Parts = _rows.Where(r => !string.IsNullOrWhiteSpace(r.Reference)).Select(r => r.ToModel()).ToList();
            _target.Include = IncludeItem.IsChecked == true;
            _target.EnsureBasePart();
            _target.Edited = true;
            DialogResult = true;
        }
    }
}
