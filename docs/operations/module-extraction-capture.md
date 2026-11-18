# Extracting a module: capture (business development and proposal pricing)

Phase 4 of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md); the pilot and the
billing change are [govcon](module-extraction-govcon.md) and [billing](module-extraction-billing.md); the order and class of each module
come from the [audit](module-extraction-audit.md), which called `capture` class A: extractable now). This page is the inventory, every place
core touched the module and how it was resolved, the data path for existing installs, and what the next modules should expect.

The code now lives in the **private repository** (`Workbench-Modules`, a local git repository with no remote yet) as
`src/Workbench.Module.Capture*`, builds into `modules/capture/`, and loads like any plugin. This repository ships without it.

## What moved and what did not

**Moved to the module:**

| Feature | Core layers that held it |
| --- | --- |
| Accounts, contacts, activities (CRM), opportunities, the pipeline board, forecast and win/loss, B&P charge codes | Domain `Crm/`, Contracts `Crm/`, Application `Crm/`, endpoints (`crm`), HTTP clients, EF configurations, UI pages `Crm/` |
| Go/no-go gates (the "Opportunity gate" approval handler), capture plans, Pwin assessments, color-team schedules | same layers |
| SAM.gov leads and saved searches (including the daily search job), lead-fit scoring | Application `Crm/` (`SamGov`, `OpportunityLeadService`, `SavedLeadSearchBackgroundService`) |
| Requirements and the compliance matrix (solicitation reading, assistant sorting) | Application `Crm/` (`RequirementService`, `SolicitationParser`) |
| Proposals and their pricing: price models, scenarios, locked versions, cost volume workbook, price to win, won handoff to contract and project | Domain `Pricing/`, Contracts `Pricing/`, Application `Pricing/`, endpoints (`pricing`), UI `Pricing/`, `PricingLayout`, `PricingPrintLayout` |
| The importers for Capture Exec, HubSpot, Salesforce and plain CSV | `Imports/CaptureExec`, `Imports/Commercial` |
| The won-handoff to-do, the home-page CRM to-dos, the resourcing "If we win the pipeline" panel | `WonHandoffTodoProvider`, `CrmTodoProvider`, `PipelineDemandPanel` |
| Sample data: the government pipeline, capture plans and proposal pricing; the commercial demo's pipeline | `CrmSeed`, `CapturePlanSeed`, `PricingSeed`, the CRM half of `CommercialDemoSeed` |

About 23,000 lines in 125 files (production code, UI and the module class), 28 tables, plus 30 test files. Pages, routes (`/bd/...`, `/pricing/...`), API
paths (`/api/v1/crm/...`, `/api/v1/pricing/...`), permission keys (`bd.*`, `pricing.prices`) and role behavior are unchanged.

**Stayed in core**, each for a reason:

* **The roles** `OrgRoles.BusinessDevelopment` and `OrgRoles.Pricing`, `ApprovalOrgRole.BusinessDevelopment`/`Pricing`, and their role-check
  extension methods. Roles are stored in the database and by the role editor; they exist with or without the module.
* **The fee field permission** `fields.pricing_fee` (`Permission.FieldsPricingFee`) and `SensitiveFieldAttribute` itself: field permissions are
  core's family (prefix `fields.`), and the module's DTOs use the attribute. A host without the module still lists the permission.
* **The import enums** (`ImportSource.CaptureExec`/`HubSpot`/`Salesforce`/`GenericCsv`, `ImportKind.CrmAccounts`/`CrmContacts`/`Opportunities`):
  stored in import history. Without the module nothing imports them and the wizard no longer offers those sources.
* **`KpiKind.Pipeline`** and the **permission area** "Business development", the **"growth" menu section** (empty and hidden without the module),
  `ModuleIds.Capture` and its catalog entry.
