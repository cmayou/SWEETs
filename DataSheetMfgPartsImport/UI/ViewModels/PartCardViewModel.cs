using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using DataSheetMfgPartsImport.Services;
using DataSheetMfgPartsImport.Extraction;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.UI.ViewModels;

public enum CardState { Ready, NeedsAttention, Building, Created, Updated, Skipped, Failed }

/// <summary>Preview card for one manufacturer part: editable fields, class, circuits, duplicate handling.</summary>
public sealed class PartCardViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isSelected;
    private ClassCandidate _selectedClass;
    private bool _classConfirmed;
    private ExistingPartInfo _existing;
    private DuplicateAction _duplicateAction = DuplicateAction.Skip;
    private CardState _state = CardState.Ready;
    private string _resultMessage;
    private string _resultDetails;
    private bool _isExpanded;

    public PartCardViewModel(ExtractedPart part, IReadOnlyList<ClassCandidate> classes, IReadOnlyList<CircuitTypeInfo> circuitTypes,
        IDictionary<string, string> pinnedCircuitTypes, bool selected, Action changed)
    {
        _changed = changed;
        SourceFile = part.SourceFile;
        Origin = part.Origin;
        Category = part.Category;
        Classes = classes;
        CircuitTypes = circuitTypes;
        _isSelected = selected;
        _isExpanded = selected;

        foreach (var info in PartFieldInfo.All)
        {
            var vm = new FieldViewModel(info, part[info.Field], OnFieldChanged);
            (info.Group switch
            {
                FieldGroup.Identity => IdentityFields,
                FieldGroup.Ratings => RatingFields,
                FieldGroup.Dimensions => DimensionFields,
                _ => OtherFields,
            }).Add(vm);
        }

        foreach (var c in part.Circuits)
            AddCircuitVm(new CircuitViewModel(c, circuitTypes, pinnedCircuitTypes));

        var suggestion = ClassMatcher.Suggest(part.Category, classes)
                         ?? ClassMatcher.SuggestFromText(part.Get(PartField.ComponentType), classes);
        _selectedClass = suggestion;
        IsClassSuggested = suggestion != null;

        RemoveCircuitCommand = new RelayCommand(p => { if (p is CircuitViewModel c) { Circuits.Remove(c); Changed(); } });
        AddCircuitCommand = new RelayCommand(() =>
        {
            AddCircuitVm(new CircuitViewModel(new CircuitSpec { Kind = CircuitKind.Generic, Source = "Added manually" }, circuitTypes, pinnedCircuitTypes));
            Changed();
        });
        ConfirmClassCommand = new RelayCommand(() => ClassConfirmed = true, () => SelectedClass != null && !ClassConfirmed);
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    // ---------------- data ----------------

    public string SourceFile { get; }
    public string SourceFileName => Path.GetFileName(SourceFile ?? "");
    public string Origin { get; }
    public string Category { get; }
    public string CategoryText => string.IsNullOrEmpty(Category) ? "Uncategorised" : Category;

    public ObservableCollection<FieldViewModel> IdentityFields { get; } = new();
    public ObservableCollection<FieldViewModel> RatingFields { get; } = new();
    public ObservableCollection<FieldViewModel> DimensionFields { get; } = new();
    public ObservableCollection<FieldViewModel> OtherFields { get; } = new();
    public ObservableCollection<CircuitViewModel> Circuits { get; } = new();

    public IReadOnlyList<ClassCandidate> Classes { get; }
    public IReadOnlyList<CircuitTypeInfo> CircuitTypes { get; }

    public IEnumerable<FieldViewModel> AllFields => IdentityFields.Concat(RatingFields).Concat(DimensionFields).Concat(OtherFields);

    public FieldViewModel FieldOf(PartField f) => AllFields.First(x => x.Field == f);

    public string Reference => FieldOf(PartField.Reference).Value;
    public string Manufacturer => FieldOf(PartField.Manufacturer).Value;
    public string Title => string.IsNullOrWhiteSpace(Reference) ? "(no reference)" : Reference;
    public string Subtitle => string.Join("  ·  ", new[] { Manufacturer, FieldOf(PartField.Description).Value }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public int CoveragePercent
    {
        get
        {
            var all = AllFields.ToList();
            return all.Count == 0 ? 0 : (int)Math.Round(100.0 * all.Count(f => f.HasValue) / all.Count);
        }
    }

    public int FoundCount => AllFields.Count(f => f.HasValue);
    public int FieldCount => AllFields.Count();

    // ---------------- selection / class ----------------

    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value)) Changed(); }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    public ClassCandidate SelectedClass
    {
        get => _selectedClass;
        set
        {
            if (!Set(ref _selectedClass, value)) return;
            IsClassSuggested = false;
            OnPropertyChanged(nameof(IsClassSuggested));
            // Picking a class from the list is an explicit confirmation.
            ClassConfirmed = value != null;
        }
    }

    public bool IsClassSuggested { get; private set; }

    public bool ClassConfirmed
    {
        get => _classConfirmed;
        set
        {
            if (!Set(ref _classConfirmed, value)) return;
            OnPropertyChanged(nameof(ClassStatusText));
            Changed();
        }
    }

    public string ClassStatusText => SelectedClass == null ? "Choose a class"
        : ClassConfirmed ? "Confirmed"
        : IsClassSuggested ? $"Suggested from “{CategoryText}” — confirm"
        : "Confirm class";

    // ---------------- duplicates ----------------

    public ExistingPartInfo Existing
    {
        get => _existing;
        set
        {
            if (!Set(ref _existing, value)) return;
            OnPropertyChanged(nameof(IsDuplicate));
            OnPropertyChanged(nameof(DuplicateText));
            Changed();
        }
    }

    public bool IsDuplicate => _existing != null;
    public string DuplicateText => _existing == null ? "" : $"Already in the manufacturer parts manager{(string.IsNullOrEmpty(_existing.Description) ? "" : $": {_existing.Description}")}";

    public bool Overwrite
    {
        get => _duplicateAction == DuplicateAction.Overwrite;
        set
        {
            _duplicateAction = value ? DuplicateAction.Overwrite : DuplicateAction.Skip;
            OnPropertyChanged(nameof(Overwrite));
            OnPropertyChanged(nameof(Skip));
            Changed();
        }
    }

    public bool Skip
    {
        get => !Overwrite;
        set => Overwrite = !value;
    }

    // ---------------- validation / state ----------------

    public IReadOnlyList<string> Problems
    {
        get
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(Reference)) list.Add("Reference is required");
            if (string.IsNullOrWhiteSpace(Manufacturer)) list.Add("Manufacturer is required");
            if (SelectedClass == null) list.Add("Choose a class");
            else if (!ClassConfirmed) list.Add("Confirm the suggested class");
            foreach (var f in new[] { PartField.WidthMm, PartField.HeightMm, PartField.DepthMm, PartField.WeightG })
            {
                var v = FieldOf(f).Value;
                if (!string.IsNullOrWhiteSpace(v) && !Units.TryParseNumber(v, out _)) list.Add($"{FieldOf(f).Label} must be a number");
            }
            if (Circuits.Any(c => !c.HasType)) list.Add("Pick a circuit type for every circuit");
            return list;
        }
    }

    public bool IsValid => Problems.Count == 0;

    /// <summary>Will this card actually write to the catalog on Build?</summary>
    public bool WillWrite => IsSelected && !(IsDuplicate && Skip);

    public string ProblemText => string.Join(" · ", Problems);

    public CardState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value)) OnPropertyChanged(nameof(StateText));
        }
    }

    public string StateText => State switch
    {
        CardState.Ready => "Ready",
        CardState.NeedsAttention => "Needs attention",
        CardState.Building => "Saving…",
        CardState.Created => "Saved",
        CardState.Updated => "Updated",
        CardState.Skipped => "Skipped",
        CardState.Failed => "Failed",
        _ => "",
    };

    public string ResultMessage
    {
        get => _resultMessage;
        set => Set(ref _resultMessage, value);
    }

    public string ResultDetails
    {
        get => _resultDetails;
        set => Set(ref _resultDetails, value);
    }

    public bool IsFinished => State is CardState.Created or CardState.Updated or CardState.Skipped or CardState.Failed;

    // ---------------- commands ----------------

    public ICommand RemoveCircuitCommand { get; }
    public ICommand AddCircuitCommand { get; }
    public ICommand ConfirmClassCommand { get; }
    public ICommand ToggleExpandCommand { get; }

    // ---------------- helpers ----------------

    public PartDraft ToDraft()
    {
        var d = new PartDraft
        {
            ClassId = SelectedClass?.Id ?? 0,
            Exists = IsDuplicate,
            DuplicateAction = _duplicateAction,
            DatasheetPath = SourceFile,
        };
        foreach (var f in AllFields) d.Values[f.Field] = f.Value?.Trim();
        foreach (var c in Circuits)
        {
            var cd = new CircuitDraft { Kind = c.Kind, CircuitTypeKeyCode = c.EffectiveKeyCode };
            cd.Terminals.AddRange(c.TerminalList);
            d.Circuits.Add(cd);
        }
        return d;
    }

    private void AddCircuitVm(CircuitViewModel c)
    {
        c.PropertyChanged += (_, __) => Changed();
        Circuits.Add(c);
    }

    private void OnFieldChanged() => Changed();

    private void Changed()
    {
        if (!IsFinished && State != CardState.Building)
            State = IsSelected && !IsValid ? CardState.NeedsAttention : CardState.Ready;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(CoveragePercent));
        OnPropertyChanged(nameof(FoundCount));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(ProblemText));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(WillWrite));
        OnPropertyChanged(nameof(ClassStatusText));
        _changed?.Invoke();
    }

    /// <summary>Re-evaluates state after construction (once Existing has been checked).</summary>
    public void Refresh() => Changed();
}
