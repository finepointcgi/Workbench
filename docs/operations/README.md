# Operations

How to run Workbench in production, self-hosted (one organization, `Tenancy:Mode=SingleTenant`) or as the
multi-tenant SaaS (`MultiTenant`, a database per tenant plus a catalog). Everything is portable: one container image,
PostgreSQL 17, configuration from environment variables and secret files. No cloud provider is assumed.

| Page | What it covers |
| --- | --- |
| [deployment.md](deployment.md) | The image, the compose example, Kubernetes and managed-container notes, first install, upgrades, rollback |
| [configuration.md](configuration.md) | Every setting, its default and when to change it |
| [data-protection-keys.md](data-protection-keys.md) | Encrypting the key ring (certificate, Azure Key Vault, AWS KMS), rotation, escrow and recovery |
| [backup-restore.md](backup-restore.md) | Encrypted backups per database, point-in-time recovery, the restore test, RPO and RTO |
| [monitoring.md](monitoring.md) | OpenTelemetry traces/metrics/logs, health endpoints, structured logs and redaction |
| [alerts.md](alerts.md) | Alert rules and what to do when each fires |
| [email.md](email.md) | SMTP with TLS, SendGrid, Amazon SES, DNS (SPF/DKIM/DMARC) and bounce handling |
| [runbooks.md](runbooks.md) | Routine procedures: deploy, roll back, rotate secrets, add a tenant, scale, fail over |
| [incident-response.md](incident-response.md) | The technical incident steps that ISO 27001 A.5.24–A.5.28 needs, tied to the incident response plan |

Policies these procedures implement live in `docs/iso/` (backup policy, business continuity, incident response plan)
and the security configuration in `docs/security/configuration.md`.

## Commands the image understands

The image's entry point is `dotnet /app/Workbench.Web.dll`; arguments select a mode:

| Arguments | What it does | Exit |
| --- | --- | --- |
| *(none)* | Runs the web host on port 8080 | — |
| `--migrate` | Applies pending migrations (the catalog, then every tenant in MultiTenant mode) and exits. Run it before every rollout. | 0 when done |
| `--rewrap-data-protection-keys` | Re-encrypts the stored key ring with the configured key encryption, then exits. After rotating the key-encryption certificate or key. | 0 when done |
| `--health-check [url]` | Probes `/health/live` (the container `HEALTHCHECK`; the image has no shell or curl). | 0 healthy, 1 not |
