# Plantilla: Bot RPA (.NET 10) — Plataforma fija + Ficha del bot

> Uso: copia desde "PARTE A" hasta el final, **escribe solo la PARTE C (Descripción del proceso)** y pégalo en Claude Code.
> - **Parte A** (plataforma): no se edita nunca.
> - **Parte B** (formato de la ficha): no se edita; Claude la llena por ti en la Fase 0 a partir de la Parte C.
> - **Parte C** (descripción del proceso): lo único que escribes, en texto libre y con todo el detalle posible.
> Lo que la descripción no responda, Claude lo marca como `<PENDIENTE>` y **pregunta**; nunca lo supone.

---

# PARTE A — CONTRATO DE PLATAFORMA (no cambia entre bots)

## ROL

Eres un arquitecto/desarrollador senior .NET con experiencia en Worker Services, Cronos, Flyway, Dapper, Polly, Playwright y Docker. Vas a construir un bot que reemplaza un proceso manual de un operador (o un flujo de Power Automate), descrito en la PARTE B.

## REGLAS DE TRABAJO (obligatorias)

1. **No asumas nada.** Antes de escribir código, genera la Ficha (Fase 0) a partir de la PARTE C: si un campo no se puede deducir con certeza de la descripción, o es ambiguo (límites `>` vs `>=`, qué pasa cuando ninguna regla aplica, redondeo, qué hace un botón), **pregunta y espera**.
2. **No amplíes el alcance.** Implementa solo lo que pide la Ficha. Lo que creas que falta va como sugerencia al final.
3. **Simplicidad primero.** Sin MediatR, AutoMapper, EF Core, repositorios genéricos ni contenedores DI extra.
4. **Trabaja por fases y confirma.** Al cerrar cada fase: build + tests, reporte, commit.
5. **Nunca pongas secretos reales** en archivos versionados.
6. Si detectas un error o contradicción en este prompt, dilo antes de continuar.
7. **Nunca desactives TLS/certificados** como atajo (excepción: `TrustServerCertificate=True` solo en el SQL local de la PoC, documentado).
8. **Acciones con efecto en el sistema destino** (clics que cambian estado: aprobar, validar, enviar, guardar):
   - solo contra el entorno de pruebas declarado en la Ficha;
   - el bot arranca en **`Bots:DryRun=true` por defecto** (evalúa y registra, pero no ejecuta acciones); pasar a `false` es una decisión explícita de configuración;
   - nunca las ejecutes en un sistema real durante el desarrollo sin mi confirmación.
9. **Datos personales:** fixtures y tests solo con datos ficticios; en logs, enmascara identificadores (cédula, cuenta, nombre) según la Ficha.

## DECISIONES TÉCNICAS CERRADAS

- .NET 10 (`net10.0`), C#, Nullable e ImplicitUsings habilitados, `TreatWarningsAsErrors=true`.
- Arquitectura hexagonal (puertos y adaptadores).
- Scheduler: **Cronos** dentro de un `BackgroundService` (sin Quartz.NET).
- Datos: **Dapper** + `Microsoft.Data.SqlClient`; SQL siempre parametrizado.
- Resiliencia: **Polly v8** (reintento exponencial + jitter y circuit breaker) **solo para operaciones de lectura o idempotentes**.
- Migraciones: **Flyway** (contenedor oficial) contra SQL Server dockerizado.
- RPA: **Playwright** headless; selectores centralizados en una sola clase por pantalla; timeouts explícitos; `await using` en páginas/contextos.
- Pruebas: xUnit; tiempo siempre vía `TimeProvider`.
- CI: GitHub Actions (PoC).

## MODELO GENÉRICO DEL BOT

Todo bot sigue el mismo pipeline por ítem; la Ficha solo define el contenido de cada paso:

