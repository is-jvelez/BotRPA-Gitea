namespace Bots.Application;

/// <summary>Opciones del job. Clase plana: Application no depende de Microsoft.Extensions.Options.</summary>
public sealed class SyncReposOptions
{
    public const string DefaultJobName = "sync-repos";

    public string JobName { get; set; } = DefaultJobName;

    /// <summary>Si es <c>false</c> (default), 0 repos en la fuente deja la corrida en <c>Failed</c>.</summary>
    public bool AllowEmptySource { get; set; }
}
