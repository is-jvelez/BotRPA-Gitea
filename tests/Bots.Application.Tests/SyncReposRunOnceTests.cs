using Bots.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bots.Application.Tests;

public sealed class SyncReposRunOnceTests
{
    private const string Job = SyncReposOptions.DefaultJobName;

    private readonly FakeTimeProvider _time = new();
    private readonly FakeRepoSource _source = new();
    private readonly FakeRepoStore _store = new();
    private readonly FakeJobRunStore _jobRuns = new();
    private readonly ListLogger<SyncReposRunOnce> _logger = new();
    private readonly SyncReposRunOnceOptions _options = new() { StaleRunThreshold = TimeSpan.FromMinutes(45), Lookback = TimeSpan.FromHours(1) };

    public SyncReposRunOnceTests() => _source.Keys.Add("ana/a");

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 20, hour, minute, 0, TimeSpan.Zero);

    private SyncReposRunOnce Create()
    {
        var schedule = new FakeSchedule(TimeSpan.FromMinutes(5));
        var calculator = new PendingOccurrenceCalculator(schedule, _jobRuns, _time, new ListLogger<PendingOccurrenceCalculator>());
        var job = new SyncReposJob(_source, _store, _jobRuns, new SyncReposOptions(), new ListLogger<SyncReposJob>());
        return new SyncReposRunOnce(schedule, _jobRuns, calculator, job, _options, _time, _logger);
    }

    [Fact]
    public async Task Fired_at_the_occurrence_runs_it_without_history()
    {
        _time.SetUtcNow(At(10, 5));

        var result = await Create().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobRunStatus.Succeeded, result?.Status);
        Assert.Equal([TimeSpan.FromMinutes(45)], _jobRuns.MarkStaleCalls);
        Assert.Equal(At(10, 5), Assert.Single(_jobRuns.Rows).Key.ScheduledForUtc);
    }

    [Fact]
    public async Task Fired_late_runs_the_latest_occurrence_already_due()
    {
        _jobRuns.Seed(Job, At(9, 0), JobRunStatus.Succeeded);
        _time.SetUtcNow(At(10, 7));

        await Create().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobRunStatus.Succeeded, _jobRuns.Rows[(Job, At(10, 5))].Status);
        Assert.Equal(2, _jobRuns.Rows.Count);
    }

    [Fact]
    public async Task With_history_older_than_the_lookback_the_pending_occurrence_is_still_found()
    {
        _jobRuns.Seed(Job, At(1, 0), JobRunStatus.Succeeded);
        _options.Lookback = TimeSpan.FromMinutes(1);
        _time.SetUtcNow(At(10, 7));

        await Create().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobRunStatus.Succeeded, _jobRuns.Rows[(Job, At(10, 5))].Status);
    }

    [Fact]
    public async Task An_already_completed_occurrence_is_not_repeated()
    {
        _jobRuns.Seed(Job, At(10, 5), JobRunStatus.Succeeded);
        _time.SetUtcNow(At(10, 6));

        var result = await Create().RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(_store.Upserts);
    }

    [Fact]
    public async Task Without_history_or_occurrence_in_the_lookback_nothing_runs()
    {
        _options.Lookback = TimeSpan.FromMinutes(1);
        _time.SetUtcNow(At(10, 7));

        var result = await Create().RunAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Empty(_jobRuns.Rows);
        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("sin ocurrencia que correr"));
    }

    [Fact]
    public async Task A_failed_run_is_returned_as_failed()
    {
        _source.Keys.Clear();
        _time.SetUtcNow(At(10, 5));

        var result = await Create().RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(JobRunStatus.Failed, result?.Status);
    }
}
