using Bots.Application.Ports;
using Cronos;

namespace Bots.Infrastructure.Scheduling;

/// <summary>
/// Calendario con Cronos. La expresión (5 campos, formato estándar) se evalúa en la zona horaria
/// configurada, de modo que "0 8 * * *" es a las 8:00 locales; las ocurrencias se devuelven en UTC.
/// </summary>
public sealed class CronosSchedule : ISchedule
{
    private readonly CronExpression _expression;
    private readonly TimeZoneInfo _timeZone;

    /// <exception cref="CronFormatException">Si la expresión cron no es válida.</exception>
    /// <exception cref="TimeZoneNotFoundException">Si la zona horaria no existe (IANA, p. ej. America/Guayaquil).</exception>
    public CronosSchedule(string cronExpression, string timeZoneId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cronExpression);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZoneId);

        _expression = CronExpression.Parse(cronExpression, CronFormat.Standard);
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }

    public DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc) =>
        _expression.GetNextOccurrence(fromUtc, _timeZone, inclusive: false)?.ToUniversalTime();

    public IReadOnlyList<DateTimeOffset> GetOccurrences(DateTimeOffset fromUtcExclusive, DateTimeOffset toUtcInclusive)
    {
        if (toUtcInclusive <= fromUtcExclusive)
        {
            return [];
        }

        return _expression
            .GetOccurrences(fromUtcExclusive, toUtcInclusive, _timeZone, fromInclusive: false, toInclusive: true)
            .Select(o => o.ToUniversalTime())
            .ToList();
    }
}
