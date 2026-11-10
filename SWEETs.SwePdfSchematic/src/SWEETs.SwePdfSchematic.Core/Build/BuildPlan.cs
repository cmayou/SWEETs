using System;
using System.Collections.Generic;
using System.Linq;
using SWEETs.SwePdfSchematic.Core.Geometry;
using SWEETs.SwePdfSchematic.Core.Mapping;
using SWEETs.SwePdfSchematic.Core.Model;

namespace SWEETs.SwePdfSchematic.Core.Build
{
    /// <summary>Drawing units of the SOLIDWORKS Electrical template (ANSI templates are usually inch, IEC metric).</summary>
    public enum DrawingUnits { Millimeters, Inches }

    public static class UnitConversion
    {
        /// <summary>Drawing units per millimetre of PDF geometry.</summary>
        public static double PerMm(DrawingUnits u) => u == DrawingUnits.Inches ? 1 / 25.4 : 1.0;
        public static string Label(DrawingUnits u) => u == DrawingUnits.Inches ? "in" : "mm";
    }

    public sealed class BuildOptions
    {
        /// <summary>
        /// Units of the template's drawing. The PDF is read in millimetres; coordinates, drawing area and grid are in
        /// these units. Wrong units scale the schematic by 25.4.
        /// </summary>
        public DrawingUnits Units { get; set; } = DrawingUnits.Millimeters;
        public string ProjectName { get; set; }
        public string ProjectDescription { get; set; }
        /// <summary>SWE project template name (as listed by the template manager, e.g. "IEC" / "ANSI").</summary>
        public string TemplateName { get; set; }
        /// <summary>Optional .tewzip template archive path; when set it is unarchived instead of using TemplateName.</summary>
        public string TemplateArchivePath { get; set; }
        /// <summary>File of the template picked from the environment's template folder; tried when the name is not accepted.</summary>
        public string TemplatePath { get; set; }
        /// <summary>Drawing area of the scheme sheet in the template, mm.</summary>
        public double SheetDrawingLeft { get; set; } = 10;
        public double SheetDrawingBottom { get; set; } = 10;
        public double SheetDrawingWidth { get; set; } = 400;
        public double SheetDrawingHeight { get; set; } = 277;
        public double GridSnap { get; set; } = 2.5;
        public bool FitToSheet { get; set; } = true;
        public bool AttachSourcePdf { get; set; } = true;
        public bool CreateOriginDestinationArrows { get; set; } = true;
        public bool ApplyWireNumbers { get; set; } = true;
        /// <summary>Put the manufacturer parts read from the PDF (and edited in review) on the components.</summary>
        public bool AssignParts { get; set; } = true;
        /// <summary>Create cables, rails, ducts and location items that have parts but no schematic symbol.</summary>
        public bool CreateItemsWithoutSymbols { get; set; } = true;
    }

    public sealed class PartPlan
    {
        public string Manufacturer;
        public string Reference;
        public string Description;
        public string ArticleNumber;
        public int Quantity = 1;
        /// <summary>Gets the circuits and terminals of the component's symbols when the catalog part has none.</summary>
        public bool CarriesCircuits;
        public override string ToString() => $"{Manufacturer} {Reference}" + (Quantity > 1 ? $" ×{Quantity}" : "");
    }

    /// <summary>A symbol placed for a component, with the PDF's terminal designation at each of its library pins.</summary>
    public sealed class PlacedSymbol
    {
        public string Handle;
        public string SweSymbolName;
        public string SourceId;
        public List<(Pt at, string name)> PinNames = new List<(Pt, string)>();
    }

    public sealed class ProjectSpec
    {
        public string Name;
        public string Description;
        public string TemplateName;
        public string TemplateArchivePath;
        public string TemplatePath;
        public string SourcePdfPath;
        /// <summary>Drawing units of the template; library symbols drawn in the other unit are scaled to match.</summary>
        public DrawingUnits Units = DrawingUnits.Millimeters;
        public Dictionary<string, string> TitleBlock = new Dictionary<string, string>();
    }

    public sealed class SymbolPlan
    {
        public string SourceId;
        public string SweSymbolName;
        public Pt Position;
        public int Rotation;
        public string ComponentTag;
        public List<Pt> ExpectedPins = new List<Pt>();
        /// <summary>Terminal designation printed at each expected pin ("13", "A1", "L1"), or null.</summary>
        public List<string> ExpectedPinNames = new List<string>();
    }

    public sealed class WirePlan
    {
        public int SourceNetIndex;
        public List<Segment> Segments = new List<Segment>();
        public string WireNumber;
    }

    /// <summary>A PDF wire number and the library pins its net connects on one page.</summary>
    public sealed class WireNumberPlan
    {
        public string PageHandle;
        public int SourcePage;
        public string Number;
        public List<Pt> Pins = new List<Pt>();
    }

    public sealed class LinkPlan
    {
        public string SourceId;
        public string PairedSourceId;
        public Pt Location;
        public string Label;
    }

