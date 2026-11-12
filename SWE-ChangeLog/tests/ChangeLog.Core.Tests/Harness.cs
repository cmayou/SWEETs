using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ChangeLog.Core.Integrity;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Tests
{
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TestAttribute : Attribute { }

    public static class Assert
    {
        public static void True(bool c, string msg)
        {
            if (!c) throw new Exception("Assert failed: " + msg);
        }

        public static void Equal<T>(T expected, T actual, string msg)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new Exception("Assert failed: " + msg + " (expected '" + expected + "', got '" + actual + "')");
        }
    }

    /// <summary>Settable clock for deterministic coalescing tests.</summary>
    public sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = new DateTime(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);
        public void Advance(double seconds) => UtcNow = UtcNow.AddSeconds(seconds);
    }

    /// <summary>
    /// TEST-ONLY signer (HMAC keyed by the "public" key, i.e. insecure). Production uses
    /// ECDSA via CNG, which Mono cannot execute. Distinct instances behave like distinct users.
    /// </summary>
    public sealed class FakeSigner : ISigner, ISignatureVerifier
    {
        public FakeSigner()
        {
            var k = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(k);
            PublicKey = Convert.ToBase64String(k);
            KeyId = KeyIds.FromPublicKey(PublicKey);
        }

        public string KeyId { get; }
        public string PublicKey { get; }

        public byte[] Sign(byte[] data)
        {
            using (var h = new HMACSHA256(Convert.FromBase64String(PublicKey))) return h.ComputeHash(data);
        }

        public bool Verify(string publicKeyBase64, byte[] data, byte[] signature)
        {
            using (var h = new HMACSHA256(Convert.FromBase64String(publicKeyBase64)))
                return h.ComputeHash(data).SequenceEqual(signature);
        }
    }

    public static class Program
    {
        public static int Main(string[] args)
        {
            var tests = typeof(Program).Assembly.GetTypes()
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                .Where(m => m.GetCustomAttributes(typeof(TestAttribute), false).Length > 0)
                .OrderBy(m => m.DeclaringType.Name).ThenBy(m => m.Name).ToList();
            var failed = 0;
            foreach (var t in tests)
            {
                var dir = Path.Combine(Path.GetTempPath(), "cl-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(dir);
                try
                {
                    if (t.GetParameters().Length == 1) t.Invoke(null, new object[] { dir });
                    else t.Invoke(null, null);
                    Console.WriteLine("  PASS  " + t.DeclaringType.Name + "." + t.Name);
                }
                catch (TargetInvocationException e)
                {
                    failed++;
                    Console.WriteLine("  FAIL  " + t.DeclaringType.Name + "." + t.Name + ": " + e.InnerException);
                }
                finally
                {
                    try { Directory.Delete(dir, true); } catch (IOException) { }
                }
            }

            Console.WriteLine(failed == 0 ? "ALL " + tests.Count + " TESTS PASSED" : failed + " of " + tests.Count + " TESTS FAILED");
            return failed == 0 ? 0 : 1;
        }
    }
}
