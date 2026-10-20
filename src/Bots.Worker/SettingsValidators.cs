using Bots.Infrastructure.Gitea;
using Bots.Infrastructure.Scheduling;
using Bots.Infrastructure.Sql;
using Microsoft.Extensions.Options;

namespace Bots.Worker;

// Validaciones de arranque (ValidateOnStart): si la configuración no es válida, el bot no arranca y el
// log dice qué falta. Solo comprueban presencia y rangos; la lógica (p. ej. parsear el cron) vive en
// Infrastructure y está probada allí.

internal sealed class BotsSettingsValidator : IValidateOptions<BotsSettings>
{
    public ValidateOptionsResult Validate(string? name, BotsSettings options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.Schedule.Cron) || string.IsNullOrWhiteSpace(options.Schedule.TimeZone))
        {
            errors.Add("Bots:Schedule:Cron y Bots:Schedule:TimeZone son obligatorios (BOTS_SCHEDULE_CRON, BOTS_SCHEDULE_TIMEZONE).");
        }
        else
        {
            try
            {
                _ = new CronosSchedule(options.Schedule.Cron, options.Schedule.TimeZone);
            }
            catch (Exception ex)
            {
                errors.Add($"Calendario inválido (cron '{options.Schedule.Cron}', zona '{options.Schedule.TimeZone}'): {ex.Message}");
            }
        }

        if (options.Recovery.MaxAttempts < 1)
        {
            errors.Add("Bots:Recovery:MaxAttempts debe ser >= 1.");
        }

        if (options.Recovery.StaleRunThreshold <= TimeSpan.Zero)
        {
            errors.Add("Bots:Recovery:StaleRunThreshold debe ser mayor que 0.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class GiteaOptionsValidator : IValidateOptions<GiteaOptions>
{
    public ValidateOptionsResult Validate(string? name, GiteaOptions options)
    {
        var errors = new List<string>();

        if (options.BaseUrl is not { IsAbsoluteUri: true } url || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("Gitea:BaseUrl debe ser una URL http(s) absoluta (GITEA_BASE_URL).");
        }

        if (string.IsNullOrWhiteSpace(options.User) || string.IsNullOrWhiteSpace(options.Password))
        {
            errors.Add("Gitea:User y Gitea:Password son obligatorios (GITEA_USER, GITEA_PASSWORD).");
        }

        if (options.NavigationTimeout <= TimeSpan.Zero || options.ActionTimeout <= TimeSpan.Zero)
        {
            errors.Add("Gitea:NavigationTimeout y Gitea:ActionTimeout deben ser mayores que 0.");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}

internal sealed class SqlConnectionSettingsValidator : IValidateOptions<SqlConnectionSettings>
{
    public ValidateOptionsResult Validate(string? name, SqlConnectionSettings options) =>
        string.IsNullOrWhiteSpace(options.Server) || string.IsNullOrWhiteSpace(options.Database)
            || string.IsNullOrWhiteSpace(options.User) || string.IsNullOrWhiteSpace(options.Password)
            ? ValidateOptionsResult.Fail("Sql:Server, Sql:Database, Sql:User y Sql:Password son obligatorios (APP_DB_USER, APP_DB_PASSWORD).")
            : ValidateOptionsResult.Success;
}
