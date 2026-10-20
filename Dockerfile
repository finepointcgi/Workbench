# syntax=docker/dockerfile:1.7
# Workbench web host: multi-stage build, non-root, read-only-root friendly, health-checked.
#
#   docker build -t workbench:local .
#   docker run --rm workbench:local --migrate          # the one-shot migration job (same image)
#   docker build --target demo -t workbench:demo .     # the public demo (docs/demo-hosting.md)
#
# See docs/operations/deployment.md. The runtime image is Ubuntu "chiseled": no shell, no package manager, runs as
# the non-root "app" user (UID 1654). The "extra" flavour adds ICU and tzdata (time zones for digests and reports).

ARG DOTNET_VERSION=10.0

# The build stage runs natively (published output is portable), so cross-building linux/amd64 on Apple silicon is fast.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG BUILD_CONFIGURATION=Release
ARG VERSION=0.0.0
WORKDIR /src
# Project files first so the restore layer is cached until a dependency changes.
COPY Workbench.slnx dotnet-tools.json ./
COPY src/Workbench.Domain/Workbench.Domain.csproj src/Workbench.Domain/
COPY src/Workbench.Contracts/Workbench.Contracts.csproj src/Workbench.Contracts/
COPY src/Workbench.Application/Workbench.Application.csproj src/Workbench.Application/
COPY src/Workbench.Infrastructure/Workbench.Infrastructure.csproj src/Workbench.Infrastructure/
COPY src/Workbench.Migrations.PostgreSql/Workbench.Migrations.PostgreSql.csproj src/Workbench.Migrations.PostgreSql/
COPY src/Workbench.Migrations.Sqlite/Workbench.Migrations.Sqlite.csproj src/Workbench.Migrations.Sqlite/
COPY src/Workbench.Costpoint/Workbench.Costpoint.csproj src/Workbench.Costpoint/
COPY src/Workbench.Api/Workbench.Api.csproj src/Workbench.Api/
COPY src/Workbench.Ui/Workbench.Ui.csproj src/Workbench.Ui/
COPY src/Workbench.Web/Workbench.Web.csproj src/Workbench.Web/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet restore src/Workbench.Web/Workbench.Web.csproj
COPY src/ src/
# The sample GSA per diem table is an embedded resource of Workbench.Application.
COPY samples/perdiem/ samples/perdiem/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages \
    dotnet publish src/Workbench.Web/Workbench.Web.csproj --no-restore -c "$BUILD_CONFIGURATION" -o /app \
      -p:UseAppHost=false -p:Version="$VERSION" -p:DebugType=none
# Mount points for the writable volumes, owned by the runtime user (a new named volume copies this ownership).
RUN mkdir -p /data/blobs /data/mail

# The production image. Build it with --target runtime (CI, deploy.yml and docker-compose.prod.yml do): the last stage
# below is the demo, which Render and Fly build by default.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS runtime
ARG VERSION=0.0.0
ARG REVISION=unknown
LABEL org.opencontainers.image.title="Workbench" \
      org.opencontainers.image.description="Workbench GovCon ERP web host" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
WORKDIR /app
COPY --from=build --chown=0:0 --chmod=0755 /app ./
COPY --from=build --chown=1654:1654 /data /data
# Writable state lives on mounted volumes: blobs (attachments, receipts) and, in development only, the mail pickup
# folder. Everything else (keys, data) is in PostgreSQL. Mount /tmp as tmpfs when the root filesystem is read-only.
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Security__TlsTerminatedByProxy=true \
    Storage__Root=/data/blobs \
    Email__PickupFolder=/data/mail \
    SecretsDirectory=/run/secrets \
    DOTNET_gcServer=1 \
    DOTNET_NOLOGO=1
EXPOSE 8080
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=60s --retries=3 \
    CMD ["dotnet", "/app/Workbench.Web.dll", "--health-check"]
ENTRYPOINT ["dotnet", "/app/Workbench.Web.dll"]

# The public demo (docs/demo-hosting.md): sample data on a SQLite database in the /data volume, the demo sign-in page
# and the fake assistant, reset nightly. Built on the regular (non-chiseled) image because hosted volumes are mounted
# owned by root: the entrypoint hands /data to the "app" user and drops to it before starting Workbench. Never point
# this image at a real database; it refuses one that has users but no demo administrator.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble AS demo
ARG VERSION=0.0.0
ARG REVISION=unknown
LABEL org.opencontainers.image.title="Workbench demo" \
      org.opencontainers.image.description="Workbench GovCon ERP public demo (sample data only)" \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$REVISION"
WORKDIR /app
COPY --from=build --chown=0:0 --chmod=0755 /app ./
COPY --chown=0:0 --chmod=0644 samples/costpoint/ /app/samples/costpoint/
COPY --chown=0:0 --chmod=0755 deploy/demo/entrypoint.sh /usr/local/bin/workbench-demo
# Demo mode takes both: the Demo environment (appsettings.Demo.json) and Demo:Enabled.
ENV ASPNETCORE_ENVIRONMENT=Demo \
    Demo__Enabled=true \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0 \
    DOTNET_NOLOGO=1
EXPOSE 8080
VOLUME /data
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s --retries=3 \
    CMD ["dotnet", "/app/Workbench.Web.dll", "--health-check"]
ENTRYPOINT ["/usr/local/bin/workbench-demo"]
