# Threat model

STRIDE analysis of Workbench (ISO 27001 A.8.25, A.8.27). Review it when the architecture changes (new external
interface, new data store, new hosting model) and at least yearly. Owner: `<security lead>`. Last reviewed:
`<date>`.

## Architecture and data

```
                     ┌────────────── Identity provider (Entra ID, Okta, any OIDC) ─────────────┐
                     │ OIDC sign-in (code + PKCE)           SCIM 2.0 provisioning (bearer token) │
                     ▼                                                       │                  │
 Browser ──HTTPS──► Web host (ASP.NET Core, Blazor Server) ◄──────────────────┘                  │
   │  cookie            │  ├─ Blazor circuit over SignalR (WebSocket)                            │
   │                    │  ├─ REST API /api/v1  ◄──HTTPS + JWT bearer── Desktop client (MAUI) ◄───┘
   │                    │  ├─ SignalR hubs /hubs (live updates, notifications)
   │                    │  └─ /scim/v2, /health
   │                    ▼
   │              Database (PostgreSQL; SQLite for evaluation)    Blob store (attachments, receipts)
   │              Worker (Costpoint import, scheduled jobs)       SMTP relay (notifications)
```

**Trust boundaries:** the internet ↔ the web host; the web host ↔ the database and blob store; the web host ↔ the
identity provider; the desktop client's machine ↔ the API. In MultiTenant (SaaS) mode, also **tenant ↔ tenant**: every
tenant has its own database, and the catalog database (tenants, connection strings, the shared Data Protection key
ring) sits behind the host admin site.

**Assets:** labor hours and cost rates (DCAA-relevant, CUI-adjacent), contract values, budgets and financials,
ODCs and receipts, employee records, the audit and security logs, credentials (OIDC client secret, SCIM token, Data
Protection keys, database password).

## STRIDE by component

Status: **M** = mitigated in the product, **P** = partly mitigated, **O** = the operator/company must act.

### Browser

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Spoofing | Stolen session cookie | `HttpOnly`, `Secure` (outside Development), `SameSite=Lax` cookie; 30-minute idle and 12-hour absolute lifetime; HSTS; deactivation and "sign out everywhere" reject the cookie on the next request | M |
| Tampering | XSS injecting script | Razor encodes output; CSP `script-src 'self'` (no inline or eval), `object-src 'none'`, `base-uri 'self'` | M |
| Tampering | CSRF on form posts | Antiforgery tokens on forms (dev sign-in); Blazor actions go over the circuit, not form posts; `SameSite=Lax` | M |
| Tampering | Clickjacking | `frame-ancestors 'none'`, `X-Frame-Options: DENY` | M |
| Info disclosure | Cached pages or CSVs on shared machines | `Cache-Control: no-store` on authenticated responses | M |
| Info disclosure | Referrer leaks ids in URLs | `Referrer-Policy: strict-origin-when-cross-origin` | M |
| Repudiation | "I didn't export that" | `export.*` security events with actor, IP, user agent | M |
| Elevation | CSV formula injection in exports | Security-event CSV export prefixes `=`, `+`, `-`, `@` | P (other exports: review) |

### Web host (server)

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Spoofing | Sign-in by someone not authorized | OIDC only outside Development; startup refuses the dev sign-in; `RequireInvite` so only invited/SCIM users get accounts; deactivated users refused | M |
| Spoofing | Credential stuffing / brute force | Sign-in is at the IdP (MFA there); rate limit on `/account/*` and callbacks | M / O (enforce MFA at IdP) |
| Tampering | Changing records without permission | Permission decorators on every service; workspace and org roles; 403 is logged | M |
| Tampering | Changing history | Audit and security events are append-only; setup data closed, not deleted; legal holds block deletion | M |
| Repudiation | Admin denies changing roles | `user.*`, `invite.*`, `access_review.*`, `settings.changed` events | M |
| Info disclosure | Cost rates or secrets in logs | Logging rule (no secrets, tokens, rates); EF sensitive data logging off; OTel drops query strings | M |
| Info disclosure | Stack traces | `UseExceptionHandler` + ProblemDetails outside Development | M |
| Denial of service | Request floods | Rate limiter on API, sign-in, SCIM; operator WAF / load balancer | P / O |
| Elevation | Admin locks the organization out | Nobody removes their own Admin role or deactivates themselves; the last active Admin is protected | M |
| Elevation | Weak deployment config | Startup check refuses dev sign-in, HTTP-only, empty SCIM token, SQLite in Production | M |

### SignalR (Blazor circuit and hubs)

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Spoofing | Circuit keeps working after the user is deactivated | Circuit re-validates every 60 s and on reconnect; idle timeout enforced inside the circuit; client sent to sign in | M |
| Spoofing | Cross-site WebSocket hijacking | Cookie `SameSite=Lax`; CSP `connect-src 'self'`; hubs require authentication | M |
| Info disclosure | Live updates for sheets the user can't see | Hub `JoinSheet` checks sheet access | M |
| Denial of service | Many open circuits | Server circuit limits (defaults); operator capacity monitoring (`/health`, OTel metrics) | P / O |
| Elevation | Long-lived hub connection after deactivation | Hub calls re-check permissions per call; **open hub connections aren't closed on deactivation** (they receive notifications until they disconnect) | P |

