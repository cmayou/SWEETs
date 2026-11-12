using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.AccessControl;
using System.Text;
using ChangeLog.Core.Storage;

namespace ChangeLog.Core.Integrity
{
    /// <summary>
    /// Publishes journal heads somewhere the journal's author cannot rewrite. This is what
    /// makes tampering by the *only* user of a project detectable.
    /// </summary>
    public interface IAnchorSink
    {
        void Anchor(string projectGuid, string file, long seq, string hash);
        IEnumerable<WitnessHead> ReadAnchors(string projectGuid);
    }

    public sealed class NullAnchorSink : IAnchorSink
    {
        public static readonly NullAnchorSink Instance = new NullAnchorSink();
        public void Anchor(string projectGuid, string file, long seq, string hash) { }
        public IEnumerable<WitnessHead> ReadAnchors(string projectGuid) => new WitnessHead[0];
    }

    /// <summary>
    /// Appends heads to "&lt;folder&gt;\&lt;projectGuid&gt;.anchors". Intended for an IT-managed share whose
    /// NTFS ACL grants users only "Create files", "Append data" and "Read" (no Write data,
    /// no Delete). The file is opened with AppendData rights only, so it works under that ACL.
    /// </summary>
    public sealed class FileAnchorSink : IAnchorSink
    {
        private readonly string _folder;

        public FileAnchorSink(string folder)
        {
            _folder = folder;
        }

        private string PathFor(string projectGuid) => Path.Combine(_folder, projectGuid + ".anchors");

        public void Anchor(string projectGuid, string file, long seq, string hash)
        {
            var line = string.Join("\t", file, seq.ToString(CultureInfo.InvariantCulture), hash,
                DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)) + "\n";
            var bytes = Encoding.UTF8.GetBytes(line);
            using (var fs = new FileStream(PathFor(projectGuid), FileMode.Append, FileSystemRights.AppendData, FileShare.Read, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
            }
        }

        public IEnumerable<WitnessHead> ReadAnchors(string projectGuid)
        {
            var p = PathFor(projectGuid);
            if (!File.Exists(p)) yield break;
            string[] lines;
            using (var fs = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8))
            {
                lines = sr.ReadToEnd().Split('\n');
            }

            foreach (var l in lines)
            {
                var parts = l.Split('\t');
                long seq;
                if (parts.Length >= 3 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seq))
                    yield return new WitnessHead { File = parts[0], Seq = seq, Hash = parts[2] };
            }
        }
    }
}
