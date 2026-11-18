# Extracting a module: government contracting (`govcon`), the pilot

Phase 2 of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md)). The `govcon`
module was extracted as a pilot to prove the seam on real code and to measure the coupling. This page is the inventory, every place core
touched the module and how it was resolved, the data path for existing installs, and what the remaining modules should expect.

The code now lives in a **private repository** (a local git repository so far, `Workbench-Modules`, with no remote), builds into
`modules/govcon/`, and loads like any plugin. This repository ships without it.

## What moved and what did not

**Moved to the module** (`src/Workbench.Module.GovCon*` in the private repository):

| Feature | Core layers that held it |
| --- | --- |
| Incurred cost submission (ICE), `incurred-cost` API, Schedules A to K | Domain, Contracts, Application (`IncurredCost/`), API endpoints, HTTP client, EF configuration, seed, UI pages, print layout, `incurredCost.js`, permission decorators |
| Purchasing system review (CPSR) and CAS disclosure statements (DS-1), `compliance` API | Domain, Contracts, Application (`Compliance/`), endpoints, HTTP client, EF configurations, UI pages, permission decorators |
| CMMC assessment | same as above |
| DCAA readiness page (`admin/dcaa-readiness`) | UI page only; its checklist service is generic and stays in core (see below) |
| Total time accounting: the policy and reports service, `total-time` API, the timesheet summary | Contracts, `TotalTimeService`, endpoints, HTTP client, UI, seed, permission decorators |
| Costpoint parallel run, `migration/parallel-run` | Contracts, service and explainer, endpoint, HTTP client, UI page, permission decorator |

About 9,100 lines of production code in 79 files, plus 14 test files (about 1,900 lines) and the module class, context and baseline migrations.

**Stayed in core**, each for a reason (details under *Coupling*):

* **Government billing** (progress payments, performance-based payments, PIEE, SF 1443, contract financing and liquidation) stayed in
  this pilot: it is woven into invoice generation, voiding and ready-to-bill. **It moved afterwards, in phase 3a, behind an
  invoice-financing hook**: see [module-extraction-billing.md](module-extraction-billing.md).
* The labor cost engine: `LaborCostBook`, `LaborCostAudit`, `TotalTimeCosting`, `LaborCostPolicy`. GL posting and the timesheet reports
  cost every hour through it, with or without the module.
* `IProcurementCompetitionSource` and `CompetitionEvidenceDto`: purchasing implements the source, the module reads it.
* The DCAA checklist service (`IComplianceChecklistService`, `ComplianceChecklistEntry`): one generic checklist engine, also used for
  the security checklists.
* The Reports hub, the menu, the permission catalog and the API key scope lists, as *extension points* the module fills.

## Inventory by layer

Everything that `ModuleMap` assigned to `govcon`, or that only the govcon features used, by layer. "Core after" is what this repository
keeps.

| Layer | Govcon content | Core after |
| --- | --- | --- |
| Domain | `Compliance/` (Cpsr, Cmmc, CasDisclosure: 8 entities), `IncurredCost/` (2 entities) | none; `Billing/GovernmentBilling.cs` stays (government billing), `ProjectAccounting/LaborCostPolicy.cs` stays (engine) |
| EF configurations | `ComplianceConfigurations`, `CmmcConfigurations`, `IncurredCostConfigurations` (8 tables) | none |
| Contracts | `Compliance/`, `IncurredCost/`, `TotalTime/`, `Migration/ParallelRunContracts.cs` (now `Workbench.Contracts.ParallelRun`) | `Compliance/ProcurementCompetition.cs` (the one purchasing seam) |
| Application | `Compliance/`, `IncurredCost/`, `TotalTime/TotalTimeService`, `Migration/ParallelRun*`, three permission decorator files and the parallel-run decorator | `TotalTime/` engine files; registrations removed |
| API | `ComplianceEndpoints`, `IncurredCostEndpoints`, `TotalTimeEndpoints`, the parallel-run group of `MigrationEndpoints` | none |
| API client | `HttpComplianceServices`, `HttpIncurredCostService`, `HttpTotalTimeService`, `HttpParallelRunService` | none (the desktop app does not load modules, see *Not done*) |
| UI | `Compliance/`, `IncurredCost/`, `TotalTime/`, `IncurredCostPrintLayout`, `incurredCost.js`, `ParallelRunPage`, `Pages/Admin/DcaaReadiness` | `UiSlot` (the extension point) |
| Seeds | `IncurredCostSeed`, `TotalTimeSeed`, the CPSR/DS-1 half of `TaxBooksComplianceSeed` | `TaxBooksSeed` (the tax books half) |
| Migrations | the eight tables in three core migrations | a new `ReleaseGovConTables` migration; the three old ones no longer create the tables |
| Tests | `Compliance/` (3 files), `IncurredCost/` (3), `TotalTimeServiceTests`, `ParallelRun*` (3) | split tests listed below |
| Docs | admin-guide `cmmc-assessment.md`, `compliance.md` and the ICE/TTA text | **unchanged** (owner decision, see *Documentation*) |