```
Corrida (ocurrencia cron)
  └─ Listar ítems (fuente)                     ← IWorkSource
       └─ por cada ítem:
            1. Leer detalle                     ← IWorkSource      (lectura, reintentable)
            2. Decidir (reglas puras)           ← IDecisionPolicy  (sin I/O, 100 % testeable)
            3. Leer estado actual en destino    ← ITargetSystem    (lectura, reintentable)
            4. Actuar (si aplica y !DryRun)     ← ITargetSystem    (NO reintentable)
            5. Verificar resultado en destino   ← ITargetSystem    (lectura, reintentable)
            6. Registrar decisión + evidencia   ← IResultStore
```

Bots de solo lectura (ej. sincronizar datos) omiten los pasos 3–5.

### Puertos (Application)

- `IWorkSource<TItem>`: lista paginada de claves de ítem + detalle por ítem; un fallo en un ítem no aborta los demás.
- `IDecisionPolicy<TItem, TDecision>`: función pura `Decide(TItem) → TDecision`. Incluye la versión de reglas aplicada (`RuleVersion`).
- `ITargetSystem<TItem, TDecision>` (solo si hay acciones): `ReadStateAsync`, `ExecuteAsync`, `VerifyAsync`.
- `IResultStore`: guarda, en una transacción por ítem, la decisión, sus entradas, el resultado de la acción y la versión de reglas.
- `IJobRunStore` y `ISchedule`: iguales para todos los bots (ver "Garantías").

### Garantías (iguales para todos los bots)

**Por corrida** (tabla `bots.job_runs`, `UNIQUE (job_name, scheduled_for_utc)`):
- Lock entre instancias con `sp_getapplock` (`@LockOwner='Session'`, timeout 0) en conexión dedicada mantenida por un lease `IAsyncDisposable`.
- `TryStartAsync`: sin lock → `null`; fila nueva → `Running`, `attempt=1`; `Failed`/`Abandoned` → reintento con `attempt+1` hasta `Bots:Recovery:MaxAttempts`; `Running` con lock obtenido → el dueño murió: `Abandoned` y reintento; `Succeeded`/`PartiallyFailed` → `null`.
- Estados: `Running`, `Succeeded`, `PartiallyFailed`, `Failed`, `Abandoned`. `PartiallyFailed` cuenta como completada.
- 0 ítems en la fuente = `Failed` (posible scraping roto), salvo `Bots:AllowEmptySource=true`.
- Recuperación al arrancar y en cada despertar: se ejecuta solo la ocurrencia pendiente más reciente; las demás se registran (Warning).
- Anti-solapamiento: si una corrida sigue activa, la siguiente ocurrencia se salta y se registra.

**Por ítem** (tabla `bots.job_items`, solo si el bot actúa sobre el destino):
- Clave de idempotencia de negocio: `UNIQUE (job_name, item_key, idempotency_scope)` donde `idempotency_scope` lo define la Ficha (ej. "una vez por cliente", "una vez por cliente y día").
- Estados: `Pending → Decided → Acted → Verified`, más `Skipped` (no aplica ninguna regla / ya estaba hecho), `Failed` y **`NeedsReview`**.
- **Check-then-act:** antes de actuar se lee el estado en el destino; si ya está aplicado, se marca `Verified` sin volver a hacer clic.
- **Una acción no idempotente nunca se reintenta automáticamente.** Si falla de forma ambigua (timeout después del clic, no se sabe si se aplicó) → `NeedsReview` y alerta en el log; no se repite en corridas siguientes hasta resolución manual.
- **Discrepancia entre la decisión del bot y lo que muestra el destino** (ej. el bot espera "Aprobado" y la app muestra otra cosa) → `NeedsReview`, sin actuar.
- Cada decisión guarda: entradas usadas, regla que aplicó, `rule_version`, resultado de cálculo, acción ejecutada, timestamps, `job_run_id`, `attempt`.

### Reglas de negocio

- Viven en **Domain/Application como código puro**, con una tabla de decisión ordenada (la primera regla que aplica gana).
- Umbrales y tasas en configuración validada al arranque (`IOptions` + `ValidateOnStart`), con `RuleVersion` explícita; cambiar un umbral = nueva versión registrada.
- Dinero siempre en `decimal`; redondeo explícito (`MidpointRounding` definido en la Ficha).
- Cobertura de tests obligatoria: cada fila de la tabla de decisión, cada límite (valor exacto, +0.01, −0.01), datos faltantes y "ninguna regla aplica".

