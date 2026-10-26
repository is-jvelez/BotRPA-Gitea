# Prompt inicial — Bots.Platform (repo nuevo, PoC actual como referencia)

> Uso: abre Claude Code en la carpeta **nueva y vacía** del repo (ej. `C:\Users\jose.velez\Desktop\BG\MP\Project\Bots.Platform`),
> agrega la carpeta de la PoC actual como directorio adicional de solo lectura y pega desde "ROL" hasta el final.
> Antes, en la PoC: haz commit de lo pendiente (RunOnce, docker-compose.cron.yml, docs) y crea el tag `poc-final`,
> para que la referencia sea un estado fijo y no un working tree con cambios sueltos.

---

## ROL

Eres un arquitecto/desarrollador senior .NET con experiencia en Worker Services, Cronos, Flyway, Dapper, Polly, Playwright, Docker y SQL Server. Vas a construir **Bots.Platform**: un monorepo con una plataforma reutilizable para bots RPA y, como primer bot sobre ella, **SyncRepos** (reimplementación del bot de la PoC).

## CONTEXTO

- **PoC de referencia (SOLO LECTURA):** `C:\Users\jose.velez\Desktop\BG\MP\Project\Bots` (tag `poc-final`).
  - Es evidencia y material de aprendizaje. **No modifiques, no hagas commit, no cambies de rama ni ejecutes nada que escriba en esa carpeta** (incluido `dotnet build`, que genera `bin/` y `obj/`). Solo léela.
  - Contiene un bot que sincroniza repos de Gitea por RPA (Playwright) hacia SQL Server, con garantías fuertes contra omisión y duplicado. Léete primero: `docs/blueprint.md`, `docs/runbook.md`, `docs/ejecucion.md`, `docs/prompts/plantilla-bot-rpa.md`, `docs/prompts/ficha-ejemplo-creditos.md`, `flyway/sql/*`, `src/**`, `tests/**`, `Dockerfile`, `docker-compose*.yml`, `.github/**`, `Directory.*.props`.
- **Repo nuevo:** la carpeta actual. Historial git nuevo, rama `main`.
- **Criterio de negocio (no cambia):** la omisión es lo peor; el duplicado también es grave.

## QUÉ SIGNIFICA "EMPEZAR DE CERO CON LO QUE TENGO"

- La estructura, el esquema de BD y los nombres son nuevos (ver ESTRUCTURA y BASE DE DATOS).
- El **comportamiento** de la PoC es el requisito: cada garantía, regla de resiliencia y decisión del `blueprint.md` de la PoC debe existir en el repo nuevo, salvo que la cambie explícitamente este prompt.
- Puedes **portar código** de la PoC (copiar y adaptar). Al portar, no "mejores" en silencio: si cambias un comportamiento, dilo en el reporte de la fase y justifícalo.
- Las **pruebas de la PoC son la red de seguridad**: se portan todas (repartidas entre plataforma y bot) y deben pasar. Ninguna se borra sin explicarlo.

## REGLAS DE TRABAJO (obligatorias)

1. **No asumas nada.** Si algo es ambiguo o contradice la PoC, pregunta y espera.
2. **No amplíes el alcance.** Lo que creas que falta va como sugerencia al final de la fase.
3. **Simplicidad primero.** Sin MediatR, Quartz, AutoMapper, EF Core, repositorios genéricos ni contenedores DI extra.
4. **Regla de dos.** No inventes abstracciones que solo tengan un consumidor imaginario. Lo que la plataforma ofrece para bots que actúan (decidir, actuar, verificar, `job_items`) se diseña ahora, pero se valida con un **bot de prueba** dentro de los tests; su API queda marcada como **provisional** hasta que el bot de créditos (segundo consumidor real) la confirme.
5. **Trabaja por fases y confirma.** Al cerrar cada fase: build + tests, reporte breve (qué se hizo, qué se portó, qué cambió respecto a la PoC, qué queda pendiente), commit `fase N: …`. **Nunca push** sin pedirlo.
6. **Nunca secretos reales** en archivos versionados.
7. **Nunca desactives TLS/certificados** como atajo (excepción documentada: `TrustServerCertificate=True` solo en el SQL local).
8. **Acciones con efecto en un sistema destino:** `DryRun=true` por defecto; la plataforma garantiza que en DryRun nunca se llama a `ExecuteAsync`.
9. Si detectas un error o contradicción en este prompt, dilo antes de continuar.

