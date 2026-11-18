# Extracting a module: accounting (cash, bank files, fixed assets, corporate budgets, sales tax)

Phase 8 of moving paid license modules out of this repository (the seam is in [module-plugins.md](module-plugins.md); the earlier moves are
[govcon](module-extraction-govcon.md), [billing](module-extraction-billing.md), [capture](module-extraction-capture.md),
[materials](module-extraction-materials.md), [project controls](module-extraction-projectcontrols.md) and [workforce](module-extraction-workforce.md); the
[audit](module-extraction-audit.md) called `accounting` class C as a whole, with five class A slices). The owner approved moving **only the slices**: the ledger,
vendors, purchase orders and payables, currencies and indirect rates stay in core, and free, because dozens of core files and other paid modules read them.
This page is the inventory, **the change to the sold line**, every place core touched the slice and how it was resolved, the data path for existing installs, and what is still public.

The slice now lives in the **private repository** (`Workbench-Modules`, a local git repository with no remote yet) as `src/Workbench.Module.Accounting*`,
builds into `dist/modules/accounting/` (copy it to a host's `modules/accounting/`), and loads like any plugin. This repository ships without it.

## The price line changed: read this first

The catalog sold `accounting` as "the general ledger, period close, payables, purchasing, payment runs, cash and bank reconciliation, fixed assets, sales tax,
multi-currency, corporate budgets and indirect rates". The ledger and everything it needs stay public, so **the sold module is now the five slices only**:

| | Before | After |
| --- | --- | --- |
| `ModuleCatalog` text of `accounting` | "The general ledger, period close, payables, purchasing, payment runs, cash and bank reconciliation, fixed assets, sales tax, multi-currency, corporate budgets and indirect rates." | "Cash and bank reconciliation, bank payment files, fixed assets with tax books and depreciation, corporate budgets with planning cycles and recurring journals, and sales and use tax." |
| `ModuleCatalog` text of `core` | "Free forever: time, ..., the audit log and importers." | the same, plus "the general ledger, period close, payables, purchasing, payment runs, multi-currency and indirect rates" |
| **Free now** (core rows) | paid | The general ledger (chart of accounts, journals, posting rules, period close, trial balance and statements, revaluation), vendors, requisitions, purchase orders and receiving, subcontracts, AP bills, credits and refunds, payment runs and payments, RFQs and the supplier portal, corporate cards and card charges, **multi-currency** (currencies, rates, revaluation), **indirect rates** and burdened cost, the accounting exports |
| **Still paid** (module rows) | paid | Bank accounts, statement import (CSV, OFX, BAI2), reconciliation, the cash forecast; **bank payment files** (NACHA, positive pay, remittance advice) **and vendor bank details** (the approved-change workflow); fixed assets, categories, depreciation, transfers, tax books and the asset reports; **corporate budgets** (versions, budget vs actual, planning cycles and submissions) **and recurring journals**; **sales and use tax** (jurisdictions, rates, codes, exemption certificates, invoice sales tax, bill use tax, the return) |
| API segments | `accounting`, `cash`, `assets`, `currencies`, `planning`, `indirect-rates`, `sales-tax`, `purchasing`, `cards`, `vendor-bank`, `bank-files` | module: `cash`, `assets`, `planning`, `sales-tax`, `bank-files`, `vendor-bank`; **core**: `accounting`, `currencies`, `indirect-rates`, `purchasing`, `cards` |
| Permission prefixes | `gl.`, `planning.`, `tax.`, `assets.`, `ap.`, `purchasing.`, `cash.`, `cards.`, `setup.currencies`, `compliance.indirect_rates` | module: `planning.`, `tax.`, `assets.`, `cash.`, `gl.recurring.` (7 permissions); **core**: `gl.`, `ap.`, `purchasing.`, `cards.`, `setup.currencies`, `compliance.indirect_rates` |
| Menu | groups Accounting, Payables & purchasing, Cash, Assets, Planning | module: groups **Cash, Assets, Planning**, "Recurring journals", "Sales and use tax return", "Sales and use tax" (Setup), "My budgets", "Asset reports"; **core**: Accounting, Payables & purchasing |
| Service namespaces | `Accounting`, `Assets`, `BankFiles`, `Cash`, `Currencies`, `IndirectRates`, `Planning`, `Purchasing`, `SalesTax` | module: `Assets`, `BankFiles`, `Cash`, `Planning`, `SalesTax`; **core**: `Accounting`, `Currencies`, `IndirectRates`, `Purchasing` (and `IBurdenedCostService`, the card services) |

Things people may notice that follow from the same decision:

* **The supplier portal feature (`Feature.SupplierPortal`) now belongs to the `core` module** (it is purchasing). It still needs the Business edition, but a key that lists only `enterprise` now unlocks it
  (`ModuleEntitlementTests` says so). If the portal should stay paid, it needs a gate of its own; that is the owner's call.
* **Recurring journals went with Planning** (they were in the audit's Planning area, 0 core references, and they were listed with the budgets in the permission text). They post through the free ledger.
  If recurring journals should be free, it is `RecurringJournalService`, its page and endpoints (about 1,000 lines) plus the two tables, a small follow-up.
* **Vendor bank details live in the module** (`vendor-bank`): a host without it has no vendor ACH details, no approved-change workflow and no NACHA or positive pay files. Payment runs, payments and
  vendor refunds work as before. `ap.vendors.bank` and the separation-of-duties pair that names it stay in core's catalog and do nothing without the module.
* **Corporate cards moved from the Finance › Cash menu group to Payables & purchasing** (the Cash group is the module's). The page and its permission are core.
* **Three tabs now come last**: Budgeted rates (Indirect rates) and Budget vs actual (accounting reports) are contributed through slots and are appended after the core tabs.
* Nothing is enforced today (`accounting` is not enforced), so no current license loses anything, but **the next price list must not sell the general ledger, payables, purchasing, multi-currency or indirect rates as
  accounting**. `docs/commercial/modules-and-pricing.md` (the module's price line and the plan tables), `website-copy-suggestions.md`, `docs/editions.md` and the marketing pages still say they are: public pages, listed below, not edited.
  The hosted trial and entitlement lists name `accounting` by id (`TenantScope.TrialModules`, the license tool, the license server tests), so they need no edit.

## What moved and what did not

**Moved to the module** (143 files, about 24,100 lines of production code, 39 tables, plus the moved tests: 22 files):

| Area | Core layers that held it |
| --- | --- |
| Cash: bank accounts (account numbers protected), statement import, matching, reconciliation with its print page and close check, waivers, the cash forecast | Domain `Cash/`, Contracts `Cash/`, Application `Cash/`, UI `Cash/`, `Layout/CashPrintLayout`, `wwwroot/cash.js`, `CashEndpoints`, `HttpCashServices` |
| Bank files: vendor bank details with the approved-change workflow, bank ACH settings, NACHA and positive pay writers, payment file downloads, remittance advice | Application `BankFiles/`, Contracts `BankFiles/`, `Domain/Purchasing/VendorBanking.cs`, `Domain/Cash/PaymentFiles.cs`, the payment run files panel and the vendor bank card and dialog |
| Assets: register, categories, capitalization from bills, depreciation and its close check, transfers, disposals, tax books (MACRS, 179, bonus, form 4562), the asset reports and the Costpoint fixed asset importer | Domain `Assets/`, Contracts `Assets/`, Application `Assets/` and `Imports/Costpoint/FixedAssetImporter`, UI `Assets/` |
| Planning: corporate budget versions, budget vs actual and the report source, planning cycles and submissions (with the approval handler), recurring journals and their six-hourly job, budgeted rates | Domain `Planning/`, Contracts `Planning/`, Application `Planning/`, UI `Planning/`, the Budgeted rates tab |
| Sales tax: jurisdictions, rates, codes and assignments, customer profiles and exemption certificates, invoice tax and bill use tax, the return | Domain `SalesTax/`, Contracts `SalesTax/`, Application `SalesTax/`, UI `SalesTax/` |
| Seven permissions (`gl.recurring.manage`, `planning.budgets.manage`, `tax.sales_tax.manage`, `assets.manage`, `assets.depreciation.run`, `cash.manage`, `cash.forecast.view`), their decorators, 20 menu entries, the `ModuleMap` rows, the API key segments `cash`, `assets`, `planning`, `sales-tax` | `Permission`/`PermissionCatalog`, `IdentityRegistration`, `NavCatalog`, `ModuleMap`, `ApiKeyScopes` |
| Sample data: fixed assets and bank accounts, the planning sample, bank files and sales tax, the tax books | `AssetsSeed`, `CashSeed`, `PlanningSeed`, `BankFilesTaxSeed`, `TaxBooksSeed`, `DatabaseInitializer` |

The pages, routes (`/cash/...`, `/assets/...`, `/planning/...`, `/accounting/recurring`, `/accounting/sales-tax`, `/setup/sales-tax`), API paths (`/api/v1/cash`, `assets`, `planning`, `sales-tax`, `bank-files`,
`vendor-bank`, `accounting/recurring-journals`), permission keys and role behavior are unchanged.

**Stayed in core**, each for a reason:

* **The ledger, vendors, purchase orders, AP, payment runs, subcontracts, sourcing**: read by dozens of core files and by every other paid module (the audit's class C). Free.
* **Currencies and indirect rates** (the audit's class B): 4 and 5 real core references, burdened cost read by billing and capture, the multi-currency expense report. The owner kept them in core. The one place they read the
  slice, the revaluation of foreign currency bank accounts, is a hook now (`IForeignBalanceSource`).
* **Corporate cards and card feeds** (travel): card statements use the CSV and OFX **statement file readers** (`Application/Cash/Statements`) and so do capture's lead importer. The readers, `BankTransactionType` and `CsvColumnMapping`
  therefore stayed in core with them; the module's statement import uses them.
* **`Allocation.Split`** (money split in whole cents, `Domain/Planning/Allocation.cs`): corporate budgets and materials' landed cost both use it.
* **The three purchase orders of the CPSR sample** (lab equipment and engineering support on project 1001, office furniture): `TaxBooksSeed` made them. They are core sample data (project 1001's funding
  runway counts them as committed, and two shipped tests depend on it), so they are `CpsrPurchaseOrderSeed` in core now; the government contracting module's seed builds on them as before.
* **Enum members and names that saved data uses**: `InvoiceLineKind.SalesTax`, `JournalSource.Asset`, the approval document types for budgets, planning and vendor bank changes, `CompanyRecordKind.BankAccount` and `FixedAsset`,
  `SensitiveActions.BankDetails`, `ValueListCatalog.TaxExemptionReasons`.

## Coupling: every place core referenced the slice, and the resolution

The audit counted 1 to 4 real core files per area. Compiling the shipped repository after deleting the code found **about 45 places**; none needed the slice to stay, and no area was left out for coupling.

| # | Core referenced the slice through | Resolution |
| --- | --- | --- |
| 1 | `ModuleMap`: segments, prefixes, groups, pages, service namespaces, service types | **move** to the module's rows; what stays maps to core (explicit core rows for `finance/Accounting`, `finance/Payables & purchasing`, the `cards` page, `setup/currencies`) |
| 2 | `Permission` constants and `PermissionCatalog` rows; the separation-of-duties pair `gl.journals.post` / `cash.manage` | **move** (`AccountingPermission`); the pair keeps its key as a string |
| 3 | `NavCatalog` (20 entries), `NavAccess.OwnsBudget` (read `IPlanningCycleService`) | **move**; "My budgets" uses the relationship flag `accounting.owns-budget` (`INavAccessFlag`); `OwnsBudget` is gone from `NavAccess` |
| 4 | `DependencyInjection`, `IdentityRegistration`, `WorkbenchApi`, `ApiClient`, `NotificationsHub` (the recurring journal job) | **move** into `ConfigureServices` and `MapEndpoints` (the hosted job runs in every host that loads the module) |
| 5 | `DatabaseInitializer`: five seeds | **new stages** `ModuleSeedStage.AfterProcurement` (assets, cash) and `AfterSourcing` (bank files, sales tax, tax books); planning uses `AfterPlanning`; `CpsrPurchaseOrderSeed` stays core |
| 6 | `InvoiceService` (sales tax at approval), `BillingLedger` and `ExportDataLoader` (the account sales tax lines credit) | **`IInvoiceTaxHook`**, optional, default account 2150 (`SalesTaxDefaults.PayableNumber`) |
| 7 | `ApBillService` (use tax at posting and on void; a bill line that became a fixed asset), `ApCreditService` (the use tax line of the bill trail) | **`IBillTaxHook`** (`BillTaxRecord`) and the existing **`IBillLineGuard`** |
| 8 | `CompanyService` (move records between companies: bank accounts and fixed assets), `CompanyPermissionDecorators` (the company filter on both services), `CompanyConfigurations` (their `CompanyId` columns) | **`ICompanyRecordSource`**; the module applies the same decorators (`AddAccountingCompanyScoping`) and owns the two column configurations |
| 9 | `Reauthentication`: the bank account number prompt | **move**: the module decorates its own service (`ReauthBankAccountService`) |
| 10 | `RevaluationEngine`, `RevaluationService`, `CurrencySupport.ForeignCashAccountAsync`, `BillingLedger`, `ApPaymentService` (foreign currency bank accounts) | **`IForeignBalanceSource`** (open balances, names, and the GL cash account for a currency) |
| 11 | `SetupAdminService`, `SetupSteps`, `SetupHome` ("Bank accounts" step) | **`IBankAccountProbe`**; the step shows only where the cash page is in the menu (`SetupSteps.CashAvailable`) |
| 12 | `CustomListService`: a sales tax exemption reason in use | **`IValueListUsage`** |
| 13 | `ImportRegistration`: the fixed asset importer | **move** (`IImporter` registered by the module) |
| 14 | `ReportSources`: the corporate budgets source | **move** (a `report-source` row) |
| 15 | UI: `BillPage` (sales and use tax card, per-line "add to fixed assets"), `InvoicePage` (tax card), `PaymentRunPage` (bank files panel), `VendorPage` (bank details card), `IndirectRatesPage` (Budgeted rates tab), the accounting reports page (Budget vs actual tab) | **six slots** (`purchasing.bill.panels`, `billing.invoice.tax`, `purchasing.payment-run.panels`, `purchasing.vendor.panels`, `indirect-rates.tabs`, `accounting.reports.tabs`); the bill's per-line button became a "Fixed assets" card under the lines |
| 16 | `CardFeeds` (travel) and capture's lead importer use the statement readers | **stay core** (with `BankTransactionType` and `CsvColumnMapping`) |
| 17 | `AccountingMapping`, `LedgerLookups`, `CompanyService.EnsureDefaultAsync` (internal helpers the module needs) | made **public** |
| 18 | Three other paid modules read assets: workforce (the assets a leaver holds, returning them), project controls (government property still in service at closeout), government contracting (the CAS disclosure's capitalization threshold and depreciation methods, and a sample date) | **`IAssetSource`** in core, optional; the three modules ask it. Without the module: no assets to recover, no property open, and the two CAS items have no live answer |
| 19 | Workforce's checklist task had a foreign key to the asset table | **key-only**: the column stays, the foreign key is gone (the index stays). The unreleased workforce baseline was regenerated, so a new install does not need the accounting tables (see Data) |
| 20 | Old core migrations that created, altered or populated the tables | see Data |
| 21 | Shipped tests (`Cash`, `BankFiles`, `Assets`, `Planning`, `SalesTax`, the golden path's reconciliation, role parity, setup, navigation, entitlement and seat tests) | see Tests |

### Slots, hooks and seams added

| What | Where |
| --- | --- |
| Slots `billing.invoice.tax`, `purchasing.bill.panels`, `purchasing.payment-run.panels`, `purchasing.vendor.panels`, `indirect-rates.tabs`, `accounting.reports.tabs` | host pages |
| `IInvoiceTaxHook`, `IBillTaxHook`, `SalesTaxDefaults` | `Application/Billing/InvoiceTaxHook.cs` |
| `IAssetSource`, `AssetRef`, `AssetPolicySummary` | `Application/Accounting/AssetSource.cs` |
| `IForeignBalanceSource` | `Application/Currencies/ForeignBalanceSource.cs` |
| `ICompanyRecordSource` | `Application/Companies/ICompanyRecordSource.cs` |
| `IBankAccountProbe`, `IValueListUsage`, `ModuleSeedStage.AfterProcurement` and `AfterSourcing`, `SetupSteps.CashAvailable` | setup, custom lists, `ModuleSeed.cs`, contracts |
| Through existing seams: 20 menu entries, 7 permissions, 5 API segments (+`vendor-bank`), 4 API key segments, 5 permission decorators and two company/re-authentication decorators, one `IBillLineGuard`, one importer, one report source row, three seeds, one hosted job | `ModuleContribution`, `IBillLineGuard`, `IImporter`, `IModuleSeed` |

## Data: existing installs keep their tables, new installs get them from the module

Same rule and recipe as the earlier modules: no table is dropped, no core migration row changes for an installed database, and the module can be added or left out of an upgrade at any time.
39 tables, 103 indexes and their foreign keys are inside the module. Its foreign keys point at 17 core tables that its context maps key-only (the ledger `GlAccounts`, `GlJournalEntries`, `GlJournalLines`; `FiscalPeriod`, `FiscalYear`; `Vendor`, `ApBill`,
`ApBillLine`, `ApPaymentRun`; `Invoice`, `Customer`, `Contract`, `Project`, `WbsElement`, `OrgUnit`, `Employee`, `Companies`). **No core table has a foreign key into the module's tables**, and no other module's table does either (see below).

The 39 tables: assets 6 (`FixedAsset`, `AssetCategory`, `AssetDepreciation`, `AssetDepreciationRun`, `AssetTransfer`, `AssetTaxBooks`), cash 9 (`BankAccounts`, `BankTransactions`, `BankStatementImports`, `BankMatches`, `BankMatchLines`, `BankReconciliations`,
`BankCloseWaivers`, `CashForecastItems`, `CashSettings`), bank files 6 (`BankAchSettings`, `PaymentFileDownloads`, `PositivePayLayouts`, `RemittanceAdvices`, `VendorBankAccounts`, `VendorBankChanges`), planning 9 (`GlBudgetVersions`, `GlBudgetLines`, `GlBudgetSubmissions`,
`GlBudgetSubmissionLines`, `GlPlanningCycles`, `GlRecurringTemplates`, `GlRecurringTemplateLines`, `GlRecurringRuns`, `GlRecurringSettings`), sales tax 9 (`TaxJurisdictions`, `TaxRates`, `TaxCodes`, `TaxCodeAssignments`, `SalesTaxSettings`, `CustomerTaxProfiles`,
`TaxExemptionCertificates`, `InvoiceTaxes`, `ApBillTaxes`).

1. **`ReleaseAccountingTables`** (core, both providers): the model no longer maps the entities. EF generated 39 `DropTable` statements; `Up` and `Down` are empty (`empty-release-migration.py`). Only the snapshot changes.
   `has-pending-model-changes` is clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL.
2. **Four old core migrations no longer create the tables** (`InitialCreate` 68 statements, `GoLiveReadiness` 10, `WaveAFinanceCore` 55, `WavesCeManufacturingProcurement` 95; the same four in both providers), with `strip-core-migration-tables.py` unchanged.
   Databases that already ran them are unaffected. None of the four had raw SQL on these tables.
3. **The module's `AccountingBaseline`** (first migration of `AccountingDbContext`, history `__EFMigrationsHistory_accounting`): 142 statements, each `CREATE ... IF NOT EXISTS`, `Down` empty. **Three column defaults a later core
   migration gave are written into the baseline by hand** (the previous release's databases have them): `AssetCategory.Convention` (`''`, both providers), and on PostgreSQL `FixedAsset.Convention` (`''`) and
   `FixedAsset.OpeningAccumulatedDepreciation` (`0.0`); the SQLite `FixedAsset` columns carry no default in the previous release (its table was rebuilt later) and the baseline matches. `CompanyId` columns carry the default through the model.
   The ledger entities map to tables named `GlAccounts`, `GlJournalEntries`, `GlJournalLines` and `Companies`, and `IndirectPoolSetup` to `IndirectPoolSetups`: the module's context says so.
4. **Load order and cross-module foreign keys.** The module's baseline references only core tables, which exist before any module runs, so the order among modules does not matter and `DependsOn` is empty. The one other-module reference found, workforce's
   `ChecklistTask.AssetId` to `FixedAsset`, was a foreign key: a new install with workforce and without accounting would have failed on it. It is **key-only now** (`HasIndex` instead of `HasOne`), and workforce's baseline (not released yet:
   the workforce extraction is an unmerged PR) was regenerated with `dotnet ef migrations remove` and `add`; it still has 84 statements and the same index. Its migration path test no longer compares that one foreign key. Existing installs keep the constraint (their tables are untouched).
5. **A host without the module** keeps the tables and rows after an upgrade (nothing creates or edits them) and runs the ledger, payables, ODCs, expense reports, currencies and exports as before. A host that adds the module later finds them.

| Database | Core | Module present | Module absent |
| --- | --- | --- | --- |
| Existing, current release | runs `ReleaseAccountingTables`: no table touched | the baseline finds the tables, changes nothing | tables and rows stay, unused |
| Existing, older release | runs the remaining core migrations (which no longer create the tables), then the release | the baseline creates the tables with the current schema | no tables, none needed |
| New | no module tables | the baseline creates them | none |

## Tests

| Shipped suite | Now |
| --- | --- |
| `Cash/*` (7 files with `CashKit`), `BankFiles/*` (3), `Assets/*` (7), `Planning/*` (4 with `PlanningKit`), `SalesTax/*` (1) | moved to the module (22 files); the sample file lookups read this repository's `samples/bank` through `WORKBENCH_CORE_DIR` |
| `GoldenPath/GoldenPathTests`: the bank reconciliation of the customer's receipt | removed (the module's `ReconciliationTests` cover reconciliation; the close checklist assertions stay) |
| `Identity/BuiltInRoleParityTests`: seven rows | moved to the module's `Permissions_are_in_the_catalog_and_held_by_the_same_roles_as_before` |
| `Identity/SignInPolicyTests`, `OrganizationSsoWebTests`: the bank account number as the sensitive action | now a vendor's tax id (`SensitiveActions.VendorTaxId`) |
| `Identity/CustomRoleTests`, `FinanceActionGatingTests` | `cash.manage` replaced by `gl.journals.post`; the asset service fake is gone |
| `Reports/ReportWriterTests` | 7 sources for Finance (the corporate budgets source is the module's) |
| `Licensing/*` | `accounting` joins the modules "no service maps to it in this build"; the seat tests count seats on billing and enterprise; the supplier portal feature belongs to core; the unlicensed page and the page guard examples use billing |
| `Navigation/NavMenuTests`, `Setup/SetupComponentTests` | no Cash, Assets or Planning groups ("Corporate cards" is under Payables & purchasing); the setup guide has 12 steps (no bank account step, no payroll step) |
| new in core: `Modules/AccountingAbsentTests` | a new database has none of the 39 tables and keeps the ledger, vendors, bills, currencies and indirect rates; the module's permissions, API segments, menu entries and services are absent while the ledger's are core; its API paths answer 404 and the ledger, payables, currencies, indirect rates and exports answer; core's hooks have no implementation |
| `Workforce` tests (module repository) | offboarding uses a fake `IAssetSource`; the migration path test ignores the one foreign key that is key-only now |
| `Materials` tests (module repository) | the purchase order service maps to core |
| new in the module | `MigrationPathTests` (a new install; the baseline equals the previous release's schema; an existing install keeps every table, row and column, gains only history rows, twice; an upgrade without the module keeps its data), `ModuleSurfaceTests` (permissions and roles, ModuleMap rows, menu, hooks, slots, importer, seeds, sample data), `ApiSmokeTests` |

## Documentation, demo data and public pages that describe the slice

Nothing was removed; whether the public site keeps describing a paid module is the owner's decision. Copies of three pages are in the module repository's `docs/` (`finance-cash.md`, `finance-assets.md`, `finance-planning.md`). The public references are:

* **The slice:** `docs/finance/cash.md`, `assets.md`, `planning.md`, `payables.md` (bank files, vendor bank details), `billing.md` and `revenue.md` (sales tax), `docs/finance/accounting.md` and `index.md`, `docs/admin-guide/accounting-export.md`, `organization.md`, `docs/multicompany.md` (bank accounts and
  fixed assets per company), `docs/integrations/costpoint.md` and `docs/migration/costpoint-cutover.md` (the fixed asset import), `docs/compliance/dcaa/sf1408.md`, `docs/customization.md`, `docs/security/permissions.md`, `docs/reference/glossary.md`, `docs/training/*`
* **Commercial and marketing:** `docs/commercial/modules-and-pricing.md` (the module's price line and the plan tables), `website-copy-suggestions.md`, `docs/editions.md`, `docs/feature-coverage.md`, `README.md`, `mkdocs.yml`, the marketing pages `site/index.html` and `site/costpoint-alternative.html`
* **In the application:** the help articles `bank-reconciliation`, `month-end-close` and `paying-bills`, `Help/Glossary.cs`, `WhatsNew.md` and the tours; `samples/bank/*` and `samples/import/costpoint/fixed-assets.csv` are the sample files (the module's tests read them from this repository)
* **Internal handover notes:** `docs/operations/cash-bankrec.md`, `fix-assets.md`, `purchasing-assets.md`, `bankfiles-tax-integration.md`, `taxbooks-cpsr.md`, `gl-budgets.md`, `multicurrency.md`, `multi-company.md`, `w2-ap-credits.md`, `module-extraction-audit.md` (still says what the audit measured)
* **The sample data:** the sample fixed assets, bank account and statement, corporate budgets and cycle, recurring journals, tax rates and the taxed NVDC invoice, the Nettle Systems vendor with its ACH details and bill, and the tax books exist only with the module. **A new database without it has
  3 ledger accounts fewer (2150, 2160, 6250), 7 journal entries fewer, and no NVDC customer, contract or invoice and no Nettle vendor or bill.** The three CPSR purchase orders remain.

## Verification

* Shipped repository, no `modules/` folder: `Workbench.Server.slnf` builds (the MAUI desktop project cannot be built on this machine; it does not reference the module); the filtered groups pass: Licensing, Modules, Navigation, Tenancy, Migration, Identity, Reports, Customization, Accessibility, GoldenPath,
  Dashboards, Accounting, Assets, Cash, BankFiles, SalesTax, Purchasing, Travel, Time, Setup and AccountingExport (**977 passed, 1 skipped**: the PostgreSQL numbering test; the Assets, Cash, BankFiles and SalesTax groups moved to the module and match nothing here).
* Module repository, module loaded from the built folder: its **141 tests** pass on InMemory and on SQLite (`WORKBENCH_TEST_DB=Sqlite`); the capture (213), materials (77, after one expectation changed), project controls (100) and workforce (126, after the offboarding and migration path tests changed) suites still pass against this core.
  `Workbench.Module.GovCon.Tests` does not compile against this core (it did not before either: `Workbench.Tests.Evm` and `MutableClock` left with the project controls kit).
* `has-pending-model-changes`: clean for core `WorkbenchDbContext` and `CatalogDbContext` on SQLite and PostgreSQL, and for the module's `AccountingDbContext` and the workforce module's `WorkforceDbContext` on both, checked with `dotnet ef` directly (`scripts/check-migrations.sh`), never with `scripts/new-migration.sh`.
* **Upgrade from the previous release's host** (`253f114`, the base of this change; published, run in Development so it seeds the sample company; 309 tables, 39 of them the module's), against this release's host (published without a `modules/` folder, and with `modules/accounting`):
  * the *upgrade* (the previous release's database, once with the module and once without; both hosts start with no `fail:` lines): only `ReleaseAccountingTables` ran in core, and `AccountingBaseline` with the module (history table `__EFMigrationsHistory_accounting`, one row);
    **every table's row count is identical, and the `CREATE` text, the indexes and every row of the 39 tables are identical** in both cases;
  * a *new* database made by this host **with** the module has the 39 tables identical to the previous release's (columns with type, nullability, default and key, foreign keys, indexes) and the same row counts in every table; **without** it the database has none of the 39 tables, the host starts,
    and the sample company is smaller by what the module seeds (above).
* Module tests on SQLite (`MigrationPathTests`): a new install; the baseline's schema equals the previous release's (columns with type, nullability and default, foreign keys and indexes, table by table, against a fixture dumped from the previous release's sample database); an existing install keeps every table, row and column,
  gains only history rows, twice; an upgrade without the module keeps its data.
* Not run: PostgreSQL itself (the private repository's CI should run `WORKBENCH_TEST_DB=PostgreSql` and a PostgreSQL upgrade; in particular the foreign keys to `GlAccounts`, `GlJournalEntries`, `GlJournalLines` and `Companies`, and the three hand-written column defaults), the full shipped suite (known to hang locally), the MAUI desktop build.

## Not done

* The worker and the desktop app do not load modules: the accounting pages and clients are not in the desktop app (they were), and the recurring journal job runs in the web host only (it always did).
* `Workbench.Module.GovCon.Tests` does not compile against this core (see above; unchanged).
* The golden path no longer reconciles the bank in the shipped suite; the module has no end-to-end test that pays an invoice and reconciles the receipt (the pieces are covered separately).
* The saved-data names (`CompanyRecordKind.BankAccount`/`FixedAsset`, the budget, planning and vendor bank approval document types, `hr-change`) stay in core's lists: without the module the Companies page still offers "Bank accounts" and "Fixed assets" and lists nothing, and the workflow list shows three types nobody can submit.
* PostgreSQL itself, the full shipped suite, the MAUI desktop build.

## Lessons for the next extractions (enterprise, billing)

* **Delete and compile; do not count references.** A name search found 1 to 4 real core files per area and about 400 false hits (`Allocation`, `Names`, `TaxRate`); compiling found about 45 places. Expect three rounds, because errors surface one project layer at a time (Contracts, Application, Infrastructure, then UI and tests).
* **Seeds carry core sample data.** `TaxBooksSeed` made three purchase orders that two core tests (a funding warning) depend on. Diff the sample database (row counts per table) between the previous release and the stripped host before trusting the tests: that is how the PO seed was found.
* **Other paid modules read the slice**: a list of three modules (assets) became one small optional interface in core (`IAssetSource`). Check `Workbench-Modules/src` for the slice's namespaces *before* the first build, and a key-only foreign key in another module means regenerating that module's baseline.
* **Defaults from later core migrations** are invisible in the model: scan the old migrations' `AddColumn` calls with a `defaultValue` for the moved tables. The migration path test's pragma comparison catches the SQLite ones; the PostgreSQL ones have to be read.
* **Internal helpers the module needs become public** (`AccountingMapping`, `LedgerLookups`, `EnsureDefaultAsync`): a short, boring list, but each is a contract now.
* **Scoped services in seeds**: a seed receives the root provider; resolve optional hooks through a scope (`CreateScope`).
* **Slots can carry tab panels**, so a core page that lists its tabs gets the module's tab last; say so in the price-line change.
* **Enterprise** will meet the same shapes: `ICompanyRecordSource` shows the pattern for "core lists records another module owns" (companies, access reviews, retention), `CompanyColumnConfiguration` has to be copied into the module (it is internal), and the company decorators move with the services they wrap.
* **Billing** (invoices and payments) is read by the accounting module's sales tax and by the exports: `IInvoiceTaxHook` and `SalesTaxDefaults` are the model for the one place the ledger asks a module for an account number.
