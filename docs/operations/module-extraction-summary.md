# Module extraction: summary and whole-set verification

The eight paid modules moved out of this repository in PRs #43 to #54 (the plugin seam, then `govcon`, government billing, the audit, the shared seams, `capture`, `materials`, `projectcontrols`, `workforce`, `accounting`, the `billing` leaves and `enterprise`). Each PR proved its own module. This page records the check of **the whole set together**, done in the private module repository's "phase 4" ([module-delivery.md](module-delivery.md) for how modules reach a customer, [module-history.md](module-history.md) for the history question, [module-plugins.md](module-plugins.md) for the contract).

Verified on the tip of the stack (`claude/cranky-darwin-8ec774`, the enterprise PR) plus the small core change in this PR, with the private repository at `master` (its eight extraction branches fast-forwarded into one linear history; the old branches are untouched) plus its phase 4 commits.

## What this change touches in the core

* **`IEstimateAtCompletionSource`** (`Workbench.Application.ProjectAccounting`): government billing's SF 1443 progress payment preview read the billing module's `RevenueEstimate` table directly, which stopped compiling once the billing leaves moved (the govcon module did not build against the stripped core; found by building the whole solution for the first time). The estimate to complete is now read through this interface; the billing module implements it, a host without billing has no source and the preview treats that as "no approved estimate", as it already did for a project nobody had estimated.
* **A warning for a licensed module with no folder** (`Program.cs`): `Module X is in the license but is not installed: there is no 'X' folder in the modules folder, so the product runs without it.` once per id, except under the development override (`License:EditionOverride` Business), which licenses every module.
* **License server test** that its module list equals `ModuleCatalog` minus `core` (the list is kept by hand in both places).
* This documentation.

## Results

| Check | Result |
| --- | --- |
| Build of the core at the tip and of all eight modules (`scripts/build-all.sh`) | 8 modules, 8 zips and one bundle; every `module.json` contract version and core range checked |
| Module suites on SQLite | govcon 100, billing 65, capture 213, materials 77, projectcontrols 100, workforce 126, accounting 141, enterprise 89: all pass (see the notes below on parallelism) |
| Module suites on **PostgreSQL 17** (Docker, local) | the same 911 tests pass. One was a real PostgreSQL-only defect, fixed: workforce's roster-settings audit action was 36 characters in a `varchar(32)` column |
| `Workbench.Modules.Licensing.Tests` (11 tests, real built modules) | pass: see "Licensing" |
| `Workbench.LicenseServer.Tests` | 45 pass |
| Core `Workbench.Tests`, the module plugin, seam and licensing tests (`Workbench.Tests.Modules`, `Workbench.Tests.Licensing`) | 148 pass |
| Full host: all modules, fresh SQLite database | starts with no `fail:` lines; 8 modules `Loaded`; every probed API and page answers (below) |
| Full host: all modules on a fresh PostgreSQL database, and the previous release's PostgreSQL database upgraded | both start; the same probes pass; the only `fail:` lines are EF's probe of the history table on an empty database |
| Upgrade of the previous release's seeded SQLite database (453 tables) | with all modules and with none: starts, no `fail:` lines, **schema identical** (columns, nullability, defaults, keys, foreign keys, indexes: 0 differences), row counts identical except the history table (44 to 53 rows in core, 8 new history tables) and what the probes themselves wrote (1 sign-in event, 1 timesheet) |
| A new database made with all modules vs. the previous release's | same tables and row counts; the schema differs in exactly two foreign keys, by design: `RfqLine.PartId` and `ChecklistTask.AssetId` no longer reference the module tables (a core table cannot depend on a plugin's) |
| No modules, only `capture` (folder only, and key only) | clean start, no `fail:` lines; absent modules' APIs answer 404 and their pages "Not found"; the menu has no People, Materials, Cash, Assets, Planning, Payroll or Compliance entries, and keeps Proposal pricing and Business development |

