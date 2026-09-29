# MSS-043: production-like local image for MtgSoloSports.
#
# Multi-stage Linux build:
#   1. web-build     — Node 22 builds the React/Vite frontend into
#                      src/MtgSoloSports/wwwroot (the ASP.NET static-file root).
#   2. dotnet-build  — .NET 10 SDK restores (locked mode) and publishes the
#                      ASP.NET Core host, including the compiled wwwroot above.
#   3. runtime       — ASP.NET 10 runtime only, running as non-root user `app`.
#
# The default Compose mode COPYs source into image layers. It never
# bind-mounts host source, node_modules or build output over the image
# filesystem, so a Windows/NTFS checkout cannot leak case-folded paths into
# the running app. See compose.yaml and README.md.
#
# Linux path casing is significant in every COPY below; the CI casing guard
# (scripts/check-casing.py) verifies these sources exist with exact case.

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG DOTNET_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0
ARG NODE_IMAGE=node:22-bookworm-slim

# ---------------------------------------------------------------- web-build
FROM ${NODE_IMAGE} AS web-build
WORKDIR /repo

# Install frontend dependencies first for better layer caching.
COPY src/MtgSoloSports.Web/package.json src/MtgSoloSports.Web/package-lock.json ./src/MtgSoloSports.Web/
RUN npm ci --prefix src/MtgSoloSports.Web

# Build the frontend. Vite emits to ../MtgSoloSports/wwwroot, i.e.
# /repo/src/MtgSoloSports/wwwroot, the ASP.NET static-file root.
COPY src/MtgSoloSports.Web ./src/MtgSoloSports.Web
RUN npm run build --prefix src/MtgSoloSports.Web

# ------------------------------------------------------------ dotnet-build
FROM ${DOTNET_SDK_IMAGE} AS dotnet-build
WORKDIR /repo

# Restore first for better layer caching (exact-case project paths).
COPY MtgSoloSports.slnx Directory.Build.props Directory.Packages.props NuGet.Config ./
COPY src/MtgSoloSports/MtgSoloSports.csproj ./src/MtgSoloSports/
COPY tests/MtgSoloSports.Tests/MtgSoloSports.Tests.csproj ./tests/MtgSoloSports.Tests/
COPY src/MtgSoloSports/packages.lock.json ./src/MtgSoloSports/
COPY tests/MtgSoloSports.Tests/packages.lock.json ./tests/MtgSoloSports.Tests/
RUN dotnet restore MtgSoloSports.slnx --locked-mode

# Backend sources, then the compiled frontend from the web-build stage.
# The COPY --from order matters: it must come after the backend COPY so the
# fresh Linux-built assets are what `dotnet publish` packs into wwwroot.
COPY src/MtgSoloSports ./src/MtgSoloSports
COPY --from=web-build /repo/src/MtgSoloSports/wwwroot ./src/MtgSoloSports/wwwroot
RUN dotnet publish src/MtgSoloSports/MtgSoloSports.csproj --configuration Release --no-restore --output /app/publish

# ---------------------------------------------------------------- runtime
FROM ${DOTNET_RUNTIME_IMAGE} AS runtime
WORKDIR /app

# curl backs the Compose healthcheck (`/api/health`); kept minimal and
# unprivileged. No SDK, no Node, no source in the final image.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=dotnet-build /app/publish ./

# Writable data directories for the non-root runtime user. SQLite state
# (per-save files + shared catalog) lives under /data, which Compose mounts
# as a named volume. Created at build time so `USER app` can write without
# privileged startup.
RUN mkdir -p /data/saves /data/catalog && chown -R app:app /data

# Bind Kestrel to every container interface on 8080 and point file-backed
# stores at the persistent volume. These are local-development defaults, not
# credentials; user data is never baked into the image.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Saves__SavesRoot=/data/saves \
    Catalog__CatalogPath=/data/catalog/catalog.db

USER app

EXPOSE 8080

ENTRYPOINT ["dotnet", "MtgSoloSports.dll"]
