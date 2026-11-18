# Extracting a module: billing (revenue recognition and live accounting push)

Phase 9 of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md); the earlier moves are
[govcon](module-extraction-govcon.md) (with [government billing](module-extraction-billing.md)), [capture](module-extraction-capture.md),
[materials](module-extraction-materials.md), [project controls](module-extraction-projectcontrols.md), [workforce](module-extraction-workforce.md) and
[accounting](module-extraction-accounting.md); the [audit](module-extraction-audit.md) called `billing` class C for the engine and class A for two leaves). This page moves **only the two leaves**:
revenue recognition and the live push to QuickBooks Online and Xero. The invoice and payment services, the receivables ledger and the billing kernel stay in core, **and free**. It is the inventory,
**the change to the sold line**, every place core touched the leaves and how it was resolved, the data path for existing installs, and what is still public.

The leaves now live in the **private repository** (`Workbench-Modules`, a local git repository with no remote yet) as `src/Workbench.Module.Billing*`, build into `dist/modules/billing/` (copy it to a host's
`modules/billing/`), and load like any plugin. This repository ships without them.

## The price line changed: read this first

The catalog sold `billing` as "invoices, ready to bill, customer payments, AR aging, unbilled accrual, milestones, retainers, revenue recognition and live accounting push". Invoicing and the receivables ledger cannot leave (84 core files and
five other paid modules read them), so **they are free now, and the sold module is the two leaves only**:

| | Before | After |
| --- | --- | --- |
| `ModuleCatalog` text of `billing` | "Invoices, ready to bill, customer payments, AR aging, unbilled accrual, milestones, retainers, revenue recognition and live accounting push." | "Revenue recognition (setup by method, estimates at completion, the monthly revenue run and the revenue reports) and live push to QuickBooks Online and Xero." |
| `ModuleCatalog` text of `core` | "... the general ledger, period close, payables, purchasing, payment runs, multi-currency and indirect rates. Always included." | the same, plus "invoicing, ready to bill, customer payments, AR aging, unbilled accrual, milestones, retainers and statements" and "accounting exports as files" |
| **Free now** (core rows) | paid | Invoices (draft, generate from time, ODCs, milestones and agreements, approve, void, credit, print and PDF), ready to bill, customer payments and applications, AR aging and statements, unbilled accrual and its close check, billing milestones, retainers and commercial billing agreements, overdue and due-soon reminders, billing settings, the receivables standard reports, **and the accounting file export** (QuickBooks Online CSV, QuickBooks Desktop IIF, Xero CSV, generic CSV and Excel; account mapping; export history) |
| **Still paid** (module rows) | paid | **Revenue recognition**: setup by method (billings, cost-to-cost, T&M billed or incurred, cost plus fee, fixed price milestones), estimates at completion (submit, approve, reject), deliveries, the monthly revenue run (preview, post through the ledger, reverse), the revenue reports (by period, revenue vs billings, backlog), the "revenue recognized" close check, the project page's Revenue tab and the EAC to-dos. **Live accounting push**: OAuth connections to QuickBooks Online and Xero, preview and push of journal entries, invoices and bills, per-record results and retry, provider links |
| API segments | `billing`, `revenue`, `accounting-live` | module: `revenue`, `accounting-live`; **core**: `billing` (and `accounting-export` as before) |
| Permission prefixes | `billing.`, `revenue.` | module: `revenue.` (one permission, `revenue.manage`); **core**: `billing.` |
| Menu | group Billing & AR, pages `billing/invoices`, `billing/statements`, `accounting/revenue/*` | module: **Revenue**, Revenue estimates, Revenue setup, Revenue by project (group Revenue & compliance and Finance) and **Revenue reports** (Reports); **core**: group Billing & AR and its pages |
| Service namespaces | `Billing`, `Revenue`, `AccountingLive` | module: `Revenue`, `AccountingLive`; **core**: `Billing` |
| API key area `billing` | `billing`, `revenue` | core `billing`; the module adds `revenue` |

