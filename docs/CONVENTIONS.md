# Workbench conventions

Read this before adding a feature. Several sessions build features in parallel on separate
branches, so these rules keep merges small and predictable.

## Adding a feature end to end

1. **Domain** (`src/Workbench.Domain`): new entities in their own file (e.g. `Domain/Comments/Comment.cs`).
   Derive from `Entity` (v7 GUID ids). Use `DateTime` UTC fields named `*Utc` (SQLite can't order by `DateTimeOffset`).
2. **Persistence** (`src/Workbench.Infrastructure/Persistence/Configurations`): one
   `IEntityTypeConfiguration<T>` per entity in a new file. They are discovered automatically.
   Do **not** edit `WorkbenchDbContext.cs` or add properties to `IWorkbenchDbContext`; use `db.Set<T>()`.
3. **Contracts** (`src/Workbench.Contracts/<Feature>/`): DTO records, request records and a service
   interface. The UI only ever depends on these. Throw `WorkbenchNotFoundException` for missing things and
   `ArgumentException` for invalid input.
4. **Application** (`src/Workbench.Application/<Feature>/`): the in-process implementation. Use
   `IWorkbenchDbContextFactory` and create one context per operation (`await using var db = ...`).
   Register it with **one line** in `Application/DependencyInjection.cs`.
5. **API** (`src/Workbench.Api/<Feature>Endpoints.cs`): `internal static void Map<Feature>Endpoints(this RouteGroupBuilder v1)`,
   plus **one line** in `WorkbenchApi.cs`. The group filter already maps not-found → 404 and argument errors → 400.
6. **API client** (`src/Workbench.ApiClient/Http<Feature>Service.cs`): implement the same interface over HTTP using
   `ApiResponses.ReadAsync` / `EnsureOkAsync`, plus **one line** in `ApiClient/DependencyInjection.cs`.
   This is what the desktop app uses, so every UI-facing interface needs one.
7. **UI** (`src/Workbench.Ui`): components inject contract interfaces only, never EF Core or Application types.
8. **Tests** (`tests/Workbench.Tests/<Feature>/`): service tests with `TestDb.CreateFactory()`; component tests with bUnit
   (`await using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose; ctx.Services.AddMudServices();`).

## Sheet views

- Views live in `src/Workbench.Ui/Views/` and inherit `SheetViewBase` (`Sheet`, `Items`, `Changed`).
- Edit through `IItemService` / `IColumnService`, then `await Changed.InvokeAsync()` so the page reloads.
- Put view-specific subcomponents in their own folder (`Ui/Grid`, `Ui/Board`, `Ui/Calendar`, `Ui/Gantt`).
- To open an item's details panel from any view, navigate to `sheets/{sheetId}?item={itemId}`.
- Cell values are invariant strings keyed by column id: Number/Currency `1234.5`, Date `yyyy-MM-dd`,
  Checkbox `true`/`false`, Dropdown the option text, Person an email, CostpointLookup the Costpoint id.

## MudBlazor 9 notes

- `MudText` uses `HtmlTag="h1"` (not `Component`). `MudTabs` uses `TabPanelsClass` (not `PanelClass`).
- Avoid `Color.Secondary` for quiet text (it is pink); use `Class="mud-text-secondary"`.
- Dialogs: `[CascadingParameter] IMudDialogInstance MudDialog`, `DialogService.ShowAsync<T>()`, `await dialog.Result`.
- `MudMenu` with `ActivatorContent`: the activator must call `context.ToggleAsync(e)`; it isn't wired automatically.
- Treat MudBlazor analyzer warnings (`MUD0002`) as errors: they flag removed parameters.

## Build and verify

```bash
dotnet test Workbench.Server.slnf
dotnet run --project src/Workbench.Web --launch-profile http   # http://localhost:5104
```

`Workbench.Server.slnf` excludes the desktop app, which needs Xcode (macOS) or Windows to build.
The dev database is `src/Workbench.Web/workbench.db` (SQLite, migrated and seeded on start). After pulling a schema
change it is migrated; after a baseline regeneration, delete it (or let Development set it aside on start).

Tests run on EF Core InMemory by default. `WORKBENCH_TEST_DB=Sqlite` or `=PostgreSql` runs the whole suite on a real,
migrated database per test (CI runs both). PostgreSQL comes from `docker compose up -d` (or `WORKBENCH_TEST_POSTGRES`);
tests that only make sense there use `[PostgresFact]` and skip when no server answers.

## Database migrations

Schema changes are EF Core migrations, one assembly per provider: `src/Workbench.Migrations.PostgreSql` and
`src/Workbench.Migrations.Sqlite`, each with a `WorkbenchDb/` (the tenant database) and a `CatalogDb/` folder (the
MultiTenant catalog). Migrations are provider-specific SQL and EF keeps one model snapshot per context per assembly, so
one shared assembly can't serve both providers.

- **Until the pilot, the baseline is regenerated instead of adding migrations.** A feature branch adds its entity and
  configuration and then either runs `scripts/regenerate-baseline.sh` itself, or leaves that to whoever merges it. Until
  it is regenerated, CI's model-drift check and the SQLite/PostgreSQL test legs fail for the new tables (the InMemory
  default still passes locally). Never hand-merge generated migration files: on a conflict, take either side and regenerate.
