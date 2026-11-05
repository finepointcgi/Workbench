# Deployment guide

## Shape of a deployment

```
            HTTPS                    HTTP :8080                 TLS (verify-full when off-host)
 users ───────────▶ reverse proxy ─────────────▶ web host(s) ─────────────────────────▶ PostgreSQL 17
                   (TLS, HSTS by app)          (stateless,                               ├─ workbench          (SingleTenant)
                                                read-only root,                          └─ workbench_catalog  (MultiTenant)
                                                volume /data/blobs)                         + workbench_<tenant> …
 deploy ──▶ migrate job (same image, --migrate) ──┘
```

- **One image** (`Dockerfile`): .NET 10 on Ubuntu chiseled (no shell, no package manager), runs as the non-root
  `app` user (UID 1654), listens on 8080, has a `HEALTHCHECK`, and works with a read-only root filesystem
  (mount `/tmp` as tmpfs and a volume at `/data/blobs`).
- **Web hosts are stateless** once files are in a bucket (`Storage:Provider=Gcs` or `S3`, [Object storage for files](object-storage.md)); with the default `Local` they're in `/data/blobs`. Data Protection keys,
  sessions and everything else are in PostgreSQL, so you can run several replicas behind the proxy. Blazor Server
  keeps a WebSocket per user: enable **sticky sessions** (cookie or source-IP affinity) on the load balancer when
  running more than one replica, and allow WebSockets with an idle timeout of at least 100 seconds.
- **Files go in a bucket when there's more than one replica**: Google Cloud Storage or S3/MinIO, private, encrypted,
  with the platform's identity (Cloud Run service account, IAM role) instead of keys. `--copy-blobs` moves an existing
  `/data/blobs` folder into the bucket ([Object storage for files](object-storage.md)).
- **Migrations are a separate step**: `Database:MigrateOnStartup` is false outside Development, so a deploy runs the
  image once with `--migrate` (a Kubernetes Job, an ECS task, a compose one-shot service) and only then rolls out the
  web hosts. For zero-downtime rollouts, migrations must leave the schema usable by the previous version
  (expand, deploy, then contract in a later release); otherwise stop the web hosts while the job runs.
- **TLS terminates at the proxy.** The app refuses to start in Production on plain HTTP unless
  `Security:TlsTerminatedByProxy=true`, and then only trusts forwarded headers from `ForwardedHeaders:KnownProxies`
  or `ForwardedHeaders:KnownNetworks`.

## Before the first deployment

1. **PostgreSQL 17** (managed: Azure Database for PostgreSQL Flexible Server, Amazon RDS/Aurora, Cloud SQL; or your
   own). Turn on point-in-time recovery (see [Backup and restore](backup-restore.md)). Create a `workbench` login.
   SingleTenant: one database it owns. MultiTenant: the catalog database, and give the login `CREATEDB` so tenant
   provisioning can create `workbench_<tenant>` databases.
2. **An OIDC identity provider** (Entra ID, Okta, Keycloak, Login.gov for some users): register the app with redirect
   URI `https://<host>/signin-oidc` (MultiTenant: one per tenant subdomain, or a wildcard if the IdP allows it).
3. **Key encryption** for the Data Protection key ring: a certificate, an Azure Key Vault key or an AWS KMS key.
   Production refuses to start without one. See [Data Protection keys](data-protection-keys.md).
4. **Email**: SMTP relay with TLS, SendGrid or SES ([Email](email.md)), and SPF/DKIM/DMARC for the sender domain.
5. **Secrets** in your vault, delivered as environment variables or files (below): the database connection string,
   the OIDC client secret, the SCIM token, the key-encryption certificate password (or none with Key Vault/KMS), email
   credentials, the OTLP header if your telemetry backend needs one.
6. **DNS and certificates**: SingleTenant `workbench.example.com`. MultiTenant: the base domain and `*.base-domain`;
   a wildcard certificate needs a DNS-01 ACME challenge (Caddy and cert-manager both support it with a DNS-provider
   plugin) or a certificate from your CA.