**The question for the owner: what is "Billing" worth selling now?** What is left is a back-office add-on for firms on cost-type or percentage-of-completion contracts (revenue recognition) and for firms that keep QuickBooks
or Xero as the books (the live push). The file export to the same packages is free, so the live push sells only the convenience of not importing a file; revenue recognition is the part with substance (it posts through the
free ledger and adds a period close check). The price list (`docs/commercial/modules-and-pricing.md`: "Billing: per person using it per month, included from Team") still describes the old module and **must be rewritten before
`billing` is enforced**: a "Billing" line that includes invoicing sells something that is free. Options the owner might weigh: fold the two leaves into `accounting` and retire `billing` as a sold line; keep `billing` as a
small "Revenue and QuickBooks/Xero" add-on; or price live push on its own and give revenue recognition to the Business edition. Nothing is enforced today (`billing` is not enforced), so no current license loses anything, and
the ids `billing` (license server, trial lists, issued keys) are unchanged and still valid.

Things people may notice that follow from the same decision:

* **Invoicing and receivables are free**, including everything under *Billing & AR* and the standard reports AR aging and customer statements. A key that lists only `billing` unlocks revenue recognition and live push, nothing else
  (`ModuleSeatTests`, `ModuleEntitlementTests` and `BillingLeavesAbsentTests` say so).
* **The project page's Revenue tab and the Estimates at completion report page appear only with the module.** Without it a project manager has no EAC to-dos and project start no longer sets revenue up from the contract.
* **The "Live connections" tab of Accounting export and the "Push to QuickBooks/Xero" panel appear only with the module**, as slots: the export page works as before (tabs Export, Account mapping, History). The OAuth callback page
  (`/admin/integrations/accounting-live/callback/{target}`) is the module's. A link to `?tab=connections` on a host without the module opens the Export tab.
* **The menu group "Revenue & compliance" is gone without the module**: Indirect rates is its only page, so the menu shows that one link where it showed a group.
* **The sold line no longer mentions invoicing**, but the public docs and the marketing pages still do (below): they are not edited here.
* **The load-data tool no longer runs a revenue phase** (`tools/Workbench.LoadData` read the revenue services directly); a load test of the revenue run needs the module and its own tool step.

## What moved and what did not

**Moved to the module** (57 files, about 6,850 lines of production code, 10 tables, plus the moved tests: 8 files):

| Area | Core layers that held it |
| --- | --- |
| Revenue recognition: setups, estimates at completion, deliveries, the revenue run (preview, post, reverse), the engine and calculator, the three reports, the close check | Domain `Revenue/`, Contracts `Revenue/`, Application `Revenue/` (7 files), EF configurations, `RevenueEndpoints`, `HttpRevenueServices`, UI `Revenue/` (13 files), `RevenueSeed` |
| Live accounting push: OAuth for QuickBooks Online and Xero, token service (renewal under a lock, protected storage), the two publishers, the push service with preview, retry and provider links | Domain `AccountingExport/AccountingLive.cs` (5 entities), Contracts `AccountingLive/`, Application `AccountingLive/` (9 files), the five configurations in `AccountingExportConfigurations`, `AccountingLiveEndpoints`, `HttpAccountingLiveServices`, UI `AccountingExport/` (Live connections tab, push panel, run view, OAuth callback) |
| One permission (`revenue.manage`), its decorators, five menu entries, the `ModuleMap` rows, the API segments `revenue` and `accounting-live`, the key segment `revenue` | `Permission`/`PermissionCatalog`, `IdentityRegistration`, `AccountingExportPermissionDecorators` (the two live decorators), `NavCatalog`, `ModuleMap`, `ApiKeyScopes` |
| Sample data: project 1001's revenue setup (cost plus fee), an approved EAC and the current period's posted run | `RevenueSeed`, `DatabaseInitializer` |

The pages, routes (`/accounting/revenue/...`, `/projects/{id}/revenue`, `/admin/integrations/accounting-live/callback/...`), API paths (`/api/v1/revenue`, `/api/v1/accounting-live`), the permission key and role behavior are unchanged.

