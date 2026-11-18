# Extracting a module: workforce (HR records and payroll)

Phase 7 of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md); the earlier moves are
[govcon](module-extraction-govcon.md), [billing](module-extraction-billing.md), [capture](module-extraction-capture.md),
[materials](module-extraction-materials.md) and [project controls](module-extraction-projectcontrols.md); the order and class of each module come from the
[audit](module-extraction-audit.md), which called `workforce` class B for HR and payroll and class C for resourcing). This page is the inventory, every place core
touched the module and how it was resolved, **the change to the sold line** (resourcing is core now), the leave kernel, the data path for existing installs, and
what is still public.

The HR and payroll code now lives in the **private repository** (`Workbench-Modules`, a local git repository with no remote yet) as
`src/Workbench.Module.Workforce*`, builds into `dist/modules/workforce/` (copy it to a host's `modules/workforce/`), and loads like any plugin.
This repository ships without it.

## The price line changed: read this first

The catalog sold `workforce` as "HR records, positions, compensation, benefits, onboarding, payroll, **and cross-project capacity, utilization and skills**".
Resourcing stays in core (the Plan, timesheets, schedule health, the project hub, project visibility and the leave calendar read its records, and the docs
treat "assign people" as a core feature), so **the sold module is now HR and payroll only**:

| | Before | After |
| --- | --- | --- |
| `ModuleCatalog` text of `workforce` | "HR records, positions, compensation, benefits, onboarding, payroll, and cross-project capacity, utilization and skills." | "HR records, positions, compensation, benefits, onboarding, payroll and the Paylocity file exchange." |
| API segment `resourcing` | workforce | **core** |
| Permissions `projects.resourcing.manage` and `.view` | workforce (the `projects.resourcing.` prefix row) | **core** (the row is gone: they fall under `projects.`) |
| Menu: Resourcing (Projects) and My staffing (My work) | workforce | **core** |
| Service namespace `Resourcing` | workforce | **core** |
| Cross-project capacity, utilization, heat map, open demand, skills | paid | **free** (they are in `Application/Resourcing` and the Resourcing pages, which did not move) |

Nothing is enforced today (the module is not enforced), so no current license loses anything, but **the next price list must not sell "capacity, utilization and
skills" as workforce**. If the owner still wants them paid, the analytics (`Capacity`, `AllocationForecastService`, `ResourcePlanningService`, the heat map,
utilization and demand tabs, about 4,000 lines) need a gate of their own in core or a later module; that is the owner's call. The hosted trial and entitlement lists
need no edit: they name `workforce` by id, and no list, test or signup page named resourcing under it. `docs/commercial/modules-and-pricing.md` (the module's
line) and `website-copy-suggestions.md` ("Workforce (HR, payroll, capacity)") still say capacity: they are public pages, listed below, and were not edited.

Two more things people may notice are consequences of the same decision:

* **The People directory, the org chart, My profile and My benefits moved with HR** (they are the HR module's pages: the same personnel records and the same
  services). A host without the module has no `hr`, `hr/org-chart`, `hr/me` or `hr/benefits/me` pages and no People section in the menu; employees themselves
  (Setup › People) stay core. The audit counted these in the 53 HR files and listed `IHrAccessService` and `IHrReportService` as core in the map only because nothing
  was enforced. If the directory and org chart should stay free, they are about 600 lines (`PeoplePage`, `OrgChartPage`, `HeadcountTable`) plus `orgChart.js` and would
  need a core read service for the directory: say so and it is a small follow-up.
* **`hr.workforce.view` ("See workforce and pay data") and `hr.reports.restricted` stay in core's permission catalog** with their roles. Only the module's pages check
  them, so without the module they do nothing.

## What moved and what did not

**Moved to the module:**

