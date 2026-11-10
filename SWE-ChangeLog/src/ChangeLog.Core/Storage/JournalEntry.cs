using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Serialization;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Storage
{
    public static class EntryType
    {
        public const string Header = "header";
        public const string Change = "change";
        public const string Witness = "witness";
        public const string Checkpoint = "checkpoint";
        public const string Baseline = "baseline";
        public const string Close = "close";
    }

    /// <summary>
    /// One line of a journal file. The payload is stored as a JSON *string* in
    /// <see cref="Body"/> so the hash covers the exact persisted bytes and never
    /// depends on re-serialisation.
    /// </summary>
    [DataContract]
    public sealed class JournalEntry
    {
        [DataMember(Name = "seq", Order = 0)] public long Seq { get; set; }
        [DataMember(Name = "type", Order = 1)] public string Type { get; set; }
        [DataMember(Name = "ts", Order = 2)] public string TimestampIso { get; set; }
        [DataMember(Name = "prev", Order = 3)] public string PrevHash { get; set; }
        [DataMember(Name = "body", Order = 4)] public string Body { get; set; }
        [DataMember(Name = "hash", Order = 5)] public string Hash { get; set; }

        public const string GenesisPrev = "0000000000000000000000000000000000000000000000000000000000000000";

        /// <summary>SHA-256 over seq|type|ts|prev|body. Changing any of them breaks the chain.</summary>
        public string ComputeHash()
        {
            return Util.Hash.Sha256Hex(string.Join("\u001f",
                Seq.ToString(CultureInfo.InvariantCulture), Type ?? string.Empty, TimestampIso ?? string.Empty,
                PrevHash ?? string.Empty, Body ?? string.Empty));
        }

        public T Payload<T>() => Json.Deserialize<T>(Body);
    }

    [DataContract]
    public sealed class HeaderPayload
    {
        [DataMember(Name = "schema", Order = 0)] public int Schema { get; set; } = 1;
        [DataMember(Name = "projectGuid", Order = 1)] public string ProjectGuid { get; set; }
        [DataMember(Name = "projectName", Order = 2)] public string ProjectName { get; set; }
        [DataMember(Name = "sweProjectId", Order = 3)] public int SweProjectId { get; set; }
        [DataMember(Name = "user", Order = 4)] public string User { get; set; }
        [DataMember(Name = "windowsUser", Order = 5)] public string WindowsUser { get; set; }
        [DataMember(Name = "machine", Order = 6)] public string Machine { get; set; }
        [DataMember(Name = "sessionId", Order = 7)] public string SessionId { get; set; }
        [DataMember(Name = "keyId", Order = 8)] public string KeyId { get; set; }
        [DataMember(Name = "publicKey", Order = 9)] public string PublicKey { get; set; }
        [DataMember(Name = "appVersion", Order = 10, EmitDefaultValue = false)] public string AppVersion { get; set; }
        [DataMember(Name = "addinVersion", Order = 11, EmitDefaultValue = false)] public string AddInVersion { get; set; }

        /// <summary>Previous session file of the same user+machine, linking sessions into one chain.</summary>
        [DataMember(Name = "prevFile", Order = 12, EmitDefaultValue = false)] public string PrevSessionFile { get; set; }
        [DataMember(Name = "prevSeq", Order = 13)] public long PrevSessionSeq { get; set; } = -1;
        [DataMember(Name = "prevHead", Order = 14, EmitDefaultValue = false)] public string PrevSessionHead { get; set; }
    }

    [DataContract]
    public sealed class WitnessHead
    {
        [DataMember(Name = "file", Order = 0)] public string File { get; set; }
        [DataMember(Name = "seq", Order = 1)] public long Seq { get; set; }
        [DataMember(Name = "hash", Order = 2)] public string Hash { get; set; }
    }

    /// <summary>Records the current heads of other journals; rewriting them later is detectable.</summary>
    [DataContract]
    public sealed class WitnessPayload
    {
        public WitnessPayload()
        {
            Heads = new List<WitnessHead>();
        }

        [DataMember(Name = "heads", Order = 0)] public List<WitnessHead> Heads { get; set; }
    }

    /// <summary>Signature over the hash of the preceding entry.</summary>
    [DataContract]
    public sealed class CheckpointPayload
    {
        [DataMember(Name = "headSeq", Order = 0)] public long HeadSeq { get; set; }
        [DataMember(Name = "head", Order = 1)] public string HeadHash { get; set; }
        [DataMember(Name = "keyId", Order = 2)] public string KeyId { get; set; }
        [DataMember(Name = "sig", Order = 3)] public string Signature { get; set; }
    }

    [DataContract]
    public sealed class BaselinePayload
    {
        [DataMember(Name = "id", Order = 0)] public string BaselineId { get; set; }
        [DataMember(Name = "name", Order = 1)] public string Name { get; set; }
        [DataMember(Name = "kind", Order = 2)] public Model.BaselineKind Kind { get; set; }
        [DataMember(Name = "file", Order = 3)] public string FileName { get; set; }
        [DataMember(Name = "sha256", Order = 4)] public string Sha256 { get; set; }
        [DataMember(Name = "objects", Order = 5)] public int ObjectCount { get; set; }
        [DataMember(Name = "captured", Order = 6)] public string CapturedIso { get; set; }
        [DataMember(Name = "by", Order = 7)] public string CapturedBy { get; set; }
        [DataMember(Name = "nativeSnapshotId", Order = 8, EmitDefaultValue = false)] public int? NativeSnapshotId { get; set; }
    }
}
