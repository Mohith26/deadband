using Deadband.Core.Config;
using Deadband.Core.Detection;
using Deadband.Core.Oee;
using Deadband.Core.Tep;

namespace Deadband.Core.Eval;

/// <summary>
/// End-to-end experiment. Protocol: fit limits + PCA on d00.dat samples 0-299, tune the rationalization parameters on
/// the TRAINING files only (d00.dat samples 300-499 for false alarms, d01-d21.dat for detection), then report once on the
/// held-out TEST files (d00_te + d01_te..d21_te, 48 h each, fault at 8 h).
/// </summary>
public static class Benchmark
{
    public const int FitSamples = 300;

    public sealed record TuningRow(double SigmaK, int OnDelay, double Confidence, double VarianceToKeep, int ValidationFalseAlarms, int TrainFaultsDetected, double? TrainMedianDelayMin, double TrainAlarmsPer10Min);

    public static Dictionary<string, object?> Run(string dataDir, Action<string>? log = null)
    {
        log ??= _ => { };
        var d00 = TepRun.Load(Path.Combine(dataDir, "d00.dat"));
        var fit = d00.Samples.Take(FitSamples).ToList();
        var validNormal = d00.Slice(FitSamples, d00.Length - FitSamples, "d00[300:500]");
        var trainFaults = TepRun.LoadDirectory(dataDir, test: false).Where(r => r.Fault > 0).ToList();
        var test = TepRun.LoadDirectory(dataDir, test: true).ToList();
        if (trainFaults.Count != 21 || test.Count != 22) throw new FileNotFoundException($"expected 21 training fault files and 22 test files in {dataDir}");

        // 1) tune on training data only. False alarms: 5-fold blocked cross-validation over d00.dat (fit on 400 samples,
        //    replay the held-out 100), 25 h of normal operation in total. Detection: d01-d21.dat with the model fit on 0-299.
        var folds = Enumerable.Range(0, 5).Select(f =>
        {
            var hold = d00.Slice(f * 100, 100, $"d00[{f * 100}:{f * 100 + 100}]");
            var train = d00.Samples.Where((_, i) => i < f * 100 || i >= f * 100 + 100).ToList();
            return (hold, train);
        }).ToList();
        var pcaCache = new Dictionary<(int, double, double), PcaMonitor>();
        PcaMonitor Pca(int fold, IReadOnlyList<double[]> data, double conf, double var) =>
            pcaCache.TryGetValue((fold, conf, var), out var p) ? p : pcaCache[(fold, conf, var)] = PcaMonitor.Fit(data, var, conf);
        var grid = new List<(RationalizationParams p, TuningRow row)>();
        foreach (var k in new[] { 3.0, 4.0, 5.0 })
        foreach (var od in new[] { 1, 2, 3 })
        foreach (var conf in new[] { 0.99, 0.999 })
        foreach (var vk in new[] { 0.5, 0.7 })
        {
            var p = new RationalizationParams(SigmaK: k, OnDelay: od, Confidence: conf, VarianceToKeep: vk);
            int cvFalse = 0;
            for (int f = 0; f < folds.Count; f++)
            {
                var cfgF = ConfigFactory.Rationalized(folds[f].train, p);
                cvFalse += Evaluator.Run(cfgF, Pca(f, folds[f].train, conf, vk), folds[f].hold).Annunciated;
            }
            var cfg = ConfigFactory.Rationalized(fit, p);
            var tr = trainFaults.Select(r => Evaluator.Run(cfg, Pca(-1, fit, conf, vk), r)).ToList();
            var s = Evaluator.Summarize("train", tr);
            grid.Add((p, new TuningRow(k, od, conf, vk, cvFalse, s.FaultsDetected, s.MedianDetectionDelayMin, s.AlarmsPer10Min)));
        }
        // selection rule (fixed before looking at test data): fewest cross-validated false alarms, then most training faults
        // detected, then fastest median detection, then lowest alarm rate
        var best = grid.OrderBy(g => g.row.ValidationFalseAlarms).ThenByDescending(g => g.row.TrainFaultsDetected)
            .ThenBy(g => g.row.TrainMedianDelayMin ?? double.MaxValue).ThenBy(g => g.row.TrainAlarmsPer10Min).First();
        log($"selected k={best.p.SigmaK} onDelay={best.p.OnDelay} conf={best.p.Confidence} var={best.p.VarianceToKeep}");
        // refit on all normal training data with the selected hyperparameters
        fit = d00.Samples.ToList();

        // 2) held-out test evaluation, layered so each mechanism's contribution is visible
        var pcaBest = PcaMonitor.Fit(fit, best.p.VarianceToKeep, best.p.Confidence);
        var raw = ConfigFactory.Raw(fit);
        var layers = new (string key, string label, AlarmConfig cfg)[]
        {
            ("raw", "as-found: 3-sigma on all 52 tags, no deadband/delay", raw),
            ("L1_limits", "+ rationalized limits, deadband, on/off delay (22 continuous tags + operating limits)",
                ConfigFactory.Rationalized(fit, best.p with { Multivariate = false, Chatter = false, Flood = false })),
            ("L2_pca", "+ multivariate PROCESS.DEVIATION alarm (PCA T2/SPE)",
                ConfigFactory.Rationalized(fit, best.p with { Chatter = false, Flood = false })),
            ("L3_chatter", "+ chattering-alarm suppression", ConfigFactory.Rationalized(fit, best.p with { Flood = false })),
            ("rationalized", "+ flood detection with priority-based suppression (full)", ConfigFactory.Rationalized(fit, best.p)),
        };
        var summaries = new Dictionary<string, object?>();
        var perRun = new Dictionary<string, object?>();
        foreach (var (key, label, cfg) in layers)
        {
            var issues = ConfigValidator.Validate(cfg);
            if (issues.Count > 0) throw new InvalidOperationException($"{key} config invalid: {issues[0].Message}");
            var (s, rm) = Evaluator.Evaluate(label, cfg, cfg.Multivariate.Enabled ? pcaBest : null, test);
            summaries[key] = s;
            if (key is "raw" or "rationalized") perRun[key] = rm;
            log($"{key,-13} alarms/10min={s.AlarmsPer10Min:0.00} peak={s.Peak10Min} normalFA={s.NormalAnnunciated} detected={s.FaultsDetected}/{s.FaultRuns} median={s.MedianDetectionDelayMin:0.#}min");
        }

        // PCA alone (no per-tag alarms), the textbook monitor, as a detection reference
        var pcaOnlyCfg = ConfigFactory.Rationalized(fit, best.p with { Chatter = false, Flood = false });
        pcaOnlyCfg = pcaOnlyCfg with { Tags = pcaOnlyCfg.Tags.Select(t => t with { Enabled = false, Rationale = t.Rationale }).ToList(), Name = "PCA only" };
        summaries["pca_only_reference"] = Evaluator.Evaluate("PCA T2/SPE only (detection reference)", pcaOnlyCfg, pcaBest, test).summary;

        // 3) OEE per test run (continuous-process adaptation)
        var oeeCfg = layers[^1].cfg.Oee;
        var oee = test.Select(r =>
        {
            var full = OeeCalculator.Compute(oeeCfg, Evaluator.TagIds, r.Samples);
            var post = OeeCalculator.Compute(oeeCfg, Evaluator.TagIds, r.Samples.Skip(r.FaultOnsetIndex ?? 0));
            return new { run = r.Name, fault = r.Fault, oee = Math.Round(full.Oee, 4), availability = Math.Round(full.Availability, 4), performance = Math.Round(full.Performance, 4), quality = Math.Round(full.Quality, 4), postOnsetOee = Math.Round(post.Oee, 4) };
        }).ToList();

        var rawS = (ConfigSummary)summaries["raw"]!;
        var ratS = (ConfigSummary)summaries["rationalized"]!;
        var headline = new
        {
            rawAlarmsPer10Min = Math.Round(rawS.AlarmsPer10Min, 2),
            rationalizedAlarmsPer10Min = Math.Round(ratS.AlarmsPer10Min, 2),
            alarmRateReductionPct = Math.Round(100 * (1 - ratS.AlarmsPer10Min / rawS.AlarmsPer10Min), 1),
            rawPeak10Min = rawS.Peak10Min, rationalizedPeak10Min = ratS.Peak10Min,
            rawFloodWindowPct = Math.Round(100 * rawS.FloodWindowShare, 2), rationalizedFloodWindowPct = Math.Round(100 * ratS.FloodWindowShare, 2),
            normalOperationHours = ratS.NormalHours,
            rawNormalFalseAlarms = rawS.NormalAnnunciated, rationalizedNormalFalseAlarms = ratS.NormalAnnunciated,
            falseAlarmReductionPct = Math.Round(100 * (1 - (double)ratS.NormalAnnunciated / rawS.NormalAnnunciated), 1),
            faultRuns = ratS.FaultRuns,
            rawDetectedWithin2h = rawS.DetectedWithin2h, rationalizedDetectedWithin2h = ratS.DetectedWithin2h,
            rawMedianDelayWithin2hMin = rawS.MedianDelayWithin2hMin, rationalizedMedianDelayWithin2hMin = ratS.MedianDelayWithin2hMin,
            limitsOnlyDetectedWithin2h = ((ConfigSummary)summaries["L1_limits"]!).DetectedWithin2h,
        };

        return new Dictionary<string, object?>
        {
            ["headline"] = headline,
            ["dataset"] = new
            {
                name = "Tennessee Eastman Process (Downs and Vogel 1993), Braatz et al. closed-loop data",
                source = "https://github.com/camaramm/tennessee-eastman-profBraatz",
                samplePeriodSec = TepCatalog.SamplePeriodSec,
                tags = TepCatalog.TagCount,
                fitSamples = "tuning: d00.dat samples 0-299 (15 h normal); final model refit on all 500 d00.dat samples",
                validationNormal = "5-fold blocked cross-validation over d00.dat (25 h normal)",
                trainingFaultRuns = trainFaults.Count,
                testRuns = test.Count,
                testHoursPerRun = test[0].Length * TepCatalog.SamplePeriodSec / 3600,
                testFaultOnsetHour = TepCatalog.TestFaultOnsetIndex * TepCatalog.SamplePeriodSec / 3600,
            },
            ["assumptions"] = new[]
            {
                "one console operator for the whole TEP plant (alarm rates are per operator)",
                "an alarm counts toward load when annunciated: a raise not suppressed by shelving, chatter or flood logic, plus one consolidated flood alarm per flood",
                "a fault is detected when any annunciated alarm occurs between onset and the end of the run; delay is measured from the onset sample",
                "normal-operation false alarms = annunciated alarms during d00_te (48 h) plus the 8 h before onset in each of the 21 fault runs",
                "tuning used training files only; test files were evaluated once with the selected parameters",
            },
            ["selected"] = new { best.p.SigmaK, best.p.OnDelay, best.p.Confidence, best.p.VarianceToKeep, ewmaLambda = 0.2, best.p.DeadbandSigma, best.p.OffDelay, pcaComponents = pcaBest.Components, pcaBest.T2Limit, pcaBest.SpeLimit },
            ["tuning"] = grid.Select(g => g.row).ToList(),
            ["test"] = summaries,
            ["perRun"] = perRun,
            ["oee"] = oee,
        };
    }
}
