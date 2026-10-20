// Composition root. Se completa en la Fase 6 (CronWorker, DI, configuración).
var builder = Host.CreateApplicationBuilder(args);

var host = builder.Build();
await host.RunAsync();
