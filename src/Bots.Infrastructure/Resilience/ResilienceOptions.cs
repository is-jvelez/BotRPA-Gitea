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

    /// <summary>
    /// Errores de rango, con los mismos límites que exige Polly 8 al construir el pipeline, pero con el nombre
    /// de la clave de configuración (p. ej. <c>Bots:Resilience:Sql:BreakDuration</c>) para que el arranque
    /// diga qué corregir. Vacía si las opciones son válidas.
    /// </summary>
    public IReadOnlyList<string> Validate(string section)
    {
        var errors = new List<string>();
        var minWindow = TimeSpan.FromMilliseconds(500);
        var oneDay = TimeSpan.FromDays(1);

        if (MaxRetryAttempts < 0)
        {
            errors.Add($"{section}:MaxRetryAttempts debe ser >= 0 (0 = sin reintentos).");
        }

        if (BaseDelay < TimeSpan.Zero || BaseDelay > oneDay)
        {
            errors.Add($"{section}:BaseDelay debe estar entre 00:00:00 y 1.00:00:00.");
        }

        if (MaxDelay < TimeSpan.Zero || MaxDelay > oneDay)
        {
            errors.Add($"{section}:MaxDelay debe estar entre 00:00:00 y 1.00:00:00.");
        }

        if (double.IsNaN(BreakerFailureRatio) || BreakerFailureRatio < 0 || BreakerFailureRatio > 1)
        {
            errors.Add($"{section}:BreakerFailureRatio debe estar entre 0 y 1.");
        }

        if (BreakerSamplingDuration < minWindow || BreakerSamplingDuration > oneDay)
        {
            errors.Add($"{section}:BreakerSamplingDuration debe estar entre 00:00:00.500 y 1.00:00:00.");
        }

        if (BreakerMinimumThroughput < 2)
        {
            errors.Add($"{section}:BreakerMinimumThroughput debe ser >= 2.");
        }

        if (BreakDuration < minWindow || BreakDuration > oneDay)
        {
            errors.Add($"{section}:BreakDuration debe estar entre 00:00:00.500 y 1.00:00:00.");
        }

        return errors;
    }
}
