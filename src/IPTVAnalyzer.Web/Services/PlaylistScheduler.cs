namespace IPTVAnalyzer.Web.Services;

/// <summary>Starts playlist check runs when they are due.</summary>
public sealed class PlaylistScheduler(MonitorRegistry registry, ILogger<PlaylistScheduler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            foreach (var p in registry.Playlists)
            {
                if (p.Running || p.NextRun is not { } due || due > DateTimeOffset.UtcNow) continue;
                p.NextRun = p.Config.IntervalMinutes > 0 ? DateTimeOffset.UtcNow.AddMinutes(p.Config.IntervalMinutes) : null;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await p.RunAsync(stoppingToken);
                        if (p.Config.IntervalMinutes > 0) p.NextRun = DateTimeOffset.UtcNow.AddMinutes(p.Config.IntervalMinutes);
                    }
                    catch (Exception ex) { logger.LogError(ex, "Playlist run failed for {Name}", p.Config.Name); }
                }, stoppingToken);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