### Evidencia y logs

- Logging estructurado a stdout con `JobRunId`, `Attempt`, `ItemKey` (enmascarado si es PII) en el scope.
- Captura de pantalla de Playwright en cada acción y en cada fallo, en una carpeta configurable **no versionada**, con retención definida en la Ficha.

## ESTRUCTURA

```
bots/
├─ src/
│  ├─ Bots.Domain/          (entidades del bot, reglas puras; sin dependencias)
│  ├─ Bots.Application/     (puertos, job genérico, PendingOccurrenceCalculator)
│  ├─ Bots.Infrastructure/  (Dapper, Playwright, Polly, Cronos)
│  └─ Bots.Worker/          (CronWorker + Program.cs; único composition root)
├─ tests/ (Bots.Application.Tests, Bots.Infrastructure.Tests)
├─ flyway/sql/              (V1 esquema + control genérico; V2+ tablas del bot)
├─ docs/ (runbook.md, blueprint.md)
├─ .github/workflows/ci.yml
├─ docker-compose.yml       (sql + sql-init + flyway + [app de práctica] + bot)
├─ Dockerfile               (targets sqlserver, build, runtime Playwright)
├─ .env.example, .gitignore, README.md, Bots.sln
└─ Directory.Build.props, Directory.Packages.props
```

Dependencias: Domain → nada; Application → Domain + Logging.Abstractions (sin Cronos); Infrastructure → Application; Worker → Application + Infrastructure. Sin proyecto `Worker.Tests`: la lógica no trivial vive en Application o Infrastructure.

## FLYWAY, DOCKER, CI, GIT, DOCUMENTACIÓN

Igual que el estándar del equipo:
- **Flyway:** `V<n>__<desc>.sql`; migraciones inmutables; `BotsDb`, rol de app y usuario de Flyway (`db_owner`) los crea `sql-init`; historial en `dbo`; `V1` crea el esquema `bots` y hace `GRANT ... ON SCHEMA::bots TO bots_app_role` (falla claro si el rol no existe).
- **Docker:** Dockerfile multi-stage (`sqlserver`, `build`, `runtime` sobre imagen Playwright .NET alineada al paquete; usuario no root; verificar `dotnet --list-runtimes` y si no trae .NET 10 publicar self-contained). Compose con `sql` (healthcheck, puerto solo en 127.0.0.1), `sql-init`, `flyway`, `bot` (`restart: unless-stopped`, `stop_grace_period`). Si el destino corre en el host: `extra_hosts: host.docker.internal:host-gateway` y verificar redirecciones a `ROOT_URL` tras el login.
- **CI:** `build-test` (warnings como errores, trx como artefacto), `flyway-validate` (mismo `sql-init` + `migrate` + `validate`), `docker-build` del target `runtime`. Tests SQL con Testcontainers; tests de Playwright contra **fixtures HTML locales**. `permissions: contents: read`.
- **Git:** rama `main`; `.gitignore` excluye `.env`, `bin/`, `obj/`, `TestResults/`, evidencias y volúmenes; un commit por fase (`fase N: ...`); **nunca push** sin pedirlo.
- **Docs:** README (arranque en 3 comandos), `blueprint.md` (Mermaid hexagonal, decisiones, garantías, límites, ruta a Azure), `runbook.md` (qué revisar, árbol por síntoma —incluye `NeedsReview`—, cómo relanzar, a quién avisar con `<PENDIENTE: definir responsable>`).

## FASES

