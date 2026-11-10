using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Infrastructure;

namespace SWEETs.SwePdfSchematic.Core.Build
{
    /// <summary>
    /// The only surface the builder needs from SOLIDWORKS Electrical. The add-in implements it over EwAPI;
    /// <see cref="DryRunTarget"/> implements it in memory for tests and the "simulate build" button.
    /// Handles are opaque strings owned by the implementation.
    /// </summary>
    public interface ISchematicTarget
    {
        void CreateProject(ProjectSpec spec);
        string AddSchemePage(PagePlan page);
        string EnsureComponent(ComponentPlan component);
        string InsertSymbol(string pageHandle, SymbolPlan symbol, string componentHandle);
        IList<Pt> GetSymbolPins(string symbolHandle);
        /// <summary>The library symbol's pins relative to its insertion point at the plan's rotation; empty when unknown before insertion.</summary>
        IList<Pt> GetPinOffsets(string pageHandle, SymbolPlan symbol);
        void MoveSymbol(string symbolHandle, Pt newPosition);
        void DrawWireLine(string pageHandle, Pt a, Pt b);
        /// <summary>
        /// Runs at the end of the build: numbers the new wires, then gives every wire of a numbered PDF net that number in
        /// manual mode. Returns a summary.
        /// </summary>
        string ApplyWireNumbers(IList<WireNumberPlan> nets);
        void LinkOriginDestination(string pageHandleA, Pt a, string pageHandleB, Pt b, string label);
        void AttachDocument(string filePath);
        /// <summary>Puts manufacturer parts on a component created by <see cref="EnsureComponent"/>; creates catalog parts that are missing. Returns a summary.</summary>
        string AssignParts(string componentHandle, ComponentPlan component);
        /// <summary>Creates a cable, cabinet component or location item that has no symbol, with its parts. Returns a summary.</summary>
        string CreateItem(ComponentPlan item);
        void Finish();
    }

    public enum LogLevel { Info, Success, Warning, Error }

    public sealed class BuildLogEntry
    {
        public DateTime Time = DateTime.Now;
        public LogLevel Level;
        public string Message;
        public override string ToString() => $"{Time:HH:mm:ss} [{Level}] {Message}";
    }

    public sealed class BuildProgress
    {
        public int Done;
        public int Total;
        public string Stage;
        public BuildLogEntry Entry;
        public double Fraction => Total == 0 ? 0 : (double)Done / Total;
    }

    public sealed class BuildResult
    {
        public int Pages, Symbols, Wires, Components, Links, Parts, Items, Errors, Warnings;
        public List<BuildLogEntry> Log = new List<BuildLogEntry>();
        public bool Cancelled;
    }

    /// <summary>
    /// Executes a <see cref="BuildPlan"/> against a target. Every operation is isolated: one failing symbol is
    /// logged and skipped rather than aborting the project. Symbols are re-aligned so their real library pins
    /// land on the original connection points before wires are drawn to them.
    /// </summary>
    public sealed class BuildRunner
    {
        private readonly ISchematicTarget _t;
        private readonly Func<Task> _yield;
        private BuildResult _r;
        private int _done, _total;
        private IProgress<BuildProgress> _progress;

        /// <param name="yieldToUi">Called between operations so an STA/COM-bound UI stays responsive (e.g. Dispatcher.Yield).</param>
        public BuildRunner(ISchematicTarget target, Func<Task> yieldToUi = null)
        {
            _t = target; _yield = yieldToUi ?? (() => Task.CompletedTask);
        }

        /// <summary>Drawing units per mm and their label; tolerances below are defined in mm.</summary>
        private double _perMm = 1.0;
        private string _unit = "mm";

        public async Task<BuildResult> RunAsync(BuildPlan plan, IProgress<BuildProgress> progress, CancellationToken ct)
        {
            _r = new BuildResult(); _done = 0; _total = plan.TotalOperations; _progress = progress;
            _perMm = UnitConversion.PerMm(plan.Units); _unit = UnitConversion.Label(plan.Units);
            Log(LogLevel.Info, $"Drawing units: {(plan.Units == DrawingUnits.Inches ? "inches" : "millimetres")}.");

            if (!await Step("Creating project", () => { _t.CreateProject(plan.Project); return $"Project \"{plan.Project.Name}\" created from template."; }, true))
                return _r;

            var componentHandles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var wireNumbers = new List<WireNumberPlan>();
            var componentPlans = new Dictionary<string, ComponentPlan>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in plan.Components) { c.Symbols.Clear(); componentPlans[c.Tag] = c; }
            foreach (var c in plan.Components)
            {
                if (ct.IsCancellationRequested) return Cancel();
                await Step("Creating components", () => { componentHandles[c.Tag] = _t.EnsureComponent(c); _r.Components++; return null; });
            }

