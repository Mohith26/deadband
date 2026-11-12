using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Deadband.Core.Config;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Deadband.Api.Tests;

public sealed class DeadbandFactory : WebApplicationFactory<Program>
{
    public string Temp { get; } = Path.Combine(Path.GetTempPath(), "deadband-api-" + Guid.NewGuid().ToString("N"));

    public static string Root
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !File.Exists(Path.Combine(d.FullName, "Deadband.sln"))) d = d.Parent;
            return d!.FullName;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Temp);
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Alarms"] = $"Data Source={Path.Combine(Temp, "test.db")}",
            ["Plant:DataDir"] = Path.Combine(Root, "data", "tep"),
            ["Plant:ConfigDir"] = Path.Combine(Temp, "config-store"),
            ["Plant:SeedConfig"] = Path.Combine(Root, "config", "rationalized.json"),
            ["Plant:Run"] = "d06_te",
            ["Plant:AutoReplay"] = "false",
        }));
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(Temp, true); } catch (IOException) { }
    }
}

public class ApiTests : IClassFixture<DeadbandFactory>
{
    private readonly DeadbandFactory _f;
    private readonly HttpClient _c;
    private static readonly JsonSerializerOptions Json = new(AlarmConfig.Json) { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals };

    public ApiTests(DeadbandFactory f) { _f = f; _c = f.Client(); }

    private async Task<JsonNode> Get(string url)
    {
        var r = await _c.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return JsonNode.Parse(await r.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task HealthAndOpenApiDocument()
    {
        Assert.Equal("ok", (await Get("/healthz"))["status"]!.GetValue<string>());
        var doc = await Get("/swagger/v1/swagger.json");
        var paths = doc["paths"]!.AsObject().Select(p => p.Key).ToList();
        Assert.Contains("/api/v1/alarms/active", paths);
        Assert.Contains("/api/v1/config/rollback/{v}", paths);
    }

    [Fact]
    public async Task UnsupportedApiVersionIsRejected()
    {
        var r = await _c.GetAsync("/api/v2/alarms/active");
        Assert.Contains(r.StatusCode, new[] { HttpStatusCode.BadRequest, HttpStatusCode.NotFound });
        var ok = await _c.GetAsync("/api/v1/alarms/active");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("1.0", ok.Headers.GetValues("api-supported-versions").Single());
    }

    [Fact]
    public async Task ReplayRaisesAlarmsJournalsThemAndReportsKpis()
    {
        await _c.PostAsJsonAsync("/api/v1/replay", new { run = "d06_te" });
        var step = await (await _c.PostAsync("/api/v1/replay/step?n=220", null)).Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal(220, step!["cursor"]!.GetValue<int>());
        var active = (await Get("/api/v1/alarms/active")).AsArray();
        Assert.NotEmpty(active);
        Assert.Contains(active, a => a!["tag"]!.GetValue<string>() is "XMEAS01" or "PROCESS.DEVIATION");
        var hist = (await Get("/api/v1/alarms/history?limit=5")).AsArray();
        Assert.Equal(5, hist.Count);
        var tagHist = (await Get("/api/v1/alarms/history?tag=XMEAS01&limit=10")).AsArray();
        Assert.All(tagHist, h => Assert.Equal("XMEAS01", h!["tag"]!.GetValue<string>()));
        var load = await Get("/api/v1/kpi/alarm-load");
        Assert.True(load["annunciatedPer10MinAverage"]!.GetValue<double>() > 0);
        var oee = await Get("/api/v1/kpi/oee");
        Assert.InRange(oee["oee"]!.GetValue<double>(), 0, 1);
        var snap = await Get("/api/v1/tags/snapshot");
        Assert.True(snap["values"]!.AsArray().Count >= 52);
        Assert.Equal(220, (await Get("/api/v1/tags/XMEAS07/trend")).AsArray().Count);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.GetAsync("/api/v1/tags/NOPE/trend")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.GetAsync("/api/v1/alarms/history?limit=5000")).StatusCode);
    }

