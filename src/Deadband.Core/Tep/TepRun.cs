using System.Globalization;

namespace Deadband.Core.Tep;

/// <summary>One Tennessee Eastman run (Braatz et al. data files dNN.dat / dNN_te.dat), rows = samples, cols = 52 tags.</summary>
public sealed class TepRun
{
    public required string Name { get; init; }
    public required int Fault { get; init; }          // 0 = normal operation
    public required bool IsTest { get; init; }
    public required double[][] Samples { get; init; }
    /// <summary>Index of the first faulty sample, or null for normal runs.</summary>
    public int? FaultOnsetIndex => Fault == 0 ? null : (IsTest ? TepCatalog.TestFaultOnsetIndex : 0);
    public int Length => Samples.Length;

    public static TepRun Load(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var isTest = name.EndsWith("_te", StringComparison.Ordinal);
        var fault = int.Parse(name.Substring(1, 2), CultureInfo.InvariantCulture);
        var rows = File.ReadAllLines(path)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray())
            .ToArray();
        // d00.dat is stored transposed (52 rows x 500 cols) in the original distribution.
        if (rows.Length == TepCatalog.TagCount && rows[0].Length != TepCatalog.TagCount)
            rows = Transpose(rows);
        if (rows.Any(r => r.Length != TepCatalog.TagCount))
            throw new InvalidDataException($"{path}: expected {TepCatalog.TagCount} columns per sample");
        return new TepRun { Name = name, Fault = fault, IsTest = isTest, Samples = rows };
    }

    public TepRun Slice(int start, int count, string? name = null) => new()
    {
        Name = name ?? $"{Name}[{start}:{start + count}]", Fault = Fault, IsTest = IsTest,
        Samples = Samples.Skip(start).Take(count).ToArray(),
    };

    private static double[][] Transpose(double[][] m)
    {
        var r = new double[m[0].Length][];
        for (int i = 0; i < r.Length; i++)
        {
            r[i] = new double[m.Length];
            for (int j = 0; j < m.Length; j++) r[i][j] = m[j][i];
        }
        return r;
    }

    public static IEnumerable<TepRun> LoadDirectory(string dir, bool test)
    {
        for (int f = 0; f <= 21; f++)
        {
            var p = Path.Combine(dir, test ? $"d{f:00}_te.dat" : $"d{f:00}.dat");
            if (File.Exists(p)) yield return Load(p);
        }
    }
}
