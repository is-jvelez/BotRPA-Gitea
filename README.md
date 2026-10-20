# Bots: sincronización de repos de Gitea (PoC)

Bot RPA en .NET 10 que reemplaza un flujo de Power Automate. Según un calendario cron, inicia sesión en
**Gitea** con Playwright (navegando la UI web, sin usar la API) y lee los repositorios **del usuario
logueado**: nombre, descripción, si tiene `README.md`, número de PRs (abiertos + cerrados) y, **por cada
rama, su número de commits**. Guarda todo en **SQL Server** con Dapper, de forma idempotente y auditable.

- La omisión es lo peor y el duplicado también es grave: cada ocurrencia programada se registra una sola
  vez (`UNIQUE`), se reintenta si falla, se recupera si el bot estuvo caído y nunca corre en paralelo
  consigo misma. Detalle en [docs/blueprint.md](docs/blueprint.md).
- Si algo falla: [docs/runbook.md](docs/runbook.md).

## Requisitos

- **Docker Desktop** (o Docker Engine con Compose v2).
- **Gitea** accesible en `http://localhost:3000` con el usuario cuyos repos se procesan (sin 2FA).
- Para desarrollar y correr los tests: **.NET 10 SDK**.

## Arranque en 3 comandos

```bash
cp .env.example .env
```

Completa `.env`: reemplaza cada `CAMBIAR_ESTO` (contraseñas de SQL, credenciales de Gitea y el cron, por
ejemplo `"*/5 * * * *"`). Fija también `MSSQL_TAG` en un tag concreto (probado: `2022-CU27-ubuntu-22.04`).

```bash
docker compose up --build
```

Compose levanta, en orden: `sql` (SQL Server) → `sql-init` (crea `BotsDb`, el rol y los usuarios) →
`flyway` (aplica las migraciones) → `bot`. El `.env` no se versiona (está en `.gitignore`).

Para ver qué hace el bot:

```bash
docker compose logs -f bot
```

## Tests

```bash
dotnet build Bots.sln -c Release
```

La primera vez, instala el navegador que usan los tests de Playwright (la versión va ligada al paquete
`Microsoft.Playwright`):

```bash
pwsh tests/Bots.Infrastructure.Tests/bin/Release/net10.0/playwright.ps1 install chromium
```

Sin PowerShell 7 se puede usar el driver que trae el paquete:
`tests/Bots.Infrastructure.Tests/bin/Release/net10.0/.playwright/node/<plataforma>/node tests/Bots.Infrastructure.Tests/bin/Release/net10.0/.playwright/package/cli.js install chromium`.

```bash
dotnet test --solution Bots.sln -c Release --no-build
```

- `Bots.Application.Tests`: lógica del job, del cálculo de ocurrencias y del programador, con fakes y
  `FakeTimeProvider`.
- `Bots.Infrastructure.Tests`: SQL contra un SQL Server de **Testcontainers** (requiere Docker en marcha)
  con las migraciones reales; Playwright contra un **Gitea falso** con fixtures HTML; Cronos y Polly.
- `dotnet test` usa Microsoft.Testing.Platform (activado en `global.json`).
- Prueba opcional contra un Gitea real (se omite si no se definen las variables):

  ```bash
  GITEA_E2E_BASE_URL=http://localhost:3000 GITEA_E2E_USER=<usuario> GITEA_E2E_PASSWORD=<contraseña> dotnet test --solution Bots.sln -c Release --no-build
  ```

## CI (GitHub Actions)

[.github/workflows/ci.yml](.github/workflows/ci.yml) corre en `push` y `pull_request` sobre `main`:
build + tests (con `.trx` como artefacto), validación de Flyway con el mismo `sql-init` del compose sobre
una BD vacía y `docker build` de la imagen (sin push). Solo se ejecuta en GitHub: hay que crear un repo
vacío allí y subir el proyecto.

```bash
git remote add origin <url-del-repo-en-github>
```

```bash
git push -u origin main
```

## Estructura

```
src/
  Bots.Domain/          Entidades e invariantes (sin dependencias)
  Bots.Application/     SyncReposJob, PendingOccurrenceCalculator, SyncReposScheduler y puertos
  Bots.Infrastructure/  Adaptadores: SQL (Dapper, applock), Gitea (Playwright), Polly, Cronos
  Bots.Worker/          Composition root: Program.cs, configuración y CronWorker
tests/
  Bots.Application.Tests/
  Bots.Infrastructure.Tests/   (incluye Fixtures/Gitea con el HTML de prueba)
flyway/sql/             Migraciones V1 (esquema y datos) y V2 (control de corridas)
docs/                   blueprint.md (diseño) y runbook.md (operación)
Dockerfile              Targets sqlserver, build y runtime (imagen del bot)
docker-compose.yml      sql, sql-init, flyway y bot
.env.example            Plantilla de variables (sin secretos)
```

## Correr el bot fuera de Docker

Útil para depurar. Necesita SQL Server con las migraciones aplicadas (por ejemplo, levantando solo
`docker compose up -d sql sql-init flyway`) y Chromium de Playwright instalado (ver Tests).

Las contraseñas van en **user-secrets** (fuera del repo), nunca en `appsettings.json`:

```bash
dotnet user-secrets --project src/Bots.Worker set "Sql:User" "bots_app"
```

```bash
dotnet user-secrets --project src/Bots.Worker set "Sql:Password" "<APP_DB_PASSWORD>"
```

```bash
dotnet user-secrets --project src/Bots.Worker set "Gitea:User" "<usuario>"
```

```bash
dotnet user-secrets --project src/Bots.Worker set "Gitea:Password" "<contraseña>"
```

```bash
dotnet user-secrets --project src/Bots.Worker set "Sql:TrustServerCertificate" "true"
```

`TrustServerCertificate=true` es solo para el SQL Server local de la PoC (certificado autofirmado del
contenedor). Fuera de Docker, Gitea es `http://localhost:3000` y SQL `localhost,1433` (valores por defecto
de `appsettings.json`). Los user-secrets solo se cargan en el entorno `Development`:

```bash
DOTNET_ENVIRONMENT=Development dotnet run --project src/Bots.Worker
```

## Configuración

| Variable de `.env` | Clave .NET | Para qué |
|---|---|---|
| `APP_DB_USER` / `APP_DB_PASSWORD` | `Sql:User` / `Sql:Password` | Usuario de la app (sin DDL) |
| `GITEA_BASE_URL` | `Gitea:BaseUrl` | En Docker `http://host.docker.internal:3000` |
| `GITEA_USER` / `GITEA_PASSWORD` | `Gitea:User` / `Gitea:Password` | Usuario cuyos repos se procesan |
| `BOTS_SCHEDULE_CRON` | `Bots:Schedule:Cron` | Cron de 5 campos |
| `BOTS_SCHEDULE_TIMEZONE` | `Bots:Schedule:TimeZone` | Zona IANA del cron (`America/Guayaquil`) |
| `BOTS_ALLOW_EMPTY_SOURCE` | `Bots:AllowEmptySource` | `false`: 0 repos = corrida `Failed` |
| `BOTS_RECOVERY_MAX_ATTEMPTS` | `Bots:Recovery:MaxAttempts` | Intentos por ocurrencia (default 3) |

Los parámetros de Polly (`Bots:Resilience:Sql` y `Bots:Resilience:Gitea`), los timeouts de Gitea y el
umbral de corridas huérfanas tienen valores por defecto en `appsettings.json`. La configuración se valida
al arrancar: si falta algo o el cron no es válido, el bot no arranca y el log dice qué corregir.


## Probar CI

Esto es una prueba de como funciona Github Actions con CI