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

- Who is signed in: `ICurrentUser` (sync: email, name). Permissions, roles and the linked employee:
  `ICurrentUserContext.GetAsync()` (`CurrentUserInfo`: `Can(Permission.X)`, `AssignedRoles`, `EmployeeId`). Server code
  injects `Application.Identity.AccessControl` (`RequireAsync(Permission.X, "what", ct)`, `HasAsync`, `RequireSheetRoleAsync`).
- Two kinds of roles. **Workspace roles** (Viewer < Commenter < Editor < Owner) govern sheets, through membership of the
  sheet's workspace. **Organization roles** are bundles of **permissions** from the catalog
  (`Contracts/Identity/Permissions.cs`, documented in `docs/security/permissions.md`): the ten built-in roles (Admin holds
  every permission; everyone is an Employee) and custom roles from Admin › Roles. A user's permissions are the union of
  their roles'. Decide access with a permission, never with `OrgRoles` flags (a compatibility view of the built-in roles).
  A new capability gets a new permission (plus its built-in grants and a parity-test entry); a new page's menu entry
  declares its permission (`NavRule.Need`).
- A role assignment can be limited to companies, org units, projects or contracts. When a check is about a record, use
  `HasForProjectAsync` / `HasForContractAsync` / `HasForEmployeeAsync` (or `ProjectsWithAsync` etc. for lists) so limited
  roles count; plain `HasAsync` only counts roles that apply everywhere (fails closed).
- Sensitive fields are permissions too: mark the contract property `[property: SensitiveField(Permission.X)]` and return
  reads through `FieldMasker.Mask(dto, permissions)` in the permission decorator. Admin-defined things (custom fields,
  automations, forms) register their permissions at run time with `IPermissionRegistry`; read the full catalog with
  `IPermissionCatalog`. Details in `docs/security/permissions.md`.
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

## Recurring journals, corporate budgets and planning cycles

- `Application/Planning`, `Contracts/Planning`, `Domain/Planning`. Recurring journals post with `JournalSource.Recurring` (source
  id = the template, revision = the occurrence number) through `GeneralLedger` in one unit of work with their
  `RecurringJournalRun`; the unique run per occurrence keeps "Generate due entries" and the six-hourly job
  (`RecurringJournalSettings.AutoGenerate`) from both creating one. A closed period is a skipped run with a message, retried next time.
- Corporate budgets are `GlBudgetVersion`/`GlBudgetLine` (account × org unit × fiscal period, natural sign, revenue and expense
  detail accounts only), distinct from project `BudgetVersion`. Adopted versions are locked (`EnsureEditable`); changes go into
  a Revised copy. Split money with `Domain.Planning.Allocation.Split` (whole cents, largest remainder) so spreads add up exactly.
- Budgeted indirect rates reuse `PoolAccountMap` and `BurdenCascade.ActualRates` with budget amounts instead of GL balances.
- Planning cycles route each org unit's `BudgetSubmission` through the `budget-submission` approval workflow (default: Finance);
  the final approval replaces that unit's lines in the cycle's version. Owners edit their own unit; Finance/Admin everything.

## Multicurrency

- Domain `Domain/Currencies` (`Currency`, `ExchangeRate`, `CurrencySettings`, `CurrencyRevaluation`/`Line`), contracts `Contracts/Currencies`
  (`ICurrencyService`, `IRevaluationService`), services `Application/Currencies`. Pages: Setup › Currencies and rates (`/setup/currencies`),
  Accounting › Currency revaluation (`/accounting/revaluation`, linked from Period close) and the foreign currency report (`/accounting/foreign-currency`).
  Anyone signed in reads currencies and rates; changing them, the setup, revaluation and the report are Finance/Admin.
