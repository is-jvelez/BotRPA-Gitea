namespace Bots.Application;

/// <summary>
/// Resumen de un rango de ocurrencias recorrido una sola vez y sin materializarlo: cuántas hay, la más
/// reciente y las primeras <c>maxFirst</c> (para el log).
/// </summary>
internal sealed record OccurrenceWindow(int Count, DateTimeOffset? Latest, IReadOnlyList<DateTimeOffset> First)
{
    public static OccurrenceWindow From(IEnumerable<DateTimeOffset> occurrences, int maxFirst)
    {
        ArgumentNullException.ThrowIfNull(occurrences);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFirst);

        var count = 0;
        DateTimeOffset? latest = null;
        var first = new List<DateTimeOffset>(Math.Min(maxFirst, 64));
        foreach (var occurrence in occurrences)
        {
            count++;
            if (latest is null || occurrence > latest)
            {
                latest = occurrence;
            }

            if (first.Count < maxFirst)
            {
                first.Add(occurrence);
            }
        }

        return new OccurrenceWindow(count, latest, first);
    }
}