The "previous release" is `0544441` (the base of PR #43, published and run in Development so it seeds the sample company).

### Full host, through real HTTP and the browser

Host: the core published at the tip, `modules/` holding the eight built folders, a WB2 key listing all eight (signed with a test key, `License:PublicKeyPem`), Development, fresh SQLite, signed in through the dev sign-in as the seeded administrator. All answered 200 (and 404 or "Not found" without the module):

| Module | API | Pages |
| --- | --- | --- |
| govcon | `/api/v1/incurred-cost`, `/api/v1/migration/parallel-run` | `/accounting/incurred-cost`, `/accounting/total-time`, `/setup/migration/parallel-run` |
| billing | `/api/v1/revenue/projects`, `/estimates`, `/runs`, `/api/v1/accounting-live/connections` | `/accounting/revenue`, `/accounting/revenue/estimates` |
| capture | `/api/v1/crm/opportunities`, `/accounts`, `/leads` | `/bd`, `/pricing` |
| materials | `/api/v1/materials/parts`, `/api/v1/engineering-changes`, `/api/v1/materials/sales-orders` | `/materials/inventory` |
| projectcontrols | `/api/v1/projects/{id}/evm/periods`, `/evm/baseline`, `/api/v1/risks/projects/{id}`, `/api/v1/projects/{id}/ceilings` | the project page's Risks tab |
| workforce | `/api/v1/hr/employees`, `/hr/positions`, `/payroll/pay-groups`, `/payroll/runs` | `/hr/org-chart`, `/payroll` |
| accounting | `/api/v1/cash/accounts`, `/assets`, `/planning/budgets`, `/sales-tax/settings` | `/cash`, `/assets` |
| enterprise | `/api/v1/costpoint/status`, `/integrations/jira/connection`, `/integrations/gitlab/connection` | `/admin/costpoint` |

In the browser (Blazor UI, signed in as the administrator) the menu showed Proposal pricing, HR administration, Directory, Business development, Cash, Assets, Planning, Payroll, Inventory, Manufacturing, Quality and engineering, Sales and shipping and Compliance, and the project page's tabs (Plan, Budget, Financials, Costs, Risks, Setup) rendered. With only `capture` loaded the same menu had Proposal pricing and Business development and none of the others.

The check found one regression of the phase's own change (the govcon sample data seed resolved a scoped service from the root provider once billing was loaded; the module's own tests could not see it because they run without billing), fixed before the run above. That is the case for running a whole-set check.

## Licensing

`Workbench.Modules.Licensing.Tests` (private repository), run against the real built modules:

* **An unlicensed module's assembly is not loaded.** For each of the eight ids, a key listing the other seven makes the loader report `NotLicensed` and the assembly is not in the process. No key, an invalid key, a key signed by another signer and an expired key license nothing.
* A key lists exactly the modules it names; a WB1 key licenses all eight; an unknown id is ignored; `core` is never a plugin.
* The legacy ids: `finance` licenses `billing` and `accounting`, `controls` licenses `enterprise`, and together with a current id as `ModuleCatalog` says. (The license server refuses to issue them.)
* A license that lists a module whose folder is missing starts cleanly and the web host logs the warning above; a folder whose assembly is missing is reported as `MissingAssembly` and the rest load; an absent or empty modules folder is not an error.
* The catalog sells the eight plugin modules plus the reserved `ai`; only `govcon` and `enterprise` are enforced, and only for their named features (the prices of the others are not decided).
* **A hosted tenant without the module gets nothing**: with every module enforced for its whole self, a tenant whose plan lists only `capture` is refused the other seven (`HasModule` false, `Requires` true). Under the catalog as it ships today (the other modules are not enforced) a loaded module is open to every tenant of that host: keeping a tenant from a module the operator did not install is then only that the host has nothing of it. Enforcing a module is the pricing flag in `ModuleCatalog`, not a code change.

## What paid code remains in the public repository