7. **Telemetry**: an OpenTelemetry Collector (or a vendor OTLP endpoint) and the alerts in [Alerts and runbook](alerts.md).
8. **Backups**: the backup job, an off-site immutable bucket, and a scheduled restore test.

## Configuration and secrets

Configuration is ASP.NET Core's: `appsettings.json`, then environment variables (`Section__Key`), then the command
line. Every key is in [Configuration reference](configuration.md). Never bake secrets into the image.

**Secret files.** Set `SecretsDirectory` (the image sets `/run/secrets`) and mount one file per key whose name is
the configuration key with `__` for `:` — `ConnectionStrings__Workbench`, `Scim__BearerToken`,
`Authentication__Oidc__ClientSecret`, `DataProtection__CertificatePassword`. This is how Docker/Compose secrets,
Kubernetes secret volumes and the Vault agent work. Files whose names contain a dot (`dataprotection.pfx`) are not
read as configuration, so a certificate can sit in the same folder. Secret files override environment variables.

## Example: one Docker host (`docker-compose.prod.yml`)

Suitable for a self-hosted install or a small staging environment. It runs PostgreSQL 17 with data checksums, the
migrate job, the web host (read-only root, all capabilities dropped, `no-new-privileges`) and Caddy, which gets and
renews certificates.

```bash
scripts/prod/init-secrets.sh          # ./secrets: DB password, SCIM token, key-encryption certificate, backup key pair
```

```bash
cat > .env <<'EOF'
WORKBENCH_DOMAIN=workbench.example.com
WORKBENCH_TLS=admin@example.com        # ACME account email; "internal" for a local CA while testing
OIDC_AUTHORITY=https://login.microsoftonline.com/<tenant-id>/v2.0
OIDC_CLIENT_ID=<client id>
WORKBENCH_BOOTSTRAP_ADMIN=you@example.com
EMAIL_MODE=Smtp
SMTP_HOST=smtp.example.com
EMAIL_FROM=workbench@example.com
OTEL_ENDPOINT=http://otel-collector:4317
EOF
```

```bash
docker compose -f docker-compose.prod.yml up -d --build --wait
```

Then put the IdP client secret in `secrets/Authentication__Oidc__ClientSecret`, move
`secrets/backup_private_key.asc` into your vault, escrow `secrets/dataprotection.pfx` with its password, and
schedule the backup job (`docker compose -f docker-compose.prod.yml --profile backup run --rm backup`) from cron or a
systemd timer. `docker compose ps` shows the migrate job `Exited (0)` and the web host `healthy`.

Verified locally on 2026-10-18 (Docker 29, Apple silicon): the stack starts, the migrate job applies every migration
and exits 0, `https://localhost/health/ready` answers through Caddy, redirects carry `https://` (forwarded headers are
trusted from Caddy only), the key ring in `DataProtectionKeys` is stored as `encryptedSecret` (no plain key), and
`--rewrap-data-protection-keys`, the backup job and `scripts/restore-test.sh` all succeed against it.

## Example: Kubernetes (any provider)

- `Deployment workbench-web`: the image, `envFrom` a ConfigMap (`workbench-config`) and a Secret
  (`workbench-secrets`), or mount the secret as a volume at `/run/secrets`. `securityContext`: `runAsNonRoot: true`,
  `readOnlyRootFilesystem: true`, `allowPrivilegeEscalation: false`, `capabilities.drop: [ALL]`; `emptyDir` at `/tmp`;
  a ReadWriteMany volume at `/data/blobs`. Probes: `livenessProbe` `GET /health/live`, `readinessProbe`
  `GET /health/ready` (degraded still returns 200), `startupProbe` on `/health/live` with 60 s allowance.
  Resources: start at 0.5 CPU / 1 GiB request, 2 GiB limit per replica; set `DOTNET_GCHeapHardLimitPercent=75` if
  you lower the limit.
