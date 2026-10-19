# Backup job image: the PostgreSQL 17 client tools (pg_dump/pg_restore/psql match the server's major version) plus
# gpg for encryption. Runs scripts/backup-all.sh and scripts/restore-test.sh (mounted at /scripts).
FROM postgres:17
RUN apt-get update \
 && apt-get install -y --no-install-recommends gnupg ca-certificates \
 && rm -rf /var/lib/apt/lists/*
ENTRYPOINT []
