using System;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace ChangeLog.Core.Util
{
    public interface IClock
    {
        DateTime UtcNow { get; }
    }

    public sealed class SystemClock : IClock
    {
        public static readonly SystemClock Instance = new SystemClock();
        public DateTime UtcNow => DateTime.UtcNow;
    }

    public static class Iso
    {
        public static string Format(DateTime utc)
        {
            return DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        }

        public static DateTime Parse(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return DateTime.MinValue;
            return DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        }
    }

    /// <summary>
    /// JSON via DataContractJsonSerializer: part of .NET Framework, so Core has zero
    /// NuGet dependencies. Never use JSON output as hash input; hash the stored string.
    /// </summary>
    public static class Json
    {
        private static readonly DataContractJsonSerializerSettings Settings = new DataContractJsonSerializerSettings
        {
            UseSimpleDictionaryFormat = true
        };

        public static string Serialize<T>(T value)
        {
            var ser = new DataContractJsonSerializer(typeof(T), Settings);
            using (var ms = new MemoryStream())
            {
                ser.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Deserialize<T>(string json)
        {
            var ser = new DataContractJsonSerializer(typeof(T), Settings);
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return (T)ser.ReadObject(ms);
            }
        }

        public static void WriteTo<T>(Stream stream, T value)
        {
            new DataContractJsonSerializer(typeof(T), Settings).WriteObject(stream, value);
        }

        public static T ReadFrom<T>(Stream stream)
        {
            return (T)new DataContractJsonSerializer(typeof(T), Settings).ReadObject(stream);
        }
    }

    public static class Hash
    {
        public static string Sha256Hex(string text)
        {
            return Sha256Hex(Encoding.UTF8.GetBytes(text));
        }

        public static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return ToHex(sha.ComputeHash(data));
            }
        }

        public static string Sha256HexOfFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                return ToHex(sha.ComputeHash(fs));
            }
        }

        public static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    public static class Names
    {
        /// <summary>Makes a string safe for use in a file name.</summary>
        public static string FileSafe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder();
            foreach (var ch in s)
                sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '-');
            var r = sb.ToString().Trim('-');
            return r.Length > 40 ? r.Substring(0, 40) : (r.Length == 0 ? "unknown" : r);
        }
    }
}
