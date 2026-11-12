using System.IO;
using System.Linq;
using System.Text;
using ChangeLog.Core.Export;
using ChangeLog.Core.Integrity;
using ChangeLog.Core.Model;
using ChangeLog.Core.Query;
using ChangeLog.Core.Recording;
using ChangeLog.Core.State;
using ChangeLog.Core.Storage;
using ChangeLog.Core.Util;

namespace ChangeLog.Core.Tests
{
    public static class SessionAndIntegrityTests
    {
        private static SessionOptions Opts(string dir, UserIdentity who, FakeSigner signer, FakeClock clock, IAnchorSink anchor = null)
        {
            return new SessionOptions
            {
                StoreRoot = dir, ProjectName = "Demo", SweProjectId = 42, Who = who, Signer = signer, Verifier = signer,
                Clock = clock, Synchronous = true, Anchor = anchor ?? NullAnchorSink.Instance,
                PollInterval = System.TimeSpan.Zero, WitnessInterval = System.TimeSpan.Zero
            };
        }

        private static ProjectState State(params ObjectState[] objs) =>
            new ProjectState { ProjectName = "Demo", CapturedIso = Iso.Format(new FakeClock().UtcNow), CapturedBy = "x", Objects = objs.ToList() };

        /// <summary>Runs one session for Alice: project has wire 1 in <paramref name="from"/>; she recolors it to <paramref name="color"/>.</summary>
        private static void AliceSession(string dir, FakeSigner signer, FakeClock clock, string color, string from = "BK")
        {
            var s = ChangeLogSession.Open(Opts(dir, RecorderTests.Alice, signer, clock));
            var reconciled = s.InitializeState(State(RecorderTests.Page(1, "01"), RecorderTests.Wire(1, "W1", from)), "Alice Martin");
            Assert.Equal(0, reconciled.Count, "no unexplained differences between sessions");
            s.Observe(new RawEvent(RawEventType.Update, new ObjectKey(ObjectKind.Wire, 1), RecorderTests.Wire(1, "W1", color), clock.UtcNow));
            clock.Advance(3);
            s.Pump();
            s.Close(true);
            clock.Advance(60);
        }

        private static IntegrityReport Verify(string dir, ISignatureVerifier v, IAnchorSink anchor = null)
        {
            var layout = new StoreLayout(dir);
            var files = layout.JournalFiles().Select(JournalReader.Read).ToList();
            var anchors = anchor == null ? null : anchor.ReadAnchors(layout.GetOrCreateProjectGuid());
            return new JournalVerifier(v).Verify(files, anchors, null, new FakeClock());
        }

        [Test]
        public static void Session_RoundTrip_IsVerified(string dir)
        {
            var signer = new FakeSigner();
            var clock = new FakeClock();
            AliceSession(dir, signer, clock, "RD");
            var s = ChangeLogSession.Open(Opts(dir, RecorderTests.Alice, signer, clock));
            Assert.Equal(1, s.Records.Count, "record persisted and reloaded");
            Assert.Equal("Color: BK → RD", s.Records[0].FieldSummary, "field diff persisted");
            Assert.True(s.Records[0].JournalFile != null, "journal reference");
            Assert.Equal(IntegrityStatus.Verified, s.Integrity.Status, "clean journals verify: " + string.Join(" | ", s.Integrity.Issues));
            Assert.True(s.Baselines.Count >= 2, "initial + session-end baselines");
            s.Close(false);
        }

        [Test]
        public static void Tamper_EditedBody_Detected(string dir)
        {
            var signer = new FakeSigner();
            AliceSession(dir, signer, new FakeClock(), "RD");
            var file = new StoreLayout(dir).JournalFiles().First();
            File.WriteAllText(file, File.ReadAllText(file).Replace("RD", "GN"));
            var r = Verify(dir, signer);
            Assert.Equal(IntegrityStatus.Tampered, r.Status, "edited entry detected");
        }

        [Test]
        public static void Tamper_DeletedLine_Detected(string dir)
        {
            var signer = new FakeSigner();
            AliceSession(dir, signer, new FakeClock(), "RD");
            var file = new StoreLayout(dir).JournalFiles().First();
            var lines = File.ReadAllLines(file).ToList();
            lines.RemoveAt(lines.FindIndex(l => l.Contains("\"type\":\"change\"")));
            File.WriteAllLines(file, lines);
            Assert.Equal(IntegrityStatus.Tampered, Verify(dir, signer).Status, "deleted entry detected");
        }

        [Test]
        public static void Tamper_RechainedWithoutKey_Detected(string dir)
        {
            // Attacker edits a change AND recomputes every hash, but cannot produce the signature.
            var signer = new FakeSigner();
            AliceSession(dir, signer, new FakeClock(), "RD");
            var file = new StoreLayout(dir).JournalFiles().First();
            var entries = JournalReader.Read(file).Entries;
            var prev = JournalEntry.GenesisPrev;
            var sb = new StringBuilder();
            foreach (var e in entries)
            {
                if (e.Type == EntryType.Change) e.Body = e.Body.Replace("RD", "GN");
                e.PrevHash = prev;
                e.Hash = e.ComputeHash();
                prev = e.Hash;
                sb.Append(Json.Serialize(e)).Append('\n');
            }

            File.WriteAllText(file, sb.ToString());
            var r = Verify(dir, signer);
            Assert.Equal(IntegrityStatus.Tampered, r.Status, "re-chained forgery detected by signatures");
            Assert.True(r.Issues.Any(i => i.Message.Contains("signature") || i.Message.Contains("seal")), "signature finding");
        }

