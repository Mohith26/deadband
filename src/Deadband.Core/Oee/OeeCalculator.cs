using Deadband.Core.Config;

namespace Deadband.Core.Oee;

public sealed record OeeResult(double Availability, double Performance, double Quality, int Samples)
{
    public double Oee => Availability * Performance * Quality;
}

/// <summary>
/// OEE adapted to a continuous process: Availability = running time / planned time (running = product flow above a fraction
/// of design rate); Performance = actual output / (design rate x running time), capped at 1; Quality = share of running
/// samples whose product analyzers are inside the spec band.
/// </summary>
public sealed class OeeCalculator
{
    private readonly OeeConfig _c;
    private readonly int _flow;
    private readonly int[] _q;
    private int _n, _running, _good;
    private double _output;

    public OeeCalculator(OeeConfig c, IReadOnlyList<string> tagIds)
    {
        _c = c;
        _flow = IndexOrThrow(tagIds, c.ProductFlowTag);
        _q = c.QualityTags.Select(t => IndexOrThrow(tagIds, t)).ToArray();
    }

    private static int IndexOrThrow(IReadOnlyList<string> ids, string tag)
    {
        for (int i = 0; i < ids.Count; i++) if (ids[i] == tag) return i;
        throw new ArgumentException($"OEE tag {tag} not found");
    }

    public void Add(IReadOnlyList<double> x)
    {
        _n++;
        double f = x[_flow];
        if (f < _c.RunningThreshold * _c.DesignRate) return;
        _running++;
        _output += Math.Min(f, _c.DesignRate);
        bool ok = true;
        for (int i = 0; i < _q.Length; i++) ok &= Math.Abs(x[_q[i]] - _c.QualityTargets[i]) <= _c.QualityTolerance;
        if (ok) _good++;
    }

    public OeeResult Result()
    {
        if (_n == 0) return new OeeResult(0, 0, 0, 0);
        double a = (double)_running / _n;
        double p = _running == 0 ? 0 : _output / (_c.DesignRate * _running);
        double q = _running == 0 ? 0 : (double)_good / _running;
        return new OeeResult(a, p, q, _n);
    }

    public static OeeResult Compute(OeeConfig c, IReadOnlyList<string> tagIds, IEnumerable<IReadOnlyList<double>> samples)
    {
        var calc = new OeeCalculator(c, tagIds);
        foreach (var s in samples) calc.Add(s);
        return calc.Result();
    }
}