**The re-measure.** The audit counted 26 files / 3,281 lines for Revenue (2 real core files) and 12 files / 2,370 lines for live push (4 real core files). Compiling after the move found **about nine places in core for revenue and five for
live push** (below); none needed the leaf to stay, and each became a hook, a slot or a contribution. The audit's other candidates in the billing folder were measured again and are **not leaves**:

| Candidate | Result |
| --- | --- |
| Commercial billing (agreements, periods, retainers: `AgreementBillingService`, `AgreementPeriods`, `InvoiceGenerator.Commercial`) | The generator is one class split across two files, so the agreement code calls and is called by the invoice engine; its only callers outside the folder are tests. It moves with the engine, not before it (phase 3b of the billing doc). |
| Overdue and due-soon reminders (dunning) | `OverdueInvoiceCheck` and `DueSoonInvoiceCheck` are inside `ArReportService` and `ReceivablesService`, which the receivables pages, the home widgets and the standard reports call. Not separable without a split of those services. |
| AR analytics (aging, statements, unbilled accrual, billing reports) | Read by the standard reports (`StandardReportService`), the home widgets, My to-do, the close checklist and the cash forecast. Core. |
| Customer share link and view | Already sold under `enterprise` by `ModuleMap.ServiceTypes`; not billing. |
| Milestones, payment application, `BillingCostSource`, `BillingLedger`, `BillingKernel`, `IBillingLimits` | The kernel the other paid modules read (government billing, project controls, accounting seeds, shipping). Core. |

So the sold `billing` module is exactly the two leaves.

**Stayed in core**, each for a reason:

* **Invoices and payments** (`IInvoiceService`, `IPaymentService`, the generator, documents, ready to bill, receivables, milestones, retainers, commercial agreements) and the receivables ledger records: 84 core files and five paid modules read them. Free.
* **The accounting file export** (`AccountingExport`: the exporters, mapping, history, `ExportDataLoader`, `ExportInput`): it is the module's input too, so `ExportDataLoader` is **public** now (it was internal). Free, as before.
* **`JournalSource.Revenue`, `CloseCheckKind.RevenueRecognized`, `Account` type `Revenue`**: saved data and the ledger's own names. Without the module there is no revenue run source and no revenue close check, and nobody can post the former.
* **`ProjectHubTabsDto.Revenue`**, the project page's per-viewer flag (the same rule as Financials): the module's tab reads it.
* **The Estimates at completion report key and the Revenue help article**: the report page is the module's registration now; the help article (`revenue-run`) is an embedded resource and stays (see below).

## Coupling: every place core referenced the leaves, and the resolution

