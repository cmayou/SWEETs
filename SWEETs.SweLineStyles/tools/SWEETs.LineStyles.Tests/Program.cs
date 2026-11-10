using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SWEETs.SweLineStyles.AddIn.Core;

namespace SWEETs.LineStyles.Tests
{
    /// <summary>
    /// Checks the line-style and shape-file formats against SOLIDWORKS Electrical 2026's own files (testdata\).
    /// Usage: SWEETs.LineStyles.Tests.exe [Support folder to compare as well]
    /// </summary>
    internal static class Program
    {
        private static int _fail;
        private static readonly Encoding Cp1252 = Encoding.GetEncoding(1252);

        private static void Check(bool ok, string name, string detail = "")
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? "  " + detail : "")}");
            if (!ok) _fail++;
        }

        private static int Main(string[] args)
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "testdata");
            try
            {
                Run(dir, "testdata");
                if (args.Length > 0 && Directory.Exists(args[0])) Run(args[0], "installed");
                Synthetic();
            }
            catch (Exception ex) { Console.WriteLine("FAIL exception: " + ex); _fail++; }
            Console.WriteLine(_fail == 0 ? "ALL CHECKS PASSED" : $"{_fail} CHECK(S) FAILED");
            return _fail == 0 ? 0 : 1;
        }

        private static void Run(string dir, string label)
        {
            var shpBytes = File.ReadAllBytes(Path.Combine(dir, "ltypeshp.shp"));
            var shxBytes = File.ReadAllBytes(Path.Combine(dir, "ltypeshp.shx"));
            var xlinBytes = File.ReadAllBytes(Path.Combine(dir, "linestyle.xlin"));

            // --- shapes
            var shp = ShpFile.Parse(Cp1252.GetString(shpBytes));
            Check(shp.Warnings.Count == 0, $"[{label}] shp parses without warnings", string.Join("; ", shp.Warnings));
            var fromShx = ShxFile.Read(shxBytes, out var signature);
            Check(fromShx.Count == shp.Shapes.Count, $"[{label}] shx read: {fromShx.Count} shapes, signature '{signature.Trim()}'");
            var compiled = ShxFile.Compile(shp.Shapes, signature);
            Check(compiled.SequenceEqual(shxBytes), $"[{label}] shp compiles to the shipped shx byte for byte", $"{compiled.Length} vs {shxBytes.Length} bytes");
            Check(Cp1252.GetBytes(shp.ToText()).SequenceEqual(shpBytes), $"[{label}] shp round trip is byte-identical");

            foreach (var s in shp.Shapes)
            {
                var gen = ShpFile.Generate(new ShapeDefinition { Number = s.Number, Name = s.Name, Bytes = s.Bytes });
                var re = ShpFile.Parse(gen).Shapes.Single();
                Check(re.Bytes.SequenceEqual(s.Bytes) && re.Number == s.Number && re.Name == s.Name,
                      $"[{label}] generated source for {s.Name} re-parses to the same bytes", gen.Split('\r')[1].Trim('\n'));

                var prims = ShapeInterpreter.Decode(s.Bytes);
                int div = ShapeEncoder.SuggestDivisor(prims);
                var bytes = ShapeEncoder.Encode(prims, div, out var notes);
                var again = ShapeInterpreter.Decode(bytes);
                Check(SameGeometry(prims, again, 0.5 / div + 0.02), $"[{label}] {s.Name} decode → encode → decode keeps the geometry",
                      $"{prims.Count} elements, {s.Bytes.Length} → {bytes.Length} bytes, divisor {div}");
            }

            // --- line styles
            var xlin = XlinFile.Load(xlinBytes);
            Check(xlin.Styles.Count > 0, $"[{label}] xlin: {xlin.Styles.Count} line styles");
            Check(xlin.Save().SequenceEqual(xlinBytes), $"[{label}] xlin round trip is byte-identical", $"{xlinBytes.Length} bytes");

            int parsed = 0, shapes = 0, texts = 0;
            var shapeNames = new HashSet<string>(shp.Shapes.Select(s => s.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var st in xlin.Styles)
            {
                List<PatternElement> list;
                try { list = LinePattern.Parse(st.Pattern); }
                catch (FormatException ex) { Check(false, $"[{label}] pattern {st.Name} parses", ex.Message); continue; }
                parsed++;
                shapes += list.Count(e => e.Kind == PatternKind.Shape);
                texts += list.Count(e => e.Kind == PatternKind.Text);
                var formatted = LinePattern.Format(list);
                var back = LinePattern.Parse(formatted);
                bool same = back.Count == list.Count && back.Zip(list, (a, b) =>
                    a.Kind == b.Kind && Math.Abs(a.Length - b.Length) < 1e-9 && a.Content == b.Content && a.Source == b.Source &&
                    a.Params.Count == b.Params.Count && a.Params.Zip(b.Params, (p, q) => p.Key == q.Key && Math.Abs(p.Value - q.Value) < 1e-9).All(x => x)).All(x => x);
                if (!same) Check(false, $"[{label}] pattern {st.Name} format → parse is stable", $"{st.Pattern}  →  {formatted}");
                var errors = new List<string>(); var warnings = new List<string>();
                LinePattern.Check(list, n => shapeNames.Contains(n), errors, warnings);
                if (errors.Count > 0) Check(false, $"[{label}] pattern {st.Name} passes the rules", string.Join("; ", errors));
            }
            Check(parsed == xlin.Styles.Count, $"[{label}] all {parsed} patterns parse and re-format stably ({shapes} shape and {texts} text elements)");
        }

        private static bool SameGeometry(List<ShapePrimitive> a, List<ShapePrimitive> b, double tol)
        {
            var pa = Sample(a); var pb = Sample(b);
            return pa.All(p => pb.Any(q => p.DistanceTo(q) <= tol)) && pb.All(p => pa.Any(q => p.DistanceTo(q) <= tol));
        }

        private static List<Pt> Sample(List<ShapePrimitive> prims)
        {
            var polys = new List<Pt[]>();
            foreach (var p in prims) p.Tessellate(polys, 10);
            return polys.SelectMany(x => x).ToList();
        }

        private static void Synthetic()
        {
            // Encoder: lines, circle (code 10), arcs (code 12), long pen-up move, divide scale.
            var prims = new List<ShapePrimitive>
            {
                new LinePrim(new Pt(0, 0), new Pt(0, 1)),
                new LinePrim(new Pt(0, 0), new Pt(.75, -1.75)),
                ArcPrim.Circle(new Pt(2.5, 0), .75),
                new ArcPrim(new Pt(0, 0), 1.25, 0, Math.PI),
                new ArcPrim(new Pt(5, 0), 1, 0, 1.5 * Math.PI),
                new LinePrim(new Pt(-50, 0), new Pt(-50, 2.5)),
            };
            var bytes = ShapeEncoder.Encode(prims, 4, out var notes);
            Check(bytes[0] == 3 && bytes[1] == 4 && bytes[bytes.Length - 3] == 4 && bytes[bytes.Length - 1] == 0, "encoder wraps a fine grid in 3,n … 4,n,0");
            Check(Contains(bytes, new byte[] { 10, 3, 0 }), "encoder writes a whole-number circle as an octant arc (10,3,000)");
            Check(SameGeometry(prims, ShapeInterpreter.Decode(bytes), 0.13), "encoder output decodes to the drawn geometry", $"{bytes.Length} bytes");
            var def = new ShapeDefinition { Number = 136, Name = "TEST", Bytes = bytes };
            Check(!def.Validate().Any(), "encoded test shape is valid");
            Check(ShpFile.Parse(ShpFile.Generate(def)).Shapes[0].Bytes.SequenceEqual(bytes), "encoded test shape survives .shp text");

            // Three-point arc
            var arc = ArcPrim.FromThreePoints(new Pt(1, 0), new Pt(0, 1), new Pt(-1, 0));
            Check(arc != null && Math.Abs(arc.Radius - 1) < 1e-9 && Math.Abs(arc.Sweep - Math.PI) < 1e-9, "three-point arc: counter-clockwise half circle");
            arc = ArcPrim.FromThreePoints(new Pt(1, 0), new Pt(0, -1), new Pt(-1, 0));
            Check(arc != null && Math.Abs(arc.Sweep + Math.PI) < 1e-9, "three-point arc: clockwise half circle");

            // Pattern rules
            var errs = new List<string>(); var warns = new List<string>();
            LinePattern.Check(LinePattern.Parse("[BOX,ltypeshp.shx,s=.1],-.2"), n => true, errs, warns);
            Check(errs.Any(e => e.Contains("must start")), "a pattern cannot start with a shape");
            Check(LinePattern.Num(0.5) == ".5" && LinePattern.Num(-0.25) == "-.25" && LinePattern.Num(12) == "12" && LinePattern.Num(0) == "0", "numbers use the file's style (.5, -.25)");

            // New style is written in the file layout and reads back
            var x = XlinFile.Load(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<SECTIONS><EW_HEADER/><lineStyles>\r\n\t</lineStyles>\r\n</SECTIONS>\r\n"));
            var ls = new LineStyle { Name = "SWEETS_TEST", Pattern = ".5,-.2,[\"A&B\",STANDARD,S=.1],-.2", Ascii = "__ A&B __" };
            ls.Descriptions.Add(new KeyValuePair<string, string>("en", "Test <1>"));
            x.Styles.Add(ls);
            var back = XlinFile.Load(x.Save());
            Check(back.Styles.Count == 1 && back.Styles[0].SameContent(ls), "a new style with &, <, > and quotes reads back unchanged");

            // DXF
            var dxf = DxfReader.Parse(new[]
            {
                "0", "SECTION", "2", "ENTITIES",
                "0", "LINE", "8", "0", "10", "0", "20", "0", "11", "4", "21", "0",
                "0", "CIRCLE", "8", "0", "10", "2", "20", "0", "40", "1",
                "0", "ARC", "8", "0", "10", "0", "20", "0", "40", "2", "50", "0", "51", "90",
                "0", "LWPOLYLINE", "8", "0", "90", "3", "70", "0", "10", "0", "20", "0", "42", "1", "10", "2", "20", "0", "10", "2", "20", "2",
                "0", "TEXT", "8", "0", "1", "X",
                "0", "ENDSEC", "0", "EOF",
            });
            Check(dxf.Primitives.Count == 5 && dxf.Skipped.ContainsKey("TEXT"), "DXF: LINE, CIRCLE, ARC and a 2-segment LWPOLYLINE with a bulge; TEXT ignored",
                  $"{dxf.Primitives.Count} elements; {dxf.SkippedSummary}");
            Check(dxf.Primitives[3] is ArcPrim bul && Math.Abs(Math.Abs(bul.Sweep) - Math.PI) < 1e-9, "DXF: bulge 1 is a half circle");
        }

        private static bool Contains(byte[] hay, byte[] needle)
        {
            for (int i = 0; i + needle.Length <= hay.Length; i++)
                if (hay.Skip(i).Take(needle.Length).SequenceEqual(needle)) return true;
            return false;
        }
    }
}
