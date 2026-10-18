#!/usr/bin/env bash
# Adds an EF Core migration to both providers (PostgreSQL and SQLite), so they never drift apart.
#
#   scripts/new-migration.sh AddPurchaseOrders            # the tenant database (WorkbenchDbContext)
#   scripts/new-migration.sh AddTenantRegion --catalog    # the MultiTenant catalog (CatalogDbContext)
#
# Commit both generated folders together. See docs/CONVENTIONS.md ("Database migrations").
set -euo pipefail

if [[ $# -lt 1 || "$1" == -* ]]; then
  echo "usage: $0 MigrationName [--catalog]" >&2
  exit 2
fi

name="$1"
context="Workbench"
folder="WorkbenchDb"
if [[ "${2:-}" == "--catalog" ]]; then
  context="Catalog"
  folder="CatalogDb"
fi

cd "$(dirname "$0")/.."
dotnet tool restore >/dev/null
for provider in PostgreSql Sqlite; do dotnet build "src/Workbench.Migrations.$provider" --nologo -v quiet; done

for provider in PostgreSql Sqlite; do
  project="src/Workbench.Migrations.$provider"
  echo "==> $provider: $name ($context)"
  dotnet tool run dotnet-ef migrations add "$name" \
    --project "$project" --startup-project "$project" \
    --context "${context}DbContext" \
    --output-dir "$folder"
done

# Fails if the model still differs from the snapshot in either provider (e.g. a migration was edited by hand).
for provider in PostgreSql Sqlite; do
  project="src/Workbench.Migrations.$provider"
  dotnet tool run dotnet-ef migrations has-pending-model-changes \
    --project "$project" --startup-project "$project" --context "${context}DbContext"
done
