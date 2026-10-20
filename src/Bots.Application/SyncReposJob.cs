using Bots.Application.Ports;
using Bots.Domain;
using Microsoft.Extensions.Logging;

namespace Bots.Application;

/// <summary>
/// Sincroniza todos los repos de la fuente hacia el almacén para una ocurrencia programada.
/// Un fallo en un repo no aborta los demás; solo un fallo fatal (listado, BD) aborta la corrida.
/// </summary>
public sealed class SyncReposJob(
    IRepoSource source,
    IRepoStore store,
    IJobRunStore jobRuns,
    SyncReposOptions options,
    ILogger<SyncReposJob> logger)
{
    internal const int MaxSummaryLength = 2000;
    private const int MaxReposInSummary = 10;

    /// <summary>
    /// Ejecuta la ocurrencia. Devuelve <c>null</c> si no se inició (lock tomado, ya completada o
    /// intentos agotados). Si <paramref name="ct"/> se cancela, deja la corrida en <c>Abandoned</c>
    /// (se retomará) y relanza <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<SyncReposResult?> RunAsync(DateTimeOffset scheduledForUtc, CancellationToken ct)
    {
        await using var lease = await jobRuns.TryStartAsync(options.JobName, scheduledForUtc, ct);
        if (lease is null)
        {
            logger.LogInformation(
                "No se inicia {JobName} para {ScheduledForUtc:o}: otra instancia tiene el lock, la ocurrencia ya está completada o se agotaron los intentos.",
                options.JobName, scheduledForUtc);
            return null;
        }

        var run = lease.Run;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["JobName"] = run.JobName,
            ["JobRunId"] = run.Id,
            ["Attempt"] = run.Attempt,
            ["ScheduledForUtc"] = run.ScheduledForUtc,
        });

        return await ExecuteAsync(run, ct);
    }

    private async Task<SyncReposResult> ExecuteAsync(JobRun run, CancellationToken ct)
    {
        var ok = 0;
        var failedRepos = new List<string>();

        try
        {
            var keys = (await source.ListRepoKeysAsync(ct))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            logger.LogInformation("Corrida iniciada (intento {Attempt}): {RepoCount} repos a procesar.", run.Attempt, keys.Count);

            if (keys.Count == 0)
            {
                return await CompleteEmptyAsync(run, ct);
            }

            foreach (var key in keys)
            {
                ct.ThrowIfCancellationRequested();

                if (await TryProcessRepoAsync(run, key, ct) is { } failure)
                {
                    failedRepos.Add(failure);
                }
                else
                {
                    ok++;
                }
            }

            var status = failedRepos.Count == 0 ? JobRunStatus.Succeeded
                : ok == 0 ? JobRunStatus.Failed
                : JobRunStatus.PartiallyFailed;
            var summary = status == JobRunStatus.Succeeded ? null : BuildSummary(keys.Count, failedRepos);

            await jobRuns.CompleteAsync(run, status, ok, failedRepos.Count, summary, ct);
            LogOutcome(run, status, ok, failedRepos.Count, summary);
            return new SyncReposResult(run.Id, run.Attempt, status, ok, failedRepos.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Corrida cancelada por apagado tras {ReposOk} repos OK y {ReposFailed} fallidos. Queda Abandoned; la ocurrencia se retomará con un nuevo intento.",
                ok, failedRepos.Count);
            await TryCompleteAsync(run, JobRunStatus.Abandoned, ok, failedRepos.Count, "Cancelada por apagado ordenado antes de terminar.");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fallo fatal en la corrida: {ErrorType}. Se marca Failed; la ocurrencia se reintentará.", ex.GetType().Name);
            await TryAddErrorAsync(run, ex);
            await TryCompleteAsync(run, JobRunStatus.Failed, ok, failedRepos.Count, Truncate($"Fallo fatal: {ex.GetType().Name}: {ex.Message}"));
            return new SyncReposResult(run.Id, run.Attempt, JobRunStatus.Failed, ok, failedRepos.Count);
        }
    }

    /// <summary>Devuelve <c>null</c> si el repo se sincronizó, o una descripción corta del fallo.</summary>
    private async Task<string?> TryProcessRepoAsync(JobRun run, string key, CancellationToken ct)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["RepoKey"] = key });
        try
        {
            var snapshot = await source.GetRepoAsync(key, ct);
            if (!string.Equals(snapshot.ExternalKey, key, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"La fuente devolvió el repo '{snapshot.ExternalKey}' al pedir '{key}'.");
            }

            await store.UpsertAsync(snapshot, run.Id, ct);
            logger.LogDebug("Repo sincronizado: {BranchCount} ramas, {PullRequestCount} PRs.", snapshot.BranchCount, snapshot.PullRequestCount);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fallo al procesar el repo {RepoKey} ({ErrorType}); se continúa con el resto.", key, ex.GetType().Name);
            // Si registrar el error falla (p. ej. BD caída) la excepción sube y la corrida es fatal.
            await jobRuns.AddErrorAsync(run, key, ex.GetType().Name, ex.Message, ct);
            return $"{key} ({ex.GetType().Name})";
        }
    }

    private async Task<SyncReposResult> CompleteEmptyAsync(JobRun run, CancellationToken ct)
    {
        if (options.AllowEmptySource)
        {
            logger.LogWarning("La fuente devolvió 0 repos y AllowEmptySource=true: la corrida se da por Succeeded.");
            await jobRuns.CompleteAsync(run, JobRunStatus.Succeeded, 0, 0, null, ct);
            return new SyncReposResult(run.Id, run.Attempt, JobRunStatus.Succeeded, 0, 0);
        }

        const string summary = "La fuente devolvió 0 repos. Posible fallo del scraping (login, selectores) o usuario sin repos. Ver runbook: 'Failed por 0 repos'.";
        logger.LogError("Corrida Failed: {ErrorSummary}", summary);
        await jobRuns.AddErrorAsync(run, null, "EmptySource", summary, ct);
        await jobRuns.CompleteAsync(run, JobRunStatus.Failed, 0, 0, summary, ct);
        return new SyncReposResult(run.Id, run.Attempt, JobRunStatus.Failed, 0, 0);
    }

    private void LogOutcome(JobRun run, JobRunStatus status, int ok, int failed, string? summary)
    {
        switch (status)
        {
            case JobRunStatus.Succeeded:
                logger.LogInformation("Corrida Succeeded: {ReposOk} repos sincronizados.", ok);
                break;
            case JobRunStatus.PartiallyFailed:
                logger.LogWarning(
                    "Corrida PartiallyFailed: {ReposOk} OK, {ReposFailed} fallidos. Detalle en bots.job_run_errors (job_run_id={JobRunId}, attempt={Attempt}). {ErrorSummary}",
                    ok, failed, run.Id, run.Attempt, summary);
                break;
            default:
                logger.LogError(
                    "Corrida {Status}: {ReposOk} OK, {ReposFailed} fallidos. Detalle en bots.job_run_errors (job_run_id={JobRunId}, attempt={Attempt}). {ErrorSummary}",
                    status, ok, failed, run.Id, run.Attempt, summary);
                break;
        }
    }

    private async Task TryAddErrorAsync(JobRun run, Exception ex)
    {
        try
        {
            await jobRuns.AddErrorAsync(run, null, ex.GetType().Name, ex.Message, CancellationToken.None);
        }
        catch (Exception logEx)
        {
            logger.LogError(logEx, "No se pudo registrar el error fatal en bots.job_run_errors.");
        }
    }

    /// <summary>Cierre de mejor esfuerzo: si falla, la fila queda Running y se marcará Abandoned al retomarse.</summary>
    private async Task TryCompleteAsync(JobRun run, JobRunStatus status, int ok, int failed, string summary)
    {
        try
        {
            await jobRuns.CompleteAsync(run, status, ok, failed, summary, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "No se pudo registrar el cierre {Status} de la corrida; queda Running y se marcará Abandoned al retomarse.", status);
        }
    }

    private static string BuildSummary(int total, List<string> failedRepos)
    {
        var listed = string.Join(", ", failedRepos.Take(MaxReposInSummary));
        var more = failedRepos.Count > MaxReposInSummary ? $" y {failedRepos.Count - MaxReposInSummary} más" : string.Empty;
        return Truncate($"{failedRepos.Count} de {total} repos fallaron: {listed}{more}.");
    }

    private static string Truncate(string value) =>
        value.Length <= MaxSummaryLength ? value : value[..(MaxSummaryLength - 1)] + "…";
}
