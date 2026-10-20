using Bots.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Bots.Application.Tests;

/// <summary>
/// El bucle corre en segundo plano con un FakeTimeProvider. Los tests se sincronizan con los logs (seguros
/// entre hilos) y solo leen los fakes de datos después de detener el bucle.
/// </summary>
public sealed class SyncReposSchedulerTests
{
    private const string Job = SyncReposOptions.DefaultJobName;
    private const string Waiting = "siguiente ocurrencia";

    private readonly FakeTimeProvider _time = new();
    private readonly FakeRepoSource _source = new();
    private readonly FakeRepoStore _store = new();
    private readonly FakeJobRunStore _jobRuns = new();
    private readonly ListLogger<SyncReposScheduler> _logger = new();
    private readonly SyncReposSchedulerOptions _options = new() { StaleRunThreshold = TimeSpan.FromMinutes(45) };

    public SyncReposSchedulerTests() => _source.Keys.Add("ana/a");

    private static DateTimeOffset At(int hour, int minute) => new(2026, 10, 20, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public async Task At_startup_marks_stale_runs_and_recovers_the_missed_occurrence()
    {
        _jobRuns.Seed(Job, At(10, 0), JobRunStatus.Succeeded);
        _time.SetUtcNow(At(10, 12));

        await using (var running = Start())
        {
            await running.WaitForLogAsync(Waiting, 1);
        }

        Assert.Equal([TimeSpan.FromMinutes(45)], _jobRuns.MarkStaleCalls);
        Assert.Equal(JobRunStatus.Succeeded, _jobRuns.Rows[(Job, At(10, 10))].Status);
    }

    [Fact]
    public async Task Without_history_it_waits_and_runs_the_due_occurrence()
    {
        _time.SetUtcNow(At(10, 2));

        await using (var running = Start())
        {
            await running.WaitForLogAsync(Waiting, 1);
            Assert.Empty(_store.Upserts);

            _time.Advance(TimeSpan.FromMinutes(3));
            await running.WaitForLogAsync(Waiting, 2);
        }

        var row = Assert.Single(_jobRuns.Rows);
        Assert.Equal(At(10, 5), row.Key.ScheduledForUtc);
        Assert.Equal(JobRunStatus.Succeeded, row.Value.Status);
    }

    [Fact]
    public async Task An_exception_in_a_run_does_not_stop_the_loop()
    {
        _time.SetUtcNow(At(10, 2));
        _jobRuns.TryStartExceptionOnce = new TimeoutException("BD caída");

        await using (var running = Start())
        {
            await running.WaitForLogAsync(Waiting, 1);
            _time.Advance(TimeSpan.FromMinutes(3)); // 10:05 falla al iniciar
            await running.WaitForLogAsync(Waiting, 2);
            _time.Advance(TimeSpan.FromMinutes(5)); // 10:10 corre bien
            await running.WaitForLogAsync(Waiting, 3);
        }

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("excepción no controlada"));
        Assert.Equal(JobRunStatus.Succeeded, _jobRuns.Rows[(Job, At(10, 10))].Status);
    }

    [Fact]
    public async Task Occurrences_arriving_during_a_run_are_not_started_in_parallel_and_the_latest_is_recovered()
    {
        _jobRuns.Seed(Job, At(10, 0), JobRunStatus.Succeeded);
        _time.SetUtcNow(At(10, 1));
        var slowOnce = true;
        _source.OnGet = _ =>
        {
            if (slowOnce)
            {
                slowOnce = false;
                _time.Advance(TimeSpan.FromMinutes(12)); // la corrida de 10:05 termina a las 10:17
            }
        };

        await using (var running = Start())
        {
            await running.WaitForLogAsync(Waiting, 1);
            _time.Advance(TimeSpan.FromMinutes(4)); // 10:05
            await running.WaitForLogAsync(Waiting, 2);
        }

        Assert.Contains(_logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("no se iniciaron en paralelo"));
        Assert.Equal(JobRunStatus.Succeeded, _jobRuns.Rows[(Job, At(10, 5))].Status);
        Assert.Equal(JobRunStatus.Succeeded, _jobRuns.Rows[(Job, At(10, 15))].Status);
        Assert.False(_jobRuns.Rows.ContainsKey((Job, At(10, 10))));
    }

    [Fact]
    public async Task Cancellation_stops_the_loop_without_throwing()
    {
        _time.SetUtcNow(At(10, 2));
        var running = Start();
        await running.WaitForLogAsync(Waiting, 1);

        await running.DisposeAsync();

        Assert.True(running.Task.IsCompletedSuccessfully);
        Assert.Contains(_logger.Entries, e => e.Message.Contains("programador detenido"));
    }

    private RunningScheduler Start()
    {
        var schedule = new FakeSchedule(TimeSpan.FromMinutes(5));
        var calculator = new PendingOccurrenceCalculator(schedule, _jobRuns, _time, new ListLogger<PendingOccurrenceCalculator>());
        var job = new SyncReposJob(_source, _store, _jobRuns, new SyncReposOptions(), new ListLogger<SyncReposJob>());
        var scheduler = new SyncReposScheduler(schedule, _jobRuns, calculator, job, _options, _time, _logger);

        var cts = new CancellationTokenSource();
        return new RunningScheduler(Task.Run(() => scheduler.RunAsync(cts.Token)), cts, _logger);
    }

    private sealed class RunningScheduler(Task task, CancellationTokenSource cts, ListLogger<SyncReposScheduler> logger) : IAsyncDisposable
    {
        public Task Task { get; } = task;

        /// <summary>Espera (en tiempo real, máx. 10 s) a que el log contenga <paramref name="text"/> al menos <paramref name="count"/> veces.</summary>
        public async Task WaitForLogAsync(string text, int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (logger.Entries.Count(e => e.Message.Contains(text)) < count)
            {
                if (Task.IsFaulted)
                {
                    await Task;
                }

                Assert.True(DateTime.UtcNow < deadline, $"No apareció '{text}' {count} veces en el log.");
                await System.Threading.Tasks.Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await cts.CancelAsync();
            await Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Dispose();
        }
    }
}
