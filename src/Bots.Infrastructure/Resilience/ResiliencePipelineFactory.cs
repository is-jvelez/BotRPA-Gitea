using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace Bots.Infrastructure.Resilience;

/// <summary>
/// Construye un pipeline de Polly: reintento con backoff exponencial + jitter y, dentro, circuit
/// breaker. Ambos actúan solo sobre los errores que <paramref name="isTransient"/> considera transitorios.
/// </summary>
internal static partial class ResiliencePipelineFactory
{
    public static ResiliencePipeline Create(
        string name,
        ResilienceOptions options,
        Func<Exception, bool> isTransient,
        TimeProvider timeProvider,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.MaxRetryAttempts);

        var shouldHandle = new PredicateBuilder().Handle(isTransient);
        var builder = new ResiliencePipelineBuilder { Name = name, TimeProvider = timeProvider };

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
                    LogRetry(logger, args.Outcome.Exception, name, args.Outcome.Exception?.GetType().Name, args.AttemptNumber + 1,
                        options.MaxRetryAttempts, (int)args.RetryDelay.TotalMilliseconds);
                    return default;
                },
            });
        }

        return builder
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = shouldHandle,
                FailureRatio = options.BreakerFailureRatio,
                SamplingDuration = options.BreakerSamplingDuration,
                MinimumThroughput = options.BreakerMinimumThroughput,
                BreakDuration = options.BreakDuration,
                OnOpened = args =>
                {
                    LogCircuitOpened(logger, args.Outcome.Exception, name, (int)args.BreakDuration.TotalSeconds);
                    return default;
                },
                OnClosed = _ =>
                {
                    LogCircuitClosed(logger, name);
                    return default;
                },
            })
            .Build();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Pipeline}: error transitorio ({ErrorType}); reintento {RetryAttempt} de {MaxRetryAttempts} en {RetryDelayMs} ms.")]
    private static partial void LogRetry(
        ILogger logger, Exception? exception, string pipeline, string? errorType, int retryAttempt, int maxRetryAttempts, int retryDelayMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Pipeline}: circuit breaker ABIERTO durante {BreakDurationSeconds} s; las operaciones fallarán sin intentarse. Ver runbook: circuit breaker.")]
    private static partial void LogCircuitOpened(ILogger logger, Exception? exception, string pipeline, int breakDurationSeconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Pipeline}: circuit breaker cerrado; el servicio responde de nuevo.")]
    private static partial void LogCircuitClosed(ILogger logger, string pipeline);
}
