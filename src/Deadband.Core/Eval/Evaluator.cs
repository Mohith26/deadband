using Deadband.Core.Alarms;
using Deadband.Core.Config;
using Deadband.Core.Detection;
using Deadband.Core.Tep;

namespace Deadband.Core.Eval;

public sealed record RunMetrics(
    string Run, int Fault, double Hours, int Annunciated, int RaisedTotal, int Suppressed,
    double AlarmsPer10Min, int Peak10Min, double FloodWindowShare,
    int NormalAnnunciated, double NormalHours, double? DetectionDelayMin, string? FirstAlarmTag);

public sealed record ConfigSummary(
    string Config, int Runs, double TotalHours, int Annunciated, double AlarmsPer10Min, int Peak10Min,
    double FloodWindowShare, int NormalAnnunciated, double NormalHours, double FalseAlarmsPerDay,
    int FaultsDetected, int FaultRuns, double? MedianDetectionDelayMin, IReadOnlyList<int> MissedFaults,
    double ExpectedChanceDetections, int DetectedWithin2h, double? MedianDelayWithin2hMin);

/// <summary>Replays TEP runs through an alarm configuration and measures operator alarm load and fault detection.</summary>
public static class Evaluator
{
    public const double OperatorWindowSec = 600; // ISA-18.2 / EEMUA 191 alarm-rate window (10 minutes)
    public const int FloodRate = 10;             // >= 10 alarms in 10 minutes = flood

    public static IReadOnlyList<string> TagIds => TepCatalog.Tags.Select(t => t.Id).ToList();

    public static RunMetrics Run(AlarmConfig cfg, PcaMonitor? pca, TepRun run, double horizonSamples = double.PositiveInfinity)
    {
        var engine = new AlarmEngine(cfg, TagIds, pca);
        double dt = cfg.SamplePeriodSeconds;
        var annTimes = new List<double>();
        int raised = 0, suppressed = 0;
        string? firstTag = null;
        double? delay = null;
        double onsetT = (run.FaultOnsetIndex ?? int.MaxValue) * dt;
        for (int i = 0; i < run.Length; i++)
        {
            double t = i * dt;
            foreach (var e in engine.Process(t, run.Samples[i]))
            {
                bool isAlarm = e.Type == AlarmEventType.Raised || e.Type == AlarmEventType.FloodStart;
                if (!isAlarm) continue;
                if (e.Type == AlarmEventType.Raised) { raised++; if (!e.Annunciated) suppressed++; }
                if (!e.Annunciated) continue;
                annTimes.Add(t);
                if (run.FaultOnsetIndex is int on && i >= on && delay is null && i - on <= horizonSamples)
                {
                    delay = (t - onsetT) / 60.0;
                    firstTag = e.Tag;
                }
            }
        }
        double hours = run.Length * dt / 3600;
        int normalSamples = run.FaultOnsetIndex ?? run.Length;
        int normalAnn = annTimes.Count(t => t < normalSamples * dt);
        // sliding 10-minute windows anchored at every sample
        int peak = 0, floodWindows = 0, windows = 0;
        for (int i = 0; i < run.Length; i++)
        {
            double start = i * dt, end = start + OperatorWindowSec;
            if (end > run.Length * dt) break;
            int c = annTimes.Count(x => x >= start && x < end);
            peak = Math.Max(peak, c); windows++;
            if (c >= FloodRate) floodWindows++;
        }
        return new RunMetrics(run.Name, run.Fault, hours, annTimes.Count, raised, suppressed,
            annTimes.Count / (hours * 6), peak, windows == 0 ? 0 : (double)floodWindows / windows,
            normalAnn, normalSamples * dt / 3600, delay, firstTag);
    }

    public static (ConfigSummary summary, List<RunMetrics> runs) Evaluate(string name, AlarmConfig cfg, PcaMonitor? pca, IEnumerable<TepRun> runs)
    {
        var rm = runs.Select(r => Run(cfg, pca, r)).ToList();
        return (Summarize(name, rm), rm);
    }

    public static ConfigSummary Summarize(string name, IReadOnlyList<RunMetrics> rm)
    {
        double hours = rm.Sum(r => r.Hours);
        int ann = rm.Sum(r => r.Annunciated);
        var faults = rm.Where(r => r.Fault > 0).ToList();
        var detected = faults.Where(r => r.DetectionDelayMin is not null).ToList();
        double normalHours = rm.Sum(r => r.NormalHours);
        int normalAnn = rm.Sum(r => r.NormalAnnunciated);
        double totalWindowsWeighted = rm.Sum(r => r.FloodWindowShare * r.Hours);
        return new ConfigSummary(name, rm.Count, hours, ann, ann / (hours * 6), rm.Count == 0 ? 0 : rm.Max(r => r.Peak10Min),
            hours == 0 ? 0 : totalWindowsWeighted / hours, normalAnn, normalHours, normalHours == 0 ? 0 : normalAnn / normalHours * 24,
            detected.Count, faults.Count, Median(detected.Select(r => r.DetectionDelayMin!.Value).ToList()),
            faults.Where(r => r.DetectionDelayMin is null).Select(r => r.Fault).ToList(),
            // how many of the detections a pure false-alarm process at the normal-operation rate would also have produced
            // by the observed delay: sum of 1 - exp(-rate * delay)
            Math.Round(detected.Sum(r => 1 - Math.Exp(-(normalHours == 0 ? 0 : normalAnn / normalHours) * r.DetectionDelayMin!.Value / 60)), 2),
            detected.Count(r => r.DetectionDelayMin <= 120),
            Median(detected.Where(r => r.DetectionDelayMin <= 120).Select(r => r.DetectionDelayMin!.Value).ToList()));
    }

    public static double? Median(List<double> xs)
    {
        if (xs.Count == 0) return null;
        xs.Sort();
        return xs.Count % 2 == 1 ? xs[xs.Count / 2] : (xs[xs.Count / 2 - 1] + xs[xs.Count / 2]) / 2;
    }
}
