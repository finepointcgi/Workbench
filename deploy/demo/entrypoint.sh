#!/bin/sh
# Public demo entrypoint (docs/demo-hosting.md). Hosted volumes (Fly.io, Render) are mounted owned by root, so this
# starts as root only to create and hand /data to the non-root "app" user, then runs Workbench as that user. Also the
# way to run a one-off command in the running container, e.g. `workbench-demo --reset-demo` over `fly ssh console`,
# whose session may not carry the image's environment: hence the defaults below (the same as the Dockerfile's).
set -eu
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Demo}"
export Demo__Enabled="${Demo__Enabled:-true}"
export ASPNETCORE_HTTP_PORTS="${ASPNETCORE_HTTP_PORTS:-8080}"
mkdir -p /data/blobs /data/mail
chown -R app:app /data
cd /app
exec setpriv --reuid=app --regid=app --init-groups dotnet /app/Workbench.Web.dll "$@"
