using System;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace SWEETs.SwePdfSchematic.AddIn.Interop
{
    /// <summary>
    /// Reads the preview image every DWG (R13 and later) stores in its header: the long at offset 0x0D points to an
    /// image block (sentinel, size, entry count, then code/start/size per entry; 2 = BMP DIB, 6 = PNG).
    /// No CAD library needed; returns null for anything unexpected.
    /// </summary>
    public static class DwgThumbnail
    {
        private static readonly byte[] Sentinel = { 0x1F, 0x25, 0x6D, 0x07, 0xD4, 0x36, 0x28, 0x28, 0x9D, 0x57, 0xCA, 0x3F, 0x9D, 0x44, 0x10, 0x2B };

        public static BitmapSource Read(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var br = new BinaryReader(fs))
                {
                    if (fs.Length < 0x40) return null;
                    var version = new string(br.ReadChars(6));
                    if (!version.StartsWith("AC10") && !version.StartsWith("AC1.")) return null;
                    fs.Position = 0x0D;
                    int seeker = br.ReadInt32();
                    if (seeker <= 0 || seeker + 21 >= fs.Length) return null;
                    fs.Position = seeker;
                    if (!br.ReadBytes(16).SequenceEqual(Sentinel)) return null;
                    br.ReadInt32(); // overall size
                    int count = br.ReadByte();
                    int bmpStart = 0, bmpSize = 0, pngStart = 0, pngSize = 0;
                    for (int i = 0; i < count; i++)
                    {
                        byte code = br.ReadByte(); int start = br.ReadInt32(); int size = br.ReadInt32();
                        if (code == 6) { pngStart = start; pngSize = size; }
                        else if (code == 2) { bmpStart = start; bmpSize = size; }
                    }
                    if (pngSize > 0 && pngStart + pngSize <= fs.Length) return Decode(Slice(fs, pngStart, pngSize));
                    if (bmpSize > 40 && bmpStart + bmpSize <= fs.Length) return Decode(WithFileHeader(Slice(fs, bmpStart, bmpSize)));
                    return null;
                }
            }
            catch { return null; }
        }

        private static byte[] Slice(FileStream fs, int start, int size)
        {
            var buf = new byte[size];
            fs.Position = start;
            int read = 0;
            while (read < size) { int n = fs.Read(buf, read, size - read); if (n <= 0) break; read += n; }
            return buf;
        }

        /// <summary>DWG stores a bare DIB; prepend BITMAPFILEHEADER so the BMP decoder accepts it.</summary>
        private static byte[] WithFileHeader(byte[] dib)
        {
            int headerSize = BitConverter.ToInt32(dib, 0);
            short bits = BitConverter.ToInt16(dib, 14);
            int used = dib.Length >= 36 ? BitConverter.ToInt32(dib, 32) : 0;
            int palette = bits <= 8 ? (used > 0 ? used : 1 << bits) * 4 : 0;
            var bmp = new byte[14 + dib.Length];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(14 + headerSize + palette).CopyTo(bmp, 10);
            Buffer.BlockCopy(dib, 0, bmp, 14, dib.Length);
            return bmp;
        }

        private static BitmapSource Decode(byte[] data)
        {
            var img = new BitmapImage();
            using (var ms = new MemoryStream(data))
            {
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
            }
            img.Freeze();
            return img;
        }
    }
}
