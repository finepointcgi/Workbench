# Extracting a module: materials (inventory, manufacturing, quality, sales and shipping)

Phase 5 of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md); the earlier moves are
[govcon](module-extraction-govcon.md), [billing](module-extraction-billing.md) and [capture](module-extraction-capture.md); the order and class
of each module come from the [audit](module-extraction-audit.md), which called `materials` class A: extractable now, large). This page is the inventory,
every place core touched the module and how it was resolved, the data path for existing installs, and what the next modules should expect.

The code now lives in the **private repository** (`Workbench-Modules`, a local git repository with no remote yet) as
`src/Workbench.Module.Materials*`, builds into `dist/modules/materials/` (copy it to a host's `modules/materials/`), and loads like any plugin.
This repository ships without it.

## What moved and what did not

**Moved to the module:**

| Feature | Core layers that held it |
| --- | --- |
| Parts and part forecasts, warehouses and bins, inventory balances and transactions (receive, issue, transfer, adjust, count, revalue), lots and serials, government-furnished material | Domain `Materials/`, Contracts `Materials/`, Application `Materials/` (`PartService`, `InventoryService`, `StockEngine`, `MaterialsLedger`), endpoints (`materials`), HTTP clients, EF configurations, UI pages `Materials/` |
| Bills of materials, routings, work centers, work orders (components, operations, labor, genealogy), MRP runs and planned orders, material reports | same layers (`ManufacturingService`, `MrpEngine`/`MrpService`, `MaterialReportService`) |
| The shop floor: kits and pick lists, backflush, landed cost, physical inventory and count tags, finite capacity scheduling | Application `Materials/` (`KittingService`, `BackflushService`, `LandedCostService`, `PhysicalInventoryService`, `CapacitySchedulingService`), endpoints (`materials`), UI `Materials/ShopFloor/` |
| Quality: inspection plans and the inspection queue, nonconformances (NCRs) and the material review board, corrective actions, supplier quality, vendor debit memos; engineering change control (ECOs) and its hourly effective-date job | Domain `Quality/`, Contracts `Quality/`, Application `Quality/`, endpoints (`quality`, `engineering-changes`), UI `Quality/` |
| Sales orders, pick/pack/ship, DD250, WAWF receiving reports, shipment invoicing (through the billing kernel) | Application `Materials/Sales/`, endpoints (`sales-orders`, `shipping`), UI `Sales/` |
| The phone pages: receive into inventory (`/m/receive`) and physical inventory count (`/m/count`) | UI `Mobile/ReceivePage`, `CountPage` |
| Permissions `materials.view`, `materials.manage`, `materials.costing`, `quality.dispositions.apply`, `sales.orders.view`, `sales.orders.manage`, `sales.credit_hold`; the permission decorators of every service; the 24 menu pages; the "+ New › Engineering change" action; the `materials` API key area | `Permission`/`PermissionCatalog`, `NavCatalog`, `NewCatalog`, `ApiKeyScopes`, `IdentityRegistration` |
| Rows core listed by name: the part form and the part custom-field record type, the sales order list-only record type, the `parts` and `sales-orders` list pages, eight numbered documents (SO, PK, SH, NCR, CAR, ECO, INS, DM), the `shipment.shipped` and `ncr.dispositioned` webhook events, the "NCR disposition" and "Engineering change" approval types and default workflows | `BuiltInForms`, `CustomizationRegistration`, `CustomizableLists`, `ListSources`, `DocumentNumberTypes`, `IntegrationEventType`, `WebhookEventCatalog`, `ApprovalDocumentTypes`, `ApprovalDefaults` |
| The sample data: parts, a three-level BOM, stock, GFM, work orders (Mia Torres, project 3001); sales orders and shipments; quality (inspections, an NCR with debit memo and SCAR); the shop floor | `MaterialsSeed`, `SalesShippingSeed`, `QualitySeed`, `ShopFloorSeed` |

About 28,500 lines in 167 files (production code, UI and the module class), 57 tables, plus 18 moved test files (and 6 new ones). Pages, routes (`/materials/...`, `/m/receive`, `/m/count`),
API paths (`/api/v1/materials`, `/quality`, `/engineering-changes`, `/sales-orders`, `/shipping`), permission keys and role behavior are unchanged.

**Stayed in core**, each for a reason:

* **The Materials role** (`OrgRoles.Materials`, `ApprovalOrgRole.Materials`, the role-check extension methods `IsMaterials`/`CanViewMaterials`) and the permission area "Materials":
  roles are stored in the database and edited in the role editor; they exist with or without the module. The **"materials" menu section** stays defined (empty and hidden without the module, like "growth").
* **`ModuleKeys.Materials`/`ModuleIds.Materials`** and its catalog entry; **`CustomizableLists` and the other key lists** lose the module's keys (they are `MaterialsKeys` in the module's contracts).
* **The AP side of quality returns**: `ApBill.QualityDebitMemoId` (a plain key, no foreign key), and AP's "debit memo from a quality return" and "void puts the return back" flows (see 9 below).
* **The purchase order, receiving and receipt records** and the sourcing, supplier portal and EDI code that name a part; the **billing kernel** (`ShipmentInvoicer` is the module's, the invoice it creates is core's).
* **The accounting engine** the module posts through (`GeneralLedger`, `PostingEngine`, journals, accounts), vendors, projects, WBS, contracts, CLINs, employees, labor categories, companies, stored files: the module's context maps them key-only.

