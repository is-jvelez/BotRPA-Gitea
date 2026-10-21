using Bots.Infrastructure.Resilience;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bots.Infrastructure.Tests;

/// <summary>
/// <see cref="ResilienceOptions.Validate"/> debe aceptar y rechazar exactamente lo mismo que Polly al construir
/// el pipeline: así el arranque falla con un mensaje claro en vez de con la excepción de Polly.
/// </summary>
public sealed class ResilienceOptionsTests
{
    private const string Section = "Bots:Resilience:Sql";

    public static TheoryData<string, Action<ResilienceOptions>> Invalid => new()
    {
        { "MaxRetryAttempts", o => o.MaxRetryAttempts = -1 },
        { "BaseDelay", o => o.BaseDelay = TimeSpan.FromMilliseconds(-1) },
        { "BaseDelay", o => o.BaseDelay = TimeSpan.FromDays(2) },
        { "MaxDelay", o => o.MaxDelay = TimeSpan.FromDays(2) },
        { "BreakerFailureRatio", o => o.BreakerFailureRatio = 1.5 },
        { "BreakerFailureRatio", o => o.BreakerFailureRatio = -0.1 },
        { "BreakerSamplingDuration", o => o.BreakerSamplingDuration = TimeSpan.FromMilliseconds(499) },
        { "BreakerMinimumThroughput", o => o.BreakerMinimumThroughput = 1 },
        { "BreakDuration", o => o.BreakDuration = TimeSpan.FromMilliseconds(499) },
        { "BreakDuration", o => o.BreakDuration = TimeSpan.FromDays(2) },
    };

    public static TheoryData<Action<ResilienceOptions>> ValidBoundaries => new()
    {
        o => { },
        o => o.MaxRetryAttempts = 0,
        o => { o.BaseDelay = TimeSpan.Zero; o.MaxDelay = TimeSpan.FromDays(1); },
        o => { o.BreakerFailureRatio = 0; o.BreakerMinimumThroughput = 2; },
        o => o.BreakerFailureRatio = 1,
        o => { o.BreakerSamplingDuration = TimeSpan.FromMilliseconds(500); o.BreakDuration = TimeSpan.FromMilliseconds(500); },
        o => { o.BreakerSamplingDuration = TimeSpan.FromDays(1); o.BreakDuration = TimeSpan.FromDays(1); },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Out_of_range_value_is_reported_with_its_key_and_Polly_rejects_it_too(string property, Action<ResilienceOptions> change)
    {
        var options = new ResilienceOptions();
        change(options);

        var error = Assert.Single(options.Validate(Section));

        Assert.StartsWith($"{Section}:{property} ", error);
        Assert.ThrowsAny<Exception>(() => Build(options));
    }

    [Theory]
    [MemberData(nameof(ValidBoundaries))]
    public void Valid_value_passes_and_Polly_accepts_it_too(Action<ResilienceOptions> change)
    {
        var options = new ResilienceOptions();
        change(options);

        Assert.Empty(options.Validate(Section));
        Assert.NotNull(Build(options));
    }

    private static Polly.ResiliencePipeline Build(ResilienceOptions options) =>
        ResiliencePipelineFactory.Create("test", options, _ => true, TimeProvider.System, NullLogger.Instance);
}
