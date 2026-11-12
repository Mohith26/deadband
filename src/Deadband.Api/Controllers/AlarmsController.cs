using System.Text.Json;
using Asp.Versioning;
using Deadband.Api.Data;
using Deadband.Api.Services;
using Deadband.Core.Alarms;
using Deadband.Core.Config;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Deadband.Api.Controllers;

public sealed record ShelveRequest(string User, string Reason, double DurationMinutes);
public sealed record OperatorRequest(string User);

/// <summary>Live alarm state, journal queries and operator actions (acknowledge, shelve).</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/alarms")]
public sealed class AlarmsController(PlantRuntime plant, AlarmDb db, AlarmBroadcaster bus) : ControllerBase
{
    // SSE frames must be single-line JSON; NaN appears on operator-action events that carry no value
    private static readonly JsonSerializerOptions SseJson = new(AlarmConfig.Json)
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>Currently active alarms, highest priority first (includes suppressed ones with the reason).</summary>
    [HttpGet("active")]
    public IReadOnlyList<ActiveAlarm> Active() => plant.Active();

    /// <summary>Alarm journal, newest first. Filter by tag and/or event type; page with beforeId.</summary>
    [HttpGet("history")]
    public async Task<IActionResult> History([FromQuery] string? tag, [FromQuery] AlarmEventType? type, [FromQuery] long? beforeId,
        [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        if (limit is < 1 or > 1000) return Problem(statusCode: 400, title: "limit must be 1..1000");
        var q = db.Events.AsNoTracking().AsQueryable();
        if (tag != null) q = q.Where(e => e.Tag == tag);
        if (type != null) q = q.Where(e => e.Type == type);
        if (beforeId != null) q = q.Where(e => e.Id < beforeId);
        var rows = tag != null
            ? await q.OrderByDescending(e => e.TimeSec).ThenByDescending(e => e.Id).Take(limit).ToListAsync(ct)
            : await q.OrderByDescending(e => e.Id).Take(limit).ToListAsync(ct);
        return Ok(rows);
    }

    /// <summary>Operator audit trail: shelving, unshelving, acknowledgements and automatic chatter suppression.</summary>
    [HttpGet("audit")]
    public async Task<IReadOnlyList<AlarmEventRow>> Audit([FromQuery] int limit = 100, CancellationToken ct = default) =>
        await db.Events.AsNoTracking().Where(e => e.User != null).OrderByDescending(e => e.Id).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(ct);

    [HttpGet("shelved")]
    public IReadOnlyList<ShelvedTag> Shelved() => plant.Shelved();

    /// <summary>Shelve all alarms on a tag for a bounded time. Requires a user and a reason (ISA-18.2 shelving).</summary>
    [HttpPost("{tag}/shelve")]
    public async Task<IActionResult> Shelve(string tag, [FromBody] ShelveRequest req, CancellationToken ct)
    {
        try { return Ok(await plant.OperatorActionAsync(e => e.Shelve(tag, req.DurationMinutes * 60, req.User, req.Reason), ct)); }
        catch (KeyNotFoundException ex) { return Problem(statusCode: 404, title: ex.Message); }
        catch (ArgumentException ex) { return Problem(statusCode: 400, title: ex.Message); }
    }

    [HttpDelete("{tag}/shelve")]
    public async Task<IActionResult> Unshelve(string tag, [FromQuery] string user, CancellationToken ct)
    {
        try { return Ok(await plant.OperatorActionAsync(e => e.Unshelve(tag, user), ct)); }
        catch (KeyNotFoundException ex) { return Problem(statusCode: 404, title: ex.Message); }
    }

    [HttpPost("{tag}/{kind}/ack")]
    public async Task<IActionResult> Acknowledge(string tag, AlarmKind kind, [FromBody] OperatorRequest req, CancellationToken ct)
    {
        try { return Ok(await plant.OperatorActionAsync(e => e.Acknowledge(tag, kind, req.User), ct)); }
        catch (KeyNotFoundException ex) { return Problem(statusCode: 404, title: ex.Message); }
    }

    /// <summary>Server-Sent Events stream of alarm events (text/event-stream).</summary>
    [HttpGet("stream")]
    public async Task Stream([FromQuery] int? max, CancellationToken ct)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        var (id, reader) = bus.Subscribe();
        try
        {
            await Response.WriteAsync(": connected\n\n", ct);
            await Response.Body.FlushAsync(ct);
            int sent = 0;
            await foreach (var e in reader.ReadAllAsync(ct))
            {
                await Response.WriteAsync($"event: alarm\ndata: {JsonSerializer.Serialize(e, SseJson)}\n\n", ct);
                await Response.Body.FlushAsync(ct);
                if (max is int m && ++sent >= m) break;
            }
        }
        catch (OperationCanceledException) { }
        finally { bus.Unsubscribe(id); }
    }
}
