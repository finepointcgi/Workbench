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

## Sign-in, MFA and re-authentication

- The organization's identity provider, MFA rule (off / admins / everyone), just-in-time provisioning and session policy
  are tenant settings (`SignInSettings`, `ISignInSettingsService`, **Admin › Users and security › Sign-in**), read through
  the cached `SignInSettingsStore`. Every sign-in goes through `SignInGate.EvaluateAsync(..., claims)`; don't add sign-in
  paths that bypass it. Guides: `docs/security/sso-entra.md`, `sso-okta.md`.
- A **sensitive action** (revealing pay, bank or tax data; changing bank accounts, approval workflows or sign-in settings)
  calls `ReauthenticationGuard.RequireRecentAsync(SensitiveActions.X)` in a decorator registered by
  `AddWorkbenchReauthentication` (above `AddWorkbenchIdentity`, so permission checks run first). It throws
  `ReauthenticationRequiredException` (a 403); the layout's `ReauthenticationPrompt` offers "Sign in again". Add new
  actions to `SensitiveActions`. Without an interactive session (jobs, service tests) the guard doesn't apply.

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

## Procure-to-pay and fixed assets (wave 6)

- Entities: `PurchaseRequisition(Line)`, `RequisitionApproval`, `PurchaseOrder(Line)`, `PurchaseOrderRevision`,
  `PurchaseReceipt(Line)` (Domain/Purchasing/Procurement.cs); `AssetCategory`, `FixedAsset`, `AssetDepreciationRun`,
  `AssetDepreciation`, `AssetTransfer` (Domain/Assets/FixedAssets.cs). `ApBillLine` gained `PurchaseOrderLineId`, `Quantity`,
  `UnitPrice` and the match result/override columns; `PurchaseOrderLine.QuantityReceived` is a running total kept with receipts.
- Contracts: `IRequisitionService`, `IPurchaseOrderService` (receiving included), `IThreeWayMatchService`, `ICommitmentService`
  (Contracts/Purchasing/ProcurementContracts.cs); `IAssetService`, `IDepreciationService`, `IAssetReportService` (Contracts/Assets).
- A bill line with a `PurchaseOrderLineId` takes its charging from the PO line; the match (billed to date on live bills ≤
  received, price within tolerance) is recorded at submit and re-checked at approval, which it blocks unless Finance overrode it.
- Commitments are open PO line amounts less what posted bills billed; never posted. `ProcurementSupport.CommitmentsByWbsAsync`
  feeds CLIN funding status ("committed") and `ICommitmentService` the project financials page.
- GL sources `Asset` (capitalization reclass and disposal, source id = asset) and `Depreciation` (source id = run). Depreciation
  is the scheduled amount through the period less what's posted (late additions catch up once), unique per asset and period,
  so runs are idempotent; closed periods are refused; `DepreciationCloseCheck` adds "Depreciation posted" to the period close.
  Government-furnished and contractor-acquired property is tracked but never depreciated.
- An asset's schedule is `AssetSupport.Schedule(asset)` (method + `DepreciationConvention`: full month, mid-month or following
  month); use `ExpectedThrough` rather than calling the calculator yourself. Categories (`/assets/categories`, Finance/Admin)
  carry the default method, convention, life, accounts and an optional capitalization threshold (else `Assets:CapitalizationThreshold`);
  a category with assets is deactivated, never deleted.
- Opening balances (Costpoint "Fixed assets" importer): `OpeningBalanceDate` (a month end) + `OpeningAccumulatedDepreciation`,
  never posted. The schedule puts the opening amount in the cut-over month and spreads cost − salvage − opening over the
  remaining months, so runs resume the month after the cut-over and catch up once. Reports treat the opening amount as
  depreciation in the cut-over month. Asset exports record `export.assets` (`SecurityLoggingAssetReportService`).

## Payroll and leave (wave 6)

- Workbench never calculates payroll taxes. `Application/Payroll/IPayrollProvider` adapters (Paylocity; ADP, Paychex and Gusto
  are "coming soon" stubs that throw `NotSupportedException`) format the hours export and parse the provider's register.
  The Paylocity file formats Workbench assumes are documented on `PaylocityFormats`; sample: `samples/payroll/paylocity-register-sample.csv`.
