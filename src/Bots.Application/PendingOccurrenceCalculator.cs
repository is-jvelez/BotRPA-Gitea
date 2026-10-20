using Bots.Application.Ports;
using Microsoft.Extensions.Logging;

namespace Bots.Application;

/// <summary>
/// Determina qué ocurrencia programada está pendiente (anti-omisión). Si hay varias perdidas
/// devuelve solo la más reciente: el job sincroniza el estado completo y es idempotente, así
/// que ejecutar las anteriores no aportaría datos.
/// </summary>
public sealed class PendingOccurrenceCalculator(
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
            logger.LogInformation("{JobName} no tiene corridas completadas previas: no hay recuperación que hacer.", jobName);
            return null;
        }

        var now = timeProvider.GetUtcNow();
        var missed = schedule.GetOccurrences(lastCompleted.Value, now);
        if (missed.Count == 0)
        {
            return null;
        }

        var ordered = missed.Order().ToList();
        var pending = ordered[^1];

        if (ordered.Count > 1)
        {
            var skipped = ordered.Take(ordered.Count - 1).ToList();
            logger.LogWarning(
                "{JobName}: {MissedCount} ocurrencias pendientes desde la última completada ({LastCompletedUtc:o}). Se ejecuta solo la más reciente ({PendingUtc:o}); se omiten {SkippedCount}: {SkippedOccurrences}{SkippedMore}",
                jobName, ordered.Count, lastCompleted.Value, pending, skipped.Count,
                string.Join(", ", skipped.Take(MaxSkippedToLog).Select(o => o.ToString("o"))),
                skipped.Count > MaxSkippedToLog ? $" (y {skipped.Count - MaxSkippedToLog} más)" : string.Empty);
        }
        else
        {
            logger.LogInformation("{JobName}: ocurrencia pendiente {PendingUtc:o}.", jobName, pending);
        }

        return pending;
    }
}
