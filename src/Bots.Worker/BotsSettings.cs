using Bots.Infrastructure.Resilience;

namespace Bots.Worker;

/// <summary>Sección <c>Bots</c> de la configuración.</summary>
public sealed class BotsSettings
{
    public const string SectionName = "Bots";

    public ScheduleSettings Schedule { get; set; } = new();

    /// <summary>Si es <c>false</c> (default), 0 repos en la fuente deja la corrida en Failed.</summary>
    public bool AllowEmptySource { get; set; }

    public RecoverySettings Recovery { get; set; } = new();

    public ResilienceSettings Resilience { get; set; } = new();
}

public sealed class ScheduleSettings
{
    /// <summary>
    /// Expresión cron de 5 campos. appsettings.json trae "*/5 * * * *" por defecto; en Docker la fija
    /// BOTS_SCHEDULE_CRON (obligatoria en el compose).
    /// </summary>
    public string Cron { get; set; } = string.Empty;

    /// <summary>Zona horaria IANA en la que se evalúa el cron, p. ej. America/Guayaquil.</summary>
    public string TimeZone { get; set; } = string.Empty;
}

public sealed class RecoverySettings
{
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Al arrancar, las corridas Running más antiguas que esto se marcan Abandoned.</summary>
    public TimeSpan StaleRunThreshold { get; set; } = TimeSpan.FromMinutes(30);
}

public sealed class ResilienceSettings
{
    public ResilienceOptions Sql { get; set; } = new();

    public ResilienceOptions Gitea { get; set; } = new();
}
