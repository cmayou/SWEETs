using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Storage
{
    /// <summary>A parsed journal file.</summary>
    public sealed class JournalFile
    {
        public JournalFile()
        {
            Entries = new List<JournalEntry>();
            MalformedLines = new List<int>();
        }

        public string Name { get; set; }
        public string Path { get; set; }
        public List<JournalEntry> Entries { get; }

        /// <summary>1-based line numbers that are not valid JSON entries.</summary>
        public List<int> MalformedLines { get; }

        /// <summary>The file ends without a newline (a write was interrupted or is in progress).</summary>
        public bool IncompleteTail { get; set; }

        public HeaderPayload Header
        {
            get
            {
                var h = Entries.FirstOrDefault(e => e.Type == EntryType.Header);
                if (h == null) return null;
                try { return h.Payload<HeaderPayload>(); }
                catch (SerializationException) { return null; }
            }
        }

        public JournalEntry Last => Entries.Count == 0 ? null : Entries[Entries.Count - 1];
    }

    public static class JournalReader
    {
        public static JournalFile Read(string path)
        {
            var jf = new JournalFile { Path = path, Name = System.IO.Path.GetFileName(path) };
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream())
            {
                fs.CopyTo(ms);
                bytes = ms.ToArray();
            }

            var text = Encoding.UTF8.GetString(bytes);
            var lines = text.Split('\n');
            jf.IncompleteTail = lines.Length > 0 && lines[lines.Length - 1].Length > 0;
            var complete = jf.IncompleteTail ? lines.Length - 1 : lines.Length;
            for (var i = 0; i < complete; i++)
            {
                var line = lines[i].TrimEnd('\r');
                if (line.Length == 0) continue;
                var e = TryParse(line);
                if (e == null) jf.MalformedLines.Add(i + 1);
                else jf.Entries.Add(e);
            }

            return jf;
        }

        public static JournalEntry TryParse(string line)
        {
            try
            {
                var e = Json.Deserialize<JournalEntry>(line);
                return e != null && e.Type != null && e.Hash != null ? e : null;
            }
            catch (SerializationException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Incrementally reads new complete lines appended to journal files (other users' live
    /// sessions). Byte offsets are safe because '\n' never occurs inside a UTF-8 sequence.
    /// </summary>
    public sealed class JournalTailer
    {
        private readonly StoreLayout _layout;
        private readonly Dictionary<string, long> _offsets = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public JournalTailer(StoreLayout layout)
        {
            _layout = layout;
        }

        /// <summary>Marks a file as already consumed up to its current length.</summary>
        public void Skip(string path)
        {
            _offsets[path] = new FileInfo(path).Length;
        }

        public IList<KeyValuePair<string, JournalEntry>> Poll(ISet<string> ignoreFiles)
        {
            var result = new List<KeyValuePair<string, JournalEntry>>();
            foreach (var path in _layout.JournalFiles())
            {
                var name = Path.GetFileName(path);
                if (ignoreFiles != null && ignoreFiles.Contains(name)) continue;
                long offset;
                _offsets.TryGetValue(path, out offset);
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (fs.Length <= offset) continue;
                        fs.Seek(offset, SeekOrigin.Begin);
                        var buf = new byte[fs.Length - offset];
                        var read = 0;
                        while (read < buf.Length)
                        {
                            var n = fs.Read(buf, read, buf.Length - read);
                            if (n <= 0) break;
                            read += n;
                        }

                        var lastNl = Array.LastIndexOf(buf, (byte)'\n', read - 1);
                        if (lastNl < 0) continue;
                        var chunk = Encoding.UTF8.GetString(buf, 0, lastNl + 1);
                        foreach (var line in chunk.Split('\n'))
                        {
                            var l = line.TrimEnd('\r');
                            if (l.Length == 0) continue;
                            var e = JournalReader.TryParse(l);
                            if (e != null) result.Add(new KeyValuePair<string, JournalEntry>(name, e));
                        }

                        _offsets[path] = offset + lastNl + 1;
                    }
                }
                catch (IOException)
                {
                    // file locked or vanished; try next poll
                }
            }

            return result;
        }
    }
}
