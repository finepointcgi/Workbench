#!/usr/bin/env bash
# Encrypted, snapshot-consistent PostgreSQL backups of every Workbench database, with retention (ISO 27001 A.8.13).
# See docs/operations/backup-restore.md. Connection settings come from libpq variables (PGHOST, PGPORT, PGUSER,
# PGPASSFILE or a vault-injected PGPASSWORD, PGSSLMODE); never pass passwords on the command line.
#
#   SingleTenant:  BACKUP_DATABASES="workbench" scripts/backup-all.sh
#   MultiTenant:   BACKUP_CATALOG_DB=workbench_catalog BACKUP_TENANT_DB_TEMPLATE='workbench_{tenant}' scripts/backup-all.sh
#
# Settings (environment):
#   BACKUP_DIR                  where run folders go (default /backups); copy them off-host to immutable storage
#   BACKUP_DATABASES            space-separated databases to back up (SingleTenant, or extra databases)
#   BACKUP_CATALOG_DB           MultiTenant: the catalog database; its Active/Suspended tenants are added
#   BACKUP_TENANT_DB_TEMPLATE   MultiTenant: tenant database name with {tenant} (default workbench_{tenant})
#   BACKUP_GPG_RECIPIENT        gpg key id/email to encrypt to (in the keyring), or
#   BACKUP_GPG_RECIPIENT_FILE   an armored public key file (imported into a throwaway keyring)
#   BACKUP_ALLOW_UNENCRYPTED=1  only for development: write plain dumps
#   BACKUP_RETENTION_DAYS       delete complete runs older than this (default 35); the newest BACKUP_MIN_KEEP (7) stay
#   BACKUP_NOTIFY_URL           optional: GET <url> on success, <url>/fail on failure (healthchecks.io style dead-man switch)
#
# Each run writes BACKUP_DIR/<UTC stamp>/: <db>.dump.gpg (pg_dump custom format), <db>.manifest (row counts and the
# trial-balance fingerprint, taken in the same snapshot as the dump), SHA256SUMS, and COMPLETE last. A run without
# COMPLETE failed part-way and is never used or counted for retention. Exits non-zero on any failure.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/pg-manifest.sh
source "$here/lib/pg-manifest.sh"

backup_dir="${BACKUP_DIR:-/backups}"
retention_days="${BACKUP_RETENTION_DAYS:-35}"
min_keep="${BACKUP_MIN_KEEP:-7}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
run="$backup_dir/$stamp"
umask 077

notify() { [[ -n "${BACKUP_NOTIFY_URL:-}" ]] && curl -fsS -m 10 --retry 3 "$BACKUP_NOTIFY_URL$1" > /dev/null || true; }
on_exit() {
  local status=$?
  if [[ $status -ne 0 ]]; then log "Backup FAILED (exit $status); $run is incomplete"; notify /fail; fi
}
trap on_exit EXIT

# Encryption: refuse to write plain dumps unless explicitly allowed.
gpg_args=()
if [[ -n "${BACKUP_GPG_RECIPIENT_FILE:-}" ]]; then
  GNUPGHOME="$(mktemp -d)"
  export GNUPGHOME
  gpg --batch --quiet --import "$BACKUP_GPG_RECIPIENT_FILE"
  recipient="$(gpg --batch --with-colons --list-keys | awk -F: '/^fpr:/ { print $10; exit }')"
  gpg_args=(--batch --yes --trust-model always --encrypt --recipient "$recipient")
elif [[ -n "${BACKUP_GPG_RECIPIENT:-}" ]]; then
  gpg_args=(--batch --yes --trust-model always --encrypt --recipient "$BACKUP_GPG_RECIPIENT")
elif [[ "${BACKUP_ALLOW_UNENCRYPTED:-}" != "1" ]]; then
  log "No encryption key: set BACKUP_GPG_RECIPIENT or BACKUP_GPG_RECIPIENT_FILE (or BACKUP_ALLOW_UNENCRYPTED=1 in development)."
  exit 2
fi

