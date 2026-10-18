#!/usr/bin/env bash
# Backs up the Workbench database (ISO 27001 A.8.13). See docs/operations/backup-restore.md.
#
#   scripts/backup.sh sqlite <path/to/workbench.db> <backup-dir>
#   scripts/backup.sh postgres <connection-uri> <backup-dir>      # or set PGHOST/PGUSER/PGDATABASE and pass ""
#
# Writes <backup-dir>/workbench-<provider>-<UTC timestamp>.<ext> plus a .sha256 checksum, and prints the file name.
# Optional: BACKUP_GPG_RECIPIENT=<key id> encrypts the backup with gpg (recommended for off-site copies).
# Never put passwords on the command line; use PGPASSFILE / ~/.pgpass or a vault-injected PGPASSWORD.
set -euo pipefail

usage() { sed -n '2,9p' "$0" >&2; exit 2; }
[[ $# -eq 3 ]] || usage
provider="$1"; source="$2"; dir="$3"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -p "$dir"
umask 077

case "$provider" in
  sqlite)
    [[ -f "$source" ]] || { echo "No SQLite database at $source" >&2; exit 1; }
    out="$dir/workbench-sqlite-$stamp.db"
    # The online backup API copies a consistent snapshot while the app is running (WAL included).
    sqlite3 "$source" ".backup '$out'"
    result="$(sqlite3 "$out" 'PRAGMA integrity_check;')"
    [[ "$result" == "ok" ]] || { echo "Integrity check failed on $out: $result" >&2; exit 1; }
    rm -f "$out-wal" "$out-shm"
    ;;
  postgres|postgresql)
    out="$dir/workbench-postgres-$stamp.dump"
    # Custom format: compressed, and pg_restore can restore selectively and in parallel.
    if [[ -n "$source" ]]; then pg_dump --format=custom --no-owner --file="$out" "$source"; else pg_dump --format=custom --no-owner --file="$out"; fi
    pg_restore --list "$out" > /dev/null
    ;;
  *) usage ;;
esac

if [[ -n "${BACKUP_GPG_RECIPIENT:-}" ]]; then
  gpg --batch --yes --encrypt --recipient "$BACKUP_GPG_RECIPIENT" --output "$out.gpg" "$out"
  rm -f "$out"
  out="$out.gpg"
fi

(cd "$(dirname "$out")" && shasum -a 256 "$(basename "$out")" > "$(basename "$out").sha256")
echo "$out"
