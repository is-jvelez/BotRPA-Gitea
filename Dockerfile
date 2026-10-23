# syntax=docker/dockerfile:1
# Un solo Dockerfile con targets nombrados:
#   sqlserver -> imagen de SQL Server para el servicio `sql` del compose
#   build     -> compila y publica Bots.Worker
#   runtime   -> imagen final del bot (target por defecto: es el último)
#
# Las imágenes base van fijadas por digest (@sha256:...): un tag puede volver a publicarse con
# otro contenido, el digest no. Al cambiar un tag hay que cambiar también su digest; se obtiene con
#   docker buildx imagetools inspect <imagen>:<tag> --format '{{json .Manifest.Digest}}'

# Tag y digest de SQL Server (se pueden fijar en .env con MSSQL_TAG / MSSQL_DIGEST). El digest
# incluye la @ y debe corresponder al tag; vacío = solo el tag.
ARG MSSQL_TAG=2022-CU27-ubuntu-22.04
ARG MSSQL_DIGEST=@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090
# Debe coincidir con la versión del paquete Microsoft.Playwright de Directory.Packages.props
# (paquete 1.63.0 -> imagen v1.63.0-noble). Cambiar ambos a la vez: si no coinciden, el
# driver del paquete no encuentra los navegadores de la imagen.
ARG PLAYWRIGHT_TAG=v1.63.0-noble
ARG PLAYWRIGHT_DIGEST=@sha256:aa3eaf29a65df8c93fc32ab20cf9359f4d8ecc53e6e89f79378647ace87b46b3

# ---------------------------------------------------------------------------
# Target `sqlserver`: solo la imagen base. SQL Server corre como contenedor aparte
# (servicio `sql` del compose); NO se instala dentro de la imagen del bot.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/mssql/server:${MSSQL_TAG}${MSSQL_DIGEST} AS sqlserver

# ---------------------------------------------------------------------------
# Target `build`. Tag y digest literales para que Dependabot pueda actualizar el digest.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:747cbade5bfd918a175ac32f8f1d931edec2c7d0d5397a3de72eaef1ac1f66d2 AS build
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
# NuGet no conserva el bit de ejecución: sin chmod el driver de Playwright falla con "Permission denied".
# Se busca el binario node de cualquier plataforma (linux-x64, linux-arm64...), no una ruta fija.
RUN dotnet publish src/Bots.Worker/Bots.Worker.csproj -c Release --no-restore -o /app/publish \
    && node_bins="$(find /app/publish/.playwright/node -type f -name node)" \
    && test -n "$node_bins" \
    && chmod +x $node_bins

# ---------------------------------------------------------------------------
# Target `runtime` (por defecto). La imagen de Playwright ya trae el runtime de .NET 10
# (verificado en la Fase 3 con `dotnet --list-runtimes`: Microsoft.NETCore.App 10.0.12),
# así que el publish es framework-dependent. Los navegadores vienen en /ms-playwright;
# no se descargan en el build.
# ---------------------------------------------------------------------------
FROM mcr.microsoft.com/playwright/dotnet:${PLAYWRIGHT_TAG}${PLAYWRIGHT_DIGEST} AS runtime
WORKDIR /app
# Los archivos quedan de root y solo lectura para el usuario de ejecución.
COPY --from=build /app/publish .
# Usuario no root que trae la imagen de Playwright (uid 1001).
USER pwuser
ENTRYPOINT ["dotnet", "Bots.Worker.dll"]
