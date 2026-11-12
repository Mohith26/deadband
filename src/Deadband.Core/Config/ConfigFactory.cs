using Deadband.Core.Tep;

namespace Deadband.Core.Config;

public sealed record RationalizationParams(
    double SigmaK = 4, double DeadbandSigma = 0.5, int OnDelay = 2, int OffDelay = 2,
    double Confidence = 0.99, double VarianceToKeep = 0.5, bool Multivariate = true, bool Chatter = true, bool Flood = true);

/// <summary>Builds the "as-found" (raw) and the rationalized alarm configuration from normal-operation training statistics.</summary>
public static class ConfigFactory
{
    public static (double[] mean, double[] std) Stats(IReadOnlyList<double[]> normal)
    {
        int n = normal.Count, m = normal[0].Length;
        var mean = new double[m]; var std = new double[m];
        for (int j = 0; j < m; j++)
        {
            mean[j] = normal.Average(r => r[j]);
            std[j] = Math.Sqrt(normal.Sum(r => Math.Pow(r[j] - mean[j], 2)) / (n - 1));
        }
        return (mean, std);
    }

    /// <summary>
    /// The baseline a plant often starts with: every tag (all 52) alarmed at mean +/- 3 sigma, no deadband, no delays,
    /// no shelving logic, everything Medium priority.
    /// </summary>
    public static AlarmConfig Raw(IReadOnlyList<double[]> normal)
    {
        var (mean, std) = Stats(normal);
        var tags = TepCatalog.Tags.Select((t, j) => new TagAlarmConfig
        {
            Tag = t.Id, Description = t.Description, Units = t.Units,
            Hi = Round(mean[j] + 3 * std[j]), Lo = Round(mean[j] - 3 * std[j]),
            Priority = AlarmPriority.Medium, Rationale = "as-found: 3-sigma statistical limit",
        }).ToList();
        return new AlarmConfig { Name = "as-found (3-sigma on all 52 tags)", Tags = tags, Oee = Oee(mean) };
    }

    // Operating limits from Downs and Vogel (1993), Table 4 (normal operating limits; shutdown limits are beyond these).
    private static readonly Dictionary<string, (double? hiHi, double? loLo, string why)> SafetyLimits = new()
    {
        ["XMEAS07"] = (2895, null, "reactor pressure approaching 3000 kPa shutdown limit; operator reduces feed / opens purge"),
        ["XMEAS08"] = (null, 50, "reactor level below operating limit; risk of low-level shutdown"),
        ["XMEAS09"] = (150, null, "reactor temperature above operating limit; risk of runaway / 175 degC shutdown"),
        ["XMEAS12"] = (null, 30, "separator level below operating limit; risk of losing liquid seal"),
        ["XMEAS15"] = (null, 30, "stripper level below operating limit; product pump protection"),
    };

    /// <summary>
    /// Rationalized configuration (ISA-18.2 ideas): only the 22 continuous process measurements get limit alarms,
    /// each with deadband, on/off delay and a written rationale; sampled analyzers and controller outputs are covered by one
    /// multivariate PROCESS.DEVIATION alarm; physical operating limits become Critical; chatter and flood handling are on.
    /// </summary>
    public static AlarmConfig Rationalized(IReadOnlyList<double[]> normal, RationalizationParams p)
    {
        var (mean, std) = Stats(normal);
        var tags = new List<TagAlarmConfig>();
        for (int j = 0; j < TepCatalog.TagCount; j++)
        {
            var t = TepCatalog.Tags[j];
            if (t.IsAnalyzer || t.IsManipulated)
            {
                tags.Add(new TagAlarmConfig
                {
                    Tag = t.Id, Description = t.Description, Units = t.Units, Enabled = false,
                    Rationale = t.IsAnalyzer
                        ? "sampled analyzer with 6-15 min dead time: no single-tag operator action; monitored by PROCESS.DEVIATION"
                        : "controller output, not a process condition; monitored by PROCESS.DEVIATION",
                });
                continue;
            }
            SafetyLimits.TryGetValue(t.Id, out var safe);
            tags.Add(new TagAlarmConfig
            {
                Tag = t.Id, Description = t.Description, Units = t.Units,
                Hi = Round(mean[j] + p.SigmaK * std[j]), Lo = Round(mean[j] - p.SigmaK * std[j]),
                HiHi = safe.hiHi, LoLo = safe.loLo,
                Deadband = Round(p.DeadbandSigma * std[j]), OnDelaySamples = p.OnDelay, OffDelaySamples = p.OffDelay,
                Priority = safe.why != null ? AlarmPriority.High : AlarmPriority.Medium,
                Rationale = safe.why ?? $"{t.Unit}: sustained {p.SigmaK:0.#}-sigma excursion of a continuous measurement; operator checks {t.Unit.ToLowerInvariant()} controls",
            });
        }
        // physical limits must sit outside the statistical band, otherwise drop them (validator would reject the ordering)
        for (int i = 0; i < tags.Count; i++)
        {
            var t = tags[i];
            if (t.HiHi is double hh && t.Hi is double h && hh <= h) tags[i] = t with { HiHi = null };
            if (t.LoLo is double ll && t.Lo is double l && ll >= l) tags[i] = tags[i] with { LoLo = null };
        }
        return new AlarmConfig
        {
            Name = $"rationalized (k={p.SigmaK}, on-delay={p.OnDelay}, PCA conf={p.Confidence}, var={p.VarianceToKeep})",
            Tags = tags,
            Multivariate = new MultivariateConfig { Enabled = p.Multivariate, Confidence = p.Confidence, VarianceToKeep = p.VarianceToKeep, OnDelaySamples = p.OnDelay, OffDelaySamples = p.OffDelay },
            Chatter = new ChatterConfig { Enabled = p.Chatter },
            Flood = new FloodConfig { Enabled = p.Flood },
            Oee = Oee(mean),
        };
    }

    private static OeeConfig Oee(double[] mean) => new()
    {
        DesignRate = Round(mean[16]),
        QualityTargets = new[] { Round(mean[39]), Round(mean[40]) },
    };

    private static double Round(double v) => Math.Round(v, 4);
}
