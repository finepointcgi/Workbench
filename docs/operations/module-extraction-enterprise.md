# Extracting a module: enterprise (Jira and GitLab sync, Costpoint sync, customer links)

Phase 10 of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md); the earlier moves are
[govcon](module-extraction-govcon.md) (with [government billing](module-extraction-billing.md)), [capture](module-extraction-capture.md),
[materials](module-extraction-materials.md), [project controls](module-extraction-projectcontrols.md), [workforce](module-extraction-workforce.md),
[accounting](module-extraction-accounting.md) and the [billing leaves](module-extraction-billing-leaves.md); the [audit](module-extraction-audit.md) called `enterprise`
class C: a platform layer under 52 real core files and 20 files of the other paid modules, with only five features gated). This page moves **only the slices that are leaves**:
the Jira and GitLab sync, the Costpoint sync and customer view links. The platform layer (custom fields and layouts, the automation builder, the integration outbox with webhooks,
API keys and connectors, companies, approval workflows, access reviews and retention) stays in core, **and the catalog now says so**. It is the re-measure, **the change to the sold line**,
every place core touched the slices and how it was resolved, the data path for existing installs, and what is still public.

The slices now live in the **private repository** (`Workbench-Modules`, a local git repository with no remote yet) as `src/Workbench.Module.Enterprise*`, build into `dist/modules/enterprise/` (copy it to a host's
`modules/enterprise/`), and load like any plugin. This repository ships without them.

## The price line changed: read this first

`enterprise` is enforced for named features only (`WholeModule: false`): five features need a key that lists it. Before this change that key unlocked five gated save buttons on top of a large body of
code that was free in practice. After it, **the sold module is the code that moved, and what stayed is either free or still gated by the same key as before. Nothing was made free or paid by this change except
what the second table says.** Every decision that is the owner's is listed here, not decided in code.

### Features whose paid/free status changed, or is ambiguous (for the owner)