- **After the pilot** (once any shared or production database exists), every schema change is
  `scripts/new-migration.sh AddSomething` (`--catalog` for the catalog). It adds the migration to both providers and
  checks nothing is left over. Commit both folders together; never edit a generated migration after it has shipped.
- **Regenerating the baseline (pre-pilot only).** There are no production databases yet, so after merging feature
  branches run `scripts/regenerate-baseline.sh`: it deletes every migration and generates one `InitialCreate` per
  provider and context from the merged model. Then delete local dev databases (`src/Workbench.Web/*.db`,
  `src/Workbench.Web/App_Data/catalog.db`, `src/Workbench.Web/App_Data/tenants/`) and run the tests on both providers.
- Startup migrates only when `Database:MigrateOnStartup` is true (Development). Production deploys run
  `dotnet Workbench.Web.dll --migrate` (the catalog, then every tenant) before starting the new version.
- Things that differ by provider, handled for you in `ProviderConventions` (`WorkbenchDbContext.cs`): every `DateTime`
  is UTC (Npgsql refuses other kinds for `timestamptz`); text uses ordinal collation `C` on PostgreSQL, like SQLite's
  `BINARY`; decimals round to their column's scale on both. Still yours to get right: string equality and unique
  indexes are case-sensitive on both providers (store emails and codes normalized; lowercase emails), give every
  decimal a precision (`HasPrecision`), give strings a `HasMaxLength` (PostgreSQL enforces it), don't rely on how
  NULLs sort (last on PostgreSQL, first on SQLite), and never use provider-specific SQL outside `Infrastructure`.

## Multi-tenancy

`Tenancy:Mode=MultiTenant` gives every tenant its own database; SingleTenant (the default) is one database. Feature
code doesn't see tenants, provided it follows these rules:

- Open data only through `IWorkbenchDbContextFactory` (or `IDbContextFactory<WorkbenchDbContext>` in Infrastructure). It
  opens the current tenant's database and throws `TenantNotResolvedException` without one. Never build `DbContextOptions`
  or connections yourself.
- A `BackgroundService` wraps each run in `ITenantJobRunner.ForEachTenantAsync(ct => ...)` and creates its scope inside
  the callback (see `MissingTimesheetBackgroundService`). In SingleTenant mode it runs once.
- Singletons must not cache tenant data under a bare key: qualify keys with `TenantScope.Qualify(key)` (see
  `UserStatusCache`) or key by `TenantScope.Key`. In-process fan-outs (`SheetEventHub`, `NotificationHub`) and SignalR
  group names are already tenant-qualified. No `static` mutable state.
- Blob keys are prefixed per tenant by the store; email links use `Notifications:PublicBaseUrl` (which may contain `{tenant}`).
- Per-tenant settings live in the tenant database (e.g. the timesheet cadence, `/setup/organization`), not in
  configuration. Server-wide configuration is the same for every tenant.
