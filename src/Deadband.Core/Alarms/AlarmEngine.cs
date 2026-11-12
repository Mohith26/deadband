using System.Text.Json.Serialization;
using Deadband.Core.Config;
using Deadband.Core.Detection;

namespace Deadband.Core.Alarms;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlarmKind { HiHi, Hi, Lo, LoLo, Deviation }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlarmEventType { Raised, Cleared, Acknowledged, Shelved, Unshelved, ChatterSuppressed, FloodStart, FloodEnd }

/// <summary>One entry in the alarm journal. Annunciated = shown to the operator (counts toward alarm load).</summary>
public sealed record AlarmEvent(
    double TimeSec, string Tag, AlarmKind? Kind, AlarmEventType Type, AlarmPriority Priority,
    double Value, double Limit, bool Annunciated, string? SuppressedBy = null, string? Detail = null, string? User = null);

public sealed record ActiveAlarm(string Tag, AlarmKind Kind, AlarmPriority Priority, double RaisedAtSec, double Value, double Limit,
    bool Annunciated, string? SuppressedBy, bool Acknowledged, string? Detail);

public sealed record ShelvedTag(string Tag, double UntilSec, string User, string Reason);

/// <summary>
/// ISA-18.2 style alarm state machine: limit alarms with deadband, on/off delay, operator shelving,
/// chattering suppression, and flood detection with priority-based dynamic suppression.
/// Deterministic and clock-free: callers pass sample time, so replay and live use the same code.
/// </summary>
public sealed class AlarmEngine
{
    public const string DeviationTag = "PROCESS.DEVIATION";

    private sealed class LimitState
    {
        public required string Tag; public required int Index; public required AlarmKind Kind; public required double Limit;
        public required double Deadband; public required int On; public required int Off; public required AlarmPriority Priority;
        public bool High => Kind is AlarmKind.Hi or AlarmKind.HiHi or AlarmKind.Deviation;
        public int OnCount, OffCount; public bool Active, Annunciated, Acked; public string? SuppressedBy, Detail;
        public double RaisedAt, LastValue;
    }

    private readonly AlarmConfig _cfg;
    private readonly List<LimitState> _limits = new();
    private readonly PcaMonitor? _pca;
    private readonly string[] _tagIds;
    private readonly Dictionary<string, ShelvedTag> _shelved = new();
    private readonly Dictionary<string, double> _chatterUntil = new();
    private readonly Dictionary<string, Queue<double>> _raiseHistory = new();
    private readonly Queue<(double t, string tag)> _floodWindow = new();
    private bool _flood;
    private double _now;

    public AlarmConfig Config => _cfg;
    public bool InFlood => _flood;
    public PcaScore? LastScore { get; private set; }
    /// <summary>EWMA-filtered deviation index that the PROCESS.DEVIATION alarm evaluates (1.0 = control limit).</summary>
    public double? DeviationIndex { get; private set; }
    public IReadOnlyList<string> TagIds => _tagIds;

    public AlarmEngine(AlarmConfig cfg, IReadOnlyList<string> tagIds, PcaMonitor? pca = null)
    {
        _cfg = cfg;
        _tagIds = tagIds.ToArray();
        _pca = cfg.Multivariate.Enabled ? pca ?? throw new ArgumentException("multivariate alarm enabled but no PCA model") : null;
        foreach (var t in cfg.Tags.Where(t => t.Enabled))
        {
            int idx = Array.IndexOf(_tagIds, t.Tag);
            if (idx < 0) throw new ArgumentException($"tag {t.Tag} not in tag list");
            void Add(AlarmKind k, double? lim, AlarmPriority pr)
            {
                if (lim is double l) _limits.Add(new LimitState { Tag = t.Tag, Index = idx, Kind = k, Limit = l, Deadband = t.Deadband, On = t.OnDelaySamples, Off = t.OffDelaySamples, Priority = pr });
            }
            var bumped = t.Priority == AlarmPriority.Critical ? AlarmPriority.Critical : t.Priority + 1;
            Add(AlarmKind.HiHi, t.HiHi, bumped); Add(AlarmKind.Hi, t.Hi, t.Priority);
            Add(AlarmKind.Lo, t.Lo, t.Priority); Add(AlarmKind.LoLo, t.LoLo, bumped);
        }
        if (_pca != null)
        {
            var m = cfg.Multivariate;
            _limits.Add(new LimitState { Tag = DeviationTag, Index = -1, Kind = AlarmKind.Deviation, Limit = 1.0, Deadband = m.Deadband, On = m.OnDelaySamples, Off = m.OffDelaySamples, Priority = m.Priority });
        }
    }

