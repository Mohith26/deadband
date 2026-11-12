using Deadband.Core.Alarms;
using Deadband.Core.Config;

namespace Deadband.Core.Tests;

public class AlarmEngineTests
{
    private static readonly string[] Ids = { "T1", "T2", "T3" };

    private static AlarmConfig Cfg(TagAlarmConfig t1, ChatterConfig? chatter = null, FloodConfig? flood = null, params TagAlarmConfig[] more) => new()
    {
        SamplePeriodSeconds = 60,
        Tags = new[] { t1 }.Concat(more).ToList(),
        Chatter = chatter ?? new ChatterConfig(),
        Flood = flood ?? new FloodConfig(),
    };

    private static TagAlarmConfig Tag(string id = "T1", double? hi = 10, double? lo = null, double db = 0, int on = 0, int off = 0,
        AlarmPriority pr = AlarmPriority.Medium, double? hiHi = null, double? loLo = null) =>
        new() { Tag = id, Hi = hi, Lo = lo, HiHi = hiHi, LoLo = loLo, Deadband = db, OnDelaySamples = on, OffDelaySamples = off, Priority = pr, Rationale = "test" };

    private static List<AlarmEvent> Feed(AlarmEngine e, params double[] t1Values)
    {
        var all = new List<AlarmEvent>();
        for (int i = 0; i < t1Values.Length; i++) all.AddRange(e.Process(i * 60, new[] { t1Values[i], 0.0, 0.0 }));
        return all;
    }

    private static int Raises(IEnumerable<AlarmEvent> ev) => ev.Count(e => e.Type == AlarmEventType.Raised);

    [Fact]
    public void HiAlarmRaisesAndClears()
    {
        var e = new AlarmEngine(Cfg(Tag()), Ids);
        var ev = Feed(e, 5, 11, 12, 9);
        Assert.Equal(1, Raises(ev));
        Assert.Contains(ev, x => x.Type == AlarmEventType.Cleared && x.TimeSec == 180);
        Assert.Empty(e.ActiveAlarms());
    }

    [Fact]
    public void OnDelayFiltersShortSpikes()
    {
        var e = new AlarmEngine(Cfg(Tag(on: 3)), Ids);
        Assert.Equal(0, Raises(Feed(e, 11, 11, 5, 11, 5)));
        var e2 = new AlarmEngine(Cfg(Tag(on: 3)), Ids);
        var ev = Feed(e2, 11, 11, 11);
        Assert.Equal(1, Raises(ev));
        Assert.Equal(120, ev.Single(x => x.Type == AlarmEventType.Raised).TimeSec);
    }

    [Fact]
    public void DeadbandStopsChatterAroundTheLimit()
    {
        var noisy = new double[] { 10.2, 9.8, 10.3, 9.7, 10.1, 9.9, 10.4, 9.6 };
        Assert.Equal(4, Raises(Feed(new AlarmEngine(Cfg(Tag(db: 0)), Ids), noisy)));
        Assert.Equal(1, Raises(Feed(new AlarmEngine(Cfg(Tag(db: 0.5)), Ids), noisy)));
    }

    [Fact]
    public void OffDelayHoldsAlarmUntilClearIsSustained()
    {
        var e = new AlarmEngine(Cfg(Tag(off: 2)), Ids);
        var ev = Feed(e, 11, 9, 11, 9, 9);
        Assert.Equal(1, Raises(ev));
        Assert.Single(ev, x => x.Type == AlarmEventType.Cleared);
        Assert.Equal(240, ev.Single(x => x.Type == AlarmEventType.Cleared).TimeSec);
    }

    [Fact]
    public void HiHiAndLoLoAreOnePriorityHigher()
    {
        var e = new AlarmEngine(Cfg(Tag(hi: 10, hiHi: 20, lo: 0, loLo: -10)), Ids);
        var ev = Feed(e, 25, 5, -15);
        var raised = ev.Where(x => x.Type == AlarmEventType.Raised).ToList();
        Assert.Equal(AlarmPriority.High, raised.Single(x => x.Kind == AlarmKind.HiHi).Priority);
        Assert.Equal(AlarmPriority.Medium, raised.Single(x => x.Kind == AlarmKind.Hi).Priority);
        Assert.Equal(AlarmPriority.High, raised.Single(x => x.Kind == AlarmKind.LoLo).Priority);
    }

