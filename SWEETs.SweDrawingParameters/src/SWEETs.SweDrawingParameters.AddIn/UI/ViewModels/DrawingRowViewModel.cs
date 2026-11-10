using SWEETs.SweDrawingParameters.AddIn.Model;
using SWEETs.SweDrawingParameters.AddIn.Services;

namespace SWEETs.SweDrawingParameters.AddIn.UI.ViewModels
{
    public enum RowState { Reading, Ready, Unreadable, NotSupported, Queued, Working, Verified, Skipped, Failed }

    public sealed class DrawingRowViewModel : ObservableObject
    {
        public DrawingInfo Drawing { get; }

        public DrawingRowViewModel(DrawingInfo d) { Drawing = d; }

        public int Position => Drawing.Position;
        public string Name => System.IO.Path.GetFileNameWithoutExtension(Drawing.FilePath);
        public string Description => string.IsNullOrWhiteSpace(Drawing.Description) ? "(no description)" : Drawing.Description;
        public string TypeLabel => Drawing.TypeLabel;

        private bool _isSelected = true;
        public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }

        private DrawingParameters _current;
        public DrawingParameters Current
        {
            get => _current;
            set { if (Set(ref _current, value)) { Raise(nameof(SnapText)); Raise(nameof(GridText)); Raise(nameof(ZoneText)); Raise(nameof(OrthoText)); } }
        }

        public string SnapText => _current?.SnapText ?? "—";
        public string GridText => _current?.GridText ?? "—";
        public string ZoneText => _current?.ZoneText ?? "—";
        public string OrthoText => DrawingParameters.OnOff(_current?.OrthoOn);

        private RowState _state = RowState.Reading;
        public RowState State { get => _state; set { if (Set(ref _state, value)) Raise(nameof(StateText)); } }

        private string _detail;
        /// <summary>Tooltip text: read error, verification differences, skip reason.</summary>
        public string Detail { get => _detail; set => Set(ref _detail, value); }

        public string StateText
        {
            get
            {
                switch (_state)
                {
                    case RowState.Reading: return "Reading…";
                    case RowState.Ready: return "Ready";
                    case RowState.Unreadable: return "Unreadable";
                    case RowState.Queued: return "Queued";
                    case RowState.Working: return "Updating…";
                    case RowState.NotSupported: return "Not supported";
                    case RowState.Verified: return "Verified";
                    case RowState.Skipped: return "Skipped";
                    default: return "Failed";
                }
            }
        }
    }
}
