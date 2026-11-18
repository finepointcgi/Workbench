# Extracting a module, phase 3a: government billing moves to `govcon`; the `billing` module analysis

Phase 3a of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md), the pilot in
[module-extraction-govcon.md](module-extraction-govcon.md)). It had two parts:

1. **Done:** finish the government billing leftover from the pilot (progress payments on SF 1443, performance-based payments, SF 1034
   vouchers, PIEE). It moved into the `govcon` module behind an **invoice-financing hook** that stays in core.
2. **Analyzed, not moved:** the `billing` module itself (invoicing, accounts receivable, commercial billing, revenue recognition, live
   accounting push). The coupling analysis below shows where its real boundary is and why the engine cannot leave in one change.
   Nothing of `billing` was deleted from this repository.

## 1. Government billing now lives in the `govcon` module

### Why `govcon` and not `billing`

The code was always licensed by `govcon` (`ModuleMap.ServiceTypes`), its settings and screens are for government contracts, and every
one of its call sites in invoicing is a *hook-shaped* question ("does this contract have financing?"). The reverse direction (the
module calling invoicing) needs only a handful of engine pieces (`BillingContext`, the live-invoices query, invoice numbering,
`BillingCostSource`, `BillingOptions`, `IInvoiceService`). Those stay in core as a small public **billing kernel**, so the module needs
nothing from a future `billing` module and a customer can license `govcon` and `billing` independently of the code's location.

### What moved (about 4,000 lines of production code, 5 test files)

| Feature | Core layers that held it |
| --- | --- |
| Progress payments: SF 1443 calculator, requests, the contract's progress payments page, print view | Domain, Contracts, Application (`Billing/Government/`), endpoints, HTTP client, EF configuration, UI pages and components, permission decorators |
| Performance-based payments: events, evidence, completion, billing of completed events | same layers |
| SF 1034 / SF 1035 public voucher (pagination, print form), per-contract invoice format | same layers |
| PIEE (WAWF) invoice file: build, export, submit through `IPieeSubmitter`, status tracking | same layers |
| Contract financing ledger: liquidation lines on delivery invoices, the unliquidated balance, approve/void guards | `Application/Billing/Government/ContractFinancingLedger` |
| Sample data | `GovernmentBillingSeed` |

Four tables moved to the module: `ContractGovernmentBilling`, `ProgressPaymentRequest`, `PerformanceBasedPaymentEvent`,
`PieeSubmission` (the enums `ContractFinancing`, `InvoiceFormat`, `ProgressPaymentCostBasis`, `PieeDocumentType` and
`PieeSubmissionStatus` went with them).

### What stayed in core, and why

| Stayed | Reason |
| --- | --- |
| The ledger records: `Invoice`, `InvoiceLine` (kinds `ProgressPayment`, `PerformanceBasedPayment`, `Liquidation`), `BilledSource`, `Payment`, `BillingMilestone` | Used by 84 files across cash, currencies, sales tax, ceilings, EVM, reports, the accounting export, imports and shipping. They are the receivables ledger, not the module's feature. |
| `BillingLedger` and the account `2320` Contract financing; `InvoiceType.ProgressPayment` / `PerformanceBased` and the `InvoiceKind` values | The general ledger posting and the accounting export read invoice lines of these kinds. A host without the module can hold (imported) financing invoices. |
| CLIN funding checks, `ClinFundingCalculator`, `FundingForecaster`, the `reports/standard/funding-status` page | Contract funding is not financing; they never read the moved entities. The funding status page is still *licensed* by `govcon` through `ModuleMap.NavPages`. |
| The billing kernel (below) | Shared by the module and by invoicing. |

### The hook (`IInvoiceFinancing`, `src/Workbench.Application/Billing/InvoiceFinancing.cs`)

The contract is in core; the default is `NoInvoiceFinancing`, registered by `AddWorkbenchBilling`. The `govcon` module registers
`ContractFinancingHook` after it, so the module's replaces the default. Invoicing asks the hook, in order of the invoice's life:

| Hook method | Called from | `NoInvoiceFinancing` |
| --- | --- | --- |
| `PerformanceBasedContractsAsync` | `InvoiceService.ListBillingContractsAsync` (the contract offers the invoice type) | none |
| `GeneratePerformanceEventsAsync` | `InvoiceGenerator.GenerateAsync` for `InvoiceType.PerformanceBased` | a warning ("doesn't use performance-based payments"), no lines |
| `AddLiquidationAsync` | preview, generate, regenerate, ready-to-bill | the generation unchanged |
| `BindEventsAsync` | when generated lines are written, and when sources are released | nothing |
| `EnsureCanApproveAsync` | approve | nothing |
| `EnsureCanVoidAsync` | void | nothing |
| `PreferredPrintFormAsync` | `GetPrintAsync` (sets `InvoicePrintDto.PreferredForm`, `"sf1034"` or null) | null |
| `OnDraftDeletedAsync` | delete draft (removes the SF 1443 request and PIEE submission) | nothing |

