namespace Bots.Application;

/// <summary>Opciones del bucle de programación. Clase plana: Application no depende de Microsoft.Extensions.Options.</summary>
public sealed class SyncReposSchedulerOptions
{
    public string JobName { get; set; } = SyncReposOptions.DefaultJobName;

    /// <summary>Al arrancar, las corridas Running iniciadas hace más de esto se marcan Abandoned.</summary>
    public TimeSpan StaleRunThreshold { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Espera máxima de una sola vez. Las esperas largas se trocean: Task.Delay no admite plazos muy largos
    /// y así se recalcula la siguiente ocurrencia si el reloj del sistema cambia.
    /// </summary>
    public TimeSpan MaxSingleWait { get; set; } = TimeSpan.FromHours(1);
}
