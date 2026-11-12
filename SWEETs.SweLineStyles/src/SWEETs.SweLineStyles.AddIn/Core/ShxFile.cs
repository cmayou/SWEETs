using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SWEETs.SweLineStyles.AddIn.Core
{
    /// <summary>
    /// Compiled shape file (*.shx), "-86 shapes 1.0" layout:
    /// <code>
    /// signature  "TraceCAD-86 shapes 1.0\r\n\x1A"   (AutoCAD writes "AutoCAD-86 shapes 1.0\r\n\x1A")
    /// uint16     first shape number, last shape number, shape count
    /// count ×    uint16 shape number, uint16 entry length (name + NUL + bytes)
    /// count ×    name, NUL, specification bytes
    /// "EOF"
    /// </code>
    /// Verified: compiling SOLIDWORKS Electrical 2026's Support\ltypeshp.shp with the signature read from its
    /// ltypeshp.shx reproduces that file byte for byte (131 bytes).
    /// </summary>
    public static class ShxFile
    {
        /// <summary>Signature of SOLIDWORKS Electrical's shape files (used when no existing .shx is available).</summary>
        public const string TraceCadSignature = "TraceCAD-86 shapes 1.0\r\n\x1A";

        public static byte[] Compile(IEnumerable<ShapeDefinition> shapes, string signature = TraceCadSignature)
        {
            var list = shapes.OrderBy(s => s.Number).ToList();
            if (list.Count == 0) throw new InvalidOperationException("A shape file needs at least one shape.");
            var dup = list.GroupBy(s => s.Number).FirstOrDefault(g => g.Count() > 1);
            if (dup != null) throw new InvalidOperationException($"Shape number {dup.Key} is used more than once.");

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(Encoding.ASCII.GetBytes(signature));
                w.Write((ushort)list[0].Number);
                w.Write((ushort)list[list.Count - 1].Number);
                w.Write((ushort)list.Count);
                foreach (var s in list)
                {
                    w.Write((ushort)s.Number);
                    w.Write((ushort)(Encoding.ASCII.GetByteCount(s.Name) + 1 + s.Bytes.Length));
                }
                foreach (var s in list)
                {
                    w.Write(Encoding.ASCII.GetBytes(s.Name));
                    w.Write((byte)0);
                    w.Write(s.Bytes);
                }
                w.Write(Encoding.ASCII.GetBytes("EOF"));
                w.Flush();
                return ms.ToArray();
            }
        }

        /// <summary>Reads a compiled shape file. <paramref name="signature"/> receives the header text up to and including 0x1A.</summary>
        public static List<ShapeDefinition> Read(byte[] data, out string signature)
        {
            int sub = Array.IndexOf(data, (byte)0x1A);
            if (sub < 0 || sub > 64) throw new InvalidDataException("Not a shape file (no header).");
            signature = Encoding.ASCII.GetString(data, 0, sub + 1);
            if (!signature.Contains("shapes")) throw new InvalidDataException($"Unsupported shape file header '{signature.Trim()}'.");

            int p = sub + 1;
            int U16() { int v = data[p] | data[p + 1] << 8; p += 2; return v; }
            U16(); U16();
            int count = U16();
            var index = new List<(int num, int len)>();
            for (int k = 0; k < count; k++) index.Add((U16(), U16()));

            var result = new List<ShapeDefinition>();
            foreach (var (num, len) in index)
            {
                if (p + len > data.Length) throw new InvalidDataException($"Shape {num} runs past the end of the file.");
                int nul = Array.IndexOf(data, (byte)0, p, len);
                if (nul < 0) throw new InvalidDataException($"Shape {num} has no name terminator.");
                var name = Encoding.ASCII.GetString(data, p, nul - p);
                var bytes = new byte[len - (nul - p) - 1];
                Array.Copy(data, nul + 1, bytes, 0, bytes.Length);
                result.Add(new ShapeDefinition { Number = num, Name = name, Bytes = bytes });
                p += len;
            }
            return result;
        }
    }
}
