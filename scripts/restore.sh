#!/usr/bin/env bash
# Restores a Workbench backup made by scripts/backup.sh (ISO 27001 A.8.13). See docs/operations/backup-restore.md.
#
#   scripts/restore.sh sqlite <backup-file> <path/to/workbench.db>
#   scripts/restore.sh postgres <backup-file> <connection-uri>     # target database must exist and be empty
#
# Stop the Workbench web host and worker first. The checksum is verified before anything is changed; an existing
# SQLite file is kept as <file>.pre-restore-<timestamp>. Encrypted (.gpg) backups are decrypted with gpg.
set -euo pipefail

usage() { sed -n '2,8p' "$0" >&2; exit 2; }
[[ $# -eq 3 ]] || usage
provider="$1"; backup="$2"; target="$3"
[[ -f "$backup" ]] || { echo "No backup at $backup" >&2; exit 1; }

if [[ -f "$backup.sha256" ]]; then
  (cd "$(dirname "$backup")" && shasum -a 256 -c "$(basename "$backup").sha256" > /dev/null) || { echo "Checksum mismatch for $backup" >&2; exit 1; }
else
  echo "Warning: no checksum file next to $backup" >&2
fi

work="$backup"
if [[ "$backup" == *.gpg ]]; then
  work="$(mktemp)"
  trap 'rm -f "$work"' EXIT
  gpg --batch --yes --decrypt --output "$work" "$backup"
fi

case "$provider" in
  sqlite)
    result="$(sqlite3 "$work" 'PRAGMA integrity_check;')"
    [[ "$result" == "ok" ]] || { echo "Backup failed its integrity check: $result" >&2; exit 1; }
    if [[ -f "$target" ]]; then
      keep="$target.pre-restore-$(date -u +%Y%m%dT%H%M%SZ)"
      mv "$target" "$keep"
      rm -f "$target-wal" "$target-shm"
      echo "Kept the previous database as $keep" >&2
    fi
    sqlite3 "$work" ".backup '$target'"
    rm -f "$work-wal" "$work-shm"
    ;;
  postgres|postgresql)
    pg_restore --exit-on-error --no-owner --dbname="$target" "$work"
    ;;
  *) usage ;;
esac

echo "Restored $backup to $target"
