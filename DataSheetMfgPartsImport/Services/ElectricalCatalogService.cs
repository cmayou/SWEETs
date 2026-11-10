using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using DataSheetMfgPartsImport.Archive;
using DataSheetMfgPartsImport.Extraction;
using DataSheetMfgPartsImport.Interop;
using DataSheetMfgPartsImport.Model;
using DataSheetMfgPartsImport.Settings;

namespace DataSheetMfgPartsImport.Services;

/// <summary>
/// Live access to the SOLIDWORKS Electrical manufacturer parts manager. All calls must run on the
/// SOLIDWORKS Electrical UI thread (the importer dialog does this).
/// A part is saved in this order: fields, class and library, insert(); then its circuits and terminals; then update().
/// </summary>
public sealed class ElectricalCatalogService : ICatalogService
{
    private readonly IEwApplicationX _app;
    private readonly AppSettings _settings;
    private List<ClassCandidate> _classes;
    private List<CircuitTypeInfo> _circuitTypes;
    private List<string> _archiveLanguages;
    private string _language;

    public ElectricalCatalogService(IEwApplicationX app, AppSettings settings)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _settings = settings;
    }

    public bool IsLive => true;

    public string ConnectionLabel
    {
        get
        {
            try { return $"SOLIDWORKS Electrical {_app.getApplicationVersion()}"; }
            catch { return "SOLIDWORKS Electrical"; }
        }
    }

    private IEwEnvironmentX Env()
    {
        var env = _app.getEwEnvironment(out var e);
        return Com.Require(env, e, "getEwEnvironment");
    }

    private IEwManufacturerPartManagerX PartManager(IEwEnvironmentX env)
    {
        var mgr = env.getEwManufacturerPartManager(out var e);
        return Com.Require(mgr, e, "getEwManufacturerPartManager");
    }

    // ------------------------------------------------------------------ language

    private void EnsureLanguages()
    {
        if (_archiveLanguages != null) return;
        _archiveLanguages = ArchiveInfo.GetLanguages(_app);
        _language = ArchiveInfo.PickLanguage(_settings.Language, _archiveLanguages);
    }

    private string Language
    {
        get { EnsureLanguages(); return _language; }
    }

    /// <summary>The configured language first, then every archive language, then English spellings.</summary>
    private IEnumerable<string> Languages()
    {
        EnsureLanguages();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<string> { _language };
        all.AddRange(_archiveLanguages);
        all.Add("en");
        all.Add("EN");
        foreach (var l in all)
            if (!string.IsNullOrEmpty(l) && seen.Add(l)) yield return l;
    }

    // ------------------------------------------------------------------ classes

    public IReadOnlyList<ClassCandidate> GetComponentClasses()
    {
        if (_classes != null) return _classes;
        _classes = new List<ClassCandidate>();
        try
        {
            EnsureLanguages();
            var nodes = ArchiveInfo.LoadClasses(_app, _language, _archiveLanguages);
            _classes = nodes.Select(n => new ClassCandidate { Id = n.Id, Name = n.Name, Path = n.Path }).ToList();
            Log.Info($"Loaded {_classes.Count} component classes.");
        }
        catch (Exception ex)
        {
            Log.Error("Loading component classes failed", ex);
        }
        return _classes;
    }

    // ------------------------------------------------------------------ circuit types

    public IReadOnlyList<CircuitTypeInfo> GetCircuitTypes()
    {
        if (_circuitTypes != null) return _circuitTypes;
        _circuitTypes = new List<CircuitTypeInfo>();
        IEwEnvironmentX env = null;
        try
        {
            env = Env();
            var mgr = env.getEwCircuitTypeManagerX(out var e);
            Com.Require(mgr, e, "getEwCircuitTypeManagerX");

            // SOLIDWORKS Electrical 2026 returns circuit type IDs (Int32) from getEwCircuitTypeArray, not objects.
            object typeArray = mgr.getEwCircuitTypeArray(out e);
            var items = new List<object>();
            if (typeArray is Array arr) foreach (var o in arr) if (o != null) items.Add(o);
            Log.Info($"getEwCircuitTypeArray: {e}, {items.Count} item(s), element type {(items.Count > 0 ? items[0].GetType().FullName : "-")}");

            foreach (var item in items)
            {
                object obj = item;
                var id = 0;
                if (item is int || item is short || item is long)
                {
                    id = Convert.ToInt32(item);
                    obj = mgr.getEwCircuitTypeByID(id, out var be);
                    if (!Com.Ok(be)) obj = null;
                }
                var info = obj != null ? Read(obj) : new CircuitTypeInfo();
                if (info.Id == 0) info.Id = id;
                _circuitTypes.Add(info);
            }
            var resolved = _circuitTypes.Count(c => !string.IsNullOrWhiteSpace(c.KeyCode) || !string.IsNullOrWhiteSpace(c.Description));
            Log.Info($"Circuit types via API: {resolved}/{_circuitTypes.Count} with key code or description.");
        }
        catch (Exception ex)
        {
            Log.Error("Loading circuit types through the API failed", ex);
        }

        // Fallback: key codes and descriptions straight from the application database
        // (tew_element.ele_keycode, tew_translatedtext.tra_objectid = ele_id).
        if (_circuitTypes.Count > 0 && _circuitTypes.Any(c => string.IsNullOrWhiteSpace(c.KeyCode) || string.IsNullOrWhiteSpace(c.Description)) && env != null)
        {
            try
            {
                CircuitTypeDatabase.Fill(env, _circuitTypes, Language);
            }
            catch (Exception ex)
            {
                Log.Error("Reading circuit types from the database failed", ex);
            }
        }

        _circuitTypes = _circuitTypes
            .Where(c => !string.IsNullOrWhiteSpace(c.KeyCode) || !string.IsNullOrWhiteSpace(c.Description))
            .OrderBy(c => c.Description ?? c.KeyCode, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        Log.Info($"Loaded {_circuitTypes.Count} circuit types. First: {string.Join("; ", _circuitTypes.Take(5).Select(c => c.Display))}");
        return _circuitTypes;
    }

    /// <summary>Reads id / key code / description from a circuit type object, typed or late-bound.</summary>
    private CircuitTypeInfo Read(object o)
    {
        var typed = o as IEwCircuitTypeX;
        var info = new CircuitTypeInfo
        {
            Id = typed != null ? typed.getID() : Com.CallInt(o, "getID"),
            KeyCode = typed != null ? typed.getKeyCode(out _) : Com.CallWithError(o, "getKeyCode") as string,
        };
        foreach (var lang in Languages())
        {
            string desc;
            if (typed != null)
            {
                desc = typed.getDescription(lang, out var de);
                if (!Com.Ok(de)) desc = null;
            }
            else desc = Com.CallWithError(o, "getDescription", lang) as string;
            if (!string.IsNullOrWhiteSpace(desc)) { info.Description = desc.Trim(); break; }
        }
        return info;
    }

    // ------------------------------------------------------------------ duplicates

    public ExistingPartInfo FindExisting(string manufacturer, string reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            var part = PartManager(Env()).findByManufacturerAndReference(manufacturer ?? "", reference, out var e);
            if (!Com.Ok(e) || part == null) return null;
            return new ExistingPartInfo
            {
                Reference = part.getReference(out _),
                Manufacturer = part.getManufacturer(out _),
                Description = part.getDescription(Language, out _),
                CircuitCount = part.getEwManufacturerPartCircuitCount(),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Duplicate check for {manufacturer}/{reference} failed: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ build

    public BuildOutcome Build(PartDraft d)
    {
        var outcome = new BuildOutcome();
        var reference = d.Get(PartField.Reference).Trim();
        var manufacturer = d.Get(PartField.Manufacturer).Trim();

        if (d.Exists && d.DuplicateAction == DuplicateAction.Skip)
        {
            outcome.Status = BuildStatus.Skipped;
            outcome.Message = "Already exists (skipped)";
            return outcome;
        }

        try
        {
            var env = Env();
            var mgr = PartManager(env);
            IEwManufacturerPartX part = null;
            var isNew = true;

            if (d.Exists)
            {
                part = mgr.findByManufacturerAndReference(manufacturer, reference, out var fe);
                isNew = !(Com.Ok(fe) && part != null);
            }
            if (isNew) part = mgr.newEwManufacturerPart();
            if (part == null) throw new EwApiException("newEwManufacturerPart", EwErrorCode.EW_UNDEFINED_ERROR);

            // Identity (mandatory)
            Com.Require(part.setReference(reference), "setReference");
            Com.Require(part.setManufacturer(manufacturer), "setManufacturer");
            if (isNew) Soft(outcome, part.setEwManufacturerPartType(EwManufacturerPartType.kManufacturerPartBase), "setEwManufacturerPartType");

            ApplyFields(part, d, outcome);
            ApplyClass(part, d.ClassId, outcome);
            var library = ApplyLibrary(env, part, outcome);

            Com.Require(isNew ? part.insert() : part.update(), isNew ? "insert" : "update");

            // Circuits are added after the part exists, then saved with update().
            var added = 0;
            if (d.Circuits.Count > 0)
            {
                if (!isNew)
                    for (var i = part.getEwManufacturerPartCircuitCount() - 1; i >= 0; i--)
                        Soft(outcome, part.deleteEwManufacturerPartCircuitAt(i), "deleteEwManufacturerPartCircuitAt");

                foreach (var c in d.Circuits)
                {
                    if (string.IsNullOrWhiteSpace(c.CircuitTypeKeyCode))
                    {
                        outcome.Warnings.Add($"{CircuitSpec.KindLabel(c.Kind)} skipped: no circuit type selected.");
                        continue;
                    }
                    var circuit = part.addEwManufacturerPartCircuit(c.CircuitTypeKeyCode, out var ce);
                    if (!Com.Ok(ce) || circuit == null)
                    {
                        outcome.Warnings.Add($"{CircuitSpec.KindLabel(c.Kind)} ({c.CircuitTypeKeyCode}): {ce}");
                        continue;
                    }
                    added++;
                    foreach (var mark in c.Terminals.Where(t => !string.IsNullOrWhiteSpace(t)))
                    {
                        var term = circuit.addEwManufacturerPartTerminal(out var te);
                        if (!Com.Ok(te) || term == null) { outcome.Warnings.Add($"Terminal {mark}: {te}"); continue; }
                        Soft(outcome, term.setText(mark.Trim()), $"terminal {mark}");
                    }
                }
                Com.Require(part.update(), "update (circuits)");
            }

            Verify(mgr, manufacturer, reference, added, library, outcome);

            outcome.Status = isNew ? BuildStatus.Created : BuildStatus.Updated;
            outcome.Message = outcome.Warnings.Count == 0
                ? (isNew ? "Saved to the library" : "Updated")
                : $"{(isNew ? "Saved" : "Updated")} with {outcome.Warnings.Count} warning(s)";
            Log.Info($"{outcome.Status}: {manufacturer} {reference}. {string.Join("; ", outcome.Warnings)}");
        }
        catch (Exception ex)
        {
            outcome.Status = BuildStatus.Failed;
            outcome.Message = ex is EwApiException ? ex.Message : $"Unexpected error: {ex.Message}";
            Log.Error($"Save failed for {manufacturer} {reference}", ex);
        }
        return outcome;
    }

    /// <summary>Reads the part back to prove it was kept, with the circuits and library that were set.</summary>
    private static void Verify(IEwManufacturerPartManagerX mgr, string manufacturer, string reference, int circuits, string library, BuildOutcome o)
    {
        try
        {
            var check = mgr.findByManufacturerAndReference(manufacturer, reference, out var e);
            if (!Com.Ok(e) || check == null)
            {
                o.Warnings.Add("The part was saved but could not be found afterwards.");
                return;
            }
            if (circuits > 0)
            {
                var stored = check.getEwManufacturerPartCircuitCount();
                if (stored != circuits) o.Warnings.Add($"Circuits: {circuits} added but the manufacturer parts manager reports {stored}.");
            }
            if (!string.IsNullOrEmpty(library))
            {
                var lib = check.getLibraryCode(out var le) ?? "";
                if (Com.Ok(le) && !string.Equals(lib, library, StringComparison.OrdinalIgnoreCase))
                    o.Warnings.Add($"Library: the manufacturer parts manager reports '{lib}' instead of '{library}'.");
            }
        }
        catch (Exception ex)
        {
            o.Warnings.Add($"Could not read the part back: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ fields

    private void ApplyFields(IEwManufacturerPartX part, PartDraft d, BuildOutcome o)
    {
        var lang = Language;

        // Group the values by destination so that two fields mapped to the same target are combined.
        var byTarget = new Dictionary<string, List<KeyValuePair<PartField, string>>>();
        foreach (var kv in d.Values)
        {
            if (PartKeys.IsFixed(kv.Key)) continue;
            var value = (kv.Value ?? "").Trim();
            if (value.Length == 0) continue;
            var key = PartKeys.KeyOf(kv.Key);
            if (key == null) continue;
            var target = _settings.MapTarget(key);
            if (target == "none") continue;
            if (!byTarget.TryGetValue(target, out var list)) byTarget[target] = list = new List<KeyValuePair<PartField, string>>();
            list.Add(new KeyValuePair<PartField, string>(kv.Key, value));
        }

        foreach (var kv in byTarget)
        {
            var label = string.Join(" + ", kv.Value.Select(x => PartFieldInfo.Get(x.Key).Label));
            var text = kv.Value.Count == 1
                ? kv.Value[0].Value
                : string.Join("; ", kv.Value.Select(x => PartFieldInfo.Get(x.Key).Label + ": " + x.Value));
            try { Apply(part, kv.Key, text, lang, o, label); }
            catch (COMException ex) { o.Warnings.Add($"{label}: {ex.Message}"); }
        }

        if (_settings.ImportNoteSlot > 0 && !string.IsNullOrEmpty(d.DatasheetPath))
            Soft(o, part.setUserData(_settings.ImportNoteSlot, $"Imported from {Path.GetFileName(d.DatasheetPath)} {DateTime.Now:yyyy-MM-dd}"), "setUserData(note)");

        var sheet = DatasheetPathFor(d.DatasheetPath, o);
        if (!string.IsNullOrEmpty(sheet))
            Soft(o, part.setDataSheetFilePath(sheet), "setDataSheetFilePath");
    }

    private static void Apply(IEwManufacturerPartX part, string target, string text, string lang, BuildOutcome o, string label)
    {
        EwErrorCode ec;
        switch (target)
        {
            case "description":
                if (string.IsNullOrEmpty(lang)) { o.Warnings.Add($"{label}: no archive language available."); return; }
                ec = part.setDescription(lang, text); break;
            case "commercialRef":
                if (string.IsNullOrEmpty(lang)) { o.Warnings.Add($"{label}: no archive language available."); return; }
                ec = part.setCommercialReference(lang, text); break;
            case "serie": ec = part.setSerie(text); break;
            case "stock": ec = part.setStockNumber(text); break;
            case "article": ec = part.setArticleNumber(text); break;
            case "supplier": ec = part.setSupplierName(text); break;
            case "useVoltage": ec = part.setUseVoltage(text); break;
            case "useFrequency": ec = part.setUseFrequency(text); break;
            case "controlVoltage": ec = part.setControlVoltage(text); break;
            case "controlFrequency": ec = part.setControlFrequency(text); break;
            case "width":
                if (!TryNumber(text, label, o, out var w)) return;
                ec = part.setWidth(w); break;
            case "height":
                if (!TryNumber(text, label, o, out var h)) return;
                ec = part.setHeight(h); break;
            case "depth":
                if (!TryNumber(text, label, o, out var dp)) return;
                ec = part.setDepth(dp); break;
            case "weight":
                if (!TryNumber(text, label, o, out var wt)) return;
                ec = part.setWeight(wt); break;
            default:
                int n;
                if (target.StartsWith("value", StringComparison.Ordinal) && int.TryParse(target.Substring(5), out n))
                    ec = part.setValue(n, text);
                else if (target.StartsWith("user", StringComparison.Ordinal) && int.TryParse(target.Substring(4), out n))
                    ec = part.setUserData(n, text);
                else { o.Warnings.Add($"{label}: unknown target '{target}'."); return; }
                break;
        }
        if (ec != EwErrorCode.EW_NO_ERROR) o.Warnings.Add($"{label} ({target}): {ec}");
    }

    private static bool TryNumber(string text, string label, BuildOutcome o, out double value)
    {
        value = 0;
        var m = Regex.Match(text ?? "", @"[-+]?\d+(?:[.,]\d+)?");
        if (m.Success && double.TryParse(m.Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        o.Warnings.Add($"{label}: '{text}' is not a number.");
        return false;
    }

    private static void ApplyClass(IEwManufacturerPartX part, int classId, BuildOutcome o)
    {
        if (classId <= 0) { o.Warnings.Add("No class selected."); return; }
        // setClassID takes the class object ID (IEwClassX.getID). Fall back to the node ID setter if refused.
        var e = part.setClassID(classId);
        if (!Com.Ok(e)) Soft(o, part.setClassNodeID(classId), "setClassID/setClassNodeID");
    }

    private string DatasheetPathFor(string source, BuildOutcome o)
    {
        if (string.IsNullOrEmpty(source) || !File.Exists(source)) return source;
        var folder = _settings.CopyDatasheetToFolder;
        if (string.IsNullOrWhiteSpace(folder)) return source;
        try
        {
            Directory.CreateDirectory(folder);
            var target = Path.Combine(folder, Path.GetFileName(source));
            if (!File.Exists(target)) File.Copy(source, target);
            return target;
        }
        catch (Exception ex)
        {
            o.Warnings.Add($"Datasheet copy failed: {ex.Message}");
            return source;
        }
    }

    // ------------------------------------------------------------------ library

    /// <summary>Puts the part in the configured library, creating the library when it does not exist. Returns the library used.</summary>
    private string ApplyLibrary(IEwEnvironmentX env, IEwManufacturerPartX part, BuildOutcome o)
    {
        var code = (_settings.LibraryCode ?? "").Trim();
        if (code.Length == 0) return null;
        if (!EnsureLibrary(env, code, o, out var actual)) return null;
        var e = part.setLibraryCode(actual);
        if (!Com.Ok(e)) { o.Warnings.Add($"Library: setting '{actual}' returned {e}."); return null; }
        return actual;
    }

    /// <summary>Finds the library by name, or creates it as a manufacturer part library. False when it cannot be used.</summary>
    private bool EnsureLibrary(IEwEnvironmentX env, string code, BuildOutcome o, out string actual)
    {
        actual = code;
        try
        {
            var mgr = env.getEwLibraryManager(out var ec);
            if (!Com.Ok(ec) || mgr == null)
            {
                o.Warnings.Add($"Library: the library manager is not available ({ec}), so '{code}' was not applied.");
                return false;
            }

            // Like the circuit types, libraries can come back as IDs (Int32) rather than objects.
            if (mgr.getEwLibraryArray(out ec) is Array libs)
            {
                foreach (var item in libs)
                {
                    IEwLibraryX lib = item as IEwLibraryX;
                    if (lib == null && (item is int || item is short || item is long))
                    {
                        lib = mgr.findEwLibraryById(Convert.ToInt32(item), out var fe);
                        if (!Com.Ok(fe)) lib = null;
                    }
                    if (lib == null) continue;
                    var name = (lib.getName(out var e2) ?? "").Trim();
                    if (!string.Equals(name, code, StringComparison.OrdinalIgnoreCase)) continue;

                    var content = lib.getEwLibContentType(out e2);
                    if (Com.Ok(e2) && ((int)content & (int)EwLibContentType.kTypeReference) == 0)
                        o.Warnings.Add($"Library: '{name}' exists but does not hold manufacturer parts.");
                    actual = name;
                    return true;
                }
            }

            var created = mgr.newEwLibrary();
            if (created == null) { o.Warnings.Add($"Library: could not create '{code}'."); return false; }
            var cr = created.setName(code);
            if (Com.Ok(cr) && !string.IsNullOrEmpty(Language)) created.setDescription(Language, code);
            if (Com.Ok(cr)) cr = created.setVersionNumber(1);
            if (Com.Ok(cr)) cr = created.setEwLibContentType(EwLibContentType.kTypeReference);
            if (Com.Ok(cr)) cr = created.insert();
            if (!Com.Ok(cr))
            {
                o.Warnings.Add($"Library: creating '{code}' failed ({cr}), so the part was not put in a library.");
                return false;
            }
            return true;
        }
        catch (COMException ex)
        {
            o.Warnings.Add($"Library: {ex.Message}");
            return false;
        }
    }

    private static void Soft(BuildOutcome o, EwErrorCode e, string what)
    {
        if (!Com.Ok(e)) o.Warnings.Add($"{what}: {e}");
    }
}
