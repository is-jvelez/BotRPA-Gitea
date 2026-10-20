namespace Bots.Domain;

public enum JobRunStatus
{
    Running,
    Succeeded,
    PartiallyFailed,
    Failed,
    Abandoned,
}

public static class JobRunStatusExtensions
{
    /// <summary>
    /// Estados que cuentan como ocurrencia completada para la recuperación.
    /// <c>PartiallyFailed</c> cuenta: los repos fallidos se reprocesan en la siguiente corrida.
    /// </summary>
    public static bool CountsAsCompleted(this JobRunStatus status) =>
        status is JobRunStatus.Succeeded or JobRunStatus.PartiallyFailed;

    /// <summary>Estados cuya ocurrencia puede retomarse con un nuevo intento.</summary>
    public static bool IsRetryable(this JobRunStatus status) =>
        status is JobRunStatus.Failed or JobRunStatus.Abandoned;
}
