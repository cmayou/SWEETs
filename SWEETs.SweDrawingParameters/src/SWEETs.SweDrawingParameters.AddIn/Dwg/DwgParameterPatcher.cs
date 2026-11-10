using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SWEETs.SweDrawingParameters.AddIn.Model;

namespace SWEETs.SweDrawingParameters.AddIn.Dwg
{
    /// <summary>
    /// Changes the grid/snap/grid-zone/ortho values of a DWG (AC1024/AC1027) in place, without re-writing the drawing:
    /// <list type="number">
    /// <item>decompress AcDb:AcDbObjects / AcDb:Header;</item>
    /// <item>overwrite the fixed-width fields (B and 2RD) of the "*Active" VPORT record and of the header variables
    /// ORTHOMODE, LIMMIN and LIMMAX, so no object changes size;</item>
    /// <item>recompute the object CRC / header CRC;</item>
    /// <item>recompress only the pages that changed, into the space they already occupy, and update their page
    /// checksums and the per-page sizes in the section info page.</item>
    /// </list>
    /// Every other byte of the file is left as SOLIDWORKS Electrical wrote it. If a recompressed page does not fit in
    /// its existing slot, nothing is written and a <see cref="DwgPatchException"/> explains why.
    /// </summary>
    public static class DwgParameterPatcher
    {
        private const int VPortType = 65;

        public sealed class PatchResult
        {
            public byte[] Bytes;
            public DrawingParameters Before;
            public List<int> RewrittenPages = new List<int>();
            /// <summary>Pages that needed more room; the rest of the file was moved up by whole 32-byte blocks.</summary>
            public List<int> GrownPages = new List<int>();
            public bool Changed => RewrittenPages.Count > 0;
        }

        // ------------------------------------------------------------------ public API

        /// <summary>Current values (null fields = could not be read).</summary>
        public static DrawingParameters ReadValues(byte[] file)
        {
            var dwg = new DwgR2004File(file);
            var objects = dwg.ReadSection("AcDb:AcDbObjects");
            var vport = FindActiveVPort(dwg, objects);
            var header = dwg.ReadSection("AcDb:Header");
            var h = LocateHeader(header, dwg.Version);
            return new DrawingParameters
            {
                GridOn = vport.GridOn, GridX = vport.GridX, GridY = vport.GridY,
                SnapOn = vport.SnapOn, SnapX = vport.SnapX, SnapY = vport.SnapY,
                OrthoOn = h.Ortho, ZoneMinX = h.LimMinX, ZoneMinY = h.LimMinY, ZoneMaxX = h.LimMaxX, ZoneMaxY = h.LimMaxY,
            };
        }

