namespace DevBridge.Services;

public sealed class SessionCleanupService(SessionStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken)) store.RemoveExpired();
    }
}
