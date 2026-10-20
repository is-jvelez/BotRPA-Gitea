namespace Bots.Infrastructure.Sql;

/// <summary>Opciones de <see cref="SqlJobRunStore"/> (se enlazan desde <c>Bots:Recovery</c> en el Worker).</summary>
public sealed class SqlJobRunStoreOptions
{
    /// <summary>
    /// Intentos máximos por ocurrencia. Si una ocurrencia <c>Failed</c>/<c>Abandoned</c> ya llegó a este
    /// número, <c>TryStartAsync</c> no la retoma (evita bucles si el contenedor se reinicia en cascada).
    /// </summary>
    public int MaxAttempts { get; set; } = 3;
}