        public static PatchResult Patch(byte[] file, DrawingParameters request)
        {
            var dwg = new DwgR2004File(file);
            var result = new PatchResult { Bytes = (byte[])file.Clone() };
            var before = new DrawingParameters();
            var changed = new List<(DwgR2004File.Section section, byte[] buffer, byte[] original)>();

            // ---- AcDb:AcDbObjects: the *Active VPORT record
            var objSec = dwg.Sections["AcDb:AcDbObjects"];
            var objects = dwg.ReadSection("AcDb:AcDbObjects");
            var objOriginal = (byte[])objects.Clone();
            var vp = FindActiveVPort(dwg, objects);
            before.GridOn = vp.GridOn; before.GridX = vp.GridX; before.GridY = vp.GridY;
            before.SnapOn = vp.SnapOn; before.SnapX = vp.SnapX; before.SnapY = vp.SnapY;
            if (request.GridOn.HasValue) DwgBits.WriteB(objects, vp.PosGridMode, request.GridOn.Value);
            if (request.GridX.HasValue) DwgBits.WriteRD(objects, vp.PosGridUnit, request.GridX.Value);
            if (request.GridY.HasValue) DwgBits.WriteRD(objects, vp.PosGridUnit + 64, request.GridY.Value);
            if (request.SnapOn.HasValue) DwgBits.WriteB(objects, vp.PosSnapMode, request.SnapOn.Value);
            if (request.SnapX.HasValue) DwgBits.WriteRD(objects, vp.PosSnapUnit, request.SnapX.Value);
            if (request.SnapY.HasValue) DwgBits.WriteRD(objects, vp.PosSnapUnit + 64, request.SnapY.Value);
            WriteCrc(objects, vp.ObjectStart, vp.CrcPos);
            changed.Add((objSec, objects, objOriginal));

            // ---- AcDb:Header: ORTHOMODE, LIMMIN, LIMMAX
            var hdrSec = dwg.Sections["AcDb:Header"];
            var header = dwg.ReadSection("AcDb:Header");
            var hdrOriginal = (byte[])header.Clone();
            var h = LocateHeader(header, dwg.Version);
            before.OrthoOn = h.Ortho;
            before.ZoneMinX = h.LimMinX; before.ZoneMinY = h.LimMinY; before.ZoneMaxX = h.LimMaxX; before.ZoneMaxY = h.LimMaxY;
            if (request.OrthoOn.HasValue) DwgBits.WriteB(header, h.PosOrtho, request.OrthoOn.Value);
            if (request.ZoneMinX.HasValue) DwgBits.WriteRD(header, h.PosLimMin, request.ZoneMinX.Value);
            if (request.ZoneMinY.HasValue) DwgBits.WriteRD(header, h.PosLimMin + 64, request.ZoneMinY.Value);
            if (request.ZoneMaxX.HasValue) DwgBits.WriteRD(header, h.PosLimMax, request.ZoneMaxX.Value);
            if (request.ZoneMaxY.HasValue) DwgBits.WriteRD(header, h.PosLimMax + 64, request.ZoneMaxY.Value);
            WriteCrc(header, 16, h.CrcEnd);
            changed.Add((hdrSec, header, hdrOriginal));
            result.Before = before;

            // ---- recompress the pages whose content changed
            var payloads = new SortedDictionary<int, byte[]>();
            foreach (var (section, buffer, original) in changed)
            {
                foreach (var sp in section.Pages)
                {
                    int start = (int)sp.Offset;
                    int end = (int)Math.Min(sp.Offset + section.MaxDecompressedSize, section.Size);
                    if (end <= start || RangeEqual(buffer, original, start, end)) continue;
                    payloads[sp.Number] = CompressPage(dwg, section, sp, buffer, start, end);
                }
            }
            if (payloads.Count == 0) return result;   // values already as requested

            result.Bytes = Rebuild(dwg, payloads, out var grown);
            result.RewrittenPages.AddRange(payloads.Keys);
            result.GrownPages.AddRange(grown);
            return result;
        }

        // ------------------------------------------------------------------ VPORT

        private sealed class VPortInfo
        {
            public int ObjectStart, CrcPos;
            public long PosGridMode, PosGridUnit, PosSnapMode, PosSnapUnit;
            public bool GridOn, SnapOn;
            public double GridX, GridY, SnapX, SnapY;
            public string Name;
        }