- Pay rates (`EmployeePayProfile`) are what employees are paid, not cost rates. Every payroll service is Finance/Admin; rates are
  masked except `IPayrollProfileService.RevealAsync` (security event `payroll.pay_rate_revealed`); audit rows never hold rates.
- Payroll hours come from approved timesheets only; missing and unapproved timesheets are flagged. Overtime: non-exempt, over 40
  worked hours in a Monday–Sunday week (leave doesn't count). The payroll journal (`JournalSource.Payroll`, source id = register)
  clears 2100 at the GL's accrual, posts gross − accrual to 6050, employer taxes/benefits to 6020/6030 and credits 2120/2130/2140.
- Leave balances are an append-only `LeaveLedgerEntry` ledger. Usage is never typed: `LeaveEngine.SyncUsageAsync` keeps one net row
  per approved timesheet on the policy's charge code. Accrual (per pay period, idempotent by `SourceKey`) runs when payroll posts.
  Holidays aren't a balance. Seeds: `PayrollSeed.cs`, `LeaveSeed.cs`.
- Pay rates are effective-dated and append-only (`EmployeePayRate`; the profile's rate fields mirror the latest row). Read the
  rate for a date with `PayRates.HistoryAsync` + `InEffect`: runs use the rate in effect on the pay period's last day. A
  profile without rows reads as one rate from the hire date (`PayRates.BackfillAsync` stores it).
- Leave requests go through the approval engine (document type `leave-request`, default = the timesheet rule). Voiding a
  posted payroll run reverses that period's accrual (`LeaveEngine.ReverseAccrualAsync`); an accrual key counts as done while
  its rows net above zero, so posting again accrues again. Policy eligibility (`LeaveEligibility`: org units with sub-units,
  pay groups, exempt status, `EmploymentType` from the payroll profile; empty = everyone) gates accrual, balances and requests.

## Per diem and resource planning (wave 6)

- Per diem (`Domain/PerDiem`, `Application/PerDiem`, `Contracts/PerDiem`): rate tables are append-only batches (the latest
  `PerDiemRateBatch` per fiscal year and CONUS/OCONUS is in force). Pick rates through `PerDiemTables.Resolve` (season →
  standard CONUS → latest earlier year → built-in $110/$68); compute with the pure `PerDiemCalculator`. A travel report's
  per diem lives in `TravelPerDiem` (one per `ExpenseReport`); "Update expense lines" generates ordinary Travel `OdcEntry`
  lines through `IExpenseReportService` and links them in `PerDiemLine`. `PerDiemSubmitGuard` decorates
  `IExpenseReportService.SubmitAsync`; per diem lines other than Lodging need no receipt (`PerDiemReceipts`).
- Resourcing (`Domain/Resourcing`, `Application/Resourcing`, `Contracts/Resourcing`): `ResourceAllocation` is
  employee × project (× WBS) × week (Monday). Capacity = standard hours − holidays − approved leave − the non-billable
  allowance (`Resourcing:*`). Holidays and leave come from `IResourceLeaveCalendar` (no-op `NoLeaveCalendar` TryAdd'ed);
  the leave module registers its own. Who may do what: `ResourcingAccess` (org role `ResourceManager` manages every
  project, PMs their own, Finance reads; employees see themselves).

## Restricted columns

- `Column.IsRestricted` ("Finance and owners only"): Finance, administrators and the workspace's Owners see the column;
  for everyone else it doesn't exist. `RestrictedColumns.Of(columns)` adds every formula column that reads a restricted
  one (directly, through `CHILDREN`, or through another formula), so derived figures stay hidden too.
- Enforcement is in the permission decorators (`RestrictedColumnAccess`): sheet reads drop the columns, item reads drop
  their values, item writes ignore them and keep the stored values (updates send the full set the user can see),
  column changes and deletes of a hidden column are "not found", and only those who can see them restrict or lift a
  restriction. Rules that touch a restricted column (trigger, conditions, action fields or `{Column}` placeholders) are
  hidden from, and closed to, everyone else. Anything that reads sheet data through `ISheetService`/`IItemService`
  (dashboards, My Work) inherits this; code that reads `Item.Values` straight from the database must not show them to users.
- Activity is read by everyone on the sheet, so it records restricted changes without names or values
  ("Restricted column changed"). Activity written before a column was restricted keeps what it said.

## Reports (wave 7)

- `Contracts/Reports`: `IStandardReportService` (catalog, run, drill-down, export), `IReportWriterService` (curated data
  sources) and `ISavedReportService` (saved reports, schedules). Every result is a `ReportResultDto` with invariant string
  cells; `Application/Reports/ReportFiles` renders it as .xlsx (ClosedXML) or .csv (formula injection neutralized).
- A report never shows more than the pages it summarizes: standard reports call the permission-checked services; report-writer
  sources (`ReportSource`) filter rows through `ReportAccess` (the same rules as `ProjectAccess`) and mark fields
  `FieldRestriction.Finance` (cost rates, labor cost) or `ProjectFinancials` (contract values, blanked per row). Fields a
  user can't see aren't listed, and saved definitions that name them have those columns, filters and sorts dropped. Users
  choose fields by key; there is no SQL or expression input. To add a source, subclass `ReportSource` and add it to `All`.
- Saved reports are private or shared with organization roles; readers run them with their own access, only the owner edits.
- Schedules run as the user who created them (`ReportRunAs` + `RunAsAwareCurrentUser`, switched on only by
  `ReportScheduleRunner` outside HTTP requests), never as Workbench System. The next run is saved before sending (no repeats);
  deactivated owners' schedules stop. The attachment goes only to the owner; extra recipients (active users) get a link.
  `EmailMessage` takes optional `Attachments`. Exports record `export.report` security events.

## Ceilings and closeout

- `Application/Ceilings`: `ICeilingService` (hours/cost/billing ceilings at contract, CLIN, project, WBS and labor-category-per-WBS
  level, warn or block, overrides with an expiry, threshold alerts `ceiling.threshold`, indirect rate and fee caps) and
  `ICloseoutService` (Open → Physically complete → Closing → Closed; closing sets the contract or project status to Closed
  and reopening, with a reason, restores it). Hours = time on record; cost = hours at cost + submitted/approved allowable ODCs +
  open PO commitments; billed = approved/sent/paid invoice lines.
- Enforcement is in decorators registered by `AddWorkbenchCeilings` (above `AddWorkbenchIdentity`): timesheets, vendor ODCs,
  expense reports, AP bills, purchase orders and invoices. New charging paths should call `ChargeGuard` the same way.
  Refusals are `CeilingExceededException` / `ChargingClosedException` (both `ArgumentException`, so 400 over HTTP).
- Billing applies rate and fee caps through `Application/Billing/IBillingLimits` (optional in `BillingCostSource` and
  `InvoiceGenerator`); cost reports keep the real rates.

## Date rules act for their author

- Nobody is signed in when the hourly `DateTriggerBackgroundService` runs, so a date rule acts as the person who wrote it
  (`Rule.CreatedBy`). Each author's rules run, per tenant, in a scope from `RuleAuthorExecution.CreateScope` (internal,
  refused inside an HTTP request): `AccessControl` checks that author, through the ordinary permission-checked services.
  The author gets only their own roles: no system powers, and the worker's `SystemCurrentUser` doesn't leak into it.
- Checked at run time, before a sheet's rules run: the author must not be deactivated (memberships outlive deactivation,
  so this is explicit) and must be an Editor on the sheet's workspace. Otherwise the rules are skipped, logged, and recorded
  as a `Skipped` rule run with the reason (at most once per rule and day), and fire again once access is restored.
- This is deliberately not the "Workbench System" context (`SystemExecution`), which stays limited to the one burdened-cost
  read and never gains sheet data or writes. Don't use the author context for anything except running rules.
- Changes made by any rule (item or date trigger) are recorded as `Workbench Automation (rule "Name", author email)`
  (`AutomationContext.Actor`) in activity, comments and assignment notifications.

## Operations (production)

- A new configuration key goes in `docs/operations/configuration.md` (mark secrets **Secret**; secrets never in
  `appsettings*.json`). A setting that would be unsafe in Production belongs in `ProductionConfigurationCheck`.
- Background work runs through `ITenantJobRunner.ForEachTenantAsync`: that is what counts and times job runs
  (`workbench.jobs.runs`) for the failed-job alert. Let unexpected exceptions escape the callback instead of only logging them.
- Data that must stay secret at rest (TINs, bank accounts, tokens) is protected with `IDataProtectionProvider` and a
  versioned purpose (`Workbench.<Feature>.<Field>.v1`); the key ring is encrypted in Production
  (`docs/operations/data-protection-keys.md`). Never delete Data Protection key rows.
- Logs: structured templates (`{Count}`, not interpolation), no emails, names, amounts or secrets. Console JSON and OTLP
  redact emails and secret-named properties as a safety net only. Custom metrics go on `WorkbenchTelemetry.Meter`
  with low-cardinality tags (never people, tenants' data or amounts).
- Health: a dependency the app can't serve without is a `ready` health check returning Unhealthy; an optional one
  (like email) returns Degraded.
- Threshold alerts also go out hourly from `CeilingAlertBackgroundService` (Workbench System, every tenant); requisitions warn against cost ceilings unless the ceiling blocks and has `CheckAtRequisition`; fee caps may name a fiscal year, and billing then splits the CLIN's fee by the fiscal year of the cost (`InvoiceLine.FiscalYear`/`CostBasis`).

## Go-live migration and reconciliation

- `/setup/migration` (`IMigrationService`) is the cut-over checklist; steps name imports by kind **string**, so an
  importer another module adds (fixed assets: kind `FixedAssets`) lights up once its `ImportKind` exists. Status comes
  from `ImportJob` history plus hand marks (`MigrationStepRecord`); prerequisites block a step. Plan:
  `docs/migration/costpoint-cutover.md`.
- New import kinds `Accounts`, `Vendors`, `OpeningBalances`, `OpenArInvoices`, `OpenApBills` (appended to both
  `ImportKind` enums, which must stay equal by value). Open AR/AP are subledger detail written by
  `Imports/Costpoint/OpenItemConversion` without journal entries: the balances come in with the opening trial balance.
- `/setup/migration/reconcile` (`IReconciliationService`): `ReconciliationEngine` is pure (keys, mappings, tolerances);
  `ReconciliationSources` reads Workbench's side through the trial balance, aging, burdened cost and rate services. A
  new report is a `ReconciliationReports` entry plus a source; runs keep the uploaded file and are immutable once signed.
- Paylocity file layouts (`IPayrollLayoutService`, `PayrollFileLayout`): `PayrollRunService` calls providers through
  `PayrollLayoutDispatch` (`ExportHoursAsync`/`ParseRegisterAsync`); the defaults are exactly `PaylocityFormats`.

## Accessibility (WCAG 2.1 AA, Section 508)

Federal customers buy against Section 508, which means WCAG 2.1 AA. The conformance report is
`docs/compliance/accessibility/vpat.md`; keep it true when you add UI. Rules for new UI:

- **Every control has a name.** `MudIconButton` always gets `aria-label` (a `MudTooltip` around it is not a name). Repeated
  row actions say what they act on (`aria-label="@($"Delete {line.Description}")"`). Inputs get a `Label` (or `aria-label`
  when the column header labels them, as in the timesheet grid); a placeholder is not a label.
- **One tab stop per control.** Never nest a button or link inside another clickable thing. `MudMenu`'s `ActivatorContent`
  is already a focusable `role="button"`, so its content is a look-alike `<span>` with a visually hidden name
  (`Layout/NotificationBell.razor`, `Layout/UserMenu.razor`), not a `MudButton`. A clickable card puts a real link on its title
  (`Board/BoardCard.razor`). Clickable table cells hold a `<button>` (`Resourcing/HeatMapTab.razor`).
- **Pages** have one `<h1>` (`MudText Typo="Typo.h4" HtmlTag="h1"`) and headings in order below it (`HtmlTag="h2"` for card
  titles; set `HtmlTag` whenever the Typo is a heading style used as plain text). Layouts provide the landmarks (skip link,
  `<header>`, `<nav aria-label="Main menu">`, `<main id="main-content">`); a page doesn't add another `<main>`.
- **Data tables**: `<th scope="col">` headers (MudTable/`MudTh` do this), `scope="row"` for row headers, and a name:
  `aria-label` on the table or a `<caption>`. Layout tables (rare) get `role="presentation"`.
- **Color is never the only signal** (1.4.1). Status chips carry text and, where the color matters, an icon; heat maps and
  critical-path markers add a shape (▲ ● ○). Use the theme colors (`WorkbenchTheme`), which meet 4.5:1 for text in both
  themes; don't hard-code hex colors or use `Color.Secondary` for text. Text over a tinted background needs 4.5:1 too.
- **Charts** go inside `<AccessibleChart Title=… Labels=… Series=…>` (`Ui/Accessibility`): the chart becomes one named image and
  the same numbers are offered as a data table. If the page already shows the figures in a table, pass `ShowTable="false"`.
- **Progress bars** with a value get an `aria-label` saying what they measure.
- **Dialogs** come from `IDialogService`; the providers set Escape to close and focus to the first field, and MudBlazor traps
  focus and returns it to the opener. Give a dialog without a title `AriaLabel`.
- **Announcements**: snackbars are announced (role="alert"). For a change the user didn't trigger by moving focus (a card
  moved, a result count) use a visually hidden `role="status"` region (`class="wb-sr-only"`). When an action removes the
  element that had focus (an approved inbox row), move focus somewhere sensible (`ElementReference.FocusAsync()`).
- **Drag and drop always has a keyboard way** (board: "Move to" menu; Gantt: dates in the Grid view and the schedule table,
  dependencies under "Depends on" in the item details panel; grid: "Column width" in the column menu).
- **Busy buttons** may use `Disabled="_busy"`: `a11y.js` keeps focus on a button that disables itself while it works (it
  gets focus back when re-enabled, or focus moves to the next control if it stays disabled). Don't remove a focused button
  from the page instead; if an action removes it, move focus yourself.
- **Time limits**: the idle sign-out warning lives in `js/session.js` (plain DOM, because anything the circuit renders counts
  as activity and would reset the idle clock). Don't add other time limits without a warning and a way to extend.
- **Input purpose** (1.3.5): a field that asks for the user's own name, email or phone gets `autocomplete="name"` /
  `"email"` / `"tel"`; one about someone else (an employee, an invitee, a vendor contact) gets `autocomplete="off"`.
- **Breadcrumbs** use `<AccessibleBreadcrumbs>` (a labelled `nav`, no `href="#"` items).
- Helpers: `.wb-sr-only` (visually hidden text) and the focus ring live in `Ui/wwwroot/a11y.css`.

Checks:

- **bUnit**: call `A11yAssert.Check(component)` (`tests/Workbench.Tests/Accessibility`) in component tests. It fails on
  unnamed buttons/links/inputs, nested interactive controls, unnamed data tables and images without alt.
- **axe-core**: `tests/a11y/scan.mjs` runs axe (WCAG 2.1 A/AA rules) on every page for each seeded role, light and dark, at
  1280px and 375px, and exits non-zero on any serious or critical violation. Start a fresh dev server on port 5287 with
  rate limiting off, then:

  ```bash
  cd tests/a11y && npm ci && npx playwright install chromium && node scan.mjs
  ```

  (`RateLimiting__Enabled=false` on the server: the scanner signs in once per role, but session pings add up.) Use
  `A11Y_PAGES=/time,/expenses` to check a few pages while you work.
- **Browser behaviour** axe can't see (the idle warning, busy-button focus): `node keyboard.mjs` against the same server.
- **Keyboard**: before merging new UI, do the task with the keyboard alone (Tab, Shift+Tab, Enter, Space, Escape, arrows) and
  check the focus ring is always visible.

## HR (people)

- Org role **HR** (`OrgRoles.Hr`, `AppUser.IsHr`, `ApprovalOrgRole.Hr`); `IsHr()` is HR or Admin. Pages are under `/hr` (menu section
  People). Domain `Domain/Hr`, contracts `Contracts/Hr`, services `Application/Hr`, gates `Identity/HrPermissionDecorators.cs`.
- A personnel record is the existing `Employee` (supervisor, org unit, FLSA `IsExempt`, hire/termination dates) plus one
  `EmployeeHrProfile` (job title, position, employment type and status, work location, personal contact). `HrAccess.LevelAsync` decides
  what a caller sees: HR/Admin everything, the employee their own, supervisors (up the chain) and Finance the job fields and
  credentials, everyone else the directory. Employees change their own personal fields only by an `HrChangeRequest` (approval
  document type `hr-change`).
- EEO/veteran data (`EmployeeEeoRecord`) is HR, Admin and the employee only. Reading someone else's is a security event
  (`hr.eeo_viewed`); audit rows for EEO and personal fields name the fields, never the values (Finance reads the audit log).
- Compensation never keeps its own rates: an approved `CompensationChange` (document type `compensation`: HR, then Finance over
  `Hr:CompensationFinanceThreshold`) appends an `EmployeePayRate` and mirrors the payroll profile, so payroll and the Paylocity sync
  read it. Changes can't start inside a posted payroll period.
- Benefits: plan costs are monthly (`BenefitRules.PerPeriod` for payroll); enrollment rules live in `BenefitRules` and
  `BenefitService.ElectAsync`. Deductions go to payroll through `IPayrollProvider.ExportDeductions` (a default interface method;
  Paylocity implements it, `PaylocityFormats.WriteDeductions`). The payroll journal splits employer benefits by each plan's GL
  account (`BenefitFringe.SplitAsync`), so map plans to fringe accounts.
- Onboarding/offboarding tasks belong to HR, IT (administrators), Finance, the manager or the employee and appear as home to-dos
  (`ChecklistTodoProvider`). Offboarding deactivates the sign-in through `UserLifecycle`, recovers custodian assets through
  `IAssetService.TransferAsync`, checks the final timesheet, records the leave payout and ends benefits at month end.

## Materials and manufacturing

- Entities in `Domain/Materials` (`Part`, `Warehouse`, `Bin`, `InventoryBalance`, `InventoryTransaction`, `MaterialPurchaseLink`, `BillOfMaterial`/`BomComponent`,
  `WorkCenter`, `Routing`/`RoutingOperation`, `WorkOrder` + components/operations/labor reports/genealogy, `PartForecast`, `ProjectMaterialRequirement`,
  `MrpRun`, `PlannedOrder`). Contracts: `IPartService`, `IInventoryService`, `IManufacturingService`, `IMrpService`, `IMaterialReportService`
  (`Contracts/Materials`). Org role `OrgRoles.Materials` (64): Materials/Admin change, Finance reads; revaluation and the WIP report are Finance.
- Every stock movement goes through `StockEngine` (balances, moving-average or standard costing, the part's book value `Part.InventoryValue`) and
  posts through `MaterialsLedger` (sources `Inventory`, `Manufacturing`). Government-furnished stock (FAR 52.245-1) moves at 0, never posts and is
  never in average cost or valuation; contract and GFM stock is issued only to its project.
- Purchasing is unchanged: part PO lines are account lines on 1405 (received not invoiced); `InventoryReceivingPurchaseOrderService` decorates
  `IPurchaseOrderService.ReceiveAsync` so any receipt of a linked line puts the goods into stock (Dr inventory, Cr 1405, PPV for standard cost); the
  AP bill from the PO clears 1405. Links live in `MaterialPurchaseLink` (requisition line or PO line), never on purchasing's tables.
- Issues to a project WBS (directly or through a project work order) debit 5120 with project/WBS segments and reach the financials and burdened
  cost as direct ODC through `MaterialCostActuals` (called from `FinancialsLoader` and `DirectCosts`). Stock work orders collect material and shop
  labor (hours × the employee's cost rate, Dr WIP / Cr 6195 applied labor) in 1450, relieve it at completion and post the rest as the variance at close.
  Shop floor hours also go on the timesheet (the DCAA record) against the work order's charge code; the work order page compares them.

## Proposal pricing

- `Domain/Pricing` (`Proposal`, `PriceModel` and its periods, CLINs, WBS, escalations, labor lines/hours and cost lines),
  `Contracts/Pricing` (`IProposalPricingService`, `IPricingOpportunityLookup`), `Application/Pricing`. Pages under `/pricing`
  (`PricingLayout`). Finance, Admin and the org role `Pricing` (`OrgRoles.Pricing`, `CanPrice()`) see and edit prices; the
  won handoff is Finance/Admin. Cost volume exports record `export.pricing`.
- A proposal has one working price (`IsBase`), scenarios (copies) and locked versions. Locking copies the price into the next
  version and stores the computed `PriceResultDto` in `LockedSnapshot`, so a submitted price never moves with the books.
- Rates come from the books, never retyped: direct rates from `EmployeeCostRate` as of `RatesAsOf` (category average/median of
  people whose default category it is, selected people, a named person, or a typed rate), escalated once per full year to each
  period's start; indirect rates from `RateBook` for the period's fiscal year on the model's basis (`IndirectRateBasis.ForwardPricing`
  was added for this), falling back to the latest earlier year and then provisional. Burden is `BurdenCascade.Apply` per CLIN ×
  period × WBS, the same cascade and rounding as `IBurdenedCostService`. Unallowable cost lines are listed, never priced.
- `PriceCalculator` is pure: test the math there. T&M/labor-hour labor is priced at bill rates (direct × labor multiplier × (1 + fee));
  their ODCs at cost with G&A and no fee.
- `Proposal.OpportunityId` has no foreign key (the CRM owns opportunities). `IPricingOpportunityLookup` finds nothing until the CRM
  registers an adapter over `Contracts.Crm.IOpportunityService` (a later `AddScoped` replaces the `TryAdd` default).
- Won handoff (`PricingHandoff`, one unit of work): contract CLINs per CLIN × period (0001 → 1001 for option 1, options
  unexercised, funded CLINs untouched), T&M bill rates effective at each period's start, project WBS `{project}.{code}`, and a
  budget version phased evenly over the fiscal periods each period covers (unphased past the calendar).

## AI assistant

- `Application/Assistant` (off per tenant until an Admin turns it on; `docs/security/ai-assistant.md`). The model answers only
  through `AssistantToolbox`: fixed, read-only tools that resolve **contract interfaces** from the caller's scope, so the
  permission decorators apply as for the pages. A new tool is a `Spec` (name `list_`/`get_`/`read_`, strict schema) plus a
  case; it returns a one-line `summary` with the key numbers, app-relative citations and the `Type:id` records it read.
  Never add a tool that writes, sends or runs a query; never read entities directly in a tool.
- Narratives (`ProjectNarrativeService`) and flag explanations go through `AssistantRunner.CompleteTextAsync`, which audits
  them; both fall back to deterministic text when the assistant is off. Timesheet flags are `TimesheetFlagEngine` (pure);
  a new rule is a key in `TimesheetFlagRules` and never blocks anything.
- Providers live in Infrastructure (`Assistant/AnthropicModelClient`, the official Anthropic SDK; Bedrock via
  `Anthropic.Bedrock`); tests and Development use `FakeAssistantModel` (`Assistant:EnableFakeProvider`).

## Multicompany

- Legal companies within a tenant (`Domain/Companies`, `Contracts/Companies`, `Application/Companies`; design and the
  shared-versus-owned line in `docs/multicompany.md`). Company-owned entities implement `ICompanyOwned`; their column is
  configured in `Configurations/CompanyConfigurations.cs` (default = `Company.DefaultId`, so migrations backfill existing rows).
- Post through `GeneralLedger`: set `JournalEntry.CompanyId` to the source document's company (leave lines empty unless
  a line belongs to another company); the ledger fills lines from their projects, adds due-to/due-from lines across
  companies and refuses closed or inactive companies. Never write journal lines with a company yourself to "balance".
- A new company-owned record type gets a `CompanyColumnConfiguration<T>` and, if users list or open it, a company filter in
  `CompanyPermissionDecorators.cs` (`CompanyGuard.FilterAsync` / `EnsureAsync` / `StampNewAsync`).
- Reports that add up ledger lines filter by `CompanyScope.VisibleCompanyIdsAsync()` (null = all companies).
