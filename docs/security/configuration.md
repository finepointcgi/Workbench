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
| `ConnectionStrings:Catalog` | MultiTenant only: the catalog database (tenants, the shared Data Protection key ring, the host audit log). |
| `Tenancy:TenantConnectionString` | MultiTenant only: the template for new tenant databases (`{tenant}` is replaced). Its account needs `CREATEDB`. |
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
| `Database:Provider` | `PostgreSQL` | `PostgreSQL` (production) or `Sqlite` (Development's default, tests, single-user evaluation). |
| `Database:MigrateOnStartup` | `true` in Development, else `false` | Apply pending migrations on startup. Production deploys run `Workbench.Web --migrate` instead (A.8.32). |
| `Database:AllowSqliteInProduction` | `false` | SQLite is refused in Production unless this is set (single-user evaluation only). |
| `Tenancy:Mode` | `SingleTenant` | `SingleTenant` (self-hosted, one database) or `MultiTenant` (SaaS, one database per tenant; A.8.3, A.5.23). |
| `Tenancy:BaseDomain` | (none) | MultiTenant: tenants are its subdomains (`acme.workbench.example`); the base domain itself is the host admin site. |
| `Tenancy:HostAdmins` | `[]` | MultiTenant: emails of the host administrators who create and suspend tenants. Not a tenant's Admin role. |
| `Tenancy:AllowTenantHeader` | `true` | Lets the `X-Tenant` header pick the tenant. Honoured **only in Development**, whatever this says. |
| `Tenancy:DevelopmentTenants` | `[]` | Development only: tenants created with the sample data on startup when missing. |
| `Tenancy:FiscalYearStartMonth` | `10` | First month of the fiscal year seeded for a new tenant. |
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

## Organization sign-in (SSO, MFA, provisioning)

Besides the server-wide `Authentication:Oidc` provider, each organization (each tenant in MultiTenant mode) can set
its own OpenID Connect provider in **Admin › Users and security › Sign-in**: Entra ID ([sso-entra.md](sso-entra.md)),
Okta ([sso-okta.md](sso-okta.md)) or any standards-compliant IdP. These settings live in the organization's database,
not in configuration:

| Setting | Effect |
| --- | --- |
| Identity provider (issuer, client id, secret) | **Sign in** uses it (scheme `OrganizationSso`, callback `/signin-sso`). The client secret is encrypted with Data Protection (per tenant) and never shown again. Issuers must be `https` (plain `http` only on loopback, for development). |
| Only allow sign-in through this provider | Refuses the server-wide provider for this organization, including the desktop app's bearer tokens. Can be turned on only from a session that used the organization's provider. |
| MFA: off / administrators / everyone | Refuses sign-ins (and bearer tokens) whose `amr`/`acr` don't include an accepted value; `signin.mfa_required` event. Can be tightened only from a session that used MFA. |
| Just-in-time provisioning | Invite only, or also allowed email domains (default roles, never Admin); groups claim → roles; optionally the IdP manages roles at every sign-in (never removing the last admin). |
| Re-authentication window (default 15 min) | Pay rates, bank account numbers and changes, vendor TINs, approval workflow changes and these settings need a recent sign-in; `/account/reauthenticate` sends the user to the IdP with `prompt=login` and `max_age=0` and rejects an old `auth_time`. |
| Shorter session limit | An organization limit under `Authentication:Session:AbsoluteLifetimeHours`. |

Register the redirect URIs `https://<host>/signin-sso` and `https://<host>/signout-callback-sso` at the IdP (shown on the
page). The desktop app's bearer tokens still come from the server-wide provider. Changes are `settings.sso` security events.

## Built-in accounts

Email and password sign-in with an authenticator app, for organizations without single sign-on (or alongside it). See
[built-in-accounts.md](built-in-accounts.md) for the flows, the lockout and MFA rules, and the security events.
**Only allow sign-in through this provider** also refuses Workbench passwords.

## Startup check

Outside Development, the web host lists every problem and refuses to start when:

- no OIDC authority is configured and built-in accounts are off (`Authentication:Passwords:Enabled=false`) or the server
  is MultiTenant (its host site signs in only with OIDC), or the dev sign-in is enabled outside the public demo;
- `Authentication:Oidc:RequireHttpsMetadata` is `false`;
- it only listens on plain HTTP and `Security:TlsTerminatedByProxy` isn't set;
- SCIM is enabled and `Scim:BearerToken` is empty or shorter than 32 characters;
- the environment is Production, the database is SQLite and `Database:AllowSqliteInProduction` isn't `true`;
- in MultiTenant mode: `Authentication:RequireInvite` is `false` (anyone the identity provider vouches for could join
  any tenant), `Tenancy:HostAdmins` is empty, or `Tenancy:BaseDomain` is missing or `localhost`.

## Data Protection keys

ASP.NET Core Data Protection encrypts the auth cookie and antiforgery tokens. Workbench stores its key ring in the
`DataProtectionKeys` table by default, so every server instance shares keys and restarts don't sign everyone out.
Configure a certificate (`DataProtection:CertificatePath` + `:CertificatePassword`, or `:CertificateThumbprint`) so
the keys are encrypted at rest; without one they're protected only by database access controls. Keys rotate every
90 days automatically. Back up the database (and the certificate, separately) so cookies and protected data survive a
restore.

In **MultiTenant** mode there is one key ring for the whole service, stored in the catalog database
(`DataProtectionKeys`), and every protector is isolated per tenant by purpose: Workbench wraps the Data Protection
provider so each Protect/Unprotect call adds the current tenant's id to the purpose chain (the host admin site has its
own purpose). An auth cookie, antiforgery token, Blazor circuit token or encrypted vendor TIN made for one tenant
therefore fails to unprotect in any other tenant, even if copied. Tenant connection strings in the catalog are
protected with the undecorated provider under their own purpose. We chose purpose isolation over a key ring per
tenant because ASP.NET Core caches one key ring per application: per-tenant rings would need one key manager per tenant
and per-tenant key rotation, for no gain in separation (the purposes already derive different keys) and with more ways
to lose keys. A database restore of one tenant never needs the keys of another; restoring the catalog restores the ring.

