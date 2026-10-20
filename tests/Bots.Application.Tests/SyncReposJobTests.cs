using Bots.Domain;
using Microsoft.Extensions.Logging;

namespace Bots.Application.Tests;

public sealed class SyncReposJobTests
{
    private static readonly DateTimeOffset Scheduled = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeRepoSource _source = new();
    private readonly FakeRepoStore _store = new();
    private readonly FakeJobRunStore _jobRuns = new();
    private readonly ListLogger<SyncReposJob> _logger = new();
    private readonly SyncReposOptions _options = new();

    private SyncReposJob CreateJob() => new(_source, _store, _jobRuns, _options, _logger);

    [Fact]
    public async Task All_repos_ok_ends_Succeeded()
    {
        _source.Keys.AddRange(["ana/a", "ana/b", "ana/c"]);

        var result = await CreateJob().RunAsync(Scheduled, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(JobRunStatus.Succeeded, result.Status);
        Assert.Equal(3, result.ReposOk);
        Assert.Equal(0, result.ReposFailed);
        Assert.Equal(["ana/a", "ana/b", "ana/c"], _store.Upserts.Select(u => u.Snapshot.ExternalKey));
        Assert.All(_store.Upserts, u => Assert.Equal(result.JobRunId, u.JobRunId));
        Assert.Equal(JobRunStatus.Succeeded, _jobRuns.Single().Status);
        Assert.Null(_jobRuns.Single().ErrorSummary);
        Assert.Empty(_jobRuns.Errors);
        Assert.True(_jobRuns.Leases.Single().Disposed);
    }

    [Fact]
    public async Task One_repo_fails_others_continue_and_ends_PartiallyFailed()
    {
        _source.Keys.AddRange(["ana/a", "ana/b", "ana/c"]);
        _source.FailingKeys.Add("ana/b");

        var result = await CreateJob().RunAsync(Scheduled, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(JobRunStatus.PartiallyFailed, result.Status);
        Assert.Equal(2, result.ReposOk);
        Assert.Equal(1, result.ReposFailed);
        Assert.Equal(["ana/a", "ana/c"], _store.Upserts.Select(u => u.Snapshot.ExternalKey));

        var error = Assert.Single(_jobRuns.Errors);
        Assert.Equal("ana/b", error.RepoKey);
        Assert.Equal(nameof(TimeoutException), error.ErrorType);

        var row = _jobRuns.Single();
        Assert.Equal(JobRunStatus.PartiallyFailed, row.Status);
        Assert.Contains("ana/b", row.ErrorSummary);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("PartiallyFailed"));
    }

    [Fact]
    public async Task All_repos_fail_ends_Failed()
    {
        _source.Keys.AddRange(["ana/a", "ana/b"]);
        _source.FailingKeys.UnionWith(["ana/a", "ana/b"]);

        var result = await CreateJob().RunAsync(Scheduled, TestContext.Current.CancellationToken);

        Assert.Equal(JobRunStatus.Failed, result!.Status);
        Assert.Equal(2, _jobRuns.Errors.Count);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("Failed"));
    }

    [Fact]
    public async Task Listing_failure_is_fatal_and_ends_Failed()
    {
        _source.ListException = new InvalidOperationException("login rechazado");

        var result = await CreateJob().RunAsync(Scheduled, TestContext.Current.CancellationToken);

        Assert.Equal(JobRunStatus.Failed, result!.Status);
        var error = Assert.Single(_jobRuns.Errors);
        Assert.Null(error.RepoKey);
        Assert.Contains("login rechazado", _jobRuns.Single().ErrorSummary);
        Assert.True(_jobRuns.Leases.Single().Disposed);
    }

    [Fact]
    public async Task Lock_not_obtained_does_not_process()
    {
        _source.Keys.Add("ana/a");
        _jobRuns.LockAvailable = false;

        var result = await CreateJob().RunAsync(Scheduled, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, _source.ListCalls);
        Assert.Empty(_store.Upserts);
        Assert.Empty(_jobRuns.Rows);
    }

    [Fact]
    public async Task Cancellation_stops_processing_marks_Abandoned_and_rethrows()
    {
        _source.Keys.AddRange(["ana/a", "ana/b", "ana/c"]);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _source.OnGet = key =>
        {
            if (key == "ana/b")
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateJob().RunAsync(Scheduled, cts.Token));

        Assert.Equal(["ana/a"], _store.Upserts.Select(u => u.Snapshot.ExternalKey));
        Assert.DoesNotContain("ana/c", _source.Requested);
        var row = _jobRuns.Single();
        Assert.Equal(JobRunStatus.Abandoned, row.Status);
        Assert.Equal(1, row.ReposOk);
        Assert.Empty(_jobRuns.Errors);
        Assert.True(_jobRuns.Leases.Single().Disposed);
    }

    [Fact]
    public async Task Zero_repos_ends_Failed_with_error_log()
    {
        var result = await CreateJob().RunAsync(Scheduled, TestContext.Current.CancellationToken);

        Assert.Equal(JobRunStatus.Failed, result!.Status);
        Assert.Equal(JobRunStatus.Failed, _jobRuns.Single().Status);
        Assert.Equal("EmptySource", Assert.Single(_jobRuns.Errors).ErrorType);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("0 repos"));
    }

    [Fact]
    public async Task Zero_repos_with_AllowEmptySource_ends_Succeeded()
    {
        _options.AllowEmptySource = true;

        var result = await CreateJob().RunAsync(Scheduled, TestContext.Current.CancellationToken);

        Assert.Equal(JobRunStatus.Succeeded, result!.Status);
        Assert.Empty(_jobRuns.Errors);
    }

    [Fact]
    public async Task Duplicated_keys_from_source_are_processed_once()
    {
        _source.Keys.AddRange(["ana/a", "ANA/a"]);

        var result = await CreateJob().RunAsync(Scheduled, TestContext.Current.CancellationToken);

        Assert.Equal(1, result!.ReposOk);
        Assert.Single(_store.Upserts);
    }
}