        private static VPortInfo FindActiveVPort(DwgR2004File dwg, byte[] objects)
        {
            var handles = dwg.ReadSection("AcDb:Handles");
            var found = new List<VPortInfo>();
            foreach (var offset in ObjectOffsets(handles))
            {
                if (offset < 0 || offset >= objects.Length - 4) continue;
                int p = (int)offset;
                int size = DwgBits.ReadMS(objects, ref p);
                long handleBits = DwgBits.ReadUMC(objects, ref p);
                var r = new DwgBits(objects, (long)p * 8);
                if (r.OT() != VPortType) continue;

                long dataStart = (long)p * 8;
                long bitSize = (long)size * 8 - handleBits;
                var v = new VPortInfo { ObjectStart = (int)offset, CrcPos = p + size };

                r.H();                                     // own handle
                for (int n = r.BS(); n != 0; n = r.BS()) { r.H(); r.Pos += 8L * n; }   // EED
                r.BL();                                    // num reactors
                r.B();                                     // xdic missing
                if (string.CompareOrdinal(dwg.Version, "AC1027") >= 0) r.B();   // has_ds_data (R2013+)
                v.Name = ReadObjectName(objects, dataStart, bitSize);
                r.BS();                                    // is_xref_resolved (R2007+); name/xref live in other streams
                r.BD(); r.BD();                            // VIEWSIZE, view width
                r.RD(); r.RD();                            // VIEWCTR
                for (int i = 0; i < 6; i++) r.BD();        // target, direction
                for (int i = 0; i < 4; i++) r.BD();        // twist, lens length, front z, back z
                r.Bits(4);                                 // VIEWMODE
                r.RC();                                    // render mode
                r.B(); r.RC(); r.BD(); r.BD();             // default lights, lighting type, brightness, contrast
                r.CMC();                                   // ambient color
                for (int i = 0; i < 4; i++) r.RD();        // lower left, upper right
                r.B(); r.BS(); r.B(); r.BB();              // UCSFOLLOW, circle zoom, FASTZOOM, UCSICON
                v.PosGridMode = r.Pos; v.GridOn = r.B();
                v.PosGridUnit = r.Pos; v.GridX = r.RD(); v.GridY = r.RD();
                v.PosSnapMode = r.Pos; v.SnapOn = r.B();
                r.B(); r.BS(); r.BD(); r.RD(); r.RD();     // SNAPSTYLE, SNAPISOPAIR, SNAPANG, SNAPBASE
                v.PosSnapUnit = r.Pos; v.SnapX = r.RD(); v.SnapY = r.RD();
                if (v.PosSnapUnit + 128 > dataStart + bitSize) throw new DwgPatchException("VPORT record shorter than expected.");
                foreach (var d in new[] { v.GridX, v.GridY, v.SnapX, v.SnapY })
                    if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > 1e12)
                        throw new DwgPatchException("VPORT decoding produced implausible values; the file was not changed.");
                found.Add(v);
            }
            var active = found.Where(x => string.Equals(x.Name, "*Active", StringComparison.OrdinalIgnoreCase)).ToList();
            if (active.Count != 1)
                throw new DwgPatchException($"Expected one \"*Active\" viewport record, found {active.Count} (of {found.Count} VPORT records).");
            return active[0];
        }

        /// <summary>Absolute object offsets from AcDb:Handles (big-endian section sizes, UMC handle deltas, MC offset deltas).</summary>
        private static IEnumerable<long> ObjectOffsets(byte[] handles)
        {
            int pos = 0;
            while (pos + 2 <= handles.Length)
            {
                int size = (handles[pos] << 8) | handles[pos + 1];
                if (size <= 2) yield break;
                int start = pos;
                pos += 2;
                long offset = 0;
                while (pos - start < size)
                {
                    DwgBits.ReadUMC(handles, ref pos);
                    offset += DwgBits.ReadMC(handles, ref pos);
                    yield return offset;
                }
                pos += 2;   // section CRC
            }
        }

        /// <summary>R2007+ string stream: flag at bit (end-1), RS size before it, strings before that.</summary>
        private static string ReadObjectName(byte[] buf, long dataStart, long bitSize)
        {
            long end = dataStart + bitSize;
            var r = new DwgBits(buf, end - 1);
            if (!r.B()) return null;
            r.Pos = end - 1 - 16;
            int size = r.RS();
            long start;
            if ((size & 0x8000) != 0)
            {
                r.Pos = end - 1 - 32;
                int hi = r.RS();
                size = (size & 0x7FFF) | (hi << 15);
                start = end - 1 - 32 - size;
            }
            else start = end - 1 - 16 - size;
            try { return new DwgBits(buf, start).TU(); }
            catch { return null; }
        }

        // ------------------------------------------------------------------ header

