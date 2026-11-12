using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ChangeLog.Core.Model;

namespace ChangeLog.Core.Query
{
    /// <summary>Filter over change records. Empty sets mean "no restriction".</summary>
    public sealed class ChangeQuery
    {
        public ChangeQuery()
        {
            Kinds = new HashSet<ObjectKind>();
            Users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Actions = new HashSet<ChangeAction>();
        }

        public HashSet<ObjectKind> Kinds { get; }
        public HashSet<string> Users { get; }
        public HashSet<ChangeAction> Actions { get; }
        public DateTime? FromUtc { get; set; }
        public DateTime? ToUtc { get; set; }
        public int? PageId { get; set; }
        public string Text { get; set; }
        public bool IncludeReconciled { get; set; } = true;

        public bool IsEmpty =>
            Kinds.Count == 0 && Users.Count == 0 && Actions.Count == 0 && !FromUtc.HasValue && !ToUtc.HasValue &&
            !PageId.HasValue && string.IsNullOrWhiteSpace(Text) && IncludeReconciled;

        public bool Matches(ChangeRecord r)
        {
            if (Kinds.Count > 0 && !Kinds.Contains(r.Kind)) return false;
            if (Users.Count > 0 && !Users.Contains(r.User ?? string.Empty)) return false;
            if (Actions.Count > 0 && !Actions.Contains(r.Action)) return false;
            if (!IncludeReconciled && r.Source == ChangeSource.Reconciled) return false;
            if (PageId.HasValue && r.PageId != PageId) return false;
            if (FromUtc.HasValue || ToUtc.HasValue)
            {
                var t = r.TimestampUtc;
                if (FromUtc.HasValue && t < FromUtc.Value) return false;
                if (ToUtc.HasValue && t > ToUtc.Value) return false;
            }

            if (!string.IsNullOrWhiteSpace(Text))
            {
                var ci = CultureInfo.InvariantCulture.CompareInfo;
                Func<string, bool> has = s => s != null && ci.IndexOf(s, Text.Trim(), CompareOptions.IgnoreCase) >= 0;
                if (!(has(r.ObjectLabel) || has(r.PageLabel) || has(r.User) || has(r.FieldSummary) || has(r.Kind.ToString())))
                    return false;
            }

            return true;
        }

        public IEnumerable<ChangeRecord> Apply(IEnumerable<ChangeRecord> records) => records.Where(Matches);
    }

    public sealed class DayBucket
    {
        public DateTime Day { get; set; }
        public int Added { get; set; }
        public int Edited { get; set; }
        public int Removed { get; set; }
        public int Total => Added + Edited + Removed;
    }

    public sealed class ChangeStats
    {
        public int Total { get; private set; }
        public int Added { get; private set; }
        public int Edited { get; private set; }
        public int Removed { get; private set; }
        public int Contributors { get; private set; }
        public int Reconciled { get; private set; }
        public IList<KeyValuePair<string, int>> ByUser { get; private set; }
        public IList<KeyValuePair<ObjectKind, int>> ByKind { get; private set; }
        public IList<DayBucket> ByDay { get; private set; }

        /// <summary>Computes statistics; days are local calendar days ending at <paramref name="todayLocal"/>.</summary>
        public static ChangeStats Compute(IEnumerable<ChangeRecord> source, DateTime todayLocal, int days)
        {
            var list = source.ToList();
            var s = new ChangeStats
            {
                Total = list.Count,
                Added = list.Count(r => r.Action == ChangeAction.Added),
                Edited = list.Count(r => r.Action == ChangeAction.Edited),
                Removed = list.Count(r => r.Action == ChangeAction.Removed),
                Reconciled = list.Count(r => r.Source == ChangeSource.Reconciled),
                Contributors = list.Select(r => r.User).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ByUser = list.GroupBy(r => r.User ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                    .Select(g => new KeyValuePair<string, int>(g.Key, g.Count())).OrderByDescending(k => k.Value).ToList(),
                ByKind = list.GroupBy(r => r.Kind)
                    .Select(g => new KeyValuePair<ObjectKind, int>(g.Key, g.Count())).OrderByDescending(k => k.Value).ToList()
            };

            var start = todayLocal.Date.AddDays(-(days - 1));
            var buckets = Enumerable.Range(0, days).Select(i => new DayBucket { Day = start.AddDays(i) }).ToList();
            foreach (var r in list)
            {
                var d = r.TimestampUtc.ToLocalTime().Date;
                var idx = (int)(d - start).TotalDays;
                if (idx < 0 || idx >= days) continue;
                var b = buckets[idx];
                if (r.Action == ChangeAction.Added) b.Added++;
                else if (r.Action == ChangeAction.Edited) b.Edited++;
                else b.Removed++;
            }

            s.ByDay = buckets;
            return s;
        }
    }
}