| Feature | Core layers that held it |
| --- | --- |
| HR: personnel records (job, personal, emergency contacts, EEO, clearances and credentials, documents, change requests with approval), the directory and org chart, My profile | Domain `Hr/`, Contracts `Hr/`, Application `Hr/` (`HrPersonnelService`, `HrAccess`), UI `Hr/`, `wwwroot/orgChart.js` |
| Positions, compensation (the proposal and approval of pay changes, merit cycles, the pay rate history it writes), benefits (plans, tiers, open enrollment, life events, elections, deduction exports), onboarding and offboarding checklists, HR reports (headcount, turnover, EEO-1, VETS-4212, benefit summary), HR to-dos, the approval handlers for personnel and compensation changes | the same folders |
| Payroll: pay groups' admin and period generation, payroll profiles and the pay rate history, payroll runs (export hours, import the register, post, void), the provider adapters (Paylocity, ADP, Paychex, Gusto), connections and earning codes, the payroll file layouts and the Paylocity cost center mapping, export history and roster import | Domain `Payroll/`, Contracts `Payroll/`, Application `Payroll/`, UI `Payroll/` |
| The permissions `payroll.run`, `payroll.setup`, `payroll.profiles.manage`, `payroll.pay_rates.view`, `hr.records.manage`, `hr.positions.manage`, `hr.compensation.manage`, `hr.benefits.manage`, `hr.onboarding.manage`; every permission decorator; the `ModuleMap` rows for the segments `hr` and `payroll`, the prefixes `hr.` and `payroll.`, the groups "HR administration" and "Payroll", the 13 menu entries, the `people` API key area's `hr` and `payroll` segments | `Permission`/`PermissionCatalog`, `IdentityRegistration`, `ModuleMap`, `NavCatalog`, `ApiKeyScopes` |
| The People list as a chartable list source, the payroll to-do, the re-authentication prompt for revealing a pay rate, the `payroll/layouts` endpoints (they were under the migration endpoints) | `ListSources`, `FinanceTodoProviders`, `Reauthentication`, `MigrationEndpoints` |
| The sample data (a pay group with periods, earning codes, pay rates and profiles; HR positions, records, clearances, benefit plans, open enrollment and checklists) | `PayrollSeed`, `HrSeed`, `DatabaseInitializer` |

About 19,800 lines in 112 files (production code, UI and the module class: 13,200 lines in the module project, 1,650 in its contracts, 4,960 in its UI), 32 tables, plus
the moved tests (24 files in the module's test project). Pages, routes (`/hr/...`, `/payroll/...`), API paths (`/api/v1/hr`, `/api/v1/payroll`, `/api/v1/payroll/layouts`,
`/api/v1/payroll/paylocity`), permission keys and role behavior are unchanged.

**Stayed in core**, each for a reason:

