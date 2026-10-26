namespace Bots.Application;

/// <summary>Opciones del modo RunOnce. Clase plana: Application no depende de Microsoft.Extensions.Options.</summary>
public sealed class SyncReposRunOnceOptions
{
    public string JobName { get; set; } = SyncReposOptions.DefaultJobName;

    /// <summary>Al arrancar, las corridas Running iniciadas hace más de esto se marcan Abandoned.</summary>
    public TimeSpan StaleRunThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Sin historial, hasta dónde se mira hacia atrás para encontrar la ocurrencia más reciente. Cubre
    /// también una ejecución manual fuera de hora (p. ej. <c>docker compose run</c>).
    /// </summary>
    public TimeSpan Lookback { get; set; } = TimeSpan.FromDays(1);
}
