using System;
using System.Collections.Generic;
using System.Linq;
using DataSheetMfgPartsImport.Services;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.UI.ViewModels;

/// <summary>One circuit row: kind, terminal marks and the SOLIDWORKS Electrical circuit type.</summary>
public sealed class CircuitViewModel : ObservableObject
{
    private string _terminals;
    private CircuitTypeInfo _circuitType;
    private CircuitKind _kind;

    public CircuitViewModel(CircuitSpec spec, IReadOnlyList<CircuitTypeInfo> types, IDictionary<string, string> pinned)
    {
        _kind = spec.Kind;
        _terminals = string.Join(", ", spec.Terminals);
        Explicit = spec.Explicit;
        Source = spec.Source;
        Types = types;
        _circuitType = Match(spec.Kind, types, pinned);
        _keyCodeText = _circuitType?.Display;
    }

    public IReadOnlyList<CircuitTypeInfo> Types { get; }
    public static IReadOnlyList<CircuitKind> Kinds { get; } = Enum.GetValues(typeof(CircuitKind)).Cast<CircuitKind>().ToList();

    public CircuitKind Kind
    {
        get => _kind;
        set { if (Set(ref _kind, value)) OnPropertyChanged(nameof(KindLabel)); }
    }

    public string KindLabel => CircuitSpec.KindLabel(_kind);
    public bool Explicit { get; }
    public string Source { get; }
    public string OriginText => Explicit ? "From PDF" : "Derived";

    public string Terminals
    {
        get => _terminals;
        set => Set(ref _terminals, value);
    }

    public CircuitTypeInfo CircuitType
    {
        get => _circuitType;
        set { if (Set(ref _circuitType, value)) OnPropertyChanged(nameof(HasType)); }
    }

    private string _keyCodeText;

    /// <summary>Typed text in the editable circuit-type box; used as the key code when nothing is picked from the list.</summary>
    public string KeyCodeText
    {
        get => _keyCodeText;
        set { if (Set(ref _keyCodeText, value)) OnPropertyChanged(nameof(HasType)); }
    }

    /// <summary>Key code written to SOLIDWORKS Electrical.</summary>
    public string EffectiveKeyCode
    {
        get
        {
            if (_circuitType != null && string.Equals(_keyCodeText ?? "", _circuitType.Display, StringComparison.Ordinal)) return _circuitType.KeyCode;
            if (!string.IsNullOrWhiteSpace(_keyCodeText)) return _keyCodeText.Trim();
            return _circuitType?.KeyCode;
        }
    }

    public bool HasType => !string.IsNullOrWhiteSpace(EffectiveKeyCode);

    public IEnumerable<string> TerminalList =>
        (_terminals ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim());

    /// <summary>Pick the circuit type: pinned key code from settings first, then best description match.</summary>
    private static CircuitTypeInfo Match(CircuitKind kind, IReadOnlyList<CircuitTypeInfo> types, IDictionary<string, string> pinned)
    {
        if (types == null || types.Count == 0) return null;
        if (pinned != null && pinned.TryGetValue(kind.ToString(), out var key) && !string.IsNullOrWhiteSpace(key))
        {
            var p = types.FirstOrDefault(t => string.Equals(t.KeyCode, key, StringComparison.OrdinalIgnoreCase));
            if (p != null) return p;
        }

        string[][] keywordSets = kind switch
        {
            CircuitKind.Coil => new[] { new[] { "coil" }, new[] { "bobine" }, new[] { "control" } },
            CircuitKind.MainPole => new[] { new[] { "power", "no" }, new[] { "power" }, new[] { "main" }, new[] { "pole" }, new[] { "protection" } },
            CircuitKind.AuxNO => new[] { new[] { "aux", "no" }, new[] { "normally open" }, new[] { "contact", "no" }, new[] { "no" } },
            CircuitKind.AuxNC => new[] { new[] { "aux", "nc" }, new[] { "normally closed" }, new[] { "contact", "nc" }, new[] { "nc" } },
            CircuitKind.Changeover => new[] { new[] { "changeover" }, new[] { "change-over" }, new[] { "inverter" }, new[] { "co" } },
            CircuitKind.Pin => new[] { new[] { "pin" }, new[] { "connector" }, new[] { "terminal" }, new[] { "contact" } },
            CircuitKind.PowerInput => new[] { new[] { "power", "input" }, new[] { "power", "supply" }, new[] { "input" }, new[] { "mains" }, new[] { "power" } },
            CircuitKind.PowerOutput => new[] { new[] { "motor" }, new[] { "power", "output" }, new[] { "output" }, new[] { "power" } },
            _ => new[] { new[] { "generic" }, new[] { "other" } },
        };

        foreach (var set in keywordSets)
        {
            var hit = types.FirstOrDefault(t => set.All(k => Has(t.Description, k) || Has(t.KeyCode, k)));
            if (hit != null) return hit;
        }

        // Nothing fits the kind: use the Miscellaneous circuit type, found by name in common languages.
        return types.FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t.Description ?? "",
                   @"miscell|divers|sonstig|varie|diversos|^\s*other\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
               ?? types.FirstOrDefault(t => System.Text.RegularExpressions.Regex.IsMatch(t.KeyCode ?? "",
                   @"^(misc|mis|msc|divers|other)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static bool Has(string text, string word)
    {
        if (string.IsNullOrEmpty(text)) return false;
        // Whole-word match so "no" does not hit "node" or "nominal".
        return System.Text.RegularExpressions.Regex.IsMatch(text, $@"(?<![A-Za-z]){System.Text.RegularExpressions.Regex.Escape(word)}(?![A-Za-z])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
}
