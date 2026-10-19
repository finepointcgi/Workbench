#!/usr/bin/env bash
# Starts Workbench.Web (Release) against a copy of the load database, configured for the k6 scenarios:
#   tests/load/run-server.sh [template-db] [port]
# Copies the template (default workbench_load, made by tools/Workbench.LoadData) to workbench_load_run first, so the
# destructive scenarios (approvals, time entry) start from the same data every run. Logs go to $LOG (default
# /tmp/workbench-load-server.log); per-request timings are logged by Workbench.Diagnostics.ServerTiming.
set -euo pipefail
TEMPLATE="${1:-workbench_load}"
PORT="${2:-5300}"
RUN_DB="${RUN_DB:-workbench_load_run}"
LOG="${LOG:-/tmp/workbench-load-server.log}"
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
CONTAINER="${PG_CONTAINER:-$(docker ps --format "{{.Names}} {{.Ports}}" | grep -m1 "0.0.0.0:5444->" | cut -d" " -f1)}"

docker exec "$CONTAINER" psql -U workbench -d postgres -qc "DROP DATABASE IF EXISTS \"$RUN_DB\" WITH (FORCE)" -c "CREATE DATABASE \"$RUN_DB\" TEMPLATE \"$TEMPLATE\""
docker exec "$CONTAINER" psql -U workbench -d "$RUN_DB" -qc "ANALYZE"
# WEB_DIR: a published Workbench.Web folder to run instead of building the working tree (e.g. a "before" build).
if [ -z "${WEB_DIR:-}" ]; then
  WEB_DIR="${TMPDIR:-/tmp}/workbench-load-web"
  dotnet publish -c Release "$ROOT/src/Workbench.Web" -o "$WEB_DIR" -v q -nologo > /dev/null
fi

cd "$WEB_DIR"
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://localhost:$PORT"
export Database__Provider=PostgreSQL
export Database__MigrateOnStartup=false
export ConnectionStrings__Workbench="Host=localhost;Port=5444;Database=$RUN_DB;Username=workbench;Password=workbench;Maximum Pool Size=200"
export Authentication__RequireInvite=false
export RateLimiting__Enabled=false
export Diagnostics__ServerTiming=true
export Email__Mode=Pickup
export Email__PickupFolder=/tmp/workbench-load-mail
export Logging__LogLevel__Default=Warning
export Logging__Console__FormatterName=simple
echo "Workbench on http://localhost:$PORT (database $RUN_DB), log $LOG"
# Category names contain dots, which bash variables can't, so these go through env.
exec env "Logging__LogLevel__Workbench.Diagnostics.ServerTiming=Information" "Logging__LogLevel__Microsoft.EntityFrameworkCore.Query=Warning" \
  dotnet Workbench.Web.dll > "$LOG" 2>&1
