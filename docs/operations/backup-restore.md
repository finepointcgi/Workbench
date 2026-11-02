# Backup and restore

Procedure for ISO 27001 A.8.13 (information backup) and A.5.30 (ICT readiness for business continuity). The policy
(what, how often, how long, who tests) is in `docs/iso/backup-policy.md`; this page is the how-to. Scripts are in
`scripts/`; the backup job image is `deploy/backup/Dockerfile` (PostgreSQL 17 client tools plus gpg).

## Recovery objectives

| Deployment | RPO (data that may be lost) | RTO (time to be back) | How it is met |
| --- | --- | --- | --- |
| SaaS (MultiTenant), whole service | **15 minutes** | **4 hours** | Managed PostgreSQL PITR (WAL shipped continuously, 35 days) in a second zone; image redeployable from the registry; logical backups every 4 hours as the portable second copy |
| SaaS, one tenant restored (e.g. a customer's mistaken bulk change) | 15 minutes (PITR into a scratch server) or 4 hours (logical) | **2 hours** | Restore that tenant's database alone; no other tenant is touched |
| Self-hosted (SingleTenant), default | **4 hours** | **8 hours** | `scripts/backup-all.sh` every 4 hours, copied off-site; restore onto a rebuilt host |
| Self-hosted with WAL archiving | 15 minutes | 8 hours | pgBackRest or WAL-G (below) |
| Attachments (blob store) | 24 hours | 24 hours | Volume snapshots / object versioning, replicated |

These are the targets for `docs/iso/backup-policy.md` and the business continuity plan; record actual times in every
restore test. Measured locally (2026-10-18, dev-sized data): four databases restored and verified in 2 seconds, so
restore time is dominated by data size and by provisioning the replacement environment, not by the procedure.

## What to back up

| Item | Where | How |
| --- | --- | --- |
| Database(s): business data, users, security events, Data Protection keys | PostgreSQL: the SingleTenant database, or the catalog plus one database per tenant | `scripts/backup-all.sh` (logical, encrypted) **and** PITR |
| Attachments and receipts | The bucket (`Storage:Provider=Gcs`/`S3`), or `Storage:Root` (`/data/blobs` volume) | Bucket versioning or soft delete plus cross-region replication; volume snapshots for `Local` |
| Data Protection key-encryption certificate (or vault/KMS key) and its password | Your vault | Vault backup / escrow; **never in the same place as the database backups** |
| Backup decryption key (gpg private key) | Your vault, offline copy | Escrow; without it the logical backups are unreadable |
| Configuration and secrets | Deployment tooling and vault | Version-controlled deployment config; vault backup |

## Logical backups: `scripts/backup-all.sh`

One run backs up every Workbench database on the server into a time-stamped folder:

- **SingleTenant**: `BACKUP_DATABASES=workbench`.
- **MultiTenant**: `BACKUP_CATALOG_DB=workbench_catalog` (and `BACKUP_TENANT_DB_TEMPLATE` if tenant databases aren't
  named `workbench_{tenant}`): the catalog plus every Active and Suspended tenant listed in it, each in its own
  dump, so one tenant can be restored without touching the others.

For each database it opens a repeatable-read transaction, exports its snapshot, records a **manifest** (every table's
row count and a SHA-256 fingerprint of the trial balance: each GL account's posted debits and credits, plus whether
debits equal credits), and runs `pg_dump --snapshot` on that very snapshot, so the manifest describes the dump
exactly even while people keep working. The dump is checked with `pg_restore --list`, then **encrypted with gpg to a
public key** (the server never holds the private key) and the plain file removed. `SHA256SUMS` covers every file, and
`COMPLETE` is written last; a folder without it is a failed run and is never used. Runs older than
`BACKUP_RETENTION_DAYS` (35) are deleted, always keeping the newest `BACKUP_MIN_KEEP` (7). It exits non-zero on any
failure and can ping a dead-man switch (`BACKUP_NOTIFY_URL`).

```bash
# Compose (docker-compose.prod.yml): from cron or a systemd timer, every 4 hours
docker compose -f docker-compose.prod.yml --profile backup run --rm backup

# Anywhere with the PostgreSQL 17 client tools and gpg (credentials from PGPASSFILE / a vault-injected PGPASSWORD)
PGHOST=db.internal PGUSER=workbench PGSSLMODE=verify-full BACKUP_DIR=/var/backups/workbench \
  BACKUP_CATALOG_DB=workbench_catalog BACKUP_GPG_RECIPIENT_FILE=/etc/workbench/backup_public_key.asc \
  scripts/backup-all.sh
```

Then **copy each run off the host** to storage in a second location with object lock / immutability (S3 Object
Lock, Azure immutable blob storage) for at least the retention period, e.g. `aws s3 sync /var/backups/workbench
s3://…/workbench/ --exclude '*/.*'`. Monthly and yearly copies for longer retention (`docs/iso/backup-policy.md`) are
kept by lifecycle rules on that storage. The backup's database login needs only `CONNECT` and `SELECT` (use a
read-only role, e.g. `pg_read_all_data`).

## Point-in-time recovery (PITR)

Logical dumps bound the RPO by their schedule. Continuous WAL archiving gets it down to minutes and lets you restore to
just before a bad change or a bad migration.

- **Managed PostgreSQL** (Azure Database for PostgreSQL Flexible Server, Amazon RDS/Aurora, Cloud SQL): turn on
  automated backups with 35-day retention and geo-redundant backup storage. A point-in-time restore creates a **new
  server**; point Workbench at it (connection strings), or copy the one tenant's database out of it with
  `pg_dump`/`pg_restore` for a single-tenant restore. Test it as part of the quarterly restore test.