- Host administration (`/host`, `IHostAdminService`) is for `Tenancy:HostAdmins` on the base domain only. To try it:
  `dotnet run --project src/Workbench.Web --launch-profile multitenant` and open `http://acme.localhost:5251`,
  `http://beta.localhost:5251` and `http://localhost:5251/host` (sign in as host@workbench.local).

## Timesheet periods

The cadence (Weekly with a start day, Biweekly with an anchor, SemiMonthly, Monthly) is an organization setting in the
tenant database (`TimesheetPeriodRule`, effective-dated, changed from the next period boundary). Get a period with
`TimekeepingClock.PeriodForAsync(db, date)`; a timesheet keeps the `PeriodStart`/`PeriodEnd` it was opened with, so
look timesheets up by the date they contain, not by recomputing the period.

## Shared files

Only append to these, one line per feature, never reorder: `Application/DependencyInjection.cs`,
`ApiClient/DependencyInjection.cs`, `Api/WorkbenchApi.cs`, `Ui/Layout/NavMenu.razor`, `Ui/_Imports.razor`.
Generated migrations (`src/Workbench.Migrations.*`) are owned by whoever regenerates the baseline; don't hand-merge them.
Don't change existing contracts in `Workbench.Contracts/Sheets`; if a feature needs more, add new records or
interfaces next to them and say so in the commit message.

## Project accounting (system of record)

Workbench replaces Costpoint, Unanet and Asana; it is not an add-on to them. Costpoint/Unanet code paths
(`Workbench.Costpoint`, `Cp*` mirror tables, `/admin/costpoint`) are kept for future import and are hidden in the UI.

- Entities live in `src/Workbench.Domain/ProjectAccounting/` (people, contracts, projects/WBS, fiscal calendar,
  timesheets, ODCs, budgets, audit). Mappings in `Configurations/ProjectAccountingConfigurations.cs`.
- Foreign keys **restrict** deletes. Setup data is closed or deactivated, never deleted out from under history.
- Every change to rates, setup, budgets and approvals writes an `AuditEvent` through `AuditRecorder.Record(...)` in the
  same unit of work. Audit rows are append-only.
- Rates are effective-dated and append-only: a correction is a new rate with a reason.
- Money is `decimal` (18,2), rates (18,4), hours (9,2). Never use `double` for money or hours.
- A charge code is an open, chargeable **leaf** `WbsElement` of an active project. `IProjectService.ListChargeCodesAsync`
  is the single source of truth for what an employee may charge on a date.
