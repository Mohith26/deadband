using Deadband.Api.Data;
using Deadband.Core.Alarms;
using Deadband.Core.Config;
using Deadband.Core.Detection;
using Deadband.Core.Eval;
using Deadband.Core.Oee;
using Deadband.Core.Tep;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Deadband.Api.Services;

public sealed class PlantOptions
{
    public string DataDir { get; set; } = "data/tep";
    public string ConfigDir { get; set; } = "data/config-store";
    public string SeedConfig { get; set; } = "config/rationalized.json";
    public string Run { get; set; } = "d06_te";
    public bool AutoReplay { get; set; } = true;
    public int IntervalMs { get; set; } = 1000;
    public int TrendSamples { get; set; } = 240;
}

public sealed record TagSnapshot(string Tag, string Description, string Units, string Unit, double Value);
public sealed record AlarmLoad(double PlantTimeSec, int AnnunciatedLast10Min, double AnnunciatedPer10MinAverage, bool InFlood, int ActiveAlarms, int Shelved);

/// <summary>
/// Owns the live alarm engine. Every sample (replayed TEP row) goes through the active configuration; events are journaled
/// to SQLite and broadcast to SSE subscribers. Config publish / rollback swaps the engine without restarting the API.
/// </summary>
public sealed class PlantRuntime
{
    private readonly object _gate = new();
    private readonly IDbContextFactory<AlarmDb> _db;
    private readonly AlarmBroadcaster _bus;
    private readonly PlantOptions _opt;
    private readonly PcaMonitor? _pca;
    private readonly Queue<(double t, double[] x)> _trend = new();
    private readonly Queue<double> _annunciated = new();
    private AlarmEngine _engine;
    private OeeCalculator _oee;
    private TepRun _run;
    private int _cursor, _totalAnnunciated;
    private double _now;
    public ConfigStore Store { get; }
    public int ConfigVersion { get; private set; }

    public PlantRuntime(IDbContextFactory<AlarmDb> db, AlarmBroadcaster bus, IOptions<PlantOptions> opt)
    {
        _db = db; _bus = bus; _opt = opt.Value;
        Store = new ConfigStore(_opt.ConfigDir, Evaluator.TagIds.ToHashSet());
        if (Store.Versions.Count == 0)
            Store.Publish(File.Exists(_opt.SeedConfig) ? AlarmConfig.FromJson(File.ReadAllText(_opt.SeedConfig)) : new AlarmConfig { Name = "empty" },
                "system", $"seeded from {_opt.SeedConfig}");
        var normal = Path.Combine(_opt.DataDir, "d00.dat");
        var cfg = Store.GetActive();
        if (File.Exists(normal))
            _pca = PcaMonitor.Fit(TepRun.Load(normal).Samples, cfg.Multivariate.VarianceToKeep, cfg.Multivariate.Confidence);
        ConfigVersion = Store.ActiveVersion;
        _engine = BuildEngine(cfg);
        _oee = new OeeCalculator(cfg.Oee, Evaluator.TagIds);
        _run = LoadRun(_opt.Run);
        Store.Activated += (v, c) => { lock (_gate) { _engine = BuildEngine(c); _oee = new OeeCalculator(c.Oee, Evaluator.TagIds); ConfigVersion = v; } };
    }

    private AlarmEngine BuildEngine(AlarmConfig c) =>
        new(c.Multivariate.Enabled && _pca == null ? c with { Multivariate = c.Multivariate with { Enabled = false } } : c, Evaluator.TagIds, _pca);

    private TepRun LoadRun(string name)
    {
        var p = Path.Combine(_opt.DataDir, name + ".dat");
        if (!File.Exists(p)) throw new FileNotFoundException($"run {name} not found in {_opt.DataDir}");
        return TepRun.Load(p);
    }

    public string RunName { get { lock (_gate) return _run.Name; } }
    public int Cursor { get { lock (_gate) return _cursor; } }
    public int RunLength { get { lock (_gate) return _run.Length; } }
    public int? FaultOnset { get { lock (_gate) return _run.FaultOnsetIndex; } }
    public string? FaultDescription { get { lock (_gate) return _run.Fault == 0 ? null : TepCatalog.FaultDescriptions[_run.Fault]; } }

