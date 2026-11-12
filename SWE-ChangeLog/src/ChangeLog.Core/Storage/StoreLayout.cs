using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Storage
{
    /// <summary>
    /// On-disk layout, by default inside the SWE project folder so it travels with the
    /// project and is shared in collaborative environments:
    ///   ChangeLog\project.json        stable project GUID
    ///   ChangeLog\journals\*.cljl     one append-only, hash-chained file per user session
    ///   ChangeLog\baselines\*.json.gz captured project states
    /// One writer per file means no concurrent-writer corruption on SMB shares.
    /// </summary>
    public sealed class StoreLayout
    {
        public const string JournalExtension = ".cljl";

        public StoreLayout(string root)
        {
            Root = root ?? throw new ArgumentNullException(nameof(root));
        }

        public string Root { get; }
        public string JournalDir => Path.Combine(Root, "journals");
        public string BaselineDir => Path.Combine(Root, "baselines");
        public string ProjectFile => Path.Combine(Root, "project.json");

        public void EnsureCreated()
        {
            Directory.CreateDirectory(JournalDir);
            Directory.CreateDirectory(BaselineDir);
        }

        public IEnumerable<string> JournalFiles()
        {
            if (!Directory.Exists(JournalDir)) return Enumerable.Empty<string>();
            return Directory.GetFiles(JournalDir, "*" + JournalExtension).OrderBy(Path.GetFileName, StringComparer.Ordinal);
        }

        public string JournalPath(string fileName) => Path.Combine(JournalDir, fileName);

        public string BaselinePath(string fileName) => Path.Combine(BaselineDir, fileName);

        /// <summary>Returns the project's stable GUID, creating it on first use.</summary>
        public string GetOrCreateProjectGuid()
        {
            EnsureCreated();
            if (File.Exists(ProjectFile))
            {
                try
                {
                    var p = Json.Deserialize<ProjectFileContent>(File.ReadAllText(ProjectFile));
                    if (!string.IsNullOrEmpty(p.ProjectGuid)) return p.ProjectGuid;
                }
                catch (SerializationException)
                {
                    // fall through and rewrite a damaged file
                }
            }

            var content = new ProjectFileContent { ProjectGuid = Guid.NewGuid().ToString("D"), CreatedIso = Iso.Format(DateTime.UtcNow) };
            try
            {
                using (var fs = new FileStream(ProjectFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    Json.WriteTo(fs, content);
                }
            }
            catch (IOException)
            {
                // Another user created it concurrently: use theirs.
                return Json.Deserialize<ProjectFileContent>(File.ReadAllText(ProjectFile)).ProjectGuid;
            }

            return content.ProjectGuid;
        }

        [DataContract]
        private sealed class ProjectFileContent
        {
            [DataMember(Name = "projectGuid", Order = 0)] public string ProjectGuid { get; set; }
            [DataMember(Name = "created", Order = 1)] public string CreatedIso { get; set; }
        }
    }
}
