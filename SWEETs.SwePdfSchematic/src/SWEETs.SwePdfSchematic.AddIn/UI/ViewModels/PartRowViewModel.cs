using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Recognition;

namespace SWEETs.SwePdfSchematic.AddIn.UI.ViewModels
{
    /// <summary>One editable manufacturer part in the parts dialog.</summary>
    public sealed class PartRowViewModel : ObservableObject
    {
        private bool _include = true, _carries;
        private string _manufacturer, _reference, _description, _article, _note;
        private int _quantity = 1;
        private CatalogState _catalog;

        public string Origin { get; }

        public PartRowViewModel(PartAssignment p)
        {
            _include = p.Include; _manufacturer = p.Manufacturer; _reference = p.Reference; _description = p.Description;
            _article = p.ArticleNumber; _quantity = p.Quantity; _catalog = p.Catalog; _note = p.CatalogNote; Origin = p.Origin; _carries = p.CarriesCircuits;
        }

        public bool Include { get => _include; set => Set(ref _include, value); }
        /// <summary>Base part: gets the circuits of the component's symbols and the PDF's terminal designations.</summary>
        public bool CarriesCircuits { get => _carries; set => Set(ref _carries, value); }
        public string Manufacturer { get => _manufacturer; set { if (Set(ref _manufacturer, value)) Unchecked(); } }
        public string Reference { get => _reference; set { if (Set(ref _reference, value)) Unchecked(); } }
        public string Description { get => _description; set => Set(ref _description, value); }
        public string ArticleNumber { get => _article; set => Set(ref _article, value); }
        public int Quantity { get => _quantity; set => Set(ref _quantity, value < 1 ? 1 : value > 999 ? 999 : value); }

        public CatalogState Catalog { get => _catalog; set { Set(ref _catalog, value); Raise(nameof(CatalogText)); Raise(nameof(CatalogBrush)); } }
        public string CatalogNote { get => _note; set { Set(ref _note, value); Raise(nameof(CatalogText)); } }
        public string CatalogText => PartsText.StateText(_catalog);
        public Brush CatalogBrush => PartsText.StateBrush(_catalog);

        private void Unchecked() { Catalog = CatalogState.Unknown; CatalogNote = "Not checked yet"; }

        public PartAssignment ToModel() => new PartAssignment
        {
            Include = Include, Manufacturer = Manufacturer?.Trim(), Reference = Reference?.Trim(), Description = Description?.Trim(),
            ArticleNumber = ArticleNumber?.Trim(), Quantity = Quantity, Origin = Origin ?? BomReader.ReviewOrigin, Catalog = Catalog, CatalogNote = CatalogNote,
            CarriesCircuits = CarriesCircuits,
        };
    }

    /// <summary>A parts entry without a schematic symbol (cable, rail, duct, enclosure item, undrawn terminal) on the Build step.</summary>
    public sealed class BomItemViewModel : ObservableObject
    {
        public BomTarget Model { get; }
        public BomItemViewModel(BomTarget model) { Model = model; }

        public string Mark => Model.Mark;
        public string KindText => PartsText.Kind(Model.Kind);
        public string PartsSummary => PartsText.Summary(Model.Parts);
        public string Details => (Model.Description ?? "") + (Model.LocationTag != null ? $"  ·  location {Model.LocationTag}" : "")
                                 + (Model.Kind == BomItemKind.Terminal ? "  ·  not drawn: reported only" : "");
        public bool Include { get => Model.Include; set { if (Model.Include == value) return; Model.Include = value; Model.Edited = true; Raise(); } }
        public void Refresh() => RaiseAll();
    }

    public static class PartsText
    {
        public static string Summary(IEnumerable<PartAssignment> parts)
        {
            var list = parts.Where(p => p.Include).ToList();
            if (list.Count == 0) return "No parts";
            return string.Join(", ", list.Select(p => p.Reference + (p.Quantity > 1 ? $" ×{p.Quantity}" : "")));
        }

        public static string Tooltip(IEnumerable<PartAssignment> parts) =>
            string.Join("\n", parts.Select(p => $"{(p.Include ? "" : "(excluded) ")}{p.Manufacturer ?? "?"} {p.Reference}{(p.Quantity > 1 ? $" ×{p.Quantity}" : "")}"
                                                  + $"{(string.IsNullOrEmpty(p.Description) ? "" : " — " + p.Description)}  [{p.CatalogNote ?? StateText(p.Catalog)}]"));

        public static string StateText(CatalogState s) =>
            s == CatalogState.InCatalog ? "In catalog" : s == CatalogState.WillCreate ? "New" : s == CatalogState.Failed ? "Failed" : "Not checked";

        public static Brush StateBrush(CatalogState s) =>
            s == CatalogState.InCatalog ? Palette.Success : s == CatalogState.WillCreate ? Palette.Warning : s == CatalogState.Failed ? Palette.Danger : Palette.Neutral;

        public static string Kind(BomItemKind k) =>
            k == BomItemKind.Cable ? "Cable" : k == BomItemKind.Location ? "Location item" : k == BomItemKind.Terminal ? "Terminal" : k == BomItemKind.Wire ? "Wire" : "Component";
    }
}
