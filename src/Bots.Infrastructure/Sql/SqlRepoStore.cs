using System.Data;
using System.Text.Json;
using Bots.Application.Ports;
using Bots.Domain;
using Bots.Infrastructure.Resilience;
using Dapper;

namespace Bots.Infrastructure.Sql;

public sealed class SqlRepoStore(SqlConnectionFactory connections, SqlResilience resilience, TimeProvider timeProvider) : IRepoStore
{
    // HOLDLOCK evita la condición de carrera del MERGE (dos upserts del mismo repo a la vez).
    private const string MergeRepoSql = """
        MERGE bots.repos WITH (HOLDLOCK) AS t
        USING (SELECT @ExternalKey AS external_key) AS s
            ON t.external_key = s.external_key
        WHEN MATCHED THEN
            UPDATE SET title = @Title,
                       description = @Description,
                       has_readme = @HasReadme,
                       pr_count = @PullRequestCount,
                       captured_at_utc = @CapturedAtUtc,
                       last_job_run_id = @JobRunId,
                       updated_at_utc = @NowUtc
        WHEN NOT MATCHED THEN
            INSERT (external_key, title, description, has_readme, pr_count, captured_at_utc, last_job_run_id, created_at_utc, updated_at_utc)
            VALUES (@ExternalKey, @Title, @Description, @HasReadme, @PullRequestCount, @CapturedAtUtc, @JobRunId, @NowUtc, @NowUtc)
        OUTPUT inserted.id;
        """;

    // Las ramas llegan como JSON (OPENJSON) para no necesitar un tipo tabla en la BD. El CTE limita
    // el MERGE a las ramas de este repo, de modo que "NOT MATCHED BY SOURCE" solo borra las suyas.
    // branch_name usa collation binaria (git distingue mayúsculas): se fuerza también en el JOIN.
    private const string MergeBranchesSql = """
        WITH target AS
        (
            SELECT repo_id, branch_name, commit_count, captured_at_utc
            FROM bots.repo_branches
            WHERE repo_id = @RepoId
        )
        MERGE target WITH (HOLDLOCK) AS t
        USING
        (
            SELECT name, commit_count
            FROM OPENJSON(@BranchesJson) WITH (name NVARCHAR(255) '$.name', commit_count INT '$.commitCount')
        ) AS s
            ON t.branch_name = s.name COLLATE Latin1_General_100_BIN2
        WHEN MATCHED THEN
            UPDATE SET commit_count = s.commit_count, captured_at_utc = @CapturedAtUtc
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (repo_id, branch_name, commit_count, captured_at_utc)
            VALUES (@RepoId, s.name, s.commit_count, @CapturedAtUtc)
        WHEN NOT MATCHED BY SOURCE THEN
            DELETE;
        """;

    public async Task UpsertAsync(RepoSnapshot snapshot, long jobRunId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var branchesJson = JsonSerializer.Serialize(snapshot.Branches.Select(b => new { name = b.Name, commitCount = b.CommitCount }));

        await resilience.Pipeline.ExecuteAsync(async token =>
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(token);

            var repoParameters = new DynamicParameters(new
            {
                snapshot.ExternalKey,
                snapshot.Title,
                snapshot.Description,
                snapshot.HasReadme,
                snapshot.PullRequestCount,
                JobRunId = jobRunId,
            })
                .AddUtc("CapturedAtUtc", snapshot.CapturedAtUtc)
                .AddUtc("NowUtc", timeProvider.GetUtcNow());

            var repoId = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(MergeRepoSql, repoParameters, transaction, cancellationToken: token));

            var branchParameters = new DynamicParameters(new { RepoId = repoId })
                .AddUtc("CapturedAtUtc", snapshot.CapturedAtUtc);
            branchParameters.Add("BranchesJson", branchesJson, DbType.String, size: -1);

            await connection.ExecuteAsync(
                new CommandDefinition(MergeBranchesSql, branchParameters, transaction, cancellationToken: token));

            await transaction.CommitAsync(token);
        }, ct);
    }
}
