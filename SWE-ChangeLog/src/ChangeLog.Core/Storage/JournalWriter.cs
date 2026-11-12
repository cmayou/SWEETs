using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ChangeLog.Core.Integrity;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Storage
{
    /// <summary>
    /// Appends hash-chained entries to this session's own journal file. The file is
    /// opened exclusively for writing (others may read), flushed to disk on every
    /// append, and periodically sealed with a signed checkpoint. Thread-safe.
    /// </summary>
    public sealed class JournalWriter : IDisposable
    {
        private readonly object _gate = new object();
        private readonly ISigner _signer;
        private readonly IClock _clock;
        private FileStream _stream;

        private JournalWriter(string path, ISigner signer, IClock clock)
        {
            Path = path;
            FileName = System.IO.Path.GetFileName(path);
            _signer = signer;
            _clock = clock;
            _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            LastSeq = -1;
            LastHash = JournalEntry.GenesisPrev;
        }

        public string Path { get; }
        public string FileName { get; }
        public long LastSeq { get; private set; }
        public string LastHash { get; private set; }

        /// <summary>Entries appended since the last signed checkpoint.</summary>
        public int UnsignedCount { get; private set; }

        public static string NewFileName(DateTime utc, string machine, string windowsUser, string sessionId)
        {
            return utc.ToString("yyyyMMdd'T'HHmmss'Z'") + "_" + Names.FileSafe(machine) + "_" + Names.FileSafe(windowsUser) + "_" +
                   sessionId.Substring(0, 8) + StoreLayout.JournalExtension;
        }

        public static JournalWriter Create(StoreLayout layout, HeaderPayload header, ISigner signer, IClock clock)
        {
            layout.EnsureCreated();
            var name = NewFileName(clock.UtcNow, header.Machine, header.WindowsUser, header.SessionId);
            var w = new JournalWriter(layout.JournalPath(name), signer, clock);
            header.KeyId = signer.KeyId;
            header.PublicKey = signer.PublicKey;
            w.Append(EntryType.Header, Json.Serialize(header));
            w.Checkpoint();
            return w;
        }

        public JournalEntry Append(string type, string body)
        {
            lock (_gate)
            {
                if (_stream == null) throw new ObjectDisposedException(nameof(JournalWriter));
                var e = new JournalEntry
                {
                    Seq = LastSeq + 1,
                    Type = type,
                    TimestampIso = Iso.Format(_clock.UtcNow),
                    PrevHash = LastHash,
                    Body = body
                };
                e.Hash = e.ComputeHash();
                var bytes = Encoding.UTF8.GetBytes(Json.Serialize(e) + "\n");
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush(true);
                LastSeq = e.Seq;
                LastHash = e.Hash;
                if (type != EntryType.Checkpoint) UnsignedCount++;
                return e;
            }
        }

        /// <summary>
        /// Appends several entries with a single write + flush (one network round-trip for a
        /// whole burst such as a macro insertion). Returns the entries in order.
        /// </summary>
        public IList<JournalEntry> AppendBatch(string type, IList<string> bodies)
        {
            lock (_gate)
            {
                if (_stream == null) throw new ObjectDisposedException(nameof(JournalWriter));
                var entries = new List<JournalEntry>(bodies.Count);
                var sb = new StringBuilder();
                var seq = LastSeq;
                var prev = LastHash;
                var ts = Iso.Format(_clock.UtcNow);
                foreach (var body in bodies)
                {
                    var e = new JournalEntry { Seq = ++seq, Type = type, TimestampIso = ts, PrevHash = prev, Body = body };
                    e.Hash = e.ComputeHash();
                    prev = e.Hash;
                    entries.Add(e);
                    sb.Append(Json.Serialize(e)).Append('\n');
                }

                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                _stream.Write(bytes, 0, bytes.Length);
                _stream.Flush(true);
                LastSeq = seq;
                LastHash = prev;
                UnsignedCount += entries.Count;
                return entries;
            }
        }

        /// <summary>Signs the current head. Cheap; call after every batch.</summary>
        public JournalEntry Checkpoint()
        {
            lock (_gate)
            {
                if (UnsignedCount == 0) return null;
                var payload = new CheckpointPayload
                {
                    HeadSeq = LastSeq,
                    HeadHash = LastHash,
                    KeyId = _signer.KeyId,
                    Signature = Convert.ToBase64String(_signer.Sign(Encoding.UTF8.GetBytes(LastHash)))
                };
                var e = Append(EntryType.Checkpoint, Json.Serialize(payload));
                UnsignedCount = 0;
                return e;
            }
        }

        public void Close()
        {
            lock (_gate)
            {
                if (_stream == null) return;
                Append(EntryType.Close, "{}");
                Checkpoint();
                _stream.Dispose();
                _stream = null;
            }
        }

        public void Dispose() => Close();
    }
}
