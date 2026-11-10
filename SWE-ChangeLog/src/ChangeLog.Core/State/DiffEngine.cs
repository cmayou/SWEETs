using System;
using System.Collections.Generic;
using System.Linq;
using ChangeLog.Core.Model;

namespace ChangeLog.Core.State
{
    /// <summary>Field-level comparison of two states of the same object.</summary>
    public static class ObjectDiffer
    {
        private const double Tolerance = 0.001;

        public static List<FieldChange> Diff(ObjectState before, ObjectState after)
        {
            var changes = new List<FieldChange>();
            if (before == null && after == null) return changes;

            if (before == null || after == null)
            {
                var only = before ?? after;
                foreach (var f in only.Fields.OrderBy(x => x.Name, StringComparer.Ordinal))
                    changes.Add(before == null ? new FieldChange(f.Name, null, f.Value) : new FieldChange(f.Name, f.Value, null));
                var g = only.Geometry?.Describe();
                if (!string.IsNullOrEmpty(g))
                    changes.Add(before == null ? new FieldChange(ObjectState.GeometryField, null, g) : new FieldChange(ObjectState.GeometryField, g, null));
                return changes;
            }

            var names = before.Fields.Select(f => f.Name).Union(after.Fields.Select(f => f.Name)).OrderBy(n => n, StringComparer.Ordinal);
            foreach (var n in names)
            {
                var b = before.Get(n);
                var a = after.Get(n);
                if (!string.Equals(b ?? string.Empty, a ?? string.Empty, StringComparison.Ordinal))
                    changes.Add(new FieldChange(n, b, a));
            }

            if (!string.Equals(before.Label ?? string.Empty, after.Label ?? string.Empty, StringComparison.Ordinal) && before.Get("Tag") == after.Get("Tag"))
                changes.Add(new FieldChange("Label", before.Label, after.Label));

            if (before.PageId != after.PageId)
                changes.Add(new FieldChange("Page", before.PageId?.ToString(), after.PageId?.ToString()));

            if (!GeometryEqual(before.Geometry, after.Geometry))
                changes.Add(new FieldChange(ObjectState.GeometryField, before.Geometry?.Describe(), after.Geometry?.Describe()));

            return changes;
        }

        public static bool GeometryEqual(Geometry a, Geometry b)
        {
            if (a == null || b == null) return a == null && b == null;
            return a.Shape == b.Shape &&
                   Math.Abs(a.X1 - b.X1) < Tolerance && Math.Abs(a.Y1 - b.Y1) < Tolerance &&
                   Math.Abs(a.X2 - b.X2) < Tolerance && Math.Abs(a.Y2 - b.Y2) < Tolerance &&
                   Math.Abs(a.Rotation - b.Rotation) < Tolerance;
        }
    }

    public sealed class DiffItem
    {
        public ChangeAction Action { get; set; }
        public ObjectKind Kind { get; set; }
        public int Id { get; set; }
        public string Label { get; set; }
        public int? PageId { get; set; }
        public ObjectState Before { get; set; }
        public ObjectState After { get; set; }
        public List<FieldChange> Fields { get; set; }

        public ObjectKey Key => new ObjectKey(Kind, Id);
        public string FieldSummary => string.Join("; ", Fields.Select(f => f.ToString()));
    }

    public sealed class PageDiffSummary
    {
        public int? PageId { get; set; }
        public string PageLabel { get; set; }
        public int Added { get; set; }
        public int Edited { get; set; }
        public int Removed { get; set; }
        public int Total => Added + Edited + Removed;
    }

    public sealed class DiffResult
    {
        public DiffResult()
        {
            Items = new List<DiffItem>();
            Pages = new List<PageDiffSummary>();
        }

        public List<DiffItem> Items { get; }

        /// <summary>Per-page change counts, most changed first. PageId null = objects without a page.</summary>
        public List<PageDiffSummary> Pages { get; }

        public ProjectState Before { get; set; }
        public ProjectState After { get; set; }

        public int Count(ChangeAction a) => Items.Count(i => i.Action == a);
        public int Count(ObjectKind k, ChangeAction a) => Items.Count(i => i.Kind == k && i.Action == a);
    }

    /// <summary>Compares two complete project states.</summary>
    public static class DiffEngine
    {
        public static DiffResult Compare(ProjectState before, ProjectState after)
        {
            var a = before.Index();
            var b = after.Index();
            var result = new DiffResult { Before = before, After = after };

            foreach (var kv in b)
            {
                ObjectState old;
                if (!a.TryGetValue(kv.Key, out old))
                {
                    result.Items.Add(Item(ChangeAction.Added, null, kv.Value));
                    continue;
                }

                var fields = ObjectDiffer.Diff(old, kv.Value);
                if (fields.Count > 0)
                {
                    var it = Item(ChangeAction.Edited, old, kv.Value);
                    it.Fields = fields;
                    result.Items.Add(it);
                }
            }

            foreach (var kv in a)
                if (!b.ContainsKey(kv.Key))
                    result.Items.Add(Item(ChangeAction.Removed, kv.Value, null));

            result.Items.Sort((x, y) =>
            {
                var c = x.Kind.CompareTo(y.Kind);
                return c != 0 ? c : x.Id.CompareTo(y.Id);
            });

            var pageLabels = new Dictionary<int, string>();
            foreach (var p in a.Values.Concat(b.Values).Where(o => o.Kind == ObjectKind.Page)) pageLabels[p.Id] = p.DisplayLabel;

            foreach (var g in result.Items.GroupBy(i => i.PageId))
            {
                string label;
                result.Pages.Add(new PageDiffSummary
                {
                    PageId = g.Key,
                    PageLabel = g.Key.HasValue ? (pageLabels.TryGetValue(g.Key.Value, out label) ? label : "Page #" + g.Key.Value) : "No page (project-level objects)",
                    Added = g.Count(i => i.Action == ChangeAction.Added),
                    Edited = g.Count(i => i.Action == ChangeAction.Edited),
                    Removed = g.Count(i => i.Action == ChangeAction.Removed)
                });
            }

            result.Pages.Sort((x, y) => y.Total.CompareTo(x.Total));
            return result;
        }

        private static DiffItem Item(ChangeAction action, ObjectState before, ObjectState after)
        {
            var s = after ?? before;
            return new DiffItem
            {
                Action = action,
                Kind = s.Kind,
                Id = s.Id,
                Label = s.DisplayLabel,
                PageId = s.Kind == ObjectKind.Page ? s.Id : (after?.PageId ?? before?.PageId),
                Before = before,
                After = after,
                Fields = ObjectDiffer.Diff(before, after)
            };
        }
    }
}