    public sealed class PagePlan
    {
        public int SourcePage;
        public string Title;
        public string Description;
        public SymbolStandard Standard;
        public List<SymbolPlan> Symbols = new List<SymbolPlan>();
        public List<WirePlan> Wires = new List<WirePlan>();
        public List<LinkPlan> Links = new List<LinkPlan>();
    }

    public enum ComponentRole { Component, TerminalStrip, Terminal }

    public sealed class ComponentPlan
    {
        /// <summary>Key used to attach symbols and parts ("04K1", "X1", "X1.4").</summary>
        public string Tag;
        /// <summary>Mark written in SOLIDWORKS Electrical when it differs from <see cref="Tag"/> (a terminal's number "4").</summary>
        public string Mark;
        public ComponentRole Role = ComponentRole.Component;
        /// <summary>Terminal strip key of a terminal ("X1").</summary>
        public string ParentTag;
        /// <summary>Library symbol whose classification the component takes (the coil / first symbol mapped to it).</summary>
        public string SweSymbolName;
        public string Category;
        /// <summary>Component (default), Cable, Location (enclosure items) or Terminal; extras only.</summary>
        public BomItemKind Kind = BomItemKind.Component;
        /// <summary>Location an item without a symbol is placed in ("L1"), or null.</summary>
        public string LocationTag;
        public string Description;
        public List<PartPlan> Parts = new List<PartPlan>();
        /// <summary>Filled while building: the symbols inserted for this component (source of the part's circuits).</summary>
        public List<PlacedSymbol> Symbols = new List<PlacedSymbol>();
    }

    public sealed class BuildPlan
    {
        public ProjectSpec Project = new ProjectSpec();
        public List<ComponentPlan> Components = new List<ComponentPlan>();
        public List<PagePlan> Pages = new List<PagePlan>();
        /// <summary>Cables, rails, ducts and location items that have parts but no symbol on the drawings.</summary>
        public List<ComponentPlan> Extras = new List<ComponentPlan>();
        public DrawingUnits Units = DrawingUnits.Millimeters;
        public int TotalOperations => 2 + Components.Count + Pages.Sum(p => 1 + p.Symbols.Count + p.Wires.Count + p.Links.Count)
                                      + Components.Count(c => c.Parts.Count > 0) + Extras.Count + 1;
    }

    /// <summary>Converts a reviewed <see cref="ImportDocument"/> into sheet-space build instructions.</summary>
    public static class BuildPlanFactory
    {
        public static BuildPlan Create(ImportDocument doc, BuildOptions o, MappingStore mappings)
        {
            if (!doc.ReadyToBuild) throw new InvalidOperationException("Review is not complete: map or ignore every symbol before building.");

            var info = doc.DocumentInfo;
            var plan = new BuildPlan
            {
                Units = o.Units,
                Project = new ProjectSpec
                {
                    Name = Sanitize(o.ProjectName ?? info.Get("DWGNO", "DRAWINGNO", "DWG", "PROJECT") ?? System.IO.Path.GetFileNameWithoutExtension(doc.SourceFileName)),
                    Description = o.ProjectDescription ?? info.Get("PROJECT", "TITLE") ?? $"Imported from {doc.SourceFileName}",
                    TemplateName = o.TemplateName,
                    TemplateArchivePath = o.TemplateArchivePath,
                    TemplatePath = o.TemplatePath,
                    Units = o.Units,
                    SourcePdfPath = o.AttachSourcePdf ? doc.SourcePath : null,
                    TitleBlock = new Dictionary<string, string>(info.Fields),
                },
            };

            CrossRefResolverSync(doc);
            plan.Components = Components(doc);
            if (o.AssignParts)
            {
                foreach (var c in plan.Components)
                {
                    var bom = doc.BomFor(c.Tag);
                    if (bom != null && bom.Include) c.Parts = Parts(bom);
                }
                if (o.CreateItemsWithoutSymbols)
                    plan.Extras = doc.BomWithoutSymbols
                        .Where(b => b.Include && b.Parts.Any(p => p.Include))
                        .Select(b => new ComponentPlan
                        {
                            Tag = b.Kind == BomItemKind.Terminal ? b.Strip + "." + b.TerminalNumber : b.Mark,
                            Mark = b.Kind == BomItemKind.Terminal ? b.TerminalNumber : null,
                            Role = b.Kind == BomItemKind.Terminal ? ComponentRole.Terminal : ComponentRole.Component,
                            ParentTag = b.Kind == BomItemKind.Terminal ? b.Strip : null,
                            Kind = b.Kind, LocationTag = b.LocationTag, Description = b.Description, Category = b.Description, Parts = Parts(b),
                        })
                        .ToList();
            }

            foreach (var page in doc.IncludedPages) // reports, lists, layouts and pages the reviewer left out are not built
            {
                var xf = Transform(page, o);
                var pp = new PagePlan
                {
                    SourcePage = page.PageNumber,
                    Standard = page.Standard,
                    Title = page.TitleBlock.Get("TITLE") ?? $"Imported page {page.PageNumber}",
                    Description = $"{doc.SourceFileName} — page {page.PageNumber}",
                };

                foreach (var s in page.Symbols.Where(s => s.Review == ReviewState.Confirmed))
                {
                    pp.Symbols.Add(new SymbolPlan
                    {
                        SourceId = s.Id,
                        SweSymbolName = s.SelectedSweSymbol,
                        Position = Snap(xf(s.Anchor), o.GridSnap),
                        Rotation = Normalize(s.Rotation + mappings.RotationOffset(s, s.SelectedSweSymbol)),
                        ComponentTag = s.Kind == SymbolKind.Terminal && s.TerminalNumber != null ? s.PartsKey : s.Tag,
                        ExpectedPins = s.Pins.Select(p => Snap(xf(p.Location), o.GridSnap)).ToList(),
                        ExpectedPinNames = s.Pins.Select(p => p.Label).ToList(),
                    });
                }

                foreach (var net in page.Nets)
                {
                    var wp = new WirePlan { SourceNetIndex = net.Index, WireNumber = o.ApplyWireNumbers ? net.WireNumber : null };
                    foreach (var seg in net.Segments)
                    {
                        var a = Snap(xf(seg.A), o.GridSnap);
                        var b = Snap(xf(seg.B), o.GridSnap);
                        if (a.DistanceTo(b) > 0.01) wp.Segments.Add(new Segment(a, b));
                    }
                    if (wp.Segments.Count > 0) pp.Wires.Add(wp);
                }

                if (o.CreateOriginDestinationArrows)
                    foreach (var l in page.OffPageLinks.Where(l => l.PairedLinkId != null))
                        pp.Links.Add(new LinkPlan { SourceId = l.Id, PairedSourceId = l.PairedLinkId, Location = Snap(xf(l.Location), o.GridSnap), Label = l.Label ?? l.XrefText });

                plan.Pages.Add(pp);
            }
            return plan;
        }