## Coupling: every place core referenced the module, and the resolution

Resolutions: **move** (the code goes with the module), **invert** (core owns an extension point, the module fills it), **keep** (the
type stays in core, usually because core needs it without the module).

| # | Core referenced govcon through | Resolution |
| --- | --- | --- |
| 1 | `ModuleMap`: API segments, permission prefixes (`admin.compliance`, `time.total_time`, `compliance.`), nav group `admin/Compliance`, nav pages, service namespaces (`Compliance`, `IncurredCost`, `TotalTime`), `IParallelRunService` | **move** the rows into `GovConModule.Describe()`. Government billing rows (`IProgressPaymentService`, `IPerformanceBasedPaymentService`, `IGovernmentBillingService`, `reports/standard/funding-status`) stay. |
| 2 | `Permission` constants and `PermissionCatalog.BuiltIn` (4 permissions) | **move** the definitions to the module (`GovConPermission` constants live in the module's contracts). **Keep** the `admin.compliance` *key* in core: the generic checklist service checks it. |
| 3 | `NavCatalog` (7 entries) | **move** to `Describe().NavEntries`. |
| 4 | `ApiKeyScopes` (`total-time` in the *time* area, `incurred-cost` in *accounting*) | **move** to `Describe().ApiKeySegments`. |
| 5 | `AddWorkbenchApplication` and `IdentityRegistration`: service and permission-decorator registrations | **move** into `ConfigureServices`. The decorators are the module's own, added right after its services, before the host's edition gates. |
| 6 | `EditionGates`: a hard-coded `Gate<IIncurredCostService>(Feature.IncurredCost, ...)` | **invert**: new `ModuleContribution.FeatureGates`. The edition feature stays a core enum (the license server and pricing read it); which service methods it gates is the module's to say. |
| 7 | `WorkbenchApi`: `MapIncurredCost/TotalTime/ComplianceEndpoints` | **move** to `MapEndpoints`. |
| 8 | `PostingEngine`, `TimeReportService` call `LaborCostBook`/`LaborCostAudit`/`LaborCostPolicies` (the `TotalTime` namespace) | **keep** the engine in core; only the policy/report *service* and its API moved. A host without the module posts labor with the policy in the database (Standard unless a policy exists). |
| 9 | `IceSource` reads `LaborCostPolicies`, `RateBook`, `PoolAccountMap`, `PurchasingSupport` (core internals) | the engine and these helpers are core's. **Made public** (`RateBook`, `RateSet`, `PoolAccountMap`, `PurchasingSupport`): see *Seam changes*. |
| 10 | `RfqCompetitionSource` (purchasing) implements `IProcurementCompetitionSource`, declared in `Contracts.Compliance` | **keep** the interface and DTO in core (`Compliance/ProcurementCompetition.cs`). The module's `CpsrService` takes the registered source, or finds nothing when none. |
| 11 | `StandardReportService`: an "Incurred cost submission" page-only entry | **invert**: `StandardReportPage` (a registered record, public in Application). |
| 12 | Core pages embedding module components: `TimePage` and `HistoryPage` (`TimesheetTotalTime`), `MigrationPage` (the parallel-run button) | **invert**: `UiSlot` component and `UiSlotContribution` (named slots `timesheet.summary`, `setup.migration.actions`). |
| 13 | `DatabaseInitializer` called three govcon seeds in a fixed order | **invert**: `IModuleSeed` with a `ModuleSeedStage` (after revenue, after planning, after tax books, end), so the sample data is built in the same order. The CPSR half of `TaxBooksComplianceSeed` moved; the tax books half stayed (`TaxBooksSeed`). |
| 14 | `WorkbenchDbContext` | none needed: configurations are applied by assembly scan, so removing the files removed the entities. |
| 15 | Core EF migrations and snapshot held the eight tables | see *Data* below. |
| 16 | Contracts: `ParallelRunContracts` sat in `Workbench.Contracts.Migration`, a core namespace | **move** to a module-only namespace (`Workbench.Contracts.ParallelRun`), otherwise `ModuleMap` calls the service core's. Contract namespaces of a moved module must not be a core namespace. |
| 17 | ApiClient helpers (`ApiResponses`, `LedgerUrls`, `HttpProjectAccountingExtensions`) were `internal` | made public so a module's HTTP clients can use them. |
| 18 | Tests spanning the module and core | see *Tests*. |
| 19 | Government billing (`ContractFinancingLedger` called from `InvoiceGenerator`, `InvoiceService`, `ReadyToBill`; `ContractGovernmentBilling`, `ProgressPaymentRequest`, `PieeSubmission` read and deleted by `InvoiceService`; billing pages host its cards) | **keep for the pilot; moved in phase 3a** ([module-extraction-billing.md](module-extraction-billing.md)): about ten call sites became the `IInvoiceFinancing` hook, the cards became `UiSlot`s. (`ClinFundingCalculator` and the other funding engines never read the entities; they stayed.) |

## Data: existing installs keep their tables, new installs get them from the module

Existing databases have the eight tables from core's migrations, with customer data in them. The rule is that no table is dropped, no
core migration row changes for an installed database, and the module can be added or left out of an upgrade at any time.

**Approach (a) was implemented, plus a change to three old core migrations so a *new* install has no govcon tables from core.**

1. **A new core migration, `ReleaseGovConTables`** (both providers). The model no longer maps the eight entities, so EF generated the
   drops; they were removed by hand. `Up` and `Down` are empty. It only updates the snapshot, which is how "core no longer owns these
   tables" is recorded. `has-pending-model-changes` is clean.
2. **The module's own context and history.** `GovConDbContext` maps the module's tables (and the two core tables its foreign keys point to,
   `PurchaseOrder` and `Subcontract`, key-only and excluded from migrations). Its first migration, `GovConBaseline`, is made from the
   EF-generated one by `scripts/idempotent-baseline.py`: every statement is `CREATE TABLE/INDEX IF NOT EXISTS`, and `Down` is empty. The
   history table is `__EFMigrationsHistory_govcon`.
3. **The three core migrations that used to create the tables no longer do** (`Wave7RevenueIceReportsEvmCeilingsApprovals`,
   `WavesCeManufacturingProcurement`, `AddCmmcAssessment`; the matching drop statements were removed from their `Down`). Databases that
   already ran them are unaffected: EF reads the history table, not the code. A new install without the module therefore has none of the
   tables; a new install with it gets them from the baseline.

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseGovConTables`: no table touched | baseline finds the tables, changes nothing, writes its history row | tables and rows stay, unused |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then `ReleaseGovConTables` | the baseline creates the tables with the *current* schema | no tables, and none needed |
| New | no govcon tables | baseline creates them | none |

The tables the module creates are identical to the ones core used to create, statement for statement on both providers. That was checked
by generating the previous release's migration scripts and comparing the eight tables' 18 `CREATE` statements (normalized
whitespace): **zero differences on SQLite and on PostgreSQL**. Tests cover the SQLite paths (below). **PostgreSQL was not run**: no local
instance was available; the PostgreSQL migration is covered by the script comparison, `has-pending-model-changes` and the provider-selection
code, and the private repository's CI should run the same tests with `WORKBENCH_TEST_DB=PostgreSql`.

**End to end on real data.** A database made by the previous release's host with the Development sample data (44 migrations, 4 procurement
files and approvals, a DS-1 with 14 answers) plus rows added for the tables the sample leaves empty (an ICE package with an input, a CMMC
practice) was upgraded by this release's host. With the built module folder (`modules/govcon`): only `ReleaseGovConTables` and
`GovConBaseline` were applied, every row count was unchanged and the schema of the eight tables was byte-identical before and after
(same checksum of `sqlite_master`). With no modules folder: only `ReleaseGovConTables` ran, the tables and rows were still there, and the
host started and served normally.

Not solved, and called out: a *downgrade* (running an older core against a database the module has changed) is not supported, as before.
If a later module release changes these tables, it does so with an ordinary migration in the module's own history.

## Seam changes this extraction needed

All additive; contract version is still 1.

* `ModuleContribution.FeatureGates`: edition-feature gates on module services (`ModuleFeatureGate`); applied like the built-in ones, skipped when the service is not registered.
* `UiSlot` (a Razor component in `Workbench.Ui`) and `UiSlotContribution`: named places on core pages a module can add components to.
* `IModuleSeed` and `ModuleSeedStage` (Application): module sample data at defined points of the development seed.
* `StandardReportPage` (Application): a page a module lists under Reports.
* `AddWorkbenchApplication()` without arguments now registers the modules the process has loaded, so a container built by hand (tests) sees what the web host sees.
* `ModuleHost.Load` clears the published module set while it loads. Without it a second `Load` in one process (every `WebApplicationFactory` host in a test run) found each of the module's own services "already taken" and refused it.
* `ModuleHost.StaticAssets()` and a `UseStaticFiles` in the web host: a module's `wwwroot` is served at `/_content/<assembly>`; a Razor class library loaded from a folder has no static web asset manifest of its own.
* Public surface widened for modules: `RateBook`, `RateSet`, `PoolAccountMap`, `PurchasingSupport`, `ApiResponses`, `LedgerUrls`, `HttpProjectAccountingExtensions`. Each is a signal that the next module will want a deliberate public API in that area instead of a pile of `public`.
* The shipped test project exposes its kits (`TestDb`, `SeededHost`, `WorkbenchWebFactory`, the fixtures) to the module's test assembly through `InternalsVisibleTo Workbench.Module.GovCon.Tests`, and `Samples.Root` honors `WORKBENCH_CORE_DIR`.

## Tests

Split or moved (listed so nothing is lost):

| Shipped suite | Now |
| --- | --- |
| `Compliance/CpsrTests`, `CasDisclosureTests`, `CmmcTests` | moved |
| `IncurredCost/IncurredCostTests`, `IncurredCostPermissionTests`, `IncurredCostKit` | moved |
| `TotalTime/TotalTimeServiceTests` | moved; `TotalTimeCostingTests` (the engine) stays |
| `Costpoint/ParallelRunTests`, `ParallelRunComponentTests`, `ParallelRunWebTests` | moved |
| `Accessibility/KeyboardPathTests` (the certifying official's name field) | that test moved to the module's `ModuleSurfaceTests` |
| `Identity/BuiltInRoleParityTests` | the four permissions' rows moved to the module's `Permissions_are_in_the_catalog_and_held_by_the_same_roles_as_before` |
| `Reports/ReportApiTests` | the incurred-cost listing moved (`Incurred_cost_is_listed_under_reports_to_open_not_to_run`); core asserts it is absent |
| `Licensing/ModuleGateTests` | the `IIncurredCostService` feature gate rows moved to `Incurred_cost_changes_keep_their_edition_gate_without_a_license` |
| `Licensing/ModuleEntitlementTests`, `Navigation/GovernmentNavigationTests` | govcon page expectations moved (`GovernmentNavigationTests` in the module) |
| new in core: `Modules/GovConAbsentTests` | a new database has none of the tables; the menu, permissions and API paths are absent; the host starts and the API paths are 404 |
| new in the module: `MigrationPathTests` | new install, the module's tables identical to core's old ones, existing install upgrade with rows, an upgrade without the module |

## Documentation

Nothing was removed from the public docs. The pages that describe this module are
`docs/admin-guide/cmmc-assessment.md` and `docs/admin-guide/compliance.md` (DCAA readiness, CPSR, CAS), `docs/finance/*` mentions of the
ICE package, `docs/compliance/dcaa/sf1408.md`, and the help articles under `src/Workbench.Ui/Help/Articles` (`ice-package.md`). Copies
of the two admin-guide pages are in the private repository's `docs/`. Whether the public site should keep describing a paid module is
the owner's decision; help articles are embedded resources read by the UI and would need a help-article extension point to move.

## Effort and lessons for the remaining modules

See the report that came with the pull request; the short version is in [module-plugins.md](module-plugins.md#extraction-lessons).

## Not done

* Government billing: done in [phase 3a](module-extraction-billing.md).
* The worker (`Workbench.Worker`) and the desktop app do not load modules; neither the module's API clients nor any background job it
  owns run there. The desktop app loses the govcon pages (it has no menu for them without the module's assemblies).
* Help articles (`ice-package.md`) stay in the shipped UI.
* The module is not signed, and the private repository has no CI or packaging yet (phase 4).
