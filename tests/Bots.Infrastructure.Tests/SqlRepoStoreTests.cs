using Bots.Domain;

namespace Bots.Infrastructure.Tests;

/// <summary>Cada test usa su propia external_key, así que pueden compartir la BD sin interferir.</summary>
public sealed class SqlRepoStoreTests(SqlServerFixture db)
{
    private static readonly DateTimeOffset Captured = new(2026, 10, 20, 13, 0, 0, TimeSpan.Zero);

    private readonly string _key = $"ana/repo-{Guid.NewGuid():N}";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private RepoSnapshot Snapshot(string? description = "desc", int prs = 2, params BranchSnapshot[] branches) =>
        new(_key, _key.Split('/')[1], description, hasReadme: true, prs, branches, Captured);

    [Fact]
    public async Task Upsert_twice_leaves_a_single_row_with_the_same_branches()
    {
        var store = db.RepoStore();
        var snapshot = Snapshot(branches: [new("main", 5), new("dev", 7)]);

        await store.UpsertAsync(snapshot, jobRunId: 1, Ct);
        await store.UpsertAsync(snapshot, jobRunId: 1, Ct);

        Assert.Single(await RepoRowsAsync());
        Assert.Equal([("dev", 7), ("main", 5)], await BranchesAsync());
    }

    [Fact]
    public async Task Upsert_updates_fields_and_last_job_run()
    {
        var store = db.RepoStore();
        await store.UpsertAsync(Snapshot("vieja", 1, new BranchSnapshot("main", 1)), jobRunId: 10, Ct);

        await store.UpsertAsync(Snapshot(null, 4, new BranchSnapshot("main", 9)), jobRunId: 11, Ct);

        var row = Assert.Single(await RepoRowsAsync());
        Assert.Null(row.Description);
        Assert.Equal(4, row.PrCount);
        Assert.Equal(11, row.LastJobRunId);
        Assert.Equal([("main", 9)], await BranchesAsync());
    }

    [Fact]
    public async Task Branch_deleted_in_the_source_disappears()
    {
        var store = db.RepoStore();
        await store.UpsertAsync(Snapshot(branches: [new("main", 5), new("feature/x", 2)]), jobRunId: 1, Ct);

        await store.UpsertAsync(Snapshot(branches: [new("main", 6)]), jobRunId: 2, Ct);

        Assert.Equal([("main", 6)], await BranchesAsync());
    }

    [Fact]
    public async Task Empty_repo_without_branches_is_stored_and_clears_old_branches()
    {
        var store = db.RepoStore();
        await store.UpsertAsync(Snapshot(prs: 0), jobRunId: 1, Ct);

        Assert.Single(await RepoRowsAsync());
        Assert.Empty(await BranchesAsync());

        await store.UpsertAsync(Snapshot(branches: [new("main", 1)]), jobRunId: 2, Ct);
        await store.UpsertAsync(Snapshot(prs: 0), jobRunId: 3, Ct);

        Assert.Empty(await BranchesAsync());
    }

    [Fact]
    public async Task Branches_differing_only_in_case_are_distinct()
    {
        var store = db.RepoStore();

        await store.UpsertAsync(Snapshot(branches: [new("Main", 1), new("main", 2)]), jobRunId: 1, Ct);
        await store.UpsertAsync(Snapshot(branches: [new("main", 3)]), jobRunId: 2, Ct);

        Assert.Equal([("main", 3)], await BranchesAsync());
    }

    private Task<IReadOnlyList<RepoRow>> RepoRowsAsync() =>
        db.QueryAsync<RepoRow>(
            "SELECT description, pr_count AS PrCount, last_job_run_id AS LastJobRunId FROM bots.repos WHERE external_key = @Key",
            new { Key = _key });

    private async Task<IReadOnlyList<(string Name, int Commits)>> BranchesAsync() =>
        await db.QueryAsync<(string, int)>(
            """
            SELECT b.branch_name, b.commit_count
            FROM bots.repo_branches b JOIN bots.repos r ON r.id = b.repo_id
            WHERE r.external_key = @Key
            ORDER BY b.branch_name
            """,
            new { Key = _key });

    private sealed record RepoRow(string? Description, int PrCount, long? LastJobRunId);
}