## Coupling: every place core referenced the module, and the resolution

The audit counted 9 core files; the real count was about 40, again almost all of them *catalogs and engines that listed the module's records by name*, plus **five accounting and project-cost
flows the audit did not list** (rows 10 to 14). None needed the module to stay: every one became a small interface in core with an empty default. Resolutions: **move**, **invert**
(core owns an extension point, the module fills it), **keep**.

| # | Core referenced materials through | Resolution |
| --- | --- | --- |
| 1 | `ModuleMap`: API segments `materials`, `quality`, `engineering-changes`, `sales-orders`, `shipping`; permission prefixes `materials.`, `quality.`, `sales.`; nav section `materials`; service namespaces `Materials`, `Quality` | **move** into `MaterialsModule.Describe()` (the section stays defined in the menu, hidden while empty) |
| 2 | `Permission` constants and `PermissionCatalog.BuiltIn` (7 permissions) | **move** (`MaterialsPermission`, `Describe().Permissions`); the area and roles stay |
| 3 | `NavCatalog` (24 pages incl. the three phone pages), `NewCatalog` ("+ New › Engineering change"), `NavAccess.IsMaterials`/`CanViewMaterials` | **move** (`Describe().NavEntries`, a `NewAction` row); the two unused `NavAccess` members were removed |
| 4 | `ApiKeyScopes`: the `materials` area | **move** to `Describe().ApiKeyAreas` |
| 5 | `AddWorkbenchApplication` / `IdentityRegistration` / `WorkbenchApi` / ApiClient `Add*Clients`: services, permission decorators, endpoints | **move** into `ConfigureServices` and `MapEndpoints` (the desktop app does not load modules, see *Not done*) |
| 6 | `NotificationsHub` hosted the hourly engineering-change job | **move**: the module registers it in `ConfigureServices` |
| 7 | `DatabaseInitializer` seeded materials, sales/shipping, quality and shop floor in the middle of the sample data | **invert**: `IModuleSeed` at two **new stages** `AfterHr` and `AfterTravel`, so the order (and every row count of the sample database) is as before |
| 8 | Catalog rows built into core: custom lists `parts`, `sales-orders`, list sources, the part form, eight numbered documents, two webhook events (their payload records moved with them), two approval types and workflows, "+ New" | **invert** through the #47 rows (`ModuleContribution.Rows`) |
| 9 | Sales orders are a record type with a list but no form (`BuiltInForms.ListOnly`, fields in `BuiltInEntityFieldCatalog`) | **invert**: new row `ModuleListOnlyEntity(entityType, fields)` |
| 10 | The part as a customizable record (`CustomizationRegistration`) and its required-field guard (`LayoutsRegistration`) | **invert**: the module calls `AddCustomizableEntity` and `AddFormRequirementGuard<IPartService>()` |
| 11 | Sourcing (RFQ), the supplier portal and EDI named the part a line buys, through `IItemReader`, whose **default read the part table** | **invert**: the default is now `NoItemReader` (a host without the module has no parts); the module registers `MaterialsItemReader`. EDI also read `MaterialPurchaseLink` (the part a PO line buys): `IItemReader.ReadForPurchaseOrderLinesAsync` |
| 12 | `ApCreditService` and `ApBillService` read `VendorDebitMemo` and `Nonconformance` (the AP debit memo from a quality return; void reopens the return) and bound `MaterialsOptions` | **invert**: `IVendorReturnSource` (find, mark applied, reopen) in core; `QualityVendorReturnSource` in the module supplies the receiving clearing account number from its own options. Without it "debit memo from a quality return" answers "not found" |
| 13 | `ApBillService` refused to void a bill whose line a landed-cost charge allocated from | **invert**: `IBillLineGuard` (a list of guards; the module's `LandedChargeBillLineGuard` returns the same message) |
| 14 | `FinancialsLoader` and `IndirectRateData` counted **material issued from inventory to projects** as a direct ODC (contract funding, CLIN funding, burdened cost, the indirect-rate base) through the module's `MaterialCostActuals` | **invert**: new row `ProjectCostSource` (catalog `project-cost-source`); the module lists its source; core's `ProjectCostSources.LoadAsync` asks every loaded module's. Same numbers with the module; without it only time and ODCs |
| 15 | `ProcurementPermissionDecorators` let `materials.manage` holders receive any PO line | **keep**, as the string key (a permission that is not in the catalog is simply never held) |
| 16 | The materials purchase-order decorator (`InventoryReceivingPurchaseOrderService`) wrapped core's `IPurchaseOrderService` from `AddWorkbenchMaterials` | **new seam**: `ModuleServiceDecoration(service, decorator)`, applied by `ModuleHost` right after the module's services are copied (see *Seam changes*) |
| 17 | `SourcingConfigurations` declared the foreign key `RfqLine.PartId` to the part table | **keep the column, drop the key** in the core model (see *Data*) |
| 18 | `SourcingSeed` needed the vendor "DIGI", which the materials sample made, so **sourcing's sample data (RFQ, supplier portal contact, EDI partner) vanished without the module** | core's seed now makes DIGI when it is not there |
| 19 | Two shipped tests used project 3001 (the materials sample project) as "a project Alex cannot see" | now use project 5101 (the lifecycle sample) |
| 20 | Internals the module reused | none needed to become public this time (everything the module uses was already public from the earlier moves); `CompanyColumnConfiguration<T>` and the decimal precision helpers (`Money`, `Rate`, `Hours`, `Quantity`) were copied into the module as internals |

The module's `ModuleMap` rows are the same as before for every service (a test checks the module's service interfaces map to it).