### Desktop client (MAUI)

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Spoofing | Token theft from the device | OIDC public client with PKCE and loopback redirect; tokens kept in memory/OS secure storage | P / O (device management, disk encryption) |
| Spoofing | Token use after deactivation | Every API call checks the user is active and the token was issued after "sign out everywhere" | M |
| Tampering | Modified client | The server enforces every permission; the client is untrusted | M |
| Info disclosure | Data on a lost laptop | Thin client: data stays on the server | M / O (MDM, disk encryption) |

### Database

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Info disclosure | Database copied or backup leaked | Encryption at rest (managed PostgreSQL / disk encryption); backups encrypted with gpg; TLS (`sslmode=verify-full`) | O |
| Tampering | Direct SQL edits bypass the app | Least-privilege DB accounts; DB audit logging | O |
| Info disclosure | Data Protection keys stolen with the DB | Keys encrypted with a certificate when `DataProtection:Certificate*` is set; certificate kept in a vault | M / O |
| Denial of service | Data loss | Backups, tested restores (`docs/operations/backup-restore.md`), `/health/ready` | M / O |
| Repudiation | Security log purged to hide activity | Purge only after the retention period; `retention.run` is itself logged; export to a SIEM | P / O |

### Blob store (attachments, receipts)

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Tampering | Malicious upload (HTML, SVG, executables) | Extension allow-list, server-chosen content type, 25 MB limit | M |
| Info disclosure | Direct access to blobs | Blobs served only through permission-checked endpoints; private storage | M / O |
| Denial of service | Storage exhaustion | Size limit per file; operator quotas | P / O |
| — | Malware in uploads | Not scanned by Workbench | O (AV scanning on the store) |

### Identity provider

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Spoofing | Weak authentication at the IdP | MFA and conditional access are configured in the IdP | O |
| Spoofing | Token from another tenant / audience | Authority and audience validation by ASP.NET Core; HTTPS metadata required | M |
| Spoofing | IdP account for a leaver remains | SCIM deactivation; quarterly access reviews flag 90-day dormant users | M / O |
| Info disclosure | Client secret leaked | Secret from a vault, never committed (gitleaks); prefer certificate credentials | O |

### SCIM endpoint (`/scim/v2`)

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Spoofing | Guessing or reusing the bearer token | 32+ character token required in production, compared in constant time; rate limited; failures logged | M |
| Tampering | Creating an admin through SCIM | SCIM can't set roles; new users are Employees | M |
| Tampering | Renaming a user's email to take over history | `userName` changes refused | M |
| Elevation | Deactivating every admin | Last active Admin protected | M |
| Repudiation | Unknown provisioning changes | Every SCIM call is a `scim.request` event | M |
| Info disclosure | Enumerating users | Requires the token; the token grants only user provisioning | M |

### Multi-tenancy (SaaS mode)

| Threat | Example | Mitigation | Status |
| --- | --- | --- | --- |
| Info disclosure | A user of tenant A reads tenant B's data | One database per tenant; the tenant comes from the subdomain before authentication; the only database factory opens the resolved tenant's database and throws without one; isolation tests cover the API, hubs, background jobs and cookies | M |
| Spoofing | Choosing a tenant with a header or query string | `X-Tenant` is honoured only in Development; production resolves from the host name only, one subdomain label of `Tenancy:BaseDomain` | M |
| Spoofing | Replaying tenant A's cookie at tenant B | Data Protection purposes are per tenant, so the cookie doesn't decrypt; the cookie also carries the tenant id and is rejected if it differs | M |
| Spoofing | IdP user joins a tenant they don't belong to | Tenant membership is the tenant's own user table; `RequireInvite` is enforced in MultiTenant production; first admin invited by the host | M |
| Info disclosure | Shared in-process caches or event fan-outs leak across tenants | User status, provisioning caches, live updates and notifications are keyed by tenant; SignalR groups are tenant-qualified | M |
| Info disclosure | A long-lived Blazor circuit switches tenant | Each circuit is pinned to its first tenant; an activity from another tenant is refused | M |
| Elevation | A tenant Admin acts on other tenants | Host administration is a separate role (`Tenancy:HostAdmins`) and only works on the base domain | M |
| Denial of service | One tenant's background job failure stops others | The job runner isolates failures per tenant | M |
| Tampering | Suspended tenant keeps using the service | Requests get 403 within 30 s (at once on the instance that suspended it); open circuits stop on their next activity; jobs skip it | M |
| Info disclosure | Catalog breach exposes all tenant databases | Connection strings are encrypted with Data Protection (certificate-protected keys recommended); least-privilege database accounts per tenant | M / O |
| Info disclosure | Attachments mixed across tenants | Blob keys are prefixed `tenants/<slug>/`; nothing is stored without a tenant | M |
| Repudiation | Host admin denies suspending a tenant | Host audit log in the catalog (append-only) | M |
| — | SCIM and the Costpoint worker | Still single-tenant: SCIM is disabled in MultiTenant mode; run the worker per tenant database if needed | O |

## Open items for the company

1. Enforce MFA and conditional access at the identity provider.
2. Encrypt the database, backups and blob store at rest; keep backups in a second location.
3. Forward application logs and the security event log export to a SIEM with alerting.
4. Put a WAF or DDoS protection in front of internet-facing deployments.
5. Malware scanning on the blob store.
6. Close SignalR hub connections on deactivation (product backlog).
7. Annual penetration test by an independent tester; track findings in the risk register.
8. SaaS: least-privilege database accounts per tenant (the provisioning account only creates databases), and per-tenant
   SCIM tokens before SCIM is enabled for MultiTenant mode (product backlog).
