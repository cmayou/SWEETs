using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.AddIn.UI.ViewModels
{
    /// <summary>One detected symbol as a review card.</summary>
    public sealed class SymbolCardViewModel : ObservableObject
    {
        public DetectedSymbol Model { get; }
        public SymbolCardViewModel(DetectedSymbol model) { Model = model; }

        public string Id => Model.Id;
        public int Page => Model.PageNumber;
        public string Tag => string.IsNullOrEmpty(Model.Tag) ? "No tag" : Model.PartsKey;
        public bool HasTag => !string.IsNullOrEmpty(Model.Tag);
        public string KindText => Humanize(Model.Kind);
        public string GroupKey => $"{Model.Standard} · {KindText}";
        public string Category => Model.Category;
        public string Standard => Model.Standard.ToString();
        public string Subtitle => $"Page {Model.PageNumber} · {Model.Category} · {Model.Pins.Count} pin{(Model.Pins.Count == 1 ? "" : "s")}"
                                  + (Model.IsCombined ? $" · {Model.CombinedFrom.Count} poles combined" : "");

        /// <summary>Detected poles of the same device on this page (including this card); 2+ enables "Combine N poles".</summary>
        public IReadOnlyList<SymbolCardViewModel> CombineGroup { get => _group; set { _group = value ?? new List<SymbolCardViewModel>(); Raise(); Raise(nameof(CanCombine)); Raise(nameof(CombineText)); } }
        private IReadOnlyList<SymbolCardViewModel> _group = new List<SymbolCardViewModel>();
        public bool CanCombine => _group.Count >= 2 && !Model.IsCombined;
        public string CombineText => $"Combine {_group.Count} poles";
        public bool IsCombined => Model.IsCombined;
        public string SweSymbol => Model.SelectedSweSymbol ?? "Not mapped";
        public bool IsMapped => Model.SelectedSweSymbol != null;
        public double Confidence => Model.Confidence;
        public string ConfidenceText => $"{Model.Confidence:P0}";
        public string Notes => string.Join(" ", Model.Notes.Concat(Model.Xrefs.Any() ? new[] { "Xref: " + string.Join(", ", Model.Xrefs) } : new string[0]));
        public bool HasNotes => !string.IsNullOrEmpty(Notes);
        public ReviewState State => Model.Review;

        public string StateText
        {
            get
            {
                switch (Model.Review)
                {
                    case ReviewState.Confirmed: return "Confirmed";
                    case ReviewState.NeedsReview: return "Needs review";
                    case ReviewState.Unmapped: return "Unmapped";
                    default: return "Ignored";
                }
            }
        }

        public Brush StateBrush => Palette.ForState(Model.Review);
        public Brush StateSoftBrush => Palette.SoftForState(Model.Review);
        public Brush ConfidenceBrush => Model.Confidence >= 0.85 ? Palette.Success : Model.Confidence >= 0.5 ? Palette.Warning : Palette.Danger;

        /// <summary>Manufacturer parts of this symbol's component (shared by every symbol with the same tag), or null.</summary>
        public BomTarget Bom { get => _bom; set { _bom = value; Raise(nameof(PartsLine)); Raise(nameof(PartsTooltip)); Raise(nameof(PartsBadge)); Raise(nameof(HasPartsBadge)); } }
        private BomTarget _bom;
        public string PartsLine => !HasTag ? "Tag the symbol to give it parts" : _bom == null || !_bom.Include ? (_bom == null ? "No parts" : "Parts excluded") : PartsText.Summary(_bom.Parts);
        public string PartsTooltip => _bom == null || _bom.Parts.Count == 0 ? "No manufacturer parts for this component. Use Parts… to add one." : PartsText.Tooltip(_bom.Parts);
        public string PartsBadge
        {
            get
            {
                if (_bom == null || !_bom.Include) return null;
                int added = _bom.Parts.Count(p => p.Include && p.Catalog == CatalogState.WillCreate);
                return added > 0 ? $"{added} new" : _bom.Parts.Any(p => p.Include && p.Catalog == CatalogState.InCatalog) ? "In catalog" : null;
            }
        }
        public bool HasPartsBadge => PartsBadge != null;
        public Brush PartsBadgeBrush => _bom != null && _bom.Parts.Any(p => p.Include && p.Catalog == CatalogState.WillCreate) ? Palette.Warning : Palette.Success;

        public void Refresh() => RaiseAll();

        public static string Humanize(SymbolKind k)
        {
            switch (k)
            {
                case SymbolKind.NoContact: return "NO contact";
                case SymbolKind.NcContact: return "NC contact";
                case SymbolKind.PushbuttonNo: return "Pushbutton NO";
                case SymbolKind.PushbuttonNc: return "Pushbutton NC";
                case SymbolKind.CircuitBreaker: return "Circuit breaker";
                case SymbolKind.OffPageArrow: return "Off-page arrow";
                case SymbolKind.BlackBox: return "Black box";
                case SymbolKind.Unknown: return "Unrecognized";
                default: return k.ToString();
            }
        }
    }

    /// <summary>Theme-consistent brushes for code-built visuals (canvas overlays, chart bars).</summary>
    public static class Palette
    {
        private static SolidColorBrush B(string hex) { var b = (SolidColorBrush)new BrushConverter().ConvertFrom(hex); b.Freeze(); return b; }
        public static readonly Brush Success = B("#047857"), SuccessSoft = B("#E6F4EE");
        public static readonly Brush Warning = B("#B45309"), WarningSoft = B("#FDF3E6");
        public static readonly Brush Danger = B("#B91C1C"), DangerSoft = B("#FBEAEA");
        public static readonly Brush Neutral = B("#6B7280"), NeutralSoft = B("#F0F2F5");
        public static readonly Brush Accent = B("#1D4ED8");
        public static readonly Brush Ink = B("#111827"), InkSubtle = B("#9AA1AD");

        public static Brush ForState(ReviewState s) =>
            s == ReviewState.Confirmed ? Success : s == ReviewState.NeedsReview ? Warning : s == ReviewState.Unmapped ? Danger : Neutral;
        public static Brush SoftForState(ReviewState s) =>
            s == ReviewState.Confirmed ? SuccessSoft : s == ReviewState.NeedsReview ? WarningSoft : s == ReviewState.Unmapped ? DangerSoft : NeutralSoft;
    }
}