| # | Core referenced the leaf through | Resolution |
| --- | --- | --- |
| 1 | `ModuleMap`: API segments, `revenue.` prefix, nav pages `accounting/revenue*`, namespaces `Revenue` and `AccountingLive`, group Billing & AR | module rows for the leaves; `billing` segment, `billing.` prefix, the Billing & AR group, `billing/invoices`, `billing/statements` and the `Billing` namespace map **core** |
| 2 | `Permission.RevenueManage` and its catalog row; the built-in role parity test row | **move** (`BillingPermission.RevenueManage` in the module contracts; the catalog row is in `Describe().Permissions`, Finance holds it as before) |
| 3 | `NavCatalog`: five entries | **move** (`ModuleContribution.NavEntries`) |
| 4 | `DependencyInjection` (`AddWorkbenchRevenue`, `AddWorkbenchAccountingLive`), `IdentityRegistration` (`AddWorkbenchRevenuePermissions`), `AccountingExportPermissionDecorators` (the two live decorators), `WorkbenchApi`, `ApiClient.DependencyInjection` | **move** into the module's `ConfigureServices` and `MapEndpoints` |
| 5 | `DatabaseInitializer`: `RevenueSeed` | **new stage** `ModuleSeedStage.AfterResourcing` at the same point; `AfterRevenue` still follows it |
| 6 | `ProjectStartService`: set up revenue from the contract when a project starts or a handed-off project is finished | **`IProjectStartHook`** (list; `Workbench.Application.Projects`), implemented by the module (`RevenueProjectStartHook`, the same logic and the same swallowed permission errors) |
| 7 | `ProjectTodoProviders`: `EacReviewTodoProvider` (EAC every 90 days, rejected EACs, Finance asked to decide) | **move**: the module registers it as an `IMyTodoProvider` |
| 8 | `ProjectHubPage` / `ProjectsFormat`: the built-in Revenue tab | **`ProjectHubTabContribution`** (slug `revenue`, label Revenue, order 60, under More, the Financials rule); the built-in tab list and the page's switch lose it; a link to `/projects/{id}/revenue` without the module opens the Overview |
| 9 | `StandardReportService`: the "Estimates at completion" page entry and `StandardReportKeys.EstimatesAtCompletion` | **`StandardReportPage`** registered by the module (same key, page and permission); the core constant is gone |
| 10 | `PeriodClosePage` / `FinanceTodoProviders`: links to `accounting/revenue` for the revenue check | stay: the check itself is the module's `IPeriodCloseCheck`, so without the module the item never appears and the link is never shown |
| 11 | `ApiKeyScopes`: area `billing` listed `revenue` | the module adds `revenue` to the area (`ApiKeySegments`); core's area is `billing` only (title "Billing") |
| 12 | `AccountingExportPage`: the Live connections tab; `AccountingExportTab`: the push panel; `AccountingExport` UI `AccountingLiveCallback` | **two slots**, `accounting-export.tabs` (the tab panel renders itself; the page ignores `?tab=connections` without it) and `accounting-export.push` (the panel, inside the same QuickBooks/Xero condition); the callback page is the module's |
| 13 | `AccountingExportConfigurations`: the five live push entity configurations; the old migrations that created the tables | see Data |
| 14 | `ExportDataLoader` (internal) used by the push service | made **public** (a short list of what is public for modules: `AccountingMapping`, `LedgerLookups`, `EnsureDefaultAsync`, `ExportDataLoader`) |
| 15 | Tests: `Revenue/*` (4 files), `AccountingLive/*` (4), `MyTodoProviderTests` (the EAC rule), `ProjectLifecycleTests` (revenue set up at start, twice), `ReportApiTests` (the EAC page), the role parity row, `ModuleMap`, entitlement and seat tests | see Tests |
| 16 | `tools/Workbench.LoadData`: a phase that ran the real revenue services and counted revenue runs | **removed** (see above) |
| 17 | The Revenue help article (`revenue-run`, an embedded resource, mapped from `accounting/revenue` in `HelpRouteMap`) | **stays** (public): the article is reachable only from the module's pages |

### Seams added

| What | Where |
| --- | --- |
| `IProjectStartHook` | `Application/Projects/ProjectHooks.cs`; `ProjectStartService` asks every registered hook after the project exists |
| `ModuleSeedStage.AfterResourcing` | `Application/ModuleSeed.cs`; run between the resourcing seed and `AfterRevenue` |
| Slots `accounting-export.tabs` and `accounting-export.push` | the accounting export page and its export tab |
| Public: `ExportDataLoader` | `Application/AccountingExport/ExportDataLoader.cs` |
| Through existing seams: five menu entries, one permission, two API segments and one key segment, two permission decorator sets, one `IMyTodoProvider`, one `ProjectHubTabContribution`, one `StandardReportPage`, one `IPeriodCloseCheck`, one seed, the two migrations contexts | `ModuleContribution`, `IModuleSeed`, `IMyTodoProvider`, `IPeriodCloseCheck` |

## Data: existing installs keep their tables, new installs get them from the module

Same rule and recipe as the earlier modules: no table is dropped, no core migration row changes for an installed database, and the module can be added or left out of an upgrade at any time. The module has **10 tables and 8 indexes**, with
foreign keys to four core tables that its context maps key-only (`Project`, `Clin`, `FiscalPeriod`, `GlJournalEntries`). **No core table has a foreign key into the module's tables**, and no other module's table does either.

