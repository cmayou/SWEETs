using System;
using System.Linq;
using System.Windows.Media;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.AddIn.UI.ViewModels
{
    /// <summary>One PDF page in the "Pages to import" list.</summary>
    public sealed class PageRowViewModel : ObservableObject
    {
        private readonly Action<PageRowViewModel, bool> _changed;
        private bool _include;

        public PageRowViewModel(PageAnalysis model, Action<PageRowViewModel, bool> changed)
        {
            Model = model;
            _changed = changed;
            _include = model.Include && model.Kind == PageKind.Schematic;
        }

        public PageAnalysis Model { get; }
        public int Number => Model.PageNumber;
        public string Label => $"Page {Model.PageNumber}";

        /// <summary>Two-way bound to the checkbox; the main view model decides what happens (a report page triggers re-analysis).</summary>
        public bool Include
        {
            get => _include;
            set { if (_include == value) return; _include = value; Raise(); _changed?.Invoke(this, value); }
        }

        /// <summary>Changes the checkbox without notifying the main view model (bulk changes, cancelled prompts).</summary>
        public void SetSilently(bool include) { _include = include; Raise(nameof(Include)); }

        public bool CanToggle => !Model.IsRasterOnly;

        public string KindText =>
            Model.IsRasterOnly ? "Raster" :
            Model.Kind == PageKind.Schematic ? (Model.ForcedSchematic ? "Schematic (forced)" : "Schematic") :
            Model.Kind == PageKind.Layout ? "Layout" : "Report";

        public Brush KindBrush => Model.Kind == PageKind.Schematic && !Model.IsRasterOnly ? Palette.Success : Palette.Neutral;

        public string Title
        {
            get
            {
                var t = Model.Kind == PageKind.Schematic ? Model.TitleBlock?.Get("TITLE") : Model.ReportTitle;
                if (string.IsNullOrWhiteSpace(t)) t = Model.TitleBlock?.Get("TITLE");
                if (string.IsNullOrWhiteSpace(t)) return "—";
                return t.Length > 70 ? t.Substring(0, 67) + "…" : t;
            }
        }

        public string Facts =>
            Model.Kind == PageKind.Schematic && !Model.IsRasterOnly
                ? $"{Model.Symbols.Count} symbols · {Model.Nets.Count} wire nets" + (string.IsNullOrEmpty(Model.SheetNumber) ? "" : $" · sheet {Model.SheetNumber}")
                : Model.IsRasterOnly ? "Scanned page — not supported" : "Tick to analyse and import it as a schematic";

        public void Refresh() { _include = Model.Include && Model.Kind == PageKind.Schematic; RaiseAll(); }
    }
}
