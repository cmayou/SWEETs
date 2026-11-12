using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Mapping;
using SWEETs.SwePdfSchematic.Core.Model;
using SWEETs.SwePdfSchematic.Core.Pdf;

namespace SWEETs.SwePdfSchematic.Core.Recognition
{
    public sealed class RecognitionProgress
    {
        public int Page;
        public int PageCount;
        public string Stage;
        public double Fraction => PageCount == 0 ? 0 : Math.Min(1, (Page - 1 + StageFraction) / PageCount);
        public double StageFraction;
    }

    /// <summary>
    /// Orchestrates the per-page pipeline (layout → wires → symbols → text → classify → map) and the
    /// document-level pass (standard vote, pole groups, cross-references, origin/destination pairing).
    /// Pure managed code; runs off the UI thread.
    /// </summary>
    public sealed class SchematicRecognizer
    {
        private readonly RecognitionOptions _o;
        private readonly RuleLibrary _rules;
        private readonly MappingStore _mappings;

        public SchematicRecognizer(RecognitionOptions options, RuleLibrary rules, MappingStore mappings)
        {
            _o = options; _rules = rules; _mappings = mappings;
        }

        /// <param name="forceSchematic">PDF page numbers to analyse as schematics even if they look like reports or layouts.</param>
        public ImportDocument Recognize(string pdfPath, IProgress<RecognitionProgress> progress = null, CancellationToken ct = default,
                                        ISet<int> forceSchematic = null)
        {
            _force = forceSchematic ?? new HashSet<int>();
            var doc = new ImportDocument
            {
                SourcePath = pdfPath,
                SourceFileName = Path.GetFileName(pdfPath),
                SourceSha256 = VectorPdfReader.Sha256(pdfPath),
            };
            int count = VectorPdfReader.PageCount(pdfPath);

            // Pass 1: read every page; report pages (BOM, wire lists...) are recognised and their parts list harvested
            // first, so device marks and descriptions are known while the drawings are analysed.
            var raws = new List<RawPage>();
            foreach (var raw in VectorPdfReader.ReadPages(pdfPath))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new RecognitionProgress { Page = raw.Number, PageCount = count, Stage = "Reading vectors", StageFraction = 0.05 });
                raws.Add(raw);
                if (!_force.Contains(raw.Number) && ReportReader.IsReportPage(raw, out _))
                {
                    ReportReader.Harvest(raw, doc.Parts);
                    try { BomReader.Harvest(raw, doc.BomLines); }
                    catch (Exception ex) { doc.Warnings.Add($"Page {raw.Number}: parts table not read ({ex.Message})."); }
                }
            }
            _knownTags = new HashSet<string>(doc.Parts.Keys.Where(k => TagParser.ParseDeviceTag(k) != null), StringComparer.OrdinalIgnoreCase);

            // Pass 2: drawings
            foreach (var raw in raws)
            {
                ct.ThrowIfCancellationRequested();
                void Report(string stage, double f) => progress?.Report(new RecognitionProgress { Page = raw.Number, PageCount = count, Stage = stage, StageFraction = f });
                doc.Pages.Add(AnalyzePage(raw, Report));
            }

