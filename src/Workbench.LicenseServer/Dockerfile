# Workbench license server: multi-stage build, non-root, chiseled runtime (no shell).
# Build from the repository root:
#   docker build -f src/Workbench.LicenseServer/Dockerfile -t workbench-license-server .
# Run (see docs/admin-guide/license-server.md for every setting):
#   docker run -p 8080:8080 -v ls-data:/data -v $PWD/secrets:/run/secrets:ro \
#     -e Admin__ApiKey=... -e Signing__PrivateKeyPath=/run/secrets/license-private.pem workbench-license-server
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src
COPY src/Workbench.LicenseServer/Workbench.LicenseServer.csproj src/Workbench.LicenseServer/
RUN dotnet restore src/Workbench.LicenseServer/Workbench.LicenseServer.csproj
COPY src/Workbench.LicenseServer/ src/Workbench.LicenseServer/
RUN dotnet publish src/Workbench.LicenseServer/Workbench.LicenseServer.csproj -c Release -o /app -p:UseAppHost=false -p:DebugType=none \
    && chmod -R 0755 /app && mkdir /data

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled AS final
WORKDIR /app
COPY --from=build --chown=0:0 /app ./
COPY --from=build --chown=1654:1654 /data /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    Database__ConnectionString="Data Source=/data/licenses.db" \
    Portal__DataProtectionPath=/data/keys \
    DOTNET_NOLOGO=1
EXPOSE 8080
VOLUME /data
# Probe GET /healthz from the orchestrator (the chiseled image has no curl).
USER 1654
ENTRYPOINT ["dotnet", "Workbench.LicenseServer.dll"]
