using Asp.Versioning;
using Deadband.Api.Services;
using Deadband.Core.Config;
using Deadband.Core.Eval;
using Microsoft.AspNetCore.Mvc;

namespace Deadband.Api.Controllers;

public sealed record PublishRequest(AlarmConfig Config, string Author, string Message);
public sealed record RollbackRequest(string Author, string? Reason);

/// <summary>Versioned alarm configuration: validate, publish, diff, roll back. History is append-only.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/config")]
public sealed class ConfigController(PlantRuntime plant) : ControllerBase
{
    private ConfigStore Store => plant.Store;

    [HttpGet("versions")]
    public object Versions() => new { active = Store.ActiveVersion, versions = Store.Versions };

    [HttpGet("versions/{v:int}")]
    public IActionResult Get(int v)
    {
        try { return Ok(Store.Get(v)); }
        catch (KeyNotFoundException ex) { return Problem(statusCode: 404, title: ex.Message); }
    }

    [HttpGet("active")]
    public AlarmConfig Active() => Store.GetActive();

    /// <summary>Dry-run validation; returns every issue with a JSON path.</summary>
    [HttpPost("validate")]
    public object Validate([FromBody] AlarmConfig config)
    {
        var issues = ConfigValidator.Validate(config, Evaluator.TagIds.ToHashSet());
        return new { valid = issues.Count == 0, issues };
    }

    /// <summary>Publish a new version (validated first) and hot-swap it into the running alarm engine.</summary>
    [HttpPost("versions")]
    public IActionResult Publish([FromBody] PublishRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Author) || string.IsNullOrWhiteSpace(req.Message))
            return Problem(statusCode: 400, title: "author and message are required for every configuration change");
        try
        {
            var info = Store.Publish(req.Config, req.Author, req.Message);
            return CreatedAtAction(nameof(Get), new { v = info.Version, version = "1.0" }, info);
        }
        catch (ConfigValidationException ex)
        {
            return UnprocessableEntity(new { title = ex.Message, issues = ex.Issues });
        }
    }

    [HttpGet("diff")]
    public IActionResult Diff([FromQuery] int from, [FromQuery] int to)
    {
        try { return Ok(ConfigDiff.Compute(Store.Get(from), Store.Get(to))); }
        catch (KeyNotFoundException ex) { return Problem(statusCode: 404, title: ex.Message); }
    }

    /// <summary>Roll back by re-publishing an earlier version as a new version (history is never rewritten).</summary>
    [HttpPost("rollback/{v:int}")]
    public IActionResult Rollback(int v, [FromBody] RollbackRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Author)) return Problem(statusCode: 400, title: "author is required");
        try { return Ok(Store.Rollback(v, req.Author, req.Reason)); }
        catch (KeyNotFoundException ex) { return Problem(statusCode: 404, title: ex.Message); }
    }
}
