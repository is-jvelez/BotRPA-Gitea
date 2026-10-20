using Bots.Domain;

namespace Bots.Application.Ports;

public interface IJobRunStore
{
    /// <summary>
    /// Toma el lock del job y abre (o retoma) la corrida de la ocurrencia <paramref name="scheduledForUtc"/>.
    /// Devuelve <c>null</c> si otra instancia tiene el lock, si la ocurrencia ya está completada
    /// o si se agotaron los intentos.
    /// </summary>
    Task<IJobRunLease?> TryStartAsync(string jobName, DateTimeOffset scheduledForUtc, CancellationToken ct);

    Task CompleteAsync(JobRun run, JobRunStatus status, int reposOk, int reposFailed, string? errorSummary, CancellationToken ct);

    /// <summary>Última ocurrencia programada en estado <c>Succeeded</c> o <c>PartiallyFailed</c>.</summary>
    Task<DateTimeOffset?> GetLastCompletedScheduledAsync(string jobName, CancellationToken ct);

    /// <summary>Marca <c>Abandoned</c> las corridas <c>Running</c> iniciadas hace más de <paramref name="olderThan"/>. Devuelve cuántas.</summary>
    Task<int> MarkStaleRunsAbandonedAsync(string jobName, TimeSpan olderThan, CancellationToken ct);

    /// <summary>Registra un error de la corrida (en el intento actual). <paramref name="repoKey"/> es <c>null</c> para errores de toda la corrida.</summary>
    Task AddErrorAsync(JobRun run, string? repoKey, string errorType, string message, CancellationToken ct);
}
