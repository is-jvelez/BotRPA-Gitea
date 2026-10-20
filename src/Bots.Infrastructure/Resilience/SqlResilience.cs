using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Bots.Infrastructure.Resilience;

/// <summary>
/// Pipeline de Polly para las operaciones de SQL: reintento con backoff exponencial + jitter y,
/// dentro, circuit breaker. Solo actúan sobre <see cref="SqlTransientErrors"/>. Debe ser una única
/// instancia compartida por los almacenes para que el estado del circuito sea común.
/// </summary>
/// <remarks>
/// Cada operación protegida abre su propia conexión y transacción, así que un reintento repite la
/// operación completa. Las operaciones están escritas para que repetirlas sea seguro.
/// </remarks>
public sealed class SqlResilience
{
    public SqlResilience(ResilienceOptions options, TimeProvider timeProvider, ILogger<SqlResilience> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetryAttempts);

        var shouldHandle = new PredicateBuilder().Handle<Exception>(SqlTransientErrors.IsTransient);
        var builder = new ResiliencePipelineBuilder { TimeProvider = timeProvider };

        // Polly exige al menos 1 reintento en la estrategia; con 0 simplemente no se añade.
        if (options.MaxRetryAttempts > 0)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = shouldHandle,
                MaxRetryAttempts = options.MaxRetryAttempts,
                Delay = options.BaseDelay,
                MaxDelay = options.MaxDelay,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                OnRetry = args =>
                {
                    logger.LogWarning(args.Outcome.Exception,
                        "Error transitorio de SQL ({ErrorType}); reintento {RetryAttempt} de {MaxRetryAttempts} en {RetryDelayMs} ms.",
                        args.Outcome.Exception?.GetType().Name, args.AttemptNumber + 1, options.MaxRetryAttempts,
                        (int)args.RetryDelay.TotalMilliseconds);
                    return default;
                },
            });
        }

        Pipeline = builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = shouldHandle,
                FailureRatio = options.BreakerFailureRatio,
                SamplingDuration = options.BreakerSamplingDuration,
                MinimumThroughput = options.BreakerMinimumThroughput,
                BreakDuration = options.BreakDuration,
                OnOpened = args =>
                {
                    logger.LogError(args.Outcome.Exception,
                        "Circuit breaker de SQL ABIERTO durante {BreakDurationSeconds} s: las operaciones de BD fallarán sin intentarse. Ver runbook: circuit breaker.",
                        (int)args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = _ =>
                {
                    logger.LogInformation("Circuit breaker de SQL cerrado: la BD responde de nuevo.");
                    return default;
                },
            })
            .Build();
    }

    public ResiliencePipeline Pipeline { get; }
}