- Labor cost is always computed (approved hours × the employee's cost rate effective on the work date), never stored as typed.
- The plan sheet and the WBS are one tree: `Project.SheetId` points at the plan sheet and `WbsElement.ItemId` at its task.
- The signed-in person's employee record: `IPeopleService.GetCurrentEmployeeAsync()` (matched by `ICurrentUser.Email`).
- Seeded dev data: `ProjectAccountingSeed.cs` (FY runs Oct–Sep; employees pm@, analyst@, dev@workbench.local, viewer@).

## Authentication and permissions

- Who is signed in: `ICurrentUser` (sync: email, name). Roles and the linked employee: `ICurrentUserContext.GetAsync()`
  (`CurrentUserInfo`: `Roles`, `EmployeeId`, `IsFinance`, `CanViewAllTimesheets`). Server code can also inject
  `Application.Identity.AccessControl` (`RequireSheetRoleAsync`, `RequireFinanceAsync`, `RequireAdminAsync`).
- Two kinds of roles. **Workspace roles** (Viewer < Commenter < Editor < Owner) govern sheets, through membership of the
  sheet's workspace. **Organization roles** (`OrgRoles` flags: Timekeeper, Finance, Admin; everyone is an Employee)
  govern project accounting. Admin can do everything.
- Enforce on the server with decorators registered by `AddWorkbenchIdentity()`, which stays the **last** line of
  `AddWorkbenchApplication` so the permission checks wrap every other decorator. Put new feature registrations
  (and their own decorators) above it, and add the new service's permission decorator inside `AddWorkbenchIdentity`.
- A sheet the user can't see is not found (404); something they can see but not change is
  `WorkbenchForbiddenException` (403, a subclass of `UnauthorizedAccessException`). UI catch filters should include it.
- Views get the user's sheet role as a cascading `SheetAccess` (`CanEdit`, `CanComment`) from `SheetPage`; hide edit
  affordances with it. A missing cascade means full access, so components still render in tests.
- Project accounting rules (`ProjectAccess`, also `IFinancialAccessService` for the UI): labor cost (the labor distribution)
  is Finance/Admin; project financials, budgets (reads and writes) and financial exports are Finance, Admin or the project's
  manager; contract funding is Finance, Admin or the manager of a project on that contract; hours and daily-entry reports
  show everyone to Timekeeper/Finance/Admin, direct reports to a supervisor, and only yourself otherwise; a project's ODC
  list is Finance/Admin/PM, others see only their own entries. Automation rules: Viewer reads, Editor changes. Dashboards:
  the owner (or an admin) edits; workspace members view workspace dashboards. Pages under `/setup` use `SetupLayout`
  (Finance/Admin, friendly 403 via `ForbiddenNotice`).
- Timesheets are approved by the supervisor, or by Admins for employees without one; Timekeeper/Finance/Admin view all.
- Development signs in at `/account/dev-signin` (seeded users: dev@ Admin, pm@ Finance and workspace Owner,
  analyst@ Editor, viewer@ Viewer). Service tests set the user with `CollaborationHost.User` / `Identity/SeededHost`.
## Security events, user lifecycle and retention

- Record security-relevant actions with `Application.Security.SecurityEventLog.RecordAsync(SecurityEventTypes.X, target,
  outcome, detail)` (scoped; writes its own row, never throws). It's append-only: never update or delete rows. Details
  are short and never contain secrets, tokens or cost/bill rates. Permission denials from `AccessControl` and HTTP 403s
  are recorded for you.
- Sensitive exports (CSV of labor, rates or financials) must record an `export.*` event; see
  `Application/Security/SecurityAuditDecorators.cs` for the decorator pattern (registered in `AddWorkbenchSecurity`,
  above `AddWorkbenchIdentity`).
- Users are deactivated, never deleted (`UserLifecycle`); deactivated users can't sign in and their sessions end at once.
  Which employee a user is: `Identity.EmployeeLinks.FindForUserAsync` (explicit admin link, else matching email).
- Before deleting anything tied to a contract or project, call `RetentionService.EnsureNotHeldAsync(db, "Contract" |
  "Project", id)`: legal holds block deletion. Time, ODC, journal and audit records are protected and never purged.
- Keep pages working under the Content-Security-Policy: no inline `<script>` or `on*=` handlers, no `eval`; put JS in
  files under `wwwroot` and import them. Inline styles are allowed (MudBlazor needs them).

## Notifications (any feature)

To tell someone something, inject `INotificationPublisher` (`Workbench.Application.Notifications`, a singleton) and
call it **after** your change is saved:

```csharp
await notifications.PublishAsync(
    approver.Email, NotificationKinds.TimesheetSubmitted,
    $"{employee.Name} submitted a timesheet", "Week of Oct 12: 40.0 h",
    link: $"time/approvals?week={weekStart:yyyy-MM-dd}");
```

- It saves the notification, pushes it live to the recipient's bell (and desktop app), and emails it according to the
  recipient's preference: right away, in the daily digest (default), or not at all. You don't pick the delivery.
- There's an overload taking several emails (each distinct valid email is notified once). Invalid or blank emails are
  skipped, never thrown; delivery failures are logged, never thrown.
- `kind`: a `NotificationKinds` constant (`timesheet.submitted/approved/rejected/missing`, `expense.approval`, ...) or
  your own short lower-case string. `link` is app-relative (`time/...`); absolute URLs throw `ArgumentException`.
  Pass `sheetId`/`itemId` instead of a link to point at a sheet item.
- The actor isn't added for you: say who did what in the title. Don't notify people about their own actions.
- For a raw email with no in-app notification, use `IEmailSender` with an `EmailMessage(to, subject, text, html)`.
  Keep HTML plain and self-contained (no external images, fonts or styles); see `NotificationEmails` for the layout.
- Scheduled checks (e.g. "timesheet missing for yesterday") belong in a `BackgroundService` registered from the web
  host (like `DateTriggerBackgroundService`), so notifications reach the in-process bell. Make them idempotent.


## Contract detail, indirect rates, billing and purchasing (wave 5)

- Shared entities: `Clin`, `ContractModification`, `FundingAction` (Domain/ProjectAccounting/Clins.cs) and `Vendor`,
  `Subcontract` (Domain/Purchasing/Vendors.cs). `WbsElement.ClinId` maps cost to a CLIN; `OdcEntry.VendorId` /
  `SubcontractId` link vendor ODCs. Seed: `ContractDetailSeed.cs` (CLIN 0001 CPFF 7% fee, CLIN 0002 cost-no-fee,
  P00001, vendors ACME and PRIMA, subcontract SUB-1001-01).
- `Clin.FundedValue` and `Subcontract.FundedValue` are running totals; change them only through funding actions,
  in the same unit of work, with an audit row.
- Burdened cost crosses modules through `Contracts/Billing/IBurdenedCostService` (implemented by indirect rates,
  consumed by billing and financials). Cascade: fringe on direct labor; overhead on direct labor + fringe; G&A on
  total cost input. Unallowable cost is never burdened or billed.
- Posting: every new module posts through the GL's journal service (sources `Billing`, `AP`, `Allocation` already
  exist in `JournalSource`), idempotently per source document, and registers its decorators above
  `AddWorkbenchIdentity`.