## DECISIONES TÉCNICAS CERRADAS

Las mismas de la PoC (ver su blueprint), en particular:
- .NET 10 (`net10.0`), C#, Nullable, ImplicitUsings, `TreatWarningsAsErrors=true`, Central Package Management.
- Arquitectura hexagonal; Cronos en `BackgroundService`; Dapper + `Microsoft.Data.SqlClient` con SQL parametrizado; Polly v8 solo en operaciones de lectura o idempotentes; Flyway; Playwright headless con selectores centralizados; xUnit; tiempo siempre vía `TimeProvider`; `IOptions` + `ValidateOnStart`.
- Modos de ejecución `Continuous` y `RunOnce` (cron externo), como en la PoC.
- VisualDebug (simulación visible del mouse en local, videos en Docker), como en la PoC.
- Imágenes base fijadas por tag **y** digest; tag de la imagen Playwright alineado con el paquete `Microsoft.Playwright`; Chromium con sandbox y perfil seccomp; usuario no root; Dependabot.
- **Nuevo:** NetArchTest (o ArchUnitNET, elige uno y justifícalo) para verificar las reglas de dependencias en CI.
- **Empaquetado:** monorepo con `ProjectReference`. Sin paquetes NuGet propios todavía.

## ESTRUCTURA

```
Bots.Platform/
├── src/
│   ├── Bots.Platform.Core/         ← sin dependencias de infraestructura
│   │   ├── Runs/                   JobRun, JobRunStatus, JobRunResult (items_ok/items_failed), UtcGuard
│   │   ├── Items/                  JobItem, JobItemStatus
│   │   ├── Scheduling/             OccurrenceWindow, PendingOccurrenceCalculator, JobScheduler
│   │   ├── Engine/                 JobEngine, ItemPipeline, RunOnce, DryRunGuard
│   │   ├── Pipeline/               IWorkSource, IDecisionPolicy, ITargetSystem, IItemSink
│   │   ├── Ports/                  IJobRunStore, IJobRunLease, IJobItemStore, ITargetLock, ISchedule
│   │   └── Resilience/             ResilienceOptions, ResiliencePipelineFactory
│   ├── Bots.Platform.SqlServer/    SqlJobRunStore, SqlJobRunLease, SqlJobItemStore, SqlTargetLock,
│   │                               SqlAppLock, SqlConnectionFactory/Settings, SqlParameters, SqlResilience
│   ├── Bots.Platform.Playwright/   BrowserSession (ciclo de vida, login, apertura/cierre de páginas),
│   │                               VisualDebug, video, EvidenceRecorder (captura por acción y por fallo),
│   │                               PageDiagnostics (HTML + screenshot al fallar)
│   ├── Bots.Platform.Hosting/      AddBotsPlatform(jobName), CronWorker, RunOnceWorker, CronosSchedule,
│   │                               PlatformSettings + validadores
│   └── Bots.Jobs.SyncRepos/        ★ bot 1, ejecutable (Sdk Worker)
│       ├── Domain/                 RepoSnapshot, BranchSnapshot
│       ├── Application/            SyncReposPipeline, SyncReposOptions
│       ├── Adapters/Gitea/         GiteaRepoSource (solo parseo; el navegador es BrowserSession),
│       │                           GiteaSelectors, GiteaOptions, GiteaExceptions, GiteaResilience
│       ├── Adapters/Sql/           SqlRepoStore (implementa IItemSink<RepoSnapshot>)
│       ├── migrations/             migraciones propias del bot
│       ├── Program.cs              (~15 líneas: AddBotsPlatform + registro del pipeline)
│       └── appsettings.json, appsettings.Development.json
├── flyway/platform/                migraciones comunes
├── tests/
│   ├── Bots.Platform.Testing/      librería: SqlServerFixture, FakeSiteBase, fakes, FakeTimeProvider
│   ├── Bots.Platform.Tests/        garantías de la plataforma (incluye el bot de prueba con acciones)
│   ├── Bots.Architecture.Tests/
│   └── Bots.Jobs.SyncRepos.Tests/  fixtures HTML de Gitea, FakeGitea, parseo, SqlRepoStore, smoke real
├── docker/                         chromium-seccomp.json
├── Dockerfile                      ARG BOT → una imagen por bot
├── docker-compose.yml, docker-compose.cron.yml
├── docs/                           blueprint.md, runbook.md, ejecucion.md, nuevo-bot.md, prompts/
├── .github/                        workflows/ci.yml, dependabot.yml
└── Bots.Platform.sln, Directory.Build.props, Directory.Packages.props, global.json, .env.example
```

