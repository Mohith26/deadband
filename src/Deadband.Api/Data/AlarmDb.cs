using Deadband.Core.Alarms;
using Deadband.Core.Config;
using Microsoft.EntityFrameworkCore;

namespace Deadband.Api.Data;

public sealed class AlarmEventRow
{
    public long Id { get; set; }
    public string Run { get; set; } = "";
    public double TimeSec { get; set; }
    public string Tag { get; set; } = "";
    public AlarmKind? Kind { get; set; }
    public AlarmEventType Type { get; set; }
    public AlarmPriority Priority { get; set; }
    public double? Value { get; set; }
    public double? Limit { get; set; }
    public bool Annunciated { get; set; }
    public string? SuppressedBy { get; set; }
    public string? Detail { get; set; }
    public string? User { get; set; }
    public int ConfigVersion { get; set; }
    public DateTime CreatedUtc { get; set; }

    public static AlarmEventRow From(AlarmEvent e, string run, int configVersion) => new()
    {
        Run = run, TimeSec = e.TimeSec, Tag = e.Tag, Kind = e.Kind, Type = e.Type, Priority = e.Priority,
        Value = double.IsNaN(e.Value) ? null : e.Value, Limit = double.IsNaN(e.Limit) ? null : e.Limit,
        Annunciated = e.Annunciated, SuppressedBy = e.SuppressedBy, Detail = e.Detail, User = e.User,
        ConfigVersion = configVersion, CreatedUtc = DateTime.UtcNow,
    };
}

public sealed class DbOptions
{
    /// <summary>Composite (Tag, TimeSec) index for history queries. Turned off only to measure the load-test baseline.</summary>
    public bool HistoryIndex { get; set; } = true;
}

public sealed class AlarmDb(DbContextOptions<AlarmDb> options, Microsoft.Extensions.Options.IOptions<DbOptions> dbOptions) : DbContext(options)
{
    public DbSet<AlarmEventRow> Events => Set<AlarmEventRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        var e = b.Entity<AlarmEventRow>();
        e.ToTable("alarm_events");
        e.Property(x => x.Kind).HasConversion<string>();
        e.Property(x => x.Type).HasConversion<string>();
        e.Property(x => x.Priority).HasConversion<string>();
        e.HasIndex(x => x.Type);
        if (dbOptions.Value.HistoryIndex) e.HasIndex(x => new { x.Tag, x.TimeSec });
    }
}
