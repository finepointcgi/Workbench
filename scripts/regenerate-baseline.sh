#!/usr/bin/env bash
# Pre-pilot only: deletes every migration and generates one InitialCreate per provider (tenant database and catalog)
# from the current model. Safe only while no shared or production database exists; afterwards add migrations with
# scripts/new-migration.sh instead. Delete local development databases (src/Workbench.Web/*.db, App_Data/tenants)
# afterwards, or let Development set the old SQLite file aside on startup.
set -euo pipefail
cd "$(dirname "$0")/.."

for provider in PostgreSql Sqlite; do
  rm -rf "src/Workbench.Migrations.$provider/WorkbenchDb" "src/Workbench.Migrations.$provider/CatalogDb"
done

scripts/new-migration.sh InitialCreate
scripts/new-migration.sh InitialCreate --catalog