    public IReadOnlyList<ActiveAlarm> ActiveAlarms() => _limits.Where(l => l.Active)
        .OrderByDescending(l => l.Priority).ThenBy(l => l.RaisedAt)
        .Select(l => new ActiveAlarm(l.Tag, l.Kind, l.Priority, l.RaisedAt, l.LastValue, l.Limit, l.Annunciated, l.SuppressedBy, l.Acked, l.Detail)).ToList();

    public IReadOnlyList<ShelvedTag> Shelved() => _shelved.Values.OrderBy(s => s.Tag).ToList();

    public List<AlarmEvent> Process(double timeSec, IReadOnlyList<double> values)
    {
        _now = timeSec;
        var events = new List<AlarmEvent>();
        foreach (var s in _shelved.Values.Where(s => s.UntilSec <= timeSec).ToList())
        {
            _shelved.Remove(s.Tag);
            events.Add(new AlarmEvent(timeSec, s.Tag, null, AlarmEventType.Unshelved, AlarmPriority.Low, double.NaN, double.NaN, false, Detail: "shelve expired", User: "system"));
        }
        if (_pca != null)
        {
            var arr = values as double[] ?? values.ToArray();
            LastScore = _pca.Score(arr);
            var lam = _cfg.Multivariate.EwmaLambda;
            DeviationIndex = DeviationIndex is double prev ? lam * LastScore.Value.Index + (1 - lam) * prev : LastScore.Value.Index;
        }
        foreach (var l in _limits)
        {
            double v = l.Index >= 0 ? values[l.Index] : DeviationIndex!.Value;
            if (double.IsNaN(v)) continue;
            l.LastValue = v;
            bool cond = l.Active
                ? (l.High ? v > l.Limit - l.Deadband : v < l.Limit + l.Deadband)
                : (l.High ? v > l.Limit : v < l.Limit);
            if (!l.Active)
            {
                if (!cond) { l.OnCount = 0; continue; }
                if (++l.OnCount < Math.Max(1, l.On)) continue;
                Raise(l, v, timeSec, events);
            }
            else
            {
                if (cond) { l.OffCount = 0; continue; }
                if (++l.OffCount < Math.Max(1, l.Off)) continue;
                l.Active = false; l.OffCount = 0; l.OnCount = 0;
                events.Add(new AlarmEvent(timeSec, l.Tag, l.Kind, AlarmEventType.Cleared, l.Priority, v, l.Limit, false, l.SuppressedBy));
            }
        }
        UpdateFlood(timeSec, events);
        return events;
    }