        private sealed class HeaderInfo
        {
            public int CrcEnd;
            public long PosOrtho, PosLimMin, PosLimMax;
            public bool Ortho;
            public double LimMinX, LimMinY, LimMaxX, LimMaxY;
        }

        private static HeaderInfo LocateHeader(byte[] hb, string version)
        {
            uint size = BitConverter.ToUInt32(hb, 16);
            // R2010+ may or may not carry an extra RL (bitsize_hi); pick the layout whose CRC matches.
            var layouts = new List<(int dataOff, int crcEnd)>();
            foreach (bool hi in new[] { true, false })
            {
                long crcEnd = (hi ? 24 : 20) + (long)size;
                if (crcEnd + 2 > hb.Length) continue;
                if (DwgChecksum.Crc16(hb, 16, (int)crcEnd - 16) == BitConverter.ToUInt16(hb, (int)crcEnd))
                    layouts.Add((hi ? 28 : 24, (int)crcEnd));
            }
            if (layouts.Count != 1) throw new DwgPatchException("Header section layout not recognised (CRC check).");
            var info = new HeaderInfo { CrcEnd = layouts[0].crcEnd };

            var r = new DwgBits(hb, layouts[0].dataOff * 8L);
            if (string.CompareOrdinal(version, "AC1027") >= 0) r.BLL();   // REQUIREDVERSIONS (R2013+)
            for (int i = 0; i < 4; i++) r.BD();      // unit ratios
            r.BL(); r.BL();                          // unknown
            r.B(); r.B(); r.B();                     // DIMASO DIMSHO PLINEGEN
            info.PosOrtho = r.Pos; info.Ortho = r.B();
            for (int i = 0; i < 5; i++) r.B();       // REGENMODE FILLMODE QTEXTMODE PSLTSCALE LIMCHECK
            r.B();                                   // unknown (R2004+)
            for (int i = 0; i < 11; i++) r.B();      // USRTIMER .. PELLIPSE
            for (int i = 0; i < 8; i++) r.BS();      // PROXYGRAPHICS .. PDMODE
            for (int i = 0; i < 3; i++) r.BL();      // unknown (R2004+)
            for (int i = 0; i < 19; i++) r.BS();     // USERI1-5, SPLINESEGS .. TEXTQLTY
            for (int i = 0; i < 21; i++) r.BD();     // LTSCALE .. CELTSCALE
            for (int i = 0; i < 2; i++) { r.BL(); r.BL(); }   // TDUCREATE TDUUPDATE
            for (int i = 0; i < 3; i++) r.BL();      // unknown (R2004+)
            for (int i = 0; i < 2; i++) { r.BL(); r.BL(); }   // TDINDWG TDUSRTIMER
            r.CMC();                                 // CECOLOR
            r.H();                                   // HANDSEED
            r.BD();                                  // PSVPSCALE
            for (int i = 0; i < 9; i++) r.BD();      // PINSBASE PEXTMIN PEXTMAX
            for (int i = 0; i < 4; i++) r.RD();      // PLIMMIN PLIMMAX
            r.BD();                                  // PELEVATION
            for (int i = 0; i < 9; i++) r.BD();      // PUCSORG PUCSXDIR PUCSYDIR
            r.BS();                                  // PUCSORTHOVIEW
            for (int i = 0; i < 18; i++) r.BD();     // PUCSORGTOP .. PUCSORGBACK
            for (int i = 0; i < 9; i++) r.BD();      // INSBASE EXTMIN EXTMAX
            info.PosLimMin = r.Pos; info.LimMinX = r.RD(); info.LimMinY = r.RD();
            info.PosLimMax = r.Pos; info.LimMaxX = r.RD(); info.LimMaxY = r.RD();
            if (info.PosLimMax + 128 > (long)info.CrcEnd * 8) throw new DwgPatchException("Header section shorter than expected.");
            foreach (var v in new[] { info.LimMinX, info.LimMinY, info.LimMaxX, info.LimMaxY })
                if (double.IsNaN(v) || double.IsInfinity(v) || Math.Abs(v) > 1e12)
                    throw new DwgPatchException("Header decoding produced implausible limits; the file was not changed.");
            return info;
        }

