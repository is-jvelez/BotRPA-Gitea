using Bots.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Bots.Application;

/// <summary>
/// Determina qué ocurrencia programada está pendiente (anti-omisión). Si hay varias perdidas
/// devuelve solo la más reciente: el job sincroniza el estado completo y es idempotente, así
/// que ejecutar las anteriores no aportaría datos.
/// </summary>
public sealed partial class PendingOccurrenceCalculator(
    ISchedule schedule,
    IJobRunStore jobRuns,
    TimeProvider timeProvider,
    ILogger<PendingOccurrenceCalculator> logger)
{
    private const int MaxSkippedToLog = 20;

    /// <summary>
    /// Ocurrencia más reciente en (última completada, ahora], o <c>null</c> si no hay ninguna
    /// o no existe historial (sin historial no hay recuperación: se espera a la siguiente).
    /// </summary>
    public async Task<DateTimeOffset?> GetPendingAsync(string jobName, CancellationToken ct)
    {
        var lastCompleted = await jobRuns.GetLastCompletedScheduledAsync(jobName, ct);
        if (lastCompleted is null)
        {
            LogNoHistory(logger, jobName);
            return null;
        }

        var now = timeProvider.GetUtcNow();
        // Las ocurrencias llegan en orden ascendente: con MaxSkippedToLog + 1 primeras basta para listar
        // las omitidas (todas menos la última) sin guardar el rango completo.
        var missed = OccurrenceWindow.From(schedule.GetOccurrences(lastCompleted.Value, now), MaxSkippedToLog + 1);
        if (missed.Latest is not { } pending)
        {
            return null;
        }

        if (missed.Count > 1)
        {
            var skippedCount = missed.Count - 1;
            if (logger.IsEnabled(LogLevel.Warning))
            {
                LogSkipped(
                    logger, jobName, missed.Count, lastCompleted.Value, pending, skippedCount,
                    string.Join(", ", missed.First.Take(Math.Min(skippedCount, MaxSkippedToLog)).Select(o => o.ToString("o"))),
                    skippedCount > MaxSkippedToLog ? $" (y {skippedCount - MaxSkippedToLog} más)" : string.Empty);
            }
        }
        else
        {
            LogPending(logger, jobName, pending);
        }

        return pending;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{JobName} no tiene corridas completadas previas: no hay recuperación que hacer.")]
    private static partial void LogNoHistory(ILogger logger, string jobName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{JobName}: {MissedCount} ocurrencias pendientes desde la última completada ({LastCompletedUtc:o}). Se ejecuta solo la más reciente ({PendingUtc:o}); se omiten {SkippedCount}: {SkippedOccurrences}{SkippedMore}")]
    private static partial void LogSkipped(
        ILogger logger, string jobName, int missedCount, DateTimeOffset lastCompletedUtc, DateTimeOffset pendingUtc,
        int skippedCount, string skippedOccurrences, string skippedMore);

    [LoggerMessage(Level = LogLevel.Information, Message = "{JobName}: ocurrencia pendiente {PendingUtc:o}.")]
    private static partial void LogPending(ILogger logger, string jobName, DateTimeOffset pendingUtc);
}
