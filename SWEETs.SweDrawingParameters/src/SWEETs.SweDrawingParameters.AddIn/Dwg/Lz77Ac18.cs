using System;
using System.IO;

namespace SWEETs.SweDrawingParameters.AddIn.Dwg
{
    /// <summary>
    /// The LZ77 variant used by DWG R2004+ section pages (ODA "Open Design Specification for .dwg files", 4.7).
    /// <see cref="Compress"/> is a port of the hash-based compressor used by ACadSharp and LibreDWG. On SOLIDWORKS
    /// Electrical drawings its output, followed by two zero bytes, is byte-identical to the pages SWE writes.
    /// </summary>
    internal static class Lz77Ac18
    {
        /// <summary>Decompresses src[pos..end) into dst; returns the number of bytes produced.</summary>
        public static int Decompress(byte[] src, int pos, int end, byte[] dst)
        {
            int sp = pos, dp = 0;
            int op1 = src[sp++];
            if ((op1 & 0xF0) == 0)
            {
                int lit = LiteralLength(src, ref sp, op1);
                Buffer.BlockCopy(src, sp, dst, dp, lit); sp += lit; dp += lit;
                op1 = src[sp++];
            }
            while (sp < end && dp < dst.Length && op1 != 0x11)
            {
                int count, offset;
                if (op1 < 0x10 || op1 >= 0x40)
                {
                    count = (op1 >> 4) - 1;
                    int op2 = src[sp++];
                    offset = (((op1 >> 2) & 3) | (op2 << 2)) + 1;
                }
                else if (op1 < 0x20)
                {
                    count = CompressedBytes(src, ref sp, op1, 7);
                    offset = (op1 & 8) << 11;
                    int b1 = src[sp++], b2 = src[sp++];
                    offset |= b1 >> 2;
                    offset |= b2 << 6;
                    offset += 0x4000;
                    op1 = b1;
                }
                else
                {
                    count = CompressedBytes(src, ref sp, op1, 0x1F);
                    int b1 = src[sp++], b2 = src[sp++];
                    offset = (b1 >> 2) | (b2 << 6);
                    offset += 1;
                    op1 = b1;
                }
                if (dp - offset < 0 || dp + count > dst.Length) throw new InvalidDataException("Invalid LZ77 back-reference.");
                for (int k = 0; k < count; k++) dst[dp + k] = dst[dp + k - offset];
                dp += count;

                int lit = op1 & 3;
                if (lit == 0)
                {
                    op1 = src[sp++];
                    if ((op1 & 0xF0) == 0) lit = LiteralLength(src, ref sp, op1);
                }
                if (lit > 0)
                {
                    if (dp + lit > dst.Length) throw new InvalidDataException("Invalid LZ77 literal run.");
                    Buffer.BlockCopy(src, sp, dst, dp, lit); sp += lit; dp += lit;
                    op1 = src[sp++];
                }
            }
            return dp;
        }

        private static int LiteralLength(byte[] src, ref int sp, int op)
        {
            int n = op & 0x0F;
            if (n == 0)
            {
                int b;
                while ((b = src[sp++]) == 0) n += 0xFF;
                n += 0x0F + b;
            }
            return n + 3;
        }

        private static int CompressedBytes(byte[] src, ref int sp, int op, int bits)
        {
            int n = op & bits;
            if (n == 0)
            {
                int b;
                while ((b = src[sp++]) == 0) n += 0xFF;
                n += b + bits;
            }
            return n + 2;
        }

        // ------------------------------------------------------------------ compression

