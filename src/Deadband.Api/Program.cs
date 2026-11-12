using System.Text.Json.Serialization;
using Asp.Versioning;
using Deadband.Api.Data;
using Deadband.Api.Services;
using Deadband.Core.Alarms;
using Deadband.Core.Config;
using Deadband.Core.Eval;
using Deadband.Core.Tep;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<PlantOptions>(builder.Configuration.GetSection("Plant"));
builder.Services.Configure<DbOptions>(builder.Configuration.GetSection("Db"));
builder.Services.AddDbContextFactory<AlarmDb>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Alarms") ?? "Data Source=data/deadband.db"));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AlarmDb>>().CreateDbContext());
builder.Services.AddSingleton<AlarmBroadcaster>();
builder.Services.AddSingleton<PlantRuntime>();
builder.Services.AddHostedService<ReplayService>();

builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});
builder.Services.AddProblemDetails();
builder.Services.AddApiVersioning(o =>
{
    o.DefaultApiVersion = new ApiVersion(1, 0);
    o.ReportApiVersions = true;
}).AddMvc().AddApiExplorer(o =>
{
    o.GroupNameFormat = "'v'VVV";
    o.SubstituteApiVersionInUrl = true;
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "Deadband alarm management API", Version = "v1" });
    var xml = Path.Combine(AppContext.BaseDirectory, "Deadband.Api.xml");
    if (File.Exists(xml)) o.IncludeXmlComments(xml);
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:4200").AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AlarmDb>();
    var cs = db.Database.GetConnectionString() ?? "";
    var file = cs.Replace("Data Source=", "").Trim();
    var dir = Path.GetDirectoryName(file);
    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}
app.Services.GetRequiredService<PlantRuntime>(); // fail fast on bad data/config paths

app.UseCors();
app.UseSwagger();
app.UseSwaggerUI(o => o.SwaggerEndpoint("/swagger/v1/swagger.json", "v1"));
app.MapControllers();
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// Load-test helper: fill the journal with a realistic history by replaying TEP runs through the as-found configuration.
if (app.Configuration.GetValue<bool>("Admin:Enabled"))
{
    app.MapPost("/api/v1/admin/shutdown", (IHostApplicationLifetime life) => { life.StopApplication(); return Results.Accepted(); });
    app.MapPost("/api/v1/admin/seed-history", async (int repeat, IDbContextFactory<AlarmDb> dbf, Microsoft.Extensions.Options.IOptions<PlantOptions> opt) =>
    {
        var normal = TepRun.Load(Path.Combine(opt.Value.DataDir, "d00.dat")).Samples;
        var cfg = ConfigFactory.Raw(normal);
        var rows = new List<AlarmEventRow>();
        var runs = TepRun.LoadDirectory(opt.Value.DataDir, test: true).ToList();
        for (int k = 0; k < repeat; k++)
            foreach (var run in runs)
            {
                var engine = new AlarmEngine(cfg, Evaluator.TagIds);
                for (int i = 0; i < run.Length; i++)
                    foreach (var e in engine.Process(i * TepCatalog.SamplePeriodSec + k * 1e6, run.Samples[i]))
                        rows.Add(AlarmEventRow.From(e, $"{run.Name}#{k}", 0));
            }
        foreach (var chunk in rows.Chunk(20000))
        {
            await using var db = await dbf.CreateDbContextAsync();
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.Events.AddRange(chunk);
            await db.SaveChangesAsync();
        }
        return Results.Ok(new { inserted = rows.Count, runs = runs.Count, repeat });
    });
}

app.Run();

public partial class Program;
