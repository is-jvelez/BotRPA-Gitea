// Composition root: configuración, validación al arranque e inyección de dependencias. Sin lógica de
// negocio (vive en Application/Infrastructure, donde se prueba).
using Bots.Application;
using Bots.Application.Ports;
using Bots.Infrastructure.Gitea;
using Bots.Infrastructure.Resilience;
using Bots.Infrastructure.Scheduling;
using Bots.Infrastructure.Sql;
using Bots.Worker;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Debe ser menor que stop_grace_period del compose: deja tiempo a la corrida en curso para cerrarse
// como Abandoned antes de que Docker mate el proceso.
builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(45));

builder.Services.AddOptions<BotsSettings>().Bind(builder.Configuration.GetSection(BotsSettings.SectionName)).ValidateOnStart();
builder.Services.AddOptions<GiteaOptions>().Bind(builder.Configuration.GetSection("Gitea")).ValidateOnStart();
builder.Services.AddOptions<SqlConnectionSettings>().Bind(builder.Configuration.GetSection("Sql")).ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BotsSettings>, BotsSettingsValidator>();
builder.Services.AddSingleton<IValidateOptions<GiteaOptions>, GiteaOptionsValidator>();
builder.Services.AddSingleton<IValidateOptions<SqlConnectionSettings>, SqlConnectionSettingsValidator>();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<BotsSettings>>().Value);

// --- Infrastructure ---
builder.Services.AddSingleton<ISchedule>(sp =>
{
    var schedule = sp.GetRequiredService<BotsSettings>().Schedule;
    return new CronosSchedule(schedule.Cron, schedule.TimeZone);
});
builder.Services.AddSingleton(sp => new SqlConnectionFactory(sp.GetRequiredService<IOptions<SqlConnectionSettings>>().Value.BuildConnectionString()));
builder.Services.AddSingleton(sp => new SqlResilience(
    sp.GetRequiredService<BotsSettings>().Resilience.Sql, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<SqlResilience>>()));
builder.Services.AddSingleton(sp => new GiteaResilience(
    sp.GetRequiredService<BotsSettings>().Resilience.Gitea, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<GiteaResilience>>()));
builder.Services.AddSingleton(sp => new SqlJobRunStoreOptions { MaxAttempts = sp.GetRequiredService<BotsSettings>().Recovery.MaxAttempts });
builder.Services.AddSingleton<IJobRunStore, SqlJobRunStore>();
builder.Services.AddSingleton<IRepoStore, SqlRepoStore>();
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<GiteaOptions>>().Value);
// Singleton IAsyncDisposable: el contenedor cierra el navegador al apagar el host.
builder.Services.AddSingleton<IRepoSource, PlaywrightRepoSource>();

// --- Application ---
builder.Services.AddSingleton(sp => new SyncReposOptions { AllowEmptySource = sp.GetRequiredService<BotsSettings>().AllowEmptySource });
builder.Services.AddSingleton(sp => new SyncReposSchedulerOptions { StaleRunThreshold = sp.GetRequiredService<BotsSettings>().Recovery.StaleRunThreshold });
builder.Services.AddSingleton<SyncReposJob>();
builder.Services.AddSingleton<PendingOccurrenceCalculator>();
builder.Services.AddSingleton<SyncReposScheduler>();
builder.Services.AddSingleton(sp => new SyncReposRunOnceOptions { StaleRunThreshold = sp.GetRequiredService<BotsSettings>().Recovery.StaleRunThreshold });
builder.Services.AddSingleton<SyncReposRunOnce>();

// El modo se lee antes de construir el host: decide qué servicio se registra. Un valor inválido lo
// rechaza el binder al arrancar.
if (builder.Configuration.GetSection(BotsSettings.SectionName).Get<BotsSettings>()?.Mode == BotsMode.RunOnce)
{
    builder.Services.AddHostedService<RunOnceWorker>();
}
else
{
    builder.Services.AddHostedService<CronWorker>();
}

var host = builder.Build();
await host.RunAsync();