    [Fact]
    public async Task ShelvingRequiresReasonAndLeavesAuditTrail()
    {
        var bad = await _c.PostAsJsonAsync("/api/v1/alarms/XMEAS09/shelve", new { user = "op1", reason = "", durationMinutes = 30 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var unknown = await _c.PostAsJsonAsync("/api/v1/alarms/XMEAS30/shelve", new { user = "op1", reason = "x", durationMinutes = 30 });
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode); // analyzer tags are not individually alarmed
        var ok = await _c.PostAsJsonAsync("/api/v1/alarms/XMEAS09/shelve", new { user = "op1", reason = "TT-9 under maintenance", durationMinutes = 30 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains((await Get("/api/v1/alarms/shelved")).AsArray(), s => s!["tag"]!.GetValue<string>() == "XMEAS09");
        var audit = (await Get("/api/v1/alarms/audit")).AsArray();
        Assert.Contains(audit, a => a!["type"]!.GetValue<string>() == "Shelved" && a["user"]!.GetValue<string>() == "op1");
        Assert.Equal(HttpStatusCode.OK, (await _c.DeleteAsync("/api/v1/alarms/XMEAS09/shelve?user=op1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.DeleteAsync("/api/v1/alarms/XMEAS09/shelve?user=op1")).StatusCode);
    }

    [Fact]
    public async Task AcknowledgeActiveAlarm()
    {
        await _c.PostAsJsonAsync("/api/v1/replay", new { run = "d06_te" });
        await _c.PostAsync("/api/v1/replay/step?n=200", null);
        var a = (await Get("/api/v1/alarms/active")).AsArray().First()!;
        var r = await _c.PostAsJsonAsync($"/api/v1/alarms/{a["tag"]}/{a["kind"]}/ack", new { user = "op2" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var again = (await Get("/api/v1/alarms/active")).AsArray().First(x => x!["tag"]!.ToString() == a["tag"]!.ToString() && x["kind"]!.ToString() == a["kind"]!.ToString())!;
        Assert.True(again["acknowledged"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PostAsJsonAsync("/api/v1/alarms/XMEAS09/LoLo/ack", new { user = "op2" })).StatusCode);
    }

    [Fact]
    public async Task ConfigValidatePublishDiffRollback()
    {
        var versions = await Get("/api/v1/config/versions");
        int start = versions["active"]!.GetValue<int>();
        var active = (await _c.GetFromJsonAsync<AlarmConfig>("/api/v1/config/active", Json))!;

        var broken = active with { Tags = active.Tags.Select(t => t.Tag == "XMEAS07" ? t with { Lo = 99999 } : t).ToList() };
        var val = await (await _c.PostAsJsonAsync("/api/v1/config/validate", broken, Json)).Content.ReadFromJsonAsync<JsonNode>();
        Assert.False(val!["valid"]!.GetValue<bool>());
        Assert.Contains(val["issues"]!.AsArray(), i => i!["code"]!.GetValue<string>() == "LIMIT_ORDER");
        var rejected = await _c.PostAsJsonAsync("/api/v1/config/versions", new { config = broken, author = "eng1", message = "bad" }, Json);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, rejected.StatusCode);

        var tightened = active with { Tags = active.Tags.Select(t => t.Tag == "XMEAS09" ? t with { OnDelaySamples = 3 } : t).ToList() };
        var pub = await _c.PostAsJsonAsync("/api/v1/config/versions", new { config = tightened, author = "eng1", message = "slower reactor temp alarm" }, Json);
        Assert.Equal(HttpStatusCode.Created, pub.StatusCode);
        int v2 = (await pub.Content.ReadFromJsonAsync<JsonNode>())!["version"]!.GetValue<int>();
        Assert.Equal(start + 1, v2);
        Assert.Equal(v2, (await Get("/api/v1/replay"))["configVersion"]!.GetValue<int>());

        var diff = await Get($"/api/v1/config/diff?from={start}&to={v2}");
        var change = diff["changes"]!.AsArray().Single()!;
        Assert.Equal("tags[XMEAS09].onDelaySamples", change["path"]!.GetValue<string>());

        var rb = await _c.PostAsJsonAsync($"/api/v1/config/rollback/{start}", new { author = "eng2", reason = "revert" });
        Assert.Equal(HttpStatusCode.OK, rb.StatusCode);
        int v3 = (await rb.Content.ReadFromJsonAsync<JsonNode>())!["version"]!.GetValue<int>();
        Assert.Equal(v2 + 1, v3);
        Assert.Empty((await Get($"/api/v1/config/diff?from={start}&to={v3}"))["changes"]!.AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PostAsJsonAsync("/api/v1/config/rollback/999", new { author = "x" })).StatusCode);
    }

    [Fact]
    public async Task ServerSentEventsStreamAlarms()
    {
        await _c.PostAsJsonAsync("/api/v1/replay", new { run = "d06_te" });
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/alarms/stream?max=1");
        using var resp = await _c.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        Assert.StartsWith(": connected", await reader.ReadLineAsync(cts.Token));
        await _c.PostAsync("/api/v1/replay/step?n=200", null);
        string? line;
        do { line = await reader.ReadLineAsync(cts.Token); } while (line != null && !line.StartsWith("data:"));
        Assert.NotNull(line);
        var ev = JsonNode.Parse(line!["data:".Length..])!;
        Assert.False(string.IsNullOrEmpty(ev["tag"]!.GetValue<string>()));
    }

    [Fact]
    public async Task ReplayValidatesRunNames()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _c.PostAsJsonAsync("/api/v1/replay", new { run = "../etc/passwd" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _c.PostAsJsonAsync("/api/v1/replay", new { run = "d19_te" })).StatusCode);
    }
}
