using System;
using System.Security.Cryptography;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Integrity
{
    public interface ISigner
    {
        /// <summary>Short fingerprint of the public key.</summary>
        string KeyId { get; }

        /// <summary>Base64 public key, published in each journal header.</summary>
        string PublicKey { get; }

        byte[] Sign(byte[] data);
    }

    public interface ISignatureVerifier
    {
        bool Verify(string publicKeyBase64, byte[] data, byte[] signature);
    }

    public static class KeyIds
    {
        public static string FromPublicKey(string publicKeyBase64)
        {
            return Hash.Sha256Hex(Convert.FromBase64String(publicKeyBase64)).Substring(0, 16);
        }
    }

    /// <summary>
    /// ECDSA P-256 signer backed by a persisted, NON-EXPORTABLE CNG key in the
    /// user's profile. The private key never touches disk in readable form, so
    /// other users cannot forge or alter this user's journal entries.
    /// Windows only (CNG).
    /// </summary>
    public sealed class CngEcdsaSigner : ISigner, IDisposable
    {
        public const string DefaultKeyName = "ChangeLog.SWE.JournalSigning.v1";

        private readonly CngKey _key;
        private readonly ECDsaCng _ecdsa;

        public CngEcdsaSigner(string keyName = DefaultKeyName)
        {
            if (CngKey.Exists(keyName))
            {
                _key = CngKey.Open(keyName);
            }
            else
            {
                var p = new CngKeyCreationParameters
                {
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing,
                    KeyCreationOptions = CngKeyCreationOptions.None
                };
                _key = CngKey.Create(CngAlgorithm.ECDsaP256, keyName, p);
            }

            _ecdsa = new ECDsaCng(_key);
            PublicKey = Convert.ToBase64String(_key.Export(CngKeyBlobFormat.EccPublicBlob));
            KeyId = KeyIds.FromPublicKey(PublicKey);
        }

        public string KeyId { get; }
        public string PublicKey { get; }

        public byte[] Sign(byte[] data) => _ecdsa.SignData(data, HashAlgorithmName.SHA256);

        public void Dispose()
        {
            _ecdsa.Dispose();
            _key.Dispose();
        }
    }

    public sealed class CngEcdsaVerifier : ISignatureVerifier
    {
        public bool Verify(string publicKeyBase64, byte[] data, byte[] signature)
        {
            try
            {
                using (var key = CngKey.Import(Convert.FromBase64String(publicKeyBase64), CngKeyBlobFormat.EccPublicBlob))
                using (var ecdsa = new ECDsaCng(key))
                {
                    return ecdsa.VerifyData(data, signature, HashAlgorithmName.SHA256);
                }
            }
            catch (CryptographicException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
