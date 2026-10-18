# Security configuration and secrets

How to configure Workbench's security controls and supply secrets without committing them (ISO 27001 A.8.9,
A.8.24, A.5.17). Outside Development the web host **refuses to start** with a known-weak configuration (see
"Startup check" below).

## Supplying secrets

Secrets never go in `appsettings*.json` or the repository. CI runs gitleaks on every pull request.

ASP.NET Core reads configuration from, in increasing priority: `appsettings.json`, `appsettings.{Environment}.json`,
user secrets (Development only), **environment variables**, and the command line. A colon in a key becomes a double
underscore in an environment variable: `Scim:BearerToken` → `Scim__BearerToken`.

| Hosting | Recommended way |
| --- | --- |
| Development | `dotnet user-secrets set "Authentication:Oidc:ClientSecret" "..." --project src/Workbench.Web` |
| Linux / containers on-prem | Environment variables from a root-owned `EnvironmentFile=` (systemd, mode 0600) or Docker/Kubernetes secrets mounted as env vars |
| Azure Government | Azure Key Vault (Gov) referenced from App Service / Container Apps app settings (`@Microsoft.KeyVault(SecretUri=...)`), with a managed identity |
| AWS GovCloud | AWS Secrets Manager or SSM Parameter Store (SecureString), injected as environment variables by ECS task definitions or EKS External Secrets |
| HashiCorp Vault | Vault Agent injector or `envconsul` rendering environment variables |

Rotate secrets at least yearly and whenever someone with access leaves (see `docs/iso/access-control-policy.md`).
Workbench reads them at startup, so restart the web host after rotating.

### Secrets Workbench uses

| Key | What it is |
| --- | --- |
| `Authentication:Oidc:ClientSecret` | The confidential client secret at your identity provider (or use certificate credentials there). |
| `Scim:BearerToken` | Random token (32+ characters) the identity provider sends to `/scim/v2`. Generate with `openssl rand -base64 48`. |
| `ConnectionStrings:Workbench` | Database connection string, including the password for PostgreSQL. Prefer a managed identity / IAM auth where available. |
| `Email:Smtp:Password` | SMTP relay password. |
| `DataProtection:CertificatePassword` | Password of the PFX that encrypts Data Protection keys. |

## Configuration keys added for ISO 27001 controls

| Key | Default | Control |
| --- | --- | --- |
| `Authentication:RequireInvite` | `true` outside Development | Only invited, SCIM-provisioned or bootstrap-admin people may sign in (A.5.16, A.5.18). |
| `Authentication:Session:IdleTimeoutMinutes` | `30` | Session ends after inactivity (A.8.5). |
| `Authentication:Session:AbsoluteLifetimeHours` | `12` | Session ends this long after sign-in (A.8.5). |
| `Authentication:Session:CircuitRevalidationSeconds` | `60` | How often an open Blazor circuit re-checks that the user is active. |
| `Scim:Enabled` | `true` | Maps `/scim/v2`. Set `false` if you don't provision with SCIM. |
| `Scim:BearerToken` | (none) | Required (32+ chars) when SCIM is enabled outside Development. |
| `RateLimiting:Enabled` | `true` | A.8.20 |
| `RateLimiting:SignInPerMinute` | `20` | Per IP, for `/account/*` and OIDC callbacks. |
| `RateLimiting:ScimPerMinute` | `300` | Per IP. |
| `RateLimiting:ApiPerMinute` | `600` | Per user (or IP). |
| `Security:TlsTerminatedByProxy` | `false` | Set `true` behind a TLS-terminating load balancer; enables forwarded headers. |
| `ForwardedHeaders:KnownProxies` | `[]` | IP addresses of trusted proxies. |
| `Security:HstsMaxAgeDays` | `365` | HSTS max-age (sent outside Development). |
| `Security:ConnectSources` | `[]` | Extra origins allowed in the CSP `connect-src`. |
| `DataProtection:Store` | `Database` | `Database`, `FileSystem` or `Default` (A.8.24). |
| `DataProtection:KeysFolder` | (none) | Folder for `FileSystem`. Restrict it to the service account. |
| `DataProtection:CertificatePath` / `:CertificatePassword` | (none) | PFX that encrypts keys at rest. |
| `DataProtection:CertificateThumbprint` | (none) | Alternatively, a certificate in the Windows certificate store. |
| `Database:AllowSqliteInProduction` | `false` | SQLite is refused in Production unless this is set (single-user evaluation only). |
| `Retention:SecurityEventDays` | `365` | A.8.15, A.5.33 |
| `Retention:NotificationDays` | `180` | |
| `Retention:ExpiredInviteDays` | `90` | |
| `Retention:ProtectedRecordDays` | `1095` | Time, ODCs, journal, audit, access reviews: report only, never auto-deleted. |
| `Retention:RunHourUtc` | `3` | Nightly purge hour. |
| `Retention:NightlyJobEnabled` | `true` | |
| `OpenTelemetry:Endpoint` | (none: off) | OTLP endpoint for traces and metrics (A.8.16), e.g. `http://otel-collector:4317`. |
| `OpenTelemetry:ServiceName` | `workbench-web` | |

Administrators can override retention periods in **Users and security → Retention**; every change is a
`settings.changed` security event.

## Startup check

Outside Development, the web host lists every problem and refuses to start when:

- no OIDC authority is configured (the dev sign-in page would be the only way in), or the dev sign-in is enabled;
- `Authentication:Oidc:RequireHttpsMetadata` is `false`;
- it only listens on plain HTTP and `Security:TlsTerminatedByProxy` isn't set;
- SCIM is enabled and `Scim:BearerToken` is empty or shorter than 32 characters;
- the environment is Production, the database is SQLite and `Database:AllowSqliteInProduction` isn't `true`.

## Data Protection keys

ASP.NET Core Data Protection encrypts the auth cookie and antiforgery tokens. Workbench stores its key ring in the
`DataProtectionKeys` table by default, so every server instance shares keys and restarts don't sign everyone out.
Configure a certificate (`DataProtection:CertificatePath` + `:CertificatePassword`, or `:CertificateThumbprint`) so
the keys are encrypted at rest; without one they're protected only by database access controls. Keys rotate every
90 days automatically. Back up the database (and the certificate, separately) so cookies and protected data survive a
restore.

## Logging

Logs are structured (use `Logging:Console:FormatterName=json` in production for a log pipeline). Workbench never logs
secrets, tokens, passwords or cost/bill rates: the security event log records event types, people and short details
only; EF Core parameter values are never logged (sensitive data logging is off), and OpenTelemetry spans drop query
strings. Code reviewers check new log statements against this rule (see `docs/iso/secure-development-policy.md`).