- Every amount the ledger and reports already read stays in the **functional currency** (USD, a tenant setting in `CurrencySettings`; it
  moves to the company once there are companies). A foreign document adds its `CurrencyCode` (null = functional, which is every
  document before multicurrency), the rate used and its amounts in that currency (`TransactionAmount`/`TransactionTotal`/`TransactionPaid`…).
  Journal lines carry `TransactionCurrency`, `TransactionAmount` (same side as the debit or credit) and `ExchangeRate`, null on functional lines;
  reversals copy them. Get a document's currency with `CurrencySupport.ResolveAsync` (spot rate on the date unless one is given) and convert
  with `DocumentCurrency`/`CurrencyMath` (half away from zero; transaction amounts to the currency's decimals, functional to the cent).
- A rate is functional units per foreign unit (EUR → USD 1.085). `CurrencySupport.FindRateAsync` takes the latest rate of the type on or before
  the date, the inverse pair works, and another type is a flagged fallback. Rates are typed in, imported from CSV, or fetched from an
  `IExchangeRateProvider` (none registered: no outside calls unless a host adds one).
- Payments relieve what a document carries at its own rate (`CurrencyMath.Relieved`: the last payment takes the rounding cent) and move cash at
  the payment's rate; the difference posts to realized gain/loss (4920) in the same entry. A foreign payment uses the active bank account
  kept in that currency (its GL account), else 1010. Project costs and billing stay functional: AP lines become functional ODCs, invoices are
  worked out in dollars and issued in the customer's currency (`InvoiceCurrency.ApplyAsync` at approval; credit memos use the credited
  invoice's rate). Foreign bills can't bill PO lines or link existing ODCs.
- The period-end revaluation (`RevaluationEngine`) values open foreign AR, AP (net of retainage) and foreign bank balances as of the period end
  at the month-end rate, posts one functional-only entry (source `Revaluation`) against unrealized gain/loss (4930) on the last day, and
  reverses it on the next period's first day; once per period. `CurrencyRevaluationCloseCheck` blocks the close while a balance would move.
  A foreign bank account must have its own GL cash account. 4920/4930 are in the default chart and added to older charts when first needed.

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

## Travel authorizations and corporate cards

- Entities (`Domain/Travel`): `TravelAuthorization` (+ `TravelAuthorizationDestination`), `TravelLineCategory`, `CardProgram`,
  `CorporateCard`, `CardStatementImport`, `CardTransaction`. Contracts: `ITravelAuthorizationService`, `ICardProgramService`
  (Finance/Admin), `ICardChargeService` (the cardholder's own) in `Contracts/Travel`. Decorators: `AddWorkbenchTravelPermissions`.
- A trip is approved through the engine (document type `travel-authorization`; default: the supervisor and the charged
  project's PM in parallel). Lodging and M&IE are never typed: `TravelEstimator` prices each night/day from the per diem
  tables through `PerDiemCalculator`. The approved trip is the budget of one expense report (`TravelAuthorization.ExpenseReportId`);
  `TravelComparison` categorizes its lines (chosen category, per diem kind, card MCC, then words) and flags categories over
  `Travel:TolerancePercent`. Over-budget isn't unallowable: unallowable and over-per-diem amounts stay with the per diem lines
  and the approver's unallowable flag. Advances are recorded (not posted) and deducted from what's reimbursed.
- Card statements are read by `ICardFeed` implementations (CSV, OFX; no live bank calls) and de-duplicated per program by
  `CardFeeds.DedupeKeys` (the bank's id, else a row hash plus its occurrence in the file). A charge becomes an ordinary expense
  line (`CardTransaction.OdcEntryId`); `TravelCardExpenseReportGuard` locks its amount and releases the charge when the line or
  report goes. Posting: a company-paid program's lines credit the program's liability account (default `2030 Corporate card
  payable`) instead of accrued reimbursements (`PostingContext.CardLiability`). Charges older than the program's
  `UnsubmittedAlertDays` and not on a submitted report are overdue (home to-do `cards:overdue`, Finance aging at `/cards`).

## Sales orders and shipping

- `Domain/Materials/Sales.cs` (`SalesOrder` + lines, `PickList` + lines, `Shipment` + lines and packages, `WawfReceivingReport`); contracts
  `ISalesOrderService`, `IShippingService` (`Contracts/Materials/SalesContracts.cs`); pages under `/materials/sales-orders`, `/materials/shipping`,
  `/materials/pick-lists`, `/materials/shipments` (`Ui/Sales`, `SalesLayout`) and the phone pick screen `/m/pick`. Orders: Materials/Finance/Admin
  change, Business development reads; fulfilment Materials/Finance/Admin; credit holds and the delivery invoice Finance/Admin.
- A released (or partly shipped) order's open lines are MRP demand (`SalesOrderDemand`, one line in `MrpService.LoadAsync`).
- Pick lists reserve stock by bin/lot/serial/owner (`SalesReservations`); nothing moves until the shipment is confirmed. Auto-picking takes the
  order project's contract stock, then general stock; government-furnished material is only picked on purpose (and only the project's own).
- Confirming a shipment issues through `StockEngine`: cost-type lines (non-FFP CLIN or contract) as `IssueToProject` to the order's WBS (5120),
  everything else as `SalesShipment` to cost of goods sold (`Materials:Sales:CostOfGoodsSoldAccountNumber`, default 5160, added on first use),
  with the project/WBS segments when the order has a WBS (then it also counts in `MaterialCostActuals`).
- The delivery invoice goes through billing's public services (`ShipmentInvoicer`: a completed milestone per FFP CLIN, then the FFP milestone
  draft), so liquidation and funding checks stay billing's. WAWF files go through `IWawfSubmitter` (default `ManualWawfSubmitter`, sends nothing).

## Quality and engineering change

- `Domain/Quality` (`InspectionPlan`/`InspectionCharacteristic`, `Inspection` + items/results, `QualityAttachment`, `DefectCode`, `Nonconformance` + items,
  `VendorDebitMemo`, `CorrectiveAction`, `EngineeringChange` + items and `EngineeringChangeWorkOrderReview`); contracts `IInspectionService`,
  `INonconformanceService`, `ICorrectiveActionService`, `IQualityFileService`, `IEngineeringChangeService` (`Contracts/Quality`). No new role: reads are
  Materials/Finance/Admin, changes Materials/Admin; dispositions and ECOs are decided by whoever their workflow routes them to.
- Stock has a status (`InventoryBalance.Status`: Available, InspectionHold, Nonconforming; part of the balance key, `StockKey.Status`). Only available stock
  is picked, issued, kitted, shown as available on work orders or netted by MRP; held stock is still costed and on the books. Moving between statuses is a
  `StatusChange` pair (no GL), always through `StockEngine.ChangeStatus`. A new place that reads balances for availability must filter on `Status`.
- Hooks (`Application/Quality/QualityHooks`): a part with an active receiving plan is received into hold with a pending inspection (any receipt path), a
  stock completion with an active final plan likewise; release opens in-process inspections and completion refuses while one is pending; release picks the
  BOM of an approved, date-effective ECO dated on or before the work order's start.
- NCR dispositions (`ncr-disposition`) and ECOs (`engineering-change`) go through the approval engine; the last approval carries out the disposition in the
  same unit of work (GL source `Inventory`, source id = the NCR): use as is (back to available), rework (a stock rework work order kitted with the material),
  return to vendor (credit inventory, debit receiving clearing at the PO price, PPV for the difference, `VendorDebitMemo` for AP), scrap (to 6152, another
  expense account, or a project WBS as direct material; on a work order through `ManufacturingService.ScrapCore`). An approved ECO becomes effective on its
  date (at approval when due, hourly by `EngineeringChangeBackgroundService`, or "Apply due changes"): the new BOM/part revision activates and open work
  orders on the old revision are flagged for review. As-designed (the BOM revision the effective ECOs give a serial or lot) vs as-built (the work order's BOM
  and genealogy) is `IEngineeringChangeService.ConfigurationAsync`.

## Shop floor: kits, backflush, landed cost, physical inventory, capacity scheduling

- Entities in `Domain/Materials` (`Kitting.cs`: `Kit`/`KitLine`/`KitPick`, `BackflushSetting`, `BackflushShortage`; `LandedCost.cs`; `PhysicalInventory.cs`:
  `PhysicalInventory`, `PhysicalInventoryBin`, `CountTag`; `CapacityScheduling.cs`: `WorkCenterCalendar`, `WorkCenterCapacityException`, `OperationSchedule`,
  `OperationLoad`, `CapacityScheduleRun`). Contracts `Contracts/Materials/ShopFloorContracts.cs` (`IKittingService`, `IBackflushService`, `ILandedCostService`,
  `IPhysicalInventoryService`, `ICapacitySchedulingService`). Reads Materials/Finance/Admin; changes Materials/Admin; landed cost drafts also Finance;
  **posting** landed cost and physical inventory adjustments is Finance/Admin.
- `StockEngine` knows two kinds of locked bins (loaded by `ShopFloorLocks`): open kits' staging bins are *reserved* (automatic picks skip them; a
  kit's stock leaves only by its issue or cancel) and bins of a physical inventory being counted are *frozen* (every movement in or out is refused,
  except that count's own posting). New stock movements get this for free through the engine; don't bypass it.
- Backflush runs inside `ManufacturingService.CompleteAsync` / `ReportLaborAsync` (same unit of work and journal entry) through `Backflush.ApplyAsync`:
  each backflushed component is issued up to its share of units done (an operation's completed units for components with an operation sequence,
  else the order's), FIFO from its configured bin/warehouse. What can't be issued becomes an open `BackflushShortage`, never a silent skip.
  BOM-level settings key on (BOM, component part) because saving a BOM replaces its component rows.
- Landed cost splits with `Allocation.Split` (whole cents). Average-cost parts: the share of units still on hand raises `Part.InventoryValue`
  (re-averaged); the share of units already gone follows the latest outgoing movements (project WBS → 5120 with segments, stock work orders → WIP
  or the variance once closed, adjustments/counts → 6152, else cost of goods sold). Standard-cost parts take it all as PPV. Value movements are
  `InventoryTransactionType.LandedCost` (quantity 0; the issued share is a + and − pair so `InventoryValue` stays the sum of amounts), and
  `MaterialCostActuals` reads them, so project financials include landed cost on material they already received.
- Physical inventory posts every difference through `InventoryService.CountDifference` (the cycle count posting) as `PhysicalCount` transactions in
  one entry; government-furnished stock moves by quantity only.
- Capacity scheduling is the pure `CapacityScheduler` (unit-test the math there) behind `CapacitySchedulingService`, which keeps queue positions and
  planner pins between runs. Pinned operations load at the full daily rate whatever else is there, so they can overload a week; that's the point.

## Sourcing, supplier portal and EDI

- Domain `Domain/Purchasing/Sourcing.cs` (`Rfq`, `RfqLine`, `RfqVendor`, `VendorQuote`(`Line`, `PriceBreak`), `RfqAward`),
  `SupplierPortal.cs` (`SupplierContact`, `SupplierSignInToken`, `SupplierPortalEvent`, `PurchaseOrderAcknowledgement`(`Line`),
  `ShipNotice`(`Line`), `SupplierInvoice`), `Edi.cs` (`EdiPartner`, `EdiMessage`). Contracts `Contracts/Purchasing/SourcingContracts.cs`
  (`IRfqService`), `SupplierPortalContracts.cs` (`ISupplierPortalService` for vendors, `ISupplierPortalAdminService` for buyers),
  `EdiContracts.cs` (`IEdiService`). Pages: Purchasing › Requests for quote (`/purchasing/rfqs`, compare at `/{id}/compare`),
  Purchasing › Supplier portal (`/purchasing/supplier-portal`), Admin › Integrations › EDI (`/admin/integrations/edi`). Buyers are Finance/Admin.
- An award (`IRfqService.AwardAsync`) creates one PO per vendor through `IPurchaseOrderService` (ceilings and permissions apply;
  a failure cancels the ones made), marks sourced requisition lines ordered, and freezes the comparison in `RfqAward`
  (quotes received, sole source, price reasonableness, rationale). Rules: a price reasonableness note always; a sole-source
  justification when an awarded line had fewer than two responsive quotes; a rationale when a line isn't awarded to its
  lowest price; expired and no-bid quote lines can't be awarded. Scoring weights: `Purchasing:Sourcing`.
- The supplier portal is a separate sign-in kind (`docs/security/supplier-portal.md`): cookie scheme `SupplierPortal`, plain
  server-rendered pages under `/supplier` in `Workbench.Web/Supplier` (no Blazor circuit). Don't add vendor-facing code
  elsewhere, and never give `ISupplierPortalService` a vendor id parameter: the vendor is `SupplierCaller`'s, set by the
  `VendorScopedSupplierPortalService` decorator. Every portal call is logged (`SupplierPortalLog`).
- Vendor documents (portal and EDI alike) go through `Application/Purchasing/SupplierDocuments`: acknowledgements, ship notices
  (`ASN-` numbers; receive one with `ISupplierPortalAdminService.ReceiveShipNoticeAsync`, an ordinary receipt) and invoices,
  which land as **draft** AP bills on the PO lines (EnteredBy "… (supplier portal)" or "EDI (CODE)") for AP to submit and match.
- EDI: `Application/Purchasing/Edi` (`X12` reader/writer, `X12Documents` for 850/855/856/810, `IEdiTransport` with
  `FileDropEdiTransport` and `SftpEdiTransport`, which needs a host-registered `ISftpEdiClient`). Issuing a PO (or a change
  order) to a vendor with an active partner that takes 850s sends one (`EdiSendingPurchaseOrderService`); failures are log rows,
  never errors. Inbound sets are applied once per partner and ISA13/ST02. Samples: `samples/edi/`.

## Bank payment files and sales tax

- Bank files (`Domain/Purchasing/VendorBanking.cs`, `Domain/Cash/PaymentFiles.cs`, `Contracts/BankFiles`, `Application/BankFiles`):
  `IVendorBankService` and `IPaymentFileService`, Finance/Admin. A vendor's ACH routing and account numbers are Data Protection
  ciphertext (`VendorBankProtector`, purposes `Workbench.Purchasing.VendorBank{Routing,Account}.v1`) with the last four digits in
  clear; they change only through a `VendorBankChange` routed through the `vendor-bank-change` approval workflow, which its
  requester can never decide (enforced in the service as well as the default workflow). Requesting, approving and revealing need
  a recent sign-in (`SensitiveActions.BankDetails`) and are in the security log; audit rows hold masked numbers only.
- Payment runs make files only for download, never sent anywhere: `NachaWriter` (CCD/CCD+, one credit per vendor, addenda
  `RMR*IV*…`, optional balanced offset, file ID modifier A–Z then 0–9 per bank account per day, trace numbers stable per run and
  stamped on the scheduled payments as their references) from `BankAchSettings` on the paying bank account; `PositivePayWriter`
  from a built-in preset or a stored `PositivePayLayout`, issues and voids, or voids only. ACH is US dollars only. Every download
  is a `PaymentFileDownload` row (counts, total, SHA-256), an audit row and an `export.payment_file` security event. Remittance
  advice is a plain `IEmailSender` email per vendor (the approved remittance email, else the contact email), logged as
  `RemittanceAdvice`. The old `IApPaymentService.ExportAchAsync`/`ExportPositivePayAsync` CSVs remain for the API but the UI uses
  the bank files panel. Known-good sample: `samples/bank/nacha-ccd-sample.ach`.
- Sales and use tax (`Domain/SalesTax`, `Contracts/SalesTax`, `Application/SalesTax`, Setup › Sales tax, Accounting › Sales and use
  tax return): `ISalesTaxService`, Finance/Admin. Jurisdictions carry effective-dated, append-only state/county/city/district rates
  (CSV import). Tax codes are Taxable, Exempt or Resale and are assigned to invoice line kinds, ODC types, GL accounts or parts;
  unassigned lines use the default sales or purchase code (none = taxable). Calculation goes through `ISalesTaxEngine`
  (`TableSalesTaxEngine` unless a host registers another first; nothing calls out); totals round half away from zero to the cent
  and the levels add up to the total.
- Invoices: `InvoiceTaxApplier` runs at approval (and on "Calculate tax" for a draft) and keeps one `InvoiceLineKind.SalesTax` line
  plus an `InvoiceTax` record; `BillingLedger` credits sales tax lines to the sales tax payable account (default 2150), never
  revenue. Ship-to is the customer's `CustomerTaxProfile` unless the invoice overrides it. Government customers (categories in the
  settings) are exempt unless marked taxable; a current `TaxExemptionCertificate` for the state exempts the sale, an expired one is
  noted and taxed. Credit memos aren't re-taxed. Sales tax lines carry no CLIN or project, so CLIN funding checks ignore them.
- Bills: `ApBillTax` records the vendor-charged tax (already in the lines), where the purchase is used (else the home jurisdiction)
  and an optional bill-wide code. `UseTaxAccrual` runs after a bill posts (never failing the posting) and when its tax is saved: no
  vendor tax on a taxable purchase accrues use tax (debit 6250 use tax expense, credit 2160 use tax payable, GL source AP, source id =
  the `ApBillTax` row); a change reverses the earlier accrual first. Accounts 2150/2160/6250 are added to the chart when first used.

## Integrations (webhooks, API keys, connectors)

- `Domain/Integrations`, `Contracts/Integrations`, `Application/Integrations`; Admin › Integrations (`/admin/integrations`). Guides:
  `docs/integrations/webhooks.md` (payload schema, signatures), `api-keys.md`, `connectors.md`. All three services are Admin-only and
  closed to API key service users (`IntegrationPermissionDecorators.cs`).
- **Raising an event:** call `IntegrationOutbox.Raise(db, IntegrationEventType.X, IntegrationEventPayloads.X(entity), actor)` next to the
  audit row, before `SaveChangesAsync`, so the event is in the same transaction as the change. A new event type is a domain
  `IntegrationEventType` + data record (ids and summary fields only: never rates, pay, bank, tax, EEO or sheet cell values; the
  redaction test enforces it), the same key in `WebhookEventCatalog`, a payload builder, and a row in `docs/integrations/webhooks.md`.
- The background `IntegrationDispatcher` fans events out to deliveries, sends them (signed, retried with backoff, endpoints marked
  unhealthy after repeated failures) and runs scheduled connectors, per tenant. Tests call `RunOnceAsync` themselves; the web test
  factory turns the hosted dispatcher off.
- **Outbound calls** go only through `IOutboundTransport` (HTTP) and `ISftpUploader`, behind `OutboundUrlGuard` (https, public addresses
  only, rechecked at connect time). Tests register fakes; never call a third party from a test.
- **API keys** (`Authorization: Bearer wbk_…`) authenticate as a service `AppUser` with the key's org roles, so permission decorators
  apply unchanged; `ApiKeyScopeEnforcement` then limits them to `ApiKeyScopes.Areas` by the first path segment after `/api/v1/`.
  A new top-level API path is closed to keys until you add it to an area (or to the test's closed list):
  `ApiKeyWebTests.Every_api_path_belongs_to_a_key_area_or_is_deliberately_closed` fails until you do.
- A connector is an `IConnector` registered with `AddScoped<IConnector, T>()`; event connectors post through the delivery queue,
  schedule connectors return a file (kept in the blob store and optionally sent over SFTP).

## Tax books, CPSR and CAS disclosure

- An asset's own depreciation (on `FixedAsset`) is the book (GAAP) book and the only one that posts. Federal, state and AMT
  books are `AssetTaxBook` rows computed on demand by `Application/Assets/TaxDepreciation.cs` (MACRS GDS/ADS, 150% DB,
  straight line; half-year, mid-quarter and mid-month; Section 179 then bonus). Tax years follow the asset's company fiscal year.
- Asset transfers that change a contractor-owned asset's organization or company post a reclass through `GeneralLedger`
  (the company move sets each side's line company; the ledger adds due to/due from). Disposals keep each tax book's gain or loss.
- CPSR files (`ProcurementFile`, one per PO or subcontract) are scored by `CpsrScoring` against `CpsrOptions`
  (`Compliance:Cpsr`). RFQ competition evidence comes from an `IProcurementCompetitionSource` registered by the procurement
  module; compliance registers a no-op with `TryAdd`.
- CAS disclosure statements are versioned (`DisclosureStatement`, answers per item); only drafts change. Items linked to the
  setup (`DisclosureItems`, `DisclosureLink`) compare their answer with the live configuration, and
  `ICasDisclosureService.CheckConsistencyAsync` flags CAS 401/402 differences. Add a link there when a new setting is disclosed.
