using Deadband.Core.Alarms;
using Deadband.Core.Config;
using Deadband.Core.Detection;
using Deadband.Core.Eval;
using Deadband.Core.Oee;
using Deadband.Core.Tep;

namespace Deadband.Core.Tests;

internal static class Repo
{
    public static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Deadband.sln"))) d = d.Parent;
            return d?.FullName ?? throw new DirectoryNotFoundException("repo root not found");
        }
    }
    public static string Tep => Path.Combine(Root, "data", "tep");
}

public class TepAndPcaTests
{
    [Fact]
    public void LoadsTransposedTrainingFileAndTestFile()
    {
        var d00 = TepRun.Load(Path.Combine(Repo.Tep, "d00.dat"));
        Assert.Equal(500, d00.Length);
        Assert.All(d00.Samples, r => Assert.Equal(52, r.Length));
        Assert.Null(d00.FaultOnsetIndex);
        var d06 = TepRun.Load(Path.Combine(Repo.Tep, "d06_te.dat"));
        Assert.Equal(960, d06.Length);
        Assert.Equal(6, d06.Fault);
        Assert.Equal(160, d06.FaultOnsetIndex);
        Assert.Equal(52, TepCatalog.Tags.Count);
        Assert.Equal("XMEAS07", TepCatalog.Tags[6].Id);
    }

    [Fact]
    public void PcaSeparatesNormalFromFault()
    {
        var normal = TepRun.Load(Path.Combine(Repo.Tep, "d00.dat")).Samples;
        var pca = PcaMonitor.Fit(normal, 0.5, 0.99);
        Assert.InRange(pca.Components, 2, 30);
        var te = TepRun.Load(Path.Combine(Repo.Tep, "d00_te.dat"));
        double normalRate = te.Samples.Count(x => pca.Score(x).Index > 1) / (double)te.Length;
        var f6 = TepRun.Load(Path.Combine(Repo.Tep, "d06_te.dat"));
        double faultRate = f6.Samples.Skip(160).Count(x => pca.Score(x).Index > 1) / (double)(f6.Length - 160);
        Assert.True(normalRate < 0.15, $"normal exceedance {normalRate:P1}");
        Assert.True(faultRate > 0.95, $"fault exceedance {faultRate:P1}");
    }

    [Fact]
    public void PcaOnSyntheticCorrelatedData()
    {
        var rng = new Random(7);
        var data = Enumerable.Range(0, 400).Select(_ => { double a = Gauss(rng); return new[] { a, a + 0.05 * Gauss(rng), -a + 0.05 * Gauss(rng), Gauss(rng) }; }).ToList();
        var pca = PcaMonitor.Fit(data, 0.7, 0.99);
        // a sample that breaks the x0 ~ x1 correlation shows up in SPE even though each value is in range
        var broken = pca.Score(new[] { 2.0, -2.0, -2.0, 0.0 });
        Assert.True(broken.Spe > pca.SpeLimit);
        Assert.Contains(broken.TopContributors, i => i is 0 or 1);
    }

    private static double Gauss(Random r) => Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble());
}

public class OeeTests
{
    private static readonly string[] Ids = { "FLOW", "G", "H" };
    private static readonly OeeConfig Cfg = new() { ProductFlowTag = "FLOW", DesignRate = 10, RunningThreshold = 0.5, QualityTags = new[] { "G", "H" }, QualityTargets = new[] { 50.0, 40.0 }, QualityTolerance = 1 };

    [Fact]
    public void AvailabilityPerformanceQuality()
    {
        var samples = new List<double[]>
        {
            new[] { 10.0, 50, 40 }, new[] { 8.0, 50, 40 }, new[] { 10.0, 53, 40 }, new[] { 2.0, 50, 40 },
        };
        var r = OeeCalculator.Compute(Cfg, Ids, samples);
        Assert.Equal(0.75, r.Availability, 6);           // one stopped sample (2 < 5)
        Assert.Equal(28.0 / 30.0, r.Performance, 6);     // (10+8+10)/(10*3)
        Assert.Equal(2.0 / 3.0, r.Quality, 6);           // G off-spec in one running sample
        Assert.Equal(0.75 * 28.0 / 30.0 * 2.0 / 3.0, r.Oee, 6);
    }

