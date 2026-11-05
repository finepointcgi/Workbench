# Hosting and operations

<p class="roles">Hosting administrators · IT</p>

Workbench runs two ways:

- **Self-hosted** (single tenant): one organization on your own server or cloud account, with PostgreSQL. You install
  it, apply updates and run backups.
- **Hosted** (SaaS, multi-tenant): one database per customer, each on its own address (for example
  `acme.workbench.example`). The hosting team runs the servers; your administrators only use the Admin pages in this
  guide.

If you use the hosted service, you don't need this page: send single sign-on details and support questions to your
Workbench support contact.

## Self-hosting at a glance

| Task | Where |
| --- | --- |
| Install and deploy (container image, PostgreSQL, TLS proxy) | [Deployment](../operations/deployment.md) |
| Every configuration setting | [Configuration](../operations/configuration.md) and [Security configuration](../security/configuration.md) |
| Sending email (SMTP or a pickup folder) | [Email](../operations/email.md) |
| Encryption keys | [Data protection keys](../operations/data-protection-keys.md) |
| Files and attachments | [Object storage](../operations/object-storage.md) |
| Backups and restore tests | [Backup and restore](../operations/backup-restore.md) |
| Health checks, logs and metrics | [Monitoring](../operations/monitoring.md), [Alerts](../operations/alerts.md) |
| Sizing and tuning | [Performance](../operations/performance.md) |
| When something goes wrong | [Runbooks](../operations/runbooks.md), [Incident response](../operations/incident-response.md) |

### Updating

Each release applies its database changes as a separate step before the new version starts:

```bash
dotnet Workbench.Web.dll --migrate
```

With the container image, run the migrate job, then start the new version (the example `docker-compose.prod.yml` does
this for you).

### Settings administrators often ask about

| Setting | What it does |
| --- | --- |
| `Authentication:Oidc:*` | Single sign-on (authority, client id and secret) |
| `Authentication:Admins` | Emails made administrators when they first sign in (to bootstrap a new install) |
| `Authentication:RequireInvite` | Only invited or provisioned people can sign in (default on) |
| `Scim:Enabled`, `Scim:BearerToken` | SCIM provisioning |
| `Email:Mode`, `Email:Smtp:*`, `Email:From` | How Workbench sends email |
| `Notifications:PublicBaseUrl` | Makes links in emails absolute |
| `Billing:CompanyName`, `RemitToAddress`, `CageCode`, `Uei` | Contractor details printed on invoices |
| `Purchasing:RequisitionFinanceThreshold` | Requisitions over this also need Finance (default 10,000) |
| `Assets:CapitalizationThreshold` | AP bill lines at or above this can be capitalized (default 5,000) |
| `Timekeeping:LateEntryBusinessDays` | When hours are flagged as entered late (default 1 business day) |
| `Payroll:Paylocity:*` | Paylocity API for employee sync |
| `PerDiem:Gsa:ApiKey` | Enables **Import from GSA API** for per diem rates |
| `Support:Email` | Where in-app problem reports are emailed |

Secrets go in environment variables or a vault, never in a settings file. Outside development, Workbench refuses to
start with a weak security configuration and says what to fix.

## The public demo

A demo server with sample data, where anyone can sign in as any demo user, is described in
[Public demo](../demo-hosting.md). Never put real company data in it.
