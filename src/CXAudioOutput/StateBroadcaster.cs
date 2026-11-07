using System.Diagnostics;

namespace CXAudioOutput;

public sealed class StateBroadcaster : BackgroundService
{
    private readonly AudioHub _hub;
    private readonly ILogger<StateBroadcaster> _logger;

    public StateBroadcaster(AudioHub hub, ILogger<StateBroadcaster> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        var lastPing = Stopwatch.GetTimestamp();

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await _hub.BroadcastStateAsync();

                var now = Stopwatch.GetTimestamp();
                if ((now - lastPing) / (double)Stopwatch.Frequency >= 5.0)
                {
                    lastPing = now;
                    await _hub.PingReceiversAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "State broadcaster stopped unexpectedly");
        }
    }
}
