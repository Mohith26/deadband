using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Deadband.Cli;

/// <summary>Closed-loop HTTP load generator: N workers issue a fixed read mix as fast as responses come back.</summary>
public static class LoadTest
{
    private static readonly string[] Paths =
    {
        "/api/v1/alarms/active",
        "/api/v1/alarms/history?tag=XMEAS09&limit=50",
        "/api/v1/alarms/history?limit=50",
        "/api/v1/tags/snapshot",
        "/api/v1/kpi/alarm-load",
    };

    public static async Task<int> RunAsync(string baseUrl, int concurrency, double seconds, string label, string outPath, JsonSerializerOptions json)
    {
        // UseCookies=false: no cookie jar needed for a read-only API test
        using var http = new HttpClient(new SocketsHttpHandler { UseCookies = false, MaxConnectionsPerServer = concurrency }) { BaseAddress = new Uri(baseUrl) };
        for (int i = 0; i < 50; i++) await http.GetAsync(Paths[i % Paths.Length]); // warm-up
        var lat = new ConcurrentBag<(int path, double ms, bool ok)>();
        var stop = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, concurrency).Select(async w =>
        {
            int i = w;
            while (stop.Elapsed.TotalSeconds < seconds)
            {
                int p = i++ % Paths.Length;
                var t0 = Stopwatch.GetTimestamp();
                bool ok;
                try { using var r = await http.GetAsync(Paths[p]); await r.Content.ReadAsByteArrayAsync(); ok = r.IsSuccessStatusCode; }
                catch { ok = false; }
                lat.Add((p, Stopwatch.GetElapsedTime(t0).TotalMilliseconds, ok));
            }
        }).ToArray();
        await Task.WhenAll(workers);
        var all = lat.ToList();
        double P(IEnumerable<double> xs, double q) { var s = xs.OrderBy(x => x).ToArray(); return s.Length == 0 ? 0 : s[Math.Min(s.Length - 1, (int)Math.Ceiling(q * s.Length) - 1)]; }
        var res = new JsonObject
        {
            ["machine"] = "one laptop (Apple Silicon arm64), client and server on the same machine, Release build",
            ["concurrency"] = concurrency,
            ["seconds"] = seconds,
            ["requests"] = all.Count,
            ["errors"] = all.Count(x => !x.ok),
            ["rps"] = Math.Round(all.Count / stop.Elapsed.TotalSeconds, 1),
            ["p50Ms"] = Math.Round(P(all.Select(x => x.ms), 0.50), 2),
            ["p95Ms"] = Math.Round(P(all.Select(x => x.ms), 0.95), 2),
            ["p99Ms"] = Math.Round(P(all.Select(x => x.ms), 0.99), 2),
            ["perEndpointP95Ms"] = new JsonObject(Paths.Select((p, i) => KeyValuePair.Create(p, (JsonNode?)Math.Round(P(all.Where(x => x.path == i).Select(x => x.ms), 0.95), 2)))),
        };
        Console.WriteLine($"{label}: {res.ToJsonString()}");
        var root = File.Exists(outPath) ? JsonNode.Parse(File.ReadAllText(outPath))!.AsObject() : new JsonObject();
        var lt = root["loadtest"] as JsonObject ?? new JsonObject();
        lt[label] = res;
        root["loadtest"] = lt;
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        File.WriteAllText(outPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return all.Any(x => !x.ok) ? 1 : 0;
    }
}
