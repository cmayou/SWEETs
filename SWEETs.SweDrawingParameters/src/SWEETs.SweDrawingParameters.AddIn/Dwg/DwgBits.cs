using System;
using System.IO;
using System.Text;

namespace SWEETs.SweDrawingParameters.AddIn.Dwg
{
    /// <summary>DWG bit-stream reader (R2007+ conventions), MSB-first within each byte.</summary>
    internal sealed class DwgBits
    {
        private readonly byte[] _b;
        public long Pos;

        public DwgBits(byte[] buffer, long bitPos = 0) { _b = buffer; Pos = bitPos; }

        public int Bit()
        {
            int v = (_b[Pos >> 3] >> (7 - (int)(Pos & 7))) & 1;
            Pos++;
            return v;
        }

        public int Bits(int n) { int v = 0; for (int i = 0; i < n; i++) v = (v << 1) | Bit(); return v; }
        public int RC() => Bits(8);
        public int RS() { int a = RC(); int b = RC(); return a | (b << 8); }
        public uint RL() { uint a = (uint)RS(); uint b = (uint)RS(); return a | (b << 16); }

        public double RD()
        {
            var bytes = new byte[8];
            for (int i = 0; i < 8; i++) bytes[i] = (byte)RC();
            return BitConverter.ToDouble(bytes, 0);
        }

        public bool B() => Bit() == 1;
        public int BB() => Bits(2);

        public int BS()
        {
            switch (BB())
            {
                case 0: return RS();
                case 1: return RC();
                case 2: return 0;
                default: return 256;
            }
        }

        public uint BL()
        {
            switch (BB())
            {
                case 0: return RL();
                case 1: return (uint)RC();
                case 2: return 0;
                default: throw new InvalidDataException($"Invalid BL code at bit {Pos}.");
            }
        }

        public ulong BLL()
        {
            int n = Bits(3);
            ulong v = 0;
            for (int i = 0; i < n; i++) v |= (ulong)RC() << (8 * i);
            return v;
        }

        public double BD()
        {
            switch (BB())
            {
                case 0: return RD();
                case 1: return 1.0;
                case 2: return 0.0;
                default: throw new InvalidDataException($"Invalid BD code at bit {Pos}.");
            }
        }

        /// <summary>Handle reference: code nibble + counter + counter bytes.</summary>
        public ulong H()
        {
            int x = RC();
            int n = x & 0x0F;
            ulong v = 0;
            for (int i = 0; i < n; i++) v = (v << 8) | (uint)RC();
            return v;
        }

        /// <summary>CMC color, R2004+ data-stream part (BS index, BL rgb, RC flags; names are in the string stream).</summary>
        public void CMC() { BS(); BL(); RC(); }

        /// <summary>Object type, R2010+.</summary>
        public int OT()
        {
            switch (BB())
            {
                case 0: return RC();
                case 1: return RC() + 0x1F0;
                default: return RS();
            }
        }

        /// <summary>R2007+ string-stream text (BS length + UTF-16LE).</summary>
        public string TU()
        {
            int n = BS();
            var bytes = new byte[n * 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)RC();
            return Encoding.Unicode.GetString(bytes);
        }

        // ---- writing in place (fixed-width fields only)

        public static void SetBits(byte[] buf, long bitPos, ulong value, int nbits)
        {
            for (int i = 0; i < nbits; i++)
            {
                int bit = (int)((value >> (nbits - 1 - i)) & 1);
                long p = bitPos + i;
                int mask = 0x80 >> (int)(p & 7);
                if (bit == 1) buf[p >> 3] = (byte)(buf[p >> 3] | mask);
                else buf[p >> 3] = (byte)(buf[p >> 3] & ~mask);
            }
        }

        public static void WriteRD(byte[] buf, long bitPos, double value)
        {
            var bytes = BitConverter.GetBytes(value);
            for (int i = 0; i < 8; i++) SetBits(buf, bitPos + 8 * i, bytes[i], 8);
        }

        public static void WriteB(byte[] buf, long bitPos, bool value) => SetBits(buf, bitPos, value ? 1UL : 0UL, 1);

        // ---- byte-oriented helpers for object/handle maps

        public static int ReadMS(byte[] buf, ref int pos)
        {
            int v = 0, shift = 0;
            while (true)
            {
                int w = buf[pos] | (buf[pos + 1] << 8);
                pos += 2;
                v |= (w & 0x7FFF) << shift;
                shift += 15;
                if ((w & 0x8000) == 0) return v;
            }
        }

        public static long ReadUMC(byte[] buf, ref int pos)
        {
            long v = 0; int shift = 0;
            while (true)
            {
                int b = buf[pos++];
                v |= (long)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0) return v;
            }
        }

        public static long ReadMC(byte[] buf, ref int pos)
        {
            long v = 0; int shift = 0;
            while (true)
            {
                int b = buf[pos++];
                if ((b & 0x80) != 0)
                {
                    v |= (long)(b & 0x7F) << shift;
                    shift += 7;
                }
                else
                {
                    bool negative = (b & 0x40) != 0;
                    v |= (long)(b & 0x3F) << shift;
                    return negative ? -v : v;
                }
            }
        }
    }

    internal static class DwgChecksum
    {
        /// <summary>Section page checksum (Adler-32 variant, ODA 4.3).</summary>
        public static uint Page(uint seed, byte[] data, int offset, int length)
        {
            uint s1 = seed & 0xFFFF, s2 = seed >> 16;
            int i = offset, end = offset + length;
            while (i < end)
            {
                int chunk = Math.Min(0x15B0, end - i);
                for (int k = 0; k < chunk; k++) { s1 += data[i++]; s2 += s1; }
                s1 %= 0xFFF1;
                s2 %= 0xFFF1;
            }
            return (s2 << 16) | (s1 & 0xFFFF);
        }

        public static uint Page(uint seed, byte[] data) => Page(seed, data, 0, data.Length);

        /// <summary>CRC-16 used for objects and the header section (reflected 0xA001, seed 0xC0C1).</summary>
        public static ushort Crc16(byte[] data, int offset, int length, ushort seed = 0xC0C1)
        {
            int crc = seed;
            for (int i = offset; i < offset + length; i++)
            {
                crc ^= data[i];
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
            }
            return (ushort)crc;
        }

        /// <summary>The pseudo-random byte stream ODA uses for the file header mask and page padding.</summary>
        public static byte[] MagicSequence(int n)
        {
            var output = new byte[n];
            uint r = 1;
            for (int i = 0; i < n; i++)
            {
                r = unchecked(r * 0x343FD + 0x269EC3);
                output[i] = (byte)(r >> 16);
            }
            return output;
        }
    }
}