The 10 tables: revenue 5 (`RevenueSetup`, `RevenueEstimate`, `RevenueDelivery`, `RevenueRun`, `RevenueRunLine`), live push 5 (`AccountingConnections`, `AccountingOAuthStates`, `AccountingProviderLinks`, `AccountingPushRuns`, `AccountingPushItems`).
A push run shares its id with the `AccountingExportRun` row that shows it in the history (core's table, no foreign key).

1. **`ReleaseBillingLeafTables`** (core, both providers): the model no longer maps the ten entities. EF generated ten `DropTable` statements; `Up` and `Down` are empty (`empty-release-migration.py`). Only the snapshot changes.
   `has-pending-model-changes` is clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL.
2. **Two old core migrations no longer create the tables**: `Wave7RevenueIceReportsEvmCeilingsApprovals` (24 statements: the five revenue tables, their indexes and keys; the rest of that migration, ICE, reports, EVM and ceilings, is untouched
   here) and `AddAccountingLivePush` (15 statements: the whole migration, so its `Up` and `Down` are empty now), in both providers, with `strip-core-migration-tables.py` unchanged.
   Databases that already ran them are unaffected. Neither had raw SQL on these tables, and none of the tables has a column default added by a later core migration (checked: each table is created once and never altered).
3. **The module's `BillingBaseline`** (first migration of `BillingDbContext`, history `__EFMigrationsHistory_billing`): 29 statements, each `CREATE ... IF NOT EXISTS` (PostgreSQL foreign keys that refer to a table made in the same
   baseline are guarded), `Down` empty. `BillingDbContext` maps the four core tables key-only and excluded from migrations, so the module never creates or alters a core table.