            var pageHandles = new Dictionary<int, string>();
            var linkHandles = new Dictionary<string, (string page, Pt at)>();
            foreach (var page in plan.Pages)
            {
                if (ct.IsCancellationRequested) return Cancel();
                string ph = null;
                if (!await Step($"Page {page.SourcePage}", () => { ph = _t.AddSchemePage(page); _r.Pages++; return $"Scheme page \"{page.Title}\" added."; }))
                    continue;
                pageHandles[page.SourcePage] = ph;

                var pins = new List<Pt>();
                var pinMap = new List<(Pt expected, Pt actual)>();
                foreach (var s in page.Symbols)
                {
                    if (ct.IsCancellationRequested) return Cancel();
                    await Step($"Page {page.SourcePage}: symbols", () =>
                    {
                        componentHandles.TryGetValue(s.ComponentTag ?? "", out var comp);
                        var pairs = PlaceSymbol(ph, s, comp, out var handle);
                        if (s.ComponentTag != null && componentPlans.TryGetValue(s.ComponentTag, out var cp))
                            cp.Symbols.Add(new PlacedSymbol
                            {
                                Handle = handle, SweSymbolName = s.SweSymbolName, SourceId = s.SourceId,
                                PinNames = pairs.Select(p => (p.actual, PinName(s, p.expected))).ToList(),
                            });
                        pinMap.AddRange(pairs);
                        pins.AddRange(pairs.Select(p => p.actual));
                        _r.Symbols++;
                        return null;
                    });
                }

                foreach (var w in page.Wires)
                {
                    if (ct.IsCancellationRequested) return Cancel();
                    await Step($"Page {page.SourcePage}: wires", () =>
                    {
                        // ends on a PDF pin follow its library pin; anything else close to a library pin is snapped onto it
                        var segs = new List<Segment>();
                        foreach (var seg in w.Segments)
                            segs.AddRange(TouchesPin(seg, pinMap) ? Reroute(new[] { seg }, pinMap) : SnapToPins(new List<Segment> { seg }, pins));
                        foreach (var seg in segs) _t.DrawWireLine(ph, seg.A, seg.B);
                        if (!string.IsNullOrEmpty(w.WireNumber) && segs.Count > 0)
                        {
                            var touched = segs.SelectMany(x => new[] { x.A, x.B })
                                              .Select(e => pins.Where(q => q.DistanceTo(e) < 0.01 * _perMm).Cast<Pt?>().FirstOrDefault())
                                              .Where(q => q.HasValue).Select(q => q.Value).Distinct().ToList();
                            wireNumbers.Add(new WireNumberPlan { PageHandle = ph, SourcePage = page.SourcePage, Number = w.WireNumber, Pins = touched });
                        }
                        _r.Wires++;
                        return null;
                    });
                }

                foreach (var l in page.Links) linkHandles[l.SourceId] = (ph, l.Location);
            }

            var linked = new HashSet<string>();
            foreach (var page in plan.Pages)
                foreach (var l in page.Links)
                {
                    if (ct.IsCancellationRequested) return Cancel();
                    if (linked.Contains(l.SourceId) || !linkHandles.TryGetValue(l.PairedSourceId, out var other)) { Tick(); continue; }
                    var mine = linkHandles[l.SourceId];
                    await Step("Origin-destination arrows", () =>
                    {
                        _t.LinkOriginDestination(mine.page, mine.at, other.page, other.at, l.Label);
                        linked.Add(l.SourceId); linked.Add(l.PairedSourceId); _r.Links++;
                        return null;
                    });
                }

