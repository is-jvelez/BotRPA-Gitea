# syntax=docker/dockerfile:1
# Un solo Dockerfile con targets nombrados:
#   sqlserver -> imagen de SQL Server para el servicio `sql` del compose
#   build     -> compila y publica Bots.Worker
#   runtime   -> imagen final del bot (target por defecto: es el último)

# Fijar un tag específico en .env (MSSQL_TAG), p. ej. 2022-CU27-ubuntu-22.04.
ARG MSSQL_TAG=2022-latest
# Debe coincidir con la versión del paquete Microsoft.Playwright de Directory.Packages.props
# (paquete 1.63.0 -> imagen v1.63.0-noble). Cambiar ambos a la vez: si no coinciden, el
# driver del paquete no encuentra los navegadores de la imagen.
ARG PLAYWRIGHT_TAG=v1.63.0-noble

# ---------------------------------------------------------------------------
# Target `sqlserver`: solo la imagen base. SQL Server corre como contenedor aparte
# (servicio `sql` del compose); NO se instala dentro de la imagen del bot.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/mssql/server:${MSSQL_TAG} AS sqlserver

# ---------------------------------------------------------------------------
# Target `build`
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Primero solo los archivos de proyecto: la capa de restore se reutiliza mientras no
# cambien las dependencias.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Bots.Domain/Bots.Domain.csproj src/Bots.Domain/
COPY src/Bots.Application/Bots.Application.csproj src/Bots.Application/
COPY src/Bots.Infrastructure/Bots.Infrastructure.csproj src/Bots.Infrastructure/
COPY src/Bots.Worker/Bots.Worker.csproj src/Bots.Worker/
RUN dotnet restore src/Bots.Worker/Bots.Worker.csproj

COPY src/ src/
RUN dotnet publish src/Bots.Worker/Bots.Worker.csproj -c Release --no-restore -o /app/publish

# ---------------------------------------------------------------------------
# Target `runtime` (por defecto). La imagen de Playwright ya trae el runtime de .NET 10
# (verificado en la Fase 3 con `dotnet --list-runtimes`: Microsoft.NETCore.App 10.0.12),
# así que el publish es framework-dependent. Los navegadores vienen en /ms-playwright;
# no se descargan en el build.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/playwright/dotnet:${PLAYWRIGHT_TAG} AS runtime
WORKDIR /app
# Los archivos quedan de root y solo lectura para el usuario de ejecución.
COPY --from=build /app/publish .
# Usuario no root que trae la imagen de Playwright (uid 1001).
USER pwuser
ENTRYPOINT ["dotnet", "Bots.Worker.dll"]
