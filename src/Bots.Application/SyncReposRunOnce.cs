using Bots.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Bots.Application;

/// <summary>
/// Modo de una sola corrida (lo ejecuta el RunOnceWorker): un cron externo (supercronic, CronJob de
/// Kubernetes, job de Container Apps) lanza el proceso a la hora y este corre la ocurrencia que toca y termina.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Al arrancar marca Abandoned las corridas Running huérfanas, igual que el modo continuo.</item>
/// <item>La ocurrencia que corre es la más reciente que ya llegó: la pendiente según el historial o, si no
/// hay historial, la más reciente dentro de <see cref="SyncReposRunOnceOptions.Lookback"/>. Si el cron
/// externo dispara a la hora, coincide con la que acaba de llegar.</item>
/// <item>Si esa ocurrencia ya está completada (otra ejecución la hizo) <see cref="SyncReposJob"/> no la repite.</item>
/// <item>El anti-solapamiento entre ejecuciones lo dan el applock y el UNIQUE de job_runs.</item>
/// </list>
/// </remarks>
public sealed partial class SyncReposRunOnce(
    ISchedule schedule,
    IJobRunStore jobRuns,
    PendingOccurrenceCalculator calculator,
    SyncReposJob job,
    SyncReposRunOnceOptions options,
    TimeProvider timeProvider,
    ILogger<SyncReposRunOnce> logger)
{
    /// <summary>
    /// Corre la ocurrencia que toca. Devuelve <c>null</c> si no hubo nada que correr o no se inició (lock
    /// tomado, ya completada, intentos agotados). Las excepciones se propagan: el proceso las convierte en
    /// código de salida.
    /// </summary>
    public async Task<SyncReposResult?> RunAsync(CancellationToken ct)
    {
        await MarkStaleRunsAsync(ct);

        var now = timeProvider.GetUtcNow();
        var pending = await calculator.GetPendingAsync(options.JobName, ct);
        var latest = OccurrenceWindow.From(schedule.GetOccurrences(now - options.Lookback, now), 0).Latest;
        var occurrence = pending is null ? latest : latest is null ? pending : (pending > latest ? pending : latest);

        if (occurrence is not { } toRun)
        {
            LogNothingToRun(logger, options.JobName, options.Lookback);
            return null;
        }

        LogRunning(logger, options.JobName, toRun);
        return await job.RunAsync(toRun, ct);
    }

    private async Task MarkStaleRunsAsync(CancellationToken ct)
    {
        try
        {
            await jobRuns.MarkStaleRunsAbandonedAsync(options.JobName, options.StaleRunThreshold, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // TryStartAsync también trata las huérfanas de la ocurrencia que retome.
            LogMarkStaleFailed(logger, ex, options.JobName);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{JobName}: modo RunOnce, se corre la ocurrencia {ScheduledForUtc:o}.")]
    private static partial void LogRunning(ILogger logger, string jobName, DateTimeOffset scheduledForUtc);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{JobName}: modo RunOnce sin ocurrencia que correr (ninguna en las últimas {Lookback} ni pendiente según el historial).")]
    private static partial void LogNothingToRun(ILogger logger, string jobName, TimeSpan lookback);

    [LoggerMessage(Level = LogLevel.Error, Message = "{JobName}: no se pudieron marcar las corridas huérfanas al arrancar; se continúa.")]
    private static partial void LogMarkStaleFailed(ILogger logger, Exception exception, string jobName);
}
