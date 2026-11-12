using System.Diagnostics;
using System.Text.Json;
using Deadband.Core.Config;
using Deadband.Core.Eval;
using Deadband.Core.Tep;
using Deadband.Cli;

var cmd = args.FirstOrDefault() ?? "help";
string Arg(string name, string def) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : def; }
var json = new JsonSerializerOptions(AlarmConfig.Json) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };
var known = TepCatalog.Tags.Select(t => t.Id).ToHashSet();

switch (cmd)
{
    case "validate":
    {
        // config lint used by CI: deadband validate config/*.json
        int bad = 0;
        foreach (var f in args.Skip(1))
        {
            var issues = ConfigValidator.Validate(AlarmConfig.FromJson(File.ReadAllText(f)), known);
            Console.WriteLine($"{f}: {(issues.Count == 0 ? "OK" : $"{issues.Count} issue(s)")}");
            foreach (var i in issues) Console.WriteLine($"  {i.Code,-18} {i.Path}: {i.Message}");
            if (issues.Count > 0) bad++;
        }
        return bad == 0 ? 0 : 1;
    }
    case "diff":
    {
        var d = ConfigDiff.Compute(AlarmConfig.FromJson(File.ReadAllText(args[1])), AlarmConfig.FromJson(File.ReadAllText(args[2])));
        foreach (var a in d.AddedTags) Console.WriteLine($"+ {a}");
        foreach (var r in d.RemovedTags) Console.WriteLine($"- {r}");
        foreach (var c in d.Changes) Console.WriteLine($"~ {c.Path}: {c.Before} -> {c.After}");
        return 0;
    }
    case "export-configs":
    {
        var data = Arg("--data", "data/tep");
        var outDir = Arg("--out", "config");
        Directory.CreateDirectory(outDir);
        // same model as the benchmark's final evaluation: all 500 normal training samples + the tuned parameters
        var fit = TepRun.Load(Path.Combine(data, "d00.dat")).Samples.ToList();
        var resultsPath = Arg("--results", "results/results.json");
        var p = new RationalizationParams();
        if (File.Exists(resultsPath))
        {
            var sel = JsonDocument.Parse(File.ReadAllText(resultsPath)).RootElement.GetProperty("selected");
            p = new RationalizationParams(SigmaK: sel.GetProperty("sigmaK").GetDouble(), OnDelay: sel.GetProperty("onDelay").GetInt32(),
                Confidence: sel.GetProperty("confidence").GetDouble(), VarianceToKeep: sel.GetProperty("varianceToKeep").GetDouble());
            Console.WriteLine($"using tuned parameters from {resultsPath}: {p}");
        }
        File.WriteAllText(Path.Combine(outDir, "as-found.json"), ConfigFactory.Raw(fit).ToJson());
        File.WriteAllText(Path.Combine(outDir, "rationalized.json"), ConfigFactory.Rationalized(fit, p).ToJson());
        Console.WriteLine($"wrote {outDir}/as-found.json and {outDir}/rationalized.json");
        return 0;
    }
    case "evaluate":
    {
        var data = Arg("--data", "data/tep");
        var outPath = Arg("--out", "results/results.json");
        var sw = Stopwatch.StartNew();
        var result = Benchmark.Run(data, Console.WriteLine);
        if (File.Exists(outPath))
        {
            // keep sections produced by other commands (load test, test counts)
            var prev = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(outPath))!;
            foreach (var (k, v) in prev) if (!result.ContainsKey(k)) result[k] = v;
        }
        result["evaluateSeconds"] = Math.Round(sw.Elapsed.TotalSeconds, 1);
        var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath));
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, json));
        Console.WriteLine($"wrote {outPath} in {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }
    case "loadtest":
        return await LoadTest.RunAsync(Arg("--url", "http://127.0.0.1:5080"), int.Parse(Arg("--concurrency", "32")),
            double.Parse(Arg("--seconds", "20")), Arg("--label", "default"), Arg("--out", "results/results.json"), json);
    default:
        Console.WriteLine("usage: deadband validate <config.json>... | diff <a.json> <b.json> | export-configs [--data dir] [--out dir] | evaluate [--data dir] [--out results.json] | loadtest --url URL [--concurrency N] [--seconds S] [--label L]");
        return cmd == "help" ? 0 : 2;
}
