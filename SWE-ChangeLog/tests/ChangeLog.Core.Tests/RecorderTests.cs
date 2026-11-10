using System.Linq;
using ChangeLog.Core.Model;
using ChangeLog.Core.Recording;

namespace ChangeLog.Core.Tests
{
    public static class RecorderTests
    {
        internal static readonly UserIdentity Alice = new UserIdentity("Alice Martin", @"CORP\amartin", "WS-01");
        internal static readonly UserIdentity Bob = new UserIdentity("Bob Chen", @"CORP\bchen", "WS-02");

        internal static ObjectState Page(int id, string tag) =>
            new ObjectState { Kind = ObjectKind.Page, Id = id, Label = tag }.Set("Tag", tag);

        internal static ObjectState Wire(int id, string tag, string color = "BK") =>
            new ObjectState { Kind = ObjectKind.Wire, Id = id, Label = tag }.Set("Tag", tag).Set("Color", color);

        internal static ObjectState Symbol(int id, int page, string comp, double x, double y) =>
            new ObjectState { Kind = ObjectKind.Symbol, Id = id, Label = comp, PageId = page, Geometry = Geometry.Rect(x, y, 10, 20, 0) }
                .Set("Component", comp);

        private static ChangeRecorder NewRecorder(FakeClock clock, params ObjectState[] state)
        {
            var r = new ChangeRecorder(Alice, clock, new RecorderOptions());
            r.LoadState(state, true);
            return r;
        }

        private static RawEvent Ev(FakeClock c, RawEventType t, ObjectState s) => new RawEvent(t, s.Key, s, c.UtcNow);

        [Test]
        public static void AddThenUpdate_IsOneAdded()
        {
            var c = new FakeClock();
            var r = NewRecorder(c, Page(1, "01"));
            r.Observe(Ev(c, RawEventType.Add, Symbol(10, 1, "-K1", 0, 0)));
            r.Observe(Ev(c, RawEventType.Update, Symbol(10, 1, "-K1", 5, 5)));
            Assert.True(!r.IsDue(c.UtcNow), "not due inside quiet window");
            c.Advance(2);
            Assert.True(r.IsDue(c.UtcNow), "due after quiet window");
            var recs = r.Flush();
            Assert.Equal(1, recs.Count, "one record");
            Assert.Equal(ChangeAction.Added, recs[0].Action, "added");
            Assert.Equal("01", recs[0].PageLabel, "page label resolved");
            Assert.True(recs[0].Fields.Any(f => f.Field == ObjectState.GeometryField && f.After.Contains("5")), "final position");
            Assert.Equal(null, recs[0].BatchId, "single record has no batch");
        }

        [Test]
        public static void NoopUpdate_IsSkipped()
        {
            var c = new FakeClock();
            var r = NewRecorder(c, Wire(5, "W1"));
            r.Observe(Ev(c, RawEventType.Update, Wire(5, "W1")));
            c.Advance(2);
            Assert.Equal(0, r.Flush().Count, "SWE no-op updates are dropped");
        }

        [Test]
        public static void AddThenDelete_IsTransient()
        {
            var c = new FakeClock();
            var r = NewRecorder(c);
            var s = Wire(7, "W7");
            r.Observe(Ev(c, RawEventType.Add, s));
            r.Observe(new RawEvent(RawEventType.Delete, s.Key, null, c.UtcNow));
            c.Advance(2);
            Assert.Equal(0, r.Flush().Count, "transient object not logged");
        }

        [Test]
        public static void Delete_UsesCachedLabelAndPage()
        {
            var c = new FakeClock();
            var sym = Symbol(10, 1, "-K1", 0, 0);
            var r = NewRecorder(c, Page(1, "05 Power"), sym);
            r.Observe(new RawEvent(RawEventType.Delete, sym.Key, null, c.UtcNow));
            c.Advance(2);
            var recs = r.Flush();
            Assert.Equal(ChangeAction.Removed, recs[0].Action, "removed");
            Assert.Equal("-K1", recs[0].ObjectLabel, "label from cache though object unreadable");
            Assert.Equal("05 Power", recs[0].PageLabel, "page from cache");
            Assert.Equal(0, r.LiveObjects.Count(o => o.Kind == ObjectKind.Symbol), "removed from index");
        }

        [Test]
        public static void Edit_ProducesFieldDiff_AndBurstSharesBatch()
        {
            var c = new FakeClock();
            var r = NewRecorder(c, Wire(1, "W1", "BK"), Wire(2, "W2", "BK"));
            r.Observe(Ev(c, RawEventType.Update, Wire(1, "W1", "RD")));
            r.Observe(Ev(c, RawEventType.Update, Wire(2, "W20", "BK")));
            c.Advance(2);
            var recs = r.Flush();
            Assert.Equal(2, recs.Count, "two edits");
            var w1 = recs.Single(x => x.ObjectId == 1);
            Assert.Equal("Color: BK → RD", w1.FieldSummary, "color diff");
            Assert.True(recs[0].BatchId != null && recs[0].BatchId == recs[1].BatchId, "same batch");
            Assert.Equal(Attribution.Confirmed, w1.Attribution, "live = confirmed");
            Assert.Equal("Alice Martin", w1.User, "attributed to session user");
        }

        [Test]
        public static void ExternalRecord_PreventsMisattribution()
        {
            var c = new FakeClock();
            var r = NewRecorder(c, Wire(1, "W1", "BK"));
            // Bob changed the color; his record arrives through the shared journal.
            r.ApplyExternal(new ChangeRecord { Action = ChangeAction.Edited, Kind = ObjectKind.Wire, ObjectId = 1, After = Wire(1, "W1", "RD") });
            // SWE then notifies Alice's client with the already-changed object.
            r.Observe(Ev(c, RawEventType.Update, Wire(1, "W1", "RD")));
            c.Advance(2);
            Assert.Equal(0, r.Flush().Count, "Bob's change must not be logged as Alice's");
        }

        [Test]
        public static void SilentKinds_TrackedButNotLogged()
        {
            var c = new FakeClock();
            var r = NewRecorder(c);
            var seg = new ObjectState { Kind = ObjectKind.WireSegment, Id = 3, PageId = 1, Geometry = Geometry.Segment(0, 0, 10, 0) };
            r.Observe(Ev(c, RawEventType.Add, seg));
            c.Advance(2);
            Assert.Equal(0, r.Flush().Count, "segments not logged by default");
            Assert.Equal(1, r.LiveObjects.Count(), "but tracked for visual compare");
        }

        [Test]
        public static void MaxLatency_FlushesDuringContinuousActivity()
        {
            var c = new FakeClock();
            var r = NewRecorder(c);
            for (var i = 0; i < 30; i++)
            {
                r.Observe(Ev(c, RawEventType.Add, Wire(100 + i, "W" + i)));
                c.Advance(0.5);
            }

            Assert.True(r.IsDue(c.UtcNow), "bulk operation flushed within max latency");
        }
    }
}