    [Fact]
    public void ChatteringTagIsSuppressedThenRestored()
    {
        var chatter = new ChatterConfig { Enabled = true, MaxRaises = 3, WindowSeconds = 600, HoldSeconds = 600 };
        var e = new AlarmEngine(Cfg(Tag(), chatter), Ids);
        var ev = Feed(e, 11, 9, 11, 9, 11, 9, 11, 9);
        var raised = ev.Where(x => x.Type == AlarmEventType.Raised).ToList();
        Assert.Equal(4, raised.Count);
        Assert.Equal(2, raised.Count(x => x.Annunciated));
        Assert.All(raised.Skip(2), x => Assert.Equal("chatter", x.SuppressedBy));
        Assert.Single(ev, x => x.Type == AlarmEventType.ChatterSuppressed);
        // after the hold expires, the tag annunciates again
        var later = e.Process(2000, new[] { 9.0, 0, 0 }).Concat(e.Process(2060, new[] { 11.0, 0, 0 })).ToList();
        Assert.True(later.Single(x => x.Type == AlarmEventType.Raised).Annunciated);
    }

    [Fact]
    public void ShelvingSuppressesAndExpiresWithAudit()
    {
        var e = new AlarmEngine(Cfg(Tag()), Ids);
        e.Process(0, new[] { 5.0, 0, 0 });
        var shelve = e.Shelve("T1", 120, "op1", "transmitter being calibrated");
        Assert.Equal("op1", shelve.User);
        var ev = e.Process(60, new[] { 11.0, 0, 0 });
        Assert.Equal("shelved", ev.Single(x => x.Type == AlarmEventType.Raised).SuppressedBy);
        var expire = e.Process(120, new[] { 11.0, 0, 0 });
        Assert.Contains(expire, x => x.Type == AlarmEventType.Unshelved && x.User == "system");
        Assert.Throws<ArgumentException>(() => e.Shelve("T1", 60, "op1", ""));
        Assert.Throws<ArgumentOutOfRangeException>(() => e.Shelve("T1", 13 * 3600, "op1", "too long"));
        Assert.Throws<KeyNotFoundException>(() => e.Shelve("NOPE", 60, "op1", "x"));
    }

    [Fact]
    public void FloodAnnunciatesOnceAndSuppressesLowPriority()
    {
        var flood = new FloodConfig { Enabled = true, Threshold = 3, WindowSeconds = 600, MinPriorityDuringFlood = AlarmPriority.High };
        var cfg = Cfg(Tag("T1"), null, flood, Tag("T2"), Tag("T3", pr: AlarmPriority.High));
        var e = new AlarmEngine(cfg, Ids);
        var ev = e.Process(0, new[] { 11.0, 11.0, 11.0 }); // three raises in one sample -> flood starts
        var start = ev.Single(x => x.Type == AlarmEventType.FloodStart);
        Assert.Contains("first-out T1", start.Detail);
        Assert.True(e.InFlood);
        e.Process(60, new[] { 5.0, 5.0, 5.0 });
        var during = e.Process(120, new[] { 11.0, 11.0, 11.0 });
        Assert.Equal("flood", during.Single(x => x.Tag == "T1" && x.Type == AlarmEventType.Raised).SuppressedBy);
        Assert.True(during.Single(x => x.Tag == "T3" && x.Type == AlarmEventType.Raised).Annunciated);
        var end = e.Process(1300, new[] { 5.0, 5.0, 5.0 });
        Assert.Contains(end, x => x.Type == AlarmEventType.FloodEnd);
        Assert.False(e.InFlood);
    }

    [Fact]
    public void AcknowledgeMarksAlarmAndRejectsUnknown()
    {
        var e = new AlarmEngine(Cfg(Tag()), Ids);
        e.Process(0, new[] { 11.0, 0, 0 });
        var ack = e.Acknowledge("T1", AlarmKind.Hi, "op2");
        Assert.Equal(AlarmEventType.Acknowledged, ack.Type);
        Assert.True(e.ActiveAlarms().Single().Acknowledged);
        Assert.Throws<KeyNotFoundException>(() => e.Acknowledge("T1", AlarmKind.Lo, "op2"));
    }

    [Fact]
    public void DisabledTagsNeverAlarm()
    {
        var t = Tag() with { Enabled = false };
        Assert.Equal(0, Raises(Feed(new AlarmEngine(Cfg(t), Ids), 50, 50)));
    }
}
