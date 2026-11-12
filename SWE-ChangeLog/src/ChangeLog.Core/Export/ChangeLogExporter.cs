using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ChangeLog.Core.Integrity;
using ChangeLog.Core.Model;
using ChangeLog.Core.Query;

namespace ChangeLog.Core.Export
{
    /// <summary>Builds the .xlsx export: Changes, Summary, Integrity sheets.</summary>
    public static class ChangeLogExporter
    {
        public static void Export(Stream output, ProjectInfo project, IList<ChangeRecord> records, IntegrityReport integrity, string filterDescription, DateTime exportedLocal)
        {
            var x = new XlsxWriter();
            var changes = x.AddSheet("Changes");
            changes.HasHeader = true;
            changes.Widths(20, 10, 16, 32, 26, 22, 22, 16, 11, 11, 12, 60, 12, 18);
            changes.Row("Time", "Action", "Type", "Object", "Page", "User", "Windows user", "Machine", "Source", "Attribution", "Batch", "Changes", "Object ID", "Integrity");
            foreach (var r in records.OrderBy(r => r.TimestampIso, StringComparer.Ordinal))
            {
                changes.Row(
                    r.TimestampUtc.ToLocalTime(),
                    r.Action.ToString(),
                    KindName(r.Kind),
                    r.ObjectLabel,
                    r.PageLabel,
                    r.User,
                    r.WindowsUser,
                    r.Machine,
                    r.Source.ToString(),
                    r.Attribution.ToString(),
                    r.BatchId,
                    string.Join("\n", r.Fields.Select(f => f.ToString())),
                    r.ObjectId,
                    EntryIntegrity(integrity, r));
            }

            var summary = x.AddSheet("Summary");
            summary.Widths(28, 14, 14, 14, 14);
            summary.Row(XlsxCell.Bold("Project"), project.ProjectName);
            summary.Row(XlsxCell.Bold("Exported"), exportedLocal);
            summary.Row(XlsxCell.Bold("Filter"), string.IsNullOrEmpty(filterDescription) ? "All changes" : filterDescription);
            summary.Row(XlsxCell.Bold("Integrity"), integrity?.Summary ?? "Not verified");
            summary.Row();
            summary.Row(XlsxCell.Bold("By type"), XlsxCell.Bold("Added"), XlsxCell.Bold("Edited"), XlsxCell.Bold("Removed"), XlsxCell.Bold("Total"));
            foreach (var g in records.GroupBy(r => r.Kind).OrderBy(g => g.Key))
                summary.Row(KindName(g.Key), g.Count(r => r.Action == ChangeAction.Added), g.Count(r => r.Action == ChangeAction.Edited),
                    g.Count(r => r.Action == ChangeAction.Removed), g.Count());
            summary.Row();
            summary.Row(XlsxCell.Bold("By user"), XlsxCell.Bold("Added"), XlsxCell.Bold("Edited"), XlsxCell.Bold("Removed"), XlsxCell.Bold("Total"));
            foreach (var g in records.GroupBy(r => r.User ?? "Unknown").OrderByDescending(g => g.Count()))
                summary.Row(g.Key, g.Count(r => r.Action == ChangeAction.Added), g.Count(r => r.Action == ChangeAction.Edited),
                    g.Count(r => r.Action == ChangeAction.Removed), g.Count());

            var integ = x.AddSheet("Integrity");
            integ.HasHeader = true;
            integ.Widths(12, 44, 10, 90);
            integ.Row("Severity", "Journal", "Entry", "Finding");
            if (integrity != null)
                foreach (var i in integrity.Issues.OrderByDescending(i => i.Severity))
                    integ.Row(i.Severity.ToString(), i.File, i.Seq.HasValue ? (object)i.Seq.Value : null, i.Message);

            x.Save(output);
        }

        public static string EntryIntegrity(IntegrityReport report, ChangeRecord r)
        {
            if (report == null || r.JournalFile == null) return "Not verified";
            if (report.TamperedFiles.Contains(r.JournalFile)) return "UNTRUSTED";
            return report.UnsignedEntries.Contains(IntegrityReport.EntryRef(r.JournalFile, r.JournalSeq)) ? "Unsigned" : "Signed";
        }

        public static string KindName(ObjectKind k)
        {
            switch (k)
            {
                case ObjectKind.WireSegment: return "Wire segment";
                case ObjectKind.ManufacturerPart: return "Manufacturer part";
                default: return k.ToString();
            }
        }
    }
}
