using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SWEETs.SwePdfSchematic.Core.Build;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Infrastructure;
using SWEETs.SwePdfSchematic.Core.Model;
using C = SWEETs.SwePdfSchematic.AddIn.Interop.SweContract;

namespace SWEETs.SwePdfSchematic.AddIn.Interop
{
    /// <summary>
    /// <see cref="ISchematicTarget"/> over the SOLIDWORKS Electrical API. Must be called on SWE's UI thread.
    /// Cross-references come from component association: every symbol of one tag is attached to the same
    /// component (setObjectID), and SWE generates the parent/child cross-references itself.
    /// </summary>
    public sealed class SweTarget : ISchematicTarget
    {
        private const string Lang = "en";
        private readonly object _app;
        private object _project;
        private int _projectId;
        private int _bookId;
        private readonly Dictionary<string, object> _files = new Dictionary<string, object>();
        private readonly Dictionary<string, int> _fileIds = new Dictionary<string, int>();
        private readonly Dictionary<string, object> _symbols = new Dictionary<string, object>();
        private readonly Dictionary<string, int> _components = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly StringBuilder _deferred = new StringBuilder("kind,page,x_mm,y_mm,value,partner\r\n");
        private int _n;
        private DrawingUnits _units = DrawingUnits.Millimeters;

        public SweTarget(object ewApplication) { _app = ewApplication; }

        public int ProjectId => _projectId;
        public string DeferredReportPath { get; private set; }

        public void CreateProject(ProjectSpec spec)
        {
            _units = spec.Units;
            var env = SweApi.Call(_app, C.AppGetEnvironment);
            var pm = SweApi.Call(env, C.EnvGetProjectManager);

            if (!string.IsNullOrEmpty(spec.TemplateArchivePath))
            {
                var ids = (Array)SweApi.Call(pm, C.PmUnarchive, spec.TemplateArchivePath, true);
                _projectId = Convert.ToInt32(ids.GetValue(0));
                _project = SweApi.Call(pm, C.PmFindById, _projectId);
                SweApi.Call(_project, C.ProjSetName, spec.Name);
                SweApi.TryCall(_project, C.ObjUpdate, out _);
            }
            else
            {
                _project = SweApi.Call(pm, C.PmNewProject);
                SweApi.Call(_project, C.ProjSetName, spec.Name);
                SweApi.TryCall(_project, C.ProjSetDescription, out _, Lang, spec.Description ?? "");
                InsertTemplate(spec);
                _projectId = Convert.ToInt32(SweApi.Call(_project, C.ObjGetId));
            }

            SweApi.Call(_app, C.AppOpenProjectId, _projectId);
            _bookId = EnsureBook();
            FileLog.Write("build", $"Project {spec.Name} id={_projectId} book={_bookId}; title block: {string.Join("; ", spec.TitleBlock.Select(kv => kv.Key + "=" + kv.Value))}");
        }

        /// <summary>
        /// insertFromTemplate takes the template as SOLIDWORKS Electrical names it; some versions expect the template
        /// file instead. Try the name, then the file picked from the template folder.
        /// </summary>
        private void InsertTemplate(ProjectSpec spec)
        {
            try { SweApi.Call(_project, C.ProjInsertFromTemplate, spec.TemplateName); }
            catch (SweApiException first) when (!string.IsNullOrEmpty(spec.TemplatePath))
            {
                FileLog.Write("build", $"Template by name \"{spec.TemplateName}\" failed ({first.Message}); trying file {spec.TemplatePath}");
                SweApi.Call(_project, C.ProjInsertFromTemplate, spec.TemplatePath);
            }
        }

        private int EnsureBook()
        {
            var bm = SweApi.Call(_project, C.ProjGetBookManager);
            if (SweApi.TryCall(bm, C.BookArray, out var arr) && arr is Array books && books.Length > 0)
                return Convert.ToInt32(SweApi.Call(books.GetValue(0), C.ObjGetId));

            var book = SweApi.Call(bm, C.BookNew);
            SweApi.TryCall(book, C.BookSetDescription, out _, Lang, "Imported schematics");
            SweApi.Call(book, C.ObjInsert);
            return Convert.ToInt32(SweApi.Call(book, C.ObjGetId));
        }

        public string AddSchemePage(PagePlan page)
        {
            var fm = SweApi.Call(_project, C.ProjGetFileManager);
            var file = SweApi.Call(fm, C.FileNew);
            SweApi.Call(file, C.FileSetType, SweApi.EnumValue("EwFileType", "kFileFolio"));
            SweApi.Call(file, C.FileSetBook, _bookId);
            SweApi.TryCall(file, C.FileSetDescription, out _, Lang, page.Title);
            SweApi.Call(file, C.ObjInsert);
            SweApi.TryCall(file, C.FileOpen, out _);

            var h = "page" + (++_n);
            _files[h] = file;
            _fileIds[h] = Convert.ToInt32(SweApi.Call(file, C.ObjGetId));
            return h;
        }