            foreach (var c in plan.Components.Where(c => c.Parts.Count > 0))
            {
                if (ct.IsCancellationRequested) return Cancel();
                if (!componentHandles.TryGetValue(c.Tag, out var ch)) { Tick(); continue; }
                await Step($"Parts of {c.Tag}", () => { var msg = _t.AssignParts(ch, c); _r.Parts += c.Parts.Sum(p => p.Quantity); return msg; });
            }
            foreach (var x in plan.Extras)
            {
                if (ct.IsCancellationRequested) return Cancel();
                await Step($"{x.Kind} {x.Tag}", () => { var msg = _t.CreateItem(x); _r.Items++; return msg; });
            }

            if (wireNumbers.Count > 0)
                await Step("Wire numbers", () => _t.ApplyWireNumbers(wireNumbers));

            if (!string.IsNullOrEmpty(plan.Project.SourcePdfPath))
                await Step("Attaching source PDF", () => { _t.AttachDocument(plan.Project.SourcePdfPath); return "Source PDF attached to the project."; });
            await Step("Finishing", () => { _t.Finish(); return null; });

            Log(_r.Errors == 0 ? LogLevel.Success : LogLevel.Warning,
                $"Build finished: {_r.Pages} pages, {_r.Symbols} symbols, {_r.Wires} wires, {_r.Components} components, {_r.Links} links, {_r.Parts} parts, {_r.Items} items without symbols, {_r.Errors} errors.");
            return _r;
        }

        private readonly Dictionary<string, IList<Pt>> _offsets = new Dictionary<string, IList<Pt>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Places one symbol so its library pins land on the PDF's connection points, then pairs every PDF pin with the
        /// library pin that serves it. A library symbol's insertion point is the first connection point of its first
        /// circuit, so the pin layout relative to the insertion point is read first (before insertion when the API allows,
        /// otherwise from the inserted symbol) and the position is chosen so the most library pins coincide exactly with
        /// PDF pins, anchored on that first pin. Pins that still differ (other pole pitch, other symbol length) are fixed
        /// by rerouting the wire ends.
        /// </summary>
        private List<(Pt expected, Pt actual)> PlaceSymbol(string page, SymbolPlan s, string comp, out string handle)
        {
            var key = s.SweSymbolName + "|" + s.Rotation;
            var planned = s.Position;
            if (!_offsets.TryGetValue(key, out var offsets))
            {
                offsets = _t.GetPinOffsets(page, s) ?? new List<Pt>();
                if (!Plausible(offsets)) offsets = new List<Pt>();
                if (offsets.Count > 0) _offsets[key] = offsets;
            }
            bool known = offsets.Count > 0;
            if (known && s.ExpectedPins.Count > 0) s.Position = BestPosition(s, offsets, planned);

            handle = _t.InsertSymbol(page, s, comp);
            var actual = _t.GetSymbolPins(handle) ?? new List<Pt>();

            // the inserted symbol is the reference: if its pins are not where the layout said, learn the real layout
            var pos = s.Position;
            if (known && actual.Count > 0 && (actual.Count != offsets.Count
                || offsets.Any(o => actual.Min(a => a.DistanceTo(new Pt(pos.X + o.X, pos.Y + o.Y))) > 0.05 * _perMm)))
            {
                FileLog.Write("build", $"{s.SourceId} {s.SweSymbolName}: pin layout read before insertion did not match the inserted symbol; using the inserted one.");
                known = false;
            }

            if (!known && actual.Count > 0)
            {
                // layout not readable before insertion: learn it from this symbol, then move it once
                offsets = actual.Select(a => new Pt(a.X - s.Position.X, a.Y - s.Position.Y)).ToList();
                _offsets[key] = offsets;
                if (s.ExpectedPins.Count > 0)
                {
                    var p = BestPosition(s, offsets, planned);
                    if (p.DistanceTo(s.Position) > 1e-6)
                    {
                        _t.MoveSymbol(handle, p);
                        s.Position = p;
                        actual = _t.GetSymbolPins(handle) ?? offsets.Select(o => new Pt(p.X + o.X, p.Y + o.Y)).ToList();
                    }
                }
            }

            FileLog.Write("build", $"{s.SourceId} {s.SweSymbolName} rot={s.Rotation} at {Fmt(s.Position)} (planned {Fmt(planned)}, layout {(known ? "read before insert" : "read after insert")}) "
                                   + $"pdf pins [{string.Join(" ", s.ExpectedPins.Select(Fmt))}] library pins [{string.Join(" ", actual.Select(Fmt))}]");

            if (actual.Count == 0 || s.ExpectedPins.Count == 0) return Pair(s.ExpectedPins, actual);
            double residual = s.ExpectedPins.Max(e => actual.Min(a => a.DistanceTo(e)));
            if (residual > 0.5 * _perMm)
                Log(LogLevel.Info, $"{s.SourceId} ({s.SweSymbolName}): library pins differ from the PDF by up to {residual:0.0##} {_unit}; wires rerouted to the library pins.");
            return Pair(s.ExpectedPins, actual);
        }

