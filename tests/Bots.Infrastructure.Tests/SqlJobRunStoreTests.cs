using Bots.Domain;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Time.Testing;

namespace Bots.Infrastructure.Tests;

/// <summary>Cada test usa su propio job_name, así que pueden compartir la BD sin interferir.</summary>
public sealed class SqlJobRunStoreTests(SqlServerFixture db)
{
    private static readonly DateTimeOffset T1 = new(2026, 10, 20, 13, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = T1.AddMinutes(5);

    private readonly string _job = $"test-{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TryStart_inserts_Running_with_attempt_1()
    {
        var store = db.JobRunStore();

        await using var lease = await store.TryStartAsync(_job, T1, Ct);

        Assert.NotNull(lease);
        Assert.Equal(1, lease.Run.Attempt);
        Assert.Equal(T1, lease.Run.ScheduledForUtc);
        var row = await SingleRowAsync();
        Assert.Equal("Running", row.Status);
        Assert.Equal(1, row.Attempt);
        Assert.Null(row.FinishedAtUtc);
    }

    [Theory]
    [InlineData(JobRunStatus.Failed)]
    [InlineData(JobRunStatus.Abandoned)]
    public async Task Failed_or_Abandoned_is_resumed_incrementing_attempt(JobRunStatus previous)
    {
        var store = db.JobRunStore();
        await using (var first = await store.TryStartAsync(_job, T1, Ct))
        {
            await store.CompleteAsync(first!.Run, previous, 1, 2, "fallo anterior", Ct);
        }

        await using var second = await store.TryStartAsync(_job, T1, Ct);

        Assert.NotNull(second);
        Assert.Equal(2, second.Run.Attempt);
        var row = await SingleRowAsync();
        Assert.Equal("Running", row.Status);
        Assert.Equal(2, row.Attempt);
        Assert.Null(row.FinishedAtUtc);
        Assert.Equal(0, row.ReposOk);
        Assert.Null(row.ErrorSummary);

        // El resumen limpiado no se pierde: queda registrado con el intento al que pertenece.
        var audit = Assert.Single(await ErrorsAsync());
        Assert.Equal(1, audit.Attempt);
        Assert.Equal($"Attempt{previous}", audit.ErrorType);
        Assert.Equal($"Intento 1 terminó {previous}: fallo anterior", audit.Message);
    }

    [Fact]
    public async Task Orphan_Running_is_marked_Abandoned_and_resumed()
    {
        var store = db.JobRunStore();
        // El dueño "muere": suelta el lock sin cerrar la corrida.
        await (await store.TryStartAsync(_job, T1, Ct))!.DisposeAsync();

        await using var lease = await store.TryStartAsync(_job, T1, Ct);

        Assert.NotNull(lease);
        Assert.Equal(2, lease.Run.Attempt);
        Assert.Equal("Running", (await SingleRowAsync()).Status);

        var audit = Assert.Single(await ErrorsAsync());
        Assert.Equal(1, audit.Attempt);
        Assert.Equal("AttemptAbandoned", audit.ErrorType);
        Assert.Contains("sin lock", audit.Message);
    }

    [Fact]
    public async Task Retry_with_the_same_start_token_adopts_its_own_run()
    {
        var store = db.JobRunStore();
        var token = Guid.NewGuid();
        // Simula un reintento de Polly tras perder la respuesta del COMMIT: la fila quedó escrita y el
        // lock se soltó al fallar, pero la llamada lógica es la misma (misma clave).
        await (await store.TryStartAsync(_job, T1, token, Ct))!.DisposeAsync();

        await using var lease = await store.TryStartAsync(_job, T1, token, Ct);

        Assert.NotNull(lease);
        Assert.Equal(1, lease.Run.Attempt);
        var row = await SingleRowAsync();
        Assert.Equal("Running", row.Status);
        Assert.Equal(1, row.Attempt);
        Assert.Empty(await ErrorsAsync());
    }

    [Fact]
    public async Task Retry_of_a_resume_with_the_same_start_token_does_not_spend_another_attempt()
    {
        var store = db.JobRunStore();
        await using (var first = await store.TryStartAsync(_job, T1, Ct))
        {
            await store.CompleteAsync(first!.Run, JobRunStatus.Failed, 0, 1, "falla", Ct);
        }

        var token = Guid.NewGuid();
        await (await store.TryStartAsync(_job, T1, token, Ct))!.DisposeAsync();
        await using var retried = await store.TryStartAsync(_job, T1, token, Ct);

        Assert.Equal(2, retried!.Run.Attempt);
        Assert.Equal(2, (await SingleRowAsync()).Attempt);
        Assert.Single(await ErrorsAsync()); // solo la auditoría del intento 1
    }

    [Fact]
    public async Task MaxAttempts_is_respected()
    {
        var store = db.JobRunStore(maxAttempts: 2);
        for (var i = 0; i < 2; i++)
        {
            await using var lease = await store.TryStartAsync(_job, T1, Ct);
            await store.CompleteAsync(lease!.Run, JobRunStatus.Failed, 0, 1, "falla", Ct);
        }

        var third = await store.TryStartAsync(_job, T1, Ct);

        Assert.Null(third);
        var row = await SingleRowAsync();
        Assert.Equal("Failed", row.Status);
        Assert.Equal(2, row.Attempt);
    }

    [Theory]
    [InlineData(JobRunStatus.Succeeded)]
    [InlineData(JobRunStatus.PartiallyFailed)]
    public async Task Completed_occurrence_is_not_started_again(JobRunStatus completed)
    {
        var store = db.JobRunStore();
        await using (var lease = await store.TryStartAsync(_job, T1, Ct))
        {
            await store.CompleteAsync(lease!.Run, completed, 3, 0, null, Ct);
        }

        Assert.Null(await store.TryStartAsync(_job, T1, Ct));
        Assert.Equal(1, (await SingleRowAsync()).Attempt);
    }

    [Fact]
    public async Task Two_concurrent_starts_only_one_gets_the_lock()
    {
        var storeA = db.JobRunStore();
        var storeB = db.JobRunStore();

        var leases = await Task.WhenAll(storeA.TryStartAsync(_job, T1, Ct), storeB.TryStartAsync(_job, T2, Ct));

        try
        {
            Assert.Single(leases, l => l is not null);
        }
        finally
        {
            foreach (var lease in leases.OfType<Bots.Application.Ports.IJobRunLease>())
            {
                await lease.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Lease_holds_the_lock_in_SQL_Server_until_disposed()
    {
        var store = db.JobRunStore();
        var lease = await store.TryStartAsync(_job, T1, Ct);
        Assert.NotNull(lease);

        Assert.Equal(-1, await TryGetAppLockFromAnotherSessionAsync());
        Assert.Null(await db.JobRunStore().TryStartAsync(_job, T2, Ct));

        await lease.DisposeAsync();

        await using var next = await db.JobRunStore().TryStartAsync(_job, T2, Ct);
        Assert.NotNull(next);
    }

    [Fact]
    public async Task AddError_records_the_current_attempt()
    {
        var store = db.JobRunStore();
        await using (var first = await store.TryStartAsync(_job, T1, Ct))
        {
            await store.AddErrorAsync(first!.Run, "ana/a", "TimeoutException", "intento 1", Ct);
            await store.CompleteAsync(first.Run, JobRunStatus.Failed, 0, 1, "falla", Ct);
        }

        await using var second = await store.TryStartAsync(_job, T1, Ct);
        await store.AddErrorAsync(second!.Run, null, "EmptySource", "intento 2", Ct);

        Assert.Equal(
            [
                new ErrorRow(1, "ana/a", "TimeoutException", "intento 1"),
                new ErrorRow(1, null, "AttemptFailed", "Intento 1 terminó Failed: falla"),
                new ErrorRow(2, null, "EmptySource", "intento 2"),
            ],
            await ErrorsAsync());
    }

    [Fact]
    public async Task AddError_with_the_same_idempotency_key_is_recorded_once()
    {
        var store = db.JobRunStore();
        await using var lease = await store.TryStartAsync(_job, T1, Ct);
        var key = Guid.NewGuid();

        // Reintento de Polly tras perder la respuesta del COMMIT: misma clave.
        await store.AddErrorAsync(lease!.Run, "ana/a", "TimeoutException", "falla", key, Ct);
        await store.AddErrorAsync(lease.Run, "ana/a", "TimeoutException", "falla", key, Ct);
        // Otro error lógico (otra clave) sí se registra aunque el texto coincida.
        await store.AddErrorAsync(lease.Run, "ana/a", "TimeoutException", "falla", Ct);

        Assert.Equal(2, (await ErrorsAsync()).Count);
    }

    [Fact]
    public async Task AddError_truncates_long_messages()
    {
        var store = db.JobRunStore();
        await using var lease = await store.TryStartAsync(_job, T1, Ct);

        await store.AddErrorAsync(lease!.Run, null, "Boom", new string('x', 10_000), Ct);

        var error = Assert.Single(await ErrorsAsync());
        Assert.Equal(Bots.Infrastructure.Sql.SqlJobRunStore.MaxErrorMessageLength, error.Message.Length);
    }

    [Fact]
    public async Task Complete_is_idempotent_but_rejects_a_stale_attempt()
    {
        var store = db.JobRunStore();
        var first = await store.TryStartAsync(_job, T1, Ct);
        var staleRun = first!.Run;
        await store.CompleteAsync(staleRun, JobRunStatus.Failed, 0, 1, "falla", Ct);
        // Repetir el mismo cierre (p. ej. reintento de Polly) no falla.
        await store.CompleteAsync(staleRun, JobRunStatus.Failed, 0, 1, "falla", Ct);
        await first.DisposeAsync();

        await using var second = await store.TryStartAsync(_job, T1, Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.CompleteAsync(staleRun, JobRunStatus.Succeeded, 1, 0, null, Ct));
        Assert.Equal("Running", (await SingleRowAsync()).Status);
    }

    [Fact]
    public async Task GetLastCompleted_returns_latest_Succeeded_or_PartiallyFailed()
    {
        var store = db.JobRunStore();
        Assert.Null(await store.GetLastCompletedScheduledAsync(_job, Ct));

        await CompleteOccurrenceAsync(store, T1, JobRunStatus.PartiallyFailed);
        await CompleteOccurrenceAsync(store, T2, JobRunStatus.Failed);

        var last = await store.GetLastCompletedScheduledAsync(_job, Ct);

        Assert.Equal(T1, last);
        Assert.Equal(TimeSpan.Zero, last!.Value.Offset);
    }

    [Fact]
    public async Task MarkStale_abandons_old_orphan_Running_rows()
    {
        var time = new FakeTimeProvider(T1);
        var store = db.JobRunStore(time);
        await (await store.TryStartAsync(_job, T1, Ct))!.DisposeAsync();

        time.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, await store.MarkStaleRunsAbandonedAsync(_job, TimeSpan.FromHours(1), Ct));

        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await store.MarkStaleRunsAbandonedAsync(_job, TimeSpan.FromHours(1), Ct));
        Assert.Equal("Abandoned", (await SingleRowAsync()).Status);
    }

    [Fact]
    public async Task MarkStale_does_not_touch_a_run_whose_lock_is_held()
    {
        var time = new FakeTimeProvider(T1);
        var store = db.JobRunStore(time);
        await using var running = await store.TryStartAsync(_job, T1, Ct);
        time.Advance(TimeSpan.FromHours(5));

        Assert.Equal(0, await store.MarkStaleRunsAbandonedAsync(_job, TimeSpan.FromHours(1), Ct));
        Assert.Equal("Running", (await SingleRowAsync()).Status);
    }

    private async Task CompleteOccurrenceAsync(Bots.Infrastructure.Sql.SqlJobRunStore store, DateTimeOffset at, JobRunStatus status)
    {
        await using var lease = await store.TryStartAsync(_job, at, Ct);
        await store.CompleteAsync(lease!.Run, status, 1, 0, null, Ct);
    }

    private async Task<JobRunRow> SingleRowAsync() =>
        Assert.Single(await db.QueryAsync<JobRunRow>(
            "SELECT status, attempt, finished_at_utc AS FinishedAtUtc, repos_ok AS ReposOk, error_summary AS ErrorSummary FROM bots.job_runs WHERE job_name = @Job",
            new { Job = _job }));

    private Task<IReadOnlyList<ErrorRow>> ErrorsAsync() =>
        db.QueryAsync<ErrorRow>(
            "SELECT e.attempt, e.repo_key AS RepoKey, e.error_type AS ErrorType, e.message FROM bots.job_run_errors e JOIN bots.job_runs r ON r.id = e.job_run_id WHERE r.job_name = @Job ORDER BY e.id",
            new { Job = _job });

    private async Task<int> TryGetAppLockFromAnotherSessionAsync()
    {
        await using var connection = new SqlConnection(db.AdminConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            "DECLARE @r INT; EXEC @r = sp_getapplock @Resource = @Resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @r;",
            new { Resource = $"bots:job:{_job}" });
    }

    private sealed record ErrorRow(int Attempt, string? RepoKey, string ErrorType, string Message);

    private sealed record JobRunRow(string Status, int Attempt, DateTime? FinishedAtUtc, int ReposOk, string? ErrorSummary);
}