        public string EnsureComponent(ComponentPlan c)
        {
            if (_components.ContainsKey(c.Tag)) return c.Tag;
            var cm = SweApi.Call(_project, C.ProjGetComponentManager);
            var comp = SweApi.Call(cm, C.ComponentNew);
            SweApi.Call(comp, C.ComponentSetTag, c.Mark ?? c.Tag);
            SweApi.TryCall(comp, C.ComponentSetDescription, out _, Lang, c.Category ?? "");
            var notes = new List<string>();
            switch (c.Role)
            {
                case ComponentRole.TerminalStrip:
                    if (!SetType(comp, "kComponentTerminalStrip")) notes.Add("type terminal strip refused");
                    break;
                case ComponentRole.Terminal:
                    if (!SetType(comp, "kComponentTerminal")) notes.Add("type terminal refused");
                    if (c.ParentTag != null)
                    {
                        if (!_components.TryGetValue(c.ParentTag, out var stripId))
                            stripId = _components[EnsureComponent(new ComponentPlan { Tag = c.ParentTag, Role = ComponentRole.TerminalStrip, Category = "Terminal strip" })];
                        if (!SweApi.TryCall(comp, C.ComponentSetParent, out _, stripId)) notes.Add($"not placed under strip {c.ParentTag}");
                    }
                    break;
            }
            if (c.Role != ComponentRole.Component)
                try { SweApi.Call(comp, C.TagObjSetTagMode, SweApi.EnumValue("EwTagMode", "kManu")); } catch (SweApiException) { }
            var cls = Classify(comp, c.SweSymbolName);
            if (cls != null) notes.Add(cls);
            SweApi.Call(comp, C.ObjInsert);
            _components[c.Tag] = Convert.ToInt32(SweApi.Call(comp, C.ObjGetId));
            FileLog.Write("build", $"component {c.Tag}" + (c.Mark != null ? $" (mark {c.Mark})" : "") + $" {c.Role}" + (notes.Count > 0 ? ": " + string.Join("; ", notes) : ""));
            return c.Tag;
        }

        private static bool SetType(object comp, string type)
        {
            try { SweApi.Call(comp, C.ComponentSetType, SweApi.EnumValue("EwComponentType", type)); return true; }
            catch (SweApiException) { return false; }
        }

        /// <summary>Gives the component the classification of the library symbol mapped to it. Returns a note, or null.</summary>
        private string Classify(object comp, string sweSymbolName)
        {
            if (string.IsNullOrEmpty(sweSymbolName)) return null;
            var sym = SweSymbolLibrary.Get(_app).Find(sweSymbolName);
            if (sym == null) return $"class not set ({sweSymbolName} not in the library list)";
            bool ok = false;
            if (sym.ClassId > 0) ok |= SweApi.TryCall(comp, C.ComponentSetClassId, out _, sym.ClassId);
            if (sym.ClassNodeId > 0) ok |= SweApi.TryCall(comp, C.ComponentSetClassNodeId, out _, sym.ClassNodeId);
            return ok ? $"class {sym.Category ?? sym.ClassId.ToString()} from {sweSymbolName}" : $"class of {sweSymbolName} not accepted";
        }

        // ------------------------------------------------------------------ manufacturer parts

        private SwePartCatalog _catalog;
        private SwePartCatalog Catalog => _catalog ?? (_catalog = new SwePartCatalog(_app));
        private readonly Dictionary<string, int> _locations = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public string AssignParts(string componentHandle, ComponentPlan c)
        {
            if (!_components.TryGetValue(componentHandle, out var id)) throw new SweApiException($"component {componentHandle} was not created.");
            var comp = SweApi.Call(SweApi.Call(_project, C.ProjGetComponentManager), C.ComponentFindById, id);
            return Assign(comp, c);
        }