        [Test]
        public static void Tamper_DeletedPreviousSession_Detected(string dir)
        {
            var signer = new FakeSigner();
            var clock = new FakeClock();
            AliceSession(dir, signer, clock, "RD");
            AliceSession(dir, signer, clock, "GN", "RD");
            var first = new StoreLayout(dir).JournalFiles().First();
            File.Delete(first);
            var r = Verify(dir, signer);
            Assert.Equal(IntegrityStatus.Tampered, r.Status, "missing earlier session detected");
        }

        [Test]
        public static void Tamper_TruncatedFileWitnessedByOtherUser_Detected(string dir)
        {
            var alice = new FakeSigner();
            var bob = new FakeSigner();
            var clock = new FakeClock();
            AliceSession(dir, alice, clock, "RD");

            // Bob opens the project: his header session witnesses Alice's journal head.
            var b = ChangeLogSession.Open(Opts(dir, RecorderTests.Bob, bob, clock));
            b.Close(false);

            // Alice (sole owner of her key) rewrites her own journal: drop everything after the header.
            var layout = new StoreLayout(dir);
            var aliceFile = layout.JournalFiles().First(f => f.Contains("amartin"));
            var lines = File.ReadAllLines(aliceFile).Take(2).ToArray(); // header + its checkpoint (validly signed)
            File.WriteAllLines(aliceFile, lines);

            var combined = new CombinedVerifier(alice, bob);
            var r = Verify(dir, combined);
            Assert.Equal(IntegrityStatus.Tampered, r.Status, "Bob's witness exposes Alice's truncation");
        }

        [Test]
        public static void Tamper_SoleAuthorTruncation_DetectedByAnchor(string dir)
        {
            var signer = new FakeSigner();
            var anchorDir = Path.Combine(dir, "anchors");
            Directory.CreateDirectory(anchorDir);
            var anchor = new FileAnchorSink(anchorDir);
            var clock = new FakeClock();
            var s = ChangeLogSession.Open(Opts(Path.Combine(dir, "store"), RecorderTests.Alice, signer, clock, anchor));
            s.InitializeState(State(RecorderTests.Wire(1, "W1", "BK")), "x");
            s.Observe(new RawEvent(RawEventType.Update, new ObjectKey(ObjectKind.Wire, 1), RecorderTests.Wire(1, "W1", "RD"), clock.UtcNow));
            clock.Advance(3);
            s.Pump();
            s.Close(false);

            var file = new StoreLayout(Path.Combine(dir, "store")).JournalFiles().Single();
            File.WriteAllLines(file, File.ReadAllLines(file).Take(2));
            Assert.Equal(IntegrityStatus.Verified, Verify(Path.Combine(dir, "store"), signer).Status, "without anchor, a sole author's truncation to a signed prefix is undetectable");
            Assert.Equal(IntegrityStatus.Tampered, Verify(Path.Combine(dir, "store"), signer, anchor).Status, "anchor detects it");
        }

        [Test]
        public static void CrashTail_IsWarningNotTamper(string dir)
        {
            var signer = new FakeSigner();
            var clock = new FakeClock();
            var layout = new StoreLayout(dir);
            var w = JournalWriter.Create(layout, new HeaderPayload { WindowsUser = "u", Machine = "m", SessionId = "0123456789abcdef" }, signer, clock);
            w.Append(EntryType.Change, Json.Serialize(new ChangeRecord { RecordId = "r", TimestampIso = Iso.Format(clock.UtcNow), User = "u" }));
            // process dies here: no checkpoint, no close (stream left open is fine for the test)
            var r = Verify(dir, signer);
            Assert.Equal(IntegrityStatus.Warning, r.Status, "unsigned tail after crash is a warning");
            Assert.Equal(1, r.UnsignedEntries.Count, "one unsigned entry");
            w.Close();
        }

        [Test]
        public static void Reconcile_ChangesMadeWithoutAddIn_AreInferred(string dir)
        {
            var signer = new FakeSigner();
            var clock = new FakeClock();
            AliceSession(dir, signer, clock, "RD");

            // Someone edits the project with the add-in not running: wire renamed, symbol added.
            var actual = State(RecorderTests.Page(1, "01"), RecorderTests.Wire(1, "W100", "RD"), RecorderTests.Symbol(9, 1, "-Q1", 1, 1));
            var s = ChangeLogSession.Open(Opts(dir, RecorderTests.Bob, new FakeSigner(), clock));
            var rec = s.InitializeState(actual, "Carol (project 'modified by')");
            Assert.Equal(2, rec.Count, "rename + add reconciled: " + string.Join(" | ", rec.Select(x => x.Action + " " + x.ObjectLabel + " " + x.FieldSummary)));
            Assert.True(rec.All(x => x.Source == ChangeSource.Reconciled && x.Attribution == Attribution.Inferred), "flagged as inferred");
            Assert.True(rec.All(x => x.User.StartsWith("Carol")), "best-guess user");
            s.Close(false);
        }

