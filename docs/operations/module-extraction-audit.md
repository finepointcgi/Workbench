# Module extraction audit: what can leave the public repository

Read-only audit that follows the `govcon` pilot ([module-extraction-govcon.md](module-extraction-govcon.md)), the government billing move
and the `billing` analysis ([module-extraction-billing.md](module-extraction-billing.md)). Nothing was changed in the product to write
it. It answers two questions for `accounting`, `capture`, `workforce`, `projectcontrols`, `materials`, `enterprise` and the pieces of
`billing` that are left:

1. What can actually be extracted into the private modules repository, and at what cost?
2. Does the paid/free line in `ModuleMap` match the boundaries in the code?

Every number below was measured from this tree (commit `6a35474`, the head of the stacked PRs #43 to #45). How is in
[Method](#method-and-its-limits), so the numbers can be redone.

## Short answer

| Module | Class | One line |
| --- | --- | --- |
| `capture` | **A** | Extractable now. 95 files, 18,760 lines. 7 core files touch it (4 are permission decorators, seeds and registration that move with it). One generic seam (catalog rows) is needed and is shared with every other module. 1 agent-session. |
| `materials` | **A** | Extractable now, but large (132 files, 24,562 lines). 9 core files touch it (6 are glue). It *depends on* accounting (36 files) and on the billing kernel (3 files), which stay available. 1 to 2 agent-sessions. |
| `projectcontrols` | **B** | 58 files, 8,900 lines, 18 core files (12 real). About 9 hooks: 4 UI slots, a dashboard widget, assistant tools, two project lifecycle hooks. 1 to 2 agent-sessions. |
| `workforce` | **B** for HR and payroll, **C** for resourcing | HR (53 files, 8,989 lines) has no real core reference and could go today. Payroll needs the leave engine's use of the pay calendar split out first (7 hooks). Resourcing (44 files, 9,046 lines) is the staffing data that the Plan, timesheets and schedule health read (19 core files). 2 agent-sessions (HR, payroll), plus 1 if the resourcing analytics are wanted. |
| `accounting` | **C** as a whole, with **A** slices | The general ledger, vendors, purchase orders, subcontracts and currencies are records that 64 core files and 93 files of the other paid modules read. Five areas are leaves: assets, cash and bank reconciliation, bank files, corporate budgets and sales tax (134 files, 22,878 lines, 40 percent of the module). 2 to 3 agent-sessions for the slice. |
| `enterprise` | **C** | It is a platform layer (custom fields, automations, integrations, companies) under 52 real core files and 20 files of the other paid modules. Only 5 features are gated today. Slices: Jira and GitLab connectors (about 7,100 lines of Application) and Costpoint sync (11 files). 2 agent-sessions for the slices. |
| `billing` (what is left) | **C** for the engine, **A** for two leaves | The ledger stays core (billing doc). `Revenue` (26 files, 3,281 lines, 2 real core files) and live accounting push (`AccountingLive`, 12 files, 2,370 lines, 4 real core files) are leaves. The invoice and payment services (67 files, 10,219 lines) have 52 files of the other paid modules and 21 real core files on them. 3 to 4 agent-sessions for everything. |

The most important finding is about the line, not the code: **only two modules are enforced today** (`govcon` and `enterprise`, and
`enterprise` only through five named features; see [The paid/free line](#the-paidfree-line-today)). Six modules ship free, so the
"paid" list for them is a plan, not behaviour. That matters, because a lot of the coupling below sits in modules nobody has to pay
for yet, and it is cheap to change the plan before the code.

## Method and its limits

* **Files per layer.** Production `.cs` and `.razor` files under `src/`, excluding `bin`, `obj` and the two migrations projects.
  A file belongs to a module when it is in the module's folder in `Domain`, `Contracts`, `Application` or `Ui` (the folders are the
  `ModuleMap.ServiceNamespaces` namespaces: for example accounting is `Accounting, Assets, BankFiles, Cash, Currencies, IndirectRates,
  Planning, Purchasing, SalesTax`), or, for `Api`, `ApiClient`, EF configurations and seeds, when its name starts with the module's name
  (for example `HttpCrmServices`, `CrmConfigurations`, `CrmSeed`). Tests belong to a module by their folder under `tests/Workbench.Tests`.
  Lines are physical lines, including comments and blank lines. These layer figures slightly undercount modules whose files sit in core
  folders (listed under [Code in core folders](#licensed-by-a-module-but-living-in-core-folders)).
* **Core files that reference the module (compile time).** A file outside the module that has a `using` or a fully qualified reference
  to `Workbench.<Domain|Contracts|Application|Ui>.<module area>`. There are no global usings, so this is exact for explicit references.
  I first tried matching type names and discarded it: words like `Result`, `Routing`, `Company`, `Decision` and `Position` gave 5 to 20
  false files per module. The first rough numbers in the extraction notes (capture 23, projectcontrols 29, materials 34, workforce 58,
  enterprise 120, accounting 204) came from that kind of match and are higher for that reason; they also counted other modules and tests.
* **Glue versus functional.** I split core references into *glue* (permission decorators, `DependencyInjection`, `IdentityRegistration`,
  `ModuleMap`, edition gates, `_Imports.razor`, seeds, EF configurations, `NotificationsHub` hosting lines) and *functional* (a core
  feature that calls the module). Glue moves with the module the way it did for `govcon`; functional references need a hook, a slot, a
  core copy, or a decision.
* **Not caught:** references through strings or data (an enum member named `Opportunity`, a custom-list entity name, a route),
  and reflection. I looked for the data-driven ones by hand and list them as *registry rows*.
* **Entities and tables.** Counted as `IEntityTypeConfiguration<T>` classes in the module's configuration files, then matched to the
  `CreateTable` names in the 46 core migrations of the SQLite set (439 tables in total). Foreign keys are from `HasOne<T>` and
  `HasMany<T>` in those configurations; a fully qualified `HasOne<Workbench.Domain...>` is easy to miss, so I also grepped by hand for the
  important tables.
* **Effort unit.** One agent-session is calibrated on what already happened: `govcon` (79 files, about 9,100 lines, about 10
  hooks, plus the seam changes) was one session, and government billing (about 4,000 lines plus the `IInvoiceFinancing` hook) was one.
  Test moves, the data path and the verification are included in a session; PostgreSQL runs are not (nobody has run them yet).

Totals for scale: production code in `src` is 367,860 lines in 2,315 files; `tests/Workbench.Tests` is 77,880 lines.

## The paid/free line today

`ModuleMap` says which module owns each API path, permission, menu entry and service interface. `ModuleCatalog`
(`src/Workbench.Domain/Licensing/Licensing.cs`) says which modules are *enforced*:

| Module | Enforced | What that means in code |
| --- | --- | --- |
| `core` | no | free |
| `billing`, `accounting`, `capture`, `workforce`, `projectcontrols`, `materials` | **no** | "every other module ships free until the price list is decided" (comment on `ModuleCatalog`). A license that lacks them loses nothing. |
| `govcon` | yes, `WholeModule: false` | one feature (`IncurredCost`) gates one service. The code is already out of the repository. |
| `enterprise` | yes, `WholeModule: false` | five features (second company, SSO, integrations, customer links, custom approval workflows) gate service methods; `EditionGates` (`Application/Licensing/EditionGates.cs`, lines 32 to 42) has ten `Gate` calls, one of them for the supplier portal, a sixth feature that `FeatureCatalog` assigns to *accounting*. The seventh feature, `IncurredCost`, is govcon's |

Two consequences for this audit:

* The gate is on **change methods of service interfaces** only (`ModuleMap.IsChange`: anything not starting with a read verb). Reads are
  never gated. A "disabled" module in the shipped repository is a module whose changes are refused; its data and pages for existing rows
  still open. That is the "code is shipped but disabled" outcome in the task.
* Today "enterprise" is, in practice, a handful of gated save buttons on top of code that mostly lives in core folders and is used by
  free features (see below). Extracting it moves a lot of generic infrastructure, not much of what is sold.

### Where the map and the code disagree

**Licensed by a module but living in core folders** (so a folder-based extraction misses them):

| Code | Folder | Licensed as | Size |
| --- | --- | --- | --- |
| Travel cards (`ICardProgramService`, `ICardChargeService`) | `Application/Travel`, `Ui/Setup/Cards` | accounting (`ServiceTypes`) | `CardProgramService`, `CardChargeService`, `CardFeeds`, `CardSupport` read `Accounting`, `Cash` and `Purchasing` types |
| Burdened cost (`IBurdenedCostService`) | `Contracts/Billing`, `Application/IndirectRates` | accounting (`ServiceTypes`) | the billing doc's "contracts namespace has to be split four ways" |
| Supplier portal (a gated accounting feature) | `Web/Supplier` (16 files, 1,092 lines) and `Application/Purchasing/SupplierPortal` (6 files, 1,356 lines) | accounting | 13 of the Web files use `Purchasing` types |
| Customer share links and view | `Contracts/Billing`, `Web/Customer` (5 files) | enterprise (`ServiceTypes`) | |
| Access reviews, retention, custom approval workflows | `Application/Security` (AccessReviewService 253, RetentionService 367 lines), `Application/Approvals/ApprovalWorkflowService.cs` (403), `Ui/Pages/Admin` tabs (514), `Ui/Setup/Approvals/ApprovalWorkflowPage.razor` (390) | enterprise | about 1,900 lines |
| SSO provider settings | `Application/Identity/SignInSettingsService` | enterprise (`Gate` on `SaveProviderAsync`) | identity code, stays core |
| Project narrative, schedule health | `Application/Assistant`, `Application/Scheduling` | projectcontrols (`ServiceTypes`) | |

**Sold as a paid module, but core code needs the records** (these are class C evidence):

| Records | Owned by (map) | Core code that needs them |
| --- | --- | --- |
| `Vendor`, `Subcontract` | accounting | The core ODC table `Odc` has foreign keys to both (`ProjectAccountingConfigurations.cs:253-254`); vendor ODC approvals, the Costpoint open-item importers and the supplier portal use them |
| Journal entries, chart of accounts | accounting | `AccountingExport` (core) reads them in 9 files; approval handlers; the Costpoint balance importers; reconciliation sources |
| `Currency`, exchange rates | accounting | multi-currency expense reports (`ExpenseReportService`, `ExpenseLineDialog`, `ExpenseReportPage`) |
| `ResourceAllocation`, `StaffingAssignment` | workforce | the Plan (`PlanPeopleSync`, `AssignmentGrid`, `PlanPeoplePanel`), timesheets (`TimesheetGrid`, `TimePage`, `TimesheetTimerService`), schedule health, project hub, project visibility, leave calendar: 19 core files |
| Payroll calendar and policy records | workforce | the leave engine (`LeaveEligibility`, `LeaveBalanceService`, `LeaveEngine`) and `Reauthentication` |
| `Invoice`, `Payment` | billing | 84 files (billing doc) |
| `Company` | enterprise | role scopes, project visibility, the main layout, project list, dashboards, writer reports: 12 core files and 11 files of other paid modules |
| Custom fields, list sources, document numbering, layouts | enterprise | sheet grid, filters, home widgets, built-in forms: 14 core files |

**Sold as a paid module, but the map gives it no menu rows:** `ModuleMap.NavPages` and `NavGroups` have no `projectcontrols` entries.
Earned value, ceilings, closeout and risks appear as tabs and cards inside core pages (`ProjectHubPage`, `ProjectSetupTab`,
`ContractPage`, `ProjectEvmPage`), so a host without the module would still show them unless they become slots.

**Registry rows** (core registries that list module entities by name, found by hand): custom lists, built-in forms, form layouts,
document numbering and home widgets (`Application/Customization/*`, `Ui/Customization/ListSources.cs`), the automation entity catalog
(`Application/Automations/AutomationEntities.cs`), integration events (`Domain/Integrations/IntegrationEvents.cs`,
`Application/Integrations/IntegrationOutbox.cs`), approval subjects and handlers (`ApprovalSubjects.cs`, `ApprovalHandlers.cs`),
`SecurityEventTypes`, `NavCatalog`, `NewCatalog`, `NavAccess`. The enterprise code lists `Crm` types in six and `Purchasing` types in seven
of these files. They are the same single problem for every module: a module has to be able to *add rows* to those catalogs.

## Measurements

### Per module, per layer (files / lines, production code)

| Layer | accounting | capture | workforce | projectcontrols | materials | enterprise | billing (left) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Domain | 18 / 3,135 | 8 / 897 | 11 / 1,177 | 4 / 503 | 13 / 1,957 | 11 / 1,486 | 4 / 769 |
| Contracts | 15 / 4,529 | 9 / 1,574 | 12 / 2,628 | 4 / 771 | 4 / 1,642 | 15 / 2,464 | 6 / 1,340 |
| Application | 94 / 25,318 | 26 / 7,662 | 45 / 14,316 | 17 / 4,603 | 37 / 10,911 | 75 / 21,021 | 36 / 8,203 |
| Api | 12 / 823 | 2 / 191 | 4 / 391 | 4 / 160 | 4 / 375 | 9 / 501 | 4 / 250 |
| ApiClient | 12 / 1,479 | 2 / 385 | 4 / 846 | 4 / 251 | 4 / 733 | 9 / 863 | 4 / 408 |
| Ui | 155 / 18,615 | 43 / 7,079 | 59 / 7,512 | 20 / 2,162 | 62 / 7,203 | 58 / 7,048 | 45 / 4,377 |
| EF configurations | 13 / 1,935 | 2 / 456 | 3 / 646 | 3 / 257 | 4 / 1,054 | 8 / 928 | 4 / 350 |
| Seeds | 11 / 1,399 | 3 / 516 | 3 / 483 | 2 / 193 | 4 / 687 | 3 / 326 | 2 / 173 |
| **Production total** | **330 / 57,233** | **95 / 18,760** | **141 / 27,999** | **58 / 8,900** | **132 / 24,562** | **188 / 34,637** | **105 / 15,870** |
| Tests (module's folders) | 51 / 8,430 | 19 / 3,090 | 33 / 6,775 | 10 / 1,599 | 17 / 2,285 | 34 / 6,825 | 19 / 3,414 |
| Entities (EF configurations) | 104 | 28 | 47 | 15 | 57 | 54 | 19 |
| Tables created in core migrations | 92 in 6 migrations | 28 in 4 | 47 in 7 | 14 in 2 | 47 in 2 | 51 in 6 | 19 in 4 |

Notes: the `billing` column is what remains after government billing moved (it is `Billing` 67 files / 10,219 lines, `Revenue`
26 / 3,281 and `AccountingLive` 12 / 2,370). The enterprise seeds do not include `CommercialDemoSeed`, which is one multi-module demo
seed. The core migration set is 46 migrations per provider (SQLite and PostgreSQL), so a table-release recipe is applied twice.

### Per area (the useful unit for deciding what can go)

"Core: functional" and "glue" count core files outside the module that reference the area; "Other paid" counts files of the other
paid modules. Files and lines are all production layers (Domain, Contracts, Application, Ui, Api, ApiClient, EF configurations, seeds).

| Module | Area | Files | Lines | Core: functional | Core: glue | Other paid modules | Test files | Other paid modules that use it |
|---|---|---:|---:|---:|---:|---:|---:|---|
| accounting | Accounting (the general ledger) | 48 | 7,311 | 22 | 7 | 66 | 52 | billing: 17, workforce: 10, capture: 5, materials: 24, enterprise: 8, projectcontrols: 2 |
| accounting | Assets | 38 | 5,907 | 1 | 4 | 5 | 2 | enterprise: 2, workforce: 2, projectcontrols: 1 |
| accounting | BankFiles | 10 | 2,117 | 0 | 2 | 0 | 0 |  |
| accounting | Cash | 35 | 5,792 | 1 | 5 | 3 | 3 | enterprise: 2, capture: 1 |
| accounting | Currencies | 15 | 2,417 | 4 | 2 | 4 | 1 | billing: 4 |
| accounting | IndirectRates | 24 | 3,187 | 5 | 3 | 6 | 5 | billing: 1, projectcontrols: 2, workforce: 1, capture: 2 |
| accounting | Planning (corporate budgets) | 36 | 5,950 | 0 | 2 | 1 | 0 | materials: 1 |
| accounting | Purchasing (vendors, POs, AP, RFQ, EDI, portal) | 109 | 21,440 | 22 | 14 | 40 | 29 | materials: 28, enterprise: 8, projectcontrols: 4 |
| accounting | SalesTax | 15 | 3,112 | 0 | 1 | 1 | 0 | billing: 1 |
| capture | Crm | 69 | 12,857 | 3 | 3 | 7 | 4 | enterprise: 6, workforce: 1 |
| capture | Pricing | 26 | 5,903 | 0 | 1 | 0 | 1 |  |
| workforce | Hr | 53 | 8,989 | 0 | 2 | 1 | 3 | enterprise: 1 |
| workforce | Payroll | 44 | 9,964 | 7 | 7 | 1 | 11 | accounting: 1 |
| workforce | Resourcing | 44 | 9,046 | 19 | 5 | 3 | 6 | enterprise: 3 |
| projectcontrols | Ceilings | 22 | 4,029 | 10 | 4 | 0 | 5 |  |
| projectcontrols | Closeout | 2 | 191 | 3 | 2 | 0 | 3 |  |
| projectcontrols | Evm | 23 | 3,965 | 5 | 3 | 0 | 3 |  |
| projectcontrols | Risks (with schedule health) | 11 | 715 | 1 | 2 | 0 | 0 |  |
| materials | Materials (with shop floor and sales) | 98 | 18,210 | 3 | 4 | 9 | 4 | accounting: 4, enterprise: 5 |
| materials | Quality | 34 | 6,352 | 0 | 2 | 1 | 0 | enterprise: 1 |
| enterprise | Automations | 21 | 6,817 | 8 | 5 | 0 | 10 |  |
| enterprise | Companies | 17 | 2,023 | 12 | 4 | 11 | 12 | accounting: 8, materials: 1, billing: 2 |
| enterprise | Customization (with layouts) | 62 | 9,702 | 14 | 4 | 0 | 7 |  |
| enterprise | Integrations (with Jira, GitLab, connectors) | 77 | 15,014 | 17 | 7 | 9 | 13 | accounting: 4, capture: 1, workforce: 1, billing: 3 |
| enterprise | Costpoint (sync) | 11 | 1,081 | 2 | 5 | 0 | 1 |  |
| billing | Billing (invoices, payments, commercial billing, share link) | 67 | 10,219 | 21 | 10 | 52 | 32 | capture: 6, accounting: 24, projectcontrols: 10, enterprise: 9, materials: 3 |
| billing | Revenue | 26 | 3,281 | 2 | 2 | 0 | 2 |  |
| billing | AccountingLive | 12 | 2,370 | 4 | 1 | 0 | 0 |  |

### Per module, totals of who touches it

| Module | Core: total | glue | functional | Files of other paid modules | Test files outside its folders | Depends on (files of this module that use) |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| capture | 7 | 4 | 3 | 7 (enterprise 6, workforce 1) | 4 | accounting 6, billing 6, enterprise 1 |
| materials | 9 | 6 | 3 | 9 (accounting 4, enterprise 5) | 1 | accounting 36, billing 3, enterprise 1 |
| projectcontrols | 18 | 6 | 12 | 0 | 6 | accounting 6, billing 10 |
| workforce | 39 | 13 | 26 | 5 | 13 | accounting 11, capture 1, enterprise 1 |
| enterprise | 73 | 21 | 52 | 20 | 35 | accounting 14, capture 6, workforce 4, materials 5, billing 9 |
| accounting | 64 | 25 | 39 | 93 | 49 | billing 24, enterprise 12, materials 4, workforce 1 |
| billing (left) | 39 | 12 | 27 | 52 | 31 | accounting 20, enterprise 5 |

Core files that reference the module, by project, for the functional ones: capture (Application 3); materials (Ui 3);
projectcontrols (Application 6, Ui 6); workforce (Application 15, Ui 9, Api and ApiClient 1 each); enterprise (Application 21,
Ui 22, Web 3, Contracts 3, Api, Infrastructure and Worker 1 each); accounting (Application 26, Ui 10, Web 3);
billing (Application 17, Ui 8, Contracts and Web 1 each).

### Dependency graph between the paid modules

Direction: "A depends on B" means files of A use types of B.

```text
materials --> accounting (36 files: GL posting, vendors, purchase orders)  --> billing kernel (3: ShipmentInvoicer)
capture   --> accounting (6), billing (6: burdened cost / rate basis)
projectcontrols --> billing (10: BillingCostSource, invoice decorators), accounting (6)
workforce --> accounting (11: the posting engine, journal lookups)
billing   <--> accounting     (billing uses the GL: 20 files; accounting uses invoices: 24 files)
enterprise --> accounting (14), billing (9), capture (6), workforce (4), materials (5)  (catalog rows, companies)
accounting --> enterprise (12: companies on assets and sales tax, outbox events on AP)
```

Two cycles: **accounting and billing** (cash forecast, currency revaluation, sales tax and indirect-rate reports read invoices; the
revenue engine and billing ledger post to the GL) and **accounting and enterprise** (companies on assets; the outbox in AP). A
cycle between two *plugins* is not loadable (`DependsOn` is acyclic), so the pairs must be broken or kept together. Breaking the first
is exactly "the receivables ledger stays core" from the billing doc: accounting then depends only on core invoices, and billing depends
on accounting.

## Class A: extractable now

### `capture` (A, 1 session)

* **Size:** 95 files and 18,760 lines (Crm 69 files, Pricing 26), 28 entities in 28 tables (4 migrations), 19 test files.
* **Core touch points (7 files):** `CrmPermissionDecorators`, `PricingPermissionDecorators`, `NotificationsHub` (hosts the daily SAM.gov
  search) and the seed registration: glue that moves. Functional: `WonHandoffTodoProvider` (a my-to-do provider; moves and
  registers itself), `CaptureExecImporters` and `CommercialCrmImporters` (importers; they move and register with the importer list).
* **Other paid modules:** `enterprise` references it in 6 files (custom lists, built-in forms, home widgets, layouts, the automation
  entities, list sources) and `workforce` in one (`PipelineDemandPanel`, a pipeline demand panel inside resourcing). Both go through the
  registry seam below; the panel becomes a slot.
* **Hooks needed:** 1 generic catalog-rows seam (shared), 1 hosted-service registration (already possible in `ConfigureServices`),
  1 slot for the resourcing panel, nothing for data. About 3 hooks.
* **Data:** no core table references it (no `Has*<T>` from another module's entity, none from core). Tables are created in 4 core
  migrations that are edited to stop creating them, as the govcon recipe did.
* **Cross-module:** depends on accounting (6, subcontracts and indirect-rate basis for pricing) and billing contracts (6, rate basis),
  both of which stay available.

### `materials` (A, 1 to 2 sessions)

* **Size:** 132 files and 24,562 lines (Materials with shop floor and sales 98, Quality 34), 57 entities, 47 tables in 2 core migrations
  17 test files.
* **Core touch points (9 files):** four permission decorators, `NotificationsHub` (hosts the engineering change effective date job),
  `_Imports.razor`: glue. Functional: `Ui/Sales/SalesFormat.cs`, `Ui/Mobile/ReceivePage.razor`, `Ui/Mobile/CountPage.razor`. The mobile
  pages are the only core UI of the module and can move with it, with their route.
* **Other paid modules:** `enterprise` registry rows (5), and `accounting` (4 files: `Purchasing/Edi/EdiService`, `Sourcing/RfqService`,
  `SupplierPortalService` and `SourcingConfigurations`) reference materials types. Those four need the part to be a
  small core interface, or the sourcing slice to go with materials.
* **It depends on:** accounting (36 files: it posts receipts and issues to the GL and creates POs and bills) and billing (3:
  `ShipmentInvoicer`, `ShippingDocuments`). With accounting still in the public repository that is free; if accounting later becomes a
  plugin, `DependsOn: accounting` and the billing doc's *invoice-created* hook cover it.
* **Hooks needed:** about 5 (two mobile pages, the registry seam, the item reader for RFQ and EDI, the invoice-created hook).
* **Data:** 57 entities; 26 configured relationships point from materials entities at accounting entities (vendors, purchase orders, accounts), and 1 points back from `SourcingConfigurations`. The module context maps those key-only, as `GovConDbContext` does for `PurchaseOrder` and `Subcontract`. No core table has a
  foreign key into materials.
* **Why 1 to 2:** it is 2.7 times the pilot; the shop floor and quality halves are separable if one session is not enough.

## Class B: after splitting contracts or hooks

### `projectcontrols` (B, 1 to 2 sessions)

* **Size:** 58 files and 8,900 lines (Ceilings 22 files, Evm 23, Risks with schedule health 11, Closeout 2), 15 entities in 14 tables
  (2 core migrations), 10 test files, 6 more outside.
* **Real core references (12):** `ProjectHubPage`, `ProjectSetupTab`, `ContractPage`, `WidgetView`, `ProjectEvmPage`,
  `ProjectFinancialsPage` (UI hosts), `ProjectNarrativeService`, `TimesheetFlagService`, `AssistantToolbox` (assistant), `ProjectTodoProviders`,
  `ProjectHubService`, `ProjectStartService` (ceiling-first project start).
* **Hooks (about 9):** 3 slots (hub cards, project setup tab, contract page; the slot mechanism exists), 1 dashboard widget
  contribution (new), 1 assistant tool and flag contribution (new, `AssistantToolbox` is 757 lines of hard-coded services),
  1 hub summary contribution, 1 project-start hook, 1 to-do provider move, 1 `ProjectFinancialsPage` ceiling card slot. The EVM page and
  nav rows move.
* **It depends on** the billing kernel (10 files: `BillingCostSource`, `IBillingLimits`, the `IInvoiceService` decorators that enforce
  ceilings) and accounting (6). All stay.
* **Data:** no core table has a foreign key into its tables; one of its entities points at a billing table.
* **The line:** the map has no nav row for it (see above); a licensing decision plus the slots fixes that.

### `workforce`: Hr and Payroll (B, 1 to 2 sessions)

* **Hr** (53 files, 8,989 lines): 0 real core references, 1 other paid module file. It is a leaf and could go on its own. The core
  `IHrReportService` and `IHrAccessService` are already mapped core (`ServiceTypes`).
* **Payroll** (44 files, 9,964 lines): 7 real core references: the leave engine (`LeaveEligibility`, `LeaveBalanceService`, `LeaveEngine`
  use the pay-group and holiday records), `LeavePoliciesAdmin`, `FinanceTodoProviders`, `MigrationEndpoints` and `HttpMigrationServices`
  (the Costpoint payroll migration), `Reauthentication`. The accounting posting engine reads payroll labor adjustments
  (`PostingEngine.LaborAdjustments.cs`).
* **Hooks (about 7):** move the pay calendar and holiday records the leave engine needs into a small core kernel (the same move as
  "keep the records"), a to-do provider move, the migration payroll step as a contribution, the re-authentication prompt list, and the
  posting engine's labor adjustment reader as an interface.
* **Data:** 47 entities in 7 core migrations (`Payroll` and `Hr` own most). Core leave tables keep their foreign keys only if the
  kernel records stay.

### `accounting`: the leaf and near-leaf areas (A and B inside a C module)

* A: **Assets** (38 files, 5,907 lines; 1 real core file), **Cash** (35 / 5,792; 1), **BankFiles** (10 / 2,117; 0), **Planning** (36 / 5,950; 0),
  **SalesTax** (15 / 3,112; 0).
* B: **Currencies** (15 / 2,417; 4 real core files, the multi-currency expense report) and **IndirectRates** (24 / 3,187; 5, reports
  and importers, and burdened cost used by billing and capture).

## Class C: tangled with core

For each, the table lists *which slice could be the paid plugin*, what the line change would be, and how much stays public. "Public"
means in this repository; a slice that stays here is shipped to every user.

### `accounting`

* **Why C:** `Accounting` (the ledger) is read by 22 real core files and 66 files of the other paid modules; `Purchasing` (vendors,
  subcontracts, purchase orders, AP) by 22 real core files (the ODC foreign keys, importers, approvals, supplier portal in the web project, travel
  cards, reports) and 40 of other modules (28 from materials). 104 entities and 92 tables in 6 migrations.
* **Posting is already inverted, which helps:** automatic posting is a decorator around the timesheet and ODC approval services
  (`PostingDecorators.cs`), not a call from core. The ledger *records* are what core and the other modules read.
* **The slice (A, 134 files, 22,878 lines, 40 percent):** Assets, Cash and bank reconciliation, BankFiles, corporate budgets (Planning)
  and sales tax. Currencies and IndirectRates (9 real core references between them) can follow once those are hooked, in one more session. **Stays public: 196 files, 34,355 lines (60 percent):** the ledger, vendors and POs and AP, and (unless
  hooked) currencies and indirect rates. Purchasing has 4 further leaves worth about 4,600 lines (EDI 1,208, sourcing 951, the supplier
  portal 1,356 in Application plus 1,092 in Web); they were measured only by folder size, not by references.
* **Map and price change:** accounting is sold as "the general ledger, period close, payables, purchasing, payment runs, cash and bank
  reconciliation, fixed assets, sales tax, multi-currency, corporate budgets and indirect rates". The slice makes the paid module "cash and bank
  reconciliation, fixed assets, sales tax, corporate budgets and bank files". **The general ledger, AP, purchasing and
  multi-currency become free,** or are priced by gating changes on the (public) services, which is option G below.

### `workforce`: Resourcing

* **Why C:** 19 real core references. `ResourceAllocation` and `StaffingAssignment` drive the Plan's "assign people" experience,
  timesheet pages, schedule health, project visibility and the leave calendar; the docs treat "assign people" as a core PM feature
  (`docs/assign-people.md`, `simplicity-pm-staffing.md`).
* **The slice:** the capacity, utilization, heat map, open demand, skills and pipeline analytics (Application: `Capacity`,
  `AllocationForecastService`, `ResourcePlanningService`, `StaffingSuggestionService`, `SkillService`; Ui: `HeatMapTab`, `UtilizationTab`,
  `DemandTab`, `SkillsPanel`, `PipelineDemandPanel`). Resourcing is Application 13 files (4,670 lines) plus Ui 20 files (2,592 lines); I did not
  measure the slice separately.
* **Stays public:** the allocation and assignment records and their services (at least `ResourceAllocationService`,
  `StaffingAssignmentService`, `ProjectStaffingService`, `ResourcePlanService`).
* **Map and price change:** `projects.resourcing.*` and the `resourcing` path become core; the paid piece is "capacity, utilization and
  skills across projects". That matches the catalog's own wording ("cross-project capacity, utilization and skills").

### `enterprise`

* **Why C:** the module is four platform layers plus a few features.
  Customization (62 files, 9,702 lines) is used by 14 real core files (sheet grid filters, sort, format rules, home widgets, form layouts);
  Automations (21 / 6,817) by 8 (sheet rules, bulk edit); Integrations (77 / 15,014) by 17 (the outbox on projects and
  timesheets, API keys, forms, the assistant tool host, edition services); Companies (17 / 2,023) by 12 real core files and 11 files of
  other modules (role scopes, project visibility, ledgers). Customer share links, the supplier portal, access reviews, retention and
  approval workflows live in core folders (see above).
* **What is actually sold:** ten gated service-method sets for five features. The ModuleMap description (layouts, custom fields, automation builder, API keys) is
  *not* enforced; `WholeModule` is false.
* **Slices that are leaves:** Jira and GitLab connectors (`Application/Integrations/Jira` 7 files, 2,470 lines; `GitLab` 10 files, 4,626;
  non-module users: `ItemDetailsDrawer`, `ProjectSetupTab`, `EditionGates`), Costpoint sync (11 files, 1,081 lines; its 5 glue files
  and `CostpointSyncWorker` in `Workbench.Worker`, which does not load modules), the integrations admin UI (26 files, 2,823 lines). The
  customer share link service (about 150 lines in `Web/Customer`, with its DTOs in `Contracts/Billing`) is tiny and could move
  with a core view interface.
* **Stays public:** Customization, Automations, the outbox and the Companies records, and everything SSO, API keys, retention and
  approvals (about two thirds of the 34,637 lines, but what a buyer sees as "Enterprise" is mostly gates on these).
* **Map and price change:** keep enterprise mostly as a *feature gate* (as today), and make the gate honest: the paid features
  are "connect to Jira and GitLab, Costpoint sync, customer links". Moving the gate is a pricing decision.

### `billing` (what is left)

* **The ledger stays** (billing doc). What remains is the invoice and payment *services*, commercial billing, revenue and live push.
* **Leaves (A):** `Revenue` (26 files, 3,281 lines; 2 real core files, 0 other modules: `ProjectTodoProviders`, `ProjectHubPage`)
  and `AccountingLive` (12 / 2,370; 4 real core files, all in `Ui/AccountingExport`: slots). Both could go in separate sessions.
* **The engine (C):** `Billing` is 67 files / 10,219 lines with 21 real core files and 52 files of the other paid modules (accounting 24,
  projectcontrols 10, enterprise 9, capture 6, materials 3); phase 3b in the billing doc (kernel first, then commercial billing, then the
  invoice and payment services) stands: about 3 sessions.
* **Map and price change:** billing is "invoices, ready to bill, payments, AR aging, unbilled accrual, milestones, retainers, revenue
  recognition, live push". After the extraction a core-only install can read and import invoices but not create them (billing doc). Slicing
  would make *invoice creation and approval* paid and *the receivables ledger and reports* free.

## Options per module, and what stays public

* **G, gate only (the license gate stays on the service interfaces):** nothing moves. The owner's goal of not shipping paid code is not
  met: 100 percent of the sold code is in the public repository, with its changes refused without a license. For modules not yet enforced it is not
  even gated.
* **X, extract the whole module:** only possible where the class is A or B. What stays public is the kernel records, the hooks and
  glue that were not already moving.
* **S, extract an add-on slice (class C):** the slice moves; the rest becomes a free feature; the price line moves with it.

| Module | G: public after | X: public after | S: slice that moves; public after |
| --- | --- | --- | --- |
| capture | all (18,760 lines) | about 0 lines of module code; 7 core files lose references | n/a |
| materials | all (24,562) | about 0; 9 core files change (3 real) | n/a |
| projectcontrols | all (8,900) | about 0; 18 core files change (12 real) | n/a |
| workforce | all (27,999) | n/a for resourcing | HR and payroll move (18,953 lines, 68 percent); resourcing records and plan staffing stay (about 9,046 lines, 32 percent, of which the analytics could move, not measured) |
| accounting | all (57,233) | n/a | 22,878 lines move (40 percent), 34,355 stay (60 percent): ledger, vendors/POs/AP, currencies, indirect rates |
| enterprise | all (34,637) | n/a | Jira, GitLab, Costpoint, integrations admin and customer links: about 11,100 lines (Jira 2,470 and GitLab 4,626 in Application, Costpoint 1,081, the integrations admin UI 2,823, customer links 149); about two thirds stays |
| billing | all (15,870 + ledger) | n/a for the engine | Revenue and live push move (5,651 lines, 36 percent); invoice and payment services last, after kernel work |

### The tradeoff in one paragraph

The owner's goal (do not ship paid code) is fully met for `capture`, `materials` and `projectcontrols`, mostly met for `workforce` (HR and
payroll), and **only partly for the three modules that are the finance and platform backbone**: accounting, enterprise and billing. For those,
"sold" and "records core needs" are the same code. Either the owner accepts shipping the ledger-like records and engines (option G) or
accepts a smaller paid module (option S) and a free ledger, AP, receivables and platform layer. There is no option in which the whole
current accounting, billing or enterprise module is both sold and not shipped, short of moving the ledger records into a core
that is no longer free of them (which would make core depend on a paid module).

## Data: existing installs

The recipe from the pilot ([module-extraction-govcon.md](module-extraction-govcon.md), *Data*) holds for every module here and has been
proved on SQLite and by script comparison on PostgreSQL (not run):

1. A core "release" migration with empty `Up` and `Down`, so core stops mapping the tables and no table is dropped.
2. Edit the old core migrations that created the tables so a new install does not (their history rows are untouched).
3. An idempotent baseline migration in the module (`CREATE ... IF NOT EXISTS`), its own history table.

**Cost and risk by module:** the number of core migrations to edit (per provider, so ×2): capture 4, materials 2, projectcontrols 2,
workforce 7, enterprise 6, accounting 6, billing 4. Larger edits have a larger chance of changing the schema a new install gets, which
is why every module needs the "identical DDL" comparison against the previous release's scripts that was done for `govcon`.

**Risks that apply to every extraction:**

* **An existing database with the module absent keeps the tables and rows but nothing creates or edits them.** For a module whose tables
  core still *reads* (accounting vendors for ODCs, resourcing for the Plan) that is a data-integrity edge: a free tier that can read
  but never update. This is why the ledger-like tables belong in core (class C).
* **Cross-module foreign keys.** Configured relationships between paid modules' entities: materials to accounting 26, billing to accounting 4,
  workforce to accounting 3, enterprise to accounting 3 (and 1 back), accounting to materials 1, billing to projectcontrols, accounting and materials 1 each. A module's context maps these
  key-only (excluded from migrations), as the pilot did. The order of releases then matters: a module's baseline can only reference tables
  that exist, so a module that depends on another has to be applied after it (`DependsOn` already orders loading).
* **No downgrade.** Same as before: an older core against a database the module changed is not supported.
* **Edition keys.** A host without a module that a license lists loads nothing and says so; WB1 keys list every module, so existing paid
  customers are unaffected, but a customer on a *cloud* plan needs the operator's modules folder to include every module they use.
* **The Worker and the Desktop app do not load modules.** Background jobs that moved out would not run in the worker; the desktop app
  would lose the pages. Today the web host starts the module jobs (`NotificationsHub` registers four: ceiling alerts, SAM.gov search,
  recurring journals, engineering changes). Nothing of that needs to change for capture, materials and projectcontrols as long as
  those jobs run in the web host.
* **Help articles** (embedded resources), public docs, `site/` and the README still describe the modules; moving them needs a
  help-article extension point and an owner decision (see the billing doc).

## Shared seam work (build once, before the second module)

| Seam | Needed by | Why |
| --- | --- | --- |
| **Catalog rows** (custom lists, built-in forms, document numbers, home widgets, automation entities, integration events, approval subjects, security event types, `NewCatalog`) | every module | Enterprise lists other modules' entities by name; a moved module has to add its rows, and enterprise's registries have to read contributions. About 14 files. 1 session. |
| **Dashboard widget and assistant tool contributions** | projectcontrols, workforce, accounting | `WidgetView`, `AssistantToolbox` (757 lines), `TimesheetFlagService`, `ProjectNarrativeService` call modules directly |
| **Data report contribution** (`ReportSources`, `StandardReports.*`) | accounting, billing | `StandardReportPage` exists for page-only entries; finance reports call module services directly (6 core files for accounting) |
| **Approval subject contribution** | accounting, billing | `ApprovalHandlers`, `ApprovalSubjects` list PO, bill, journal and invoice handlers by type |
| **Reconciliation and import source contribution** | accounting, billing, workforce | `ReconciliationSources`, `MigrationService`, the Costpoint importers |
| **Item and vendor readers** (small core interfaces) | materials, accounting | sourcing and EDI read parts; ODCs read vendors |
| **Leave kernel** (pay calendar and holidays as core records) | workforce | the leave engine needs them whether or not payroll is installed |

Everything else (slots, seeds, permissions, nav, API keys, decorators, hosted services, entities) is already in the seam.

## Recommended order

| # | Step | Class | Sessions | Why in this order |
| --- | --- | --- | ---: | --- |
| 0 | **Decide the paid line** (below) | | 0 | Decide before moving code: five modules are not enforced, so the line can still move for free. |
| 1 | Catalog-rows seam, plus the widget, assistant tool, report, approval subject and source contributions | seam | 1 to 2 | Needed by every later step; small. |
| 2 | `capture` | A | 1 | The cleanest module (7 core files, 3 real). Proves the catalog-rows seam. |
| 3 | `projectcontrols` | B | 1 to 2 | Exercises slots, widget and assistant seams; no other paid module uses it. |
| 4 | `materials` | A | 1 to 2 | Large but low in core references; depends on accounting, which stays. |
| 5 | `workforce`: Hr, then Payroll (leave kernel first) | B | 2 | Hr is a leaf; payroll needs the kernel. Resourcing analytics only if the owner wants it (C). |
| 6 | `billing` leaves: Revenue, then AccountingLive, then phase 3b of the billing doc | A, C | 3 to 4 | The billing doc's plan. |
| 7 | `accounting` slice: BankFiles, Planning, SalesTax, Cash, Assets, then Currencies and IndirectRates | A, B | 2 to 3 | After billing, since cash, currency and sales tax read invoices. |
| 8 | `enterprise` slices: Costpoint, Jira and GitLab, customer links | A | 2 | Last: it reads every other module. |
| | **Total** | | **13 to 18** | Plus a PostgreSQL run per step in the private repository's CI. |

Steps 2 and 3 can run as parallel sessions: they share only the seam from step 1 and touch different core files (capture touches
importers, the to-do provider and the hub; projectcontrols touches project pages, the assistant and the project services).

**Risks to the plan:** (a) the catalog-rows seam is the one piece of real design; if it is wrong, every module repeats the error;
(b) `AssistantToolbox`, the report services and `NotificationsHub` are central files that several steps edit, so merge them in order;
(c) module sizes are not uniform (accounting is 57k lines); (d) a cloud operator has to build, sign and deploy several assemblies
(packaging and signing are phase 4 and not done); (e) tests: the module test assemblies need the same `InternalsVisibleTo` as the
govcon one and each module's tests (capture 19 files, materials 17, projectcontrols 10, workforce 33, accounting 51, enterprise 34, billing 19) move
with it, but 4 to 49 test files outside the module's folders also reference its types and must be split.

## Decisions for the owner

1. **Which modules are paid at all?** Today only `govcon` and `enterprise` (five features) are enforced; six modules ship free by design.
   If the plan is to charge for them, say which, because every step above only makes sense for a module someone pays for.
2. **Is "not shipping paid code" a hard rule for the backbone modules?** If yes, accept option S for accounting, enterprise and
   billing: a free ledger, AP, receivables and platform layer, and a smaller paid module. If no, accept option G for them (the code is
   public and gated) and extract the rest.
3. **Accounting line.** Free general ledger, vendors, purchase orders and multi-currency, with paid "cash and bank reconciliation,
   fixed assets, sales tax, budgets, bank files" (the slice), or all of accounting stays sold and gated, public.
4. **Workforce line.** Is "assign people" a core feature (the docs say yes) and "capacity, utilization and skills" the paid part? That makes resourcing records
   core and the analytics the slice. Or is HR and payroll the whole paid product?
5. **Enterprise line.** Which of the five gated features are really "enterprise"? SSO and custom approval workflows live in core folders
   and cannot move; moving the gate to connectors (Jira, GitLab, Costpoint, customer links) changes what the paid
   tier means.
6. **Project controls menu.** The map gives it no nav rows; do ceilings, earned value and risks disappear from the project hub without the module,
   or do they stay as free views (which means nothing to extract but the engines)?
7. **Order and parallelism.** Approve steps 1 to 3 now, and whether steps 2 and 3 may run as two parallel sessions.
8. **Public documentation, `site/` and help articles** for modules that leave (the billing doc lists the pages): keep describing them,
   or move them to the private repository with a help-article extension point.
9. **Worker and desktop.** Accept that modules do not load there, or schedule the work to make them load (needed only if a moved module owns
   a background job; none of steps 2 to 4 do).

## Reproducing the numbers

The measurements were made with a throwaway script (not committed) that (1) maps every `.cs` and `.razor` under `src` and `tests` to a
module by folder or file name as described under *Method*, (2) counts lines per module, layer and area, (3) for each module and area
searches every other file for `Workbench.(Domain|Contracts|Application|Ui).<area>` after removing namespace declarations, and
(4) splits the hits into glue and functional by file name. Redoing it after a change is a few seconds; the totals above change only by
the files that move.
