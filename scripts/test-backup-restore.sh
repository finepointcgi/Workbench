#!/usr/bin/env bash
# End-to-end backup and restore test against a local PostgreSQL container (docker compose up -d, or the
# docker-compose.prod.yml one). Builds the backup image, makes a throwaway gpg key, runs scripts/backup-all.sh,
# then scripts/restore-test.sh (restore into scratch databases, compare row counts and the trial balance), and
# finally checks that an altered backup and a wrong manifest are both caught. Needs only Docker.
#
#   scripts/test-backup-restore.sh [postgres-container] [databases...]
#
# Defaults: the container of `docker compose ps -q postgres`; databases "workbench", plus the catalog and tenants
# when a workbench_catalog database exists. PGUSER/PGPASSWORD default to the development workbench/workbench.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
container="${1:-$(cd "$root" && docker compose ps -q postgres)}"
[[ -n "$container" ]] || { echo "No PostgreSQL container: run 'docker compose up -d' or pass a container name." >&2; exit 1; }
shift || true
work="$(mktemp -d)"
# The backup container runs as root with umask 077, so on a Linux host the backup folders it writes belong to root and
# only root can read them. Everything that touches them goes through the container, and cleanup hands them back first.
cleanup() {
  docker run --rm -v "$work:/work" --entrypoint chown workbench-backup:local -R "$(id -u):$(id -g)" /work > /dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT
mkdir -p "$work/keys" "$work/backups"

docker build -q -t workbench-backup:local "$root/deploy/backup" > /dev/null
tools() {
  docker run --rm --network "container:$container" \
    -e PGHOST=localhost -e PGUSER="${PGUSER:-workbench}" -e PGPASSWORD="${PGPASSWORD:-workbench}" -e BACKUP_DIR=/backups \
    -e BACKUP_GPG_RECIPIENT_FILE=/keys/public.asc -e BACKUP_GPG_PRIVATE_KEY_FILE=/keys/private.asc "$@" \
    -v "$work/keys:/keys" -v "$work/backups:/backups" -v "$root/scripts:/scripts:ro" workbench-backup:local "${cmd[@]}"
}

cmd=(bash -c 'export GNUPGHOME=$(mktemp -d); gpg --batch --pinentry-mode loopback --passphrase "" --quick-gen-key "Restore test <restore-test@workbench.invalid>" rsa3072 encr 1d 2>/dev/null
  gpg --batch --armor --export > /keys/public.asc; gpg --batch --armor --export-secret-keys > /keys/private.asc')
tools

databases="${*:-workbench}"
catalog=""
if docker exec "$container" psql -U "${PGUSER:-workbench}" -Atc "SELECT 1 FROM pg_database WHERE datname = 'workbench_catalog'" | grep -q 1; then
  catalog=workbench_catalog
fi

echo "== Backup"
cmd=(/scripts/backup-all.sh)
tools -e BACKUP_DATABASES="$databases" -e BACKUP_CATALOG_DB="$catalog"
runs=("$work"/backups/20*)
run="$(basename "${runs[${#runs[@]}-1]}")"

echo "== Restore test"
cmd=(/scripts/restore-test.sh "/backups/$run")
tools

echo "== A manifest that doesn't match the data is caught"
cmd=(bash -c 'set -e; cp -R "/backups/$0" /backups/19990101T000000Z; cd /backups/19990101T000000Z
  first="$(grep -l "^table" *.manifest | head -n 1)"
  awk -F"\t" "BEGIN { OFS = \"\t\" } \$1 == \"table\" && !done { \$3 = \$3 + 1; done = 1 } 1" "$first" > "$first.new" && mv "$first.new" "$first"
  sha256sum -- *.dump* *.manifest > SHA256SUMS' "$run")
tools
cmd=(/scripts/restore-test.sh /backups/19990101T000000Z)
if tools; then echo "FAILED: a wrong manifest passed" >&2; exit 1; fi

echo "== An altered backup file is refused"
cmd=(bash -c 'set -e; dump="$(ls /backups/19990101T000000Z/*.dump* | head -n 1)"; printf x >> "$dump"')
tools
cmd=(/scripts/restore-test.sh /backups/19990101T000000Z)
if tools; then echo "FAILED: an altered backup passed" >&2; exit 1; fi

echo "== Backup and restore test passed"
