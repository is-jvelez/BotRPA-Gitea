using Bots.Application.Ports;
using Bots.Domain;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Bots.Infrastructure.Sql;

/// <summary>
/// Mantiene abierta la conexión dedicada que tiene el applock de sesión durante toda la corrida.
/// </summary>
/// <remarks>
/// Limitación conocida: la conexión queda inactiva mientras dura la corrida. Un corte de red o un
/// timeout de inactividad (p. ej. en Azure) cierra la sesión y libera el lock sin aviso; otra instancia
/// podría entonces tomarlo. El UNIQUE de job_runs y la guarda de <c>CompleteAsync</c> evitan el
/// duplicado de filas, pero es un riesgo a revisar al ir a Azure (ver blueprint).
/// </remarks>
internal sealed partial class SqlJobRunLease(JobRun run, SqlConnection lockConnection, string resource, ILogger logger) : IJobRunLease
{
    private SqlConnection? _lockConnection = lockConnection;

    public JobRun Run { get; } = run;

    public ValueTask DisposeAsync()
    {
        var connection = Interlocked.Exchange(ref _lockConnection, null);
        return connection is null ? ValueTask.CompletedTask : CloseAsync(connection, resource, lockHeld: true, logger);
    }

    /// <summary>
    /// Libera el lock explícitamente (si se tomó) y cierra la conexión; al cerrarla termina la sesión.
    /// </summary>
    internal static async ValueTask CloseAsync(SqlConnection connection, string resource, bool lockHeld, ILogger logger)
    {
        try
        {
            if (lockHeld && connection.State == System.Data.ConnectionState.Open)
            {
                await SqlAppLock.ReleaseAsync(connection, resource, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // La conexión no usa pool: al cerrarla se termina la sesión y el lock se libera igualmente.
            LogReleaseFailed(logger, ex, resource);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo liberar explícitamente el applock {LockResource}; se libera al cerrar la sesión.")]
    private static partial void LogReleaseFailed(ILogger logger, Exception exception, string lockResource);
}