        private static void CrossRefResolverSync(ImportDocument doc) => Recognition.CrossReferenceResolver.BuildComponents(doc);

        /// <summary>Maps the PDF frame onto the template drawing area (uniform scale, aspect preserved).</summary>
        public static Func<Pt, Pt> Transform(PageAnalysis page, BuildOptions o)
        {
            var f = page.Frame.Width > 0 ? page.Frame : new Rect(0, 0, page.WidthMm, page.HeightMm);
            // drawing area is in drawing units, the frame in mm: the fit scale converts units at the same time
            double scale = UnitConversion.PerMm(o.Units);
            if (o.FitToSheet)
                scale = Math.Min(o.SheetDrawingWidth / f.Width, o.SheetDrawingHeight / f.Height);
            return p => new Pt(o.SheetDrawingLeft + (p.X - f.Left) * scale, o.SheetDrawingBottom + (p.Y - f.Bottom) * scale);
        }

        /// <summary>
        /// One component per tag, except terminal strips: the strip ("X1", type terminal strip) followed by one terminal
        /// component per terminal ("X1.4", mark "4", type terminal, parent X1). Each takes the classification of its symbol.
        /// </summary>
        private static List<ComponentPlan> Components(ImportDocument doc)
        {
            var list = new List<ComponentPlan>();
            foreach (var g in doc.Components)
            {
                var symbols = g.Symbols.Where(s => s.Review == ReviewState.Confirmed).ToList();
                if (symbols.Count > 0 && symbols.All(s => s.Kind == SymbolKind.Terminal && s.TerminalNumber != null))
                {
                    list.Add(new ComponentPlan { Tag = g.Tag, Role = ComponentRole.TerminalStrip, Category = "Terminal strip" });
                    foreach (var t in symbols.GroupBy(s => s.TerminalNumber, StringComparer.OrdinalIgnoreCase))
                        list.Add(new ComponentPlan
                        {
                            Tag = t.First().PartsKey, Mark = t.Key, Role = ComponentRole.Terminal, ParentTag = g.Tag,
                            Category = t.First().Category, SweSymbolName = t.First().SelectedSweSymbol,
                        });
                    continue;
                }
                var parent = symbols.FirstOrDefault(s => s.Kind == SymbolKind.Coil) ?? symbols.FirstOrDefault() ?? g.Parent;
                list.Add(new ComponentPlan { Tag = g.Tag, Category = g.Category, SweSymbolName = parent?.SelectedSweSymbol });
            }
            return list;
        }

        private static List<PartPlan> Parts(BomTarget b)
        {
            b.EnsureBasePart();
            return b.Parts.Where(p => p.Include && !string.IsNullOrWhiteSpace(p.Reference))
                    .Select(p => new PartPlan { Manufacturer = p.Manufacturer, Reference = p.Reference, Description = p.Description, ArticleNumber = p.ArticleNumber,
                                                Quantity = Math.Max(1, p.Quantity), CarriesCircuits = p.CarriesCircuits })
                    .ToList();
        }

        public static Pt Snap(Pt p, double grid) =>
            grid <= 0 ? p : new Pt(Math.Round(p.X / grid) * grid, Math.Round(p.Y / grid) * grid);

        private static int Normalize(int deg) => ((deg % 360) + 360) % 360;

        private static string Sanitize(string name)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }
    }
}