        /// <summary>Assigns every part (quantity times) and reports what was found, created or refused.</summary>
        private string Assign(object comp, ComponentPlan c)
        {
            var notes = new List<string>();
            int assigned = 0;
            bool anyBase = c.Parts.Any(p => p.CarriesCircuits);
            foreach (var p in c.Parts)
            {
                CatalogMatch part;
                try { part = Catalog.Ensure(p.Manufacturer, p.Reference, p.Description, p.ArticleNumber, accessory: anyBase && !p.CarriesCircuits); }
                catch (SweApiException ex) { notes.Add($"{p.Reference}: not in the catalog and could not be created ({ex.Message})"); continue; }
                if (part.Created) notes.Add($"{part.Manufacturer} {part.Reference} added to the catalog");
                if (p.CarriesCircuits && c.Symbols.Count > 0)
                {
                    try { var n = AddCircuits(part, c); if (n != null) notes.Add(n); }
                    catch (SweApiException ex) { notes.Add($"{p.Reference}: circuits not added ({ex.Message})"); }
                }
                for (int i = 0; i < p.Quantity; i++)
                {
                    try { SweApi.Call(comp, C.ComponentAssignPart, part.Manufacturer ?? p.Manufacturer ?? "", part.Reference ?? p.Reference); assigned++; }
                    catch (SweApiException ex) { notes.Add($"{p.Reference}: not assigned ({ex.Message})"); break; }
                }
            }
            SweApi.TryCall(comp, C.ObjUpdate, out _);
            var msg = $"{c.Tag}: {assigned} part{(assigned == 1 ? "" : "s")} assigned ({string.Join(", ", c.Parts)})" + (notes.Count > 0 ? "; " + string.Join("; ", notes) : "") + ".";
            FileLog.Write("parts", msg);
            if (assigned == 0 && c.Parts.Count > 0) throw new SweApiException(msg);
            return msg;
        }

        /// <summary>
        /// Gives a catalog part without circuits the circuits of the component's inserted symbols (one per symbol circuit,
        /// same circuit type, in symbol order), and names every circuit terminal with the designation printed at that pin in
        /// the PDF. Parts that already have circuits are left as they are. Returns a note for the log.
        /// </summary>
        private string AddCircuits(CatalogMatch part, ComponentPlan c)
        {
            int existing = SweApi.TryCall(part.Item, C.PartGetCircuitCount, out var cnt) && cnt != null ? Convert.ToInt32(cnt) : 0;
            if (existing > 0) return $"{part.Reference}: catalog part already has {existing} circuit{(existing == 1 ? "" : "s")}, kept as is";

            int circuits = 0, terminals = 0, named = 0, symbolNo = 0;
            var designations = new List<string>();
            foreach (var placed in c.Symbols)
            {
                symbolNo++;
                if (!_symbols.TryGetValue(placed.Handle ?? "", out var sym)) continue;

                // circuits of the project symbol (from its library symbol), and its connection points per circuit
                var symCircuits = new List<(int number, string type)>();
                if (SweApi.TryCall(sym, C.SymbolCircuits, out var ca) && ca is IEnumerable cs)
                    foreach (var sc in cs)
                    {
                        if (!SweApi.TryCall(sc, C.SymbolCircuitNumber, out var no) || !SweApi.TryCall(sc, C.SymbolCircuitKeyCode, out var key)) continue;
                        symCircuits.Add((Convert.ToInt32(no), key?.ToString()));
                    }
                var points = new List<(int circuit, int terminal, int point, Pt at)>();
                if (SweApi.TryCall(sym, C.SymbolPoints, out var pa) && pa is IEnumerable ps)
                    foreach (var pt in ps)
                    {
                        SweApi.TryCall(pt, C.PointGetCircuitNumber, out var cn);
                        SweApi.TryCall(pt, C.PointGetTerminalNumber, out var tn);
                        SweApi.TryCall(pt, C.PointGetPointNumber, out var pn);
                        if (!SweApi.TryCall(pt, C.PointGetPosition, out var pos) || pos == null) continue;
                        var at = new Pt(Convert.ToDouble(SweApi.Call(pos, C.PointGetX), CultureInfo.InvariantCulture),
                                        Convert.ToDouble(SweApi.Call(pos, C.PointGetY), CultureInfo.InvariantCulture));
                        points.Add((cn == null ? 0 : Convert.ToInt32(cn), tn == null ? 0 : Convert.ToInt32(tn), pn == null ? 0 : Convert.ToInt32(pn), at));
                    }

                foreach (var (number, type) in symCircuits.Where(x => !string.IsNullOrEmpty(x.type)).OrderBy(x => x.number))
                {
                    var pc = SweApi.Call(part.Item, C.PartAddCircuit, type);
                    SweApi.TryCall(pc, C.PartCircuitSetSymbolName, out _, placed.SweSymbolName);
                    SweApi.TryCall(pc, C.PartCircuitSetSymbolNumber, out _, symbolNo);
                    SweApi.TryCall(pc, C.PartCircuitSetSymbolCircuit, out _, number);
                    circuits++;
                    foreach (var p in points.Where(x => x.circuit == number).OrderBy(x => x.terminal).ThenBy(x => x.point))
                    {
                        var t = SweApi.Call(pc, C.PartCircuitAddTerminal);
                        terminals++;
                        var name = placed.PinNames.Where(n => n.at.DistanceTo(p.at) < 0.01 && !string.IsNullOrWhiteSpace(n.name)).Select(n => n.name).FirstOrDefault();
                        if (name != null && SweApi.TryCall(t, C.PartTerminalSetText, out _, name)) { named++; designations.Add(name); }
                    }
                }
            }
            if (circuits == 0) return $"{part.Reference}: the component's symbols have no circuits to copy";
            SweApi.Call(part.Item, C.ObjUpdate);
            var msg = $"{part.Reference}: {circuits} circuit{(circuits == 1 ? "" : "s")} and {terminals} terminals from {string.Join(", ", c.Symbols.Select(s => s.SweSymbolName).Distinct())}"
                      + (named > 0 ? $", terminals named {string.Join(" ", designations)} from the PDF" : ", no terminal designations found in the PDF");
            FileLog.Write("parts", msg);
            return msg;
        }