**Reglas de dependencias (verificadas por `Bots.Architecture.Tests`):**
- `Platform.Core` no referencia Playwright, SqlClient, Dapper, Cronos ni Hosting.
- `Platform.*` nunca referencia `Bots.Jobs.*`.
- Un `Bots.Jobs.X` nunca referencia a otro `Bots.Jobs.Y`.
- Dentro de un bot: `Domain` no depende de nada del bot; `Application` solo de `Domain` y `Platform.Core`; solo `Adapters` usa Playwright o SqlClient.

Si al implementar ves que un proyecto o carpeta de esta estructura sobra o falta, proponlo antes de cambiarlo.

## MODELO DE LA PLATAFORMA

```
Corrida (ocurrencia cron)                        ← JobScheduler + JobEngine (plataforma)
  ├─ lease por job (sp_getapplock 'job:<job_name>')
  ├─ lock por app destino (sp_getapplock 'target:<app>')   ← NUEVO
  └─ Listar ítems                                 ← IWorkSource          (bot)
       └─ por cada ítem (ItemPipeline, plataforma):
            1. Leer detalle                       ← IWorkSource          (bot)  reintentable
            2. Decidir                            ← IDecisionPolicy      (bot)  puro, opcional
            3. Leer estado en destino             ← ITargetSystem        (bot)  reintentable, opcional
            4. Actuar (si aplica y !DryRun)       ← ITargetSystem        (bot)  NO reintentable, opcional
            5. Verificar en destino               ← ITargetSystem        (bot)  reintentable, opcional
            6. Registrar                          ← IItemSink + job_items (bot + plataforma)
```

- Bots de solo lectura (SyncRepos) registran solo `IWorkSource` + `IItemSink`; la plataforma omite los pasos 2 a 5.
- **Garantías por corrida:** todas las del `blueprint.md` de la PoC, sin excepción (UNIQUE por ocurrencia, lease con applock de sesión en conexión dedicada, `TryStartAsync` con sus reglas y `start_token`, `CompleteAsync` con guarda, `AddErrorAsync` con `idempotency_key`, `Abandoned` y retoma con `attempt+1`, `MaxAttempts`, recuperación de la ocurrencia pendiente más reciente, anti-solapamiento, 0 ítems = `Failed` salvo `AllowEmptySource`, fallo de un ítem = `PartiallyFailed`, apagado ordenado = `Abandoned`), ahora genéricas por `job_name`.
- **Garantías por ítem (`job_items`):**
  - `UNIQUE (job_name, item_key, idempotency_scope)`; el `idempotency_scope` lo define cada bot.
  - Estados `Pending → Decided → Acted → Verified`, más `Skipped`, `Failed`, `NeedsReview`.
  - Cada transición con `UPDATE … WHERE status = @esperado` (mismo patrón que `CompleteAsync`); si no afecta una fila, se trata como conflicto, no como éxito.
  - Check-then-act: antes de actuar se lee el estado en el destino; si ya está aplicado → `Verified` sin actuar.
  - Una acción no idempotente nunca se reintenta sola. Resultado ambiguo o discrepancia con el destino → `NeedsReview`; no se vuelve a tocar hasta resolución manual.
  - Al retomar: un ítem en `Acted` sin `Verified` se resuelve leyendo el estado en el destino (`Verified` o `NeedsReview`), nunca volviendo a actuar.
  - Cada decisión guarda entradas, regla aplicada, `rule_version`, acción, timestamps, `job_run_id`, `attempt`.
  - Para bots de solo lectura, `job_items` registra `Verified`/`Failed` por ítem (trazabilidad de qué se procesó en cada corrida). Si crees que no aporta para SyncRepos, propónlo antes de omitirlo.
