using Bots.Infrastructure.Resilience;
using Dapper;
using Microsoft.Data.SqlClient;
using Polly.CircuitBreaker;

namespace Bots.Infrastructure.Tests;

public sealed class SqlResilienceTests(SqlServerFixture db)
{
    private static readonly ResilienceOptions Fast = new()
    {
        MaxRetryAttempts = 2,
        BaseDelay = TimeSpan.FromMilliseconds(1),
        MaxDelay = TimeSpan.FromMilliseconds(5),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Transient_error_is_retried_then_rethrown()
    {
        var pipeline = SqlServerFixture.Resilience(Fast).Pipeline;
        var calls = 0;

        await Assert.ThrowsAsync<TimeoutException>(async () => await pipeline.ExecuteAsync(_ =>
        {
            calls++;
            throw new TimeoutException();
        }, Ct));

        Assert.Equal(3, calls); // 1 intento + 2 reintentos
    }

    [Fact]
    public async Task Non_transient_error_is_not_retried()
    {
        var pipeline = SqlServerFixture.Resilience(Fast).Pipeline;
        var calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pipeline.ExecuteAsync(_ =>
        {
            calls++;
            throw new InvalidOperationException();
        }, Ct));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Real_non_transient_SqlException_is_not_classified_as_transient()
    {
        await using var connection = new SqlConnection(db.AppConnectionString);

        var permissionDenied = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync("CREATE TABLE bots.x (id INT);"));
        var uniqueViolation = await Assert.ThrowsAsync<SqlException>(() => connection.ExecuteAsync(
            "DECLARE @t TABLE (id INT PRIMARY KEY); INSERT INTO @t VALUES (1), (1);"));

        Assert.False(SqlTransientErrors.IsTransient(permissionDenied));
        Assert.False(SqlTransientErrors.IsTransient(uniqueViolation));
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_transient_failures()
    {
        var options = new ResilienceOptions
        {
            MaxRetryAttempts = 0,
            BreakerMinimumThroughput = 2,
            BreakerFailureRatio = 0.5,
            BreakDuration = TimeSpan.FromMinutes(1),
        };
        var pipeline = SqlServerFixture.Resilience(options).Pipeline;
        var calls = 0;

        for (var i = 0; i < 2; i++)
        {
            await Assert.ThrowsAsync<TimeoutException>(async () => await pipeline.ExecuteAsync(_ =>
            {
                calls++;
                throw new TimeoutException();
            }, Ct));
        }

        await Assert.ThrowsAsync<BrokenCircuitException>(async () => await pipeline.ExecuteAsync(_ =>
        {
            calls++;
            return ValueTask.CompletedTask;
        }, Ct));
        Assert.Equal(2, calls);
    }
}
