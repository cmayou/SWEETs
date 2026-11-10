using System.Collections.Generic;
using DataSheetMfgPartsImport.Extraction;
using DataSheetMfgPartsImport.Model;

namespace DataSheetMfgPartsImport.Services;

/// <summary>
/// Everything the importer dialog needs from SOLIDWORKS Electrical. The live implementation is
/// <see cref="ElectricalCatalogService"/>. Every call must run on the SOLIDWORKS Electrical UI thread.
/// </summary>
public interface ICatalogService
{
    string ConnectionLabel { get; }
    bool IsLive { get; }

    IReadOnlyList<ClassCandidate> GetComponentClasses();
    IReadOnlyList<CircuitTypeInfo> GetCircuitTypes();

    /// <summary>Returns null when no part with this manufacturer + reference exists in the catalog.</summary>
    ExistingPartInfo FindExisting(string manufacturer, string reference);

    BuildOutcome Build(PartDraft draft);
}

public sealed class CircuitTypeInfo
{
    public int Id { get; set; }
    public string KeyCode { get; set; }
    public string Description { get; set; }
    public string Display => string.IsNullOrEmpty(Description) ? (string.IsNullOrEmpty(KeyCode) ? $"Type {Id}" : KeyCode)
        : string.IsNullOrEmpty(KeyCode) ? Description : $"{Description}  ({KeyCode})";
    public override string ToString() => Display;
}

public sealed class ExistingPartInfo
{
    public string Reference { get; set; }
    public string Manufacturer { get; set; }
    public string Description { get; set; }
    public int CircuitCount { get; set; }
}

public enum DuplicateAction { Skip, Overwrite }

/// <summary>A reviewed part, ready to be written to the catalog.</summary>
public sealed class PartDraft
{
    public Dictionary<PartField, string> Values { get; } = new();
    public int ClassId { get; set; }
    public List<CircuitDraft> Circuits { get; } = new();
    public bool Exists { get; set; }
    public DuplicateAction DuplicateAction { get; set; }
    public string DatasheetPath { get; set; }

    public string Get(PartField f) => Values.TryGetValue(f, out var v) ? v ?? "" : "";
}

public sealed class CircuitDraft
{
    public CircuitKind Kind { get; set; }
    public string CircuitTypeKeyCode { get; set; }
    public List<string> Terminals { get; } = new();
}

public enum BuildStatus { Created, Updated, Skipped, Failed }

public sealed class BuildOutcome
{
    public BuildStatus Status { get; set; }
    public string Message { get; set; }
    public List<string> Warnings { get; } = new();
}
