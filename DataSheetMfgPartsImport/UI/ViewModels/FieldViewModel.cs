using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.UI.ViewModels;

/// <summary>One editable value in a part card. Editing marks it as user-confirmed.</summary>
public sealed class FieldViewModel : ObservableObject
{
    private string _value;
    private Confidence _confidence;
    private readonly System.Action _changed;

    public FieldViewModel(PartFieldInfo info, FieldValue value, System.Action changed)
    {
        Info = info;
        _value = value?.Value ?? "";
        _confidence = value?.Confidence ?? Confidence.Missing;
        Source = value?.Source;
        _changed = changed;
    }

    public PartFieldInfo Info { get; }
    public PartField Field => Info.Field;
    public string Label => Info.Label;
    public string Unit => Info.Unit;
    public bool Required => Info.Required;
    public string Source { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (!Set(ref _value, value)) return;
            OnPropertyChanged(nameof(HasValue));
            Confidence = string.IsNullOrWhiteSpace(value) ? Confidence.Missing : Confidence.Edited;
            _changed?.Invoke();
        }
    }

    public Confidence Confidence
    {
        get => _confidence;
        private set
        {
            if (!Set(ref _confidence, value)) return;
            OnPropertyChanged(nameof(ConfidenceText));
            OnPropertyChanged(nameof(Tooltip));
        }
    }

    public bool HasValue => !string.IsNullOrWhiteSpace(_value);

    public string ConfidenceText => Confidence switch
    {
        Confidence.High => "Found with label",
        Confidence.Medium => "Inferred — check",
        Confidence.Low => "Guess — check",
        Confidence.Edited => "Edited",
        _ => Required ? "Required" : "Not found",
    };

    /// <summary>Tooltip: where the value came from.</summary>
    public string Tooltip => string.IsNullOrEmpty(Source) ? ConfidenceText : $"{ConfidenceText}\n{Source}";
}