## Multi-tenancy (SaaS)

`Tenancy:Mode=MultiTenant` runs one database per tenant (A.8.3, A.8.31). The catalog database (`ConnectionStrings:
Catalog`) lists tenants with their status and their encrypted connection strings; nothing tenant-specific is stored in
it. Every request is resolved to a tenant from its subdomain before authentication runs; unknown, provisioning or
failed tenants get 404 and suspended ones 403. Blazor circuits stay pinned to the tenant they started in, SignalR hub
calls run in the tenant of their connection, and background jobs run once per active tenant. Without a tenant, opening
a tenant database throws: nothing falls back to a shared database.

- **Host admin site** (`https://<BaseDomain>/host`, API `/api/v1/host/...`): only `Tenancy:HostAdmins`, only on the base
  domain. Create a tenant (database, migrations, default chart of accounts and fiscal calendar, an invite for its first
  Admin), suspend or reactivate it, see each tenant's database size and last activity, and read the host audit log.
- **Identity provider:** the sign-in redirect URI is per tenant (`https://acme.<BaseDomain>/signin-oidc`). Register each
  tenant's URI, or use an IdP that allows a wildcard for your domain. Cookies are bound to the tenant that issued them.
- **Stays SingleTenant for now:** SCIM (one server-wide token) is off in MultiTenant mode; `Authentication:Admins`
  bootstrap admins are ignored (a tenant's first admin comes from provisioning); the Costpoint worker
  (`Workbench.Worker`) syncs one database.
- `Notifications:PublicBaseUrl` may contain `{tenant}` (e.g. `https://{tenant}.workbench.example`) so email links point at
  the right tenant.

## Logging

Logs are structured (use `Logging:Console:FormatterName=json` in production for a log pipeline). Workbench never logs
secrets, tokens, passwords or cost/bill rates: the security event log records event types, people and short details
only; EF Core parameter values are never logged (sensitive data logging is off), and OpenTelemetry spans drop query
strings. Code reviewers check new log statements against this rule (see `docs/iso/secure-development-policy.md`).