        /// <summary>
        /// Tries every "library pin i on PDF pin j" placement and keeps the one where the most pins coincide;
        /// ties go to the insertion point (first pin of the first circuit), then to the smallest total gap.
        /// </summary>
        internal Pt BestPosition(SymbolPlan s, IList<Pt> offsets, Pt planned)
        {
            double tol = 0.5 * _perMm, cap = 50 * _perMm;
            Pt best = planned; int bestHit = -1; bool bestIns = false; double bestSum = double.MaxValue, bestMove = double.MaxValue;
            foreach (var o in offsets)
            {
                bool ins = Math.Abs(o.X) < 1e-6 && Math.Abs(o.Y) < 1e-6;
                foreach (var e in s.ExpectedPins)
                {
                    var p = new Pt(e.X - o.X, e.Y - o.Y);
                    int hit = 0; double sum = 0;
                    foreach (var x in s.ExpectedPins)
                    {
                        double d = offsets.Min(q => new Pt(p.X + q.X, p.Y + q.Y).DistanceTo(x));
                        if (d <= tol) hit++;
                        sum += Math.Min(d, cap);
                    }
                    double move = p.DistanceTo(planned);
                    bool better = hit != bestHit ? hit > bestHit
                                : ins != bestIns ? ins
                                : Math.Abs(sum - bestSum) > 1e-6 ? sum < bestSum
                                : move < bestMove;
                    if (better) { best = p; bestHit = hit; bestIns = ins; bestSum = sum; bestMove = move; }
                }
            }
            return best;
        }

        /// <summary>A layout read from a not-yet-inserted symbol is trusted only if it looks like real pins near the insertion point.</summary>
        private bool Plausible(IList<Pt> offsets) =>
            offsets.Count > 0
            && offsets.All(o => !double.IsNaN(o.X) && !double.IsNaN(o.Y) && Math.Abs(o.X) < 150 * _perMm && Math.Abs(o.Y) < 150 * _perMm)
            && (offsets.Count == 1 || offsets.Any(o => o.DistanceTo(offsets[0]) > 0.1 * _perMm))
            && offsets.Any(o => Math.Abs(o.X) < 0.05 * _perMm && Math.Abs(o.Y) < 0.05 * _perMm);

        private static string Fmt(Pt p) => $"({p.X:0.###},{p.Y:0.###})";

        /// <summary>Terminal designation printed in the PDF at an expected pin.</summary>
        private static string PinName(SymbolPlan s, Pt expected)
        {
            for (int i = 0; i < s.ExpectedPins.Count && i < s.ExpectedPinNames.Count; i++)
                if (s.ExpectedPins[i].DistanceTo(expected) < 1e-6) return string.IsNullOrWhiteSpace(s.ExpectedPinNames[i]) ? null : s.ExpectedPinNames[i].Trim();
            return null;
        }

        /// <summary>One-to-one pairing of PDF pins with library pins, closest pairs first.</summary>
        internal static List<(Pt expected, Pt actual)> Pair(IList<Pt> expected, IList<Pt> actual)
        {
            var result = new List<(Pt, Pt)>();
            var candidates = new List<(int e, int a, double d)>();
            for (int i = 0; i < expected.Count; i++)
                for (int j = 0; j < actual.Count; j++) candidates.Add((i, j, expected[i].DistanceTo(actual[j])));
            var usedE = new HashSet<int>(); var usedA = new HashSet<int>();
            foreach (var c in candidates.OrderBy(x => x.d))
            {
                if (usedE.Contains(c.e) || usedA.Contains(c.a)) continue;
                usedE.Add(c.e); usedA.Add(c.a);
                result.Add((expected[c.e], actual[c.a]));
            }
            return result;
        }

