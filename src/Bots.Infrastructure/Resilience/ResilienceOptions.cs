namespace Bots.Infrastructure.Resilience;

/// <summary>Parámetros de Polly (se enlazan desde <c>Bots:Resilience</c> en el Worker).</summary>
public sealed class ResilienceOptions
{
    /// <summary>Reintentos tras el primer intento (0 = sin reintentos).</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>Retraso base del backoff exponencial (se le aplica jitter).</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Tope de cada retraso entre reintentos.</summary>
    public TimeSpan MaxDelay { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Proporción de fallos transitorios que abre el circuito.</summary>
    public double BreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Ventana en la que se mide la proporción de fallos.</summary>
    public TimeSpan BreakerSamplingDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Llamadas mínimas en la ventana antes de poder abrir el circuito.</summary>
    public int BreakerMinimumThroughput { get; set; } = 10;

    /// <summary>Tiempo que el circuito permanece abierto antes de probar de nuevo.</summary>
    public TimeSpan BreakDuration { get; set; } = TimeSpan.FromSeconds(30);
}
