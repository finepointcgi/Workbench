#!/usr/bin/env bash
# Writes tests/load/fixtures.json from a database filled by tools/Workbench.LoadData: who signs in for each scenario
# (time entry, approvers with pending tasks, project managers, finance), the open periods and the big sheets.
#   tests/load/export-fixtures.sh [database]   (default workbench_load_run; PG_CONTAINER overrides the container)
set -euo pipefail
DB="${1:-workbench_load_run}"
CONTAINER="${PG_CONTAINER:-$(docker ps --format "{{.Names}} {{.Ports}}" | grep -m1 "0.0.0.0:5444->" | cut -d" " -f1)}"
OUT="$(cd "$(dirname "$0")" && pwd)/fixtures.json"
# The local date, not the database clock (UTC): in the evening US time CURRENT_DATE is already tomorrow, and
# tomorrow's timesheet period may not have started.
TODAY="${TODAY:-$(date +%F)}"
docker exec -i "$CONTAINER" psql -U workbench -d "$DB" -At -v today="$TODAY" <<'SQL' > "$OUT"
WITH today AS (SELECT DATE :'today' AS d),
current_period AS (SELECT p.* FROM "FiscalPeriod" p, today WHERE p."Start" <= today.d AND p."End" >= today.d),
last_period AS (SELECT p.* FROM "FiscalPeriod" p, current_period c WHERE p."End" < c."Start" ORDER BY p."Start" DESC LIMIT 1),
staff AS (
  SELECT e."Email" FROM "Employee" e JOIN "AppUser" u ON u."Email" = e."Email"
  WHERE e."TerminationDate" IS NULL AND u."IsActive" AND EXISTS (SELECT 1 FROM "Employee" s WHERE s."Id" = e."SupervisorId")
  ORDER BY e."EmployeeNumber" DESC LIMIT 300),
approvers AS (
  SELECT DISTINCT t."AssigneesCsv" AS "Email" FROM "ApprovalTask" t JOIN "AppUser" u ON u."Email" = t."AssigneesCsv" AND u."IsActive"
  WHERE t."Status" = 'Pending' AND t."AssigneesCsv" NOT LIKE '%,%'),
pms AS (
  SELECT e."Email", json_agg(p."Id") AS projects FROM "Project" p JOIN "Employee" e ON e."Id" = p."ProjectManagerId"
  JOIN "AppUser" u ON u."Email" = e."Email" AND u."IsActive" WHERE p."Status" <> 'Closed' GROUP BY e."Email"),
sheets AS (
  SELECT s."Id", s."Name", count(i."Id") AS rows,
    (SELECT c."Id" FROM "Columns" c WHERE c."SheetId" = s."Id" AND c."Name" = 'Notes') AS notes,
    (SELECT json_agg(m."UserEmail") FROM "WorkspaceMember" m JOIN "AppUser" u ON u."Email" = m."UserEmail" AND u."IsActive"
     WHERE m."WorkspaceId" = s."WorkspaceId" AND m."Role" IN ('Editor', 'Owner')) AS editors
  FROM "Sheets" s JOIN "Items" i ON i."SheetId" = s."Id" WHERE s."SchedulingEnabled" = false
  GROUP BY s."Id" HAVING count(i."Id") >= 500 ORDER BY count(i."Id") DESC, s."Name")
SELECT json_build_object(
  'staff', (SELECT json_agg("Email") FROM staff),
  'approvers', (SELECT json_agg("Email") FROM approvers),
  'pms', (SELECT json_agg(json_build_object('email', "Email", 'projects', projects)) FROM pms),
  'finance', (SELECT json_agg(u."Email") FROM "AppUser" u WHERE u."IsFinance"),
  'currentPeriod', (SELECT "Id" FROM current_period),
  'lastPeriod', (SELECT "Id" FROM last_period),
  'iceYear', (SELECT max(y."Year") FROM "FiscalYear" y, today WHERE y."End" < today.d - 365),
  'today', (SELECT d FROM today),
  'sheets', (SELECT json_agg(json_build_object('id', "Id", 'name', "Name", 'rows', rows, 'notes', notes, 'editors', editors)) FROM sheets));
SQL
echo "Wrote $OUT ($(wc -c < "$OUT") bytes)"