        // ------------------------------------------------------------------ page rewriting

        private static void WriteCrc(byte[] buf, int from, int crcPos)
        {
            ushort crc = DwgChecksum.Crc16(buf, from, crcPos - from);
            buf[crcPos] = (byte)(crc & 0xFF);
            buf[crcPos + 1] = (byte)(crc >> 8);
        }

        private static bool RangeEqual(byte[] a, byte[] b, int start, int end)
        {
            for (int i = start; i < end; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>New compressed payload for one data page (LZ77 + ODA's two trailing zero bytes), round-trip checked.</summary>
        private static byte[] CompressPage(DwgR2004File dwg, DwgR2004File.Section section, DwgR2004File.SectionPage sp,
                                           byte[] sectionBuffer, int start, int end)
        {
            if (section.Compressed != 2) throw new DwgPatchException($"{section.Name} is not compressed; not supported.");
            var page = dwg.ReadDataPage(section, sp, out int decodedLength);
            Buffer.BlockCopy(sectionBuffer, start, page, 0, end - start);
            var data = WithTrailingZeros(Lz77Ac18.Compress(page, decodedLength));
            var check = new byte[section.MaxDecompressedSize];
            int n = Lz77Ac18.Decompress(data, 0, data.Length, check);
            if (n != decodedLength || !RangeEqual(check, page, 0, decodedLength))
                throw new DwgPatchException($"Compression check failed for page {sp.Number}; the file was not changed.");
            return data;
        }

        private static byte[] WithTrailingZeros(byte[] compressed)
        {
            var data = new byte[compressed.Length + 2];
            Buffer.BlockCopy(compressed, 0, data, 0, compressed.Length);
            return data;
        }

        private static readonly byte[] PageMapGap =
            { 0x3B, 0x0E, 0x63, 0x41, 0, 0, 0, 0, 0, 0, 0, 0, 0x02, 0, 0, 0, 0, 0, 0, 0 };

        private static long RoundUp32(long n) => (n + 31) & ~31L;

        /// <summary>
        /// Writes the file again page by page, in page-map order. Unchanged pages are copied byte for byte; a changed
        /// page keeps its slot when the new data fits, otherwise its slot grows by whole 32-byte blocks and every later
        /// page moves up (their headers are re-encrypted for the new address). The section info, the page map, the
        /// file header (and its copy at the end of the file) are updated to match.
        /// </summary>
        private static byte[] Rebuild(DwgR2004File dwg, SortedDictionary<int, byte[]> payloads, out List<int> grown)
        {
            grown = new List<int>();
            var src = dwg.Data;
            var output = new MemoryStream(src.Length + 4096);
            output.Write(src, 0, 0x100);
            var moved = new Dictionary<long, long>();
            var entries = new List<(int number, long size, byte[] gap)>();
            long pageMapAt = -1, pageMapSize = 0;

            // Section info with the new compressed sizes.
            var infoPage = dwg.Pages[dwg.SectionInfoId];
            int ia = checked((int)infoPage.Address);
            uint infoType = BitConverter.ToUInt32(src, ia), infoDecompressed = BitConverter.ToUInt32(src, ia + 4);
            uint infoOldCompressed = BitConverter.ToUInt32(src, ia + 8), infoCompression = BitConverter.ToUInt32(src, ia + 12);
            var info = dwg.ReadSystemPage(infoPage.Address, DwgR2004File.SectionInfoType);
            int numDesc = BitConverter.ToInt32(info, 0), p = 20, touched = 0;
            for (int i = 0; i < numDesc; i++)
            {
                int pages = BitConverter.ToInt32(info, p + 8);
                p += 32 + 64;
                for (int j = 0; j < pages; j++)
                {
                    if (payloads.TryGetValue(BitConverter.ToInt32(info, p), out var payload)) { WriteU32(info, p + 4, (uint)payload.Length); touched++; }
                    p += 16;
                }
            }
            if (touched != payloads.Count) throw new DwgPatchException("Section info does not list every rewritten page; the file was not changed.");
            var infoBytes = SystemPage(infoType, infoDecompressed, infoCompression, WithTrailingZeros(Lz77Ac18.Compress(info, info.Length)));

            for (int index = 0; index < dwg.PageList.Count; index++)
            {
                var page = dwg.PageList[index];
                long here = output.Length;
                moved[page.Address] = here;
                int a = checked((int)page.Address);

                if (page.Number == dwg.SectionMapId)
                {
                    if (index != dwg.PageList.Count - 1) throw new DwgPatchException("The page map is not the last page; not supported.");
                    pageMapAt = here; pageMapSize = page.Size;
                    entries.Add((page.Number, page.Size, null));
                    break;
                }
                if (page.Number < 0)
                {
                    output.Write(src, a, (int)page.Size);
                    entries.Add((page.Number, page.Size, page.Gap));
                    continue;
                }
                if (page.Number == dwg.SectionInfoId)
                {
                    byte[] block;
                    if (infoBytes.Length <= page.Size)
                    {
                        block = new byte[page.Size];
                        Buffer.BlockCopy(src, a, block, 0, (int)Math.Min(page.Size, src.Length - a));
                        Buffer.BlockCopy(infoBytes, 0, block, 0, infoBytes.Length);
                        long oldEnd = Math.Min(page.Size, 20 + infoOldCompressed);
                        for (long k = infoBytes.Length; k < oldEnd; k++) block[k] = 0;
                    }
                    else
                    {
                        block = new byte[RoundUp32(infoBytes.Length)];
                        Buffer.BlockCopy(infoBytes, 0, block, 0, infoBytes.Length);
                    }
                    output.Write(block, 0, block.Length);
                    entries.Add((page.Number, block.Length, null));
                    continue;
                }

                uint oldMask = 0x4164536B ^ (uint)page.Address;
                var hv = new uint[8];
                for (int k = 0; k < 8; k++) hv[k] = BitConverter.ToUInt32(src, a + 4 * k) ^ oldMask;
                if (hv[0] != DwgR2004File.DataPageType)
                {
                    if (here != page.Address) throw new DwgPatchException($"Page {page.Number} is not a data page and would move; not supported.");
                    output.Write(src, a, (int)page.Size);
                    entries.Add((page.Number, page.Size, null));
                    continue;
                }

                long alloc = page.Size;
                byte[] body;
                if (payloads.TryGetValue(page.Number, out var data))
                {
                    alloc = Math.Max(page.Size, RoundUp32(32 + data.Length));
                    if (alloc != page.Size) grown.Add(page.Number);
                    uint dataCrc = DwgChecksum.Page(0, data);
                    hv = new uint[] { hv[0], hv[1], (uint)data.Length, (uint)alloc, hv[4], hv[5], 0, dataCrc };
                    hv[6] = DwgChecksum.Page(dataCrc, ToBytes(hv));
                    body = new byte[alloc - 32];
                    Buffer.BlockCopy(data, 0, body, 0, data.Length);
                    var pad = DwgChecksum.MagicSequence((int)(alloc - 32 - data.Length));
                    Buffer.BlockCopy(pad, 0, body, data.Length, pad.Length);
                }
                else
                {
                    body = new byte[alloc - 32];
                    Buffer.BlockCopy(src, a + 32, body, 0, (int)Math.Min(alloc - 32, src.Length - a - 32));
                }
                uint newMask = 0x4164536B ^ (uint)here;
                var header = new byte[32];
                for (int k = 0; k < 8; k++) WriteU32(header, 4 * k, hv[k] ^ newMask);
                output.Write(header, 0, 32);
                output.Write(body, 0, body.Length);
                entries.Add((page.Number, alloc, null));
            }
            if (pageMapAt < 0) throw new DwgPatchException("Page map not found in the page list.");

            // Page map with the new slot sizes.
            int pa = checked((int)dwg.Pages[dwg.SectionMapId].Address);
            uint mapType = BitConverter.ToUInt32(src, pa), mapDecompressed = BitConverter.ToUInt32(src, pa + 4);
            uint mapCompression = BitConverter.ToUInt32(src, pa + 12);
            var map = new MemoryStream();
            foreach (var (number, size, gap) in entries)
            {
                map.Write(BitConverter.GetBytes(number), 0, 4);
                map.Write(BitConverter.GetBytes((uint)size), 0, 4);
                if (gap != null) map.Write(gap, 0, 16);
            }
            var mapPlain = map.ToArray();
            if (mapPlain.Length != mapDecompressed) throw new DwgPatchException("Page map size changed; the file was not changed.");
            var mapBytes = SystemPage(mapType, mapDecompressed, mapCompression, WithTrailingZeros(Lz77Ac18.Compress(mapPlain, mapPlain.Length)));
            output.Write(mapBytes, 0, mapBytes.Length);
            output.Write(PageMapGap, 0, PageMapGap.Length);
            long secondHeader = output.Length;

            // File header: page map address, end of the last page, second header address, CRC32; then encrypt.
            var fh = (byte[])dwg.FileHeader.Clone();
            WriteU64(fh, 0x54, (ulong)(pageMapAt - 0x100));
            WriteU64(fh, 0x2C, (ulong)(pageMapAt + pageMapSize - 0x100));
            WriteU64(fh, 0x34, (ulong)secondHeader);
            WriteU32(fh, 0x68, 0);
            WriteU32(fh, 0x68, Crc32(fh));
            var mask = DwgChecksum.MagicSequence(0x6C);
            var encrypted = new byte[0x6C];
            for (int k = 0; k < 0x6C; k++) encrypted[k] = (byte)(fh[k] ^ mask[k]);
            output.Write(encrypted, 0, encrypted.Length);
            var result = output.ToArray();
            Buffer.BlockCopy(encrypted, 0, result, 0x80, encrypted.Length);

            // Absolute addresses kept in the fixed header (thumbnail, summary info, VBA) must not have moved.
            foreach (int at in new[] { 0x0D, 0x20, 0x24 })
            {
                uint v = BitConverter.ToUInt32(src, at);
                if (v == 0) continue;
                long owner = -1;
                foreach (var addr in moved.Keys) if (addr <= v && addr > owner) owner = addr;
                if (owner < 0 || moved[owner] != owner) throw new DwgPatchException($"The address at 0x{at:X} would move; not supported.");
            }
            return result;
        }

        private static byte[] SystemPage(uint type, uint decompressedSize, uint compression, byte[] data)
        {
            var header = new byte[20];
            WriteU32(header, 0, type); WriteU32(header, 4, decompressedSize); WriteU32(header, 8, (uint)data.Length);
            WriteU32(header, 12, compression); WriteU32(header, 16, 0);
            WriteU32(header, 16, DwgChecksum.Page(DwgChecksum.Page(0, header), data));
            var page = new byte[20 + data.Length];
            Buffer.BlockCopy(header, 0, page, 0, 20);
            Buffer.BlockCopy(data, 0, page, 20, data.Length);
            return page;
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
            return ~crc;
        }

        private static void WriteU64(byte[] b, int at, ulong v)
        {
            WriteU32(b, at, (uint)v);
            WriteU32(b, at + 4, (uint)(v >> 32));
        }

        private static byte[] ToBytes(uint[] values)
        {
            var b = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) WriteU32(b, 4 * i, values[i]);
            return b;
        }

        private static void WriteU32(byte[] b, int at, uint v)
        {
            b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
        }
    }
}
