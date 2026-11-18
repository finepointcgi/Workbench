# Extracting a module: project controls (earned value, ceilings, closeout, risks)

Phase 6 of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md); the earlier moves are
[govcon](module-extraction-govcon.md), [billing](module-extraction-billing.md), [capture](module-extraction-capture.md) and
[materials](module-extraction-materials.md); the order and class of each module come from the [audit](module-extraction-audit.md), which called
`projectcontrols` class B: extractable after some hooks, about nine of them). This page is the inventory, every place core touched the module and how it
was resolved, the data path for existing installs, the two services the audit flagged that live outside the module's folders, and what the next modules
should expect.

The code now lives in the **private repository** (`Workbench-Modules`, a local git repository with no remote yet) as
`src/Workbench.Module.ProjectControls*`, builds into `dist/modules/projectcontrols/` (copy it to a host's `modules/projectcontrols/`), and loads like any plugin.
This repository ships without it.

## What moved and what did not

**Moved to the module:**

| Feature | Core layers that held it |
| --- | --- |
| Earned value: the performance measurement baseline (work packages, milestones, techniques), status periods and snapshots, variance analyses (VARs), baseline change requests, IPMDAR Format 1 and 5 exports, the S-curve and charts, the Earned value tab and its dashboard widget | Domain `Evm/`, Contracts `Evm/`, Application `Evm/` (`EvmService`, `EvmEngine`, `EvmCalculator`, `EvmChangeRequestService`, `EvmExportService`), endpoints, HTTP clients, EF configurations, UI `Evm/`, `Pages/Projects/ProjectEvmPage`, `wwwroot/evm.js`, `EvmSeed` |
| Ceilings: hours, cost and billing ceilings on contracts, CLINs, projects, WBS elements and labor categories; warn or block; overrides; rate ceilings (an invoice generator limit); threshold alerts and the hourly alert job | Domain `Ceilings/`, Contracts `Ceilings/`, Application `Ceilings/` (`CeilingService`, `CeilingEngine`, `ChargeGuard`, `RateCeilingBillingLimits`, `CeilingEnforcementDecorators`, `CeilingAlertBackgroundService`), UI `Ceilings/`, `CeilingsSeed` |
| Closeout: the contract and project closeout checklist, waivers, final invoice, deobligation | Domain `Closeout/`, Contracts `Closeout/`, `CloseoutService` (it lived in Application `Ceilings/`), UI `Ceilings/CloseoutPanel` |
| The risk register | Domain `Risks/`, Contracts `Risks/`, Application `Risks/RiskService`, endpoints, client, UI `Risks/` |
| **The monthly project narrative** (a decision, see below) | Contracts `Assistant/NarrativeContracts`, Application `Assistant/ProjectNarrativeService`, its permission decorator, the four `/assistant/projects/{id}/narrative` endpoints, the HTTP client, UI `Assistant/ProjectNarrativeTab` |
| Permissions `projects.evm.manage`, `projects.ceilings.manage`; the permission decorators of every service; the `ModuleMap` rows for the segments `evm`, `ceilings`, `ceiling-overrides`, `rate-ceilings`, `closeout`, `risks`; the API key segments of the "projects" area | `Permission`/`PermissionCatalog`, `IdentityRegistration`, `ModuleMap`, `ApiKeyScopes` |
| The assistant's `get_earned_value` and `get_ceilings` tools | `AssistantToolbox`, `AssistantToolHost` |
| The project manager's variance and ceiling to-dos | `ProjectTodoProviders`, `MyTodoRegistration` |
| The sample data (earned value on project 1001, two ceilings) | `EvmSeed`, `CeilingsSeed`, `DatabaseInitializer` |

About 11,400 lines in 89 files (production code, UI and the module class: 7,690 lines in the module project, 867 in its contracts, 2,807 in its UI; the audit counted 58 files because it did not count the narrative, the slot components or the hooks), 15 tables, plus 12 moved test files (and 4 new ones in the module, 1 in core). Pages, routes (`/projects/{id}/evm` and the other tabs, `/setup/contracts/{id}` tabs),
API paths (`/api/v1/evm`, `/ceilings`, `/ceiling-overrides`, `/rate-ceilings`, `/closeout`, `/risks`, `/assistant/projects/{id}/narrative`), permission keys and role behavior are unchanged.

**Stayed in core**, each for a reason:

* **Schedule health (the DCMA 14-point check, its PDF and the Gantt panel)**: a decision, see below.
* **The `ProjectNarrative` table and entity** (`Domain/Assistant`): it is the assistant's own record (one narrative per project and period), `EvmExportService` and the narrative service read it and the module
  maps nothing for it. Moving the table would have meant a data release for one table with no benefit; the entity stays in core's model.
* **The `Ceiling`-free parts of the project hub**: the Overview, the alert list and the funding line (`ProjectHubService`), the project health chip, the project page itself.
* **`WidgetType.EarnedValue`** (the dashboard widget type is stored on saved dashboards, like `KpiKind.Pipeline`), **`SecurityEventTypes.ExportEvm`** (a string constant the module logs), `AssistantInteractionKind.Narrative` and
  the narrative prompts (`AssistantPrompts.NarrativeSystem`): small names core keeps so saved data and logs keep their meaning.
* **The billing kernel** (`IBillingLimits`, `InvoiceGenerator`, `BillingCostSource`, `IInvoiceService`) and **accounting** (`IBurdenedCostService`, purchase orders, requisitions, AP bills): the module reads them and wraps their services; they stay.
* **Contract `CeilingValue`** (the contract's total value, a field of every contract) is not a ceiling of the module and stays in the contract form and the funding widget.

### Decision: schedule health stays in core, the monthly narrative moves

The audit counted "schedule health and the project narrative" (`IScheduleHealthService`, `IProjectNarrativeService`) as licensed to `projectcontrols` but living in `Application/Scheduling` and `Application/Assistant`.

* **Schedule health: keep, and say so in the map.** It is the plan's analysis engine, 899 lines in Application and a panel on the plan page, and it is read by `ResourcePlanService`, a core dashboard widget type (`ScheduleHealth`), the Gantt page and the project hub's schedule tab.
  It reads the plan sheet only: no earned value, no ceiling, no table of the module. Moving it would have needed a seam for each of those readers for no data benefit. The `ModuleMap` row that said `IScheduleHealthService` belonged to
  `projectcontrols` is gone, so it maps to core (its `Scheduling` namespace). **This changes the paid line**: the module's catalog text (`ModuleCatalog`: "Earned value, the risk register, ceilings and closeout, schedule health and the monthly narrative") still names schedule health, and
  nothing enforced it before (the module is not enforced), so no license loses anything today. If schedule health is to be paid for, it needs gating in core on its own feature, not the module; that is the owner's call.
* **Monthly narrative: move.** `ProjectNarrativeService` assembles its facts from earned value status and variances, ceilings, the funding status and the plan; without earned value there is nothing to narrate. It moved with its permission decorator, endpoints, client and tab.
  Its contracts left the `Workbench.Contracts.Assistant` namespace for `Workbench.Contracts.Evm` (a module cannot claim a service interface in a namespace core already maps to core), and it keeps its URL.

## Coupling: every place core referenced the module, and the resolution

The audit counted 18 core files (12 real) and about 9 hooks. The real count was **about 30 files and 11 hooks** (the extra work was the same kind as in materials: places that named the module's records without a namespace using, found by compiling, and the narrative, the timesheet detail and the plan's category list). None needed the module to stay.

| # | Core referenced project controls through | Resolution |
| --- | --- | --- |
| 1 | `ProjectHubPage`: the tabs Risks, Earned value, Monthly narrative, Ceilings & closeout (a static list in `ProjectsFormat`) | **invert, new seam**: `ProjectHubTabContribution` (slug, label, component, who may open it, position, "More"). `ProjectTabSet` merges the built-in tabs with the contributions; a route for a tab the host does not have lands on the Overview |
| 2 | `ProjectOverviewTab`: the "See Ceilings" link on an alert | uses the same tab set: a link shows only when the tab exists and is open to the viewer |
| 3 | `ProjectSetupTab`: the "More: ceilings and closeout" section | **slot** `project.setup.more` (the section's heading says just "More" and keeps the contract link when the slot is empty) |
| 4 | `ContractPage`: the Ceilings and Closeout tabs | **slot** `contract.tabs` (the module's component renders the two `MudTabPanel`s; the tab-by-route indexes depend on whether the slot has contributions) |
| 5 | `ProjectFinancialsPage`: the ceilings card and the "Earned value" button | **slots** `project.financials.cards` and `project.financials.actions` |
| 6 | `WidgetView`, `WidgetDataService`, `WidgetDialog`, `WidgetDataDto.EarnedValue`: the earned value dashboard widget | **invert**: `WidgetDataService` asks the `IHomeWidgetProvider` with key `ModuleWidgetKeys.EarnedValue`; the data travels in the new `WidgetDataDto.ModulePayload` (JSON) and is drawn by the **slot** `dashboard.widget.earned-value`; the widget picker offers the type while the module's `ModuleHomeWidget` row exists. Saved widgets of the type say "a module that isn't installed or licensed" without it |
| 7 | `AssistantToolbox`, `AssistantToolHost`: `get_earned_value`, `get_ceilings` | **move** through the #47 seam (`IAssistantToolContribution`, MCP area `projects`); `AssistantToolbox.Money/Percent/Clip` became public for it |
| 8 | `ProjectTodoProviders`: `VarianceTodoProvider`, `CeilingTodoProvider` | **move** (the provider list is open to modules); the EAC and funding providers stay |
| 9 | `ProjectHubService`: the ceiling alerts on the Overview (and the `ICeilingService` it injected) | **invert, project lifecycle hook 1**: `IProjectAlertSource` (a list; none registered without the module) |
| 10 | `ProjectStartService`: the contract's labor-category hour ceilings seeded the first budget; `ProjectPlanService`: the capped categories in the plan's category list | **invert, project lifecycle hook 2**: `IContractCeilingSource` (`HourCeilingsAsync`, `CappedCategoriesAsync`) with the no-op default `NoContractCeilingSource` |
| 11 | `TimesheetQueries` (the room left under an hours ceiling on the timesheet page) and `TimesheetFlagService` ("ceiling-limited code") | **invert**: `ICeilingProximitySource` (`HoursRoomAsync`, `ForWbsAsync`), a list, nothing registered without the module |
| 12 | The ceiling and closed checks on timesheets, ODCs, expense reports, AP bills, purchase orders, requisitions and invoices (decorators registered by `AddWorkbenchCeilings`) | **move**, no new core hook: they are `ModuleServiceDecoration`s from materials. The host applies them right after the module's services, inside the permission and edition decorators, as before. The one change in order: they now wrap the last registration of each service, after Sourcing's decorator of the purchase order service and the receipts and leave decorators, instead of before them |
| 13 | `NotificationsHub`: the hourly ceiling alert job | **move**: the module registers it in `ConfigureServices` (web host only, like the engineering change job) |
| 14 | `DatabaseInitializer`: `EvmSeed` and `CeilingsSeed` | **invert**: `IModuleSeed` at `ModuleSeedStage.AfterRevenue`, where the seeds always ran, so every sample row count is as before |
| 15 | `Permission.EvmManage`, `CeilingsManage`; `ModuleMap` segments, prefixes, namespaces; `ApiKeyScopes` "projects" area | **move** (`ProjectControlsPermission`, `Describe().Permissions`, `ApiKeySegments`) |
| 16 | `ProjectPlanService.CategoriesAsync` read the `Ceiling` table directly (a core file the namespace search missed) | see 10 |
| 17 | `SetupRegistration` registered `IRiskService`; `IdentityRegistration` its decorator | **move** |
| 18 | Internals the module reused | `OpenCommitments.ByWbsAsync` (a public wrapper over `ProcurementSupport.CommitmentsByWbsAsync`), `DirectCosts` and `DirectCost` became public |

**Ceiling checks that core services call.** The task allowed for "a core hook with a no-op default on time, expense and ODC entry". None was needed for the checks themselves: they were never calls from the services, they were decorators, and the seam that wraps a host service already existed.
The two places where core *asks* the ceilings a question (the timesheet page's hours room and the flags) are the `ICeilingProximitySource` hook above.

### Slots and contributions added

| Name | Where it renders | Passed to the component |
| --- | --- | --- |
| `ProjectHubTabContribution` (not a slot: a tab) | the project page's tab bar and "More" menu | `ProjectId`, `SheetId`, `Changed` |
| `project.setup.more` | Setup tab, "More" section | `ProjectId`, `Changed` |
| `contract.tabs` | the contract page's tab strip, after "Customer link" | `ContractId`, `Changed` |
| `project.financials.cards` | beside the funding panel of a project's financials | `ProjectId` |
| `project.financials.actions` | the header buttons of a project's financials page (not when embedded) | `ProjectId` |
| `dashboard.widget.earned-value` | a dashboard widget of type "Earned value" | `Data` (the `WidgetDataDto`) |

## Data: existing installs keep their tables, new installs get them from the module

Same rule and recipe as the earlier modules: no table is dropped, no core migration row changes for an installed database, and the module can be added or left out of an upgrade at any time.
15 tables and 32 indexes, with their foreign keys, inside the module (the 8 core tables it points at are key-only: contracts, CLINs, projects, WBS elements, labor categories, budget versions, fiscal periods, invoices).
No core table has a foreign key into the module's tables.

1. **`ReleaseProjectControlsTables`** (core, both providers): the model no longer maps the entities. EF generated 15 `DropTable` statements; they were removed by hand, `Up` and `Down` are empty. Only the snapshot changes.
   `has-pending-model-changes` is clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL.
2. **Three old core migrations no longer create the tables**: `Wave7RevenueIceReportsEvmCeilingsApprovals` (14 tables; 59 statements), `AddRiskRegister` (`ProjectRisk`, 3), and `GoLiveReadiness`, which added the column `Ceiling.CheckAtRequisition` (2). The matching drops are gone from their `Down`.
   `strip-core-migration-tables.py` did all three unchanged. Databases that already ran them are unaffected.
3. **The module's `ProjectControlsBaseline`** (first migration of `ProjectControlsDbContext`, history `__EFMigrationsHistory_projectcontrols`): 47 statements, each `CREATE ... IF NOT EXISTS`, `Down` empty. The one hand edit to the generated baseline:
   `Ceiling.CheckAtRequisition` carries `DEFAULT 0` (`DEFAULT FALSE` on PostgreSQL), because on an existing install it came from an `AddColumn` with a default, and a new install must get the same column. The migration test compares every column's default.
4. No foreign key crossed the line this time (contrast materials' `RfqLine.PartId`): upgraded and new databases are identical.

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseProjectControlsTables`: no table touched | the baseline finds the tables, changes nothing | tables and rows stay, unused |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then the release | the baseline creates the tables with the current schema | no tables, none needed |
| New | no project controls tables | the baseline creates them | none |

**Verified**: see *Verification*.

## Tests

| Shipped suite | Now |
| --- | --- |
| `Ceilings/*` (4 files), `Evm/*` (4), `Risks/*` (2), `Time/CeilingRoomTests`, `Assistant/ProjectNarrativeTests` | moved to the module (12 files with their kits) |
| `MyTodo/MyTodoProviderTests`: the variance write-up test; `MyTodoServiceTests`: the ceiling half of the ceilings-and-funding test | moved (`ProjectControlsTodoTests`); the funding half stays |
| `Setup/ProjectStartTests`: "Starting from a contract uses its dates, CLINs and labor hours" | the dates and CLINs half stays (it asserts no budget without the module); the budget from the hour ceilings moved (`ProjectStartCeilingTests`) |
| `Identity/BuiltInRoleParityTests` | the two permissions' rows moved to the module's `Permissions_are_in_the_catalog_and_held_by_the_same_roles_as_before` |
| `Projects/ProjectHubPageTests` | the occasional-tab test registers stand-in `ProjectHubTabContribution`s; a new test shows the page without any |
| `Licensing/ModuleGateTests` | `projectcontrols` joins the modules for which "no service maps to it in this build" |
| new in core: `Modules/ProjectControlsAbsentTests` | a new database has none of the 15 tables; the permissions, API segments and catalog row are absent; the host starts and the module's API paths (`/projects/{id}/evm/...`, `/projects/{id}/ceilings`, `/contracts/{id}/ceilings` and `/rate-ceilings`, `/ceilings/...`, `/risks/...`, `/closeout/...`, the narrative) answer 404; every core hook answers nothing; the project page and its Setup "More" section render without the module's tabs, links and sections; a saved earned value widget says where its figures come from |
| new in the module | `MigrationPathTests` (a new install; the module's schema equals the previous release's; an existing install keeps every table, row and column and gains only history rows, twice; an upgrade without the module keeps its data), `ModuleSurfaceTests` (permissions and roles, `ModuleMap` rows, the tabs, slots, hooks, widget and tools the host receives, including that every slot component declares the parameters its slot passes), `ProjectControlsTodoTests`, `ProjectStartCeilingTests` |

## Seam changes this extraction needed

All additive; the contract version is still 1.

* `ProjectHubTabContribution` (`Workbench.Contracts.Modules`) and the four slot names above; `ProjectTabSet` in the UI.
* `WidgetDataDto.ModulePayload` (JSON) replaces `WidgetDataDto.EarnedValue`; `ModuleWidgetKeys.EarnedValue`; `HomeWidgetData.ComputeModuleAsync`.
* Core interfaces with empty defaults: `IProjectAlertSource` (a list), `IContractCeilingSource` (default `NoContractCeilingSource`), `ICeilingProximitySource` (a list).
* `OpenCommitments`, `DirectCosts`, `DirectCost`, `AssistantToolbox.Money/Percent/Clip` public. `ProjectsFormat.Tabs` lost its four module entries.
* Module repository: `module-names.sh` knows `projectcontrols`; the extraction scripts needed no change.

## How project controls depends on other modules

* **Billing kernel** (`IBillingLimits`, `BillingCostSource`, the invoice services it decorates) and **accounting** (burdened cost, purchase orders and commitments, AP bills, requisitions): all in the shipped product today, so the module declares **no `DependsOn`**.
  When billing or accounting become plugins, `DependsOn: billing` and `accounting` are the entries to add. The module's 8 key-only tables are the foreign keys those modules' baselines must keep creating first.
* **govcon, capture and materials** do not reference it; materials' purchase order decorator and this module's ceiling decorator now both wrap `IPurchaseOrderService` (load order is by module id, so project controls wraps materials).
* **enterprise**: custom-field and layout rows do not name any of its records; **workforce**: allocations read by `ChargeGuard` for the staffing check are core's.

## Documentation, demo data and public pages that describe project controls

Nothing was removed; whether the public site keeps describing a paid module is the owner's decision. They are:

* `docs/user-guide/risk-register.md` (a copy is in the module repository's `docs/`), `docs/user-guide/projects.md`, `project-plan.md`, `dashboards.md`, `reports.md`, `time.md`, `approvals.md`, `sheets.md`
* `docs/admin-guide/contracts-projects.md`, `assistant.md`, `company-type.md`; `docs/finance/billing.md`, `commercial-billing.md`, `payables.md`
* `docs/security/permissions.md`, `ai-assistant.md`, `threat-model.md`; `docs/integrations/mcp.md` (the two tools); `docs/project-visibility.md`, `docs/automation.md`, `docs/sheets/formulas.md`
* `docs/feature-coverage.md`, `docs/editions.md`, `docs/commercial/modules-and-pricing.md` (the module's price line and the sentence that names schedule health), `website-copy-suggestions.md`, `docs/index.md`, `README.md`, `mkdocs.yml`
* `docs/compliance/dcaa/sf1408.md`, `nist-800-171/*`, `accessibility/vpat.md`, `docs/iso/*`, `docs/training/*`, `docs/ux/e2e-walkthrough.md`, `docs/reference/glossary.md`, `docs/operations/configuration.md`
* the marketing pages under `site/` (`index.html`, `costpoint-alternative.html`, `dcaa-timekeeping.html`, `ms-project-alternative.html`), `samples/import/README.md`, and the internal handover notes `docs/operations/evm.md`, `ceilings-closeout.md`, `fix-ceilings.md`, `ceiling-first-planning.md`, `w2-schedule-health-timer.md`
* the sample data: earned value on project 1001 and the two sample ceilings exist only with the module; the lifecycle projects and every other sample are unchanged.

## Verification

* Shipped repository, no `modules/` folder: `Workbench.Server.slnf` builds (the MAUI desktop project cannot be built on this machine; it does not reference the module); `tools/Workbench.LoadData` builds; the filtered groups pass: Licensing, Modules, Navigation, Tenancy, Migration, Identity, Reports, Customization,
  Accessibility, GoldenPath and Dashboards (668 passed, 1 skipped: the PostgreSQL numbering test), and Time, Travel, Purchasing, Setup, Assistant, plus Projects, MyTodo, Mobile, Operations, Billing, Financials, Resourcing, Scheduling, Integrations and ProjectAccounting (967 passed). The Ceilings, Evm, Closeout and Risks groups moved to the module.
  The shipped host starts without the module (no `fail:` lines in its log; the five "Sign in first" exceptions in the startup log are the same five the previous release's host logs): the project page, its Setup tab, the contract page and a project's financials answer 200 with no project controls tabs, sections or cards,
  and `/projects/{id}/evm/baseline`, `/projects/{id}/ceilings`, `/contracts/{id}/ceilings`, `/risks/...`, `/closeout/...` and the narrative path answer 404 (`ProjectControlsAbsentTests`, and the same paths against the running host).
* Module repository, module loaded from the built folder: its 100 tests pass on InMemory and on SQLite (`WORKBENCH_TEST_DB=Sqlite`), and the govcon (100), capture (213) and materials (77) suites still pass against this core. A fresh database with all four modules loaded
  (`modules/govcon`, `capture`, `materials`, `projectcontrols`): every migration applies, the sample data seeds, the module's API paths answer 200, no errors.
* `has-pending-model-changes`: clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL, and for the module's `ProjectControlsDbContext` on both, checked with `dotnet ef` directly, never with `scripts/new-migration.sh`.
* **Upgrade from the previous release's host** (`57d9510`, the base of this change; published, run in Development so it seeds the sample company, 355 tables outside the history tables), against this release's host:
  * a *new* database made by this host **with** the module has the 15 tables identical to the previous release's (columns with type, nullability and default, foreign keys and indexes, compared table by table: zero differences) and the same row counts in every one of the 355 tables
    (the rows of the 15 tables equal once ids and timestamps are masked, since each seed run makes new ones); **without** the module it has none of them and the host starts;
  * the *upgrade* (the previous release's database, host without `modules/projectcontrols` and with it): only `ReleaseProjectControlsTables` ran in core, and `ProjectControlsBaseline` with the module (and nothing the second time);
    compared with the previous release's host restarted on the same database, **every table's row count, every row of the 15 tables and the `CREATE` text of the 15 tables and their indexes are identical in both cases**, and the module's history table holds one row.
    (A restart of any host on that database adds 8 `NotificationPreference` rows; the previous release's does too, so the comparison is against a restart.)
* Module tests on SQLite (`MigrationPathTests`): a new install; the module's schema equals the previous release's; an existing install keeps every table, row and column, gains only history rows, twice; an upgrade without the module keeps its data.
* Not run: PostgreSQL itself (the baseline is plain `CREATE ... IF NOT EXISTS` DDL; the private repository's CI should run `WORKBENCH_TEST_DB=PostgreSql` and a PostgreSQL upgrade, in particular the `DEFAULT FALSE` column), the full shipped suite (known to hang locally), the MAUI desktop build.

## Not done

* The worker and the desktop app do not load modules: the hourly ceiling alert job runs in the web host only (it did before, in the web host's hosted-services list), and the desktop app loses the earned value, ceilings, closeout and risk pages and clients.
* `FakeAssistantModel` (the offline test model) still asks for `get_earned_value` and `get_ceilings` by name; without the module the toolbox answers "Unknown tool", which the assistant reports as an error line. Harmless, but the fake could ask only for tools the toolbox offers.
* The `ProjectNarrative` entity stays in core; if the narrative ever needs its own tables, they are a separate release.
* Schedule health's license line (above) needs the owner's decision.

## Lessons for the workforce extraction

* **Compile, then grep the module's record names**: the namespace grep found 12 core files; compiling the shipped repository after deleting the module's folders found three more (`ProjectPlanService`, `TimesheetQueries`, the narrative's neighbours) that used the module's entities through `Domain.Ceilings.` partial qualification. Delete first, let the compiler list the couplings, then design the hooks. Workforce's resourcing records (`ResourceAllocation`, `StaffingAssignment`) will show up the same way in the plan, timesheets and schedule health.
* **Tabs are not slots.** A page that lists its tabs from a static array (`ProjectsFormat.Tabs`) needs a contribution that carries the label, the access rule and the order; pages built from `MudTabPanel`s can take a slot whose component renders the panels itself (the contract page), as long as route-to-index maps ask whether the slot is filled.
* **A saved enum member stays; its computation moves.** `WidgetType.EarnedValue` (and `KpiKind.Pipeline` before it) are stored on user dashboards, so core keeps the name and asks a provider by key; the payload that used to be a typed field became a JSON element. Workforce's saved widget types (utilization, capacity) will want the same treatment.
* **A service in a core namespace cannot be claimed by a module.** The narrative contracts had to change namespace before the module could list them; check each moved interface's namespace against `ModuleMap.ServiceNamespaces` before moving it.
* **Existing installs added columns later**: a column added by a later core migration with a default (`Ceiling.CheckAtRequisition`) must have the same default in the module's baseline, or new and upgraded databases differ. The migration path test compares defaults for exactly this; keep that check, and grep every old migration for `table: "<each moved table>"`, not only the one that created it.
* **Decorators are the cheap case.** Everything core exposes to enforcement (time, ODC, expense, AP, PO, requisition, invoice) was a decorator, so `ModuleServiceDecoration` carried it with no new hook. Workforce's leave and per diem decorators on `ITimesheetService` and `IExpenseReportService` are the same shape; the order caveat (module decorators now sit outside every host decorator of the service) applies to them too.
* **Do not rely on a shell glob returning nothing**: a failing glob in the same command line silently skips the rest of a `grep` (zsh aborts the whole command). Quote `--include='*.cs'`.