            foreach (var p in doc.Pages.Where(p => p.Kind == PageKind.Schematic && !p.IsRasterOnly && p.Symbols.Count == 0
                                                   && !p.OffPageLinks.Any(l => l.HasArrowSymbol) && !p.ForcedSchematic))
            {
                // tables, cover sheets, layouts without a SCALE label: nothing to rebuild as a schematic
                p.Kind = PageKind.Report;
                p.ReportTitle = TitleOf(p) ?? "No schematic symbols";
                p.Warnings.Clear(); // wiring warnings from table rules are noise once the page is not a drawing
                p.Nets.Clear();
                p.OffPageLinks.Clear();
            }
            foreach (var p in doc.Pages) p.Include = p.Kind == PageKind.Schematic && !p.IsRasterOnly;
            ResolveStandards(doc);
            foreach (var p in doc.SchematicPages)
            {
                foreach (var s in p.Symbols) { s.Part = doc.PartFor(s.Tag); FinishSymbol(s, p); }
                GroupPoles(p);
                foreach (var s in p.Symbols.Where(x => x.Part == null && x.Tag != null)) { s.Part = doc.PartFor(s.Tag); if (s.Part != null) FinishSymbol(s, p); }
                AutoCombine(p);
                if (!p.TitleBlock.Fields.ContainsKey("TITLE") && TitleOf(p) is string title) p.TitleBlock.Fields["TITLE"] = title;
            }
            MapSheetReferences(doc);
            CrossReferenceResolver.Resolve(doc);
            BomReader.Organize(doc);
            progress?.Report(new RecognitionProgress { Page = count, PageCount = count, Stage = "Done", StageFraction = 1 });
            return doc;
        }

        /// <summary>
        /// Combines the poles into one multi-circuit symbol on their page and classifies, signs and scores it.
        /// Throws <see cref="ArgumentException"/> for an invalid selection.
        /// </summary>
        public DetectedSymbol Combine(ImportDocument doc, IList<DetectedSymbol> members)
        {
            var page = doc.Pages.Single(p => p.PageNumber == members[0].PageNumber);
            var c = SymbolCombiner.Merge(members);
            SymbolCombiner.Commit(page, c);
            FinishSymbol(c, page);
            c.Notes.Add($"{c.CombinedFrom.Count} poles combined into one symbol.");
            return c;
        }

        /// <summary>Restores the separate poles of a combined symbol and re-scores them.</summary>
        public List<DetectedSymbol> Split(ImportDocument doc, DetectedSymbol combined)
        {
            var page = doc.Pages.Single(p => p.PageNumber == combined.PageNumber);
            var poles = SymbolCombiner.Split(page, combined);
            foreach (var s in poles) if (!s.UserReviewed) { s.SelectedSweSymbol = null; Score(s); }
            return poles;
        }

        /// <summary>
        /// Pole groups whose combined shape a reviewer has already mapped (exact learned signature) are combined
        /// automatically, so a 4-pole fuse disconnector mapped once arrives as one symbol on every later import.
        /// </summary>
        private void AutoCombine(PageAnalysis page)
        {
            foreach (var group in SymbolCombiner.Suggestions(page).ToList())
            {
                var c = SymbolCombiner.Merge(group);
                FinishSymbol(c, page);
                if (c.Candidates.FirstOrDefault()?.Source != "learned-exact") continue;
                SymbolCombiner.Commit(page, c);
                c.Notes.Add($"{c.CombinedFrom.Count} poles combined automatically (learned mapping).");
            }
        }

        /// <summary>Largest text in the title block: the drawing title ("Power", "List of components tags").</summary>
        private static string TitleOf(PageAnalysis p)
        {
            if (p.TitleBlock?.RawText == null || p.TitleBlock.RawText.Count == 0) return null;
            var inBlock = p.AllTexts.Where(t => t.Center.Y < p.Frame.Bottom + 0.2 * p.Frame.Height && p.TitleBlock.RawText.Contains(t.Text)).ToList();
            var big = inBlock.OrderByDescending(t => t.FontSize).FirstOrDefault();
            if (big == null) return null;
            var line = inBlock.Where(t => Math.Abs(t.Center.Y - big.Center.Y) < 0.6 && Math.Abs(t.FontSize - big.FontSize) < 0.3).OrderBy(t => t.Bounds.Left);
            return string.Join(" ", line.Select(t => t.Text.Trim()));
        }

        private ISet<int> _force = new HashSet<int>();
        private HashSet<string> _knownTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Cross-references name the sheet printed in the title block ("05-1" = sheet 05, zone 1): map to PDF pages.</summary>
        private static void MapSheetReferences(ImportDocument doc)
        {
            var bySheet = new Dictionary<int, int>();
            foreach (var p in doc.SchematicPages)
                if (int.TryParse(p.SheetNumber, out int n) && !bySheet.ContainsKey(n)) bySheet[n] = p.PageNumber;
            if (bySheet.Count == 0) return;
            foreach (var l in doc.Pages.SelectMany(p => p.OffPageLinks))
                if (l.TargetPage.HasValue && bySheet.TryGetValue(l.TargetPage.Value, out int pdfPage)) l.TargetPage = pdfPage;
        }

        private PageAnalysis AnalyzePage(RawPage raw, Action<string, double> report)
        {
            var page = new PageAnalysis { PageNumber = raw.Number, WidthMm = raw.WidthMm, HeightMm = raw.HeightMm, AllTexts = raw.Texts, AllSegments = raw.Segments };
            page.ForcedSchematic = _force.Contains(raw.Number);
            if (!page.ForcedSchematic && ReportReader.IsReportPage(raw, out var reportTitle))
            {
                page.Kind = PageKind.Report;
                page.ReportTitle = reportTitle;
                return page;
            }
            if (raw.IsRasterOnly)
            {
                page.IsRasterOnly = true;
                page.Warnings.Add("Page contains only raster images. Scanned pages are not supported — export the drawing from CAD as a vector PDF.");
                return page;
            }
            if (raw.Rotation != 0) page.Warnings.Add($"Page is rotated {raw.Rotation}° in the PDF; geometry is read unrotated.");

            report("Layout & title block", 0.15);
            var layoutAnalyzer = new PageLayoutAnalyzer(_o);
            var layout = layoutAnalyzer.Analyze(raw);
            page.Frame = layout.Frame;
            page.ExcludedRegions = layout.Excluded;
            page.ColumnCount = layout.ColumnCount;
            page.ZonesAreRows = layout.ZonesAreRows;
            page.TitleBlock = layoutAnalyzer.ReadTitleBlock(raw, layout);
            page.SheetNumber = layout.SheetNumber;
            if (layout.IsLayoutDrawing && !page.ForcedSchematic)
            {
                page.Kind = PageKind.Layout;
                return page;
            }
            foreach (var r in raw.Regions) page.Locations.Add(layoutAnalyzer.LabelRegion(raw, r));

            var live = raw.Segments.Where(s => !layout.BorderPathIds.Contains(s.PathId)
                                               && !(layoutAnalyzer.IsExcluded(layout, s.A) && layoutAnalyzer.IsExcluded(layout, s.B))).ToList();

            report("Tracing wires", 0.35);
            var wires = new WireExtractor(_o).Extract(live.Where(WireExtractor.IsWireCandidate).ToList(), raw.Shapes);
            page.JunctionDots = wires.JunctionDots;

            report("Clustering symbols", 0.55);
            var symbolSegs = live.Where(s => !WireExtractor.IsWireCandidate(s)).Concat(wires.Demoted).ToList();
            var clusters = new SymbolExtractor(_o).Extract(symbolSegs, raw.Shapes, wires.Nets, wires.JunctionDotPathIds);
            page.Nets = wires.Nets; // net indices are unique per page (not contiguous after stub pruning)

            int n = 0;
            foreach (var c in clusters)
            {
                if (c.IsArrow && c.Pins.Count == 1)
                {
                    page.OffPageLinks.Add(new OffPageLink
                    {
                        Id = $"P{raw.Number}-L{page.OffPageLinks.Count + 1}", PageNumber = raw.Number, Location = c.Pins[0].Location,
                        NetIndex = c.Pins[0].NetIndex, HasArrowSymbol = true, Direction = "origin",
                    });
                    continue;
                }
                if (c.Pins.Count == 0) continue; // annotation linework (underlines, hatching, notes)
                page.Symbols.Add(new DetectedSymbol
                {
                    Id = $"P{raw.Number}-S{++n}", PageNumber = raw.Number, Bounds = c.Bounds,
                    Segments = c.Segments, Shapes = c.Shapes, Pins = c.Pins,
                    PoleGroupId = c.LinkGroup >= 0 ? $"P{raw.Number}-M{c.LinkGroup}" : null,
                });
            }

            report("Reading text", 0.75);
            AssignText(raw, layout, layoutAnalyzer, page);
            report("Classifying", 0.9);
            return page;
        }

        private void AssignText(RawPage raw, PageLayoutAnalyzer.Layout layout, PageLayoutAnalyzer la, PageAnalysis page)
        {
            var freeEnds = page.Nets.SelectMany(net => net.FreeEnds.Select(e => (net, e)))
                .Where(x => !page.Symbols.Any(s => s.Pins.Any(p => p.Location.DistanceTo(x.e) < 1e-6))
                         && !page.OffPageLinks.Any(l => l.Location.DistanceTo(x.e) < 1e-6))
                .ToList();

            foreach (var t in raw.Texts)
            {
                if (la.IsExcluded(layout, t.Center)) continue;
                var trimmed = t.Text.Trim();
                bool pinName = TagParser.IsConventionalPinName(trimmed);
                if ((pinName || !_knownTags.Contains(trimmed)) && TryAssignPinLabel(page, t)) continue;

                var inside = page.Symbols.FirstOrDefault(s => s.Bounds.Contains(t.Center));
                var role = TagParser.Classify(t.Text);

                if (role == TextRole.DeviceTag)
                {
                    var parsed = TagParser.ParseDeviceTag(t.Text);
                    bool weak = parsed.IsWeak || pinName;
                    // a pin designation printed on or beside a symbol ("L1", "T2" on contactor poles) is not its tag
                    if (pinName)
                    {
                        var host = page.Symbols.Where(x => x.Bounds.Inflate(3.0).Contains(t.Center)).OrderBy(x => x.Bounds.DistanceTo(t.Bounds)).FirstOrDefault();
                        if (host != null) { host.InnerTexts.Add(trimmed); continue; }
                    }
                    var best = Nearest(page.Symbols, t.Bounds, _o.TagRadius);
                    if (best != null)
                    {
                        double d = best.Bounds.DistanceTo(t.Bounds);
                        bool replace = best.Tag == null || (best.TagWeak && !weak) || (best.TagWeak == weak && d < best.TagDistance);
                        if (replace) { best.Tag = parsed.Raw; best.TagDistance = d; best.TagWeak = weak; }
                    }
                    continue;
                }

                if (role == TextRole.CrossReference)
                {
                    TagParser.TryParseXref(t.Text, out int tp, out int tc);
                    var link = NearestLink(page, t.Bounds, _o.XrefRadius);
                    var end = NearestFreeEnd(freeEnds, t.Bounds, _o.XrefRadius);
                    var sym = Nearest(page.Symbols, t.Bounds, _o.XrefRadius);
                    double dl = link != null ? t.Bounds.DistanceTo(Rect.FromPoints(new[] { link.Location })) : double.MaxValue;
                    double de = end.HasValue ? t.Bounds.DistanceTo(Rect.FromPoints(new[] { end.Value.e })) : double.MaxValue;
                    double ds = sym != null ? sym.Bounds.DistanceTo(t.Bounds) : double.MaxValue;

                    if (link != null && dl <= de && dl <= ds)
                    {
                        link.XrefText = link.XrefText ?? t.Text; link.TargetPage = tp; link.TargetColumn = tc;
                    }
                    else if (end.HasValue && de <= ds)
                    {
                        var l = new OffPageLink
                        {
                            Id = $"P{page.PageNumber}-L{page.OffPageLinks.Count + 1}", PageNumber = page.PageNumber, Location = end.Value.e,
                            NetIndex = end.Value.net.Index, XrefText = t.Text, TargetPage = tp, TargetColumn = tc, Direction = "destination",
                        };
                        page.OffPageLinks.Add(l);
                        freeEnds.RemoveAll(x => x.e.DistanceTo(end.Value.e) < 1e-6);
                    }
                    else sym?.Xrefs.Add(t.Text);
                    continue;
                }

                if (inside != null) { inside.InnerTexts.Add(t.Text); continue; }

                if (role == TextRole.WireNumber && StackedUnder(page, raw, t) is DetectedSymbol owner)
                {
                    owner.Attributes.Add(t.Text); // part number / rating printed in the device's attribute stack
                    continue;
                }

                if (role == TextRole.WireNumber)
                {
                    var net = NearestWireForNumber(page, t);
                    if (net != null)
                    {
                        if (net.WireNumber == null) net.WireNumber = t.Text;
                        else if (net.WireNumber != t.Text) page.Warnings.Add($"Wire numbers {net.WireNumber} and {t.Text} found on the same net; kept {net.WireNumber}.");
                        continue;
                    }
                    var sym = Nearest(page.Symbols, t.Bounds, 4);
                    if (sym != null && TagParser.IsRungList(t.Text)) { sym.Xrefs.Add(t.Text); continue; }
                }

                var fe = char.IsLetterOrDigit(trimmed.FirstOrDefault()) ? NearestFreeEnd(freeEnds, t.Bounds, _o.LabelRadius) : null;
                if (fe.HasValue)
                {
                    page.OffPageLinks.Add(new OffPageLink
                    {
                        Id = $"P{page.PageNumber}-L{page.OffPageLinks.Count + 1}", PageNumber = page.PageNumber, Location = fe.Value.e,
                        NetIndex = fe.Value.net.Index, Label = t.Text, Direction = "label",
                    });
                    freeEnds.RemoveAll(x => x.e.DistanceTo(fe.Value.e) < 1e-6);
                    continue;
                }
                var near = Nearest(page.Symbols, t.Bounds, _o.LabelRadius);
                near?.Attributes.Add(t.Text);
            }
        }

        /// <summary>
        /// Device attributes are printed as a centred column under (or over) the symbol: manufacturer, part number,
        /// rating. A number in such a column ("34241", "30") is an attribute, not a wire number.
        /// </summary>
        private static DetectedSymbol StackedUnder(PageAnalysis page, RawPage raw, TextItem t)
        {
            foreach (var s in page.Symbols)
            {
                if (Math.Abs(t.Center.X - s.Bounds.Center.X) > 1.5) continue;
                double gap = t.Center.Y < s.Bounds.Bottom ? s.Bounds.Bottom - t.Bounds.Top : t.Bounds.Bottom - s.Bounds.Top;
                if (gap < -0.2 || gap > 8) continue;
                bool column = raw.Texts.Any(o => !ReferenceEquals(o, t) && Math.Abs(o.Center.X - t.Center.X) < 1.5
                                                 && Math.Abs(o.Center.Y - t.Center.Y) < 3.5 && !s.Bounds.Contains(o.Center));
                if (column) return s;
            }
            return null;
        }

        /// <summary>
        /// Short labels printed beside a pin ("13", "A1", "T2", "+24") name that pin. Device marks
        /// ("04K1", "-K1") are never pin labels.
        /// </summary>
        private bool TryAssignPinLabel(PageAnalysis page, TextItem t)
        {
            var text = t.Text.Trim();
            if (text.Length == 0 || text.Length > 4 || text.Contains("-") && text.Length > 2) return false;
            var tag = TagParser.ParseDeviceTag(text);
            if (tag != null && !tag.IsWeak && !TagParser.IsConventionalPinName(text)) return false;
            SymbolPin best = null; DetectedSymbol owner = null; double bd = _o.PinLabelRadius;
            foreach (var s in page.Symbols)
            {
                if (!s.Bounds.Inflate(2.5).Contains(t.Center)) continue;
                foreach (var p in s.Pins)
                {
                    double d = t.Bounds.DistanceTo(Rect.FromPoints(new[] { p.Location }));
                    if (d < bd) { bd = d; best = p; owner = s; }
                }
            }
            if (best == null) return false;
            if (best.Label == null) best.Label = text;
            else owner.InnerTexts.Add(text);
            return true;
        }

        /// <summary>
        /// Wire numbers sit just above horizontal wires and just left of vertical ones; when two wires are
        /// equally close, the conventional side wins.
        /// </summary>
        private WireNet NearestWireForNumber(PageAnalysis page, TextItem t)
        {
            WireNet best = null;
            double bestD = _o.WireNumberRadius + 0.5;
            var c = t.Center;
            foreach (var net in page.Nets)
                foreach (var s in net.Segments)
                {
                    double d = t.Bounds.DistanceTo(s.Bounds);
                    if (s.Axis == Axis.Horizontal && s.A.Y > c.Y) d += 1.5; // wire above the text: unusual
                    if (s.Axis == Axis.Vertical && s.A.X < c.X) d += 0.5;   // wire left of the text: less usual
                    if (d < bestD) { bestD = d; best = net; }
                }
            return best;
        }

        private static DetectedSymbol Nearest(IEnumerable<DetectedSymbol> syms, Rect r, double radius)
        {
            DetectedSymbol best = null; double bd = radius;
            foreach (var s in syms) { double d = s.Bounds.DistanceTo(r); if (d <= bd) { bd = d; best = s; } }
            return best;
        }

        private static OffPageLink NearestLink(PageAnalysis p, Rect r, double radius) =>
            p.OffPageLinks.Where(l => l.HasArrowSymbol && l.XrefText == null)
                          .Select(l => (l, d: r.DistanceTo(Rect.FromPoints(new[] { l.Location }))))
                          .Where(x => x.d <= radius).OrderBy(x => x.d).Select(x => x.l).FirstOrDefault();

        private static (WireNet net, Pt e)? NearestFreeEnd(List<(WireNet net, Pt e)> ends, Rect r, double radius)
        {
            (WireNet, Pt)? best = null; double bd = radius;
            foreach (var x in ends) { double d = r.DistanceTo(Rect.FromPoints(new[] { x.e })); if (d <= bd) { bd = d; best = x; } }
            return best;
        }

        /// <summary>Per-page standard from tag syntax; undecided pages inherit the document majority.</summary>
        private static void ResolveStandards(ImportDocument doc)
        {
            foreach (var p in doc.Pages)
            {
                var tags = p.Symbols.Where(s => s.Tag != null).Select(s => TagParser.ParseDeviceTag(s.Tag)).Where(t => t != null).ToList();
                int iec = tags.Count(t => t.Standard == SymbolStandard.IEC), ansi = tags.Count(t => t.Standard == SymbolStandard.ANSI);
                // IEC column cross-refs ("/3.4") are a secondary hint
                iec += p.OffPageLinks.Count(l => l.XrefText != null && l.XrefText.StartsWith("/"));
                if (iec + ansi == 0)
                {
                    // SOLIDWORKS Electrical default marks (04Q1) carry no standard; its ANSI templates number zones as rows
                    if (p.Kind == PageKind.Schematic && p.ZonesAreRows) { p.Standard = SymbolStandard.ANSI; p.StandardConfidence = 0.6; }
                    else p.Standard = SymbolStandard.Unknown;
                    continue;
                }
                p.Standard = iec >= ansi ? SymbolStandard.IEC : SymbolStandard.ANSI;
                p.StandardConfidence = (double)Math.Max(iec, ansi) / (iec + ansi);
            }
            var votes = doc.Pages.Where(p => p.Standard != SymbolStandard.Unknown).GroupBy(p => p.Standard).OrderByDescending(g => g.Count()).FirstOrDefault();
            var fallback = votes?.Key ?? SymbolStandard.IEC;
            foreach (var p in doc.Pages.Where(p => p.Standard == SymbolStandard.Unknown))
            {
                p.Standard = fallback; p.StandardConfidence = 0.5;
                if (p.Symbols.Count > 0) p.Warnings.Add($"Symbol standard could not be determined from tags; assumed {fallback}.");
            }
        }

        public void FinishSymbol(DetectedSymbol s, PageAnalysis page)
        {
            var parsed = TagParser.ParseDeviceTag(s.Tag);
            s.Notes.Clear();
            if (s.TagInherited) s.Notes.Add("Tag inherited from an adjacent pole of the same device.");
            s.Standard = parsed == null || parsed.Standard == SymbolStandard.Unknown ? page.Standard : parsed.Standard;
            if (!s.IsCombined) s.Kind = SymbolClassifier.Classify(s, s.Standard); // combined: keep the poles' kind

            var rule = _rules.Find(parsed);
            var allowed = AllowedKinds(s, parsed);
            s.Category = rule?.Category ?? (s.Kind == SymbolKind.Unknown ? "Unclassified" : s.Kind.ToString());

            // Pin numbering is the strongest single hint (A1/A2 coil, 13/14 NO, 21/22 NC, H1/H2 lamp, U/V/W motor).
            var byPins = SymbolClassifier.KindFromPinLabels(s);
            if (byPins.HasValue && byPins.Value != s.Kind && (allowed.Count == 0 || allowed.Contains(byPins.Value)))
            {
                s.Kind = byPins.Value;
                s.Notes.Add("Kind from pin numbering.");
            }
            if (allowed.Count > 0 && !allowed.Contains(s.Kind))
            {
                var pick = allowed.Count == 1 ? allowed[0] : allowed.FirstOrDefault(k => SymbolClassifier.Plausible(s, k));
                if (pick != SymbolKind.Unknown)
                {
                    s.Notes.Add(s.Part != null ? $"Kind from the parts list: {s.Part.Description}." : "Kind inferred from tag prefix only.");
                    s.Kind = pick;
                }
            }
            if (s.Part != null && !string.IsNullOrEmpty(s.Part.Description) && rule == null) s.Category = s.Part.Description;

            s.Rotation = ComputeRotation(s);
            s.Signature = SymbolSignature.Compute(s.Segments, s.Shapes, s.Pins, s.InnerTexts, SymbolClassifier.CountPoles(s));
            Score(s, allowed);
        }

        /// <summary>Kinds the tag prefix allows, narrowed by the parts-list description when the PDF has one.</summary>
        private List<SymbolKind> AllowedKinds(DetectedSymbol s, ParsedTag parsed)
        {
            var byPrefix = _rules.AllowedKinds(parsed).ToList();
            var byPart = ReportReader.KindsFromDescription(s.Part);
            if (byPart.Count == 0) return byPrefix;
            if (byPrefix.Count == 0) return byPart;
            var both = byPrefix.Intersect(byPart).ToList();
            return both.Count > 0 ? both : byPart;
        }

        /// <summary>Re-scores after a reviewer action or a mapping change.</summary>
        public void Score(DetectedSymbol s, List<SymbolKind> allowed = null)
        {
            allowed = allowed ?? AllowedKinds(s, TagParser.ParseDeviceTag(s.Tag));
            double conf = s.Kind != SymbolKind.Unknown ? 0.45 : 0.15;
            if (s.Tag != null) conf += s.TagInherited ? 0.08 : 0.15;
            if (s.Part != null && allowed.Contains(s.Kind)) conf += 0.1;
            if (allowed.Count > 0) conf += allowed.Contains(s.Kind) ? 0.15 : -0.2;
            if (s.Tag == null && !s.Notes.Contains("No device tag found nearby.")) s.Notes.Add("No device tag found nearby.");

            s.Candidates = _mappings.Candidates(s, _o.SimilarMatchThreshold);
            var top = s.Candidates.FirstOrDefault();
            if (top != null)
            {
                if (top.Source == "learned-exact") conf = Math.Max(conf, top.Score);
                else conf = Math.Min(0.95, conf + (top.Source == "kind-default" ? 0.25 : 0.2));
                if (s.SelectedSweSymbol == null) s.SelectedSweSymbol = top.SweSymbolName;
            }
            s.Confidence = Math.Max(0, Math.Min(1, conf));

            if (s.UserReviewed) return; // never override a reviewer decision
            if (s.SelectedSweSymbol == null) s.Review = ReviewState.Unmapped;
            else s.Review = s.Confidence >= _o.AutoConfirmThreshold ? ReviewState.Confirmed : ReviewState.NeedsReview;
        }

        /// <summary>IEC library symbols are drawn with connections top/bottom; ANSI ladder symbols left/right.</summary>
        private static int ComputeRotation(DetectedSymbol s)
        {
            if (s.Pins.Count == 0) return 0;
            bool nativeVertical = s.Standard != SymbolStandard.ANSI;
            if (s.Pins.Count == 1)
            {
                switch (s.Pins[0].Side)
                {
                    case PinSide.Top: return nativeVertical ? 0 : 90;
                    case PinSide.Left: return nativeVertical ? 90 : 0;
                    case PinSide.Bottom: return nativeVertical ? 180 : 270;
                    default: return nativeVertical ? 270 : 180;
                }
            }
            bool vertical = s.Pins.Count(p => p.Side == PinSide.Top || p.Side == PinSide.Bottom) >= s.Pins.Count / 2.0;
            return vertical == nativeVertical ? 0 : 90;
        }

        /// <summary>
        /// Multi-pole devices drawn as separate identical poles in a row (3-pole breaker, main contacts):
        /// untagged poles inherit the tag of the tagged pole in the same row.
        /// </summary>
        private void GroupPoles(PageAnalysis page)
        {
            // Poles joined by a dashed mechanical link are one device.
            foreach (var g in page.Symbols.Where(s => s.PoleGroupId != null && s.PoleGroupId.Contains("-M")).GroupBy(s => s.PoleGroupId).ToList())
            {
                var tagged = g.Where(s => s.Tag != null && !s.TagInherited).OrderBy(s => s.TagWeak).ThenBy(s => s.TagDistance).FirstOrDefault();
                if (tagged == null) continue;
                foreach (var s in g.Where(s => s != tagged && (s.Tag == null || s.TagWeak && !tagged.TagWeak)))
                {
                    s.Tag = tagged.Tag; s.TagInherited = true; s.TagWeak = tagged.TagWeak;
                }
                foreach (var s in g) { s.Part = s.Part ?? tagged.Part; FinishSymbol(s, page); }
            }
            GroupBySignature(page);
            InheritAlignedPoles(page);
            InheritTerminalStrips(page);
        }

        /// <summary>
        /// Fallback when small drawing differences give poles different signatures: an untagged symbol of the same kind
        /// and size, directly under (or right of) a tagged one within pole spacing, is the next pole of that device.
        /// </summary>
        private void InheritAlignedPoles(PageAnalysis page)
        {
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var s in page.Symbols.Where(x => x.Tag == null && x.Kind != SymbolKind.Unknown && x.Kind != SymbolKind.Terminal).ToList())
                {
                    DetectedSymbol pick = null; double pd = double.MaxValue;
                    foreach (var t in page.Symbols.Where(o => o.Tag != null && o.Kind == s.Kind
                                                              && Math.Abs(o.Bounds.Width - s.Bounds.Width) < 0.5 && Math.Abs(o.Bounds.Height - s.Bounds.Height) < 0.5))
                    {
                        double d;
                        if (Math.Abs(t.Bounds.Left - s.Bounds.Left) < 1.0) d = t.Anchor.Y - s.Anchor.Y;
                        else if (Math.Abs(t.Bounds.Bottom - s.Bounds.Bottom) < 1.0) d = s.Anchor.X - t.Anchor.X;
                        else continue;
                        if (d <= 0 || d > _o.PoleSpacingMax / 2) continue;
                        if (d < pd) { pd = d; pick = t; }
                    }
                    if (pick == null) continue;
                    s.Tag = pick.Tag; s.TagInherited = true; s.TagWeak = pick.TagWeak; s.Part = s.Part ?? pick.Part;
                    s.PoleGroupId = pick.PoleGroupId ?? (pick.PoleGroupId = $"{pick.Id}-poles");
                    FinishSymbol(s, page);
                    grew = true;
                }
            }
        }

        /// <summary>Terminals stacked in a strip share the strip tag printed once at its start ("X1").</summary>
        private void InheritTerminalStrips(PageAnalysis page)
        {
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (var s in page.Symbols.Where(x => x.Tag == null && x.Kind == SymbolKind.Terminal))
                {
                    var src = page.Symbols
                        .Where(o => o.Tag != null && o.Kind == SymbolKind.Terminal
                                    && (Math.Abs(o.Bounds.Left - s.Bounds.Left) < 1.0 && Math.Abs(o.Anchor.Y - s.Anchor.Y) <= _o.PoleSpacingMax
                                        || Math.Abs(o.Bounds.Bottom - s.Bounds.Bottom) < 1.0 && Math.Abs(o.Anchor.X - s.Anchor.X) <= _o.PoleSpacingMax))
                        .OrderBy(o => o.Anchor.DistanceTo(s.Anchor)).FirstOrDefault();
                    if (src == null) continue;
                    s.Tag = src.Tag; s.TagInherited = true; s.Part = s.Part ?? src.Part;
                    FinishSymbol(s, page);
                    grew = true;
                }
            }
        }

        /// <summary>
        /// Identical poles drawn in a stack or a row share the device mark printed once beside the group. Poles are
        /// assigned greedily, nearest first, and each assigned pole passes the mark on to its neighbour. The usual mark
        /// position (top pole in an ANSI stack, left pole in an IEC row) is preferred, so the first pole of the next
        /// device in the same column never hands its mark upwards to the previous device.
        /// </summary>
        private void GroupBySignature(PageAnalysis page)
        {
            foreach (var g in page.Symbols.Where(s => s.Signature != null).GroupBy(s => s.Signature.Key))
            {
                var list = g.ToList();
                if (!list.Any(s => s.Tag != null)) continue;
                var root = new Dictionary<DetectedSymbol, DetectedSymbol>();
                foreach (var t in list.Where(s => s.Tag != null)) root[t] = t;
                while (true)
                {
                    DetectedSymbol bestPole = null, bestSrc = null; double best = double.MaxValue;
                    foreach (var s in list.Where(x => x.Tag == null && !root.ContainsKey(x)))
                        foreach (var t in root.Keys)
                        {
                            double e = PoleDistance(t, s);
                            if (e < best) { best = e; bestPole = s; bestSrc = t; }
                        }
                    if (bestPole == null) break;
                    root[bestPole] = root[bestSrc];
                }
                foreach (var grp in root.Where(kv => kv.Key != kv.Value).GroupBy(kv => kv.Value))
                {
                    var src = grp.Key;
                    var gid = src.PoleGroupId ?? $"{src.Id}-poles";
                    src.PoleGroupId = gid;
                    foreach (var pole in grp.Select(kv => kv.Key))
                    {
                        pole.Tag = src.Tag; pole.TagInherited = true; pole.TagWeak = src.TagWeak; pole.PoleGroupId = gid;
                        pole.Part = pole.Part ?? src.Part;
                        FinishSymbol(pole, page);
                    }
                    FinishSymbol(src, page);
                }
            }
        }

        /// <summary>
        /// Distance from a tagged pole to the next candidate pole, or MaxValue when they are not aligned or are too far
        /// apart. Against the usual mark direction the distance is penalised by half the pole spacing limit.
        /// </summary>
        private double PoleDistance(DetectedSymbol tagged, DetectedSymbol s)
        {
            bool stacked = Math.Abs(tagged.Bounds.Left - s.Bounds.Left) < 1.0 && Math.Abs(tagged.Bounds.Right - s.Bounds.Right) < 1.0;
            bool row = Math.Abs(tagged.Bounds.Bottom - s.Bounds.Bottom) < 1.0 && Math.Abs(tagged.Bounds.Top - s.Bounds.Top) < 1.0;
            double d;
            if (stacked) d = tagged.Anchor.Y - s.Anchor.Y;      // usual: mark on the top pole
            else if (row) d = s.Anchor.X - tagged.Anchor.X;     // usual: mark on the left pole
            else return double.MaxValue;
            if (Math.Abs(d) > _o.PoleSpacingMax || Math.Abs(d) < 1e-6) return double.MaxValue;
            return d > 0 ? d : -d + _o.PoleSpacingMax / 2;
        }
    }
}
