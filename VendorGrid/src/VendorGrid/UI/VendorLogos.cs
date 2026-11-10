using System;
using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VendorGrid.Core;

namespace VendorGrid.UI
{
    /// <summary>
    /// Official vendor logos shipped as resources (Resources/Vendors/{vendor id}.png). Vendors without a logo
    /// (custom websites) fall back to the coloured letter badge.
    /// </summary>
    public static class VendorLogos
    {
        static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>();

        public static ImageSource Get(string vendorId)
        {
            if (string.IsNullOrEmpty(vendorId)) return null;
            var id = vendorId.ToLowerInvariant();
            ImageSource img;
            if (Cache.TryGetValue(id, out img)) return img;

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri("pack://application:,,,/VendorGrid;component/Resources/Vendors/" + id + ".png", UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                img = bmp;
            }
            catch (Exception)
            {
                img = null;   // no logo for this vendor id
            }
            Cache[id] = img;
            return img;
        }
    }
}