0. **Ficha del bot (sin código).** Lee la PARTE C y genera `docs/ficha-bot.md` con el formato exacto de la PARTE B:
   - cada campo se llena **solo** con lo que la descripción dice; lo que se deduce se marca `(deducido: …)` para que lo confirme;
   - lo que no se puede saber queda como `<PENDIENTE: pregunta concreta>`;
   - la tabla de decisión (B.5) se escribe con `>`/`>=` explícitos y una fila "ninguna aplica";
   - B.11 incluye los ejemplos de la descripción **más** los casos límite que tú derives de las reglas (valor exacto, ±0.01, dato faltante, ya procesado, app muestra otro resultado);
   - al final, lista numerada de preguntas abiertas.
   Espera mi respuesta, actualiza la ficha y no pases a la Fase 1 mientras queden `<PENDIENTE>` en B.3, B.5, B.6 o B.7. Desde aquí, **la ficha aprobada es la fuente de verdad** (no la Parte C).
1. Solución, proyectos, props, `.gitignore`, rama `main` → build limpio.
2. Domain (entidades + **tabla de decisión y cálculos**) + Application (puertos, job, calculator) + tests, incluidos **todos los casos de la sección B.11**.
3. Docker (sql, sql-init, flyway) + verificación del runtime de Playwright → migraciones aplicadas.
4. Infrastructure SQL (Dapper, lease, `job_items`, Polly, `CronosSchedule`) + tests con Testcontainers.
5. **Verificación contra el sistema destino real antes de implementar**: confirma cada selector, cada dato a leer y cómo se ve "ya aplicado". Si algo no es obtenible de forma fiable, repórtalo y propón alternativas; espera mi decisión. Luego adaptador Playwright + fixtures + tests.
6. Worker + servicio `bot` en compose + corrida real **en DryRun**, luego una corrida con acciones en el entorno de pruebas tras mi confirmación.
7. CI, README, blueprint, runbook.

## CRITERIOS DE ACEPTACIÓN GENÉRICOS

- `docker compose up --build` desde cero deja SQL + migraciones + bot corriendo.
- Ejecutar el bot dos veces sobre los mismos datos no duplica registros **ni repite acciones en el destino**.
- Matar el contenedor a mitad de corrida: la corrida pasa a `Abandoned`, se retoma la misma ocurrencia con `attempt=2`, y ningún ítem ya `Acted`/`Verified` se vuelve a accionar.
- Una acción con resultado ambiguo queda en `NeedsReview` y no se reintenta sola.
- 0 ítems → `Failed` con log Error claro.
- `docker stop` termina de forma ordenada; build sin warnings; tests en verde; sin secretos ni `.env` en git.

## ANTES DE EMPEZAR

Empieza por la **Fase 0**: genera la ficha a partir de la PARTE C, lista las preguntas abiertas y espera mis respuestas.

---

# PARTE B — FORMATO DE LA FICHA (no la llenas tú: Claude la genera en la Fase 0)

> Es el formato que Claude debe producir en `docs/ficha-bot.md`. Los `{{...}}` los rellena Claude con lo que diga la PARTE C.

### B.1 Identidad
- Nombre del job (`job_name`): `{{...}}`
- Objetivo de negocio: `{{...}}`
- Proceso manual / flujo de Power Automate que reemplaza: `{{...}}`
- Dueño de negocio de las reglas: `<PENDIENTE>`

### B.2 Sistema destino
- URL por entorno (práctica / pruebas / producción): `{{...}}`
- ¿Existe app de práctica? Si no, ¿se construye un mock en compose? `{{...}}`
- Autenticación (usuario/clave, SSO, MFA): `{{...}}` — variables de entorno: `{{...}}`
- ¿Existe API además de la UI? `{{sí/no}}`

### B.3 Fuente de ítems
- De dónde sale la lista (la propia web, Excel, tabla SQL, API): `{{...}}`
- Clave única del ítem (`item_key`): `{{...}}`
- Volumen esperado por corrida y paginación: `{{...}}`
- Alcance de idempotencia (`idempotency_scope`): `{{una vez por ítem | por ítem y día | ...}}`

### B.4 Datos a leer por ítem

| Campo | Origen (pantalla / columna) | Tipo | Obligatorio | Ejemplo | ¿PII? |
|---|---|---|---|---|---|
| `{{...}}` | | | | | |

