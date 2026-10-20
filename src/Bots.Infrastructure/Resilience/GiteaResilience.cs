using Microsoft.Extensions.Logging;
using Polly;

namespace Bots.Infrastructure.Resilience;

/// <summary>Pipeline de Polly para la navegación en Gitea (solo <see cref="GiteaTransientErrors"/>).</summary>
public sealed class GiteaResilience(ResilienceOptions options, TimeProvider timeProvider, ILogger<GiteaResilience> logger)
{
    public ResiliencePipeline Pipeline { get; } =
        ResiliencePipelineFactory.Create("Gitea", options, GiteaTransientErrors.IsTransient, timeProvider, logger);
}
