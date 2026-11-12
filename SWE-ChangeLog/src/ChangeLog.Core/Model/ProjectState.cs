using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;

namespace ChangeLog.Core.Model
{
    /// <summary>Complete captured state of a project at one moment (a "baseline").</summary>
    [DataContract]
    public sealed class ProjectState
    {
        public ProjectState()
        {
            Objects = new List<ObjectState>();
        }

        [DataMember(Name = "schema", Order = 0)] public int Schema { get; set; } = 1;
        [DataMember(Name = "project", Order = 1)] public string ProjectName { get; set; }
        [DataMember(Name = "captured", Order = 2)] public string CapturedIso { get; set; }
        [DataMember(Name = "by", Order = 3)] public string CapturedBy { get; set; }
        [DataMember(Name = "objects", Order = 4)] public List<ObjectState> Objects { get; set; }

        public Dictionary<ObjectKey, ObjectState> Index()
        {
            var d = new Dictionary<ObjectKey, ObjectState>();
            foreach (var o in Objects ?? Enumerable.Empty<ObjectState>()) d[o.Key] = o;
            return d;
        }

        public static ProjectState FromIndex(IEnumerable<ObjectState> objects, string projectName, string capturedIso, string capturedBy)
        {
            return new ProjectState
            {
                ProjectName = projectName,
                CapturedIso = capturedIso,
                CapturedBy = capturedBy,
                Objects = objects.OrderBy(o => o.Kind).ThenBy(o => o.Id).Select(o => o.Clone()).ToList()
            };
        }
    }

    /// <summary>Who is running this session.</summary>
    public sealed class UserIdentity
    {
        public UserIdentity(string displayName, string windowsUser, string machine)
        {
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? windowsUser : displayName;
            WindowsUser = windowsUser;
            Machine = machine;
        }

        public string DisplayName { get; }
        public string WindowsUser { get; }
        public string Machine { get; }
    }

    /// <summary>Static identity of the project the log belongs to.</summary>
    public sealed class ProjectInfo
    {
        public string ProjectGuid { get; set; }
        public string ProjectName { get; set; }
        public int SweProjectId { get; set; }
        public string StoreRoot { get; set; }
    }
}
