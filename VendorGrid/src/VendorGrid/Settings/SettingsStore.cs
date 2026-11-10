using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using VendorGrid.Core;

namespace VendorGrid.Settings
{
    /// <summary>Settings are stored per Windows user, encrypted with DPAPI (API keys never touch disk in clear text).</summary>
    public static class SettingsStore
    {
        static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VendorGrid");
        static readonly string FilePath = Path.Combine(Dir, "settings.dat");
        static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VendorGrid.Settings.v1");   // unchanged so 1.x keys carry over

        public static AppSettings Load()
        {
            AppSettings s;
            try
            {
                if (!File.Exists(FilePath)) s = new AppSettings();
                else
                {
                    var clear = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
                    s = Json.Deserialize<AppSettings>(Encoding.UTF8.GetString(clear)) ?? new AppSettings();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Could not read settings; using defaults", ex);
                s = new AppSettings();
            }
            VendorCatalog.Ensure(s);
            return s;
        }

        public static void Save(AppSettings s)
        {
            Directory.CreateDirectory(Dir);
            var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(Json.Serialize(s)), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
        }

        public static AppSettings Clone(AppSettings s) { return Json.Deserialize<AppSettings>(Json.Serialize(s)); }
    }
}