        /// <summary>Compresses src[0..n) and appends the 0x11 end marker (caller adds ODA's two trailing zero bytes).</summary>
        public static byte[] Compress(byte[] src, int n)
        {
            var output = new MemoryStream(n + 32);
            if (n <= 0x18)
            {
                WriteLiterals(output, src, 0, n);
                output.WriteByte(0x11);
                return output.ToArray();
            }
            var hash = new int[0x8000];
            for (int i = 0; i < hash.Length; i++) hash[i] = -1;
            int current = 0, pos = 4, prevLen = 0, prevDist = 0;
            while (pos < n - 0x13)
            {
                int len = FindMatch(src, n, pos, hash, out int dist);
                if (len < 3) { pos++; continue; }
                int lits = pos - current;
                if (prevLen > 0) WriteMatch(output, prevDist, prevLen, lits);
                WriteLiterals(output, src, current, lits);
                pos += len;
                current = pos;
                prevLen = len;
                prevDist = dist;
            }
            int tail = n - current;
            if (prevLen > 0) WriteMatch(output, prevDist, prevLen, tail);
            WriteLiterals(output, src, current, tail);
            output.WriteByte(0x11);
            return output.ToArray();
        }

        private static int FindMatch(byte[] src, int n, int pos, int[] hash, out int dist)
        {
            dist = 0;
            if (pos + 3 >= n) return 0;
            uint v1 = (uint)src[pos + 3] << 6;
            uint v2 = v1 ^ src[pos + 2];
            uint v3 = (v2 << 5) ^ src[pos + 1];
            uint v4 = (v3 << 5) ^ src[pos];
            int idx = (int)((v4 + (v4 >> 5)) & 0x7FFF);
            int prev = hash[idx];
            int d = prev >= 0 ? pos - prev : 0;
            int len = 0;
            if (prev >= 0 && d <= 0xBFFF)
            {
                if (d > 0x400 && src[pos + 3] != src[prev + 3])
                {
                    idx = (idx & 0x7FF) ^ 0x401F;
                    prev = hash[idx];
                    if (prev >= 0) d = pos - prev;
                    if (prev < 0 || d > 0xBFFF || (d > 0x400 && src[pos + 3] != src[prev + 3]))
                    {
                        hash[idx] = pos;
                        return 0;
                    }
                }
                if (src[pos] == src[prev] && src[pos + 1] == src[prev + 1] && src[pos + 2] == src[prev + 2])
                {
                    int p = prev + 3, c = pos + 3;
                    len = 3;
                    while (c < n && src[p] == src[c]) { p++; c++; len++; }
                }
            }
            hash[idx] = pos;
            if (len < 3) return 0;
            dist = d;
            return len;
        }

        private static void WriteLongOffset(Stream s, int offset)
        {
            while (offset > 0xFF) { s.WriteByte(0); offset -= 0xFF; }
            s.WriteByte((byte)offset);
        }

        private static void WriteLength(Stream s, int u1, int match, int u2)
        {
            if (u2 < match)
            {
                s.WriteByte((byte)(u1 & 0xFF));
                WriteLongOffset(s, match - u2);
            }
            else s.WriteByte((byte)((u1 | (match - 2)) & 0xFF));
        }

        private static void WriteLiterals(Stream s, byte[] buf, int start, int n)
        {
            if (n == 0) return;
            if (n > 3) WriteLength(s, 0, n - 1, 0x11);
            s.Write(buf, start, n);
        }

        private static void WriteMatch(Stream s, int dist, int len, int lits)
        {
            int b1, b2;
            if (len < 0x0F && dist <= 0x400)
            {
                int d = dist - 1;
                b1 = ((len + 1) << 4) | ((d & 3) << 2);
                b2 = d >> 2;
            }
            else if (dist <= 0x4000)
            {
                int d = dist - 1;
                WriteLength(s, 0x20, len, 0x21);
                b1 = (d & 0xFF) << 2;
                b2 = d >> 6;
            }
            else
            {
                int d = dist - 0x4000;
                WriteLength(s, ((d >> 11) & 8) | 0x10, len, 9);
                b1 = (d & 0xFF) << 2;
                b2 = d >> 6;
            }
            if (lits < 4) b1 |= lits;
            s.WriteByte((byte)(b1 & 0xFF));
            s.WriteByte((byte)(b2 & 0xFF));
        }
    }
}
