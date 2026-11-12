using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SWEETs.SweDrawingParameters.AddIn.Dwg
{
    /// <summary>
    /// Read access to the page/section container of an R2004-family DWG file (here AC1024 and AC1027),
    /// as described in the ODA "Open Design Specification for .dwg files" (sections 4.1-4.7). SOLIDWORKS Electrical
    /// drawings (.ewg) are AC1024/AC1027 files.
    /// </summary>
    internal sealed class DwgR2004File
    {
        public const uint DataPageType = 0x4163043B;
        public const uint PageMapType = 0x41630E3B;
        public const uint SectionInfoType = 0x4163003B;

        public sealed class Page { public int Number; public long Size; public long Address; public byte[] Gap; }

        public sealed class SectionPage { public int Number; public uint DataSize; public long Offset; }

        public sealed class Section
        {
            public string Name;
            public long Size;
            public int MaxDecompressedSize;
            public int Compressed;   // 1 = no, 2 = yes
            public List<SectionPage> Pages = new List<SectionPage>();
        }

        public sealed class PageHeader
        {
            public uint PageType, SectionType, DataSize, PageSize, Offset, Unknown, HeaderCrc, DataCrc;
        }

        public byte[] Data { get; }
        public string Version { get; }
        public long SectionMapAddress { get; }
        public int SectionInfoId { get; }
        public int SectionMapId { get; }
        /// <summary>Decrypted 0x6C-byte R2004 file header.</summary>
        public byte[] FileHeader { get; }
        public Dictionary<int, Page> Pages { get; } = new Dictionary<int, Page>();
        /// <summary>All page-map entries in file order (gaps included).</summary>
        public List<Page> PageList { get; } = new List<Page>();
        public Dictionary<string, Section> Sections { get; } = new Dictionary<string, Section>();

        public DwgR2004File(byte[] data)
        {
            Data = data;
            Version = Encoding.ASCII.GetString(data, 0, 6);
            // Only the versions this patcher was validated on (SOLIDWORKS Electrical writes AC1027).
            if (Version != "AC1024" && Version != "AC1027")
                throw new DwgPatchException($"Unsupported DWG version {Version} (supported: AC1024 = 2010, AC1027 = 2013).");

            // Encrypted R2004 file header at 0x80 (0x6C bytes).
            var mask = DwgChecksum.MagicSequence(0x6C);
            var fh = new byte[0x6C];
            for (int i = 0; i < fh.Length; i++) fh[i] = (byte)(data[0x80 + i] ^ mask[i]);
            if (Encoding.ASCII.GetString(fh, 0, 11) != "AcFssFcAJMB") throw new DwgPatchException("File header could not be decrypted.");
            FileHeader = fh;
            SectionInfoId = BitConverter.ToInt32(fh, 0x5C);
            SectionMapId = BitConverter.ToInt32(fh, 0x50);
            SectionMapAddress = (long)BitConverter.ToUInt64(fh, 0x54) + 0x100;

            // Section page map.
            var map = ReadSystemPage(SectionMapAddress, PageMapType);
            long address = 0x100;
            int p = 0;
            while (p + 8 <= map.Length)
            {
                int number = BitConverter.ToInt32(map, p);
                long size = BitConverter.ToUInt32(map, p + 4);
                p += 8;
                var page = new Page { Number = number, Size = size, Address = address };
                if (number < 0)
                {
                    page.Gap = new byte[16];   // gap entry: 4 extra RLs
                    Buffer.BlockCopy(map, p, page.Gap, 0, 16);
                    p += 16;
                }
                else Pages[number] = page;
                PageList.Add(page);
                address += size;
            }

            // Section info (data section map).
            if (!Pages.TryGetValue(SectionInfoId, out var infoPage)) throw new DwgPatchException("Section info page not found.");
            var info = ReadSystemPage(infoPage.Address, SectionInfoType);
            int numDesc = BitConverter.ToInt32(info, 0);
            p = 20;
            for (int i = 0; i < numDesc; i++)
            {
                var s = new Section
                {
                    Size = (long)BitConverter.ToUInt64(info, p),
                    MaxDecompressedSize = BitConverter.ToInt32(info, p + 12),
                    Compressed = BitConverter.ToInt32(info, p + 20),
                };
                int pageCount = BitConverter.ToInt32(info, p + 8);
                p += 32;
                s.Name = Encoding.ASCII.GetString(info, p, 64).TrimEnd('\0');
                int nul = s.Name.IndexOf('\0');
                if (nul >= 0) s.Name = s.Name.Substring(0, nul);
                p += 64;
                for (int j = 0; j < pageCount; j++)
                {
                    s.Pages.Add(new SectionPage
                    {
                        Number = BitConverter.ToInt32(info, p),
                        DataSize = BitConverter.ToUInt32(info, p + 4),
                        Offset = (long)BitConverter.ToUInt64(info, p + 8),
                    });
                    p += 16;
                }
                Sections[s.Name] = s;
            }
        }

        /// <summary>Reads and decompresses a system page (page map or section info): 20-byte plain header + data.</summary>
        public byte[] ReadSystemPage(long address, uint expectedType)
        {
            int a = checked((int)address);
            uint type = BitConverter.ToUInt32(Data, a);
            if (type != expectedType) throw new DwgPatchException($"Unexpected system page type 0x{type:X8} at 0x{a:X}.");
            int decompressed = BitConverter.ToInt32(Data, a + 4);
            int compressed = BitConverter.ToInt32(Data, a + 8);
            var output = new byte[decompressed];
            int n = Lz77Ac18.Decompress(Data, a + 20, a + 20 + compressed, output);
            if (n != decompressed) throw new DwgPatchException($"System page at 0x{a:X} decompressed to {n} of {decompressed} bytes.");
            return output;
        }

        public PageHeader ReadPageHeader(int pageNumber, out long address)
        {
            if (!Pages.TryGetValue(pageNumber, out var page)) throw new DwgPatchException($"Page {pageNumber} not in the page map.");
            address = page.Address;
            int a = checked((int)address);
            uint mask = 0x4164536B ^ (uint)address;
            uint F(int i) => BitConverter.ToUInt32(Data, a + 4 * i) ^ mask;
            var h = new PageHeader
            {
                PageType = F(0), SectionType = F(1), DataSize = F(2), PageSize = F(3),
                Offset = F(4), Unknown = F(5), HeaderCrc = F(6), DataCrc = F(7),
            };
            if (h.PageType != DataPageType) throw new DwgPatchException($"Page {pageNumber} is not a data page.");
            return h;
        }

        /// <summary>Decompressed content of one data page, and the number of bytes it encodes.</summary>
        public byte[] ReadDataPage(Section section, SectionPage sp, out int decodedLength)
        {
            var h = ReadPageHeader(sp.Number, out long address);
            var buffer = new byte[section.MaxDecompressedSize];
            int a = checked((int)address);
            if (section.Compressed == 2)
                decodedLength = Lz77Ac18.Decompress(Data, a + 32, a + 32 + (int)h.DataSize, buffer);
            else
            {
                decodedLength = (int)Math.Min(h.DataSize, (uint)buffer.Length);
                Buffer.BlockCopy(Data, a + 32, buffer, 0, decodedLength);
            }
            return buffer;
        }

        /// <summary>The whole decompressed section (pages placed at their offsets).</summary>
        public byte[] ReadSection(string name)
        {
            if (!Sections.TryGetValue(name, out var s)) throw new DwgPatchException($"Section {name} not found.");
            var output = new byte[s.Size];
            foreach (var sp in s.Pages)
            {
                var page = ReadDataPage(s, sp, out _);
                long count = Math.Min(s.MaxDecompressedSize, s.Size - sp.Offset);
                if (count > 0) Buffer.BlockCopy(page, 0, output, (int)sp.Offset, (int)count);
            }
            return output;
        }
    }

    public sealed class DwgPatchException : Exception
    {
        public DwgPatchException(string message) : base(message) { }
    }
}
