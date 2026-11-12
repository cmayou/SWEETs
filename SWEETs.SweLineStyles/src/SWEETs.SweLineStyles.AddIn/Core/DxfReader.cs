using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    /// <summary>
    /// Reads the 2D geometry of an ASCII DXF file's ENTITIES section: LINE, ARC, CIRCLE, LWPOLYLINE (with bulges)
    /// and POLYLINE/VERTEX. Z values, layers and other entity types are ignored (counted in <see cref="Skipped"/>).
    /// Block references (INSERT) are not exploded: explode them in the CAD program before exporting.
    /// </summary>
    public sealed class DxfReader
    {
        public List<ShapePrimitive> Primitives { get; } = new List<ShapePrimitive>();
        public Dictionary<string, int> Skipped { get; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static DxfReader Read(string path)
        {
            var head = new byte[22];
            using (var f = File.OpenRead(path)) f.Read(head, 0, head.Length);
            if (Encoding.ASCII.GetString(head).StartsWith("AutoCAD Binary DXF", StringComparison.Ordinal))
                throw new InvalidDataException("Binary DXF is not supported. Save the file as ASCII DXF.");
            return Parse(File.ReadAllLines(path, Encoding.Default));
        }

        public static DxfReader Parse(IList<string> lines)
        {
            var r = new DxfReader();
            var pairs = new List<(int code, string value)>();
            for (int i = 0; i + 1 < lines.Count; i += 2)
            {
                if (!int.TryParse(lines[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                    throw new InvalidDataException($"Line {i + 1}: expected a group code, found '{lines[i].Trim()}'.");
                pairs.Add((code, lines[i + 1].Trim()));
            }

            // Locate the ENTITIES section.
            int k = 0;
            while (k + 1 < pairs.Count && !(pairs[k].code == 0 && pairs[k].value == "SECTION" && pairs[k + 1].code == 2 && pairs[k + 1].value == "ENTITIES")) k++;
            if (k + 1 >= pairs.Count) throw new InvalidDataException("The DXF file has no ENTITIES section.");
            k += 2;

            // Group into entities (code 0 starts each one).
            var entities = new List<(string type, List<(int code, string value)> data)>();
            while (k < pairs.Count)
            {
                if (pairs[k].code != 0) { k++; continue; }
                var type = pairs[k].value;
                if (type == "ENDSEC" || type == "EOF") break;
                var data = new List<(int, string)>();
                k++;
                while (k < pairs.Count && pairs[k].code != 0) data.Add(pairs[k++]);
                entities.Add((type, data));
            }

            for (int e = 0; e < entities.Count; e++)
            {
                var (type, d) = entities[e];
                switch (type)
                {
                    case "LINE":
                        r.Add(new LinePrim(new Pt(D(d, 10), D(d, 20)), new Pt(D(d, 11), D(d, 21))));
                        break;
                    case "CIRCLE":
                        r.Add(ArcPrim.Circle(new Pt(D(d, 10), D(d, 20)), D(d, 40)));
                        break;
                    case "ARC":
                    {
                        double a0 = D(d, 50) * Math.PI / 180, a1 = D(d, 51) * Math.PI / 180;
                        double sweep = a1 - a0;
                        while (sweep <= 0) sweep += 2 * Math.PI;           // DXF arcs run counter-clockwise
                        r.Add(new ArcPrim(new Pt(D(d, 10), D(d, 20)), D(d, 40), a0, sweep));
                        break;
                    }
                    case "LWPOLYLINE":
                    {
                        var verts = new List<(Pt p, double bulge)>();
                        foreach (var (code, value) in d)
                        {
                            if (code == 10) verts.Add((new Pt(Num(value), 0), 0));
                            else if (code == 20 && verts.Count > 0) verts[verts.Count - 1] = (new Pt(verts[verts.Count - 1].p.X, Num(value)), verts[verts.Count - 1].bulge);
                            else if (code == 42 && verts.Count > 0) verts[verts.Count - 1] = (verts[verts.Count - 1].p, Num(value));
                        }
                        r.AddPolyline(verts, ((int)D(d, 70) & 1) == 1);
                        break;
                    }
                    case "POLYLINE":
                    {
                        bool closed = ((int)D(d, 70) & 1) == 1;
                        var verts = new List<(Pt p, double bulge)>();
                        while (e + 1 < entities.Count && entities[e + 1].type == "VERTEX")
                        {
                            e++;
                            var v = entities[e].data;
                            verts.Add((new Pt(D(v, 10), D(v, 20)), D(v, 42)));
                        }
                        if (e + 1 < entities.Count && entities[e + 1].type == "SEQEND") e++;
                        r.AddPolyline(verts, closed);
                        break;
                    }
                    default:
                        r.Skipped[type] = r.Skipped.TryGetValue(type, out int n) ? n + 1 : 1;
                        break;
                }
            }
            return r;
        }

        private void Add(ShapePrimitive p)
        {
            if (p is LinePrim l && l.P1.Near(l.P2)) return;
            if (p is ArcPrim a && a.Radius <= 0) return;
            Primitives.Add(p);
        }

        private void AddPolyline(List<(Pt p, double bulge)> verts, bool closed)
        {
            int n = verts.Count;
            for (int i = 0; i < (closed ? n : n - 1); i++)
            {
                var a = verts[i]; var b = verts[(i + 1) % n];
                ShapePrimitive seg = Math.Abs(a.bulge) > 1e-12 ? ArcPrim.FromChord(a.p, b.p, 4 * Math.Atan(a.bulge)) : null;
                Add(seg ?? new LinePrim(a.p, b.p));
            }
        }

        private static double D(List<(int code, string value)> d, int code)
        {
            foreach (var (c, v) in d) if (c == code) return Num(v);
            return 0;
        }

        private static double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

        public string SkippedSummary => Skipped.Count == 0 ? "" :
            "Ignored: " + string.Join(", ", Skipped.OrderByDescending(x => x.Value).Select(x => $"{x.Value} {x.Key}"));
    }
}
