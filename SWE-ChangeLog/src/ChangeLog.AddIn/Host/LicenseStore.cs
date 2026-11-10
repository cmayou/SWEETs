using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ChangeLog.AddIn.Host
{
    public enum LicenseScope
    {
        None = 0,
        User = 1,
        Machine = 2
    }

    /// <summary>
    /// Stores the SWE API license key encrypted with Windows DPAPI. The key is never in
    /// source, the installer, or plain-text config.
    ///  * User scope (default): %APPDATA%\SWE ChangeLog\license.bin, decryptable only by this Windows user.
    ///  * Machine scope (IT deployment via build\Set-LicenseKey.ps1): %ProgramData%\SWE ChangeLog\license.bin,
    ///    decryptable by any process on this machine; acceptable for shared workstations, weaker than User scope.
    /// </summary>
    public static class LicenseStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SWE-ChangeLog/license/v1");

        private static string UserPath => Path.Combine(AddInSettings.UserFolder, "license.bin");
        private static string MachinePath => Path.Combine(AddInSettings.MachineFolder, "license.bin");

        public static string Get(out LicenseScope scope)
        {
            var k = Read(UserPath, DataProtectionScope.CurrentUser);
            if (!string.IsNullOrEmpty(k))
            {
                scope = LicenseScope.User;
                return k;
            }

            k = Read(MachinePath, DataProtectionScope.LocalMachine);
            scope = string.IsNullOrEmpty(k) ? LicenseScope.None : LicenseScope.Machine;
            return k;
        }

        public static void SaveForUser(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Key is empty.", nameof(key));
            Directory.CreateDirectory(AddInSettings.UserFolder);
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(UserPath, blob);
        }

        public static void ClearUser()
        {
            if (File.Exists(UserPath)) File.Delete(UserPath);
        }

        /// <summary>Masked form for display, e.g. "4926A1…3979".</summary>
        public static string Mask(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            return key.Length <= 10 ? new string('•', key.Length) : key.Substring(0, 6) + "…" + key.Substring(key.Length - 4);
        }

        private static string Read(string path, DataProtectionScope scope)
        {
            try
            {
                if (!File.Exists(path)) return null;
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, scope));
            }
            catch (CryptographicException e)
            {
                Log.Warn("License key at " + path + " cannot be decrypted (other user/machine?): " + e.Message);
                return null;
            }
            catch (IOException e)
            {
                Log.Warn("License key at " + path + " unreadable: " + e.Message);
                return null;
            }
        }
    }
}