        public string CreateItem(ComponentPlan x)
        {
            switch (x.Kind)
            {
                case BomItemKind.Terminal:
                    {
                        // a terminal listed in the BOM but not drawn: created under its strip with its part
                        EnsureComponent(x);
                        var tc = SweApi.Call(SweApi.Call(_project, C.ProjGetComponentManager), C.ComponentFindById, _components[x.Tag]);
                        return $"Terminal {x.Mark} created under strip {x.ParentTag}. " + Assign(tc, x);
                    }
                case BomItemKind.Cable:
                    return CreateCable(x);
                default:
                    {
                        // cabinet items (rails, ducts) and location items (enclosure, mounting plate, glands)
                        int? loc = x.LocationTag != null ? EnsureLocation(x.LocationTag) : (int?)null;
                        var cm = SweApi.Call(_project, C.ProjGetComponentManager);
                        var comp = SweApi.Call(cm, C.ComponentNew);
                        SweApi.Call(comp, C.ComponentSetTag, x.Tag);
                        SweApi.TryCall(comp, C.ComponentSetDescription, out _, Lang, x.Description ?? "");
                        if (loc.HasValue) SweApi.TryCall(comp, C.ComponentSetLocation, out _, loc.Value);
                        SweApi.Call(comp, C.ObjInsert);
                        _components[x.Tag] = Convert.ToInt32(SweApi.Call(comp, C.ObjGetId));
                        return Assign(comp, x) + (loc.HasValue ? $" Placed in location {x.LocationTag}." : "");
                    }
            }
        }

        /// <summary>
        /// Creates the cable and its cable reference in the catalog when missing. The API has no call that sets a project
        /// cable's reference, so the reference is linked through a project manufacturer part when SWE accepts it.
        /// </summary>
        private string CreateCable(ComponentPlan x)
        {
            var part = x.Parts.FirstOrDefault();
            var cm = SweApi.Call(_project, C.ProjGetCableManager);
            var cable = SweApi.Call(cm, C.CableMgrNew);
            SweApi.Call(cable, C.ComponentSetTag, x.Tag);
            SweApi.TryCall(cable, C.CableSetDescription, out _, Lang, x.Description ?? part?.Description ?? "");
            if (!string.IsNullOrWhiteSpace(part?.ArticleNumber)) SweApi.TryCall(cable, C.CableSetArticleNumber, out _, part.ArticleNumber);
            if (x.LocationTag != null) SweApi.TryCall(cable, C.CableSetUpstreamLocation, out _, EnsureLocation(x.LocationTag));
            SweApi.Call(cable, C.ObjInsert);
            if (part == null) return $"Cable {x.Tag} created.";

            string note;
            try
            {
                var reference = Catalog.Ensure(part.Manufacturer, part.Reference, part.Description, part.ArticleNumber, cable: true);
                note = reference.Created ? $"cable reference {reference.Manufacturer} {reference.Reference} added to the catalog" : "cable reference found in the catalog";
                var id = Convert.ToInt32(SweApi.Call(cable, C.ObjGetId));
                bool linked = false;
                if (SweApi.TryCall(_project, C.ProjGetPartManager, out var pm) && SweApi.TryCall(pm, C.ProjPartNew, out var pp) && pp != null)
                    linked = SweApi.TryCall(pp, C.ProjPartSetManufacturer, out _, reference.Manufacturer)
                             && SweApi.TryCall(pp, C.ProjPartSetReference, out _, reference.Reference)
                             && SweApi.TryCall(pp, C.ProjPartSetObjectId, out _, id)
                             && SweApi.TryCall(pp, C.ObjInsert, out _);
                note += linked ? ", linked to the cable" : "; assign it to the cable in SOLIDWORKS Electrical (the API cannot set a cable's reference)";
            }
            catch (SweApiException ex) { note = $"cable reference {part.Reference} not available ({ex.Message})"; }
            var msg = $"Cable {x.Tag} created: {note}.";
            FileLog.Write("parts", msg);
            return msg;
        }