So a host without the module invoices correctly: delivery invoices carry no liquidation, performance-based payments are not offered,
the commercial and CLIN print layouts are the only forms. `Billing/InvoiceFinancingDefaultTests` pins that in the shipped suite, and
the whole shipped billing suite now runs with the default.

### The billing kernel (public, in core)

`BillingContext`, `DraftLine`, `Generation` (made public), `InvoiceGenerator.PriorInvoicesAsync`, and the new `BillingKernel`
(`AuditEntityType`, `NextNumberAsync`, `DefaultPrefix`, `Clean`, extracted from `InvoiceService`). `BillingMapping` was made public
(contract to domain mappings the module reuses). When `billing` is extracted these are the first things that must *stay* core.

### Core pages that hosted the module's screens

| Page | Resolution |
| --- | --- |
| Contract billing page (settings card, performance events, financing summary) | `UiSlot`s `billing.contract.cards` and `billing.contract.summary`; the module's `ContractFinancingCards` and `FinancingCard` |
| Invoice page (PIEE panel, Finance only) | slot `billing.invoice.panels`; the module's `InvoicePieePanel` |
| Invoice print page (SF 1034 voucher form, its toolbar links, the SF 1443 link) | slots `billing.invoice.print.links` and `billing.invoice.print.sf1034`; the default form comes from `InvoicePrintDto.PreferredForm` (core no longer reads the voucher) |
| `BillingFormat` (labels for financing, voucher format, PIEE) | the module's `GovFormat` |

