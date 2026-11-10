using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace DataSheetMfgPartsImport.Extraction
{
    internal static class ImageUtil
    {

        public static byte[] PrepareImage(string path, out string mediaType)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".webp")
            {
                mediaType = "image/webp";
                return File.ReadAllBytes(path);
            }

            Image img;
            try { img = Image.FromFile(path); }
            catch (Exception) { throw new ExtractionException("This file could not be opened as an image: " + Path.GetFileName(path)); }

            using (img)
            {
                ApplyExifOrientation(img);
                double scale = Math.Min(1.0, 2000.0 / Math.Max(img.Width, img.Height));
                int w = Math.Max(1, (int)Math.Round(img.Width * scale));
                int h = Math.Max(1, (int)Math.Round(img.Height * scale));
                using (var bmp = new Bitmap(w, h))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.White);
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.DrawImage(img, 0, 0, w, h);
                    }
                    var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
                    using (var prms = new EncoderParameters(1))
                    using (var ms = new MemoryStream())
                    {
                        prms.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
                        bmp.Save(ms, codec, prms);
                        mediaType = "image/jpeg";
                        return ms.ToArray();
                    }
                }
            }
        }

        static void ApplyExifOrientation(Image img)
        {
            const int OrientationId = 0x0112;
            try
            {
                if (Array.IndexOf(img.PropertyIdList, OrientationId) < 0) return;
                int o = img.GetPropertyItem(OrientationId).Value[0];
                switch (o)
                {
                    case 3: img.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
                    case 6: img.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
                    case 8: img.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
                }
            }
            catch { }
        }
    }
}