4. **Load order.** The baseline references only core tables, which exist before any module runs, so the order among modules does not matter and `DependsOn` is empty.
5. **A host without the module** keeps the tables and rows after an upgrade (nothing creates or edits them) and invoices, records payments, runs the accounting file export, the close checklist (without a revenue item) and
   project start as before. A host that adds the module later finds the tables and rows (including connections and their encrypted tokens).

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseBillingLeafTables`: no table touched | the baseline finds the tables, changes nothing | tables and rows stay, unused |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then the release | the baseline creates the tables with the current schema | no tables, none needed |
| New | no module tables | the baseline creates them | none |

## Tests

| Shipped suite | Now |
| --- | --- |
| `Revenue/*` (calculator, run, permission tests and `RevenueKit`), `AccountingLive/*` (connection and token, push, provider scripts and `LiveKit`) | moved to the module (8 files) |
| `MyTodo/MyTodoProviderTests`: the EAC rule | moved (`EacReviewTests`) |
| `Resourcing/ProjectLifecycleTests`: revenue set up at project start and at hand-off finish | moved (`ProjectStartHookTests`, run through the module's registrations) |
| `Reports/ReportApiTests`: the EAC page | the shipped test now asserts the page and the incurred cost page are not listed without their modules; the module lists the EAC page |
| `Identity/BuiltInRoleParityTests`: the `revenue.manage` row | moved to the module's `The_revenue_permission_is_in_the_catalog_and_held_by_the_same_roles_as_before` |
| `Licensing/*` | `billing` joins the modules "no service maps to it in this build" (`ModuleGateTests`); the seat tests count seats on enterprise (billing has none without its module: `0 of 4` used); the unlicensed page and the page guard examples use Companies (enterprise) |
| `Navigation/NavMenuTests` | the Finance menu has no Revenue pages; the group *Revenue & compliance* holds only Indirect rates now, so the menu shows it as the single link *Indirect rates* |
| new in core: `Modules/BillingLeavesAbsentTests` | a new database has none of the 10 tables and keeps invoices, payments, milestones and the export history; the module's permission, API segments, menu entries and services are absent while invoicing is core; its API paths answer 404 and invoices, payments and the export formats answer; no project tab, no EAC to-do, no revenue close check, no slots or report page |
| new in the module | `MigrationPathTests` (a new install; the baseline equals the previous release's schema; an existing install keeps every table, row and column, gains only history rows, twice; an upgrade without the module keeps its data), `ModuleSurfaceTests` (permission and roles, ModuleMap rows, menu, hooks, project tab, slots, report page, seed, sample data), `ApiSmokeTests` |

## Documentation, demo data and public pages that describe the leaves

Nothing was removed; whether the public site keeps describing a paid module is the owner's decision. Copies of two pages are in the module repository's `docs/` (`finance-revenue.md`, `admin-guide-accounting-live-push.md`). The public references are:

* **Revenue:** `docs/finance/revenue.md`, `docs/finance/accounting.md`, `docs/finance/index.md`, `docs/finance/commercial-billing.md`, `docs/user-guide/projects.md`, `docs/user-guide/reports.md`, `docs/security/permissions.md`, `docs/reference/glossary.md`,
  `docs/training/agenda-finance.md`, `card-finance.md`, `card-manager.md`, `exercise-script.md`, `lifecycle-demo.md`, `docs/ux/e2e-walkthrough.md`, `docs/operations/performance.md` (the load-data revenue phase), `docs/assets/screenshots/revenue.png` and `revenue-estimates.png`, `samples/reconcile/revenue-by-project.csv`
* **Live push:** `docs/admin-guide/accounting-live-push.md`, `docs/admin-guide/accounting-export.md`
* **Commercial and marketing:** `docs/commercial/modules-and-pricing.md` (the module's price line, "Billing without accounting", the plan tables and the per-person price row), `website-copy-suggestions.md`, `docs/editions.md`, `docs/feature-coverage.md`, `README.md`, `mkdocs.yml`, `site/index.html`, `site/costpoint-alternative.html`
* **In the application:** the help articles `revenue-run`, `month-end-close` and `project-page`, `Help/Glossary.cs` and `HelpRouteMap` (`accounting/revenue` opens `revenue-run`), `WhatsNew.md` and the tours (embedded resources; moving them needs a help-article extension point)
* **Internal handover notes:** `docs/operations/revrec.md`, `module-extraction-billing.md` and `module-extraction-audit.md` (still say what they measured), `docs/CONVENTIONS.md`
* **The sample data:** project 1001's revenue setup, its approved EAC and the current period's revenue run exist only with the module. **A new database without it has no revenue journal entries in the current period** (the run posted one, and its reversal
  of the prior period's accrual) and the dashboards' revenue figures read zero: see *Verification* for the exact row-count difference.

## Verification

* Shipped repository, no `modules/` folder: `Workbench.Server.slnf` builds (the MAUI desktop project cannot be built on this machine; it references neither leaf); the filtered groups pass: Licensing 96, Modules 48, Navigation 54, Tenancy 42, Migration 37,
  Identity 276, Reports 20, Customization 51 (1 skipped), Accessibility 10, GoldenPath 14, Dashboards 28, Billing 79, AccountingExport 38 and Setup 46 (**839 passed, 1 skipped**; the Revenue and AccountingLive groups moved to the module and match nothing here).
  The groups were run one at a time (a single combined run is slow, and the full suite is known to hang).
* Module repository, module loaded from the built folder: its **65 tests** pass on InMemory and on SQLite (`WORKBENCH_TEST_DB=Sqlite`); the capture (213), materials (77), project controls (100), workforce (126) and accounting (141) suites still pass against this core
  on both. **One flaky capture test remains on SQLite** (`PricingConsistencySourceTests`, `TeamLinkTests` or `LeadImportTests`, a different one each run: a permission refusal for a test user): it failed once in seven runs on the previous release's core as well (checked against a worktree of
  `859c255`; against this core it failed in four of nine runs, which is a small sample), so it is not caused by this change. `Workbench.Module.GovCon.Tests` does not compile against this core (it did not before either).
* `has-pending-model-changes`: clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL, and for the module's `BillingDbContext` on both, checked with `dotnet ef` directly (`scripts/check-migrations.sh billing`), never with `scripts/new-migration.sh` (the baseline was
  generated with `dotnet ef migrations add` and rewritten by `idempotent-baseline.py`).
* **Upgrade from the previous release's host** (`859c255`, the base of this change; published, run in Development so it seeds the sample company; 270 tables, none of them the accounting module's) against this release's host (published without a `modules/` folder, and with `modules/billing`,
  `License:EditionOverride=Business`):
  * the *upgrade* (the previous release's database, once with the module and once without; both hosts start with no `fail:` lines): only `ReleaseBillingLeafTables` ran in core, and `BillingBaseline` with the module (history table `__EFMigrationsHistory_billing`, one row);
    **every table's row count is identical** (the one change is the core history table, 51 to 52 rows) and the `CREATE` text, the indexes and every row of the 10 tables are identical in both cases;
  * a *new* database made by this host **with** the module has the 10 tables identical to the previous release's (columns with type, nullability, default and key, foreign keys, indexes) and the same row counts in every other table; **without** it the database has none of the 10 tables and the host starts. The sample company is smaller by what the
    module seeds: **1 journal entry fewer (the current period's revenue run), 2 journal lines fewer and 5 audit events fewer**, and the five revenue rows.
* Module tests on SQLite (`MigrationPathTests`): a new install; the baseline's schema equals the previous release's (columns with type, nullability and default, foreign keys and indexes, table by table, against a fixture dumped from the previous release's sample database; the four live push tables have synthetic rows because the sample company has no connection); an existing
  install keeps every table, row and column, gains only history rows, twice; an upgrade without the module keeps its data.
* Not run: PostgreSQL itself (the private repository's CI should run `WORKBENCH_TEST_DB=PostgreSql` and a PostgreSQL upgrade; in particular the foreign keys to `Project`, `Clin`, `FiscalPeriod` and `GlJournalEntries` and the guarded constraints), the full shipped suite (known to hang locally), the MAUI desktop build.

## Not done

* The worker and the desktop app do not load modules: the Revenue tab and the live push are web host features (they were also in the desktop app before; the desktop app has no module loader).
* The module has no end-to-end test that runs a real OAuth round trip against Intuit or Xero (the provider fakes cover the protocol, as before) or a PostgreSQL run (`WORKBENCH_TEST_DB=PostgreSql` is for the private repository's CI).
* The saved-data names (`JournalSource.Revenue`, `CloseCheckKind.RevenueRecognized`, the `revenue.manage` key in custom roles) stay in core's lists and data: a host without the module keeps custom roles that name the permission and shows
  it nowhere.
* PostgreSQL itself, the full shipped suite (known to hang locally), the MAUI desktop build.

## Lessons for the next extractions (enterprise, billing engine)

* **A leaf that posts through the ledger has the same shape as a hook-fed module**: revenue's only outbound seams were the project start (a hook), the to-do providers (a registration), a tab and a report page (contributions). Looking for "who calls
  this leaf's *service interface*" found the two real callers (project start and My to-do); a text search of the type names found the rest.
* **Check the standard report list and the project page tabs**, not only the menu: both were static lists in core. `StandardReportPage` and `ProjectHubTabContribution` already existed for this.
* **Optional constructor parameters of a moved interface are a trap**: `ProjectStartService(..., IRevenueSetupService? revenueSetup = null)` compiled in a host without the interface only because the parameter was optional; the contract still had to
  leave core, so the call became a list of hooks.
* **The permission was a constant and a catalog row in core**: when a permission moves, the string key stays the saved-data name; the module's constant (`BillingPermission`) is the one the module's code uses.
* **Billing engine next** (phase 3b): the generator's two files (`InvoiceGenerator.cs`, `.Commercial.cs`) are one class, so commercial billing moves *with* the invoice and payment services, not before. The kernel and ledger records stay core.
