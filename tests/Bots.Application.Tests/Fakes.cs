using Bots.Application.Ports;
using Bots.Domain;
using Microsoft.Extensions.Logging;

namespace Bots.Application.Tests;

internal static class Snapshots
{
    public static readonly DateTimeOffset CapturedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static RepoSnapshot For(string key) =>
        new(key, key.Split('/')[1], null, hasReadme: true, pullRequestCount: 1, [new BranchSnapshot("main", 3)], CapturedAt);
}

internal sealed class FakeRepoSource : IRepoSource
{
    public List<string> Keys { get; } = [];

    public HashSet<string> FailingKeys { get; } = [];

    public Exception? ListException { get; set; }

    /// <summary>Se invoca al pedir el detalle de cada repo (antes de devolverlo).</summary>
    public Action<string>? OnGet { get; set; }

    public int ListCalls { get; private set; }

    public List<string> Requested { get; } = [];

    public Task<IReadOnlyList<string>> ListRepoKeysAsync(CancellationToken ct)
    {
        ListCalls++;
        ct.ThrowIfCancellationRequested();
        if (ListException is not null)
        {
            throw ListException;
        }

        return Task.FromResult<IReadOnlyList<string>>(Keys.ToList());
    }

    public Task<RepoSnapshot> GetRepoAsync(string externalKey, CancellationToken ct)
    {
        Requested.Add(externalKey);
        OnGet?.Invoke(externalKey);
        ct.ThrowIfCancellationRequested();
        if (FailingKeys.Contains(externalKey))
        {
            throw new TimeoutException($"Timeout simulado en {externalKey}");
        }

        return Task.FromResult(Snapshots.For(externalKey));
    }
}

internal sealed class FakeRepoStore : IRepoStore
{
    public List<(RepoSnapshot Snapshot, long JobRunId)> Upserts { get; } = [];

    public Task UpsertAsync(RepoSnapshot snapshot, long jobRunId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Upserts.Add((snapshot, jobRunId));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Almacén en memoria con reglas mínimas de TryStartAsync. Las reglas reales (applock, UNIQUE,
/// MaxAttempts) se prueban contra SQL Server en Bots.Infrastructure.Tests.
/// </summary>
internal sealed class FakeJobRunStore : IJobRunStore
{
    private long _nextId = 1;

    public bool LockAvailable { get; set; } = true;

    public Dictionary<(string JobName, DateTimeOffset ScheduledForUtc), Row> Rows { get; } = [];

    public List<(long JobRunId, string? RepoKey, string ErrorType, string Message)> Errors { get; } = [];

    public List<FakeLease> Leases { get; } = [];

    /// <summary>Si se fija, TryStartAsync lanza esta excepción una vez (p. ej. BD caída).</summary>
    public Exception? TryStartExceptionOnce { get; set; }

    public List<TimeSpan> MarkStaleCalls { get; } = [];

    public void Seed(string jobName, DateTimeOffset scheduledForUtc, JobRunStatus status) =>
        Rows[(jobName, scheduledForUtc)] = new Row { Id = _nextId++, Status = status, Attempt = 1 };

    public Row Single() => Rows.Values.Single();

    public Task<IJobRunLease?> TryStartAsync(string jobName, DateTimeOffset scheduledForUtc, CancellationToken ct)
    {
        if (TryStartExceptionOnce is { } exception)
        {
            TryStartExceptionOnce = null;
            throw exception;
        }

        if (!LockAvailable)
        {
            return Task.FromResult<IJobRunLease?>(null);
        }

        if (Rows.TryGetValue((jobName, scheduledForUtc), out var row))
        {
            if (row.Status.CountsAsCompleted())
            {
                return Task.FromResult<IJobRunLease?>(null);
            }

            row.Attempt++;
        }
        else
        {
            row = new Row { Id = _nextId++, Attempt = 1 };
            Rows[(jobName, scheduledForUtc)] = row;
        }

        row.Status = JobRunStatus.Running;
        var lease = new FakeLease(new JobRun(row.Id, jobName, scheduledForUtc, row.Attempt, Snapshots.CapturedAt));
        Leases.Add(lease);
        return Task.FromResult<IJobRunLease?>(lease);
    }

    public Task CompleteAsync(JobRun run, JobRunStatus status, int reposOk, int reposFailed, string? errorSummary, CancellationToken ct)
    {
        var row = Rows[(run.JobName, run.ScheduledForUtc)];
        row.Status = status;
        row.ReposOk = reposOk;
        row.ReposFailed = reposFailed;
        row.ErrorSummary = errorSummary;
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLastCompletedScheduledAsync(string jobName, CancellationToken ct) =>
        Task.FromResult(Rows
            .Where(r => r.Key.JobName == jobName && r.Value.Status.CountsAsCompleted())
            .Select(r => (DateTimeOffset?)r.Key.ScheduledForUtc)
            .Max());

    public Task<int> MarkStaleRunsAbandonedAsync(string jobName, TimeSpan olderThan, CancellationToken ct)
    {
        MarkStaleCalls.Add(olderThan);
        return Task.FromResult(0);
    }

    public Task AddErrorAsync(JobRun run, string? repoKey, string errorType, string message, CancellationToken ct)
    {
        Errors.Add((run.Id, repoKey, errorType, message));
        return Task.CompletedTask;
    }

    internal sealed class Row
    {
        public long Id { get; init; }

        public JobRunStatus Status { get; set; }

        public int Attempt { get; set; }

        public int ReposOk { get; set; }

        public int ReposFailed { get; set; }

        public string? ErrorSummary { get; set; }
    }
}

internal sealed class FakeLease(JobRun run) : IJobRunLease
{
    public JobRun Run { get; } = run;

    public bool Disposed { get; private set; }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Calendario de intervalo fijo alineado a la época Unix (ej. cada 5 minutos).</summary>
internal sealed class FakeSchedule(TimeSpan interval) : ISchedule
{
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc)
    {
        var ticks = (fromUtc.UtcTicks / interval.Ticks + 1) * interval.Ticks;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public IReadOnlyList<DateTimeOffset> GetOccurrences(DateTimeOffset fromUtcExclusive, DateTimeOffset toUtcInclusive)
    {
        var result = new List<DateTimeOffset>();
        for (var next = GetNextOccurrence(fromUtcExclusive); next <= toUtcInclusive; next = GetNextOccurrence(next!.Value))
        {
            result.Add(next!.Value);
        }

        return result;
    }
}

/// <summary>Logger en memoria, seguro entre hilos (el programador escribe desde otra tarea).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get
        {
            lock (_entries)
            {
                return _entries.ToList();
            }
        }
    }

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