        /// <summary>
        /// Moves wire ends that sat on a PDF pin to the library pin paired with it. A horizontal or vertical wire whose end
        /// moves sideways gets a short jog next to the pin, so the wire stays orthogonal and still enters the pin straight.
        /// </summary>
        internal List<Segment> Reroute(IList<Segment> segs, IList<(Pt expected, Pt actual)> pinMap)
        {
            double jog = 2.5 * _perMm, eps = 1e-6;
            Pt? Target(Pt p) => PinFor(p, pinMap);
            var result = new List<Segment>();
            foreach (var s in segs)
            {
                var ta = Target(s.A); var tb = Target(s.B);
                if (!ta.HasValue && !tb.HasValue) { result.Add(s); continue; }
                Pt a = ta ?? s.A, b = tb ?? s.B;
                if (s.Axis == Axis.Horizontal && Math.Abs(a.Y - b.Y) > eps)
                {
                    // jog next to the moved end (or in the middle when both moved)
                    double x = ta.HasValue && !tb.HasValue ? a.X + Math.Sign(b.X - a.X) * Math.Min(jog, Math.Abs(b.X - a.X) / 2)
                             : tb.HasValue && !ta.HasValue ? b.X - Math.Sign(b.X - a.X) * Math.Min(jog, Math.Abs(b.X - a.X) / 2)
                             : (a.X + b.X) / 2;
                    Add(result, a, new Pt(x, a.Y)); Add(result, new Pt(x, a.Y), new Pt(x, b.Y)); Add(result, new Pt(x, b.Y), b);
                }
                else if (s.Axis == Axis.Vertical && Math.Abs(a.X - b.X) > eps)
                {
                    double y = ta.HasValue && !tb.HasValue ? a.Y + Math.Sign(b.Y - a.Y) * Math.Min(jog, Math.Abs(b.Y - a.Y) / 2)
                             : tb.HasValue && !ta.HasValue ? b.Y - Math.Sign(b.Y - a.Y) * Math.Min(jog, Math.Abs(b.Y - a.Y) / 2)
                             : (a.Y + b.Y) / 2;
                    Add(result, a, new Pt(a.X, y)); Add(result, new Pt(a.X, y), new Pt(b.X, y)); Add(result, new Pt(b.X, y), b);
                }
                else Add(result, a, b);
            }
            return result;
        }

        private double PinMatch => 1.0 * _perMm;

        private Pt? PinFor(Pt p, IList<(Pt expected, Pt actual)> pinMap)
        {
            Pt? best = null; double bd = PinMatch;
            foreach (var (e, a) in pinMap) { double d = e.DistanceTo(p); if (d <= bd) { bd = d; best = a; } }
            return best;
        }

        private bool TouchesPin(Segment s, IList<(Pt expected, Pt actual)> pinMap) =>
            PinFor(s.A, pinMap).HasValue || PinFor(s.B, pinMap).HasValue;

        private void Add(List<Segment> list, Pt a, Pt b)
        {
            if (a.DistanceTo(b) > 0.01 * _perMm) list.Add(new Segment(a, b));
        }

        /// <summary>Wire endpoints within 3 mm of a library pin are pulled onto it (keeping the segment orthogonal where possible).</summary>
        private List<Segment> SnapToPins(List<Segment> segs, IList<Pt> pins)
        {
            var result = new List<Segment>();
            foreach (var s in segs)
            {
                var a = Nearest(s.A, pins); var b = Nearest(s.B, pins);
                if (s.Axis == Axis.Vertical) { if (a.HasValue && !b.HasValue) b = new Pt(a.Value.X, s.B.Y); if (b.HasValue && !a.HasValue) a = new Pt(b.Value.X, s.A.Y); }
                if (s.Axis == Axis.Horizontal) { if (a.HasValue && !b.HasValue) b = new Pt(s.B.X, a.Value.Y); if (b.HasValue && !a.HasValue) a = new Pt(s.A.X, b.Value.Y); }
                var seg = new Segment(a ?? s.A, b ?? s.B);
                if (seg.Length > 0.01 * _perMm) result.Add(seg);
            }
            return result;
        }

        private Pt? Nearest(Pt p, IList<Pt> pins)
        {
            Pt? best = null; double bd = 3.0 * _perMm;
            foreach (var q in pins) { double d = q.DistanceTo(p); if (d < bd) { bd = d; best = q; } }
            return best;
        }

