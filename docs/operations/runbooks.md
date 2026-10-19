# Runbooks

Routine production procedures. Every change to production goes through change management (A.8.32): a ticket or pull
request saying what, why, who approved, and the result. Alert responses are in [alerts.md](alerts.md); incidents in
[incident-response.md](incident-response.md).

Commands use `docker compose -f docker-compose.prod.yml` (abbreviated `dc`); the Kubernetes equivalents are a Job from
the same image and `kubectl rollout`.

## Deploy a new version

Normally through `.github/workflows/deploy.yml` (staging, smoke test, approval, production). By hand:

1. Confirm the last backup is complete (`cat $BACKUP_DIR/LATEST`, then `ls $BACKUP_DIR/<stamp>/COMPLETE`).
2. `WORKBENCH_IMAGE=<registry>/workbench@sha256:<digest> dc pull web migrate`
3. `dc run --rm --no-deps migrate` and check it exits 0.
4. `dc up -d --no-build --wait web proxy`
5. `deploy/smoke-test.sh https://<host>`; watch errors and latency for 30 minutes.

## Roll back

- Redeploy the previous digest (`WORKBENCH_IMAGE=<previous> dc up -d --no-build --wait web`). Fine when the release had
  no migration or only additive ones.
- If a migration damaged data or the old version can't run on the new schema: stop the web hosts, restore to just
  before the migrate job with point-in-time recovery ([backup-restore.md](backup-restore.md#restoring)), deploy the
  previous digest. Record the data window lost.

## Restart

`dc restart web` (or `kubectl rollout restart deployment/workbench-web`). Users' circuits reconnect; unsaved edits in
an open dialog may be lost. Prefer rolling restarts with more than one replica.

## Scale out

Add replicas behind the proxy with sticky sessions (Blazor Server). Keys and sessions are already shared through the
database; the attachment volume must be shared storage. Size the database pool: `Maximum Pool Size` × replicas (×
tenants actively used, in MultiTenant mode, since each tenant database has its own pool) must stay under PostgreSQL's
`max_connections`; add PgBouncer (transaction mode) when it doesn't.

## Rotate a secret

| Secret | How | Downtime |
| --- | --- | --- |
| Database password | Add the new password (`ALTER ROLE workbench PASSWORD …` after updating the vault), update `ConnectionStrings:*`, roll out, then confirm nothing uses the old one | Rolling restart |
| OIDC client secret | Create a second secret at the IdP, update `Authentication:Oidc:ClientSecret`, roll out, delete the old one at the IdP | None |
| SCIM token | Generate (`openssl rand -base64 48`), set it in the IdP's provisioning config and `Scim:BearerToken` together, roll out | Provisioning pauses briefly |
| Email credentials / API key | New key at the provider, update, roll out, revoke the old | None |
| Key-encryption certificate or key | [data-protection-keys.md](data-protection-keys.md#rotation) (includes `--rewrap-data-protection-keys`) | Rolling restarts |
| Backup gpg key | New key pair; new public key to the job; keep the old private key while backups encrypted to it exist | None |

Rotate at least yearly and whenever someone with access leaves (`docs/iso/access-control-policy.md`).

## Tenants (MultiTenant)

- **Create**: `https://<BaseDomain>/host` → New tenant (as a `Tenancy:HostAdmins` user). It creates
  `workbench_<slug>`, migrates and seeds it, and invites the first Admin. Add the IdP redirect URI if your IdP has no
  wildcard. The next backup run includes it automatically.
- **Suspend / reactivate**: on `/host`. Suspended tenants get 403, background jobs skip them, data is kept and still
  backed up.
- **Restore one tenant**: [backup-restore.md](backup-restore.md#restoring), suspending the tenant during the restore.
- **Offboard** (contract ended): suspend; export what the contract requires; after the agreed retention, take a final
  encrypted backup, drop the database, remove the catalog row, and record it. Never reuse the slug.

## Database maintenance

- PostgreSQL minor upgrades: managed service maintenance window, or `docker compose pull postgres` + restart (minutes
  of downtime). Major upgrades (17 → 18): `pg_upgrade` or dump/restore into a new server, tested on a restored copy
  first; update the backup image's client version to match.
- Autovacuum is enough at Workbench's write volume; watch bloat on the security event, notification and activity
  tables (the retention job purges old security events and notifications).
- Failover (managed HA): automatic; connections drop and Npgsql reconnects. Check readiness afterwards.

## Re-run a background job

Jobs run on their schedule in every web host process (once per tenant); they are idempotent. The retention purge can be
run on demand from **Users and security → Retention**. Others catch up on their next scheduled run after the cause
is fixed.

## Restore test (quarterly)

[backup-restore.md](backup-restore.md#restore-test-scriptsrestore-testsh): run `scripts/restore-test.sh` against an
off-site copy on a non-production server, record date, backup, result and time in the restore test record.
