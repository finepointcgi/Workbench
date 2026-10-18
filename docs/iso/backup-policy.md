# Backup policy

| | |
| --- | --- |
| Owner | `<Operations lead>` |
| Approved by | `<ISMS manager>` |
| Version / date | `<1.0>` / `<date>` |
| Controls | A.8.13, A.5.30, A.8.14 |
| Procedure | `../operations/backup-restore.md` |

## 1. Targets

| System | RPO (max data loss) | RTO (max downtime) | Why |
| --- | --- | --- | --- |
| Workbench database | `<4 hours>` | `<8 hours>` | Daily time entry; payroll and billing cycles |
| Attachments / receipts (blob store) | `<24 hours>` | `<24 hours>` | Receipts needed for expense approval |
| Configuration and secrets | On change | `<4 hours>` | Needed to rebuild |

## 2. What, how often, how long

| Item | Method | Frequency | Retention | Location |
| --- | --- | --- | --- | --- |
| PostgreSQL database | `scripts/backup.sh postgres` (logical), plus provider point-in-time restore | `<every 4 hours>` logical; continuous PITR | `<35 days daily, 12 monthly, 7 yearly>` | `<primary region>` + `<second region, immutable/WORM>` |
| Blob store | Object versioning + cross-region replication / folder snapshots | Continuous / daily | Same as database | `<second region>` |
| Data Protection certificate | Vault backup | On change | Until superseded + 1 year | Vault |
| Deployment configuration | Version control | On change | Indefinite | `<repo>` |

Retention must not be shorter than the records retention rules (protected records: at least 3 years after contract
close; see Workbench **Retention**). Backups older than their retention are destroyed securely (A.8.10).

## 3. Protection

- Backups are encrypted (`BACKUP_GPG_RECIPIENT`, or storage-level encryption with keys in `<KMS>`), checksummed,
  and created with owner-only permissions.
- At least one copy is offline or immutable, so ransomware with production credentials can't delete it.
- Access to backups is limited to `<role>` and reviewed with access reviews.

## 4. Monitoring

Every backup job reports success or failure to `<monitoring/alerting>`. A failed backup is investigated the same
business day. `<Owner>` checks the backup report `<weekly>`.

## 5. Restore testing

- `<Quarterly>`, restore the latest production backup into an isolated environment following
  `../operations/backup-restore.md`, start Workbench against it, check `/health/ready`, and spot-check timesheets,
  financials and the security event log.
- Record: date, backup used, who, time taken (vs RTO), data age (vs RPO), problems, actions. Keep records as audit
  evidence.
- At least yearly, run a full disaster recovery exercise from the second location (see `business-continuity-plan.md`).

## 6. Restore test log

| Date | Environment | Backup | Performed by | Time to restore | Result | Actions |
| --- | --- | --- | --- | --- | --- | --- |
| 2026-10-18 | Development (SQLite) | `workbench-sqlite-20261018T…` | Engineering | < 1 min | Pass: counts match, integrity ok, app healthy | Repeat on PostgreSQL production copy |
| `<date>` | | | | | | |