        /// <summary>Id of the project location with this tag, created when the project has none.</summary>
        private int EnsureLocation(string tag)
        {
            if (_locations.TryGetValue(tag, out var id)) return id;
            var lm = SweApi.Call(_project, C.ProjGetLocationManager);
            if (SweApi.TryCall(lm, C.LocMgrArray, out var arr) && arr is IEnumerable items)
                foreach (var loc in items)
                    if (SweApi.TryCall(loc, C.TagObjGetTag, out var t) && string.Equals(t?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                        return _locations[tag] = Convert.ToInt32(SweApi.Call(loc, C.ObjGetId));
            var created = SweApi.Call(lm, C.LocMgrNew);
            SweApi.Call(created, C.ComponentSetTag, tag);
            SweApi.Call(created, C.ObjInsert);
            FileLog.Write("parts", $"location {tag} created");
            return _locations[tag] = Convert.ToInt32(SweApi.Call(created, C.ObjGetId));
        }

        public string InsertSymbol(string pageHandle, SymbolPlan s, string componentHandle)
        {
            var sym = NewSymbol(_files[pageHandle], s);
            try
            {
                SweApi.Call(sym, C.SymbolSetX, s.Position.X);
                SweApi.Call(sym, C.SymbolSetY, s.Position.Y);
                SweApi.Call(sym, C.SymbolSetRotation, (double)s.Rotation);
                ScaleToDrawing(sym, s, log: true);
                if (componentHandle != null && _components.TryGetValue(componentHandle, out var compId)
                    && !SweApi.TryCall(sym, C.SymbolSetObject, out _, compId))
                    FileLog.Write("build", $"{s.SourceId} \"{s.SweSymbolName}\": not attached to component {componentHandle}; SOLIDWORKS Electrical assigns a mark.");
                SweApi.Call(sym, C.ObjInsert);
            }
            catch (SweApiException ex) { throw new SweApiException($"{s.SourceId} → \"{s.SweSymbolName}\": {ex.Message}", ex); }

            var h = "sym" + (++_n);
            _symbols[h] = sym;
            _symbolPage[h] = pageHandle;
            if (componentHandle != null && _components.TryGetValue(componentHandle, out var cid)) _symbolComponent[h] = cid;
            return h;
        }

        /// <summary>
        /// A library symbol drawn in millimetres placed in an inch drawing (or the reverse) comes in 25.4× too large
        /// (or small): scale it to the drawing's units.
        /// </summary>
        private void ScaleToDrawing(object sym, SymbolPlan s, bool log)
        {
            var m = SweSymbolLibrary.Get(_app).Find(s.SweSymbolName)?.Measurement;
            double f = m == "Metric" && _units == DrawingUnits.Inches ? 1 / 25.4
                     : m == "Imperial" && _units == DrawingUnits.Millimeters ? 25.4 : 1;
            if (f == 1) return;
            bool ok = SweApi.TryCall(sym, C.SymbolSetXScale, out _, f) & SweApi.TryCall(sym, C.SymbolSetYScale, out _, f);
            if (log) FileLog.Write("build", $"{s.SourceId} \"{s.SweSymbolName}\": {m.ToLowerInvariant()} symbol in {(_units == DrawingUnits.Inches ? "an inch" : "a millimetre")} drawing, scaled ×{f:0.####}{(ok ? "" : " — scale rejected by SOLIDWORKS Electrical")}.");
        }

        /// <summary>
        /// Creates the project symbol with the library symbol's own type (component, black box, connection/terminal…):
        /// a symbol created with newEwProjectSymbol is a component symbol, and setEwSymbolName rejects a library symbol of
        /// another type with EW_BAD_INPUTS.
        /// </summary>
        private object NewSymbol(object file, SymbolPlan s)
        {
            var lib = SweSymbolLibrary.Get(_app);
            var known = lib.Find(s.SweSymbolName);
            if (lib.IsLoaded && known == null)
                throw new SweApiException($"{s.SourceId}: \"{s.SweSymbolName}\" is not in the SOLIDWORKS Electrical symbol library — map it to a library symbol.");

            var types = known?.TypeCode != null
                ? new[] { known.TypeCode }
                : new[] { "kSymbolComponent", "kSymbolBlackbox", "kSymbolConnection", "kSymbolLink" };
            var errors = new List<string>();
            foreach (var t in types)
            {
                try
                {
                    var sym = SweApi.Call(file, C.FileNewSymbolOfType, SweApi.EnumValue("EwSymbolType", t));
                    SweApi.Call(sym, C.SymbolSetName, s.SweSymbolName);
                    return sym;
                }
                catch (SweApiException ex) { errors.Add($"{t}: {ex.Message}"); }
            }
            try
            {
                var sym = SweApi.Call(file, C.FileNewSymbol);
                SweApi.Call(sym, C.SymbolSetName, s.SweSymbolName);
                return sym;
            }
            catch (SweApiException ex)
            {
                errors.Add("newEwProjectSymbol: " + ex.Message);
                throw new SweApiException($"{s.SourceId} → \"{s.SweSymbolName}\" ({known?.Type ?? "type unknown"}) could not be placed: {string.Join("; ", errors)}", ex);
            }
        }

        public IList<Pt> GetSymbolPins(string symbolHandle) => ReadPins(_symbols[symbolHandle]);

        /// <summary>
        /// Pin layout of the library symbol relative to its insertion point (the first connection point of its first
        /// circuit), read from a symbol that is configured exactly like the real one but never inserted.
        /// </summary>
        public IList<Pt> GetPinOffsets(string pageHandle, SymbolPlan s)
        {
            try
            {
                var sym = NewSymbol(_files[pageHandle], s);
                SweApi.Call(sym, C.SymbolSetX, s.Position.X);
                SweApi.Call(sym, C.SymbolSetY, s.Position.Y);
                SweApi.Call(sym, C.SymbolSetRotation, (double)s.Rotation);
                ScaleToDrawing(sym, s, log: false);
                var pins = ReadPins(sym);
                return pins.Select(p => new Pt(p.X - s.Position.X, p.Y - s.Position.Y)).ToList();
            }
            catch (Exception ex)
            {
                FileLog.Write("build", $"{s.SourceId} \"{s.SweSymbolName}\": pin layout not readable before insertion ({ex.Message}).");
                return new List<Pt>();
            }
        }

        private static IList<Pt> ReadPins(object sym)
        {
            var result = new List<Pt>();
            if (!SweApi.TryCall(sym, C.SymbolPoints, out var arr) || !(arr is IEnumerable points)) return result;
            foreach (var p in points)
            {
                if (!SweApi.TryCall(p, C.PointGetPosition, out var pos) || pos == null) continue;
                double x = Convert.ToDouble(SweApi.Call(pos, C.PointGetX), CultureInfo.InvariantCulture);
                double y = Convert.ToDouble(SweApi.Call(pos, C.PointGetY), CultureInfo.InvariantCulture);
                result.Add(new Pt(x, y));
            }
            return result;
        }

        public void MoveSymbol(string symbolHandle, Pt p)
        {
            var sym = _symbols[symbolHandle];
            SweApi.Call(sym, C.SymbolSetX, p.X);
            SweApi.Call(sym, C.SymbolSetY, p.Y);
            if (!SweApi.TryCall(sym, C.ObjUpdate, out _))
                FileLog.Write("build", $"{symbolHandle}: update after move was rejected; the symbol may stay at its first position.");
        }

        public void DrawWireLine(string pageHandle, Pt a, Pt b)
        {
            var line = SweApi.Call(_files[pageHandle], C.FileNewLine, SweApi.EnumValue("EwLineType", "kLineSchematic"));
            SweApi.Call(line, C.LineSetStartX, a.X);
            SweApi.Call(line, C.LineSetStartY, a.Y);
            SweApi.Call(line, C.LineSetEndX, b.X);
            SweApi.Call(line, C.LineSetEndY, b.Y);
            var style = WireStyleId();
            if (style > 0) SweApi.TryCall(line, C.LineSetWireStyle, out _, style);
            SweApi.Call(line, C.ObjInsert);
        }

        // ------------------------------------------------------------------ wire style and wire numbers

        public const string ImportedWireStyle = "Imported";
        private int? _wireStyle;
        private readonly Dictionary<string, int> _symbolComponent = new Dictionary<string, int>();
        private readonly Dictionary<string, string> _symbolPage = new Dictionary<string, string>();

        /// <summary>Id of the project's "Imported" wire style, created on first use; 0 when the API refuses it.</summary>
        private int WireStyleId()
        {
            if (_wireStyle.HasValue) return _wireStyle.Value;
            _wireStyle = 0;
            try
            {
                var wsm = SweApi.Call(_project, C.ProjGetWireStyleManager);
                if (SweApi.TryCall(wsm, C.WireStyleArray, out var arr) && arr is IEnumerable styles)
                    foreach (var st in styles)
                        if (SweApi.TryCall(st, C.WireStyleGetName, out var n) && string.Equals(n?.ToString(), ImportedWireStyle, StringComparison.OrdinalIgnoreCase))
                            return (_wireStyle = Convert.ToInt32(SweApi.Call(st, C.ObjGetId))).Value;
                var style = SweApi.Call(wsm, C.WireStyleNew);
                SweApi.Call(style, C.WireStyleSetName, ImportedWireStyle);
                SweApi.TryCall(style, C.WireStyleSetDescription, out _, Lang, "Wires imported from PDF");
                SweApi.Call(style, C.ObjInsert);
                _wireStyle = Convert.ToInt32(SweApi.Call(style, C.ObjGetId));
                FileLog.Write("build", $"wire style \"{ImportedWireStyle}\" created (id {_wireStyle})");
            }
            catch (SweApiException ex) { FileLog.Write("build", $"wire style \"{ImportedWireStyle}\" not created: {ex.Message}"); }
            return _wireStyle.Value;
        }

        private sealed class PinRef
        {
            public string Symbol; public int SymbolId; public int ComponentId; public int Circuit; public int Terminal; public Pt At;
        }

        /// <summary>
        /// Numbers the new wires (SWE's own wire numbering), then finds the wires of each numbered PDF net: a wire belongs to
        /// the net when both its ends are on library pins the net's wire lines were drawn to. Those wires get the PDF number
        /// in manual mode, so renumbering keeps it. Ends are matched on component circuit + terminal; when that is not
        /// conclusive, on the component pair (only when that pair is unique to one net).
        /// </summary>
        public string ApplyWireNumbers(IList<WireNumberPlan> nets)
        {
            try
            {
                var nw = SweApi.Call(_project, C.ProjNewNumberWires);
                try { SweApi.Call(nw, C.NumberWiresSetSelectionType, SweApi.EnumValue("EwSelectionType", "kSelectionAll")); } catch (SweApiException) { }
                SweApi.Call(nw, C.NumberWiresProcess, SweApi.EnumValue("EwNumberWireAction", "kNumberNewWiresAction"));
            }
            catch (SweApiException ex) { FileLog.Write("build", "wire numbering: " + ex.Message); }

            // every library pin placed by this build, with its symbol, component, circuit and terminal number
            var pins = new List<PinRef>();
            foreach (var kv in _symbols)
            {
                _symbolComponent.TryGetValue(kv.Key, out var compId);
                int symId = SweApi.TryCall(kv.Value, C.ObjGetId, out var sid) && sid != null ? Convert.ToInt32(sid) : 0;
                if (!SweApi.TryCall(kv.Value, C.SymbolPoints, out var pa) || !(pa is IEnumerable ps)) continue;
                foreach (var pt in ps)
                {
                    if (!SweApi.TryCall(pt, C.PointGetPosition, out var pos) || pos == null) continue;
                    SweApi.TryCall(pt, C.PointGetCircuitNumber, out var cn);
                    SweApi.TryCall(pt, C.PointGetTerminalNumber, out var tn);
                    pins.Add(new PinRef
                    {
                        Symbol = kv.Key, SymbolId = symId, ComponentId = compId,
                        Circuit = cn == null ? 0 : Convert.ToInt32(cn), Terminal = tn == null ? 0 : Convert.ToInt32(tn),
                        At = new Pt(Convert.ToDouble(SweApi.Call(pos, C.PointGetX), CultureInfo.InvariantCulture),
                                    Convert.ToDouble(SweApi.Call(pos, C.PointGetY), CultureInfo.InvariantCulture)),
                    });
                }
            }
            var pageOf = _symbolPage;

            // the pins of each numbered net
            var netPins = new List<(WireNumberPlan net, HashSet<PinRef> pins)>();
            foreach (var n in nets)
            {
                var set = new HashSet<PinRef>(pins.Where(p => pageOf.TryGetValue(p.Symbol, out var pg) && pg == n.PageHandle
                                                             && n.Pins.Any(q => q.DistanceTo(p.At) < 0.01)));
                if (set.Count > 0) netPins.Add((n, set));
            }

            // component circuit id -> (symbol id, symbol circuit number)
            var circuits = new Dictionary<int, (int symbolId, int circuit)>();
            var cm = SweApi.Call(_project, C.ProjGetComponentManager);
            foreach (var compId in pins.Select(p => p.ComponentId).Where(i => i > 0).Distinct())
            {
                if (!SweApi.TryCall(cm, C.ComponentFindById, out var comp, compId) || comp == null) continue;
                if (!SweApi.TryCall(comp, C.ComponentCircuits, out var ca) || !(ca is IEnumerable cs)) continue;
                foreach (var c in cs)
                {
                    int id = SweApi.TryCall(c, C.ObjGetId, out var v) && v != null ? Convert.ToInt32(v) : 0;
                    SweApi.TryCall(c, C.CompCircuitSymbolId, out var s);
                    SweApi.TryCall(c, C.CompCircuitSymbolCircuit, out var k);
                    if (id > 0) circuits[id] = (s == null ? 0 : Convert.ToInt32(s), k == null ? 0 : Convert.ToInt32(k));
                }
            }

            PinRef End(int comp, int circuit, int terminal)
            {
                var ofComp = pins.Where(p => p.ComponentId == comp).ToList();
                if (circuits.TryGetValue(circuit, out var c))
                {
                    var ofSymbol = ofComp.Where(p => c.symbolId == 0 || p.SymbolId == c.symbolId).ToList();
                    var exact = ofSymbol.Where(p => p.Circuit == c.circuit && p.Terminal == terminal).ToList();
                    if (exact.Count == 1) return exact[0];
                    var byTerminal = ofSymbol.Where(p => p.Terminal == terminal).ToList();
                    if (byTerminal.Count == 1) return byTerminal[0];
                }
                return null;
            }

            int numbered = 0, wiresSeen = 0, unmatched = 0;
            var done = new HashSet<WireNumberPlan>();
            var wm = SweApi.Call(_project, C.ProjGetWireManager);
            var pending = new List<(object wire, int a, int b)>();
            if (SweApi.TryCall(wm, C.WireMgrArray, out var wa) && wa is IEnumerable wires)
                foreach (var w in wires)
                {
                    wiresSeen++;
                    int Int(C.Member m) => SweApi.TryCall(w, m, out var v) && v != null ? Convert.ToInt32(v) : 0;
                    int oc = Int(C.WireOriginComponent), dc = Int(C.WireDestComponent);
                    var o = End(oc, Int(C.WireOriginCircuit), Int(C.WireOriginTerminal));
                    var d = End(dc, Int(C.WireDestCircuit), Int(C.WireDestTerminal));
                    var hit = o != null && d != null ? netPins.FirstOrDefault(n => n.pins.Contains(o) && n.pins.Contains(d)).net : null;
                    if (hit == null) { pending.Add((w, oc, dc)); continue; }
                    if (SetWireTag(w, hit.Number)) { numbered++; done.Add(hit); }
                }

            // fallback: a component pair that only one numbered net connects
            foreach (var (w, a, b) in pending)
            {
                var candidates = netPins.Where(n => n.pins.Any(p => p.ComponentId == a) && n.pins.Any(p => p.ComponentId == b) && a != b).ToList();
                if (candidates.Count == 1 && SetWireTag(w, candidates[0].net.Number)) { numbered++; done.Add(candidates[0].net); }
                else unmatched++;
            }

            foreach (var n in nets.Where(n => !done.Contains(n)))
                _deferred.AppendLine($"wire-number,{n.PageHandle},{n.SourcePage},{Csv(n.Number)},not matched to a wire");
            var msg = $"Wire numbers: {numbered} of {wiresSeen} wires numbered manually from the PDF ({done.Count} of {nets.Count} numbered nets);"
                      + $" {unmatched} wires kept SOLIDWORKS Electrical's number" + (wiresSeen == 0 ? " — no wires were found in the project" : "") + ".";
            FileLog.Write("build", msg);
            return msg;
        }

        private static bool SetWireTag(object wire, string number)
        {
            try { SweApi.Call(wire, C.WireSetTagMode, SweApi.EnumValue("EwTagMode", "kManu")); } catch (SweApiException) { }
            if (!SweApi.TryCall(wire, C.WireSetTag, out _, number)) return false;
            SweApi.TryCall(wire, C.ObjUpdate, out _);
            return true;
        }

        /// <summary>
        /// EwAPI 2026 has no call that places or links individual origin-destination arrows (only the project-wide
    /// automatic-arrows command); 
        /// pairs are written to the deferred report (page/position/label) for the SWE "Origin-destination arrow" command.
        /// </summary>
        public void LinkOriginDestination(string pageA, Pt a, string pageB, Pt b, string label) =>
            _deferred.AppendLine($"origin-destination,{pageA},{a.X:0.##},{a.Y:0.##},{Csv(label)},{pageB}@{b.X:0.##};{b.Y:0.##}");

        public void AttachDocument(string filePath)
        {
            var fm = SweApi.Call(_project, C.ProjGetFileManager);
            var file = SweApi.Call(fm, C.FileNew);
            SweApi.Call(file, C.FileSetOriginalPath, filePath, false);
            SweApi.Call(file, C.FileSetBook, _bookId);
            SweApi.TryCall(file, C.FileSetDescription, out _, Lang, "Source PDF: " + Path.GetFileName(filePath));
            SweApi.Call(file, C.ObjInsert);
        }

        public void Finish()
        {
            DeferredReportPath = Path.Combine(FileLog.Folder, $"build-{_projectId}-{DateTime.Now:yyyyMMdd-HHmmss}-deferred.csv");
            Directory.CreateDirectory(FileLog.Folder);
            File.WriteAllText(DeferredReportPath, _deferred.ToString());
            foreach (var f in _files.Values) SweApi.TryCall(f, C.FileClose, out _);
        }

        private static string Csv(string s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
