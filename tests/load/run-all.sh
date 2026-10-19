#!/usr/bin/env bash
# One measured load run: fresh database copy, server, k6 (all scenarios), counters and pg_stat_statements.
#   tests/load/run-all.sh <label>        (WEB_DIR= a published build to test; OUT= results folder, default /tmp/workbench-load)
# Writes <label>-k6.csv, -k6.txt, -summary.txt, -counters.csv, -pgstat.txt and -server.log to $OUT.
set -euo pipefail
LABEL="${1:?label}"
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${OUT:-/tmp/workbench-load}"
mkdir -p "$OUT"
CONTAINER="${PG_CONTAINER:-$(docker ps --format "{{.Names}} {{.Ports}}" | grep -m1 "0.0.0.0:5444->" | cut -d" " -f1)}"
pkill -f "Workbench.Web.dll" || true
sleep 2
LOG="$OUT/$LABEL-server.log" "$HERE/run-server.sh" workbench_load 5300 > /dev/null 2>&1 &
for _ in $(seq 1 120); do curl -s -o /dev/null -w "%{http_code}" http://localhost:5300/account/dev-signin 2>/dev/null | grep -q 200 && break; sleep 2; done
docker exec "$CONTAINER" psql -U workbench -d workbench_load_run -qc "create extension if not exists pg_stat_statements" -c "select pg_stat_statements_reset()" > /dev/null
PID=$(pgrep -f "Workbench.Web.dll" | head -1)
dotnet-counters collect -p "$PID" --format csv -o "$OUT/$LABEL-counters.csv" --counters System.Runtime,Microsoft.AspNetCore.Hosting --refresh-interval 5 > /dev/null 2>&1 &
COUNTERS=$!
(cd "$HERE" && k6 run -q --out "csv=$OUT/$LABEL-k6.csv" workbench.js > "$OUT/$LABEL-k6.txt" 2>&1) || true
kill "$COUNTERS" 2>/dev/null || true
docker exec "$CONTAINER" psql -U workbench -d workbench_load_run -Atc "select round(total_exec_time)::int, calls, round(mean_exec_time::numeric,1), rows/greatest(calls,1), left(regexp_replace(query, '\s+', ' ', 'g'), 300) from pg_stat_statements order by total_exec_time desc limit 30" > "$OUT/$LABEL-pgstat.txt"
python3 -I "$HERE/summarize.py" "$OUT/$LABEL-k6.csv" --skip "${SKIP:-45}" > "$OUT/$LABEL-summary.txt"
cat "$OUT/$LABEL-summary.txt"