* **The sample import files** `samples/import/captureexec`, `hubspot`, `salesforce` (public sample data the module's tests read).
* **`AssistantPrompts`** for the requirement sorter and the proposal draft: strings the module's services use. They read "capture team" and
  "proposal" wording; moving them needs a prompt extension point, which is not worth one here.
* **The generic engines** the module uses: approvals, custom fields, the report writer, importers, the integration outbox, form layouts.

## Coupling: every place core referenced the module, and the resolution

The audit counted 7 core files; the real count was larger (about 35) because it counted `using` lines of the module's namespaces, not the
*catalogs* that listed the module's records by name. Resolutions: **move**, **invert** (core owns an extension point, the module fills it), **keep**.

| # | Core referenced capture through | Resolution |
| --- | --- | --- |
| 1 | `ModuleMap`: API segments `crm`, `pricing`; permission prefixes `bd.`, `pricing.`; nav section `growth`, page `pricing`; service namespaces `Crm`, `Pricing` | **move** into `CaptureModule.Describe()`. The `growth` section stays defined in the menu, hidden while empty. |
| 2 | `Permission` constants and `PermissionCatalog.BuiltIn` (5 permissions) | **move** (`CapturePermission`, `Describe().Permissions`). The roles and the area stay in core. |
| 3 | `NavCatalog` (7 pages) and `NewCatalog` ("+ New › Opportunity") | **move**: `Describe().NavEntries`, a `NewAction` row. |
| 4 | The Pipeline and Opportunities pages also open for *capture and proposal managers of an opportunity* who lack the pipeline permission (`NavAccess.ShowBusinessDevelopment` asked `IOpportunityService`) | **invert**: new `INavAccessFlag` (the module registers `ManagesOpportunityFlag`), `ModuleNavEntry.AlsoWhenFlag`, and `NavAccess.Flags`. |
| 5 | `ApiKeyScopes`: the `crm` area | **move** to `Describe().ApiKeyAreas`. |
| 6 | `AddWorkbenchApplication` / `IdentityRegistration`: services and permission decorators | **move** into `ConfigureServices`. |
| 7 | `WorkbenchApi`, ApiClient `AddCrmClients`/`AddPricingClients` | **move** (`MapEndpoints`; the desktop app does not load modules, see *Not done*). |
| 8 | `NotificationsHub` hosted the daily SAM.gov search (`AddWorkbenchCrmHosting`) | **move**: the module registers the hosted service in `ConfigureServices` (below). |
| 9 | `DatabaseInitializer` seeded CRM, capture plans and pricing in the middle of the sample data | **invert**: `IModuleSeed` at `AfterPlanning`. |
| 10 | `CommercialDemoSeed` created accounts, deals and gate reviews through the CRM services | **invert**: the CRM half (`SeedPipelineAsync`, about 130 lines) is `CommercialPipelineSeed` in the module, at the new stage `ModuleSeedStage.CommercialDemo`, run at the end of the demo seed. |
| 11 | Catalog rows built into core: custom list `opportunities`, list source, numbered document `OPP-`, automation entity, webhook event `opportunity.stage_changed`, approval document type `opportunity-gate` with its default workflow, status sets `opportunity-stage` and `opportunity-source`, the opportunity form | **invert** through the #47 rows (`ModuleContribution.Rows`). Status sets had no row catalog: new `ModuleCatalogs.StatusSets`. The approval type and the event moved out of core's lists (`ApprovalDocumentTypes.OpportunityGate`, `IntegrationEventType.OpportunityStageChanged`, `OpportunityStageEventData`). |
| 12 | `CustomizationRegistration` registered the opportunity as a customizable record; `LayoutsRegistration` guarded `IOpportunityService` for required form fields | **invert**: the module calls `AddCustomizableEntity` and the now-public `AddFormRequirementGuard<T>()` in `ConfigureServices`, in the same order as before (service, guard, permission decorators). |
| 13 | `HomeWidgetData` computed the "Weighted pipeline" KPI from `IPipelineService` | **invert**: `KpiKind.Pipeline` stays (saved home pages store it); core asks the `IHomeWidgetProvider` registered under `kpi.pipeline`, and without one the tile says "comes from a module that isn't installed". |
| 14 | `ImportRegistration` listed the six CRM importers; `ImportWizard` listed their sources | **move**; the wizard shows only sources that have importers. |
| 15 | `MyTodoService` registered `WonHandoffTodoProvider` | **move**. |
| 16 | `AutomationTemplates` offered the recipe "Tell the capture manager about stage changes" | **keep**, offered only while the `opportunity` automation entity exists (new optional `Entity` on a recipe). |
| 17 | The Resourcing heat map embedded `PipelineDemandPanel` | **invert**: `UiSlot` `resourcing.heatmap.panels` (parameter `Map`). |
| 18 | The CAS consistency check of **govcon** read capture's tables (`Proposal`, `PriceModel`, the locked cascade) | **invert**: a cross-module coupling the audit did not list. Core declares `IPricingConsistencySource` and three facts records (`Contracts/Compliance/PricingConsistency.cs`); capture implements it (`PricingConsistencySource`); govcon reads the registered sources and has no estimating findings without one. `PriceCalculator.BaseLabel` became `RateBook.BaseLabel` in core, because accounting words the same bases. |
| 19 | `FieldMasker` only masked DTOs from core's contracts assembly, so the **fee, margin and price masking of the module's DTOs would have silently stopped working** | **fixed**: types in a `Workbench.Contracts*` namespace are masked wherever the assembly lives. See *Seam changes*; a module with a sensitive field needs this. |
| 20 | Internals the module reused: `Mapping` (argument checks and DTO mappings), `SystemExecution`, `ProjectPlanService.MissingSetupAsync`, `ClinService.AddClinAsync`, `ClinRules`, `ContractImporter.ParseKind`, `EntitySnapshots` | made **public** (each is a signal that a deliberate API belongs there). The decimal precision helpers (`Money`, `Rate`, `Hours`) were copied into the module (three one-line extension methods). |
| 21 | Tests spanning core and the module | see *Tests*. |

### The SAM.gov search job

`SavedLeadSearchBackgroundService` ran in the web host through `NotificationsHosting`. It is now registered by the module's `ConfigureServices`
(`AddWorkbenchCrmHosting`), so it runs wherever the module loads, which today is the web host and test hosts (the worker and the desktop app do
not load modules, so they never ran it). It checks `ISamGovClient.IsSample` before doing anything: without `SamGov:ApiKey` it returns at once,
exactly as before, and with a key it runs every active saved search once a day per tenant as Workbench System. Confirmed with the module loaded:
the host starts, the service is among its hosted services, and a test starts and stops it; the search itself is covered by `LeadImportTests`
(sample SAM.gov client) and was not run against the real SAM.gov API. A job a module owns needs a way to say it must run in the worker too;
that is the existing open item in [module-plugins.md](module-plugins.md#not-in-this-change).

A related fix in the seam: `ModuleHost.ConfigureServices` copies the services a module registered into the host's collection, and a module that calls
`AddHttpClient` (the SAM.gov client does) copied the framework's internal registry too, which made the *host's* own later `AddHttpClient` calls fail.
The copy now skips non-public singleton registries the host already has.

## Data: existing installs keep their tables, new installs get them from the module

Same rule and recipe as the pilot: no table is dropped, no core migration row changes for an installed database, and the module can be added or
left out of an upgrade at any time. 28 tables, 55 indexes, 41 foreign keys (to `Customer`, `Contract`, `Project`, `WbsElement`, `Employee`,
`LaborCategory`; **no table of core or of another module refers to a capture table**).

1. **`ReleaseCaptureTables`** (core, both providers): the model no longer maps the entities, EF generated the drops, they were removed by hand;
   `Up` and `Down` are empty. Only the snapshot changes. `has-pending-model-changes` is clean for both contexts on both providers.
2. **Five old core migrations no longer create the tables** (`BusinessDevelopmentHrMaterialsAssistant`, `CapturePlansAndCaptureExec`,
   `AddLeadFitAndCompanyProfile`, `AddProposalSections`, `AddOpportunityRequirements`; the matching drops are gone from their `Down`). Two of
   them also *added columns* to capture tables, so `strip-core-migration-tables.py` learned `AddColumn`, `AlterColumn` and `DropColumn`.
   Databases that already ran them are unaffected.
3. **The module's `CaptureBaseline`** (first migration of `CaptureDbContext`, history `__EFMigrationsHistory_capture`): 83 `CREATE ... IF NOT EXISTS`
   statements (28 tables, 55 indexes), `Down` empty. `CaptureDbContext` maps the six core entities its keys point at key-only and excluded from
   migrations. One column, `OpportunityLead.FitByAssistant`, carries the default (`0`/`FALSE`) the old `AddColumn` gave it; the generated baseline
   did not, and the statement was edited so a new install matches an existing one.

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseCaptureTables`: no table touched | the baseline finds the tables, changes nothing | tables and rows stay, unused |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then the release | the baseline creates the tables with the current schema | no tables, none needed |
| New | no capture tables | the baseline creates them | none |

**Verified**
* Previous release's DDL against the module baseline, for both providers, from the migrations themselves (not the models): SQLite by building both
  schemas and comparing columns, keys and indexes; PostgreSQL by generating the previous release's full script and the baseline and comparing
  344 columns, 55 indexes and 41 foreign keys. After the `FitByAssistant` default: **zero differences on both**.
* Module tests on SQLite (`MigrationPathTests`): a new install; the module's schema equals core's old one; an existing install with rows in
  tables keeps every table, row and column and only gains history rows, twice; an upgrade without the module keeps its data.
* **End to end with the previous release's host** (`783ac42`, Development sample data on SQLite: 10 opportunities, 16 accounts, 1 proposal with 3
  price models, plus rows added by hand to the three tables the sample leaves empty): upgraded by this release's host with `modules/capture`, it
  applied `ReleaseCaptureTables` and `CaptureBaseline` and nothing else; every row count of the 28 tables (and of invoices, contracts, projects)
  was unchanged and the checksum of the 28 tables' schema was identical before and after. Without the folder only `ReleaseCaptureTables` ran, the
  host started, and the checksum and every count were the same.
* Not run: PostgreSQL itself (no local instance); the private repository's CI should run `WORKBENCH_TEST_DB=PostgreSql` and a PostgreSQL upgrade.
  Downgrade (an older core against a database the module changed) is not supported, as before.

## Tests

| Shipped suite | Now |
| --- | --- |
| `Crm/*` (14 files), `Pricing/*` (5), `Imports/CaptureExecImportTests` | moved (187 tests, and the commercial-import tests) |
| `Identity/BuiltInRoleParityTests` | the five permissions' rows moved to the module's `Permissions_are_in_the_catalog_and_held_by_the_same_roles_as_before` |
| `Identity/ScopedRoleAndFieldTests` | the pricing fee-field test moved (`PricingFeeFieldTests`); the shipped one keeps the other field permissions |
| `Customization/FormLayoutTests` | the opportunity form's as-built check is `ModuleSurfaceTests.The_module_adds_its_rows_to_core_catalogs` |
| `Setup/CommercialDemoSeedTests` | the pipeline assertions moved (`SampleDataTests`); the shipped test keeps the rest of the demo |
| `Navigation/NewMenuTests`, `GovernmentNavigationTests`, `Licensing/ModuleEntitlementTests`, `Licensing/ModuleGateTests`, `Customization/CustomFieldTests`, `Imports/RollbackAndPermissionTests`, `Operations/DemoModeTests` | expectations of the module's items removed (the module's gate theory is `CaptureGateTests`) |
| new in core: `Modules/CaptureAbsentTests` | a new database has none of the tables; the menu, permissions, API key area, catalog rows, importers and the pipeline tile behave as if the module never existed; the host starts and the API paths are 404 |
| new in the module | `MigrationPathTests`, `ModuleSurfaceTests` (permissions, rows, nav flag, KPI provider, form guard, event allow-list, job registration), `SampleDataTests`, `PricingConsistencySourceTests`, `CaptureGateTests` |
| govcon | `CasDisclosureTests` use a fake `IPricingConsistencySource`, plus a test without one |

## Seam changes this extraction needed

All additive; the contract version is still 1.

* `ModuleCatalogs.StatusSets`: `StatusSetDefinition` is a row (`StatusSetCatalog.BuiltIn`/`All`, and `BuiltInRowKeys`).
* `INavAccessFlag` (Workbench.Ui), `ModuleNavEntry.AlsoWhenFlag`/`Note`, `NavAccess.Flags` (replaces `ManagesOpportunities`, which was the capture module's).
* `ModuleSeedStage.CommercialDemo` and `ModuleSeeds.RunAsync` (the initializer and the commercial demo share it).
* `LayoutsRegistration.AddFormRequirementGuard<T>()` (was private).
* A `UiSlot` named `resourcing.heatmap.panels` (parameter `Map`, a `HeatMapDto`).
* `ModuleHost.StyleSheets()` and a link per sheet in `App.razor`: the scoped styles (`.razor.css`) of a module's pages. A host built with the library gets them from
  its own style sheet; a module loaded from a folder did not, so the **module's pages had lost their scoped styles** (govcon's print pages included).
  `scripts/build-module.sh` copies the bundle into the module's `wwwroot`.
* `ModuleHost.ConfigureServices` skips duplicate non-public singleton registries (see above).
* `FieldMasker` masks sensitive fields of any `Workbench.Contracts*` type, not only core's assembly.
* Core hooks with no-op defaults: `IPricingConsistencySource` (no source = no estimating findings), the `kpi.pipeline` provider (no provider = the tile says so).
* `AutomationTemplates` recipes may require an automation entity.
* Public in core for modules: `Mapping`, `SystemExecution`, `ProjectPlanService.MissingSetupAsync`, `ClinService.AddClinAsync`, `ClinRules`,
  `ContractImporter.ParseKind`, `EntitySnapshots`, `RateBook.BaseLabel`.
* Module repository: scripts take the module id (`build-module.sh capture`, `check-migrations.sh`, `new-migration.sh`);
  `strip-core-migration-tables.py` handles columns.

## How capture depends on other modules

* It needs **core readers only** and declares no `DependsOn`: it reads customers, contracts, projects, WBS, employees, labor categories, the fiscal
  calendar and indirect rates through core services and tables, all of which stay in core or are read through existing public services.
  Burdened cost and rate bases for pricing come from `IndirectRates` (accounting module in the audit's map, but the engine is core's today).
  When `accounting` leaves, those readers must stay core (`RateBook`, `IBurdenedCostService`) or move behind a reader; they are the same
  decision the audit already lists for the accounting extraction.
* **govcon** reads capture through `IPricingConsistencySource` only (no assembly reference), and **enterprise** features (custom fields, forms, layouts,
  automations, webhooks) read capture's records through rows and the customizable-entity registry.
* **workforce** (resourcing) hosts capture's panel through a slot.

## Documentation, demo data and public pages that describe capture

Nothing was removed; whether the public site keeps describing a paid module is the owner's decision. They are:

* `docs/user-guide/business-development.md`, `importing-deals.md`, `pricing.md`, `docs/user-guide/index.md`, `docs/user-guide/approvals.md`
* `docs/admin-guide/company-type.md`, `contracts-projects.md`, `customization.md`, `setup.md`, `users-and-access.md`, `approval-workflows.md`, `compliance.md`
* `docs/security/permissions.md`, `docs/integrations/webhooks.md` (the stage-changed event), `docs/automation.md`, `docs/customization.md`, `docs/project-visibility.md`
* `docs/feature-coverage.md`, `docs/editions.md`, `docs/commercial/modules-and-pricing.md`, `docs/commercial/website-copy-suggestions.md`, `docs/index.md`, `README.md`, `mkdocs.yml`
* `docs/migration/costpoint-cutover.md`, `docs/training/lifecycle-demo.md`, `docs/demo-hosting.md`, `docs/operations/configuration.md` (`SamGov:ApiKey`), the ISO pages
* the public sample files `samples/import/captureexec`, `hubspot`, `salesforce` and `samples/import/README.md`
* the marketing pages under `site/` (the CRM and pricing paragraphs) and `tools/docs-screenshots/shots.mjs` (screenshots of the pages)
* the commercial demo dataset (`Demo:Dataset=Commercial`, `CommercialDemoSeed`): its pipeline now needs the module; without it the demo has no deals.

## Verification

* Shipped repository, no `modules/` folder: builds; filtered groups pass (2,166 tests: Licensing, Modules, Navigation, Tenancy, Migration, Identity,
  Reports, Customization, Accessibility, GoldenPath, Dashboards, Setup, Billing, Time, Payroll, Ceilings, Evm, Platform, Approvals, Imports,
  Integrations, Resourcing, MyTodo, AutomationBuilder, Operations, Assistant, CompanyTypes, Hr, Materials, Purchasing, Accounting, Revenue, Sheets,
  Security, Support, Mobile, Help, Notifications, Automation, Costpoint, ContractDetail, Projects). The shipped host starts without the module:
  no menu section, no pages, the API paths answer 404 (`CaptureAbsentTests`), and its log has no errors beyond what the previous release's has.
* Module repository, module loaded from the built folder: 213 capture tests pass on InMemory and on SQLite (`WORKBENCH_TEST_DB=Sqlite`); the govcon module's 100 tests
  still pass against this core.
* `has-pending-model-changes`: clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL, and for the module's
  `CaptureDbContext` (and govcon's) on both, checked with `dotnet ef` directly, never with `scripts/new-migration.sh`.
* A fresh database with both modules loaded (`modules/govcon` and `modules/capture`): every migration applies, the sample data seeds (10 opportunities,
  a proposal, the bd@ and pricing@ sign-in people), the scoped style sheet is served, no errors.
* Upgrade from the previous release's host: see *Data*. Not run: PostgreSQL itself, the full shipped suite (known to hang locally).

## Not done

* The worker and the desktop app do not load modules: the SAM.gov search never ran there, and the desktop app loses the capture pages and clients.
* PostgreSQL runs (see above) and signing or packaging the module (phase 5).
* `AssistantPrompts` for capture stay in core (strings only).

## Lessons for materials, project controls and workforce

* **Count catalogs, not `using` lines.** The audit found 7 core files; the work was in about 35, almost all of them *catalogs and engines that listed the
  module's records by name* (forms, lists, numbering, automation, webhooks, approvals, status sets, nav rules, KPI tiles, importers, seeds). Grep core for the
  module's record names, keys and permission strings, not only its namespaces. The #47 rows paid off: eleven of the catalog items were one-line moves.
* **Look sideways for module-to-module readers.** govcon's CAS check read capture's tables, found only because the second module no longer compiled.
  Build every existing module against the stripped core before you delete, and put the shared question in core as a source interface.
* **A sensitive field in a module's DTO needs the masker to find it.** Check any `SensitiveField`/pay-rate DTO you move (materials: costing; workforce: pay and salaries).
* **Pages with `.razor.css` need the bundle linked**; the build script now does it. Check one scoped page by eye in a host that loads the module.
* **Order of registration is behavior.** Form requirement guards, permission decorators and edition gates wrap the registered service in the order they are
  added; the module registers its service, then the guard, then its permission decorators.
* **Hosted jobs and `AddHttpClient`** behave differently in a module's `ConfigureServices` (a scratch collection that is copied): both are handled now, but
  a job that must run in the worker needs the worker to load modules first.
* **The edited old migrations are the bulk of the risk**: a migration that also added *columns* to the module's tables needs its `AddColumn` removed too, and a
  later `AddColumn` default (a `defaultValue: false`) has to be copied into the baseline so a new install equals an existing one. Compare columns, keys and indexes with the previous release for both
  providers; do not trust a model comparison.
* Materials is the biggest of the three (57 entities, shop floor and sales, the MRP engine, receiving that purchasing decorates); do the item and vendor reader seams first.
  Workforce still waits on the leave-kernel decision in [module-plugins.md](module-plugins.md#not-done-here-the-leave-kernel).