## Data: existing installs keep their tables, new installs get them from the module

Same rule and recipe as the earlier modules: no table is dropped, no core migration row changes for an installed database, and the module can be added or left out of an upgrade at any time.
57 tables, 216 indexes and 182 foreign keys inside the module (the 19 core tables it points at are key-only: vendors, purchase orders, order lines, receipts, requisitions and lines, AP bill lines,
accounts, journal entries, invoices, customers, contracts, CLINs, projects, WBS elements, employees, labor categories, companies, stored files).

1. **`ReleaseMaterialsTables`** (core, both providers): the model no longer maps the entities. EF generated 72 `DropTable`/`DropForeignKey` statements; they were removed by hand, `Up` and `Down` are empty.
   Only the snapshot changes. `has-pending-model-changes` is clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL.
2. **Two old core migrations no longer create the tables** (`BusinessDevelopmentHrMaterialsAssistant`: 20 tables; `WavesCeManufacturingProcurement`: 37 tables; the matching drops are gone from their `Down`).
   `strip-core-migration-tables.py` learned `AddForeignKey`/`DropForeignKey` (PostgreSQL adds the foreign keys between the quality tables afterwards) and `--drop-fks-from`, which takes a foreign key
   *from a core table to a removed table* out of its `CREATE TABLE`. Databases that already ran them are unaffected.
