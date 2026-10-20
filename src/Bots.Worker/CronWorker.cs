using Bots.Application;

namespace Bots.Worker;

/// <summary>
/// Capa delgada: delega todo en <see cref="SyncReposScheduler"/> (Application, probado allí). El
/// programador nunca propaga excepciones de una corrida; si aun así algo escapa, se registra aquí.
/// </summary>
internal sealed partial class CronWorker(SyncReposScheduler scheduler, ILogger<CronWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await scheduler.RunAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogSchedulerCrashed(logger, ex);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "El programador terminó de forma inesperada. Ver runbook: el bot no corrió.")]
    private static partial void LogSchedulerCrashed(ILogger logger, Exception exception);
}
