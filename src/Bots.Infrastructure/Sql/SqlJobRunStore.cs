using Bots.Application.Ports;
using Bots.Domain;
using Bots.Infrastructure.Resilience;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Bots.Infrastructure.Sql;

public sealed partial class SqlJobRunStore(
    SqlConnectionFactory connections,
    SqlResilience resilience,
    SqlJobRunStoreOptions options,
    TimeProvider timeProvider,
    ILogger<SqlJobRunStore> logger) : IJobRunStore
{
    private const int MaxErrorTypeLength = 200;
    private const int MaxRepoKeyLength = 256;

    /// <summary>La columna es NVARCHAR(MAX): se acota para que un mensaje enorme no infle la tabla. El detalle completo queda en el log.</summary>
    internal const int MaxErrorMessageLength = 4000;

    private const string SelectRunSql = """
        SELECT id, status, attempt, started_at_utc AS StartedAtUtc, start_token AS StartToken
        FROM bots.job_runs WITH (UPDLOCK, HOLDLOCK)
        WHERE job_name = @JobName AND scheduled_for_utc = @ScheduledForUtc;
        """;

    private const string InsertRunSql = """
        INSERT INTO bots.job_runs (job_name, scheduled_for_utc, attempt, started_at_utc, status, repos_ok, repos_failed, start_token)
        OUTPUT inserted.id
        VALUES (@JobName, @ScheduledForUtc, 1, @NowUtc, N'Running', 0, 0, @StartToken);
        """;

    private const string MarkOrphanAbandonedSql = """
        UPDATE bots.job_runs
        SET status = N'Abandoned', finished_at_utc = @NowUtc,
            error_summary = N'La corrida seguía Running sin lock: el proceso anterior terminó sin cerrarla.'
        WHERE id = @Id;
        """;

    // Antes de reutilizar la fila se deja constancia en job_run_errors de cómo terminó el intento
    // anterior: algunos motivos (cancelación por apagado, huérfana) solo estaban en error_summary.
    // Después se limpian contadores y resumen, que describen el intento en curso.
    private const string ResumeRunSql = """
        INSERT INTO bots.job_run_errors (job_run_id, attempt, repo_key, error_type, message, occurred_at_utc)
        SELECT id, attempt, NULL, N'Attempt' + status,
               CONCAT(N'Intento ', attempt, N' terminó ', status, N': ', COALESCE(error_summary, N'sin resumen.')),
               COALESCE(finished_at_utc, @NowUtc)
        FROM bots.job_runs
        WHERE id = @Id;

        UPDATE bots.job_runs
        SET status = N'Running', attempt = attempt + 1, started_at_utc = @NowUtc, finished_at_utc = NULL,
            repos_ok = 0, repos_failed = 0, error_summary = NULL, start_token = @StartToken
        WHERE id = @Id;
        """;

    // La guarda (id + attempt + Running) impide cerrar una corrida que otra instancia ya retomó.
    // Aceptar también el mismo estado final hace que repetir el cierre (reintento de Polly tras un
    // commit cuya respuesta se perdió) sea seguro.
    private const string CompleteRunSql = """
        UPDATE bots.job_runs
        SET status = @Status, finished_at_utc = @NowUtc, repos_ok = @ReposOk, repos_failed = @ReposFailed,
            error_summary = @ErrorSummary
        WHERE id = @Id AND attempt = @Attempt AND status IN (N'Running', @Status);
        """;

    private const string LastCompletedSql = """
        SELECT MAX(scheduled_for_utc)
        FROM bots.job_runs
        WHERE job_name = @JobName AND status IN (N'Succeeded', N'PartiallyFailed');
        """;

    private const string MarkStaleSql = """
        UPDATE bots.job_runs
        SET status = N'Abandoned', finished_at_utc = @NowUtc,
            error_summary = N'Marcada al arrancar: seguía Running sin ninguna instancia ejecutándola.'
        WHERE job_name = @JobName AND status = N'Running' AND started_at_utc < @ThresholdUtc;
        """;

    // La clave de idempotencia hace que repetir la inserción (reintento de Polly tras un commit cuya
    // respuesta se perdió) no duplique la fila. El índice único la respalda.
    private const string InsertErrorSql = """
        IF NOT EXISTS (SELECT 1 FROM bots.job_run_errors WHERE idempotency_key = @IdempotencyKey)
            INSERT INTO bots.job_run_errors (job_run_id, attempt, repo_key, error_type, message, occurred_at_utc, idempotency_key)
            VALUES (@JobRunId, @Attempt, @RepoKey, @ErrorType, @Message, @NowUtc, @IdempotencyKey);
        """;

    public Task<IJobRunLease?> TryStartAsync(string jobName, DateTimeOffset scheduledForUtc, CancellationToken ct) =>
        TryStartAsync(jobName, scheduledForUtc, Guid.NewGuid(), ct);

    /// <param name="startToken">
    /// Clave de idempotencia de esta llamada: se fija una vez y se repite en cada reintento de Polly, de modo
    /// que un reintento tras perder la respuesta del COMMIT adopta su propia fila en vez de gastar un intento.
    /// </param>
    internal async Task<IJobRunLease?> TryStartAsync(string jobName, DateTimeOffset scheduledForUtc, Guid startToken, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        return await resilience.Pipeline.ExecuteAsync(
            async token => await TryStartOnceAsync(jobName, scheduledForUtc, startToken, token), ct);
    }

    public async Task CompleteAsync(JobRun run, JobRunStatus status, int reposOk, int reposFailed, string? errorSummary, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (status == JobRunStatus.Running)
        {
            throw new ArgumentException("Una corrida no se puede cerrar en estado Running.", nameof(status));
        }

        var parameters = new DynamicParameters(new
        {
            run.Id,
            run.Attempt,
            Status = status.ToString(),
            ReposOk = reposOk,
            ReposFailed = reposFailed,
            ErrorSummary = errorSummary,
        }).AddUtc("NowUtc", timeProvider.GetUtcNow());

        var affected = await resilience.Pipeline.ExecuteAsync(async token =>
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(token);
            return await connection.ExecuteAsync(new CommandDefinition(CompleteRunSql, parameters, cancellationToken: token));
        }, ct);

        if (affected == 0)
        {
            throw new InvalidOperationException(
                $"No se pudo cerrar la corrida {run.Id} (intento {run.Attempt}) como {status}: ya no está Running en ese intento.");
        }
    }

    public async Task<DateTimeOffset?> GetLastCompletedScheduledAsync(string jobName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);

        var last = await resilience.Pipeline.ExecuteAsync(async token =>
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(token);
            return await connection.ExecuteScalarAsync<DateTime?>(
                new CommandDefinition(LastCompletedSql, new { JobName = jobName }, cancellationToken: token));
        }, ct);

        return last is null ? null : SqlParameters.FromUtc(last.Value);
    }

    /// <summary>
    /// Solo marca filas si obtiene el lock del job: si otra instancia lo tiene, su corrida Running es
    /// legítima aunque supere el umbral, y no se toca.
    /// </summary>
    public async Task<int> MarkStaleRunsAbandonedAsync(string jobName, TimeSpan olderThan, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentOutOfRangeException.ThrowIfLessThan(olderThan, TimeSpan.Zero);

        var marked = await resilience.Pipeline.ExecuteAsync(async token =>
        {
            var resource = SqlAppLock.ResourceFor(jobName);
            var lockConnection = connections.CreateDedicated();
            var lockHeld = false;
            try
            {
                await lockConnection.OpenAsync(token);
                lockHeld = await SqlAppLock.TryAcquireAsync(lockConnection, resource, token);
                if (!lockHeld)
                {
                    LogLockHeldNotMarkingStale(logger, jobName);
                    return 0;
                }

                var now = timeProvider.GetUtcNow();
                var parameters = new DynamicParameters(new { JobName = jobName })
                    .AddUtc("NowUtc", now)
                    .AddUtc("ThresholdUtc", now - olderThan);

                await using var connection = connections.Create();
                await connection.OpenAsync(token);
                return await connection.ExecuteAsync(new CommandDefinition(MarkStaleSql, parameters, cancellationToken: token));
            }
            finally
            {
                await SqlJobRunLease.CloseAsync(lockConnection, resource, lockHeld, logger);
            }
        }, ct);

        if (marked > 0)
        {
            LogStaleRunsMarked(logger, jobName, marked, olderThan);
        }

        return marked;
    }

    public Task AddErrorAsync(JobRun run, string? repoKey, string errorType, string message, CancellationToken ct) =>
        AddErrorAsync(run, repoKey, errorType, message, Guid.NewGuid(), ct);

    /// <param name="idempotencyKey">Se fija una vez y se repite en cada reintento de Polly: el error se registra una sola vez.</param>
    internal async Task AddErrorAsync(JobRun run, string? repoKey, string errorType, string message, Guid idempotencyKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorType);
        ArgumentNullException.ThrowIfNull(message);

        var parameters = new DynamicParameters(new
        {
            JobRunId = run.Id,
            run.Attempt,
            RepoKey = Truncate(repoKey, MaxRepoKeyLength),
            ErrorType = Truncate(errorType, MaxErrorTypeLength),
            Message = Truncate(message, MaxErrorMessageLength),
            IdempotencyKey = idempotencyKey,
        }).AddUtc("NowUtc", timeProvider.GetUtcNow());

        await resilience.Pipeline.ExecuteAsync(async token =>
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(token);
            await connection.ExecuteAsync(new CommandDefinition(InsertErrorSql, parameters, cancellationToken: token));
        }, ct);
    }

    private async Task<IJobRunLease?> TryStartOnceAsync(string jobName, DateTimeOffset scheduledForUtc, Guid startToken, CancellationToken ct)
    {
        var resource = SqlAppLock.ResourceFor(jobName);
        var lockConnection = connections.CreateDedicated();
        var lockHeld = false;
        JobRun? run = null;
        try
        {
            await lockConnection.OpenAsync(ct);
            lockHeld = await SqlAppLock.TryAcquireAsync(lockConnection, resource, ct);
            if (!lockHeld)
            {
                LogLockHeldNotStarting(logger, jobName, scheduledForUtc);
                return null;
            }

            run = await OpenOrResumeRunAsync(jobName, scheduledForUtc, startToken, ct);
            return run is null ? null : new SqlJobRunLease(run, lockConnection, resource, logger);
        }
        finally
        {
            // Si no se entrega el lease (lock ocupado, nada que hacer o excepción), se suelta el lock aquí.
            if (run is null)
            {
                await SqlJobRunLease.CloseAsync(lockConnection, resource, lockHeld, logger);
            }
        }
    }

    /// <summary>Reglas de TryStartAsync, en una transacción y con el lock ya tomado.</summary>
    private async Task<JobRun?> OpenOrResumeRunAsync(string jobName, DateTimeOffset scheduledForUtc, Guid startToken, CancellationToken ct)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        var now = timeProvider.GetUtcNow();
        var key = new DynamicParameters(new { JobName = jobName }).AddUtc("ScheduledForUtc", scheduledForUtc);
        var row = await connection.QuerySingleOrDefaultAsync<JobRunRow>(
            new CommandDefinition(SelectRunSql, key, transaction, cancellationToken: ct));

        // 2. No existe: primer intento.
        if (row is null)
        {
            var insert = new DynamicParameters(new { JobName = jobName, StartToken = startToken })
                .AddUtc("ScheduledForUtc", scheduledForUtc)
                .AddUtc("NowUtc", now);
            var id = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(InsertRunSql, insert, transaction, cancellationToken: ct));
            await transaction.CommitAsync(ct);
            return new JobRun(id, jobName, scheduledForUtc, 1, now);
        }

        var status = Enum.Parse<JobRunStatus>(row.Status);

        // Running con la clave de esta llamada: un intento anterior de Polly ya la abrió (o retomó) y se
        // perdió la respuesta del COMMIT. Se continúa con ella; no es huérfana y no se gasta otro intento.
        if (status == JobRunStatus.Running && row.StartToken == startToken)
        {
            await transaction.CommitAsync(ct);
            LogAdoptedOwnRun(logger, jobName, row.Id, row.Attempt, scheduledForUtc);
            return new JobRun(row.Id, jobName, scheduledForUtc, row.Attempt, SqlParameters.FromUtc(row.StartedAtUtc));
        }

        // 5. Ya completada.
        if (status.CountsAsCompleted())
        {
            await transaction.CommitAsync(ct);
            LogAlreadyCompleted(logger, jobName, scheduledForUtc, status);
            return null;
        }

        var byId = new DynamicParameters(new { row.Id, StartToken = startToken }).AddUtc("NowUtc", now);

        // 4. Running con el lock libre: el dueño anterior murió.
        if (status == JobRunStatus.Running)
        {
            await connection.ExecuteAsync(new CommandDefinition(MarkOrphanAbandonedSql, byId, transaction, cancellationToken: ct));
            LogOrphanMarkedAbandoned(logger, jobName, row.Id, row.Attempt, scheduledForUtc);
        }

        // 3. Failed / Abandoned: se retoma si quedan intentos.
        if (row.Attempt >= options.MaxAttempts)
        {
            await transaction.CommitAsync(ct);
            LogAttemptsExhausted(logger, jobName, scheduledForUtc, row.Id, options.MaxAttempts);
            return null;
        }

        await connection.ExecuteAsync(new CommandDefinition(ResumeRunSql, byId, transaction, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        LogResumed(logger, jobName, scheduledForUtc, row.Id, row.Attempt + 1);
        return new JobRun(row.Id, jobName, scheduledForUtc, row.Attempt + 1, now);
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is null || value.Length <= maxLength ? value : value[..maxLength];

    private sealed record JobRunRow(long Id, string Status, int Attempt, DateTime StartedAtUtc, Guid? StartToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "{JobName}: otra instancia tiene el lock; no se marcan corridas Running como Abandoned.")]
    private static partial void LogLockHeldNotMarkingStale(ILogger logger, string jobName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{JobName}: {Count} corridas Running huérfanas (iniciadas hace más de {OlderThan}) marcadas como Abandoned.")]
    private static partial void LogStaleRunsMarked(ILogger logger, string jobName, int count, TimeSpan olderThan);

    [LoggerMessage(Level = LogLevel.Information, Message = "{JobName}: otra instancia tiene el lock del job; no se inicia {ScheduledForUtc:o}.")]
    private static partial void LogLockHeldNotStarting(ILogger logger, string jobName, DateTimeOffset scheduledForUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "{JobName}: la corrida {JobRunId} (intento {Attempt}) de {ScheduledForUtc:o} ya la había abierto esta llamada antes de un reintento; se continúa con ella.")]
    private static partial void LogAdoptedOwnRun(ILogger logger, string jobName, long jobRunId, int attempt, DateTimeOffset scheduledForUtc);

    [LoggerMessage(Level = LogLevel.Information, Message = "{JobName}: la ocurrencia {ScheduledForUtc:o} ya está {Status}; no se repite.")]
    private static partial void LogAlreadyCompleted(ILogger logger, string jobName, DateTimeOffset scheduledForUtc, JobRunStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{JobName}: la corrida {JobRunId} (intento {Attempt}) de {ScheduledForUtc:o} seguía Running sin lock; se marca Abandoned.")]
    private static partial void LogOrphanMarkedAbandoned(ILogger logger, string jobName, long jobRunId, int attempt, DateTimeOffset scheduledForUtc);

    [LoggerMessage(Level = LogLevel.Error, Message = "{JobName}: la ocurrencia {ScheduledForUtc:o} (corrida {JobRunId}) agotó sus {MaxAttempts} intentos; no se retoma. Ver runbook: intentos agotados.")]
    private static partial void LogAttemptsExhausted(ILogger logger, string jobName, DateTimeOffset scheduledForUtc, long jobRunId, int maxAttempts);

    [LoggerMessage(Level = LogLevel.Information, Message = "{JobName}: se retoma la ocurrencia {ScheduledForUtc:o} (corrida {JobRunId}) con el intento {Attempt}.")]
    private static partial void LogResumed(ILogger logger, string jobName, DateTimeOffset scheduledForUtc, long jobRunId, int attempt);
}
