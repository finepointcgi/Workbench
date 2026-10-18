# Backup and restore

Procedure for ISO 27001 A.8.13 (information backup). The policy (what, how often, how long, who tests) is in
`docs/iso/backup-policy.md`; this page is the how-to. Scripts are in `scripts/`.

## What to back up

| Item | Where | How |
| --- | --- | --- |
| Database (all business data, users, security events, Data Protection keys) | SQLite file or PostgreSQL database | `scripts/backup.sh` |
| Attachments and receipts (blob store) | `Storage:Root` folder, or your object store | Folder snapshot / object-store versioning and replication |
| Data Protection certificate (PFX) and its password | Your vault | Vault backup; **keep it separate from database backups** |
| Configuration and secrets | Your deployment tooling and vault | Version-controlled deployment config; vault backup |

## Backing up

```bash
# PostgreSQL (production). Credentials come from ~/.pgpass, PGPASSFILE or a vault-injected PGPASSWORD.
scripts/backup.sh postgres "postgresql://workbench@db.internal:5432/workbench?sslmode=verify-full" /var/backups/workbench

# SQLite (evaluation and development). Safe while the app runs: uses SQLite's online backup API.
scripts/backup.sh sqlite src/Workbench.Web/workbench.db /var/backups/workbench
```

Each run writes `workbench-<provider>-<UTC timestamp>.<db|dump>` and a `.sha256` checksum, checks the backup
(SQLite `integrity_check`, or `pg_restore --list`), and prints the file name. Set `BACKUP_GPG_RECIPIENT` to encrypt
with gpg before the file leaves the server. Files are created with mode 0600.

Schedule it (cron, systemd timer, or your platform's job runner), copy the files off the host to immutable or
versioned storage in a second location, and alert when a run fails. Managed PostgreSQL (Azure Database for
PostgreSQL, Amazon RDS) also has point-in-time restore; keep logical backups from this script as well, because they
are portable and can be restored into a fresh environment.

## Restoring

1. Declare the restore in the change or incident record (who, why, which backup).
2. Stop the Workbench web host and worker so nothing writes during the restore.
3. Restore:

   ```bash
   # SQLite: the current file is kept as workbench.db.pre-restore-<timestamp>
   scripts/restore.sh sqlite /var/backups/workbench/workbench-sqlite-20261018T021220Z.db src/Workbench.Web/workbench.db

   # PostgreSQL: into a new, empty database (createdb workbench_restore), then switch the connection string
   scripts/restore.sh postgres /var/backups/workbench/workbench-postgres-20261018T021220Z.dump "postgresql://workbench@db.internal:5432/workbench_restore"
   ```

   The script verifies the checksum first and refuses a tampered or corrupt backup.
4. Restore the blob store to the same point in time.
5. Start Workbench and check `GET /health/ready` returns `Healthy`.
6. Sign in as an administrator; open a timesheet, a project's financials and **Users and security → Security events**.
   Users who signed in after the backup must sign in again if their sessions are older than the restored key ring.
7. Record the result, the time taken (against the RTO) and the data lost (against the RPO).

## Tested restore (record of the procedure being run)

Run on 2026-10-18 against a development database while the web host was running:

| Step | Result |
| --- | --- |
| `scripts/backup.sh sqlite src/Workbench.Web/workbench.db <dir>` | Backup + checksum written; integrity check `ok` |
| `scripts/restore.sh sqlite <backup> <new path>` | Restored |
| Restore again over the existing file | Previous file kept as `.pre-restore-<timestamp>` |
| Row counts, live vs restored (users, security events, sheets, time entries, timesheets, Data Protection keys) | Identical (4/4, 1/1, 2/2, 125/125, 15/15, 1/1) |
| `PRAGMA integrity_check` on the restored file | `ok` |
| Restore of a backup with one byte appended | Refused: checksum mismatch, nothing changed |
| Web host started on the restored database; `/health/ready` | `Healthy`; admin sign-in and users page load |

The company must repeat a restore test at least quarterly in each production environment (PostgreSQL, full
blob store) and keep the record as evidence; see `docs/iso/backup-policy.md`.