3. **The module's `MaterialsBaseline`** (first migration of `MaterialsDbContext`, history `__EFMigrationsHistory_materials`): 273 (SQLite) and 277 (PostgreSQL) statements, each
   `CREATE ... IF NOT EXISTS`, `Down` empty. The four PostgreSQL foreign keys that the EF script adds with `ALTER TABLE` (tables that point at each other) are wrapped in a `DO` block that adds the
   constraint only when no foreign key from that column to that table exists (by columns, not by name), so an existing install does not get a second key (`idempotent-baseline.py`).
   `MaterialsDbContext` maps the 19 core entities key-only, excluded from migrations (three are not in a table named for the class: `GlAccounts`, `GlJournalEntries`, `Companies`).
4. **The one foreign key that crossed the line: `RfqLine.PartId` to `Part`** (a core table pointing into the module). The core model keeps the column and no longer has the key; the old migration
   that created `RfqLine` no longer creates the key. An *existing* install keeps its foreign key (nothing is dropped); a *new* install has none, so on a new install without the module a
   quote line can name a part id no table holds (nothing can create one), and with the module the module's services check the part. This is the only schema difference between an upgraded and a new database.

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseMaterialsTables`: no table touched | the baseline finds the tables, changes nothing | tables and rows stay, unused |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then the release | the baseline creates the tables with the current schema | no tables, none needed |
| New | no materials tables | the baseline creates them | none |

**Verified**

* **SQLite, the real host.** The previous release's host (`a430d31`, Development sample data: 272 rows in 52 of the 57 tables, 411 tables in all) against this release's host:
  * a *new* database made by this host **with** the module has the module's 57 tables identical to the previous release's (columns, keys and indexes, compared table by table: zero differences), every one of the 411 tables has the previous release's row count, and the sample data seeds through the module's stages;
  * a *new* database **without** the module has none of the 57 tables and the host starts;
  * the *upgrade* (the previous release's database, host with and without `modules/materials`): only `ReleaseMaterialsTables` ran in core, and `MaterialsBaseline` with the module; a fingerprint of the schema and every row of the 57 tables is **identical before and after in both cases**, as are the counts of invoices, contracts, projects, journal entries and purchase orders.
* **PostgreSQL, from the migrations (no PostgreSQL instance was available).** The previous release's full migration script against the module's baseline script, parsed table by table:
  57 tables, 707 columns (type, nullability, default), 216 indexes and 182 foreign keys: **zero differences**.
* Module tests on SQLite (`MigrationPathTests`): a new install; the module's schema equals the previous release's; an existing install with rows keeps every table, row and column, gains only history rows, twice; an upgrade without the module keeps its data.
* Not run: PostgreSQL itself. The private repository's CI should run `WORKBENCH_TEST_DB=PostgreSql` and a PostgreSQL upgrade, in particular the `DO` blocks of the baseline.
  Downgrade (an older core against a database the module changed) is not supported, as before.

## Tests

| Shipped suite | Now |
| --- | --- |
| `Materials/*` (13 files, shop floor included) and `Quality/*` (4), `Mobile/CountPageTests` | moved to the module (18 files with their kits) |
| `Purchasing/CreditTests`: the quality return to AP debit memo test | moved (`QualityReturnTests`, plus a not-found case) |
| `Customization/NumberingTests`: the unit-of-work numbers test (it numbered inspections) | moved; the NCR number assertion became `The_modules_documents_number_from_their_default_patterns` |
| `Customization/FormLayoutTests` | the part form's as-built check is `ModuleSurfaceTests.The_part_form_renders_as_built_and_requires_nothing_extra` |
| `Identity/BuiltInRoleParityTests` | the seven permissions' rows moved to the module's `Permissions_are_in_the_catalog_and_held_by_the_same_roles_as_before`; the Business development sales-orders menu check to `The_materials_role_gets_the_materials_section_and_no_finance` |
| `Navigation/NavMenuTests`, `NewMenuTests`, `Licensing/ModuleEntitlementTests`, `Licensing/ModuleGateTests`, `Customization/CustomFieldTests`, `Identity/ProjectVisibilityWebTests` | expectations of the module's items removed (the materials role's menu test now checks that the section is hidden; the entity type count is 11) |
| new in core: `Modules/MaterialsAbsentTests` | a new database has none of the 57 tables; the menu, permissions, API key area, catalog rows, cost source and readers behave as if the module never existed; the host starts and the API paths are 404; a quality return is "not found" |
| new in the module | `MigrationPathTests`, `ModuleSurfaceTests` (permissions with the roles that held them, ModuleMap rows, menu, rows, form, workflows, event payloads, the readers and the cost source), `NumberingTests`, `QualityReturnTests` |
| seam sample | `Workbench.SampleModule` decorates a host service; `ModuleSeamTests` checks the decorator got the host's reader |

## Seam changes this extraction needed

All additive; the contract version is still 1.

* `ModuleServiceDecoration(service, decorator)` (`Workbench.Plugins`): a module wraps a service the *host* registered. `ModuleHost.ConfigureServices` applies it after copying the module's services, keeping the lifetime, and drops a module whose target the host does not have.
* `ModuleSeedStage.AfterHr` and `AfterTravel` (the module's seeds keep their place in the order).
* Catalogs `ModuleCatalogs.ListOnlyEntities` (`ModuleListOnlyEntity`) and `ModuleCatalogs.ProjectCostSources` (`ProjectCostSource`, `ProjectCostSources.LoadAsync`).
* Core interfaces with empty defaults: `IVendorReturnSource` (none registered), `IBillLineGuard` (a list), `IItemReader.ReadForPurchaseOrderLinesAsync`; `NoItemReader` replaces the default `PartItemReader`.
* Module repository: scripts take the module id (`build-module.sh materials`, `check-migrations.sh`, `new-migration.sh`); `strip-core-migration-tables.py` handles foreign keys and `--drop-fks-from`;
  `idempotent-baseline.py` makes PostgreSQL `ALTER TABLE ... ADD CONSTRAINT` idempotent.

## How materials depends on other modules

* **Accounting** (36 files in the audit): it posts receipts, issues and shipments through the general ledger, creates purchase orders and bills through the purchasing services, and reads vendors, accounts and journal entries. All of that is in
  the shipped product today, so the module declares **no `DependsOn`** and has no load-order constraint. When accounting becomes a plugin, `DependsOn: accounting` is the entry to add, and this module's 19 key-only tables
  (the list under *Data*) are exactly the foreign keys the accounting module's baseline must keep creating first. The reverse direction (accounting reading materials) is the six interfaces above, all filled by this module.
* **Billing kernel** (3 files): `ShipmentInvoicer` creates a delivery invoice through core's invoice services (`IInvoiceService`); the invoice, payment and ledger records stay in core.
* **govcon** and **capture** do not reference it (both module test suites pass against this core). **enterprise** features (custom fields, forms, layouts, numbering, automations, webhooks, approvals) read materials records through rows and the customizable-entity registry.
* **workforce**: the work order labor report reads employees and labor categories (core), not payroll.

## Documentation, demo data and public pages that describe materials

Nothing was removed; whether the public site keeps describing a paid module is the owner's decision. They are:

* `docs/materials/index.md`, `inventory.md`, `manufacturing.md`, `quality.md`, `shipping.md` (copies are in the module repository's `docs/`)
* `docs/user-guide/index.md`, `approvals.md`, `project-costs.md` (material cost on projects), `projects.md`, `requisitions.md`
* `docs/admin-guide/approval-workflows.md`, `company-type.md`, `customization.md`, `setup.md`, `users-and-access.md`
* `docs/security/permissions.md`, `docs/integrations/webhooks.md` (the shipment and NCR events), `docs/integrations/api-keys.md` (the materials area), `docs/customization.md`, `docs/project-visibility.md`
* `docs/finance/accounting.md`, `billing.md`, `commercial-billing.md`, `payables.md`, `docs/getting-started/getting-around.md`, `mobile.md` (receive and count on a phone)
* `docs/feature-coverage.md`, `docs/editions.md`, `docs/commercial/modules-and-pricing.md`, `website-copy-suggestions.md`, `end-user-terms-draft.md`, `docs/index.md`, `README.md`, `mkdocs.yml`
* `docs/compliance/nist-800-171/*`, `docs/iso/*`, `docs/training/agenda-finance.md`, `card-finance.md`, `docs/ux/*`, `docs/reference/glossary.md`, `docs/demo-hosting.md`, `docs/operations/configuration.md` (`Materials:*`, `Quality:*`, `SalesShipping:*` sections)
* the marketing pages under `site/` (`index.html`, `docs.html`, `costpoint-alternative.html`) and `tools/docs-screenshots/shots.mjs` (screenshots of the materials pages), `samples/import/README.md`
* the sample data: the government sample company's parts, work orders, sales orders and quality records exist only with the module; the shipped demo's sourcing sample (RFQ, supplier portal contact, EDI partner) is unchanged.

## Verification

* Shipped repository, no `modules/` folder: `Workbench.Server.slnf` builds (the MAUI desktop project cannot be built on this machine for lack of the Mac Catalyst workload; it does not reference the module); the filtered groups (Licensing, Modules, Navigation, Tenancy, Migration, Identity, Reports, Customization,
  Accessibility, GoldenPath, Dashboards, Purchasing, Sourcing, Setup) pass (786 passed, 1 skipped), and so does every other folder of the shipped suite run in groups (3,411 tests in all, none failing; two sample-data expectations in `Mobile` and `Operations` were adjusted). The shipped host starts without the module: no menu section, no pages, the API paths answer 404 (`MaterialsAbsentTests`), no `fail:` lines in its log.
* Module repository, module loaded from the built folder: 77 materials tests pass on InMemory and on SQLite (`WORKBENCH_TEST_DB=Sqlite`); the govcon module's 100 and the capture module's 213 tests still pass against this core.
* `has-pending-model-changes`: clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL, and for the module's `MaterialsDbContext` on both, checked with `dotnet ef` directly, never with `scripts/new-migration.sh`.
* A fresh database with all three modules loaded (`modules/govcon`, `capture`, `materials`): every migration applies, the sample data seeds, the scoped style sheet is served, no errors.
* Upgrade from the previous release's host: see *Data*. Not run: PostgreSQL itself, the full shipped suite (known to hang locally).

## Not done

* The worker and the desktop app do not load modules: the engineering-change job runs in the web host only (it did before, in the web host's hosted-services list), and the desktop app loses the materials pages and clients.
* PostgreSQL runs (see above) and signing or packaging the module.
* `AssistantPrompts` and the assistant's tools did not mention materials; nothing to move.
* A `ModuleServiceDecoration` whose service the host lacks refuses the module; there is no shipped test with a module that does so (the sample module's decoration succeeds).

## Lessons for project controls and workforce

* **Search for the module's own words in accounting, not only in catalogs.** The extra work was in *accounting and the project financials* (AP debit memos from quality returns, void checks, material cost as a direct cost in funding and the indirect-rate base): grep the other
  engines for the module's record names (`VendorDebitMemo`, `LandedCharge`, `MaterialCostActuals`) before estimating. Project controls will meet the same thing from the other side: EVM, ceilings and the plan are read by core pages.
* **A static core engine needs a registry row, not a service.** `FinancialsLoader` and the indirect-rate loader are static and have only a database context; a row type that carries the loader function (`ProjectCostSource`) fits where an injected interface would not.
* **A module that decorates a core service needs the host to do it** (`ModuleServiceDecoration`); look for `Decorate<...>` calls in the module's registration before extracting.
* **A core sample that borrows the module's sample data silently disappears.** Run the shipped host without the module and compare row counts (all 411 tables) against the previous release; two shipped tests needed a different sample project.
* **Take a foreign key *out of core's model* with an edit to the old creating migration, and say so**: it is the one place an upgraded and a new database differ.
* **PostgreSQL adds foreign keys between mutually dependent tables with `ALTER TABLE`**, which has no `IF NOT EXISTS`: the baseline needs a guard, and a check by columns beats a check by name.
* **Order of sample data is behavior**: two new seed stages kept every sample row count identical; compare the whole database, not the module's tables.
* **Workforce** has the same three things to decide before extraction: the leave kernel ([module-plugins.md](module-plugins.md#not-done-here-the-leave-kernel)), pay-rate masking of its DTOs (`FieldMasker` already finds `SensitiveField` in module contracts), and the payroll export that the accounting export reads.
* **Project controls**: expect `IProjectCostSource`-style rows in the other direction (EVM and ceilings read costs; the plan and timesheets call ceilings through `ChargeGuard`, which is a core engine asking the module a question: define it as an interface with a permissive default).
