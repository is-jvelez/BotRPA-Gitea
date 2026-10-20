using Microsoft.Extensions.Logging;
using Polly;

namespace Bots.Infrastructure.Resilience;

/// <summary>
/// Pipeline de Polly para las operaciones de SQL (solo <see cref="SqlTransientErrors"/>). Debe ser una
/// única instancia compartida por los almacenes para que el estado del circuito sea común.
/// </summary>
/// <remarks>
/// Cada operación protegida abre su propia conexión y transacción, así que un reintento repite la
/// operación completa. Las operaciones están escritas para que repetirlas sea seguro.
/// </remarks>
public sealed class SqlResilience(ResilienceOptions options, TimeProvider timeProvider, ILogger<SqlResilience> logger)
{
    public ResiliencePipeline Pipeline { get; } =
        ResiliencePipelineFactory.Create("SQL", options, SqlTransientErrors.IsTransient, timeProvider, logger);
}