- **DryRun en la plataforma:** con `DryRun=true` el motor nunca llama a `ExecuteAsync`; el ítem queda `Decided` con la decisión registrada.
- **Lock por app destino:** recurso `target:<app>` configurable por bot; dos bots con la misma app/credencial no corren a la vez aunque sus horarios se crucen. El segundo espera o se salta (decide y justifica; regístralo en el log).
- **Evidencia:** `BrowserSession` captura pantalla en cada acción y en cada fallo, en carpeta configurable no versionada.

## BASE DE DATOS (diseño limpio: la BD es nueva)

Como el repo y la BD son nuevos, **no arrastres las limitaciones de las migraciones de la PoC** (eran inmutables solo para esa BD):
- `flyway/platform/`: esquema `bots` con `job_runs` (contadores genéricos `items_ok`, `items_failed`, `items_skipped`, `items_needs_review`; sin `repos_*`), `job_run_errors` (con `item_key`, no `repo_key`), `job_items`, `start_token`, `idempotency_key`. Historial Flyway propio (`-table=flyway_history_platform`). Grants a `bots_app_role`.
- `src/Bots.Jobs.SyncRepos/migrations/`: esquema `syncrepos` con `repos` y `branches` (mismas columnas, collations y restricciones que la PoC). Historial propio (`-table=flyway_history_syncrepos`).
- `sql-init` igual que la PoC (BD, rol de app, usuario de Flyway), parametrizado de forma segura.
- Desde que una migración se aplica en cualquier entorno compartido, es inmutable.

## DOCKER, CI, DESPLIEGUE

- **Dockerfile** multi-stage como el de la PoC, con `ARG BOT`: publica `src/Bots.Jobs.${BOT}` con un `AssemblyName` fijo (ej. `bot`) para que `ENTRYPOINT` no dependa de la variable. Una imagen por bot: `bots-syncrepos`.
- **Compose:** `sql`, `sql-init`, `flyway-platform`, `flyway-syncrepos` (en ese orden), `bot-syncrepos`; variante `docker-compose.cron.yml` con supercronic + RunOnce como en la PoC.
- **CI (GitHub Actions, PoC):** `build-test` (warnings como errores, trx como artefacto, Chromium con sandbox), `arch-test`, `flyway-validate` (plataforma + cada bot), `docker-build` con matriz por bot. Permisos mínimos, actions fijadas por SHA.
- **Azure DevOps (solo archivos, no desplegar):** `azure-pipelines.yml` que construye y publica una imagen por bot, y un manifiesto `CronJob` de Kubernetes por bot. Sin secretos; valores como `<PENDIENTE>`.

## FASES

0. **Lectura y plan (sin código).** Lee la PoC completa. Entrega:
   - inventario: cada archivo de `src/` y `tests/` de la PoC → destino en el repo nuevo (plataforma, bot, o se elimina y por qué);
   - lista de garantías del blueprint de la PoC, cada una con la prueba que la cubre hoy y dónde vivirá;
   - firmas propuestas de `IWorkSource`, `IDecisionPolicy`, `ITargetSystem`, `IItemSink`, `IJobItemStore`, `ITargetLock` y `AddBotsPlatform`;
   - contradicciones o dudas.
   Espera mi aprobación.