read -r -a databases <<< "${BACKUP_DATABASES:-}"
if [[ -n "${BACKUP_CATALOG_DB:-}" ]]; then
  databases+=("$BACKUP_CATALOG_DB")
  tenant_template="${BACKUP_TENANT_DB_TEMPLATE:-}"
  [[ -n "$tenant_template" ]] || tenant_template='workbench_{tenant}'
  while IFS= read -r db; do databases+=("$db"); done < <(tenant_databases "$BACKUP_CATALOG_DB" "$tenant_template")
fi
[[ ${#databases[@]} -gt 0 ]] || { log "Nothing to back up: set BACKUP_DATABASES and/or BACKUP_CATALOG_DB."; exit 2; }

mkdir -p "$run"
log "Backing up ${#databases[@]} database(s) to $run"

backup_one() {
  local db="$1" snapshot="" line
  local plain="$run/.$db.dump"
  # Hold a repeatable-read transaction open, export its snapshot, describe the data in it, and let pg_dump dump the
  # very same snapshot: the manifest then matches the dump exactly, even while users keep working.
  coproc PSQL { psql -X -q -A -t -F $'\t' -v ON_ERROR_STOP=1 -d "$db"; }
  printf '%s\n' "BEGIN ISOLATION LEVEL REPEATABLE READ READ ONLY;" "SELECT pg_export_snapshot();" '\echo __END__' >&"${PSQL[1]}"
  while IFS= read -r line <&"${PSQL[0]}"; do [[ "$line" == __END__ ]] && break; snapshot="$line"; done
  [[ -n "$snapshot" ]] || { log "$db: could not start a snapshot"; return 1; }
  { manifest_sql; printf '%s\n' '\echo __END__'; } >&"${PSQL[1]}"
  : > "$run/$db.manifest"
  while IFS= read -r line <&"${PSQL[0]}"; do [[ "$line" == __END__ ]] && break; printf '%s\n' "$line" >> "$run/$db.manifest"; done

  pg_dump --format=custom --no-owner --no-privileges --snapshot="$snapshot" --file="$plain" --dbname="$db"
  printf '%s\n' "COMMIT;" '\q' >&"${PSQL[1]}"
  wait "$PSQL_PID" 2>/dev/null || true
  pg_restore --list "$plain" > /dev/null   # The archive is readable before it is encrypted.

  if [[ ${#gpg_args[@]} -gt 0 ]]; then
    gpg "${gpg_args[@]}" --output "$run/$db.dump.gpg" "$plain"
    rm -f "$plain"
  else
    mv "$plain" "$run/$db.dump"
  fi
  log "$db: $(grep -c '^table' "$run/$db.manifest" || true) tables, trial balance $(awk -F'\t' '$1=="trial_balance" && $2=="balanced" {print $3}' "$run/$db.manifest")"
}

for db in "${databases[@]}"; do
  backup_one "$db"
done

(cd "$run" && sha256sum -- *.dump* *.manifest > SHA256SUMS)
date -u +%Y-%m-%dT%H:%M:%SZ > "$run/COMPLETE"
printf '%s\n' "$stamp" > "$backup_dir/LATEST"
log "Backup complete: $run"

# Retention: complete runs older than the retention period, always keeping the newest $min_keep complete runs.
mapfile -t complete < <(find "$backup_dir" -mindepth 2 -maxdepth 2 -name COMPLETE -printf '%h\n' | sort -r)
cutoff="$(date -u -d "-$retention_days days" +%Y%m%dT%H%M%SZ)"
for ((i = min_keep; i < ${#complete[@]}; i++)); do
  if [[ "$(basename "${complete[$i]}")" < "$cutoff" ]]; then
    rm -rf -- "${complete[$i]}"
    log "Retention: removed $(basename "${complete[$i]}")"
  fi
done
# Incomplete runs older than a day are failures already alerted on; remove them so they don't fill the disk.
find "$backup_dir" -mindepth 1 -maxdepth 1 -type d -name '20*' -mtime +1 ! -exec test -e '{}/COMPLETE' ';' -exec rm -rf -- '{}' + 2>/dev/null || true

notify ""
