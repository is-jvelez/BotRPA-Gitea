namespace Bots.Domain;

internal static class UtcGuard
{
    public static void EnsureUtc(DateTimeOffset value, string paramName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException($"Se esperaba una fecha UTC (offset 0) y se recibió offset {value.Offset}.", paramName);
        }
    }
}