- Contract detail (`Contracts/Contracts`, `Application/ContractDetail`): `IClinService`, `IContractModificationService`,
  `IFundingService`. A SLIN is `0001AA` under `0001`; a CLIN with SLINs is a roll-up (its ceiling/funded = sum of its
  SLINs) and is never funded, charged or mapped directly. Contract value = sum of active (non-option or exercised) leaf
  ceilings; contract funded = sum of leaf funding; contracts with CLINs ignore typed-in totals. Value, option and funding
  changes go through an executed modification (immutable; corrections are new mods). The charging guard lives in
  `ListChargeCodesAsync`: no WBS mapped to an unexercised option or outside its CLIN's period. Limitation of Funds notices
  (`funding.limitation`) fire once per CLIN, threshold (`FundingNotices:WarningThreshold` 0.75 / `CriticalThreshold` 0.90)
  and funded value.
- Billing and AR (`Contracts/Billing/BillingContracts.cs`, `Application/Billing/`): `IInvoiceService`, `IBillingMilestoneService`,
  `IPaymentService`, `IArReportService`. Invoices post through `BillingLedger` (GL source `Billing`, source id = invoice,
  payment or accrual id) in the same unit of work as the approval. A hour, ODC or milestone is billed once: `BilledSource`
  rows hold it until the invoice is voided. Cost-plus bills cumulative cost less billed cost, so it reads
  `IBurdenedCostService` from the contract start; without a registered implementation it bills direct cost only and says
  so. Other modules can add a period-close checklist item by registering `Application.Accounting.IPeriodCloseCheck`.

## Imports (wave 6)

- `Application/Imports`: `IImportService` (Finance/Admin) runs one `IImporter` per source × kind in three modes
  (validate, dry run, apply) through `ImportRun`. Importers call the regular services, never write entities directly
  (exceptions: the plan-sheet/WBS links, as in `PlanSyncService`). Every touched entity is recorded as an
  `ImportJobRecord` with EF property snapshots; `ImportRollback` reverts them and refuses when anything changed or now
  references them (found from the EF model's foreign keys). A new entity with a foreign key to an importable entity is
  picked up automatically; a reference without one (like comments → items) needs an explicit check there.
- External ids Workbench has no field for (Smartsheet row/sheet ids) live in `ImportKeyMap`. Samples and the expected
  columns per file: `samples/import/README.md`.