    public void Restart(string run)
    {
        var r = LoadRun(run);
        lock (_gate)
        {
            _run = r; _cursor = 0; _now = 0; _trend.Clear(); _annunciated.Clear(); _totalAnnunciated = 0;
            var c = Store.GetActive();
            _engine = BuildEngine(c); _oee = new OeeCalculator(c.Oee, Evaluator.TagIds);
        }
    }

    /// <summary>Advance the replay by n samples (wraps to the start at the end of the run).</summary>
    public async Task<IReadOnlyList<AlarmEvent>> StepAsync(int n = 1, CancellationToken ct = default)
    {
        var all = new List<AlarmEvent>();
        string run; int version;
        lock (_gate)
        {
            for (int i = 0; i < n; i++)
            {
                if (_cursor >= _run.Length) { _cursor = 0; _trend.Clear(); }
                var x = _run.Samples[_cursor];
                _now = _cursor * TepCatalog.SamplePeriodSec;
                var ev = _engine.Process(_now, x);
                _oee.Add(x);
                _trend.Enqueue((_now, x));
                while (_trend.Count > _opt.TrendSamples) _trend.Dequeue();
                foreach (var e in ev.Where(e => e.Annunciated)) { _annunciated.Enqueue(_now); _totalAnnunciated++; }
                all.AddRange(ev);
                _cursor++;
            }
            while (_annunciated.Count > 0 && _annunciated.Peek() <= _now - Evaluator.OperatorWindowSec) _annunciated.Dequeue();
            run = _run.Name; version = ConfigVersion;
        }
        await PersistAsync(all, run, version, ct);
        return all;
    }

    private async Task PersistAsync(IReadOnlyList<AlarmEvent> events, string run, int version, CancellationToken ct)
    {
        if (events.Count == 0) return;
        await using var db = await _db.CreateDbContextAsync(ct);
        db.Events.AddRange(events.Select(e => AlarmEventRow.From(e, run, version)));
        await db.SaveChangesAsync(ct);
        foreach (var e in events) _bus.Publish(e);
    }

    public IReadOnlyList<ActiveAlarm> Active() { lock (_gate) return _engine.ActiveAlarms(); }
    public IReadOnlyList<ShelvedTag> Shelved() { lock (_gate) return _engine.Shelved(); }

    public IReadOnlyList<TagSnapshot> Snapshot()
    {
        lock (_gate)
        {
            if (_trend.Count == 0) return Array.Empty<TagSnapshot>();
            var x = _trend.Last().x;
            var snap = TepCatalog.Tags.Select((t, i) => new TagSnapshot(t.Id, t.Description, t.Units, t.Unit, x[i])).ToList();
            if (_engine.DeviationIndex is double d) snap.Add(new TagSnapshot(AlarmEngine.DeviationTag, "Multivariate deviation index (EWMA of PCA T2/SPE, 1.0 = limit)", "", "Plant", d));
            return snap;
        }
    }

    public IReadOnlyList<(double t, double v)>? Trend(string tag)
    {
        int i = TepCatalog.IndexOf(tag);
        if (i < 0) return null;
        lock (_gate) return _trend.Select(s => (s.t, s.x[i])).ToList();
    }

    public AlarmLoad Load()
    {
        lock (_gate)
        {
            double hours = _cursor * TepCatalog.SamplePeriodSec / 3600;
            return new AlarmLoad(_now, _annunciated.Count, hours == 0 ? 0 : _totalAnnunciated / (hours * 6), _engine.InFlood,
                _engine.ActiveAlarms().Count(a => a.Annunciated), _engine.Shelved().Count);
        }
    }

    public OeeResult Oee() { lock (_gate) return _oee.Result(); }

    public async Task<AlarmEvent> OperatorActionAsync(Func<AlarmEngine, AlarmEvent> action, CancellationToken ct)
    {
        AlarmEvent e; string run; int v;
        lock (_gate) { e = action(_engine); run = _run.Name; v = ConfigVersion; }
        await PersistAsync(new[] { e }, run, v, ct);
        return e;
    }
}