    private void Raise(LimitState l, double v, double t, List<AlarmEvent> events)
    {
        l.Active = true; l.OnCount = 0; l.OffCount = 0; l.RaisedAt = t; l.Acked = false;
        l.Detail = l.Kind == AlarmKind.Deviation && LastScore is { } sc
            ? "top deviating tags: " + string.Join(", ", sc.TopContributors.Select(i => _tagIds[i]))
            : null;
        string? suppressed = null;
        if (_shelved.ContainsKey(l.Tag)) suppressed = "shelved";
        else if (_chatterUntil.TryGetValue(l.Tag, out var until) && until > t) suppressed = "chatter";
        else if (_cfg.Chatter.Enabled)
        {
            var h = _raiseHistory.TryGetValue(l.Tag, out var q) ? q : _raiseHistory[l.Tag] = new Queue<double>();
            h.Enqueue(t);
            while (h.Count > 0 && h.Peek() <= t - _cfg.Chatter.WindowSeconds) h.Dequeue();
            if (h.Count >= _cfg.Chatter.MaxRaises)
            {
                _chatterUntil[l.Tag] = t + _cfg.Chatter.HoldSeconds;
                h.Clear();
                suppressed = "chatter";
                events.Add(new AlarmEvent(t, l.Tag, l.Kind, AlarmEventType.ChatterSuppressed, l.Priority, v, l.Limit, false,
                    Detail: $"{_cfg.Chatter.MaxRaises} raises in {_cfg.Chatter.WindowSeconds / 60:0} min; suppressed for {_cfg.Chatter.HoldSeconds / 60:0} min", User: "system"));
            }
        }
        if (suppressed == null && _cfg.Flood.Enabled)
        {
            _floodWindow.Enqueue((t, l.Tag));
            if (_flood && l.Priority < _cfg.Flood.MinPriorityDuringFlood) suppressed = "flood";
        }
        l.SuppressedBy = suppressed;
        l.Annunciated = suppressed == null;
        events.Add(new AlarmEvent(t, l.Tag, l.Kind, AlarmEventType.Raised, l.Priority, v, l.Limit, l.Annunciated, suppressed, l.Detail));
    }

    private void UpdateFlood(double t, List<AlarmEvent> events)
    {
        if (!_cfg.Flood.Enabled) return;
        while (_floodWindow.Count > 0 && _floodWindow.Peek().t <= t - _cfg.Flood.WindowSeconds) _floodWindow.Dequeue();
        int n = _floodWindow.Count;
        if (!_flood && n >= _cfg.Flood.Threshold)
        {
            _flood = true;
            var firstOut = _floodWindow.Peek().tag;
            events.Add(new AlarmEvent(t, "PLANT.FLOOD", null, AlarmEventType.FloodStart, AlarmPriority.Critical, n, _cfg.Flood.Threshold, true,
                Detail: $"alarm flood: {n} alarms in {_cfg.Flood.WindowSeconds / 60:0} min, first-out {firstOut}"));
        }
        else if (_flood && n < Math.Max(1, _cfg.Flood.Threshold / 2))
        {
            _flood = false;
            events.Add(new AlarmEvent(t, "PLANT.FLOOD", null, AlarmEventType.FloodEnd, AlarmPriority.Critical, n, _cfg.Flood.Threshold, false));
        }
    }

    public AlarmEvent Shelve(string tag, double durationSec, string user, string reason)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("shelving requires a user and a reason");
        if (durationSec <= 0 || durationSec > 12 * 3600) throw new ArgumentOutOfRangeException(nameof(durationSec), "shelve 1 s to 12 h");
        if (!_limits.Any(l => l.Tag == tag)) throw new KeyNotFoundException($"no alarm configured for {tag}");
        _shelved[tag] = new ShelvedTag(tag, _now + durationSec, user, reason);
        foreach (var l in _limits.Where(l => l.Tag == tag && l.Active)) { l.SuppressedBy = "shelved"; }
        return new AlarmEvent(_now, tag, null, AlarmEventType.Shelved, AlarmPriority.Low, double.NaN, double.NaN, false, Detail: $"{reason} (until t={_now + durationSec:0}s)", User: user);
    }

    public AlarmEvent Unshelve(string tag, string user)
    {
        if (!_shelved.Remove(tag)) throw new KeyNotFoundException($"{tag} is not shelved");
        return new AlarmEvent(_now, tag, null, AlarmEventType.Unshelved, AlarmPriority.Low, double.NaN, double.NaN, false, User: user);
    }

    public AlarmEvent Acknowledge(string tag, AlarmKind kind, string user)
    {
        var l = _limits.FirstOrDefault(x => x.Tag == tag && x.Kind == kind && x.Active)
            ?? throw new KeyNotFoundException($"no active {kind} alarm on {tag}");
        l.Acked = true;
        return new AlarmEvent(_now, tag, kind, AlarmEventType.Acknowledged, l.Priority, l.LastValue, l.Limit, false, User: user);
    }
}
