using Microsoft.Extensions.Options;
using SeatRes.Api.Data;

namespace SeatRes.Api.Background;

/// <summary>Runs payment recovery every 10 s and the reconciler every 15 s. Neither ever moves a seat.</summary>
public sealed class PeriodicWorker(
    PaymentRecovery recovery, Reconciler reconciler, MigrationState migrations, IOptions<SeatResOptions> options,
    ILogger<PeriodicWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.BackgroundEnabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        var tick = 0;
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!migrations.Applied) continue;
            tick++;
            if (tick % 2 == 0) await Run("recovery", () => recovery.RunOnceAsync(stoppingToken));
            if (tick % 3 == 0) await Run("reconciler", () => reconciler.RunOnceAsync(stoppingToken));
        }
    }

    private async Task Run(string job, Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("{event} {job} {error}", "background.job_failed", job, ex.Message);
        }
    }
}
