# Runbook: si el bot falla

Corto y operativo. El diseño está en [blueprint.md](blueprint.md).

## 1. Qué revisar

**Logs del contenedor** (JSON a stdout; cada línea de una corrida lleva `JobRunId`, `Attempt` y, por
repo, `RepoKey` en el scope):

```bash
docker compose logs --since 2h bot
```

```bash
docker compose ps -a
```

Mensajes clave: `Corrida Succeeded|PartiallyFailed|Failed|Abandoned`, `Fallo fatal`, `agotó sus N
intentos`, `circuit breaker ABIERTO`, `Login rechazado`, `No se encontró el …`, `Gitea redirigió a …`.

**SQL** (conectado a `BotsDb`; `bots_app` puede leer `bots.*`):

```sql
-- Últimas corridas
SELECT TOP 20 id, scheduled_for_utc, attempt, status, repos_ok, repos_failed,
       started_at_utc, finished_at_utc, error_summary
FROM bots.job_runs WHERE job_name = 'sync-repos' ORDER BY scheduled_for_utc DESC;

-- Errores de una corrida (todos sus intentos)
SELECT attempt, repo_key, error_type, message, occurred_at_utc
FROM bots.job_run_errors WHERE job_run_id = @id ORDER BY id;

-- Corridas Running (¿huérfanas?)
SELECT id, scheduled_for_utc, attempt, started_at_utc
FROM bots.job_runs WHERE status = 'Running';

-- ¿Cuándo se actualizó cada repo por última vez?
SELECT external_key, last_job_run_id, captured_at_utc, updated_at_utc FROM bots.repos ORDER BY updated_at_utc;
```

**SQL Server y Gitea:**

```bash
docker compose ps sql
```

```bash
curl -s http://localhost:3000/api/healthz
```

**Circuit breaker:** `circuit breaker ABIERTO` en el log significa que SQL o Gitea fallaron de forma
transitoria muchas veces seguidas; las operaciones fallan sin intentarse durante `BreakDuration`
(30 s SQL, 60 s Gitea). Se cierra solo cuando el servicio responde. Revisar el servicio afectado.

## 2. Árbol por síntoma

| Síntoma | Qué mirar | Acción |
|---|---|---|
| **No corrió** (no hay fila para la hora esperada) | `docker compose ps -a bot`; log al arrancar (`Hosting failed to start` = configuración inválida, el mensaje dice qué variable); `siguiente ocurrencia` en el log | Corregir `.env` y `docker compose up -d bot`. Al arrancar recupera la ocurrencia pendiente más reciente. Si otra instancia tenía el lock, el log lo dice. |
| **Corrió parcial** (`PartiallyFailed`) | `job_run_errors` de la corrida: `repo_key` y `error_type` | Cuenta como completada; los repos fallidos se reprocesan en la siguiente. Si un repo falla siempre, ver "selector cambió". |
| **`Failed` por 0 repos** (`EmptySource`) | ¿El usuario tiene repos? ¿Cambió la página de repos? | Si el scraping se rompió: "selector cambió". Si el usuario realmente no tiene repos y es correcto: `BOTS_ALLOW_EMPTY_SOURCE=true`. |
| **`Abandoned`** | `error_summary`: "Cancelada por apagado ordenado" (`docker stop`) o "seguía Running sin lock" (el proceso murió) | Se retoma sola con `attempt + 1` al arrancar o en el siguiente despertar. Si fue un crash, revisar el log anterior (`docker compose logs --since …`). |
| **Intentos agotados** (log Error `agotó sus N intentos`) | `job_runs` (status `Failed`/`Abandoned`, `attempt = MaxAttempts`) y sus `job_run_errors` por intento | Corregir la causa. La siguiente ocurrencia sincroniza todo; ver "relanzar" si hace falta esa ocurrencia concreta. |
| **Login falló** (`GiteaLoginException`) | Mensaje de Gitea en el log; si menciona 2FA, el usuario tiene segundo factor | Verificar `GITEA_USER`/`GITEA_PASSWORD` entrando a mano en Gitea; actualizar `.env` y `docker compose up -d bot`. No se reintenta solo. |
| **Selector cambió** (`GiteaPageStructureException`: "No se encontró el …", o `GiteaInconsistentDataException`) | Versión de Gitea; fijar `Gitea__DiagnosticsDirectory` (p. ej. un volumen) para guardar el HTML de la página que falló | Actualizar solo `src/Bots.Infrastructure/Gitea/GiteaSelectors.cs` y los fixtures de `tests/Bots.Infrastructure.Tests/Fixtures/Gitea`; correr los tests y la prueba contra el Gitea real (README). |
| **Redirección a otro origen** (`Gitea redirigió a …`) | `ROOT_URL` de Gitea y `GITEA_BASE_URL` | Alinear `ROOT_URL` de Gitea con la URL que usa el bot, o poner Gitea en la red del compose. |

## 3. Relanzar una corrida de forma segura

Relanzar **siempre es seguro**: el UNIQUE impide duplicar la ocurrencia, el lock impide correr dos veces a
la vez y los upserts son idempotentes.

1. **Caso normal:** reiniciar el bot. Al arrancar marca huérfanas y ejecuta la ocurrencia pendiente más
   reciente (la siguiente a la última completada).

   ```bash
   docker compose restart bot
   ```

2. **Ocurrencia con intentos agotados:** basta con esperar a la siguiente ocurrencia (sincroniza el estado
   completo). Si hace falta esa ocurrencia concreta y es la pendiente más reciente, se puede habilitar
   **un** intento más; el detalle de los intentos anteriores queda en `job_run_errors`:

   ```sql
   UPDATE bots.job_runs SET attempt = attempt - 1
   WHERE id = @id AND status IN ('Failed', 'Abandoned') AND attempt > 1;
   ```

   Después: `docker compose restart bot`.

3. **No** borrar filas de `job_runs` ni cambiar su `status` a mano: rompe la auditoría y puede provocar que
   se recupere una ocurrencia ya procesada.

## 4. A quién avisar

| Situación | Responsable | Contacto |
|---|---|---|
| Bot caído o `Failed` repetido | `<PENDIENTE: definir responsable>` | `<PENDIENTE: definir responsable>` |
| Credenciales de Gitea / cambio de la página | `<PENDIENTE: definir responsable>` | `<PENDIENTE: definir responsable>` |
| SQL Server / migraciones | `<PENDIENTE: definir responsable>` | `<PENDIENTE: definir responsable>` |