- Ingress: TLS, WebSockets on, cookie affinity for Blazor (`nginx.ingress.kubernetes.io/affinity: cookie`), and
  `ForwardedHeaders__KnownNetworks__0` set to the ingress controller's pod CIDR.
- `Job workbench-migrate-<version>` from the same image with args `["--migrate"]` and `backoffLimit: 0`, run and
  waited for before `kubectl set image` (`deploy/deploy.sh kubernetes` does exactly this).
- `CronJob workbench-backup` from `deploy/backup/Dockerfile` running `scripts/backup-all.sh` (or rely on the managed
  database's backups plus a weekly logical export).
- Azure Container Apps and Amazon ECS map the same way: an app/service for the web host, a job/one-off task for
  `--migrate`, secrets from Key Vault or Secrets Manager as environment variables.

## CI/CD

- `.github/workflows/ci.yml` builds and tests on every pull request on InMemory, SQLite and PostgreSQL 17, checks
  migrations match the model, scans dependencies and secrets, and builds the image, starts the production compose
  stack, checks readiness through TLS, runs the backup/restore test, scans the image with Trivy (fails on fixable
  High/Critical) and publishes CycloneDX SBOMs for the solution and the image.
- `.github/workflows/deploy.yml` (a template) builds the image once, scans it, pushes it to GHCR with an SBOM and
  build provenance, then deploys **the same digest** to the `staging` environment, runs `deploy/smoke-test.sh`, and
  waits for approval on the `production` environment (configure *Required reviewers* there) before doing the same.
  `deploy/deploy.sh` holds the provider-specific part (`compose-ssh` and `kubernetes` today).

## Upgrades

1. Read the release notes for configuration changes; check `docs/operations/configuration.md`.
2. Make sure last night's backup succeeded (or take one: [Backup and restore](backup-restore.md)). PITR covers the rest.
3. Run the migrate job with the new image and wait for it to finish. It logs each migration it applies. On first
   install EF Core logs one error for the missing `__EFMigrationsHistory` table before creating it; that is expected.
4. Roll out the web hosts (rolling update). Watch `/health/ready`, the error rate and the job-failure metric.
5. Run `deploy/smoke-test.sh https://<host>`.

## Rolling back

- **Web host only** (no migration in the release, or a backward-compatible one): redeploy the previous image digest.
- **A migration that must be undone**: restore the database to just before the migrate job with point-in-time
  recovery, then deploy the previous image. Workbench does not run down-migrations in production. Users lose what they
  entered since the job ran, so prefer fixing forward unless the data is wrong.

## Hardening checklist

- [ ] `ASPNETCORE_ENVIRONMENT=Production` (the image's default); startup check passes (the host lists every problem otherwise)
- [ ] Key ring encrypted (certificate, Key Vault or KMS) and the certificate/key escrowed
- [ ] Database over TLS (`SSL Mode=VerifyFull` in the connection string) unless it is on a private network in the same host
- [ ] Only the proxy's address in `ForwardedHeaders:KnownProxies`/`KnownNetworks`
- [ ] Container: non-root, read-only root, capabilities dropped, no host networking, image scanned and pinned by digest
- [ ] Telemetry flowing and alerts from [Alerts and runbook](alerts.md) enabled; logs shipped off the host and retained ≥ 1 year
- [ ] Backups encrypted, copied off-site to immutable storage, restore test scheduled
- [ ] Email over TLS with SPF, DKIM and DMARC

## Known messages

- `Error: libgssapi_krb5.so.2: cannot open shared object file` once at startup: Npgsql probes for Kerberos, which the
  chiseled image doesn't include. Harmless unless you use Kerberos (GSSAPI) database authentication, which Workbench
  doesn't support.
- `Failed to determine the https port for redirect` (once): HTTPS is terminated by the proxy, which also redirects
  HTTP to HTTPS. Harmless.
