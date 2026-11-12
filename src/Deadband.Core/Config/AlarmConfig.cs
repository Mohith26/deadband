using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deadband.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlarmPriority { Low = 1, Medium = 2, High = 3, Critical = 4 }

/// <summary>Per-tag alarm definition. Limits are absolute engineering values; null = limit not configured.</summary>
public sealed record TagAlarmConfig
{
    public required string Tag { get; init; }
    public string Description { get; init; } = "";
    public string Units { get; init; } = "";
    public bool Enabled { get; init; } = true;
    public double? HiHi { get; init; }
    public double? Hi { get; init; }
    public double? Lo { get; init; }
    public double? LoLo { get; init; }
    /// <summary>Return-to-normal hysteresis in engineering units (ISA-18.2 deadband).</summary>
    public double Deadband { get; init; }
    /// <summary>Consecutive samples the condition must hold before the alarm is raised.</summary>
    public int OnDelaySamples { get; init; }
    /// <summary>Consecutive clear samples before the alarm returns to normal.</summary>
    public int OffDelaySamples { get; init; }
    public AlarmPriority Priority { get; init; } = AlarmPriority.Medium;
    /// <summary>Rationalization note: consequence / operator action (documentation, required when Enabled).</summary>
    public string Rationale { get; init; } = "";
}

/// <summary>Multivariate (PCA T2 / SPE) process-deviation alarm settings.</summary>
public sealed record MultivariateConfig
{
    public bool Enabled { get; init; }
    public double Confidence { get; init; } = 0.99;
    public double VarianceToKeep { get; init; } = 0.50;
    /// <summary>EWMA smoothing of the deviation index (1.0 = no smoothing); filters single-sample noise before the limit check.</summary>
    public double EwmaLambda { get; init; } = 0.2;
    public double Deadband { get; init; } = 0.1;    // on the normalized index (1.0 = control limit)
    public int OnDelaySamples { get; init; } = 2;
    public int OffDelaySamples { get; init; } = 2;
    public AlarmPriority Priority { get; init; } = AlarmPriority.High;
}

/// <summary>Chattering-alarm suppression: N raises within a window shelves the tag automatically for HoldSeconds.</summary>
public sealed record ChatterConfig
{
    public bool Enabled { get; init; }
    public int MaxRaises { get; init; } = 3;
    public double WindowSeconds { get; init; } = 1800;
    public double HoldSeconds { get; init; } = 3600;
}

/// <summary>Alarm flood handling: above Threshold raises per WindowSeconds, only priorities >= MinPriorityDuringFlood annunciate.</summary>
public sealed record FloodConfig
{
    public bool Enabled { get; init; }
    public int Threshold { get; init; } = 10;
    public double WindowSeconds { get; init; } = 600;
    public AlarmPriority MinPriorityDuringFlood { get; init; } = AlarmPriority.High;
}

public sealed record OeeConfig
{
    public string ProductFlowTag { get; init; } = "XMEAS17";
    public double DesignRate { get; init; } = 22.95;
    /// <summary>Below this fraction of design rate the line counts as stopped (availability loss).</summary>
    public double RunningThreshold { get; init; } = 0.5;
    public string[] QualityTags { get; init; } = { "XMEAS40", "XMEAS41" };
    public double[] QualityTargets { get; init; } = { 53.83, 43.83 };
    public double QualityTolerance { get; init; } = 1.5;
}

public sealed record AlarmConfig
{
    public int SchemaVersion { get; init; } = 1;
    public string Plant { get; init; } = "Tennessee Eastman";
    public string Name { get; init; } = "";
    public double SamplePeriodSeconds { get; init; } = 180;
    public List<TagAlarmConfig> Tags { get; init; } = new();
    public MultivariateConfig Multivariate { get; init; } = new();
    public ChatterConfig Chatter { get; init; } = new();
    public FloodConfig Flood { get; init; } = new();
    public OeeConfig Oee { get; init; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static AlarmConfig FromJson(string json) =>
        JsonSerializer.Deserialize<AlarmConfig>(json, Json) ?? throw new JsonException("empty config");
}
