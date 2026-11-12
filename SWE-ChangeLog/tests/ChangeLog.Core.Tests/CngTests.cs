using System;
using System.Security.Cryptography;
using System.Text;
using ChangeLog.Core.Integrity;

namespace ChangeLog.Core.Tests
{
    /// <summary>Exercises the real production signer. Windows only; skipped elsewhere.</summary>
    public static class CngTests
    {
        private const string TestKeyName = "ChangeLog.SWE.JournalSigning.TEST";

        [Test]
        public static void CngEcdsa_SignVerify_AndRejectsTamper()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            {
                Console.WriteLine("        (skipped: CNG requires Windows)");
                return;
            }

            try
            {
                using (var signer = new CngEcdsaSigner(TestKeyName))
                {
                    var verifier = new CngEcdsaVerifier();
                    var data = Encoding.UTF8.GetBytes("head-hash");
                    var sig = signer.Sign(data);
                    Assert.True(verifier.Verify(signer.PublicKey, data, sig), "valid signature verifies");
                    Assert.True(!verifier.Verify(signer.PublicKey, Encoding.UTF8.GetBytes("head-hasH"), sig), "tampered data rejected");
                    Assert.Equal(KeyIds.FromPublicKey(signer.PublicKey), signer.KeyId, "key id is public-key fingerprint");

                    using (var reopened = new CngEcdsaSigner(TestKeyName))
                        Assert.Equal(signer.PublicKey, reopened.PublicKey, "persisted key is reused");
                }
            }
            finally
            {
                if (CngKey.Exists(TestKeyName))
                    using (var k = CngKey.Open(TestKeyName)) k.Delete();
            }
        }
    }
}
