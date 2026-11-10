using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace ChangeLog.Core.Model
{
    [DataContract]
    public sealed class FieldChange
    {
        public FieldChange() { }

        public FieldChange(string field, string before, string after)
        {
            Field = field;
            Before = before;
            After = after;
        }

        [DataMember(Name = "n", Order = 0)] public string Field { get; set; }
        [DataMember(Name = "b", Order = 1, EmitDefaultValue = false)] public string Before { get; set; }
        [DataMember(Name = "a", Order = 2, EmitDefaultValue = false)] public string After { get; set; }

        public override string ToString()
        {
            return Field + ": " + (string.IsNullOrEmpty(Before) ? "∅" : Before) + " → " + (string.IsNullOrEmpty(After) ? "∅" : After);
        }
    }

    /// <summary>One logged change. Persisted as the body of a journal "change" entry.</summary>
    [DataContract]
    public sealed class ChangeRecord
    {
        public ChangeRecord()
        {
            Fields = new List<FieldChange>();
        }

        [DataMember(Name = "rid", Order = 0)] public string RecordId { get; set; }
        [DataMember(Name = "ts", Order = 1)] public string TimestampIso { get; set; }
        [DataMember(Name = "act", Order = 2)] public ChangeAction Action { get; set; }
        [DataMember(Name = "kind", Order = 3)] public ObjectKind Kind { get; set; }
        [DataMember(Name = "oid", Order = 4)] public int ObjectId { get; set; }
        [DataMember(Name = "lbl", Order = 5, EmitDefaultValue = false)] public string ObjectLabel { get; set; }
        [DataMember(Name = "pid", Order = 6, EmitDefaultValue = false)] public int? PageId { get; set; }
        [DataMember(Name = "plbl", Order = 7, EmitDefaultValue = false)] public string PageLabel { get; set; }
        [DataMember(Name = "usr", Order = 8)] public string User { get; set; }
        [DataMember(Name = "wusr", Order = 9, EmitDefaultValue = false)] public string WindowsUser { get; set; }
        [DataMember(Name = "mach", Order = 10, EmitDefaultValue = false)] public string Machine { get; set; }
        [DataMember(Name = "src", Order = 11)] public ChangeSource Source { get; set; }
        [DataMember(Name = "attr", Order = 12)] public Attribution Attribution { get; set; }
        [DataMember(Name = "batch", Order = 13, EmitDefaultValue = false)] public string BatchId { get; set; }
        [DataMember(Name = "fld", Order = 14)] public List<FieldChange> Fields { get; set; }

        /// <summary>Full state after the change (null for removals). Enables state replay.</summary>
        [DataMember(Name = "after", Order = 15, EmitDefaultValue = false)] public ObjectState After { get; set; }

        // ---- Populated on load, not persisted in the body ----
        public string JournalFile { get; set; }
        public long JournalSeq { get; set; }
        public string EntryHash { get; set; }

        public DateTime TimestampUtc => Util.Iso.Parse(TimestampIso);

        public ObjectKey Key => new ObjectKey(Kind, ObjectId);

        public string FieldSummary => string.Join("; ", (Fields ?? new List<FieldChange>()).Select(f => f.ToString()));
    }
}