        private async Task<bool> Step(string stage, Func<string> op, bool fatal = false)
        {
            try
            {
                var msg = op();
                if (msg != null) Log(LogLevel.Info, msg);
                Tick(stage);
                await _yield();
                return true;
            }
            catch (Exception ex)
            {
                _r.Errors++;
                Log(LogLevel.Error, $"{stage}: {ex.Message}");
                Tick(stage);
                if (fatal) Log(LogLevel.Error, "Build stopped — the project could not be created.");
                await _yield();
                return false;
            }
        }

        private void Tick(string stage = null) { _done++; _progress?.Report(new BuildProgress { Done = _done, Total = _total, Stage = stage }); }

        private void Log(LogLevel level, string message)
        {
            var e = new BuildLogEntry { Level = level, Message = message };
            if (level == LogLevel.Warning) _r.Warnings++;
            _r.Log.Add(e);
            _progress?.Report(new BuildProgress { Done = _done, Total = _total, Entry = e });
        }

        private BuildResult Cancel()
        {
            _r.Cancelled = true;
            Log(LogLevel.Warning, "Build cancelled. Items created so far remain in the project.");
            return _r;
        }
    }

    /// <summary>In-memory target: validates a plan end-to-end without SOLIDWORKS Electrical.</summary>
    public sealed class DryRunTarget : ISchematicTarget
    {
        private int _n;
        private readonly Dictionary<string, (SymbolPlan plan, Pt pos)> _symbols = new Dictionary<string, (SymbolPlan, Pt)>();
        public List<string> Operations { get; } = new List<string>();

        public void CreateProject(ProjectSpec spec) => Operations.Add($"project {spec.Name} template={spec.TemplateName ?? spec.TemplateArchivePath}");
        public string AddSchemePage(PagePlan page) { Operations.Add($"page {page.Title}"); return "page" + (++_n); }
        public string EnsureComponent(ComponentPlan c) { Operations.Add($"component {c.Tag}"); return "comp:" + c.Tag; }
        public string InsertSymbol(string ph, SymbolPlan s, string comp)
        {
            var h = "sym" + (++_n); _symbols[h] = (s, s.Position);
            Operations.Add($"symbol {s.SweSymbolName} @{s.Position} rot={s.Rotation} comp={comp}");
            return h;
        }
        public IList<Pt> GetSymbolPins(string h)
        {
            var (plan, pos) = _symbols[h];
            return plan.ExpectedPins.Select(p => new Pt(p.X + pos.X - plan.Position.X, p.Y + pos.Y - plan.Position.Y)).ToList();
        }
        public IList<Pt> GetPinOffsets(string ph, SymbolPlan s) => new List<Pt>();
        public void MoveSymbol(string h, Pt p) { _symbols[h] = (_symbols[h].plan, p); Operations.Add($"move {h} {p}"); }
        public void DrawWireLine(string ph, Pt a, Pt b) => Operations.Add($"wire {ph} {a}-{b}");
        public string ApplyWireNumbers(IList<WireNumberPlan> nets)
        {
            foreach (var n in nets) Operations.Add($"wireno {n.Number} page {n.SourcePage}: {n.Pins.Count} pins");
            return $"{nets.Count} wire numbers to apply.";
        }
        public void LinkOriginDestination(string pa, Pt a, string pb, Pt b, string label) => Operations.Add($"od {pa}{a} <-> {pb}{b} {label}");
        public void AttachDocument(string path) => Operations.Add($"attach {path}");
        public string AssignParts(string h, ComponentPlan c)
        {
            foreach (var p in c.Parts)
                Operations.Add($"part {c.Tag} {p}" + (p.CarriesCircuits
                    ? " circuits: " + string.Join(", ", c.Symbols.Select(x => x.SweSymbolName + " [" + string.Join(" ", x.PinNames.Select(n => n.name ?? "?")) + "]"))
                    : " (accessory)"));
            return null;
        }
        public string CreateItem(ComponentPlan x) { Operations.Add($"item {x.Kind} {x.Tag} loc={x.LocationTag} parts={string.Join("; ", x.Parts)}"); return null; }
        public void Finish() => Operations.Add("finish");
    }
}
