namespace Bots.Domain;

/// <summary>Identidad de una corrida (fila de <c>bots.job_runs</c>) en su intento actual.</summary>
public sealed record JobRun
{
    public JobRun(long id, string jobName, DateTimeOffset scheduledForUtc, int attempt, DateTimeOffset startedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 1);
        UtcGuard.EnsureUtc(scheduledForUtc, nameof(scheduledForUtc));
        UtcGuard.EnsureUtc(startedAtUtc, nameof(startedAtUtc));

        Id = id;
        JobName = jobName;
        ScheduledForUtc = scheduledForUtc;
        Attempt = attempt;
        StartedAtUtc = startedAtUtc;
    }

    public long Id { get; }

    public string JobName { get; }

    /// <summary>Ocurrencia programada que atiende esta corrida (clave de idempotencia con <see cref="JobName"/>).</summary>
    public DateTimeOffset ScheduledForUtc { get; }

    /// <summary>Número de intento sobre la misma ocurrencia (1 en el primero).</summary>
    public int Attempt { get; }

    public DateTimeOffset StartedAtUtc { get; }
}
