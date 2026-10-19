#!/usr/bin/env bash
# Restore test (ISO 27001 A.8.13): restores every database of a backup run into scratch databases, checks that each
# table's row count and the trial balance match what the backup recorded, then drops the scratch databases. Prints
# how long it took (evidence against the RTO). Run it at least quarterly per environment and keep the output.
#
#   BACKUP_GPG_PRIVATE_KEY_FILE=/vault/backup_private_key.asc scripts/restore-test.sh [run-folder]
#
# run-folder defaults to the newest complete run in BACKUP_DIR (default /backups). The scratch databases are created
# on the server PGHOST/PGUSER point at (the account needs CREATEDB): use a separate server, never production, unless
# you mean to. RESTORE_TEST_KEEP=1 keeps them for inspection. Exits non-zero on any mismatch.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
# shellcheck source=lib/pg-manifest.sh
source "$here/lib/pg-manifest.sh"

backup_dir="${BACKUP_DIR:-/backups}"
run="${1:-}"
if [[ -z "$run" ]]; then
  run="$(find "$backup_dir" -mindepth 2 -maxdepth 2 -name COMPLETE -printf '%h\n' | sort -r | head -n 1)"
fi
[[ -n "$run" && -f "$run/COMPLETE" ]] || { log "No complete backup run found (${run:-$backup_dir})."; exit 1; }

started=$(date +%s)
work="$(mktemp -d)"
scratch=()
cleanup() {
  if [[ "${RESTORE_TEST_KEEP:-}" != "1" ]]; then
    for db in "${scratch[@]}"; do dropdb --if-exists "$db" 2>/dev/null || true; done
  fi
  rm -rf "$work"
}
trap cleanup EXIT

log "Restore test of $run"
(cd "$run" && sha256sum --quiet -c SHA256SUMS) || { log "Checksums don't match: the backup was altered or damaged."; exit 1; }

if [[ -n "${BACKUP_GPG_PRIVATE_KEY_FILE:-}" ]]; then
  export GNUPGHOME="$work/gnupg"
  mkdir -m 700 "$GNUPGHOME"
  gpg --batch --quiet --import "$BACKUP_GPG_PRIVATE_KEY_FILE"
fi

failures=0
suffix="$(date -u +%Y%m%d%H%M%S)"
for manifest in "$run"/*.manifest; do
  db="$(basename "$manifest" .manifest)"
  if [[ -f "$run/$db.dump.gpg" ]]; then
    gpg --batch --quiet --decrypt --output "$work/$db.dump" "$run/$db.dump.gpg"
  else
    cp "$run/$db.dump" "$work/$db.dump"
  fi

  target="restoretest_${db:0:30}_$suffix"
  scratch+=("$target")
  createdb "$target"
  pg_restore --exit-on-error --no-owner --no-privileges --dbname="$target" "$work/$db.dump"
  rm -f "$work/$db.dump"

  manifest_sql | psql -X -q -A -t -F $'\t' -v ON_ERROR_STOP=1 -d "$target" > "$work/$db.restored"
  if diff -u "$manifest" "$work/$db.restored" > "$work/$db.diff"; then
    tables=$(grep -c '^table' "$manifest" || true)
    rows=$(awk -F'\t' '$1=="table" {s+=$3} END {print s+0}' "$manifest")
    balanced=$(awk -F'\t' '$1=="trial_balance" && $2=="balanced" {print $3}' "$manifest")
    if [[ "$balanced" == "false" ]]; then
      log "FAIL $db: restored as backed up, but the trial balance doesn't balance (it didn't at backup time either)."
      failures=$((failures + 1))
    else
      log "OK   $db: $tables tables, $rows rows identical; trial balance identical (balanced: $balanced)"
    fi
  else
    log "FAIL $db: the restored database differs from the backup manifest:"
    cat "$work/$db.diff" >&2
    failures=$((failures + 1))
  fi
done

elapsed=$(( $(date +%s) - started ))
if [[ $failures -gt 0 ]]; then
  log "Restore test FAILED for $failures database(s) after ${elapsed}s."
  exit 1
fi
log "Restore test passed: $(ls "$run"/*.manifest | wc -l) database(s) restored and verified in ${elapsed}s (backup taken $(cat "$run/COMPLETE"))."
