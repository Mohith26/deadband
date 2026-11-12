using Asp.Versioning;
using Deadband.Api.Services;
using Deadband.Core.Tep;
using Microsoft.AspNetCore.Mvc;

namespace Deadband.Api.Controllers;

/// <summary>Tag database, live values and short trends.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/tags")]
public sealed class TagsController(PlantRuntime plant) : ControllerBase
{
    [HttpGet]
    public IEnumerable<TepCatalog.TagInfo> List() => TepCatalog.Tags;

    [HttpGet("snapshot")]
    public object Snapshot() => new { plantTimeSec = plant.Load().PlantTimeSec, values = plant.Snapshot() };

    [HttpGet("{tag}/trend")]
    public IActionResult Trend(string tag)
    {
        var t = plant.Trend(tag);
        return t is null ? Problem(statusCode: 404, title: $"unknown tag {tag}") : Ok(t.Select(p => new { t = p.t, v = p.v }));
    }
}

/// <summary>Operator alarm-load and line OEE KPIs.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/kpi")]
public sealed class KpiController(PlantRuntime plant) : ControllerBase
{
    /// <summary>Annunciated alarms in the last 10 minutes of plant time (ISA-18.2 target: about 1 or fewer; 10 or more = flood).</summary>
    [HttpGet("alarm-load")]
    public AlarmLoad AlarmLoad() => plant.Load();

    /// <summary>Availability x Performance x Quality since the replay started (continuous-process adaptation).</summary>
    [HttpGet("oee")]
    public object Oee()
    {
        var r = plant.Oee();
        return new { r.Availability, r.Performance, r.Quality, oee = r.Oee, r.Samples };
    }
}

public sealed record ReplayRequest(string Run);

/// <summary>Controls the Tennessee Eastman replay that feeds the alarm engine.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/replay")]
public sealed class ReplayController(PlantRuntime plant) : ControllerBase
{
    [HttpGet]
    public object Status() => new
    {
        run = plant.RunName, cursor = plant.Cursor, length = plant.RunLength, faultOnsetSample = plant.FaultOnset,
        fault = plant.FaultDescription, configVersion = plant.ConfigVersion, paused = ReplayService.Paused,
    };

    [HttpPost]
    public IActionResult Restart([FromBody] ReplayRequest req)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(req.Run, "^d[0-2][0-9](_te)?$")) return Problem(statusCode: 400, title: "run must look like d06_te");
        try { plant.Restart(req.Run); return Ok(Status()); }
        catch (FileNotFoundException ex) { return Problem(statusCode: 404, title: ex.Message); }
    }

    /// <summary>Advance the replay deterministically by n samples (used by tests and demos).</summary>
    [HttpPost("step")]
    public async Task<object> Step([FromQuery] int n = 1, CancellationToken ct = default)
    {
        var ev = await plant.StepAsync(Math.Clamp(n, 1, 5000), ct);
        return new { cursor = plant.Cursor, events = ev.Count, annunciated = ev.Count(e => e.Annunciated) };
    }

    [HttpPost("pause")]
    public object Pause([FromQuery] bool paused = true) { ReplayService.Paused = paused; return Status(); }
}
