using Deadband.Core.Config;

namespace Deadband.Core.Tests;

public class ConfigTests
{
    internal static AlarmConfig Valid() => new()
    {
        Name = "unit",
        Tags = new()
        {
            new TagAlarmConfig { Tag = "XMEAS07", Hi = 2750, Lo = 2650, HiHi = 2895, Deadband = 5, OnDelaySamples = 2, Rationale = "reactor pressure" },
            new TagAlarmConfig { Tag = "XMEAS09", Hi = 121, Lo = 120, Deadband = 0.1, Rationale = "reactor temperature" },
        },
    };

    private static readonly HashSet<string> Known = new() { "XMEAS07", "XMEAS09", "XMEAS12" };

    [Fact]
    public void ValidConfigHasNoIssues() => Assert.Empty(ConfigValidator.Validate(Valid(), Known));

    public static IEnumerable<object[]> Mutations() => new List<object[]>
    {
        new object[] { "DUPLICATE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = c.Tags.Append(c.Tags[0]).ToList() }) },
        new object[] { "LIMIT_ORDER", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { c.Tags[0] with { Lo = 2800 } } }) },
        new object[] { "LIMIT_ORDER", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { c.Tags[0] with { HiHi = 2700 } } }) },
        new object[] { "DEADBAND_TOO_WIDE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { c.Tags[0] with { Deadband = 60 } } }) },
        new object[] { "RANGE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { c.Tags[0] with { Deadband = -1 } } }) },
        new object[] { "RANGE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { c.Tags[0] with { OnDelaySamples = -1 } } }) },
        new object[] { "UNKNOWN_TAG", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { c.Tags[0] with { Tag = "XMEAS99" } } }) },
        new object[] { "UNRATIONALIZED", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { c.Tags[0] with { Rationale = "" } } }) },
        new object[] { "NO_LIMITS", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { new TagAlarmConfig { Tag = "XMEAS12", Rationale = "x" } } }) },
        new object[] { "REQUIRED", (Func<AlarmConfig, AlarmConfig>)(c => c with { Tags = new() { new TagAlarmConfig { Tag = " " } } }) },
        new object[] { "RANGE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Multivariate = new MultivariateConfig { Enabled = true, Confidence = 1.2 } }) },
        new object[] { "RANGE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Multivariate = new MultivariateConfig { Enabled = true, EwmaLambda = 0 } }) },
        new object[] { "RANGE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Flood = new FloodConfig { Enabled = true, Threshold = 1 } }) },
        new object[] { "RANGE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Chatter = new ChatterConfig { Enabled = true, MaxRaises = 1 } }) },
        new object[] { "SCHEMA", (Func<AlarmConfig, AlarmConfig>)(c => c with { SchemaVersion = 2 }) },
        new object[] { "RANGE", (Func<AlarmConfig, AlarmConfig>)(c => c with { SamplePeriodSeconds = 0 }) },
        new object[] { "SHAPE", (Func<AlarmConfig, AlarmConfig>)(c => c with { Oee = new OeeConfig { QualityTargets = new[] { 1.0 } } }) },
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public void ValidatorCatchesSeededDefect(string code, Func<AlarmConfig, AlarmConfig> mutate)
    {
        var issues = ConfigValidator.Validate(mutate(Valid()), Known);
        Assert.Contains(issues, i => i.Code == code);
    }

    [Fact]
    public void DiffIgnoresTagOrderAndReportsFieldChanges()
    {
        var a = Valid();
        var reordered = a with { Tags = a.Tags.AsEnumerable().Reverse().ToList() };
        Assert.True(ConfigDiff.Compute(a, reordered).IsEmpty);

        var b = a with
        {
            Tags = new() { a.Tags[0] with { Hi = 2760 }, new TagAlarmConfig { Tag = "XMEAS12", Lo = 30, Rationale = "sep level" } },
            Flood = a.Flood with { Threshold = 12 },
        };
        var d = ConfigDiff.Compute(a, b);
        Assert.Equal(new[] { "XMEAS12" }, d.AddedTags);
        Assert.Equal(new[] { "XMEAS09" }, d.RemovedTags);
        Assert.Contains(d.Changes, c => c.Path == "tags[XMEAS07].hi" && c.Before == "2750" && c.After == "2760");
        Assert.Contains(d.Changes, c => c.Path == "flood.threshold");
    }

    [Fact]
    public void JsonRoundTripIsLossless()
    {
        var a = Valid();
        var b = AlarmConfig.FromJson(a.ToJson());
        Assert.True(ConfigDiff.Compute(a, b).IsEmpty);
    }
}

public class ConfigStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deadband-store-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void PublishRollbackAndReload()
    {
        var store = new ConfigStore(_dir);
        var v1 = store.Publish(ConfigTests.Valid(), "alice", "initial");
        var changed = ConfigTests.Valid() with { Name = "tightened" };
        var v2 = store.Publish(changed, "bob", "tighten");
        Assert.Equal(2, store.ActiveVersion);
        var v3 = store.Rollback(1, "alice", "bad change");
        Assert.Equal(3, v3.Version);
        Assert.Equal(1, v3.RollbackOf);
        Assert.Equal(v1.Sha256, v3.Sha256);
        Assert.True(ConfigDiff.Compute(store.Get(1), store.GetActive()).IsEmpty);

        var reopened = new ConfigStore(_dir);
        Assert.Equal(3, reopened.ActiveVersion);
        Assert.Equal(new[] { 1, 2, 3 }, reopened.Versions.Select(v => v.Version));
        Assert.Equal("tightened", reopened.Get(v2.Version).Name);
    }

    [Fact]
    public void InvalidConfigIsRejectedAndNothingIsWritten()
    {
        var store = new ConfigStore(_dir);
        var bad = ConfigTests.Valid() with { Tags = new() { ConfigTests.Valid().Tags[0] with { Lo = 9999 } } };
        var ex = Assert.Throws<ConfigValidationException>(() => store.Publish(bad, "eve", "oops"));
        Assert.Contains(ex.Issues, i => i.Code == "LIMIT_ORDER");
        Assert.Empty(store.Versions);
        Assert.Empty(Directory.GetFiles(_dir, "v*.json"));
    }

    [Fact]
    public void TamperedVersionFileFailsIntegrityCheck()
    {
        var store = new ConfigStore(_dir);
        store.Publish(ConfigTests.Valid(), "alice", "initial");
        File.WriteAllText(Path.Combine(_dir, "v0001.json"), ConfigTests.Valid().ToJson().Replace("2750", "9999"));
        Assert.Throws<InvalidDataException>(() => store.Get(1));
    }

    [Fact]
    public void ActivationEventFiresOnPublish()
    {
        var store = new ConfigStore(_dir);
        int seen = 0;
        store.Activated += (v, _) => seen = v;
        store.Publish(ConfigTests.Valid(), "alice", "initial");
        Assert.Equal(1, seen);
    }
}
