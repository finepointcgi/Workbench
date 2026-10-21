# Workbench web host: multi-stage build, non-root, read-only-root friendly, health-checked.
#
#   docker build -t workbench:local .
#   docker run --rm workbench:local --migrate          # the one-shot migration job (same image)
#   docker build --target demo -t workbench:demo .     # the public demo (docs/demo-hosting.md)
#
# See docs/operations/deployment.md. The runtime image is Ubuntu "chiseled": no shell, no package manager, runs as
# the non-root "app" user (UID 1654). The "extra" flavour adds ICU and tzdata (time zones for digests and reports).

ARG DOTNET_VERSION=10.0

# Plain Dockerfile syntax only (no --platform=$BUILDPLATFORM, no RUN --mount): Google Cloud Build's default builder
# isn't BuildKit and rejects both. The restore layer is still cached until a project file changes.
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
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
RUN dotnet restore src/Workbench.Web/Workbench.Web.csproj
COPY src/ src/
# The sample GSA per diem table is an embedded resource of Workbench.Application.
COPY samples/perdiem/ samples/perdiem/
# No --no-restore: the restore above ran with only the project files, and the SDK adds the Blazor framework assets
# package (blazor.web.js) only when it can see the components. Restoring again here is quick (packages are cached).
RUN dotnet publish src/Workbench.Web/Workbench.Web.csproj -c "$BUILD_CONFIGURATION" -o /app \
      -p:UseAppHost=false -p:Version="$VERSION" -p:DebugType=none
# Root-owned and read-only for the app user in the final images (set here: the chiseled runtime image has no shell).
RUN chmod -R 0755 /app
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
COPY --from=build --chown=0:0 /app ./
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
COPY --from=build --chown=0:0 /app ./
COPY --chown=0:0 samples/costpoint/ /app/samples/costpoint/
COPY --chown=0:0 deploy/demo/entrypoint.sh /usr/local/bin/workbench-demo
RUN chmod -R u=rwX,go=rX /app/samples/costpoint && chmod 0755 /usr/local/bin/workbench-demo
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