| Feature | Before | Now | Status |
| --- | --- | --- | --- |
| **Jira sync** (connect, link projects, the sync engine, the polling job, the webhook receiver `/webhooks/jira/{linkId}`, the Jira tab, the task and project panels) | Saving the connection needed an enterprise key. Everything else (reading, linking, sync, webhooks) worked without one | **Module.** The save gate stays (module feature gate `Integrations`). Without the module there is no Jira page, no sync and no webhook receiver | **Changed free to paid** for everything except the connection save, which was already gated. A host that already linked projects and then runs without the module **stops syncing** (its rows stay) |
| **GitLab sync** (same shape, `/webhooks/gitlab/{linkId}`) | same as Jira | same as Jira | **Changed free to paid**, same note |
| **Costpoint sync** (the read-only mirror, the manual sync, plan versus actual, the admin page `/admin/costpoint`, the scheduled pull) | Ungated: free. The `enterprise` catalog text mentioned "Costpoint" under *Integrations* but no gate covered it | **Module.** The scheduled pull now runs in the web host (it was the standalone worker's only job; see below) | **Changed free to paid** |
| **Customer view links** (make, list and end a link, the Customer link tab, the anonymous `/c/{token}` page) | Creating a link needed an enterprise key. Listing, ending and opening an existing link did not | **Module.** The create gate stays (module feature gate `CustomerLinks`). Without the module an existing link's page says "isn't available" and Finance cannot list or end links | **Changed free to paid** for listing, ending and the customer's page |
| **More than one company** | gated (`MultiCompany` on `ICompanyService.CreateAsync`) | **core, gate kept** | **Ambiguous: gate only.** The key buys a gate on core code. Free it by deleting the `Gate<ICompanyService>` line in `EditionGates` and the feature |
| **Single sign-on** (the provider save) | gated | **core, gate kept** | **Ambiguous: gate only.** SCIM and sign-in settings were never gated |
| **Connectors** (adding and changing an outbound connector) and **Slack and Teams connections** | gated (`Integrations` on `IConnectorService.CreateAsync/UpdateAsync` and `…ConnectAsync`) | **core, gates kept** | **Ambiguous: gate only.** The outbox, webhooks, API keys and delivery log were never gated |
| **Custom approval workflows** (`SaveAsync`, `CreateRecordWorkflowAsync`) | gated | **core, gate kept** | **Ambiguous: gate only, and woven in** (see *Left in core*) |
| **Custom fields and layouts, the automation builder, API keys, webhooks, the integration outbox** | listed in the `enterprise` catalog text, **never gated** (free in practice) | core; the catalog text and the map now say free | **Label only: no behavior change** |
| **Access reviews and audit retention** | listed in the catalog text, never gated | core; label moved from `enterprise` to `core` | **Label only: no behavior change.** Technically leaves (see below); moving them would make free features paid |
| **EDI** (`IEdiService`, `admin/integrations/edi`) | mapped to `enterprise`, never gated | label moved to `core` | **Label only** |
| **Supplier portal** | gated by the Business edition (module `core`) | unchanged | not touched |

The `ModuleCatalog` text of `enterprise` now reads: "The add-on code that loads from the enterprise module: Jira and GitLab sync of project plans, the Costpoint sync ... and customer view links. A key that lists enterprise also unlocks four gates that stay in the product:
a second company, single sign-on, adding connectors and Slack or Teams connections, and custom approval workflows. Custom fields and layouts, the automation builder, webhooks, API keys, audit retention and access reviews are in the product, and free."
The `core` text gained "custom fields and layouts, the automation builder, the integration outbox with webhooks, API keys and connectors, audit retention and access reviews". `Enforced: true, WholeModule: false` is unchanged.
The price list (`docs/commercial/modules-and-pricing.md`) still describes the old module and **must be rewritten before anything sells it**: it says "Enterprise" is SSO, companies, custom fields and automations, and most of that is now either free or a gate.

**Decisions for the owner** (none was made here): (1) keep the four surviving gates, or free them (one line each in `EditionGates`); (2) is a key that unlocks four gates plus three add-ons one product, or should the gates be retired and the module be the whole product;
(3) whether Jira and GitLab links that already exist should keep syncing for a host without the module (today they do not); (4) whether access reviews and retention should be paid (they are small leaves, but the retention purge runs nightly for every host today); (5) the price.
Nothing is enforced any differently today: the shipped flags are the same and a key that lists `enterprise` still loads the module and still passes every gate. A key that does *not* list it loses nothing it had, except the three bullets marked "changed free to paid".

## What moved and what did not

**Moved to the module** (80 files, about 12,600 lines of production code including the module's own 500 lines of glue, 20 tables, 28 indexes, and 13 moved test files):

| Area | Core layers that held it |
| --- | --- |
| **Jira sync**: connection, project links, task links, the sync engine, outbox connector, webhook receiver, polling job, the Jira tab, a project's panel, a task's link | Domain `Integrations/Jira` (5 entities), Contracts `Integrations/Jira`, Application `Integrations/Jira` (7 files, 2,470 lines), `JiraEndpoints`, `HttpJiraServices`, `JiraConfigurations`, UI `Integrations/Jira` (607 lines), `JiraWebhookEndpoints` (Web) |
| **GitLab sync** (issues, epics and milestones; same shape; the person matching dialog) | Domain `Integrations/GitLab` (7 entities), Contracts, Application `Integrations/GitLab` (10 files, 4,626 lines), `GitLabEndpoints`, `HttpGitLabServices`, `GitLabConfigurations`, UI (775 lines), `GitLabWebhookEndpoints` |
| **Costpoint sync**: the mirror (7 entities), `CostpointSyncService`, `CostpointService`, plan versus actual, the source adapters (`ICostpointSource`, the CSV folder source, the REST and database stubs, `AddCostpointSource`), the admin page, the status chip, the scheduled pull | Domain `Costpoint`, Contracts `Costpoint`, Application `Costpoint` (4 files), `Workbench.Costpoint` (the adapters), `CostpointEndpoints`, `HttpCostpointService`, `CostpointConfigurations`, UI `Costpoint` and `Pages/Admin/CostpointAdmin`, `CostpointSyncWorker` |
| **Customer links**: the link service and the anonymous view, the Customer link tab, the page and its layout | Domain `Billing/CustomerShareLink`, Contracts `Billing/CustomerViewContracts` (now namespace `Workbench.Contracts.CustomerLinks`), `CustomerShareLinkService`, `CustomerViewService`, the three endpoints under `/api/v1/billing/contracts/{id}/customer-links`, `HttpCustomerShareLinkService`, `CustomerShareLinkConfiguration`, UI `Setup/CustomerLinksPanel`, `Web/Customer` (endpoint, layout, two pages) |
| Two permission decorators (Costpoint, customer links), three feature gates, five hosted services (the Jira and GitLab change listeners and polling jobs, and the Costpoint pull), two connectors (`JiraSyncConnector`, `GitLabSyncConnector`) | `PermissionDecorators`, `BillingPermissionDecorators`, `EditionGates`, `IntegrationRegistration`, `IntegrationPermissionDecorators`, `NotificationsHub` registration |

The pages, routes (`/admin/costpoint`, `/c/{token}`, `/webhooks/jira/{linkId}`, `/webhooks/gitlab/{linkId}`), API paths and permission keys are unchanged.

**The re-measure.** The audit counted Jira and GitLab at about 7,100 lines of Application (2,470 and 4,626: right), Costpoint at 11 files and 1,081 lines, the integrations admin UI at 26 files and customer links at about 150 lines. Compiling after the move confirmed
the slices are leaves: the only core references were the registrations, the tabs and panels on four pages, the permission decorators, three gates, the webhook mapping and a handful of `internal` helpers that the sync engines needed (made public below). The audit's other candidates were measured again:

| Candidate | Result |
| --- | --- |
| Integrations admin page and tabs | The page stays: webhooks, deliveries, API keys, connectors, Teams and Slack are the platform's own screens. Only the **GitLab and Jira tabs** moved, through a slot |
| Connectors framework (`ConnectorService`, `ConnectorRunner`, `IConnector`, chat and file connectors, `IntegrationConnector`) | **Not a leaf.** The entity is read by automations, webhooks, the dispatcher and the Slack and Teams notifications that free reminders use; about 20 files of core and tests reference its types. Jira and GitLab *plug into it* (each registers an `IConnector`) |
| Outbox, webhooks, delivery log, API keys, MCP | Core plumbing used by free features (the outbox on projects, timesheets, forms; the assistant tool host). Stays |
| Custom approval workflows (`ApprovalWorkflowService`, the Approval rules pages) | **Woven in.** The service also holds the org unit approvers, the reset to default and the read side the standard timesheet, leave and expense approvals use; the pages are hosted by the automation admin page. Only `SaveAsync` and `CreateRecordWorkflowAsync` are gated, so the gate stays and the code stays |
| Access reviews (`AccessReviewService`, its tab) | A real leaf: 2 files, 559 lines, 2 core touch points (a registration, a tab). **Not moved**: it was never gated, so moving it makes a free feature paid; its tables and entities sit in the shared security entity file. Listed as a decision above |
| Retention (`RetentionService`, its tab, the nightly purge job) | A leaf by references (3 files, 708 lines, the job and an options object in the web host), but the purge runs for every host today: a host without the module would stop purging. **Not moved** |
| Companies, custom fields, layouts, automations | The platform layer (12 and 14 core files, 11 files of other modules). Stays |
| EDI, supplier portal | Not enterprise code (purchasing). Untouched; EDI's map label moved to core |

**Stayed in core**, each for a reason: the platform layer above; the `Cp*`-free parts of the Costpoint **importers** (they import Costpoint exports on demand, and `CsvRecordReader` is the tokenizer both they and the module's file adapter use: it was `CostpointCsv.ReadRecords`, now `Workbench.Application.Imports.CsvRecordReader`);
the connector framework Jira and GitLab register into; the webhook signature helper (`WebhookSignature`) the receivers use; and `IntegrationFormat` (UI text helpers, made public for the module).

## Coupling: every place core referenced the slices, and the resolution

| # | Core referenced the slice through | Resolution |
| --- | --- | --- |
| 1 | `ModuleMap`: segments `integrations`, `customization`, `automations`, `access-reviews`, `retention`, `edi`, `costpoint`; four `admin.` permission prefixes; `admin/Customization` and the integrations pages; the `Integrations`, `Integrations.Jira`, `Integrations.GitLab`, `Costpoint`, `Automations` and `Customization` namespaces; four service types | The module owns `costpoint` and the Jira, GitLab, Costpoint and customer link services. **Core rows now** (free): the platform segments, namespaces, `admin/integrations`, `admin/Customization`, `IAccessReviewService`, `IRetentionService`, `IEdiService`. **Still `enterprise`** (a gate needs a key): `companies`, `approval-workflows`, the `Companies` namespace, `IApprovalWorkflowService`, `setup.companies`, `approvals.workflows.manage` and their pages |
| 2 | `ModuleCatalog` text of `enterprise` and `core`, the `Integrations` and `CustomerLinks` feature texts | rewritten (above) |
| 3 | `EditionGates`: three gates (`IJiraConnectionService.SaveAsync`, `IGitLabConnectionService.SaveAsync`, `ICustomerShareLinkService.CreateAsync`) | **move** into the module's `ModuleContribution.FeatureGates` (the seam govcon's incurred cost uses) |
| 4 | `DependencyInjection` (`AddWorkbenchCostpoint`), `IntegrationRegistration` (Jira and GitLab, and their hosting), `IntegrationPermissionDecorators`, `BillingRegistration`, `IdentityRegistration`, `PermissionDecorators`, `BillingPermissionDecorators`, `WorkbenchApi`, `ApiClient.DependencyInjection`, `HttpIntegrationServices`, `HttpBillingServices`, `BillingEndpoints` | **move** into the module's `ConfigureServices` and `MapEndpoints` (`EnterprisePermissionDecorators` carries the two moved decorators) |
| 5 | `Workbench.Web/Program.cs`: `MapWorkbenchCustomerView`, `MapWorkbenchJiraWebhooks`, `MapWorkbenchGitLabWebhooks`, `AddCostpointSource` | **new seam `IWorkbenchModule.MapHostEndpoints(app)`**: routes at the host's root, outside `/api/v1`, mapped by the module (anonymous: a token or a signature is the only key). `AddCostpointSource(configuration)` is the module's `ConfigureServices` |
| 6 | `IntegrationsAdminPage`: the GitLab and Jira tabs and the `?tab=gitlab` and `?tab=jira` links | **slot `integrations.tabs`** (each panel is a `MudTabPanel`, like the accounting export's); `?tab=gitlab` without the module opens the first tab |
| 7 | `ItemDetailsDrawer`: the GitLab and Jira links on a task | **slot `item.details.links`** (parameters `SheetId`, `ItemId`) |
| 8 | `ProjectSetupTab`: the GitLab and Jira panels | **slot `project.setup.panels`** (parameters `ProjectId`, and `IsOpen` and `Toggle` so the route's `/setup/jira` still opens its panel) |
| 9 | `ContractPage`: the Customer link tab | the existing slot `contract.tabs` (a `CustomerLinksTabPanel`, order 100, after the project controls tabs) |
| 10 | `CostpointSyncWorker` in `Workbench.Worker` (the project's only job) | **the worker project is removed** (see *Not done and surprises*). The module's `CostpointSyncBackgroundService` does the same pull in every tenant of the web host, on `Costpoint:SyncInterval`, and does nothing while `Costpoint:Source` is `None` |
| 11 | `Workbench.Costpoint` (adapters) and `Application/Imports/TabularFile` (the CSV tokenizer) | the adapters move; the tokenizer becomes core's `CsvRecordReader`; the project is deleted and removed from the solution, the solution filter, the Dockerfile and the README table |
| 12 | `internal` members the engines needed | made **public**: `OutboundUrlGuard.ParseShape`, `IntegrationOutbox.Build` and `.Json`, `RequireIntegrationAdminAsync`, `PlanReflow` and `PlanState`, `IntegrationFormat` |
| 13 | The customer link contracts were in `Workbench.Contracts.Billing`, which `ModuleMap` maps to core | **namespace changed** to `Workbench.Contracts.CustomerLinks` (a module may not claim a service the built-in namespace table already maps). Only moved code and tests used the types |
| 14 | The old core migrations that created the tables; `ApplyConfigurationsFromAssembly` | see Data |
| 15 | Tests: `Integrations/Jira/*` (5 files), `Integrations/GitLab/*` (4 files and 10 fixtures), `Costpoint/*` (3 files, `CostpointCsvTests` among them), `GoldenPath/CustomerViewTests`, the Costpoint permission test in `PermissionEnforcementTests`, the three names in `ModuleGateTests.FeatureGated` | see Tests |
| 16 | `README.md` rows for `src/Workbench.Costpoint` and `src/Workbench.Worker` | removed (the config lines under *Configuration* are listed below, not edited) |

### Seams added

| What | Where |
| --- | --- |
| `IWorkbenchModule.MapHostEndpoints(IEndpointRouteBuilder app)` and `ModuleHost.MapHostEndpoints` | `Workbench.Modules.Abstractions`; `Workbench.Web/Program.cs` calls it where the three mappings were. Optional, so `ModuleContract.Version` stays 1 |
| Slots `integrations.tabs`, `item.details.links`, `project.setup.panels` | the integrations page, the task details drawer, a project's Setup tab |
| Public: `CsvRecordReader`, `OutboundUrlGuard.ParseShape`, `IntegrationOutbox.Build/Json`, `RequireIntegrationAdminAsync`, `PlanReflow`, `PlanState`, `IntegrationFormat` | `Workbench.Application`, `Workbench.Ui` |
| Through existing seams: one API segment, four service-interface sets, three feature gates, one `UiSlotContribution` list, two `IConnector` registrations, five hosted services, one `DbContext` | `ModuleContribution`, `ModuleHost.ConfigureServices`, `Migrations` |

## Data: existing installs keep their tables, new installs get them from the module

Same rule and recipe as the earlier modules: no table is dropped, no core migration row changes for an installed database, and the module can be added or left out of an upgrade at any time. The module has **20 tables and 28 indexes**, with
foreign keys to two core tables that its context maps key-only (`Project`, `Contract`). **No core table has a foreign key into the module's tables**, and no other module's table does either.

The 20 tables: Jira 5 (`JiraConnections`, `JiraProjectLinks`, `JiraItemLinks`, `JiraSyncNotes`, `JiraWebhookReceipts`), GitLab 7 (`GitLabConnections`, `GitLabProjectLinks`, `GitLabItemLinks`, `GitLabUserMaps`, `GitLabSyncConflicts`, `GitLabPendingIssues`, `GitLabWebhookReceipts`),
the Costpoint mirror 7 (`CpProjects`, `CpWbsElements`, `CpEmployees`, `CpLaborCategories`, `CpBudgets`, `CpActuals`, `CpSyncRuns`) and `CustomerShareLink`.

1. **`ReleaseEnterpriseTables`** (core, both providers): the model no longer maps the twenty entities. EF generated twenty `DropTable` statements; `Up` and `Down` are empty (`empty-release-migration.py`). Only the snapshot changes.
   `has-pending-model-changes` is clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL.
2. **Four old core migrations no longer create the tables**, in both providers, with `strip-core-migration-tables.py` unchanged: `InitialCreate` (the seven `Cp*` tables and their 14 indexes, 21 statements), `AddGitLabSync` (25), `CustomerShareLinks` (4) and `AddJiraSync` (18).
   Databases that already ran them are unaffected. None had raw SQL on these tables, and no later migration alters any of them (each table is created once).
3. **The module's `EnterpriseBaseline`** (first migration of `EnterpriseDbContext`, history `__EFMigrationsHistory_enterprise`): 48 statements, each `CREATE ... IF NOT EXISTS`, `Down` empty. `EnterpriseDbContext` maps `Project` and `Contract` key-only and excluded from migrations,
   so the module never creates or alters a core table.
4. **Load order.** The baseline references only core tables, so the order among modules does not matter and `DependsOn` is empty.
5. **A host without the module** keeps the tables and rows after an upgrade (nothing creates or edits them), and runs integrations, companies, custom fields and automations as before. A host that adds the module later finds the tables and rows, including the Jira and GitLab connections and their encrypted tokens.

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseEnterpriseTables`: no table touched | the baseline finds the tables, changes nothing | tables and rows stay, unused; linked projects stop syncing |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then the release | the baseline creates the tables with the current schema | no tables, none needed |
| New | no module tables | the baseline creates them | none |

## Tests

| Shipped suite | Now |
| --- | --- |
| `Integrations/Jira/*`, `Integrations/GitLab/*` (with the fixtures and the fake servers), `Costpoint/CostpointSyncServiceTests`, `PlanVsActualTests`, `CostpointCsvTests`, `GoldenPath/CustomerViewTests`, the Costpoint permission test | moved to the module (13 files) |
| `Licensing/ModuleGateTests` | the three module gates leave the `FeatureGated` list (the module's tests check them); the "changes tried" floor is 450 (it was 500) |
| `Identity/PermissionEnforcementTests` | the Costpoint test moved |
| new in core: `Modules/EnterpriseAbsentTests` | a new database has none of the 20 tables and keeps the platform tables (outbox, connectors, webhooks, API keys, companies, custom fields, contracts, access reviews, retention); the `costpoint` segment and the module's services are absent while the platform segments are core and `companies` and `approval-workflows` stay enterprise; the five features are unchanged; the API paths of the module answer 404, the platform's answer; none of the webhook or customer routes is mapped; no slots, jobs or connectors from the module |
| new in the module | `MigrationPathTests` (a new install; the baseline's schema equals the previous release's, table by table, against a fixture dumped from the previous release's sample database; an existing install keeps every table, row and column and only gains history rows, twice; an upgrade without the module keeps its data), `ModuleSurfaceTests` (the module registers into a bare collection and loads through the loader; ModuleMap rows; slots, jobs and connectors; the three gates refuse without a key and pass with one; flipping the whole-module flag refuses every change and no read; the manual Costpoint sync is for admins), `ApiSmokeTests` (the API paths answer, an employee is refused, the host-root routes are mapped) |

## Documentation, demo data and public pages that describe the slices

Nothing was removed; whether the public site keeps describing a paid module is the owner's decision. Copies of four pages are in the module repository's `docs/` (`integrations-jira.md`, `integrations-gitlab.md`, `integrations-costpoint.md`, `security-customer-view.md`). The public references are:

* **Jira and GitLab:** `docs/integrations/jira.md`, `docs/integrations/gitlab.md`, `docs/admin-guide/integrations.md`, `docs/admin-guide/index.md`, `docs/integrations/unanet-api-sync-design.md`, `docs/feature-coverage.md`, `docs/operations/configuration.md` (the `Jira` and `GitLab` sections), `mkdocs.yml`, `site/index.html`, `site/docs.html`
* **Costpoint sync:** `docs/integrations/costpoint.md`, `docs/security/permissions.md`, `docs/operations/configuration.md` (`Costpoint:Source`, `Costpoint:Csv:Folder`, `Costpoint:SyncInterval`), `README.md` (the three `Costpoint:` configuration lines, one still pointing at the deleted `src/Workbench.Costpoint/README.md`), `docs/CONVENTIONS.md` and `docs/security/configuration.md` (both name the worker), `site/docs.html`
* **Customer links:** `docs/security/customer-view.md`, `docs/admin-guide/contracts-projects.md`, `docs/user-guide/projects.md`, `docs/quick-start.md`, `docs/editions.md`, `docs/feature-coverage.md`, `docs/ux/design-system.md`, `mkdocs.yml`, `site/index.html`, `site/docs.html`, `site/costpoint-alternative.html`, `site/smartsheet-alternative.html`, `site/self-host.html`, `site/ms-project-alternative.html`, `site/dcaa-timekeeping.html`
* **Commercial:** `docs/commercial/modules-and-pricing.md` (the whole Enterprise line and the plan tables), `docs/editions.md`
* **In the application:** no help article, glossary entry or tour names Jira, GitLab, Costpoint sync or customer links (checked). The integrations page text now says "and GitLab and Jira with the Enterprise add-on"
* **Internal notes:** `docs/operations/costpoint-sync.md`, `costpoint-integration.md`, `gitlab-sync.md`, `webhooks-integrations.md` and `module-extraction-audit.md` still describe the code as built in
* **Sample data:** none of the 20 tables holds sample rows (all were empty in the previous release's sample database), so a new database with and without the module differs only by the module's history table

## Verification

* Shipped repository, no `modules/` folder: `Workbench.Server.slnf` builds (0 errors; the MAUI desktop project cannot be built on this machine and references none of the moved types); the filtered groups pass, run one at a time (a single combined run is slow and the full suite is known to hang):
  Licensing 96, Modules 52, Navigation 54, Tenancy 42, Migration 37, Identity 275, Reports 20, Customization 51 (1 skipped), AutomationBuilder 47, Integrations 81, Approvals 38, Costpoint 7, Accessibility 10, GoldenPath 11, Dashboards 28 and Setup 46
  (**895 passed, 1 skipped**; the Jira, GitLab and customer view groups moved to the module and match nothing here; `Automations` matches no namespace, the builder's tests are `AutomationBuilder`). The host **starts with the folder absent** (published, run in Development).
* Module repository, module loaded from the built folder: its **89 tests** pass on InMemory and on SQLite (`WORKBENCH_TEST_DB=Sqlite`; one `JiraWebTests` run failed once in four SQLite runs and passed alone and twice more: a timing flake of a 15-second web test, not a defect). `has-pending-model-changes` is clean for the module's `EnterpriseDbContext` on both providers.
* `has-pending-model-changes`: clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL, and for the module's `EnterpriseDbContext` on both, checked with `dotnet ef` directly (`scripts/check-migrations.sh enterprise`), never with `scripts/new-migration.sh` (the baseline was
  generated with `dotnet ef migrations add` and rewritten by `idempotent-baseline.py`).
* **Upgrade from the previous release's host** (`14780e6`, the base of this change; published, run in Development so it seeds the sample company; 260 tables) against this release's host (published without a `modules/` folder, and with `modules/enterprise`, `License:EditionOverride=Business`, `Costpoint:Source=None`):
  * the *upgrade* (the previous release's database, once with the module and once without; both hosts start with no `fail:` lines): only `ReleaseEnterpriseTables` ran in core, and `EnterpriseBaseline` with the module (history table `__EFMigrationsHistory_enterprise`, one row);
    **every table's row count is identical** (the one change is the core history table, 52 to 53 rows) and the **DDL of the 20 tables is identical** (columns, nullability, defaults, keys, foreign keys, indexes) in both cases;
  * a *new* database made by this host **with** the module has the 20 tables identical to the previous release's (same comparison) and the same row counts in every other table; **without** it the database has 240 tables (none of the 20) and the host starts.
* Not run: PostgreSQL itself (the private repository's CI should run `WORKBENCH_TEST_DB=PostgreSql` and a PostgreSQL upgrade; in particular the foreign keys of the Jira, GitLab and customer link tables to `Project` and `Contract` and the guarded constraints), the full shipped suite (known to hang locally), the MAUI desktop build.

## Not done, and surprises

* **The standalone worker is gone.** `Workbench.Worker` existed only for the Costpoint sync (its `Program` registered the application, the source and that one hosted service), so the project was deleted and the scheduled pull moved into the module's `CostpointSyncBackgroundService`, which the web host runs for every tenant.
  A deployment that ran the worker as a separate process must now run the web host with the module and `Costpoint:Source` set; the worker's own `appsettings.json` is gone with it. The in-repo `docs/CONVENTIONS.md` and `docs/security/configuration.md` still name it.
* **A host without the module drops webhooks silently.** Jira and GitLab post to `/webhooks/jira/{linkId}` and `/webhooks/gitlab/{linkId}`; with no route mapped the host's catch-all page answers (not a 4xx), so the sender sees a success and nothing is received. Operators who remove the module should unlink the projects or delete the webhooks in Jira and GitLab.
* The module's scheduled Costpoint pull runs once at startup (like the worker did) and then every interval, in the web host; with `Costpoint:Source` set to `Csv` in Development it reads `samples/costpoint` from the working directory upward and fills the mirror on first start.
* PostgreSQL itself, the full shipped suite (known to hang locally), the MAUI desktop build; the worker and the desktop app do not load modules (the desktop client has no Jira, GitLab, Costpoint or customer link pages either way: its HTTP clients for them moved with the module and are not registered).
* Access reviews, retention, approval workflows, the connectors framework and the platform layer are **not** moved: see the table above and the decisions.

## What paid code is left in this repository after all extractions

"Paid" here means anything the catalog or `ModuleMap` ever sold, or a feature gate still asks a key for. Everything below that is not a gate is **free code in the shipped product**.

| Module | Where the sold code is now | Still in this repository |
| --- | --- | --- |
| `govcon`, `capture`, `materials`, `projectcontrols`, `workforce` (HR and payroll), `accounting` (cash, bank files, fixed assets, budgets, sales tax), `billing` (revenue recognition, live push), **`enterprise` (Jira, GitLab, Costpoint sync, customer links)** | `Workbench-Modules` | the seam, hooks, slots and the free parts listed per module in each extraction page |
| `enterprise` gate-only, in core: **second company** (`CompanyService.CreateAsync`; the companies records and pages, about 1,600 lines), **single sign-on provider save** (`SignInSettingsService`, 371 lines), **connectors** (create and update; the framework is 769 lines, `ConnectorService` 248, its tab and dialogs 416) and **Slack and Teams connections**, **custom approval workflows** (`ApprovalWorkflowService` 403, its pages 554) | here | about 3,800 lines whose **save methods** need a key; everything else about them is free |
| Free platform code that the old `enterprise` text listed | here | custom fields and layouts (about 8,800 lines), the automation builder (about 9,900), the integration plumbing (about 4,900), access reviews and retention (1,134); never gated, now labelled free |
| `supplier portal` (Feature gate on the Business edition, module `core`) | here | `Web/Supplier` (1,092) and `Application/Purchasing/SupplierPortal` (1,357): about 2,450 lines, gated by edition |
| Billing, accounting, workforce slices that the audit called class C (the receivables ledger and invoice engine, the general ledger, vendors, purchase orders, payables, currencies, indirect rates, resourcing and the leave pay calendar) | here | **free by decision in each extraction**: too many core features and other modules read their records. Nothing gates them |

So after this change **the only paid code that still ships in the public repository is the four gated save methods above and the supplier portal's edition gate** (about 6,000 lines of code around them, nearly all of it free); the sold *add-on* code is all in the private repository.

## Lessons for what is left

* **A leaf's webhook and anonymous pages need a host-root seam**, not an API path: `MapHostEndpoints` is the first piece of the product that a module maps outside `/api/v1`. The next module with a public page (a supplier portal move, a public form) uses it.
* **A module may not claim a service in a namespace the built-in table maps**: the customer link contracts had to change namespace. Check `ModuleMap.ServiceNamespaces` before moving a contract file.
* **Razor does not fail on an unresolved component tag.** `<Workbench.Ui.Integrations.Jira.JiraTaskLink />` in a core page compiled after the type moved (it is rendered as an unknown element). Grep the old component names after a move, not just the build.
* **A job that lived in a project of its own** (`Workbench.Worker`) has to be moved with the module or the project dies: check what else a "host" project does before deleting a leaf from it.
