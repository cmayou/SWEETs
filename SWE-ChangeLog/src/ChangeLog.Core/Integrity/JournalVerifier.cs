using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using ChangeLog.Core.Storage;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Integrity
{
    public enum IntegrityStatus
    {
        Empty = 0,
        Verified = 1,
        Warning = 2,
        Tampered = 3
    }

    public enum IssueSeverity
    {
        Info = 0,
        Warning = 1,
        Tampered = 2
    }

    public sealed class IntegrityIssue
    {
        public IntegrityIssue(IssueSeverity severity, string file, long? seq, string message)
        {
            Severity = severity;
            File = file;
            Seq = seq;
            Message = message;
        }

        public IssueSeverity Severity { get; }
        public string File { get; }
        public long? Seq { get; }
        public string Message { get; }

        public override string ToString() => Severity + " " + File + (Seq.HasValue ? "#" + Seq.Value : string.Empty) + ": " + Message;
    }

    public sealed class IntegrityReport
    {
        public IntegrityReport()
        {
            Issues = new List<IntegrityIssue>();
            TamperedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            UnsignedEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        public IntegrityStatus Status { get; set; }
        public List<IntegrityIssue> Issues { get; }
        public int FilesChecked { get; set; }
        public long EntriesChecked { get; set; }
        public long SignedEntries { get; set; }
        public DateTime VerifiedUtc { get; set; }

        /// <summary>Files containing at least one tamper finding; their records are untrusted.</summary>
        public HashSet<string> TamperedFiles { get; }

        /// <summary>"file#seq" of entries not yet covered by a signed checkpoint.</summary>
        public HashSet<string> UnsignedEntries { get; }

        public static string EntryRef(string file, long seq) => file + "#" + seq;

        public string Summary
        {
            get
            {
                switch (Status)
                {
                    case IntegrityStatus.Empty: return "No journal yet";
                    case IntegrityStatus.Verified: return "Verified · " + EntriesChecked + " entries in " + FilesChecked + " sessions";
                    case IntegrityStatus.Warning: return Issues.Count(i => i.Severity == IssueSeverity.Warning) + " warning(s)";
                    default: return "Tampering detected · " + Issues.Count(i => i.Severity == IssueSeverity.Tampered) + " finding(s)";
                }
            }
        }
    }

    /// <summary>
    /// Verifies all journals of a project. Detects: edited/inserted/deleted entries
    /// (hash chain), forged entries (ECDSA checkpoints), deleted or truncated earlier
    /// sessions (per user+machine session links), rewritten journals of other users
    /// (witness entries), and rewrites of the newest data (external anchors, if configured).
    /// Cannot detect: removal of a sole author's newest unwitnessed, unanchored tail.
    /// </summary>
    public sealed class JournalVerifier
    {
        private readonly ISignatureVerifier _verifier;

        public JournalVerifier(ISignatureVerifier verifier)
        {
            _verifier = verifier;
        }

        public IntegrityReport Verify(IEnumerable<JournalFile> files, IEnumerable<WitnessHead> anchors, ISet<string> activeFiles, IClock clock)
        {
            var report = new IntegrityReport { VerifiedUtc = clock.UtcNow };
            var all = files.ToList();
            var byName = all.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
            activeFiles = activeFiles ?? new HashSet<string>();

            foreach (var f in all)
            {
                report.FilesChecked++;
                VerifyFile(f, activeFiles.Contains(f.Name), report);
            }

            VerifySessionLinks(all, byName, report);

            foreach (var f in all)
            {
                foreach (var w in f.Entries.Where(e => e.Type == EntryType.Witness))
                {
                    WitnessPayload p;
                    try { p = w.Payload<WitnessPayload>(); }
                    catch (SerializationException) { continue; } // body hash already covers it
                    foreach (var h in p.Heads)
                        CheckHead(byName, h, report, "witnessed by " + f.Name + "#" + w.Seq);
                }
            }

            foreach (var a in anchors ?? Enumerable.Empty<WitnessHead>())
                CheckHead(byName, a, report, "external anchor");

            if (all.Count == 0) report.Status = IntegrityStatus.Empty;
            else if (report.Issues.Any(i => i.Severity == IssueSeverity.Tampered)) report.Status = IntegrityStatus.Tampered;
            else if (report.Issues.Any(i => i.Severity == IssueSeverity.Warning)) report.Status = IntegrityStatus.Warning;
            else report.Status = IntegrityStatus.Verified;
            return report;
        }

        private void VerifyFile(JournalFile f, bool active, IntegrityReport r)
        {
            Action<long?, string> tamper = (seq, msg) =>
            {
                r.Issues.Add(new IntegrityIssue(IssueSeverity.Tampered, f.Name, seq, msg));
                r.TamperedFiles.Add(f.Name);
            };

            foreach (var line in f.MalformedLines)
                tamper(null, "Line " + line + " is not a valid journal entry.");

            if (f.IncompleteTail)
                r.Issues.Add(new IntegrityIssue(active ? IssueSeverity.Info : IssueSeverity.Warning, f.Name, null,
                    "File ends with an incomplete line (interrupted write)."));

            if (f.Entries.Count == 0)
            {
                tamper(null, "Journal has no entries.");
                return;
            }

            if (f.Entries[0].Type != EntryType.Header) tamper(0, "First entry is not a session header.");
            var header = f.Header;
            if (header == null)
            {
                tamper(0, "Session header is unreadable.");
                return;
            }

            if (string.IsNullOrEmpty(header.PublicKey) || KeyIds.FromPublicKey(header.PublicKey) != header.KeyId)
                tamper(0, "Header key fingerprint does not match its public key.");

            var prev = JournalEntry.GenesisPrev;
            long lastSigned = -1;
            for (var i = 0; i < f.Entries.Count; i++)
            {
                var e = f.Entries[i];
                r.EntriesChecked++;
                if (e.Seq != i) tamper(e.Seq, "Sequence gap: expected " + i + ", found " + e.Seq + " (entry inserted or deleted).");
                if (e.PrevHash != prev) tamper(e.Seq, "Chain broken: previous-hash does not match.");
                if (e.ComputeHash() != e.Hash) tamper(e.Seq, "Content hash mismatch (entry modified).");
                prev = e.Hash;

                if (e.Type == EntryType.Checkpoint)
                {
                    CheckpointPayload cp = null;
                    try { cp = e.Payload<CheckpointPayload>(); }
                    catch (SerializationException) { }
                    if (cp == null || i == 0 || cp.HeadSeq != i - 1 || cp.HeadHash != f.Entries[i - 1].Hash)
                    {
                        tamper(e.Seq, "Checkpoint does not seal the preceding entry.");
                        continue;
                    }

                    byte[] sig;
                    try { sig = Convert.FromBase64String(cp.Signature ?? string.Empty); }
                    catch (FormatException) { sig = new byte[0]; }
                    if (!_verifier.Verify(header.PublicKey, Encoding.UTF8.GetBytes(cp.HeadHash), sig))
                        tamper(e.Seq, "Checkpoint signature invalid (entries forged or re-signed with another key).");
                    else
                        lastSigned = i;
                }
            }

            r.SignedEntries += lastSigned + 1;
            var unsigned = f.Entries.Where(e => e.Seq > lastSigned && e.Type != EntryType.Checkpoint).ToList();
            foreach (var e in unsigned) r.UnsignedEntries.Add(IntegrityReport.EntryRef(f.Name, e.Seq));
            if (unsigned.Count > 0)
                r.Issues.Add(new IntegrityIssue(active ? IssueSeverity.Info : IssueSeverity.Warning, f.Name, unsigned[0].Seq,
                    unsigned.Count + " entr" + (unsigned.Count == 1 ? "y is" : "ies are") + " not covered by a signed checkpoint" +
                    (active ? " yet (session in progress)." : " (session ended abnormally).")));

            if (!active && f.Entries.All(e => e.Type != EntryType.Close))
                r.Issues.Add(new IntegrityIssue(IssueSeverity.Info, f.Name, null, "Session did not close cleanly."));
        }

        private static void VerifySessionLinks(List<JournalFile> all, Dictionary<string, JournalFile> byName, IntegrityReport r)
        {
            foreach (var f in all)
            {
                var h = f.Header;
                if (h == null || string.IsNullOrEmpty(h.PrevSessionFile)) continue;
                JournalFile prev;
                if (!byName.TryGetValue(h.PrevSessionFile, out prev))
                {
                    r.Issues.Add(new IntegrityIssue(IssueSeverity.Tampered, f.Name, 0, "Previous session journal '" + h.PrevSessionFile + "' is missing (deleted)."));
                    r.TamperedFiles.Add(f.Name);
                    continue;
                }

                if (h.PrevSessionSeq < 0 || h.PrevSessionSeq >= prev.Entries.Count || prev.Entries[(int)h.PrevSessionSeq].Hash != h.PrevSessionHead)
                {
                    r.Issues.Add(new IntegrityIssue(IssueSeverity.Tampered, prev.Name, h.PrevSessionSeq,
                        "Session was truncated or rewritten after the next session started (link from " + f.Name + ")."));
                    r.TamperedFiles.Add(prev.Name);
                }
            }

            // The newest session per user+machine must not be silently dropped: if the same chain
            // has files A -> C, a missing B is caught above. Report unlinked additional heads as info.
            foreach (var g in all.Where(f => f.Header != null).GroupBy(f => f.Header.WindowsUser + "|" + f.Header.Machine))
            {
                var referenced = new HashSet<string>(g.Select(f => f.Header.PrevSessionFile).Where(x => x != null), StringComparer.OrdinalIgnoreCase);
                var roots = g.Count(f => string.IsNullOrEmpty(f.Header.PrevSessionFile));
                if (roots > 1)
                    r.Issues.Add(new IntegrityIssue(IssueSeverity.Warning, g.First().Name, null,
                        "User chain '" + g.Key + "' has " + roots + " starting sessions (journal history may have been reset)."));
                var heads = g.Count(f => !referenced.Contains(f.Name));
                if (heads > 1)
                    r.Issues.Add(new IntegrityIssue(IssueSeverity.Info, g.First().Name, null,
                        "User chain '" + g.Key + "' has " + heads + " parallel sessions (concurrent SWE instances)."));
            }
        }

        private static void CheckHead(Dictionary<string, JournalFile> byName, WitnessHead h, IntegrityReport r, string source)
        {
            JournalFile target;
            if (!byName.TryGetValue(h.File, out target))
            {
                r.Issues.Add(new IntegrityIssue(IssueSeverity.Tampered, h.File, h.Seq, "Journal is missing but was " + source + "."));
                r.TamperedFiles.Add(h.File);
                return;
            }

            if (h.Seq < 0 || h.Seq >= target.Entries.Count || target.Entries[(int)h.Seq].Hash != h.Hash)
            {
                r.Issues.Add(new IntegrityIssue(IssueSeverity.Tampered, h.File, h.Seq, "Entry differs from the state " + source + " (truncated or rewritten)."));
                r.TamperedFiles.Add(h.File);
            }
        }
    }
}
