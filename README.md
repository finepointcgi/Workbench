# Workbench

A standalone project ERP and work-management system for government contractors: projects, contracts and WBS,
DCAA timesheets, budgets, ODCs and plan vs. actual, plus Smartsheet/Asana-style sheets, boards and Gantt charts.
It replaces Costpoint, Unanet and Asana; a Costpoint connector comes later.
Built with .NET 10, Blazor and MudBlazor. Runs in the browser and as a desktop app.

Product and technical plan: https://claude.ai/code/artifact/6cda230a-5649-42ac-9dc0-5ec505da3921

## Layout

| Project | Purpose |
| --- | --- |
| `src/Workbench.Web` | ASP.NET Core host: Blazor (Interactive Server), REST API, static assets |
| `src/Workbench.Ui` | Razor class library with all pages and components, shared by web and desktop |
| `src/Workbench.Desktop` | .NET MAUI Blazor Hybrid shell for Windows and macOS (thin client over the API) |
| `src/Workbench.Contracts` | DTOs and service interfaces the UI depends on (`ISheetService`, ...) |
| `src/Workbench.Application` | Use cases implementing the contracts in-process |
| `src/Workbench.Domain` | Entities: workspace, sheet, column, item |
| `src/Workbench.Infrastructure` | EF Core (PostgreSQL for deployments, SQLite for dev and tests), tenancy (catalog, provisioning) |
| `src/Workbench.Migrations.PostgreSql`, `src/Workbench.Migrations.Sqlite` | EF Core migrations, one assembly per provider (`scripts/new-migration.sh`) |
| `src/Workbench.Api` | Minimal API endpoints (`/api/v1/...`) mapped by the web host |
| `src/Workbench.ApiClient` | HTTP implementations of the contracts, used by the desktop app |
| `src/Workbench.Costpoint` | `ICostpointSource` adapter interface and Costpoint record types |
| `src/Workbench.Worker` | Background service for Costpoint sync and automations |
| `tests/Workbench.Tests` | xUnit + bUnit tests |

The UI never references EF Core. The web host binds the contracts to `Workbench.Application`;
the desktop app binds them to `Workbench.ApiClient`, so both run the same components.

## Run

Web (migrates and seeds `workbench.db`, SQLite, in Development):

```bash
dotnet run --project src/Workbench.Web --launch-profile https
```

On PostgreSQL 17 (the production database) locally:

```bash
docker compose up -d
Database__Provider=PostgreSQL ConnectionStrings__Workbench="Host=localhost;Database=workbench;Username=workbench;Password=workbench" \
  dotnet run --project src/Workbench.Web --launch-profile http
```

Multi-tenant (SaaS) mode with two development tenants, `http://acme.localhost:5251` and `http://beta.localhost:5251`,
and the host admin site at `http://localhost:5251/host`:

```bash
dotnet run --project src/Workbench.Web --launch-profile multitenant
```

Production deploys apply migrations as their own step, then start the host:

```bash
dotnet Workbench.Web.dll --migrate
```

Production runs from the container image (`Dockerfile`; `docker-compose.prod.yml` is a complete single-host example
with PostgreSQL, the migrate job and a TLS proxy). Deployment, every configuration setting, key encryption, backups,
monitoring, alerts and incident steps are in `docs/operations/` (start with `docs/operations/README.md`).

Build and test everything except the desktop app:

```bash
dotnet test Workbench.Server.slnf
```

Desktop: start the web host first, then build on the matching OS. macOS needs full Xcode;
Windows needs the Windows App SDK. Set `WORKBENCH_SERVER_URL` to point at another server
(default `https://localhost:7154/`); for a SaaS tenant set `WORKBENCH_TENANT_URL` (e.g. `https://acme.workbench.example/`)
or `WORKBENCH_TENANT` with `WORKBENCH_SERVER_URL`.

```bash
dotnet build src/Workbench.Desktop -t:Run
```

## Configuration