By design, the **kernels** stay: the general ledger and posting, invoicing, receivables and payments, payables and purchasing, resourcing and the leave engine with the pay calendar tables, the platform layer (custom fields, the automation builder, the outbox, webhooks, API keys, companies, approval workflows, access reviews, retention), schedule health, total time accounting costing (`LaborCostBook` and friends, which the ledger calls), and the seam itself. They are free. What this section measures is the module-named code that is left, the by-design residue and the documentation.

| Module | Source and tests still in core with the module's names | Migrations kept (history; the old migrations still create the tables for installs older than the extraction) | Public documentation pages (`docs/`) |
| --- | --- | --- | --- |
| govcon | 196 lines in 2 files (`TotalTime` kernel; `CpsrPurchaseOrderSeed`, a core sample), 137 lines of tests | 8 files | 2 pages, 97 lines |
| billing | none | 4 files | 2 pages, 245 lines |
| capture | none | 8 files | 3 pages, 226 lines |
| materials | none | none counted by name | 5 pages, 202 lines, screenshots |
| projectcontrols | 56 lines (kernel) | 8 files | 1 page, 34 lines |
| workforce | 62 lines of pay configuration, 106 lines of the pay calendar kernel, 84 lines of tests | 4 files | 3 pages, 257 lines |
| accounting | none | none counted by name | 3 pages, 150 lines |
| enterprise | `ModuleMap` rows and four feature gates for the gates that stay in the product (second company, single sign-on, adding connectors, custom approval workflows) | 8 files | 4 pages, 244 lines |

("Migrations kept" counts files named for the module's features, about 551,000 lines, mostly designer and snapshot files; the module tables' definitions still exist there because an existing install's migration history must stay valid. They are table definitions, not business logic.) The size of what moved, from the private repository (code, Razor and tests / migrations): govcon 16,085 / 5,810; billing 9,088 / 3,692; capture 26,910 / 9,798; materials 31,455 / 23,653; projectcontrols 13,702 / 5,871; workforce 23,962 / 9,862; accounting 29,085 / 14,321; enterprise 16,310 / 6,470 lines.

## Known gaps and decisions left

* **`Workbench.Worker` and the desktop app do not load modules.** Jobs a module owns run in the web host. A deployment that used the standalone worker for the Costpoint sync must run the web host with the enterprise module (the worker project is gone).
* **Public documentation and the site still describe the paid modules** (the pages in the table, `docs/commercial/`, `site/` marketing pages, `docs/feature-coverage.md`). They are copies in the private repository too. Whether the public documentation should keep describing paid features is the owner's call; nothing was deleted.
* **History** still holds all the code: [module-history.md](module-history.md).
* **A core NuGet feed** for building the modules is not available (the core publishes no packages): the modules build against a path to a core checkout, and CI checks the public repository out at `WORKBENCH_CORE_REF`. The core has no version file, so the `core` range in each `module.json` is checked against `WORKBENCH_CORE_VERSION` or the newest `v*` tag.
* **Module assemblies are not signed.** The key proves entitlement, not provenance.
* **Hosted tenants and enforcement**: only `govcon` and `enterprise` are enforced, for named features; every other loaded module is open to every tenant of that host until the price flags are flipped.
* **A host without a module drops webhooks silently** (the enterprise note): Jira and GitLab post to routes that do not exist and the catch-all page answers.
* **CI is written, not run**: the private repository has no remote. The workflows were not executed on GitHub; their commands were run locally (`scripts/build-all.sh`, `scripts/test-all.sh` on both providers, the licensing tests).
* The full shipped suite (`Workbench.Tests`) is known to be slow or to hang locally and was not run in full; the module, licensing and license server tests were.
* **Tests run serially in each module's test project** now. The host keeps the loaded modules in process-wide state and several tests start their own web host with its own license, which replaced the set while other tests used it (8 accounting and 5 enterprise failures in the parallel run, none when serial).
