using Bots.Domain;

namespace Bots.Application.Ports;

/// <summary>
/// Corrida en curso con el lock entre instancias tomado. El lock se mantiene hasta
/// <see cref="IAsyncDisposable.DisposeAsync"/>, que lo libera.
/// </summary>
public interface IJobRunLease : IAsyncDisposable
{
    JobRun Run { get; }
}
