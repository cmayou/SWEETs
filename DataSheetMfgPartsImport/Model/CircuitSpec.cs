using System.Collections.Generic;
using System.Linq;

namespace DataSheetMfgPartsImport.Model;

/// <summary>Electrical function of a circuit. Mapped to a SOLIDWORKS Electrical circuit type in the preview.</summary>
public enum CircuitKind
{
    Coil,
    MainPole,      // power contact / breaker pole
    AuxNO,         // normally open auxiliary contact
    AuxNC,         // normally closed auxiliary contact
    Changeover,    // CO / SPDT relay contact
    Pin,           // connector pin / terminal position
    PowerInput,    // drive / power supply input (L1, L2, L3)
    PowerOutput,   // drive / power supply output (U, V, W)
    Generic,
}

public sealed class CircuitSpec
{
    public CircuitKind Kind { get; set; }

    /// <summary>Terminal marks in order, e.g. ["A1","A2"] or ["13","14"] or ["11","12","14"].</summary>
    public List<string> Terminals { get; set; } = new();

    /// <summary>True when the marks were read from the PDF; false when derived from the contact configuration.</summary>
    public bool Explicit { get; set; }

    public string Source { get; set; }

    public CircuitSpec Clone() => new()
    {
        Kind = Kind, Terminals = Terminals.ToList(), Explicit = Explicit, Source = Source,
    };

    public static string KindLabel(CircuitKind k) => k switch
    {
        CircuitKind.Coil => "Coil",
        CircuitKind.MainPole => "Main pole",
        CircuitKind.AuxNO => "NO contact",
        CircuitKind.AuxNC => "NC contact",
        CircuitKind.Changeover => "Changeover",
        CircuitKind.Pin => "Pin",
        CircuitKind.PowerInput => "Power input",
        CircuitKind.PowerOutput => "Power output",
        _ => "Circuit",
    };

    public override string ToString() => $"{KindLabel(Kind)} {string.Join("-", Terminals)}";
}