- **Self-managed PostgreSQL**: use pgBackRest or WAL-G. In outline (pgBackRest):

  ```
  # postgresql.conf
  wal_level = replica
  archive_mode = on
  archive_command = 'pgbackrest --stanza=workbench archive-push %p'
  archive_timeout = 300          # at most 5 minutes of WAL in flight
  ```

  with a full backup weekly and differentials daily (`pgbackrest --stanza=workbench --type=full backup`) to an
  encrypted (`repo1-cipher-type=aes-256-cbc`) repository on object storage. Restore to a time with
  `pgbackrest --stanza=workbench --delta --type=time "--target=2026-10-18 14:05:00+00" restore` on a stopped,
  separate server, then start it and verify before switching over.
- Keep the logical backups as well: they are portable across versions and providers, can restore a single tenant,
  and are what the restore test verifies.

## Restoring

1. Declare the restore in the change or incident record (who, why, which backup or point in time).
2. Stop the web hosts (scale to zero) so nothing writes during the restore. For a single tenant in MultiTenant mode,
   suspend that tenant on `/host` instead.
3. Restore into a **new, empty** database and switch to it, rather than overwriting the old one (keep it until the
   restore is accepted):

   ```bash
   gpg --decrypt /restore/20261018T120000Z/workbench_acme.dump.gpg > /tmp/workbench_acme.dump   # private key from the vault
   createdb workbench_acme_restored
   pg_restore --exit-on-error --no-owner --no-privileges --dbname=workbench_acme_restored /tmp/workbench_acme.dump
   shred -u /tmp/workbench_acme.dump
   ```

   Then rename databases (`ALTER DATABASE workbench_acme RENAME TO workbench_acme_old;` and the restored one to
   `workbench_acme`) so the tenant's connection string still matches; SingleTenant can instead change
   `ConnectionStrings:Workbench`. Restoring the **catalog** restores the shared Data Protection key ring and the
   tenant list; restore it when it was lost, not to undo a tenant's change.
4. Run the migrate job (a backup from an older version is brought up to date), restore attachments to the same point
   in time, start the web hosts (or reactivate the tenant).
5. Check `GET /health/ready` (`database` and `catalog` `Healthy`), sign in as an administrator, open a timesheet, a
   project's financials, the trial balance and **Users and security → Security events**. People whose sessions are
   newer than the restored key ring sign in again.
6. Record the result, the time taken (against the RTO) and the data lost (against the RPO).

`scripts/restore.sh` (single file, SQLite or PostgreSQL) still works for the single-file backups made by
`scripts/backup.sh`, which remains for SQLite evaluation installs.

## Restore test: `scripts/restore-test.sh`

Restores every database of a backup run into scratch databases (`restoretest_<db>_<time>`) on the server `PGHOST`
points at, then compares each restored database with the manifest recorded at backup time: **every table's row count
and the trial balance fingerprint must match exactly**, and the trial balance must balance. It checks `SHA256SUMS`
first (an altered or damaged backup is refused), prints how long it took, drops the scratch databases and exits
non-zero on any mismatch.

```bash
# Against the newest complete run in BACKUP_DIR, on a non-production server, with the private key from the vault
BACKUP_GPG_PRIVATE_KEY_FILE=/vault/backup_private_key.asc PGHOST=restore-test.internal scripts/restore-test.sh

# Compose
docker compose -f docker-compose.prod.yml --profile backup run --rm \
  -e BACKUP_GPG_PRIVATE_KEY_FILE=/keys/private.asc -v /vault/backup_private_key.asc:/keys/private.asc:ro \
  backup /scripts/restore-test.sh
```

**Locally / in CI**: `scripts/test-backup-restore.sh [postgres-container]` runs the whole thing with Docker only:
builds the backup image, makes a throwaway key, backs up (MultiTenant when a `workbench_catalog` database exists),
runs the restore test, and confirms that a wrong manifest and an altered backup are both caught. CI runs it against
the production compose stack on every pull request.

Run the restore test **at least quarterly in each production environment** (and after changing the backup setup),
using a real off-site copy, and keep its output as evidence (`docs/iso/backup-policy.md`).

### Restore test record

| Date | Environment | Backup | Result | Time | Notes |
| --- | --- | --- | --- | --- | --- |
| 2026-10-18 | Local, PostgreSQL 17 (Docker), MultiTenant dev data | `scripts/test-backup-restore.sh` | Passed: catalog, `workbench_acme` (87 tables, 779 rows), `workbench_beta` (775 rows), `workbench`; trial balances identical and balanced. Wrong manifest → FAIL as expected; altered file → checksum refused | 2 s | Scratch databases dropped |
| 2026-10-18 | Local `docker-compose.prod.yml` stack | compose `backup` job, then `restore-test.sh` with the vault key | Passed: `workbench` 165 tables, trial balance identical | 1 s | |
| 2026-10-18 | Development SQLite | `scripts/backup.sh sqlite` / `restore.sh` | Passed: row counts identical, `integrity_check` ok, tampered file refused, host started on the restored file | — | Earlier procedure, SQLite evaluation installs |

## Alerting

Alert when a backup run fails or no complete run appears for longer than its schedule plus an hour (the dead-man
switch, or the age of `BACKUP_DIR/LATEST`), and when the off-site copy fails. See [Alerts and runbook](alerts.md).
