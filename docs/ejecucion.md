# Ejecución: local y en Docker, con y sin simulación

El bot se puede ejecutar de dos formas, y en las dos con o sin la simulación del mouse (VisualDebug: una
flecha que se mueve paso a paso hasta cada elemento que el bot lee o pulsa, azul al pulsar):

| | Sin simulación | Con simulación |
|---|---|---|
| **Local** (`dotnet run`, SQL en Docker) | Chromium headless, solo logs | Chromium en ventana, en vivo y frenado para seguirlo a ojo |
| **Docker** (todo en contenedores) | Chromium headless, solo logs (el modo normal) | Chromium headless + un video `.webm` por página en `./videos/` |
| Se elige con | `"VisualDebug": false` / `GITEA_VISUAL_DEBUG=false` | `"VisualDebug": true` / `GITEA_VISUAL_DEBUG=true` |

En Docker la simulación no se ve en vivo porque el contenedor no tiene pantalla: se graba. El bot detecta
solo si hay pantalla; la opción es la misma.

Los comandos son de **PowerShell** (Windows). En bash, `$env:VAR="valor"; comando` se escribe
`VAR=valor comando`.

## Cómo lee la configuración

| | Local (`dotnet run`) | Docker |
|---|---|---|
| Entorno | `DOTNET_ENVIRONMENT=Development` (se pasa en el comando) | `Production` (lo fija el compose) |
| Valores base | `appsettings.json` | `appsettings.json` |
| Valores propios | `appsettings.Development.json` (cron corto, VisualDebug, SQL) | `.env` (compose lo traduce a variables de entorno) |
| Contraseñas | user-secrets | `.env` |
| ¿Lee el `.env`? | **No** | Sí |
| ¿Lee `appsettings.Development.json` y user-secrets? | Sí | **No** |

Local, cada fuente pisa a la anterior:

1. [`appsettings.json`](../src/Bots.Worker/appsettings.json): base de todos los entornos, versionada (cron
   cada hora, `VisualDebug: false`). **No se toca para depurar.**
2. [`appsettings.Development.json`](../src/Bots.Worker/appsettings.Development.json): solo las claves que
   cambian en local. Aquí se elige con o sin simulación.
3. user-secrets: usuarios y contraseñas, fuera del repo.
4. Variables de entorno de la sesión, p. ej. `$env:Gitea__VisualDebug="true"` (`__` equivale a `:`).
5. Argumentos: `dotnet run --project src/Bots.Worker -- --Gitea:VisualDebug=true`.

Las fuentes 2 y 3 solo se cargan con `DOTNET_ENVIRONMENT=Development`. Esa variable no puede ir en ningún
`appsettings`: .NET decide el entorno antes de leerlos (y de él depende cuáles carga).

