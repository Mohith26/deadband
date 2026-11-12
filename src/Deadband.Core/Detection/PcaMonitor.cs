using MathNet.Numerics.Distributions;
using MathNet.Numerics.LinearAlgebra;

namespace Deadband.Core.Detection;

public readonly record struct PcaScore(double T2, double Spe, double Index, int[] TopContributors);

/// <summary>
/// Classic PCA process monitor (Hotelling T2 + squared prediction error) fitted on normal-operation data.
/// Index = max(T2 / T2_limit, SPE / SPE_limit); index above 1 means outside the normal operating envelope.
/// </summary>
public sealed class PcaMonitor
{
    public double[] Mean { get; }
    public double[] Std { get; }
    public int Components { get; }
    public double T2Limit { get; }
    public double SpeLimit { get; }
    private readonly Matrix<double> _p;      // m x k loadings
    private readonly double[] _lambda;       // k retained eigenvalues

    private PcaMonitor(double[] mean, double[] std, Matrix<double> p, double[] lambda, double t2, double spe)
    {
        Mean = mean; Std = std; _p = p; _lambda = lambda; Components = lambda.Length; T2Limit = t2; SpeLimit = spe;
    }

    public static PcaMonitor Fit(IReadOnlyList<double[]> normal, double varianceToKeep = 0.90, double confidence = 0.99)
    {
        int n = normal.Count, m = normal[0].Length;
        var mean = new double[m];
        var std = new double[m];
        for (int j = 0; j < m; j++)
        {
            double s = 0; for (int i = 0; i < n; i++) s += normal[i][j];
            mean[j] = s / n;
            double v = 0; for (int i = 0; i < n; i++) v += Math.Pow(normal[i][j] - mean[j], 2);
            std[j] = Math.Max(Math.Sqrt(v / (n - 1)), 1e-9);
        }
        var z = Matrix<double>.Build.Dense(n, m, (i, j) => (normal[i][j] - mean[j]) / std[j]);
        var cov = z.TransposeThisAndMultiply(z) / (n - 1);
        var evd = cov.Evd(Symmetricity.Symmetric);
        var eig = evd.EigenValues.Select(c => c.Real).ToArray();
        var order = Enumerable.Range(0, m).OrderByDescending(i => eig[i]).ToArray();
        double total = eig.Sum(), acc = 0;
        int k = 0;
        while (k < m && acc / total < varianceToKeep) { acc += eig[order[k]]; k++; }
        var p = Matrix<double>.Build.Dense(m, k, (r, c) => evd.EigenVectors[r, order[c]]);
        var lambda = order.Take(k).Select(i => eig[i]).ToArray();
        // T2 limit (F distribution), SPE limit (Jackson-Mudholkar)
        double t2 = k * (n - 1.0) * (n + 1.0) / (n * (n - (double)k)) * FisherSnedecor.InvCDF(k, n - k, confidence);
        var resid = order.Skip(k).Select(i => Math.Max(eig[i], 0)).ToArray();
        double th1 = resid.Sum(), th2 = resid.Sum(x => x * x), th3 = resid.Sum(x => x * x * x);
        double h0 = 1 - 2 * th1 * th3 / (3 * th2 * th2);
        double ca = Normal.InvCDF(0, 1, confidence);
        double spe = th1 * Math.Pow(ca * Math.Sqrt(2 * th2 * h0 * h0) / th1 + 1 + th2 * h0 * (h0 - 1) / (th1 * th1), 1 / h0);
        return new PcaMonitor(mean, std, p, lambda, t2, spe);
    }

    public PcaScore Score(double[] x)
    {
        int m = Mean.Length;
        var z = Vector<double>.Build.Dense(m, j => (x[j] - Mean[j]) / Std[j]);
        var t = _p.TransposeThisAndMultiply(z);
        double t2 = 0; for (int i = 0; i < Components; i++) t2 += t[i] * t[i] / _lambda[i];
        var r = z - _p * t;
        double spe = r.DotProduct(r);
        double idx = Math.Max(t2 / T2Limit, spe / SpeLimit);
        var top = Enumerable.Range(0, m).OrderByDescending(j => Math.Abs(z[j])).Take(3).ToArray();
        return new PcaScore(t2, spe, idx, top);
    }
}
