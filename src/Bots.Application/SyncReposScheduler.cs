using Bots.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Bots.Application;

/// <summary>
/// Bucle de programación del job (lo ejecuta el CronWorker). Vive en Application para poder probarse.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Al arrancar: marca Abandoned las corridas Running huérfanas y recupera la ocurrencia pendiente.</item>
/// <item>Espera hasta la siguiente ocurrencia y, al despertar, vuelve a calcular la pendiente
/// (anti-omisión): se ejecuta la más reciente entre la pendiente y la que se esperaba.</item>
/// <item>Anti-solapamiento: las corridas son secuenciales; si llegan ocurrencias mientras una corre, no se
/// inicia otra en paralelo, se registra el salto y al terminar se recupera la más reciente.</item>
/// <item>Ninguna excepción de una corrida detiene el bucle: se registra y se espera a la siguiente.</item>
/// </list>
/// </remarks>
public sealed class SyncReposScheduler(
    ISchedule schedule,
    IJobRunStore jobRuns,
    PendingOccurrenceCalculator calculator,
    SyncReposJob job,
    SyncReposSchedulerOptions options,
    TimeProvider timeProvider,
    ILogger<SyncReposScheduler> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        logger.LogInformation("{JobName}: programador iniciado.", options.JobName);
        await MarkStaleRunsAsync(ct);

        // Ocurrencia por la que se despertó (null al arrancar: solo hay recuperación).
        DateTimeOffset? due = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var occurrence = Latest(await GetPendingAsync(ct), due);
                due = null;

                if (occurrence is { } toRun)
                {
                    await RunOccurrenceAsync(toRun, ct);
                    if (LogOverlap(toRun))
                    {
                        // Llegaron ocurrencias mientras corría: se recupera la más reciente sin esperar.
                        continue;
                    }
                }

                var next = schedule.GetNextOccurrence(timeProvider.GetUtcNow());
                if (next is null)
                {
                    logger.LogWarning("{JobName}: la expresión cron no tiene más ocurrencias; el programador se detiene.", options.JobName);
                    return;
                }

                logger.LogInformation("{JobName}: siguiente ocurrencia {NextUtc:o}.", options.JobName, next.Value);
                await WaitUntilAsync(next.Value, ct);
                due = next;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Apagado ordenado.
        }

        logger.LogInformation("{JobName}: programador detenido.", options.JobName);
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
            logger.LogError(ex, "{JobName}: no se pudieron marcar las corridas huérfanas al arrancar; se continúa.", options.JobName);
        }
    }

    private async Task<DateTimeOffset?> GetPendingAsync(CancellationToken ct)
    {
        try
        {
            return await calculator.GetPendingAsync(options.JobName, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "{JobName}: no se pudo calcular la ocurrencia pendiente; se reintentará en el siguiente despertar.", options.JobName);
            return null;
        }
    }

    private async Task RunOccurrenceAsync(DateTimeOffset occurrence, CancellationToken ct)
    {
        try
        {
            await job.RunAsync(occurrence, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Nunca se propaga: un fallo de una corrida no debe matar el BackgroundService.
            logger.LogError(ex,
                "{JobName}: la corrida de {ScheduledForUtc:o} terminó con una excepción no controlada; se espera a la siguiente ocurrencia.",
                options.JobName, occurrence);
        }
    }

    /// <summary>Registra las ocurrencias que llegaron mientras corría <paramref name="ran"/>. Devuelve si hubo.</summary>
    private bool LogOverlap(DateTimeOffset ran)
    {
        var arrived = schedule.GetOccurrences(ran, timeProvider.GetUtcNow());
        if (arrived.Count == 0)
        {
            return false;
        }

        logger.LogWarning(
            "{JobName}: {Count} ocurrencias llegaron mientras corría la de {ScheduledForUtc:o} ({Occurrences}); no se iniciaron en paralelo. Se recupera la más reciente.",
            options.JobName, arrived.Count, ran, string.Join(", ", arrived.Select(o => o.ToString("o"))));
        return true;
    }

    private async Task WaitUntilAsync(DateTimeOffset target, CancellationToken ct)
    {
        while (true)
        {
            var remaining = target - timeProvider.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining < options.MaxSingleWait ? remaining : options.MaxSingleWait, timeProvider, ct);
        }
    }

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : (a > b ? a : b);
}