    [Fact]
    public void PerformanceIsCappedAtDesignRate()
    {
        var r = OeeCalculator.Compute(Cfg, Ids, new[] { new[] { 15.0, 50, 40 } });
        Assert.Equal(1.0, r.Performance, 6);
    }
}

public class EvaluatorTests
{
    [Fact]
    public void RationalizedConfigCutsLoadOnRealNormalDataAndStillCatchesAFeedLoss()
    {
        var normal = TepRun.Load(Path.Combine(Repo.Tep, "d00.dat")).Samples;
        var p = new RationalizationParams(SigmaK: 4, OnDelay: 2, Confidence: 0.99, VarianceToKeep: 0.5);
        var raw = ConfigFactory.Raw(normal);
        var rat = ConfigFactory.Rationalized(normal, p);
        Assert.Empty(ConfigValidator.Validate(raw));
        Assert.Empty(ConfigValidator.Validate(rat));
        var pca = PcaMonitor.Fit(normal, p.VarianceToKeep, p.Confidence);
        var te = TepRun.Load(Path.Combine(Repo.Tep, "d00_te.dat"));
        var rawRun = Evaluator.Run(raw, null, te);
        var ratRun = Evaluator.Run(rat, pca, te);
        Assert.True(ratRun.Annunciated * 5 < rawRun.Annunciated, $"raw {rawRun.Annunciated} vs rationalized {ratRun.Annunciated}");

        var f6 = Evaluator.Run(rat, pca, TepRun.Load(Path.Combine(Repo.Tep, "d06_te.dat")));
        Assert.NotNull(f6.DetectionDelayMin);
        Assert.True(f6.DetectionDelayMin <= 30);
    }

    [Fact]
    public void SummaryCountsDetectionsAndMedian()
    {
        var runs = new List<RunMetrics>
        {
            new("a", 1, 48, 10, 10, 0, 0.03, 2, 0, 1, 8, 6, null),
            new("b", 2, 48, 10, 10, 0, 0.03, 2, 0, 0, 8, 30, null),
            new("c", 3, 48, 10, 10, 0, 0.03, 2, 0, 0, 8, null, null),
            new("n", 0, 48, 2, 2, 0, 0.007, 1, 0, 2, 48, null, null),
        };
        var s = Evaluator.Summarize("x", runs);
        Assert.Equal(2, s.FaultsDetected);
        Assert.Equal(3, s.FaultRuns);
        Assert.Equal(18, s.MedianDetectionDelayMin);
        Assert.Equal(new[] { 3 }, s.MissedFaults);
        Assert.Equal(3, s.NormalAnnunciated);
        Assert.Equal(2, s.DetectedWithin2h);
    }

    [Fact]
    public void DeviationAlarmNamesContributingTags()
    {
        var normal = TepRun.Load(Path.Combine(Repo.Tep, "d00.dat")).Samples;
        var cfg = ConfigFactory.Rationalized(normal, new RationalizationParams(Confidence: 0.99, VarianceToKeep: 0.5));
        var engine = new AlarmEngine(cfg, Evaluator.TagIds, PcaMonitor.Fit(normal, 0.5, 0.99));
        var f6 = TepRun.Load(Path.Combine(Repo.Tep, "d06_te.dat"));
        var events = Enumerable.Range(0, 260).SelectMany(i => engine.Process(i * 180, f6.Samples[i])).ToList();
        var dev = events.First(e => e.Tag == AlarmEngine.DeviationTag && e.Type == AlarmEventType.Raised);
        Assert.StartsWith("top deviating tags:", dev.Detail);
        Assert.True(dev.TimeSec >= 160 * 180);
    }
}