1. **Esqueleto:** solución, proyectos, props, `global.json`, `.gitignore`, `.editorconfig`, `Bots.Architecture.Tests` con las reglas → build limpio y tests de arquitectura en verde.
2. **Platform.Core:** runs, scheduling, engine, RunOnce, puertos, resiliencia, portando las pruebas de la PoC (`PendingOccurrenceCalculatorTests`, scheduler, job, RunOnce, invariantes) a `Bots.Platform.Tests` contra fakes.
3. **BD + Platform.SqlServer:** Docker (`sql`, `sql-init`, `flyway-platform`), migraciones de plataforma, `SqlJobRunStore`, lease, applock, resiliencia SQL, portando sus pruebas con Testcontainers.
4. **Platform.Playwright + Hosting:** `BrowserSession` (extraído de `PlaywrightRepoSource`), VisualDebug, video, evidencia, diagnóstico; `CronWorker`, `RunOnceWorker`, `AddBotsPlatform`, validación de settings.
5. **Bot SyncRepos:** dominio, `GiteaRepoSource`, `SqlRepoStore`, migraciones `syncrepos`, `Program.cs`, compose; portar fixtures y pruebas de Gitea. **Regresión:** mismas salidas que la PoC contra el mismo Gitea (compara `repos`/`branches` de ambas BD y reporta diferencias).
6. **Pipeline de ítems:** `job_items`, `SqlJobItemStore`, transiciones con guarda, check-then-act, `NeedsReview`, DryRun, `SqlTargetLock`. Valídalo con un **bot de prueba con acciones** en `Bots.Platform.Tests` contra un sitio falso (`FakeSiteBase`), cubriendo: doble corrida, pod muerto tras `Acted`, acción ambigua, discrepancia, DryRun, dos bots con el mismo `target`.
7. **Despliegue y docs:** CI, `azure-pipelines.yml`, CronJob por bot; `README` (arranque en 3 comandos), `blueprint.md` (Mermaid de la plataforma y del bot, garantías, decisiones, límites conocidos, ruta a Azure), `runbook.md` (incluye `NeedsReview` y el lock por app), `ejecucion.md`, y `docs/nuevo-bot.md` (cómo crear `Bots.Jobs.<Nombre>`, paso a paso).
8. **Plantilla v2:** actualiza `docs/prompts/plantilla-bot-rpa.md` (copiada de la PoC) para que la PARTE A describa la plataforma: un bot nuevo = proyecto `Bots.Jobs.<Nombre>` que implementa los puertos del pipeline, sin reimplementar garantías. Mantén las PARTES B y C.

## CRITERIOS DE ACEPTACIÓN

- Todas las pruebas portadas de la PoC pasan; ninguna se eliminó sin justificación.
- `docker compose up --build` desde cero deja SQL + migraciones + `bot-syncrepos` corriendo, y sincroniza lo mismo que la PoC.
- Matar el contenedor a mitad de corrida: `Abandoned`, se retoma la misma ocurrencia con `attempt=2`.
- En el bot de prueba: dos corridas no repiten acciones; un ítem `Acted` sin `Verified` se resuelve sin volver a actuar; un resultado ambiguo queda en `NeedsReview` y no se reintenta; en DryRun nunca se llama a `ExecuteAsync`; dos bots con el mismo `target` no corren a la vez.
- `Bots.Architecture.Tests` falla si un bot referencia a otro o si la plataforma referencia a un bot.
- `docker build --build-arg BOT=SyncRepos .` genera la imagen del bot.
- `SyncRepos` no contiene código de corridas, lease, scheduler, RunOnce, DryRun ni manejo genérico del navegador.
- Build sin warnings; sin secretos ni `.env` en git; la carpeta de la PoC sin cambios (`git status` limpio en ella).

## PROPUESTAS 2 Y 3

Se añadirán en prompts posteriores. No las anticipes ni dejes código especulativo para ellas.

## ANTES DE EMPEZAR

Empieza por la **Fase 0**. No escribas código hasta que apruebe el plan.