The routes did not change: `/billing/contracts/{id}/progress-payments`, `/billing/progress-payments/{id}/sf1443` and the API paths
under `/api/v1/billing/...` are the same. Because the segment `billing` is still mapped to the billing module, the API gate for these
routes is the billing license; the change gate on the services is still `govcon` (the module's service interfaces map to it).

### Other core touch points (resolution in one line each)

| Where | Resolution |
| --- | --- |
| `ModuleMap.ServiceTypes` (3 interfaces) | removed; the module's `Describe().ServiceInterfaces` maps them |
| `BillingPermissionDecorators`, `AddWorkbenchBilling`, `WorkbenchApi`, ApiClient registration | the government rows removed; the module registers its own decorators, endpoints and services |
| `DatabaseInitializer` seeded it between multicompany and travel | `ModuleSeedStage.AfterMulticompany` (new): the module's `GovernmentBillingSeed` runs at the same point |
| `IModuleSeed` seeds used `WorkbenchDbContext` and an infrastructure helper | the seed uses `IWorkbenchDbContext` and `IWorkbenchDbContextFactory`; the fiscal-year helper is copied into it |
| The shipped `BillingKit` (test kit) built services by hand | `BillingKit.Financing` (default none); the module tests set it to the real hook in the module initializer |

## 2. Data

Same rule and recipe as the pilot. No table is dropped, no core migration history row changes for an installed database.

1. **`ReleaseGovernmentBillingTables`** (core, both providers): the model no longer maps the four entities, EF generated the drops, they
   were removed by hand; `Up` and `Down` are empty. Only the snapshot changes. `has-pending-model-changes` is clean for both
   providers and both contexts.
2. **`WaveAFinanceCore`**, the one old migration that created the tables, no longer creates them (and its `Down` no longer drops them);
   `scripts/strip-core-migration-tables.py` in the module repository does that edit mechanically. Databases that already ran it are
   unaffected.
3. **The module's `GovernmentBillingBaseline`** (second migration of `GovConDbContext`, history `__EFMigrationsHistory_govcon`): four
   `CREATE TABLE IF NOT EXISTS` and seven `CREATE INDEX IF NOT EXISTS`, `Down` empty. `GovConDbContext` maps `Contract`, `Clin` and
   `Invoice` key-only and excluded from migrations (the module's foreign keys point at them).

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseGovernmentBillingTables`: no table touched | the migration finds the tables, changes nothing | tables and rows stay, unused |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then the release | the migration creates the tables with the current schema | no tables, none needed |
| New | no government billing tables | the migration creates them | none |

**Verified** (module repository tests on SQLite; PostgreSQL covered by script comparison and `has-pending-model-changes`, not run, as in the
pilot): a new install; the module's tables identical to what core used to create (11 `CREATE` statements compared against a fixture
generated from the previous release's migrations); an existing install with rows in all four tables keeps every row and gains only history
rows, and running the module twice changes nothing; an existing install upgraded without the module keeps its data. **End to end** on real
data with the previous release's host: see *Verification* below.

## 3. Tests

| Shipped suite | Now |
| --- | --- |
| `Billing/GovernmentBillingTests` (SF 1443 calculator, progress payments, performance-based payments, SF 1034 pagination, PIEE) | moved |
| `Billing/GovernmentBillingPermissionTests`, `GovernmentBillingKit` | moved |
| `Licensing/ModuleGateTests` for `govcon` | `govcon` has no service left in the shipped build, so the shipped theory asserts that; the module has `GovConGateTests` (the same theory over the module's services, including government billing) |
| `Modules/GovConAbsentTests` | the four tables and the three API paths are asserted absent |
| new in core: `Billing/InvoiceFinancingDefaultTests` | invoicing without the module |
| new in the module: `GovernmentBilling/GovernmentBillingMigrationPathTests` | new install, identical schema, existing install with rows, upgrade without the module |

## 4. The `billing` module: why it did not move in this change

`ModuleMap` assigns to `billing` the API segments `billing`, `revenue`, `accounting-live`, the permission prefixes `billing.` and
`revenue.`, the nav group *Billing & AR* and the pages `billing/invoices`, `billing/statements` and `accounting/revenue/*`, and the
service namespaces `Billing`, `Revenue` and `AccountingLive`.

| Area | Production lines | Files in the module's own folders | Core references from outside |
| --- | --- | --- | --- |
| `Application/Billing` (invoices, payments, receivables, AR reports, commercial agreements, milestones, accruals, share links) | about 4,700 after this change | 28 | see below |
| `Application/Revenue` | 1,395 | 8 | 4 files |
| `Application/AccountingLive` (QuickBooks/Xero push) | 2,132 | 9 | 3 files |
| Contracts, Domain, configurations, endpoints, client | about 3,500 | 25 | |
| UI (`Billing/`, `Revenue/`, accounting-live pages) | | 62 (of which government billing took 14) | 15 |

### The real boundary

* **The receivables ledger cannot leave.** `Invoice`, `InvoiceLine`, `BilledSource`, `Payment`, `PaymentApplication`, `InvoiceStatus`,
  `InvoiceType` and the customer and contract billing settings are referenced from 84 files outside the billing folders (46 in
  `Application`): the cash forecast and ledger, currency revaluation, sales tax, ceilings and closeout, EVM, the accounting export, the
  Costpoint importers (open items), shipping, approvals, automation entities, the standard reports, the company service. A core-only
  install has to read (and import) invoices and payments for those. They are *records*, like the labor cost engine in the pilot.
* **The engine's outside callers are few.** Outside the billing folders only these call the Application types: `BillingCostSource`
  (EVM, 11 files, a cost reader that stays core), `BillingLedger` (the accounting export and the migration service: kernel), and the
  seeds (six, all become module seeds). `InvoiceService`, `InvoiceGenerator`, `InvoiceDocuments`, `UnbilledAccruals` and
  `PaymentService` are used by seeds and by contract-level decorators only. That is a good sign: moving the *services* is mostly
  moving files.
* **What does reference the billing service contracts** (and needs a hook, a slot or a core copy): ceiling enforcement decorators
  around `IInvoiceService`, `ShipmentInvoicer` (shipping creates invoices), the approval handlers, `CompanyPermissionDecorators`
  (payments), customization (home widgets, built-in forms, layouts, document numbering), the My to-do providers,
  `StandardReportService` and the finance reports, `ReconciliationSources`, `EditionGates`, the customer portal
  (`CustomerViewEndpoints`), and the contracts of indirect rates and pricing that share DTOs with billing.
* **Contracts are the second wall.** `Workbench.Contracts.Billing` holds DTOs for the ledger (read by core), for the billing services
  (module), for the customer share link and view (licensed by `enterprise`) and for burdened cost (`accounting`). The namespace has to
  be split four ways before the module's service interfaces can leave, because `ModuleMap` maps a namespace to one module.
* **Invoicing is billing by the map, and the map has to be changed to extract it.** The product sells invoicing under `billing`. A
  core-only install after the extraction has no Billing area: the menu group *Billing & AR*, `billing/*` and `accounting/revenue/*`
  pages are absent (a module-owned nav group already works that way), invoices and payments that exist (imports, a former license)
  stay in the ledger, the cash and revaluation features keep reading them, and nothing can create or approve one. That is a sane story,
  but it is a product decision, because anything a customer relies on from the free tier that touches invoicing would disappear.

### Recommended plan for the `billing` extraction (phase 3b, not done)

1. **Kernel first (core, no module yet).** Declare in core what stays: the ledger entities and their tables, `BillingKernel`,
   `BillingLedger`, `BillingCostSource`, `IBillingLimits`, `BillingContext`, plus two new hooks the same way `IInvoiceFinancing` was
   done: an *invoice-created* hook for `ShipmentInvoicer` and the seeds, and a *billing facts* reader for the widgets and reports
   (billed to date, aging, to-do items) that today call `IInvoiceService`/`IArReportService`. Split the contracts namespace.
2. **Revenue and live accounting push first, as a separate pair of PRs.** They are the leaves: 9 and 8 files of Application, 4 and 3 core
   references, their own tables (revenue: seven; live push lives in the accounting export area) and no engine callers. Each moves with
   its pages and seeds and releases its tables the way government billing did.
3. **Then commercial billing** (agreements, milestones, unbilled accruals, reminders): its entities are referenced only by the engine
   (`BillingContext.Plan` is the one core-visible field), and its tables can be released like the above.
4. **Last the invoice and payment services and their pages**, once every outside caller goes through a hook or reader. The tables of the
   ledger records stay core-owned, so there is *no data migration at all* for that step.
5. Each step keeps the pilot's rules: release migrations only, the module's baseline idempotent, the previous release's host as the
   upgrade test.

## 5. Public documentation

Nothing was removed. Pages that describe what moved (owner decision: keep describing a paid module on the public site, or not):

* `docs/finance/billing.md`, `docs/finance/commercial-billing.md`, `docs/finance/index.md` (progress payments, SF 1443, performance-based
  payments, PIEE, SF 1034)
* `docs/admin-guide/contracts-projects.md`, `docs/admin-guide/company-type.md`, `docs/security/permissions.md`
* `docs/training/card-finance.md`, `docs/training/agenda-finance.md`, `docs/training/exercise-script.md`
* `docs/compliance/dcaa/sf1408.md`, `docs/feature-coverage.md`, `docs/index.md`, `README.md`
* the marketing pages `site/index.html`, `site/costpoint-alternative.html`
* the help articles `src/Workbench.Ui/Help/Articles/government-billing.md` and `billing-an-invoice.md` (embedded resources; moving them
  needs a help-article extension point)

## 6. Seam changes

All additive; the contract version is still 1.

* `IInvoiceFinancing` and `NoInvoiceFinancing` (core, `Workbench.Application.Billing`); `InvoicePrintDto.PreferredForm`.
* `ModuleSeedStage.AfterMulticompany`.
* Public in core for modules: `BillingContext`, `DraftLine`, `Generation`, `BillingMapping`, `InvoiceGenerator.PriorInvoicesAsync`, the new `BillingKernel`.
* Four named UI slots (above).
* Module repository: scripts `strip-core-migration-tables.py`, `empty-release-migration.py`; `idempotent-baseline.py` takes the migration and module names.

## Verification

* Shipped repository, no `modules/` folder: builds; filtered groups pass (Licensing 96, Modules 16, Navigation 56, Tenancy 42, Migration 37,
  Identity 279, Reports 20, Accessibility 10, GoldenPath 14, Billing 79, Time 83, Payroll 80, Setup 46, CompanyTypes 28, Customization 54,
  Ceilings 34, Evm 33, Platform 8 (two PostgreSQL tests skipped)). One shipped test assumed the government sample project 4101 and now uses 3001.
* Module repository, module loaded from the built folder: 99 tests pass on InMemory and on SQLite (`WORKBENCH_TEST_DB=Sqlite`).
* `has-pending-model-changes`: clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL, and for the module's
  `GovConDbContext` on both (checked with `dotnet ef` directly, never with `scripts/new-migration.sh`).
* Upgrade from the previous release's host (`471b458`, Development sample data on SQLite: 6 invoices, 2 government billing settings, 1 progress
  payment request, 4 performance-based payment events, 1 PIEE submission added by hand): with `modules/govcon` the upgrade applied only
  `ReleaseGovernmentBillingTables` (core) and `GovernmentBillingBaseline` (module); without the folder only the core one. Row counts of all four
  tables and of invoices were unchanged and the four tables' schema checksum was identical before and after in both runs; the host started in both.
* Not run: PostgreSQL (no local instance); the module repository's CI should run `WORKBENCH_TEST_DB=PostgreSql`. The full shipped suite (known to hang locally).
