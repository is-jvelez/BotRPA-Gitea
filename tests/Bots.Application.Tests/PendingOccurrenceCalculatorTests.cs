using Bots.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bots.Application.Tests;

public sealed class PendingOccurrenceCalculatorTests
{
    private const string Job = SyncReposOptions.DefaultJobName;

    private readonly FakeJobRunStore _jobRuns = new();
    private readonly FakeTimeProvider _time = new();
    private readonly ListLogger<PendingOccurrenceCalculator> _logger = new();

    private PendingOccurrenceCalculator CreateCalculator() =>
        new(new FakeSchedule(TimeSpan.FromMinutes(5)), _jobRuns, _time, _logger);

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 20, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task No_history_means_no_recovery()
    {
        _time.SetUtcNow(At(10, 7));

        var pending = await CreateCalculator().GetPendingAsync(Job, TestContext.Current.CancellationToken);

        Assert.Null(pending);
    }

    [Fact]
    public async Task Up_to_date_returns_null()
    {
        _jobRuns.Seed(Job, At(10, 5), JobRunStatus.Succeeded);
        _time.SetUtcNow(At(10, 7));

        Assert.Null(await CreateCalculator().GetPendingAsync(Job, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_missed_occurrence_is_returned()
    {
        _jobRuns.Seed(Job, At(10, 0), JobRunStatus.Succeeded);
        _time.SetUtcNow(At(10, 7));

        Assert.Equal(At(10, 5), await CreateCalculator().GetPendingAsync(Job, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Occurrence_exactly_now_is_pending()
    {
        _jobRuns.Seed(Job, At(10, 0), JobRunStatus.Succeeded);
        _time.SetUtcNow(At(10, 5));

        Assert.Equal(At(10, 5), await CreateCalculator().GetPendingAsync(Job, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Several_missed_runs_only_the_most_recent_and_logs_the_rest()
    {
        _jobRuns.Seed(Job, At(10, 0), JobRunStatus.Succeeded);
        _time.SetUtcNow(At(10, 22));

        var pending = await CreateCalculator().GetPendingAsync(Job, TestContext.Current.CancellationToken);

        Assert.Equal(At(10, 20), pending);
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("4 ocurrencias pendientes", warning.Message);
        Assert.Contains(At(10, 5).ToString("o"), warning.Message);
        Assert.Contains(At(10, 15).ToString("o"), warning.Message);
    }

    [Fact]
    public async Task Long_outage_returns_the_latest_and_logs_only_the_first_skipped()
    {
        // Un año caído con un cron de 5 minutos: 105 120 ocurrencias en el rango.
        _jobRuns.Seed(Job, At(10, 0), JobRunStatus.Succeeded);
        var latest = At(10, 0).AddYears(1);
        _time.SetUtcNow(latest);

        var pending = await CreateCalculator().GetPendingAsync(Job, TestContext.Current.CancellationToken);

        Assert.Equal(latest, pending);
        var warning = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("105120 ocurrencias pendientes", warning.Message);
        Assert.Contains("se omiten 105119", warning.Message);
        Assert.Contains(At(11, 40).ToString("o"), warning.Message);       // la 20.ª omitida
        Assert.DoesNotContain(At(11, 45).ToString("o"), warning.Message); // la 21.ª ya no se lista
        Assert.EndsWith("(y 105099 más)", warning.Message);
    }

    [Fact]
    public async Task Previously_failed_occurrence_is_retried()
    {
        _jobRuns.Seed(Job, At(10, 0), JobRunStatus.Succeeded);
        _jobRuns.Seed(Job, At(10, 5), JobRunStatus.Failed);
        _time.SetUtcNow(At(10, 7));

        Assert.Equal(At(10, 5), await CreateCalculator().GetPendingAsync(Job, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PartiallyFailed_counts_as_completed()
    {
        _jobRuns.Seed(Job, At(10, 0), JobRunStatus.Succeeded);
        _jobRuns.Seed(Job, At(10, 5), JobRunStatus.PartiallyFailed);
        _time.SetUtcNow(At(10, 7));

        Assert.Null(await CreateCalculator().GetPendingAsync(Job, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(JobRunStatus.Succeeded, true)]
    [InlineData(JobRunStatus.PartiallyFailed, true)]
    [InlineData(JobRunStatus.Failed, false)]
    [InlineData(JobRunStatus.Abandoned, false)]
    [InlineData(JobRunStatus.Running, false)]
    public void CountsAsCompleted_matches_recovery_rule(JobRunStatus status, bool expected) =>
        Assert.Equal(expected, status.CountsAsCompleted());
}