        [Test]
        public static void Baseline_ModifiedFile_IsRejected(string dir)
        {
            var signer = new FakeSigner();
            var clock = new FakeClock();
            AliceSession(dir, signer, clock, "RD");
            var layout = new StoreLayout(dir);
            var bfile = Directory.GetFiles(layout.BaselineDir).First();
            File.AppendAllText(bfile, "x");
            var s = ChangeLogSession.Open(Opts(dir, RecorderTests.Alice, signer, clock));
            var info = s.Baselines.First(b => b.Payload.FileName == Path.GetFileName(bfile));
            var threw = false;
            try { s.LoadBaseline(info); }
            catch (InvalidDataException) { threw = true; }
            Assert.True(threw, "hash mismatch rejected");
            s.Close(false);
        }

        [Test]
        public static void Diff_GroupsByPage()
        {
            var a = State(RecorderTests.Page(1, "01"), RecorderTests.Page(2, "02"), RecorderTests.Symbol(10, 1, "-K1", 0, 0), RecorderTests.Symbol(11, 2, "-K2", 0, 0), RecorderTests.Wire(1, "W1"));
            var b = State(RecorderTests.Page(1, "01"), RecorderTests.Page(2, "02"), RecorderTests.Symbol(10, 1, "-K1", 30, 0), RecorderTests.Symbol(12, 2, "-K3", 0, 0), RecorderTests.Wire(1, "W1", "RD"));
            var d = DiffEngine.Compare(a, b);
            Assert.Equal(1, d.Count(ChangeAction.Added), "K3 added");
            Assert.Equal(1, d.Count(ChangeAction.Removed), "K2 removed");
            Assert.Equal(2, d.Count(ChangeAction.Edited), "K1 moved + W1 recolored");
            Assert.Equal(2, d.Pages.Single(p => p.PageId == 2).Total, "page 02 has 2 changes");
            Assert.Equal("No page (project-level objects)", d.Pages.Single(p => p.PageId == null).PageLabel, "wire is page-less");
        }

        [Test]
        public static void Query_FiltersByKindUserTextAndAction()
        {
            var recs = new[]
            {
                new ChangeRecord { Kind = ObjectKind.Wire, User = "Alice", Action = ChangeAction.Edited, ObjectLabel = "W1", TimestampIso = "2026-11-01T10:00:00.000Z" },
                new ChangeRecord { Kind = ObjectKind.Symbol, User = "Bob", Action = ChangeAction.Added, ObjectLabel = "-K1", TimestampIso = "2026-11-02T10:00:00.000Z" },
                new ChangeRecord { Kind = ObjectKind.Symbol, User = "Alice", Action = ChangeAction.Removed, ObjectLabel = "-K2", TimestampIso = "2026-11-03T10:00:00.000Z", Source = ChangeSource.Reconciled }
            };
            var q = new ChangeQuery();
            q.Kinds.Add(ObjectKind.Symbol);
            Assert.Equal(2, q.Apply(recs).Count(), "kind");
            q.Users.Add("alice");
            Assert.Equal(1, q.Apply(recs).Count(), "kind+user (case-insensitive)");
            q.IncludeReconciled = false;
            Assert.Equal(0, q.Apply(recs).Count(), "reconciled excluded");
            var t = new ChangeQuery { Text = "k1" };
            Assert.Equal(1, t.Apply(recs).Count(), "text search");
            var stats = ChangeStats.Compute(recs, new System.DateTime(2026, 11, 3), 7);
            Assert.Equal(3, stats.Total, "stats total");
            Assert.Equal(2, stats.Contributors, "contributors");
        }

        [Test]
        public static void Export_WritesWorkbook(string dir)
        {
            var signer = new FakeSigner();
            var clock = new FakeClock();
            AliceSession(dir, signer, clock, "RD");
            var s = ChangeLogSession.Open(Opts(dir, RecorderTests.Alice, signer, clock));
            var path = Path.Combine(Path.GetTempPath(), "changelog-export-test.xlsx");
            using (var fs = File.Create(path))
                ChangeLogExporter.Export(fs, s.Project, s.Records.ToList(), s.Integrity, "All", System.DateTime.Now);
            Assert.True(new FileInfo(path).Length > 1000, "xlsx written to " + path);
            s.Close(false);
        }

        /// <summary>Verifier accepting either test key (HMAC verifiers are key-specific).</summary>
        private sealed class CombinedVerifier : ISignatureVerifier
        {
            private readonly FakeSigner[] _s;
            public CombinedVerifier(params FakeSigner[] s) { _s = s; }
            public bool Verify(string pk, byte[] data, byte[] sig) => _s.Any(x => x.Verify(pk, data, sig));
        }
    }
}
