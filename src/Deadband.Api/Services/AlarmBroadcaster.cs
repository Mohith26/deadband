using System.Collections.Concurrent;
using System.Threading.Channels;
using Deadband.Core.Alarms;

namespace Deadband.Api.Services;

/// <summary>Fan-out of alarm events to Server-Sent Events subscribers. Slow subscribers drop oldest events, never block the engine.</summary>
public sealed class AlarmBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Channel<AlarmEvent>> _subs = new();

    public (Guid id, ChannelReader<AlarmEvent> reader) Subscribe()
    {
        var ch = Channel.CreateBounded<AlarmEvent>(new BoundedChannelOptions(500) { FullMode = BoundedChannelFullMode.DropOldest });
        var id = Guid.NewGuid();
        _subs[id] = ch;
        return (id, ch.Reader);
    }

    public void Unsubscribe(Guid id)
    {
        if (_subs.TryRemove(id, out var ch)) ch.Writer.TryComplete();
    }

    public int Subscribers => _subs.Count;

    public void Publish(AlarmEvent e)
    {
        foreach (var ch in _subs.Values) ch.Writer.TryWrite(e);
    }
}

public sealed class ReplayService(PlantRuntime plant, Microsoft.Extensions.Options.IOptions<PlantOptions> opt, ILogger<ReplayService> log) : BackgroundService
{
    public static volatile bool Paused;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!opt.Value.AutoReplay) return;
        log.LogInformation("replaying {Run} every {Ms} ms", plant.RunName, opt.Value.IntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(opt.Value.IntervalMs));
        while (await timer.WaitForNextTickAsync(ct))
        {
            if (Paused) continue;
            try { await plant.StepAsync(1, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogError(ex, "replay step failed"); }
        }
    }
}
