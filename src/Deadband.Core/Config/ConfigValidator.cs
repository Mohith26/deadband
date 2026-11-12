namespace Deadband.Core.Config;

public sealed record ValidationIssue(string Path, string Code, string Message);

/// <summary>Static checks run before a configuration version can be published (and in CI via the CLI).</summary>
public static class ConfigValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(AlarmConfig c, ISet<string>? knownTags = null)
    {
        var issues = new List<ValidationIssue>();
        void Add(string p, string code, string m) => issues.Add(new ValidationIssue(p, code, m));

        if (c.SchemaVersion != 1) Add("schemaVersion", "SCHEMA", "only schemaVersion 1 is supported");
        if (c.SamplePeriodSeconds <= 0) Add("samplePeriodSeconds", "RANGE", "sample period must be positive");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < c.Tags.Count; i++)
        {
            var t = c.Tags[i];
            var p = $"tags[{i}]";
            if (string.IsNullOrWhiteSpace(t.Tag)) { Add($"{p}.tag", "REQUIRED", "tag id is required"); continue; }
            if (!seen.Add(t.Tag)) Add($"{p}.tag", "DUPLICATE", $"tag '{t.Tag}' is defined more than once");
            if (knownTags != null && !knownTags.Contains(t.Tag)) Add($"{p}.tag", "UNKNOWN_TAG", $"tag '{t.Tag}' does not exist in the tag database");
            if (t.Deadband < 0) Add($"{p}.deadband", "RANGE", "deadband cannot be negative");
            if (t.OnDelaySamples < 0 || t.OnDelaySamples > 100) Add($"{p}.onDelaySamples", "RANGE", "on-delay must be 0..100 samples");
            if (t.OffDelaySamples < 0 || t.OffDelaySamples > 100) Add($"{p}.offDelaySamples", "RANGE", "off-delay must be 0..100 samples");
            if (!Enum.IsDefined(t.Priority)) Add($"{p}.priority", "RANGE", "unknown priority");
            if (!t.Enabled) continue;
            if (t.HiHi is null && t.Hi is null && t.Lo is null && t.LoLo is null)
                Add(p, "NO_LIMITS", $"enabled tag '{t.Tag}' has no limits");
            if (string.IsNullOrWhiteSpace(t.Rationale))
                Add($"{p}.rationale", "UNRATIONALIZED", $"enabled tag '{t.Tag}' has no rationalization note");
            Order(t.LoLo, t.Lo, "loLo", "lo"); Order(t.Lo, t.Hi, "lo", "hi"); Order(t.Hi, t.HiHi, "hi", "hiHi");
            Order(t.LoLo, t.Hi, "loLo", "hi"); Order(t.Lo, t.HiHi, "lo", "hiHi"); Order(t.LoLo, t.HiHi, "loLo", "hiHi");
            if (t.Hi is double h && t.Lo is double l && t.Deadband >= (h - l) / 2)
                Add($"{p}.deadband", "DEADBAND_TOO_WIDE", "deadband overlaps the opposite limit; the alarm could never clear");
            foreach (var (val, name) in new[] { (t.HiHi, "hiHi"), (t.Hi, "hi"), (t.Lo, "lo"), (t.LoLo, "loLo") })
                if (val is double v && (double.IsNaN(v) || double.IsInfinity(v))) Add($"{p}.{name}", "RANGE", "limit must be finite");

            void Order(double? lower, double? upper, string ln, string un)
            {
                if (lower is double a && upper is double b && a >= b)
                    Add($"{p}.{un}", "LIMIT_ORDER", $"{ln} ({a}) must be below {un} ({b})");
            }
        }

        var m = c.Multivariate;
        if (m.Enabled)
        {
            if (m.Confidence <= 0.5 || m.Confidence >= 1) Add("multivariate.confidence", "RANGE", "confidence must be in (0.5, 1)");
            if (m.VarianceToKeep <= 0 || m.VarianceToKeep >= 1) Add("multivariate.varianceToKeep", "RANGE", "varianceToKeep must be in (0, 1)");
            if (m.EwmaLambda <= 0 || m.EwmaLambda > 1) Add("multivariate.ewmaLambda", "RANGE", "ewmaLambda must be in (0, 1]");
            if (m.Deadband < 0 || m.Deadband >= 1) Add("multivariate.deadband", "RANGE", "deadband must be in [0, 1)");
            if (m.OnDelaySamples < 0 || m.OffDelaySamples < 0) Add("multivariate", "RANGE", "delays cannot be negative");
        }
        if (c.Chatter.Enabled && (c.Chatter.MaxRaises < 2 || c.Chatter.WindowSeconds <= 0 || c.Chatter.HoldSeconds <= 0))
            Add("chatter", "RANGE", "chatter needs maxRaises >= 2 and positive window/hold");
        if (c.Flood.Enabled && (c.Flood.Threshold < 2 || c.Flood.WindowSeconds <= 0))
            Add("flood", "RANGE", "flood needs threshold >= 2 and a positive window");
        if (c.Oee.DesignRate <= 0) Add("oee.designRate", "RANGE", "design rate must be positive");
        if (c.Oee.QualityTags.Length != c.Oee.QualityTargets.Length) Add("oee.qualityTargets", "SHAPE", "one target per quality tag");
        return issues;
    }
}
