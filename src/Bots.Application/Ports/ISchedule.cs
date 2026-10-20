namespace Bots.Application.Ports;

/// <summary>Calendario del job (la implementación con Cronos vive en Infrastructure).</summary>
public interface ISchedule
{
    /// <summary>Primera ocurrencia estrictamente posterior a <paramref name="fromUtc"/>, o <c>null</c> si no hay más.</summary>
    DateTimeOffset? GetNextOccurrence(DateTimeOffset fromUtc);

    /// <summary>
    /// Ocurrencias en (<paramref name="fromUtcExclusive"/>, <paramref name="toUtcInclusive"/>], en orden ascendente.
    /// Perezosa: con un cron frecuente y una caída larga el rango puede tener cientos de miles de ocurrencias,
    /// así que se recorre sin materializarlo (ver <c>OccurrenceWindow</c>).
    /// </summary>
    IEnumerable<DateTimeOffset> GetOccurrences(DateTimeOffset fromUtcExclusive, DateTimeOffset toUtcInclusive);
}
