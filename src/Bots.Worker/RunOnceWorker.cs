using Bots.Application;
using Bots.Domain;

namespace Bots.Worker;

/// <summary>
/// Capa delgada del modo RunOnce: corre una ocurrencia con <see cref="SyncReposRunOnce"/>, fija el código de
/// salida y detiene el host. El cron externo (supercronic) registra ese código.
/// </summary>
/// <remarks>Códigos: 0 = completada o nada que correr; 1 = Failed, cancelada o excepción no controlada.</remarks>
internal sealed partial class RunOnceWorker(SyncReposRunOnce runOnce, IHostApplicationLifetime lifetime, ILogger<RunOnceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await runOnce.RunAsync(stoppingToken);
            Environment.ExitCode = result is { Status: JobRunStatus.Failed } ? 1 : 0;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // La corrida ya quedó Abandoned y se retomará en la siguiente ejecución.
            Environment.ExitCode = 1;
        }
        catch (Exception ex)
        {
            LogRunOnceCrashed(logger, ex);
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "El modo RunOnce terminó con una excepción no controlada. Ver runbook: el bot no corrió.")]
    private static partial void LogRunOnceCrashed(ILogger logger, Exception exception);
}