- `Database:Provider`: `PostgreSQL` (default) or `Sqlite` (Development's default)
- `ConnectionStrings:Workbench`: connection string for the chosen provider
- `Database:MigrateOnStartup`: apply pending migrations on startup (default true in Development only; production runs `--migrate`)
- `Tenancy:Mode`: `SingleTenant` (self-hosted, default) or `MultiTenant` (SaaS, one database per tenant), with
  `ConnectionStrings:Catalog`, `Tenancy:BaseDomain`, `Tenancy:HostAdmins` and `Tenancy:TenantConnectionString`
  (see `docs/security/configuration.md`, "Multi-tenancy")
- `Costpoint:Source`: `None` (default), `Csv`, `Rest` or `Database` (see `src/Workbench.Costpoint/README.md`)
- `Costpoint:Csv:Folder`: folder with the Costpoint CSV exports (Development uses `samples/costpoint`)
- `Costpoint:SyncInterval`: worker sync interval (default 24 hours)
- `Authentication:Oidc:Authority`, `:ClientId`, `:ClientSecret`, `:Audience`, `:Scopes`: OpenID Connect sign-in (Entra ID
  or any OIDC provider). Optional for a SingleTenant server, which can run on built-in accounts (email, password and an
  authenticator app; see `docs/security/built-in-accounts.md`); required in MultiTenant mode for the host site. Without
  an authority, Development and the public demo also offer the dev user picker.
- `Authentication:Passwords:*`: built-in accounts (`Enabled`, default `true`; `MinimumLength` 12; `MaxFailedAttempts` 5;
  `LockoutDuration` 15 minutes; `ResetLinkLifetime` 1 hour)
- `Authentication:Admins`: emails made administrators when they sign in (bootstrap for a new install; without SSO, they
  use **Forgot your password?** on the sign-in page to set a first password)
- Desktop sign-in: `WORKBENCH_OIDC_AUTHORITY`, `WORKBENCH_OIDC_CLIENT_ID` and optional `WORKBENCH_OIDC_SCOPE`
  (a public client that allows `http://127.0.0.1` loopback redirects)
- `Email:Mode`: `Pickup` (write `.eml` files to `Email:PickupFolder`, default `App_Data/mail`; Development default) or `Smtp`
  (`Email:Smtp:Host`, `Port`, `EnableSsl`, `UserName`, `Password`); `Email:From` sets the sender
- `Notifications:PublicBaseUrl`: makes links in emails absolute; `Notifications:DigestHourLocal`: daily digest hour (default 7)
- `Support:Email`: where in-app "Report a problem / Send feedback" requests are emailed (blank: stored only; admins see them at `/admin/support`)
- `Billing:CompanyName`, `Billing:RemitToAddress`, `Billing:CageCode`, `Billing:Uei`: the contractor details printed on invoices (SF 1034 and commercial)
- Timesheet periods are an organization setting (Admin, `/setup/organization`): Weekly with a start day, Biweekly with
  an anchor date, SemiMonthly (1st–15th, 16th–end) or Monthly, changed from the next period boundary. `Timekeeping:Cadence`
  (and `:WeekStartDay`, `:BiweeklyAnchor`) only give the starting value until an Admin saves one.
- `Purchasing:RequisitionFinanceThreshold`: requisitions over this total also need Finance approval after the project manager (default 10000); `Purchasing:MatchPriceTolerancePercent`: three-way match price tolerance (default 5)
- `Assets:CapitalizationThreshold`: AP bill lines at or above this can be capitalized (default 5000); `Assets:GainLossAccountNumber` (default 4900) and `Assets:CashAccountNumber` (default 1010) for disposals
- `PerDiem:LodgingOveragePolicy`: lodging above the GSA maximum is `RequiresJustification` (default; allowable up to `PerDiem:JustifiedLodgingCapPercent`, 300) or `Unallowable`
- `PerDiem:Gsa:ApiKey` (secret, an api.data.gov key) and `PerDiem:Gsa:BaseUrl`: enables "Import from GSA API" on `/setup/per-diem`
- `Resourcing:NonBillableAllowancePercent` (10), `Resourcing:FullThresholdPercent` (90), `Resourcing:StaffingTolerancePercent` (10): capacity, heat map and staffing-gap thresholds
- `Timekeeping:TimeZone`: time zone that decides "today" for future-date and late-entry checks (default: server local)
- `Timekeeping:LateEntryBusinessDays`: a day's hours are flagged late when first entered more than this many business days after the work date (default 1)
- `Timekeeping:MissingTimeReminderHour`: local hour from which employees with no hours for the previous business day get a reminder (default 9)
- Timesheet approval follows roles, not configuration: the supervisor approves; administrators approve for employees without a supervisor; Timekeeper, Finance and Admin can view every timesheet
- `Payroll:Paylocity:ClientId`, `:ClientSecret` (secret), `:BaseUrl`, `:TokenUrl`, `:Scope`: Paylocity REST API for employee sync (OAuth client
  credentials; only used when configured). Hours export and register import are CSV files and need no credentials.
- Security (ISO 27001 controls; details and every key in `docs/security/configuration.md`):
  `Authentication:RequireInvite` (default true outside Development), `Authentication:Session:*` (idle 30 min,
  absolute 12 h), `Scim:Enabled` / `Scim:BearerToken` (secret, 32+ chars), `RateLimiting:*`,
  `Security:TlsTerminatedByProxy`, `DataProtection:*`, `Retention:*`, `Database:AllowSqliteInProduction`,
  `OpenTelemetry:Endpoint` (off by default). Outside Development the host refuses to start with a weak configuration.
  Supply secrets with environment variables or a vault, never in appsettings.

## Moving from Costpoint and Smartsheet

**Setup → Import** (`/setup/import`) imports Costpoint CSV/Excel exports (organizations, employees with PLCs and cost
rates, customers, contracts with CLINs and funding, projects with their WBS, bill rates, budgets) and Smartsheet sheets
(API token or `.xlsx` export, with hierarchy, predecessors and an optional link to a project's WBS). Upload, map,
validate, dry run, import; each job can be rolled back. Sample files and expected columns: `samples/import/README.md`.

## Security and ISO 27001

- Users and security (`/admin/users`): users and roles, invites, quarterly access reviews, the security event log and
  records retention with legal holds. SCIM 2.0 provisioning at `/scim/v2/Users`; health at `/health/live` and
  `/health/ready`.
- Backups: `scripts/backup-all.sh` (encrypted, per database), `scripts/restore-test.sh` and
  `docs/operations/backup-restore.md`; run `scripts/test-backup-restore.sh` locally.
- `SECURITY.md` (reporting vulnerabilities), `docs/security/threat-model.md`, and the ISMS templates in `docs/iso/`
  (start with `docs/iso/README.md`).