En Docker, compose traduce los nombres del `.env` a claves .NET (`GITEA_VISUAL_DEBUG` → `Gitea__VisualDebug`,
`BOTS_SCHEDULE_CRON` → `Bots__Schedule__Cron`…) y se las pasa al bot como variables de entorno, que pisan a
`appsettings.json`. Tabla completa en la sección Configuración del [README](../README.md#configuración).

## Requisitos

- Docker Desktop en marcha (SQL Server corre en Docker en los dos casos).
- Gitea en `http://localhost:3000` con el usuario cuyos repos se procesan (sin 2FA).
- `.env` creado desde `.env.example` y sin ningún `CAMBIAR_ESTO`: en Docker es toda la configuración, y en
  local compose lo usa para crear la base y el usuario de la app.
- Solo para local: .NET 10 SDK.

**Nunca los dos a la vez**: el bot local y el contenedor `bot` compartirían la base y el lock, y uno saltaría
las corridas del otro. Antes de uno, detén el otro (`Ctrl+C` en `dotnet run`, o `docker compose stop bot`).

**Cuándo corre**: al arrancar, el bot solo ejecuta algo si hay una ocurrencia pendiente desde la última
corrida completada (recuperación, log `ocurrencia pendiente ...`); si no la hay (o la base no tiene
historial), espera a la siguiente ocurrencia del cron (log `siguiente ocurrencia ...`). En local el cron es
cada 2 minutos (`appsettings.Development.json`); en Docker es el de `BOTS_SCHEDULE_CRON` del `.env`. Con un
cron corto es normal que corra enseguida al arrancar, porque casi siempre hay una ocurrencia pendiente.

Qué buscar en el log de una corrida: `Corrida iniciada (intento N): N repos a procesar.`, una línea
`Repo sincronizado ...` por repo y al final `Corrida Succeeded: N repos sincronizados.` (o
`PartiallyFailed` / `Failed`; ver el [runbook](runbook.md)).

---

## Local (`dotnet run`)

### Paso 1: levantar solo la base de datos

```powershell
docker compose up -d sql sql-init flyway
```

```powershell
docker compose stop bot
```

### Paso 2: compilar e instalar Chromium (solo la primera vez)

```powershell
dotnet build Bots.sln -c Release
```

```powershell
pwsh tests/Bots.Infrastructure.Tests/bin/Release/net10.0/playwright.ps1 install chromium
```

Sin PowerShell 7, ver la alternativa con `node` en la sección Tests del [README](../README.md#tests).

Este build en Release solo hace falta para tener `playwright.ps1`; `dotnet run` compila por su cuenta (en
Debug). Chromium se instala una vez por usuario (`%LOCALAPPDATA%\ms-playwright`) y lo usan tanto los tests
como el worker, porque comparten la versión de `Microsoft.Playwright`.

### Paso 3: user-secrets (solo la primera vez)

Solo usuarios y contraseñas; el resto ya está en `appsettings.Development.json`. Usa los mismos valores del
`.env` (`APP_DB_USER`, `APP_DB_PASSWORD`, `GITEA_USER`, `GITEA_PASSWORD`). Si una contraseña lleva `$`,
escríbela entre comillas simples.

```powershell
dotnet user-secrets --project src/Bots.Worker set "Sql:User" "bots_app"
```

```powershell
dotnet user-secrets --project src/Bots.Worker set "Sql:Password" "<APP_DB_PASSWORD>"
```

```powershell
dotnet user-secrets --project src/Bots.Worker set "Gitea:User" "<GITEA_USER>"
```

```powershell
dotnet user-secrets --project src/Bots.Worker set "Gitea:Password" "<GITEA_PASSWORD>"
```

Para revisar qué claves hay (muestra también los valores):

```powershell
dotnet user-secrets --project src/Bots.Worker list
```

Si tienes también `Sql:Server`, `Sql:TrustServerCertificate` o `Gitea:VisualDebug` en user-secrets (de una
versión anterior de esta guía), quítalos: ganan sobre `appsettings.Development.json` y harían que cambiar el
archivo no tenga efecto.

```powershell
dotnet user-secrets --project src/Bots.Worker remove "Gitea:VisualDebug"
```

### Paso 4: elegir el modo y ejecutar

Siempre el mismo comando; el modo se elige con `VisualDebug` en
[`appsettings.Development.json`](../src/Bots.Worker/appsettings.Development.json):

```powershell
$env:DOTNET_ENVIRONMENT="Development"; dotnet run --project src/Bots.Worker
```

`dotnet run` recompila y copia los `appsettings` editados: basta con guardar el archivo y volver a lanzar el
comando (`Ctrl+C` primero si ya estaba corriendo). `Ctrl+C` detiene el worker y cierra la corrida en curso
como `Abandoned`.

#### Local sin simulación

Es lo que viene en el repo:

```json
"Gitea": {
  "VisualDebug": false
},
```

No se abre ninguna ventana. Se sabe que funcionó porque el log muestra la corrida (`Corrida iniciada` →
`Corrida Succeeded`) y los datos quedan en `BotsDb`.

Variante: ver el navegador **sin** la simulación (ventana normal, a velocidad real, sin flecha ni pausas):

```json
"Gitea": {
  "VisualDebug": false,
  "Headless": false
},
```

#### Local con simulación

```json
"Gitea": {
  "VisualDebug": true
},
```

El bot abre Chromium en ventana (ignora `Headless`), dibuja la flecha y la mueve paso a paso hasta cada
elemento, con cada acción frenada para seguirla. Cada lectura abre su propia pestaña, así que verás pestañas
abrirse y cerrarse. La ventana se abre **cuando empieza la corrida**, no al arrancar.

Al terminar, vuelve a `"VisualDebug": false` antes de hacer commit (un `true` subido por error solo afecta a
quien ejecuta en local, pero el repo debe quedar con el modo por defecto).

#### Sin tocar archivos: variables de entorno

Para una prueba puntual, una variable de la sesión pisa a los archivos:

```powershell
$env:DOTNET_ENVIRONMENT="Development"; $env:Gitea__VisualDebug="true"; dotnet run --project src/Bots.Worker
```

O con argumentos, que no quedan en la sesión (van después del `--`):

```powershell
$env:DOTNET_ENVIRONMENT="Development"; dotnet run --project src/Bots.Worker -- --Gitea:VisualDebug=true
```

Cuidado: las variables `$env:` **se quedan en la ventana** hasta que la cierras, y mientras existan cambiar
`appsettings.Development.json` no tiene efecto. Para quitarlas, abre una ventana nueva o:

```powershell
Remove-Item Env:Gitea__VisualDebug, Env:Bots__Schedule__Cron -ErrorAction SilentlyContinue
```

---

## Docker

### Paso 1: detener el worker local

`Ctrl+C` en la ventana de `dotnet run`, si está corriendo.

### Paso 2: elegir el modo (y el cron) en `.env`

Sin simulación:

```
GITEA_VISUAL_DEBUG=false
```

Con simulación (videos):

```
GITEA_VISUAL_DEBUG=true
```

Si la línea no existe, el default es `false`. Para probar sin esperar a la siguiente hora en punto, usa un
cron corto y al terminar vuelve a `"0 * * * *"` (el `.env` no se versiona):

```
BOTS_SCHEDULE_CRON="*/2 * * * *"
```

### Paso 3: levantar

`--build` hace falta la primera vez y cada vez que cambia el código o `appsettings.json`:

```powershell
docker compose up -d --build
```

Si solo cambiaste el `.env`, basta con recrear el bot (compose detecta el cambio de variables):

```powershell
docker compose up -d bot
```

Para comprobar qué valor tiene el contenedor:

```powershell
docker compose exec bot printenv Gitea__VisualDebug
```

### Paso 4: seguir los logs

`Ctrl+C` sale de los logs sin detener el bot:

```powershell
docker compose logs -f bot
```

Deberías ver `programador iniciado` y `ocurrencia pendiente ...` o `siguiente ocurrencia ...`; después, la
corrida (`Corrida iniciada` → `Corrida Succeeded`).

#### Docker sin simulación

Solo logs: no se graba nada en `./videos`.

#### Docker con simulación (videos)

Al empezar la corrida el log avisa con un warning
`VisualDebug activo sin pantalla: se graban videos en /app/videos/<fecha>`. Cómo quedan los videos:

- `/app/videos` del contenedor es `./videos` del repo en el host (volumen del compose).
- Una subcarpeta por sesión del navegador con la fecha y hora en UTC, p. ej. `videos/20261023T170200/`;
  con un cron corto, una carpeta nueva por corrida.
- Un `.webm` por página (con nombre aleatorio): login, lista de repos y cada repo o rama.
- **Cada video se completa al cerrarse su página**: espera al `Corrida Succeeded` antes de abrirlos.
- No hay las pausas de la ejecución local (`SlowMo` solo se aplica con pantalla), así que el video va más
  rápido que la simulación en vivo.

Listar los videos y abrir la carpeta de la última corrida:

```powershell
Get-ChildItem videos -Recurse -Filter *.webm | Sort-Object LastWriteTime
```

```powershell
explorer (Get-ChildItem videos -Directory | Sort-Object Name | Select-Object -Last 1).FullName
```

Se ven arrastrando el `.webm` a Chrome o Edge, o con VLC.

Al terminar, vuelve a `GITEA_VISUAL_DEBUG=false` (y el cron a `"0 * * * *"`), recrea el bot con
`docker compose up -d bot` y borra los videos que ya no necesites:

```powershell
Remove-Item videos\* -Recurse
```

### Modo cron externo (supercronic + RunOnce)

Por defecto el bot es un proceso que no termina y programa las corridas con Cronos. Con el override
`docker-compose.cron.yml`, **supercronic** (un cron para contenedores) queda como PID 1 del contenedor
`bot`: lee el mismo `BOTS_SCHEDULE_CRON` y en cada ocurrencia lanza
`dotnet Bots.Worker.dll --Bots:Mode=RunOnce`, que corre la ocurrencia que toca y termina. Entre corridas
solo queda vivo supercronic (ni .NET ni Chromium). `GITEA_VISUAL_DEBUG` funciona igual en los dos modos.

Levantar (o pasar del modo continuo a este; compose recrea el bot):

```powershell
docker compose -f docker-compose.yml -f docker-compose.cron.yml up -d --build
```

En los logs, cada corrida aparece entre las líneas de supercronic `starting` y `job succeeded` /
`job failed` (código de salida 1 = corrida `Failed` o excepción), con los logs JSON del bot en medio:

```powershell
docker compose logs -f bot
```

Lanzar una corrida a mano sin esperar al cron (corre la ocurrencia más reciente; si ya está completada,
no hace nada y sale con 0):

```powershell
docker compose exec bot dotnet /app/Bots.Worker.dll --Bots:Mode=RunOnce
```

Volver al modo continuo:

```powershell
docker compose up -d bot
```

Lo que no cambia: recuperación de ocurrencias pendientes, applock y `UNIQUE` de `job_runs` (sin duplicados ni
solapamiento; supercronic además no lanza una corrida si la anterior sigue en curso). Lo que sí cambia: al
detener el contenedor, supercronic espera a la corrida en curso pero no le reenvía la señal; si no termina en
60 s Docker la mata, la fila queda `Running` y la siguiente ejecución la marca `Abandoned` y la retoma.

### Volver a local

```powershell
docker compose stop bot
```

Y seguir la sección Local desde el paso 4 (SQL sigue corriendo en Docker).

---

## Seguridad de la simulación

La contraseña se rellena de golpe (no tecla a tecla), pero la ventana y los videos muestran los datos de los
repos: no compartirlos ni versionarlos (`videos/` está en `.gitignore`). Solo para depurar: **nunca en
producción**.

## Sobre `appsettings.Development.json`

Es la forma estándar de .NET de tener valores propios de la ejecución local. `Host.CreateApplicationBuilder`
lo carga **encima** de `appsettings.json` solo con `DOTNET_ENVIRONMENT=Development`; Docker corre en
`Production` y lo ignora aunque el archivo esté dentro de la imagen.

Contiene **solo las diferencias** con `appsettings.json`:

- `Bots:Schedule:Cron` = `*/2 * * * *`: para no esperar una hora a la siguiente ocurrencia.
- `Gitea:VisualDebug`: con o sin simulación.
- `Sql:Server` = `127.0.0.1,1433`: compose publica SQL solo en IPv4 (`127.0.0.1:1433`) y en Windows
  `localhost` resuelve primero a `::1`, donde no escucha nada; con `localhost` la conexión agota el tiempo
  de espera.
- `Sql:TrustServerCertificate` = `true`: solo para el SQL Server local de la PoC (certificado autofirmado).

**Nunca lleva contraseñas**: van en user-secrets. Está versionado para que quien clone el repo tenga la
configuración local lista.

### Por qué no clonar `appsettings.json` completo

Clonarlo todo funciona hoy, pero se desincroniza con el tiempo. .NET no reemplaza un archivo por el otro:
los combina **clave por clave**. Si una clave está en Development gana Development; si no está, se usa la de
`appsettings.json`. Con el clon completo están todas, así que `appsettings.json` deja de influir en local.

Ejemplo: hoy los dos tendrían `"NavigationTimeout": "00:00:30"`. Dentro de un mes alguien lo sube a 60 s en
`appsettings.json` porque Gitea tarda más:

| | Docker / producción | Local |
|---|---|---|
| Clon completo | 60 s (nuevo) | **30 s (viejo)**: Development lo pisa |
| Solo diferencias | 60 s | 60 s: se hereda |

En local seguirías viendo timeouts que en Docker ya no ocurren, sin una razón obvia. Lo mismo pasa con:

- **Claves renombradas o eliminadas**: el clon conserva las viejas, que ya no sirven y confunden.
- **Cambios en Polly, logging, `MaxPages`…**: habría que hacer cada uno dos veces.
- **Legibilidad**: con solo las diferencias se ve de un vistazo qué cambia en local (cron, VisualDebug,
  SQL); con el clon son decenas de líneas casi idénticas y hay que compararlas a mano.

### Por qué no editar `appsettings.json`

Funciona (el worker lo lee igual), pero es la base de todos los entornos y entra en la imagen de Docker: un
cron de 2 minutos o un `VisualDebug: true` subido por error llegaría a cualquier despliegue que no lo
sobrescriba, y habría que acordarse de revertirlo antes de cada commit. Todo lo local va en
`appsettings.Development.json`; todo lo de Docker, en `.env`.

## Problemas frecuentes

| Dónde | Síntoma | Causa | Qué hacer |
|---|---|---|---|
| Local | `Sql:Server, Sql:Database, Sql:User y Sql:Password son obligatorios` | Faltan user-secrets o no se pasó `DOTNET_ENVIRONMENT=Development` | Paso 3 y comando del paso 4 |
| Local | `SqlException` con `Error Number:258` (tiempo de espera agotado) | SQL apagado, o `localhost` resuelve a `::1` | `docker compose ps` (el `sql` debe estar `healthy`); `Sql:Server` debe ser `127.0.0.1,1433` |
| Local | `siguiente ocurrencia ...` a una hora en punto | No se cargó `appsettings.Development.json` | Falta `$env:DOTNET_ENVIRONMENT="Development"` |
| Local | Cambié `appsettings.Development.json` y no se nota | Un `$env:` o un user-secret con la misma clave gana sobre el archivo | `Remove-Item Env:...` o ventana nueva; quitar el user-secret |
| Local | No se abre la ventana con `VisualDebug: true` | La corrida aún no empezó, o algo pisa el valor | Esperar a la hora de `siguiente ocurrencia`; revisar la fila anterior |
| Local | Error de Playwright al lanzar el navegador | Chromium no instalado | Paso 2 de Local |
| Docker | `Falta ... en .env` al hacer `docker compose up` | Variable obligatoria vacía o ausente | Completar `.env` (ver `.env.example`) |
| Docker | Cambié `.env` y el bot sigue igual | El contenedor no se recreó | `docker compose up -d bot` y comprobar con `printenv` |
| Docker | Cambié el código y no se nota | La imagen es la vieja | `docker compose up -d --build` |
| Docker | `GITEA_VISUAL_DEBUG=true` y `./videos` vacía | Todavía no hubo corrida, o el volumen no es escribible | Esperar la corrida; en un host Linux, `./videos` debe ser escribible por el uid 1001 |
| Docker | `VisualDebug no puede grabar videos en ...` | `/app/videos` no está montado o no tiene permisos | Revisar el volumen `./videos:/app/videos` del compose |
| Docker | El video está vacío o cortado | Se abrió antes de que se cerrara su página | Esperar al `Corrida Succeeded` |
| Docker | No conecta a Gitea | `GITEA_BASE_URL` con `localhost` | Usar `http://host.docker.internal:3000` |
| Ambos | `siguiente ocurrencia ...` y no pasa nada | No había ocurrencia pendiente: el bot espera al cron | Esperar a esa hora, o cron corto |
| Ambos | `No se inicia sync-repos para ...: otra instancia tiene el lock...` | El bot local y el contenedor corren a la vez | Detener uno de los dos |

Para fallos del bot en general (corridas `Failed`, Gitea caído, recuperación), ver el
[runbook](runbook.md).