* **Resourcing** (`ResourceAllocation`, `StaffingAssignment`, `SkillService`, capacity and utilization, the Plan's staffing, the pages): see the price line above.
* **The leave kernel: `PayGroup`, `PayPeriod`, `EmployeePayProfile`** (and the enums `PayFrequency`, `PayType`, `EmploymentType` with their contract mirrors) now live in
  `Domain/Payroll/PayCalendar.cs` and `Contracts/Payroll/PayKernelContracts.cs`. The leave engine reads them (eligibility by pay group and employment type, accruals per pay
  period); the payroll module creates and edits them but core owns the three tables. A pay profile also carries the pay type and rate on file, which is the module's to write;
  splitting the table was not worth a data release. A host without the module has no pay groups and leave runs as for people with no pay profile ("full-time, no pay group").
* **`LaborCostAdjustment`** (the labor cost correction after a back-dated rate change) and **`PostingEngine.AdjustTimesheetLaborAsync`**: the accounting posting engine writes and
  reverses them, and they are cost, not pay. **`LaborRateChanges`** (re-costing posted labor after a *cost* rate change) stays too: core's people service calls it. The part that turns a
  *pay* rate change into a cost rate moved to the module as `PayRateCostSync`.
* **`JournalSource.Payroll` and `LaborAdjustment`**, **`SensitiveActions.PayRateReveal`**, the approval document type names `hr-change` and `compensation`, and the `ReauthenticationGuard`: names core keeps so
  saved data and logs keep their meaning.
* **`Employee`, `OrgUnit`, `LaborCategory`, the leave tables, timesheets and the per-diem rates**: core.

### Decision: the leave kernel (pay calendar and holidays as core records)

[module-plugins.md](module-plugins.md#the-leave-kernel) predicted two shapes: the kernel entities stay core-mapped and the module reads them, or the leave engine asks a reader. The first
one was taken, and nothing about the leave engine's signatures changed (`LeaveEngine.AccrueAsync` and `ReverseAccrualAsync` still take a `PayPeriod`; the payroll run service calls them
from the module, so `LeaveEngine` became public). `Holiday` was already core. What is new in core for it:

* **`IPayGroupChoiceService`** (`Contracts/Leave`, `GET /api/v1/leave/pay-groups`, an HTTP client): the pay groups by code and name, for the eligibility choices of a leave policy. It replaces
  `IPayGroupService` in `LeavePoliciesAdmin` (that service is the module's). Empty without payroll.
* **The re-authentication prompt list**: core's `AddWorkbenchReauthentication` no longer decorates `IPayrollProfileService`; the module decorates it (under its permission checks, so someone
  without the role is refused before being asked to sign in again, as before).
* **`SetupSteps.PayrollAvailable`**: the setup guide's "Payroll provider" step and its link show only where the payroll page is in the menu. The step "done" check reads the pay group
  table (core's) instead of the provider settings table (the module's).

## Coupling: every place core referenced the module, and the resolution

The audit counted 19 core files for resourcing (which stays) and, for HR and payroll, 0 and 7 real references with about 7 hooks. Compiling the shipped repository after deleting the
code found **29 places**; none needed the module to stay.

| # | Core referenced workforce through | Resolution |
| --- | --- | --- |
| 1 | `LeaveEligibility`, `LeaveEngine`, `LeaveBalanceService`: pay group, pay period, pay profile | **keep as core records** (the kernel above) |
| 2 | `LeavePoliciesAdmin` (`IPayGroupService`, `PayGroupDto`) | **new core service** `IPayGroupChoiceService` |
| 3 | `Reauthentication`: `ReauthPayrollProfileService` | **move**, the module decorates its own service |
| 4 | `FinanceTodoProviders`: `PayrollTodoProvider` (and its registration in `MyTodoService`) | **move** (`IMyTodoProvider` is open to modules) |
| 5 | `MigrationEndpoints`, `HttpMigrationServices`, `MigrationPermissionDecorators`, `MigrationConfigurations`: the payroll file layout service, its client, decorator and two tables | **move** (the `payroll` segment's endpoints) |
| 6 | `PostingEngine.LaborAdjustments`, `Journal`: labor cost adjustments | **keep**: `LaborCostAdjustment` and its table are core's |
| 7 | `PeopleService` (core): `LaborRateChanges` after a cost rate change | **split** `LaborRateChanges`: the re-costing stays and is registered by core; the pay to cost rate step is the module's `PayRateCostSync` |
| 8 | `SetupAdminService`: "payroll set up" read the provider settings table | now reads `PayGroup` |
| 9 | `SetupSteps`, `SetupHome`: the payroll step and link | shown only when the payroll page exists (`SetupSteps.PayrollAvailable`) |
| 10 | `ListSources`: the People list (`IHrPersonnelService`, `PeoplePage`) | **move** to a `ListSource` row of the module; core's saved lists keep the `people` key |
| 11 | `DatabaseInitializer`: `PayrollSeed`, `HrSeed`, `HrSeed.HarperEmail` | `IModuleSeed` at the new stage `ModuleSeedStage.AfterCash` (where the payroll seed ran: before the leave seed) and the existing `AfterHr`; Harper's login stays a core dev user |
| 12 | `Permission` constants, `PermissionCatalog` rows, the separation-of-duties pairs | **move** (`WorkforcePermission`, `Describe().Permissions`); the pairs keep their keys as strings, so a role holding both still warns when the module is installed; `hr.workforce.view` and `hr.reports.restricted` stay |
| 13 | `SkillService`, `NavAccess.IsHr`: `Permission.HrRecordsManage` | the key as a string (a resource manager or HR may edit skills; without the module only the first applies) |
| 14 | `NavCatalog`: 13 entries | **move** (`NavEntries`; the groups "HR administration" and "Payroll" are the module's `NavGroups`) |
| 15 | `ModuleMap`: segments, prefixes, groups, pages, service namespaces, `IHrReportService` and `IHrAccessService` mapped core | **move** to the module's rows; `resourcing` and the `Resourcing` namespace map core |
| 16 | `ApiKeyScopes` "people" area: `hr`, `payroll` | the module adds them (`ApiKeySegments`) |
| 17 | `DependencyInjection`, `IdentityRegistration`: the HR, payroll, Paylocity and layout registrations and permission decorators | **move** (`ConfigureServices`) |
| 18 | `WorkbenchApi`: `MapPayrollLeaveEndpoints`, `MapPaylocityEndpoints`, `MapHrEndpoints`; `ApiClient`: `AddPayrollLeaveClients`, `AddHrClients` | the leave endpoints and clients stay (`LeaveEndpoints`, `AddLeaveClients`); the rest moved |
| 19 | `LeaveSeed`, `PayrollConfigurations`, `HrConfigurations`, `MigrationConfigurations` | split: the three kernel configurations stay, the others moved with the entities |
| 20 | `OnboardingService` (HR) read `LeaveEngine.BalancesAsync` | `LeaveEngine` is public |
| 21 | Old core migrations that created, altered or populated the tables | see Data |
| 22 | Tests that used the HR and payroll kits (`HrKit`, `PayrollKit`) for something that is not HR or payroll | see Tests |

Links to `hr/employees/{id}` in the chat connectors (`employee.hired`, `employee.terminated`) and the automation entity "Employee" are text in core; without the module they land on a missing page.

### Slots, contributions and hooks added

Few, because this extraction was mostly existing seams:

| What | Where |
| --- | --- |
| `ModuleSeedStage.AfterCash` | `Application/ModuleSeed.cs` |
| `IPayGroupChoiceService` and its endpoint and client | Contracts, Application and Api of `Leave` |
| `LeaveEngine` public; `LaborRateChanges` registered by core | `Application` |
| `SetupSteps.PayrollAvailable` | Contracts `ProjectAccounting` |
| Through existing seams: 13 menu entries, 9 permissions and their areas, the `hr` and `payroll` segments, 5 to-do providers, 2 approval document handlers, 2 sample-data seeds, a list source row, API key segments | `ModuleContribution`, `IMyTodoProvider`, `IApprovalDocumentHandler`, `IModuleSeed` |

## Data: existing installs keep their tables, new installs get them from the module

Same rule and recipe as the earlier modules: no table is dropped, no core migration row changes for an installed database, and the module can be added or left out of an upgrade at any time.
32 tables and 52 indexes, with their foreign keys, are inside the module. Its foreign keys point at nine core tables that it maps key-only (employees, org units, labor categories, ledger accounts,
fixed assets, journal entries, stored files and the kernel's `PayGroup` and `PayPeriod`). **No core table has a foreign key into the module's tables.**

The 32 tables: HR 21 (`Position`, `EmployeeHrProfile`, `EmployeeEeoRecord`, `EmergencyContact`, `EmployeeCredential`, `EmployeeDocument`, `HrChangeRequest`, `CompensationChange`, `MeritCycle`, `BenefitPlan`,
`BenefitTier`, `OpenEnrollmentWindow`, `BenefitDependent`, `BenefitElection`, `BenefitElectionDependent`, `LifeEvent`, `BenefitDeductionExport`, `ChecklistTemplate`, `ChecklistTemplateTask`, `EmployeeChecklist`,
`ChecklistTask`) and payroll 11 (`EmployeePayRate`, `PayrollProviderSetting`, `PayrollProviderConnection`, `PayrollRun`, `PayrollExportLine`, `PayrollExportRecord`, `PayrollRegister`, `PayrollRegisterLine`,
`PayrollFileLayout`, `PaylocityCostCenterMapping`, `PaylocityRosterSetting`). The audit's "47 entities in 7 migrations" counted the leave and kernel entities too.

1. **`ReleaseWorkforceTables`** (core, both providers): the model no longer maps the entities. EF generated 32 `DropTable` statements; they were removed by hand, `Up` and `Down` are empty. Only the snapshot changes.
   `has-pending-model-changes` is clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL.
2. **Five old core migrations no longer create the tables** (`InitialCreate` 19 statements, `GoLiveReadiness` 6, `BusinessDevelopmentHrMaterialsAssistant` 79, `PayRateHistoryLaborAdjustments` 3, `AddPaylocityExchange` 9; the same
   five in both providers), with `strip-core-migration-tables.py` unchanged. Databases that already ran them are unaffected.
3. **One data step was removed from `PayRateHistoryLaborAdjustments`**: two `INSERT`s that gave every pay profile saved before the pay rate history a history row, and gave employees without a cost
   rate the matching one. They wrote into `EmployeePayRate`, which core no longer creates, so they would have failed on any database old enough to still run that migration. A database that already ran it is unaffected.
   **A database from before 2026-10-24 (the pay rate history) that upgrades straight to this release gets the tables from the module's baseline but not that backfill**; `PayRates.BackfillAsync` in the module does the
   same idempotently and runs when the sample company is seeded, but nothing runs it at startup. Say if it should (a second module migration is the shape).
4. **The module's `WorkforceBaseline`** (first migration of `WorkforceDbContext`, history `__EFMigrationsHistory_workforce`): 84 statements, each `CREATE ... IF NOT EXISTS`, `Down` empty. No hand edit to the generated
   baseline was needed (no column of these tables got a default from a later core migration). The two ledger entities map to tables named `GlAccounts` and `GlJournalEntries`, not the class names: the module's context says so.
5. No foreign key crossed the line, so upgraded and new databases are identical.

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseWorkforceTables`: no table touched | the baseline finds the tables, changes nothing | tables and rows stay, unused |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then the release | the baseline creates the tables with the current schema | no tables, none needed |
| New | no HR or payroll tables | the baseline creates them | none |

## Tests

| Shipped suite | Now |
| --- | --- |
| `Hr/*` (7 files with their kit), `Payroll/*` (11 files, the provider fixtures, `PayrollKit`) | moved to the module (24 files with their kits and fixtures) |
| `Leave/LeaveTests`, `LeaveWorkflowAndEligibilityTests` used `PayrollKit` | now use a new `LeaveKit`, which writes the pay group, periods and profiles straight to core's kernel records (no payroll service); "posting payroll runs the period's leave accrual" moved to the module (`PayrollLeaveAccrualTests`) |
| `Identity/ScopedRoleAndFieldTests`: the salary masking and EEO tests | moved (`HrFieldPermissionTests`); the field count now says 7 |
| `Identity/BuiltInRoleParityTests`: nine rows | moved to the module's `Permissions_are_in_the_catalog_and_held_by_the_same_roles_as_before` |
| `Identity/SignInPolicyTests`, `OrganizationSsoWebTests`: the pay rate reveal as the sensitive action | now the bank account details; the pay rate case is the module's |
| `MyTodo/MyTodoProviderTests`: the payroll to-do | moved (`PayrollTodoTests`) |
| `Navigation/NavMenuTests`: the People section and the Payroll group | expectations updated; the two People tests moved (`PeopleMenuTests`) |
| `Setup/SetupComponentTests`: "7 of 14 done" | "6 of 13" (no payroll step without the module); the module's `ModuleSurfaceTests` asserts the step is offered with it |
| `Licensing/ModuleGateTests`, `ModuleEntitlementTests` | `workforce` joins the modules "no service maps to it in this build"; the menu test no longer expects Workforce pages in core |
| `Migration/ReconciliationServiceTests`, `Identity/CustomRoleTests` | the payroll layout and pay group refusals moved to the module's permission tests |
| new in core: `Modules/WorkforceAbsentTests` | a new database has none of the 32 tables and keeps the pay calendar and leave tables; the permissions, API segments, menu entries and list source are absent while `resourcing` and the two core HR permissions are core; the module's API paths answer 404; leave accrues on the core pay calendar with no payroll module and the eligibility choices list the pay group |
| new in the module | `MigrationPathTests` (a new install; the baseline equals the previous release's schema; an existing install keeps every table, row and column and gains only history rows, twice; an upgrade without the module keeps its data), `ModuleSurfaceTests`, `ApiSmokeTests`, `PayrollLeaveAccrualTests`, `HrFieldPermissionTests`, `PayrollTodoTests`, `PeopleMenuTests` |

Removed: `PayRateMigrationTests` (it ran the data step of item 3 above).

## Documentation, demo data and public pages that describe workforce

Nothing was removed; whether the public site keeps describing a paid module is the owner's decision. Copies of three pages are in the module repository's `docs/`
(`user-guide-people.md`, `finance-payroll.md`, `admin-guide-paylocity.md`). The public references are:

* **HR, directory and benefits:** `docs/user-guide/people.md`, `docs/user-guide/index.md`, `docs/admin-guide/organization.md`, `users-and-access.md`, `docs/getting-started/getting-around.md`, `docs/security/permissions.md`,
  `docs/integrations/webhooks.md`, `docs/sheets/formulas.md`, `docs/demo-hosting.md`, `docs/reference/glossary.md`, `docs/compliance/nist-800-171/*`, `docs/commercial/data-processing-addendum-draft.md`
* **Payroll and Paylocity:** `docs/finance/payroll.md`, `docs/finance/index.md`, `accounting.md`, `cash.md`, `revenue.md`, `docs/admin-guide/paylocity.md`, `hosting.md`, `setup.md`, `docs/integrations/costpoint.md`,
  `docs/migration/costpoint-cutover.md`, `docs/multicompany.md`, `docs/operations/configuration.md` (the `Payroll:Paylocity` options), `docs/CONVENTIONS.md`, `docs/iso/*` and `docs/training/*` (payroll in the agendas and cards)
* **Commercial and marketing:** `docs/commercial/modules-and-pricing.md` (the module's price line, "capacity, utilization and skills" and "Workforce" in the plan tables), `website-copy-suggestions.md`, `end-user-terms-draft.md`,
  `soc2-readiness.md`, `docs/editions.md`, `docs/feature-coverage.md`, `docs/index.md`, `README.md`, `mkdocs.yml`, the marketing pages `site/index.html` and `site/costpoint-alternative.html`
* **In the application:** the help articles `users-and-roles`, `month-end-close`, `admin-getting-started`, `WhatsNew.md`, the glossary and the tours (`Help/Tours.cs`) mention payroll or HR; `samples/payroll/*` are the Paylocity sample files (the module's tests read them from this repository)
* **Internal handover notes:** `docs/operations/payroll-leave.md`, `fix-payroll-leave.md`, `hr-benefits.md`, `paylocity-formats-research.md`, `w2-labor.md`, `w2-platform.md`
* **The sample data:** the sample pay group, pay periods, pay rates and payroll profiles, and all HR sample records (positions, EEO, clearances, benefit plans, checklists, Harper Reed's and Morgan Blake's employee records) exist only with the module. The development login `hr@example.com` stays.

## Verification

* Shipped repository, no `modules/` folder: `Workbench.Server.slnf` builds (the MAUI desktop project cannot be built on this machine; it does not reference the module); `tools/Workbench.LoadData` builds; the filtered groups pass:
  Licensing, Modules, Navigation, Tenancy, Migration, Identity, Reports, Customization, Accessibility, GoldenPath, Dashboards, Time, Leave, Payroll, Hr, Setup, Resourcing and Plan (**962 passed, 1 skipped**: the PostgreSQL numbering test; the
  Payroll and Hr groups moved to the module and match nothing here). The shipped host starts without the module (no `fail:` lines in its log) and with it.
* Module repository, module loaded from the built folder: its **126 tests** pass on InMemory and on SQLite (`WORKBENCH_TEST_DB=Sqlite`); the capture (213), materials (77, after one menu expectation lost its People section) and project controls (100)
  suites still pass against this core. `Workbench.Module.GovCon.Tests` does not compile (see *Not done*). A fresh database with all five modules loaded (`modules/govcon`, `capture`, `materials`, `projectcontrols`, `workforce`): every migration applies,
  the sample data seeds (one pay group with 78 periods, 5 positions, 5 pay rates, 2 leave policies, accruals), no `fail:` lines.
* `has-pending-model-changes`: clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL, and for the module's `WorkforceDbContext` on both, checked with `dotnet ef` directly, never with `scripts/new-migration.sh`.
* **Upgrade from the previous release's host** (`3fbb6df`, the base of this change; published, run in Development so it seeds the sample company; 341 tables), against this release's host (published without a `modules/` folder, and with `modules/workforce`):
  * the *upgrade* (the previous release's database, once with the module and once without): only `ReleaseWorkforceTables` ran in core, and `WorkforceBaseline` with the module (history table `__EFMigrationsHistory_workforce`, one row);
    compared with the previous release's host restarted on the same database, **every table's row count (the same 341, plus the module's history table), every row of the 32 tables and the `CREATE` text of the 32 tables and their 52 indexes are identical in both cases**
    (the stored text of an index the baseline creates ends with a newline; nothing else differs);
  * a *new* database made by this host **with** the module has the 32 tables identical to the previous release's (`CREATE` text, indexes, and every row once ids and timestamps are masked) and the same row counts in all other tables except `NotificationPreference`
    (a restart of any host adds 8, and the previous release's database was not restarted); **without** it the database has none of the 32 tables and the host starts. The sample company then has no pay group, pay periods, profiles or HR records, and two employees fewer
    (Harper Reed's and Morgan Blake's records are HR sample data); its leave sample data is unchanged.
* Module tests on SQLite (`MigrationPathTests`): a new install; the baseline's schema equals the previous release's (columns with type, nullability and default, foreign keys and indexes, table by table); an existing install keeps every table, row and column, gains only history rows, twice; an upgrade without the module keeps its data.
* Not run: PostgreSQL itself (the private repository's CI should run `WORKBENCH_TEST_DB=PostgreSql` and a PostgreSQL upgrade; in particular the two foreign keys to `GlAccounts` and `GlJournalEntries`), the full shipped suite (known to hang locally), the MAUI desktop build.

## Not done

* The worker and the desktop app do not load modules: the HR and payroll pages and clients are not in the desktop app (they were).
* `Workbench.Module.GovCon.Tests` does not compile against this core (`Workbench.Tests.Evm` and `MutableClock` left with the project controls kit); that was already so on the base of this change.
* The saved-data names `hr-change` and `compensation` (approval document types) stay in core's list of built-in document types; a host without the module lists two workflow types nobody can submit.
* The pay-rate-history backfill of item 3 under Data.
* PostgreSQL itself (the baseline is plain `CREATE ... IF NOT EXISTS` DDL for 32 tables), the full shipped suite (known to hang locally), the MAUI desktop build.
* The capture and materials UI import `./_content/Workbench.Ui/evm.js`, a file the project controls extraction moved to its own module's folder: a broken path in two other modules, found while reading the module repository; not touched here.

## Lessons for the next extraction (enterprise, accounting)

* **Grep the table names in every old migration for `Sql(` too**: the strip script handles operations, not raw SQL; one old migration ran `INSERT INTO "EmployeePayRate"` and would have broken an upgrade.
* **Tables of other modules and the ledger have names that are not their class names** (`GlAccounts`, `GlJournalEntries`): key-only mappings need the real table name, or the baseline's foreign keys point at a table that does not exist. The migration path test caught it only because it compares foreign keys with the previous release's.
* **A kit that wires many services for one test file is a coupling in disguise**: the leave tests needed three rows of core records, not the payroll services; writing them directly (`LeaveKit`) is what let the leave suite stay in core.
* **A static list that names a page (`SetupSteps`) needs the same "is the page there" check as the menu**; the cheapest is to ask `ModuleMap.ForNav`.