Qué hacer si falta un dato obligatorio: `{{Failed | NeedsReview | Skipped}}`

### B.5 Reglas de decisión (orden importa: la primera que aplica gana)

| # | Condición (con `>`/`>=` explícito) | Resultado | Parámetros |
|---|---|---|---|
| 1 | `{{...}}` | `{{...}}` | |
| … | Ninguna aplica | `{{...}}` | |

`RuleVersion` inicial: `{{v1}}`

### B.6 Cálculos
- Fórmula exacta: `{{...}}`
- Base (monto, plazo, periodicidad): `{{...}}`
- Redondeo (decimales y `MidpointRounding`): `{{...}}`
- ¿El resultado se ingresa en la app o solo se registra? `{{...}}`
- Ejemplo resuelto a mano: `{{...}}`

### B.7 Acciones en el destino (paso a paso del operador)
1. `{{...}}`
- Cómo se verifica que la acción tuvo éxito: `{{...}}`
- Cómo se ve en la app un ítem **ya procesado** (para check-then-act): `{{...}}`
- ¿Qué hace el bot si la app muestra un resultado distinto al que el bot decidió? `{{NeedsReview recomendado}}`

### B.8 Salida y registro
- Tablas del bot (V2+): `{{...}}`
- Evidencia (capturas): `{{sí/no}}`, retención: `{{...}}`

### B.9 Programación
- Cron y zona horaria: `{{...}}`
- Ventana permitida (ej. solo horario laboral): `{{...}}`

### B.10 Criterio de riesgo específico
- Qué es peor en este bot, omitir o duplicar: `{{...}}`
- Acciones irreversibles: `{{...}}`

### B.11 Casos de prueba de negocio (obligatorio, incluye límites)

| Caso | Entradas | Resultado esperado | Cálculo esperado | Acción esperada |
|---|---|---|---|---|
| `{{...}}` | | | | |

### B.12 Fuera de alcance
- `{{...}}`

---

# PARTE C — DESCRIPCIÓN DEL PROCESO (lo único que escribes)

Escríbelo en texto libre, como se lo explicarías a un operador nuevo en su primer día, frente a la pantalla. No hace falta seguir un orden ni un formato. Cuanto más detalle, menos preguntas en la Fase 0.

Para que no falte nada, intenta cubrir estos puntos (es una guía para ti, no un formulario):

- **Qué y para qué:** qué se hace hoy, quién lo hace y qué problema resuelve el bot.
- **La aplicación:** URL de práctica o pruebas, cómo se inicia sesión y si existe una API.
- **La lista de trabajo:** de dónde sale (la propia web, un Excel, una tabla) y cómo se identifica cada ítem sin confundirlo con otro.
- **El recorrido, clic a clic:** cada pantalla, campo y botón en orden, con el **texto exacto** que se ve en la app.
- **Los datos que se miran:** dónde aparece cada uno, en qué formato y qué valores puede tener.
- **Las reglas:** con números exactos, y diciendo "mayor que" o "mayor o igual que". Indica también qué pasa cuando no se cumple ninguna.
- **Los cálculos:** la fórmula, sobre qué monto y plazo, el redondeo y **un ejemplo resuelto a mano**.
- **El resultado:** qué se hace con la decisión (clic, dato ingresado, solo registro) y cómo se sabe que salió bien.
- **Lo ya hecho:** cómo se ve en la app un ítem que ya se procesó y si se puede deshacer.
- **Cuando algo sale mal:** dato faltante, la app muestra algo inesperado, pantalla caída.
- **Cuándo corre:** frecuencia, horario permitido y zona horaria.
- **El riesgo:** qué es peor en este proceso, omitir un ítem o hacerlo dos veces.
- **Ejemplos reales:** al menos 3 casos con datos ficticios y su resultado esperado.
- **Lo que NO hace** el bot.

```
<<ESCRIBE AQUÍ LA DESCRIPCIÓN DEL PROCESO>>
```
