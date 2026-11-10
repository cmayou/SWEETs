using System;
using System.IO;
using System.IO.Compression;
using ChangeLog.Core.Model;
using ChangeLog.Core.Storage;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.State
{
    /// <summary>Metadata of a stored baseline (from the journal entry that registered it).</summary>
    public sealed class BaselineInfo
    {
        public BaselinePayload Payload { get; set; }
        public string JournalFile { get; set; }
        public long JournalSeq { get; set; }

        public string Id => Payload.BaselineId;
        public string Name => Payload.Name;
        public DateTime CapturedUtc => Iso.Parse(Payload.CapturedIso);
        public string CapturedBy => Payload.CapturedBy;
        public BaselineKind Kind => Payload.Kind;

        public string Display => Name + " · " + CapturedUtc.ToLocalTime().ToString("g") + " · " + CapturedBy;
    }

    /// <summary>
    /// Stores project states as gzip JSON. Each baseline's SHA-256 is recorded in the
    /// signed journal, so a modified baseline file is detected on load.
    /// </summary>
    public sealed class BaselineStore
    {
        private readonly StoreLayout _layout;

        public BaselineStore(StoreLayout layout)
        {
            _layout = layout;
        }

        /// <summary>Writes the state file and returns the payload to register in the journal.</summary>
        public BaselinePayload Save(ProjectState state, string name, BaselineKind kind, int? nativeSnapshotId)
        {
            _layout.EnsureCreated();
            var id = Guid.NewGuid().ToString("N").Substring(0, 12);
            var fileName = Iso.Parse(state.CapturedIso).ToString("yyyyMMdd'T'HHmmss'Z'") + "_" + id + ".json.gz";
            var path = _layout.BaselinePath(fileName);
            using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
            {
                Json.WriteTo(gz, state);
            }

            return new BaselinePayload
            {
                BaselineId = id,
                Name = name,
                Kind = kind,
                FileName = fileName,
                Sha256 = Hash.Sha256HexOfFile(path),
                ObjectCount = state.Objects.Count,
                CapturedIso = state.CapturedIso,
                CapturedBy = state.CapturedBy,
                NativeSnapshotId = nativeSnapshotId
            };
        }

        /// <summary>Loads a baseline, verifying it against the hash recorded in the journal.</summary>
        public ProjectState Load(BaselineInfo info)
        {
            var path = _layout.BaselinePath(info.Payload.FileName);
            if (!File.Exists(path)) throw new FileNotFoundException("Baseline file is missing.", path);
            var actual = Hash.Sha256HexOfFile(path);
            if (!string.Equals(actual, info.Payload.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Baseline '" + info.Name + "' was modified after capture (hash mismatch).");
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var gz = new GZipStream(fs, CompressionMode.Decompress))
            {
                return Json.ReadFrom<ProjectState>(gz);
            }
        }
    }
}
