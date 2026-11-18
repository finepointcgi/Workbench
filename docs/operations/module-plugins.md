# License modules as plugins

A license module (billing, accounting, capture, workforce, project controls, materials, government contracting, enterprise) can live outside this repository as a set of assemblies. The host finds them in a folder at startup, loads the ones the license includes, and the product then behaves as if the module had always been built in: its services, API paths, pages, menu entries, permissions, API key scopes, tables and migrations all take part.

The `govcon` module was the first to move out ([module-extraction-govcon.md](module-extraction-govcon.md)), with its government billing ([module-extraction-billing.md](module-extraction-billing.md)); `capture` followed ([module-extraction-capture.md](module-extraction-capture.md)), then `materials` ([module-extraction-materials.md](module-extraction-materials.md)), `projectcontrols` ([module-extraction-projectcontrols.md](module-extraction-projectcontrols.md)) the HR and payroll part of `workforce` ([module-extraction-workforce.md](module-extraction-workforce.md); resourcing stays core) and the cash, bank files, fixed assets, corporate budgets and sales tax slice of `accounting` ([module-extraction-accounting.md](module-extraction-accounting.md); the ledger, payables and purchasing, currencies and indirect rates stay core) and the revenue recognition and live accounting push leaves of `billing` ([module-extraction-billing-leaves.md](module-extraction-billing-leaves.md); invoicing and receivables stay core); the other modules are still built in. With an empty modules folder the product is the shipped product, without government contracting, business development and proposal pricing, materials (inventory, manufacturing, quality, sales orders and shipping), project controls (earned value, ceilings and closeout, the risk register and the monthly narrative), HR and payroll (personnel records, benefits, payroll runs and the Paylocity exchange), and cash and bank reconciliation, bank payment files, fixed assets, corporate budgets and sales tax, and revenue recognition and the live push to QuickBooks and Xero. This page is the contract a module author builds against and the rules the host follows.

## What a module contributes

A module is a class that implements `IWorkbenchModule` (`src/Workbench.Modules.Abstractions`, the one assembly a module repository references besides the contracts). The host asks it for:

| What | Where in the interface | How the host uses it |
| --- | --- | --- |
| The license module id | `Id` (a `ModuleIds` value, such as `govcon`) | Checked against the license key before the assembly is loaded. |
| The `ModuleMap` rows | `Describe()`: `ApiSegments`, `PermissionPrefixes`, `NavSections`, `NavGroups`, `NavPages`, `ServiceInterfaces` | `ModuleMap.ForApiSegment`, `ForPermission`, `ForNav`, `ForService` read the built-in table and then these rows. |
| Service gates | `ServiceInterfaces` | `EditionGates` wraps every change on each interface like an in-repo service. |
| Menu entries | `Describe().NavEntries`, `NewNavSections` | `NavCatalog.Entries` and `Sections` are the built-in list plus these. Their module is the module that listed them. An entry may also open for a relationship (`AlsoWhenFlag`, an `INavAccessFlag` registered in `ConfigureServices`, for example "manages an opportunity"). |
| Permissions | `Describe().Permissions`, `PermissionAreas` | `PermissionCatalog.All`, `Find` and the roles page read the built-in catalog plus these; `GrantedTo` roles hold them. |
| API key scopes | `Describe().ApiKeyAreas`, `ApiKeySegments` | `ApiKeyScopes.Areas` and `Check` read the built-in areas plus these. A module may only open its own API paths to keys. |
| Blazor pages | `Describe().RoutableAssemblies` | Passed to the router and to `MapRazorComponents` as additional assemblies. |
| Services | `ConfigureServices(services, context)` | Runs after the core services and before the permission and edition decorators. |
| API endpoints | `MapEndpoints(api)` | `api` is the `/api/v1` group: already authorized, with the host's error mapping. |
| Edition feature gates | `Describe().FeatureGates` | Changes on the module's services that also need an edition feature (`ModuleFeatureGate`), applied with the edition gates. |
| Places on core pages | `UiSlotContribution` registered in `ConfigureServices` | A core page renders `<UiSlot Name="..."/>`; every contribution with that name is rendered there (slots: `timesheet.summary`, `setup.migration.actions`, `billing.contract.cards`, `billing.contract.summary`, `billing.invoice.panels`, `billing.invoice.print.links`, `billing.invoice.print.sf1034`, `resourcing.heatmap.panels`, `project.setup.more`, `contract.tabs`, `project.financials.cards`, `project.financials.actions`, `dashboard.widget.earned-value`, and the accounting module's `billing.invoice.tax`, `purchasing.bill.panels`, `purchasing.payment-run.panels`, `purchasing.vendor.panels`, `indirect-rates.tabs`, `accounting.reports.tabs`, and the billing module's `accounting-export.tabs` and `accounting-export.push`). A slot component declares every parameter its slot passes; a page whose slot is empty renders as if the module never existed. |
| Tabs on the project page | `ProjectHubTabContribution` registered as a singleton in `ConfigureServices` | The project page's tab bar and "More" menu list the built-in tabs and these (slug, label, a rule on the viewer's tab flags, position, "More"); the tab's component is rendered with `ProjectId`, `SheetId` and `Changed`. A route to a tab the host does not have lands on the Overview. |
| Reports hub pages | `StandardReportPage` registered in `ConfigureServices` | Listed under Reports as a page to open. |
| Sample data | `IModuleSeed` registered in `ConfigureServices` | Run by the development seed at its `ModuleSeedStage` (`AfterProcurement`, `AfterCash`, `AfterResourcing`, `AfterRevenue`, `AfterHr`, `AfterPlanning`, `AfterMulticompany`, `AfterTravel`, `AfterSourcing`, `AfterTaxBooks`, `End`), or at the end of the commercial demo dataset (`CommercialDemo`). |
| Rows in core catalogs | `Describe().Rows` (a list of `IModuleRow`) | Custom lists, built-in forms (and their layouts), document numbering, automation entities, integration events, approval document types, security event names, "+ New" items, report sources, chartable lists, home widgets. See [Catalog rows](#catalog-rows). |
| Home and dashboard widgets | `ModuleHomeWidget` row, `IHomeWidgetProvider` in `ConfigureServices` | Offered in the widget picker; computed with the viewer's access. |
| Assistant tools | `IAssistantToolContribution` in `ConfigureServices` | Offered to the in-app assistant and, with an API key area, over MCP. |
| Data reports | `IStandardReportContribution` in `ConfigureServices` | Listed, run, drilled into and exported like a built-in standard report. |
| Approval documents | `ModuleApprovalDocumentType` row, `IApprovalDocumentHandler` in `ConfigureServices` | The type is in the workflow list with its default workflow; the inbox decides through the handler. |
| Go-live and reconciliation | `MigrationStepContribution`, `IReconciliationSource`, `IImporter` in `ConfigureServices` | A checklist step; the module's side of a parallel-run report; importers the steps use. |
| Wrapping a host service | `ModuleServiceDecoration(service, decorator)` registered as a singleton in `ConfigureServices` | The host applies it right after copying the module's services, to the last registration of `service` the host already made (the module's own scratch collection cannot see it): the decorator takes the original as a constructor parameter, keeps its lifetime, and sits inside the permission and edition decorators. A service the host does not have refuses the module. Materials uses it to put goods received on part lines into stock. |
| Core readers | `IItemReader`, `IVendorReader` in `ConfigureServices` | Replace how core reads parts or vendors, once the module owns them. |
| Core data flows | an interface declared in core with a no-op default (`IInvoiceFinancing`, `IItemReader`, `IContractCeilingSource`) or with no registration at all (`IPricingConsistencySource`, `IVendorReturnSource`, `IBillLineGuard`, `IProjectAlertSource`, `ICeilingProximitySource`, and the accounting module's `IInvoiceTaxHook`, `IBillTaxHook`, `IAssetSource`, `IForeignBalanceSource`, `IBankAccountProbe`, `IValueListUsage` and `ICompanyRecordSource`, read as a list of sources or an optional dependency) | The module registers its implementation after the host's default; a host without the module keeps the default. |
| Static files | the module folder's `wwwroot` | Served at `/_content/<routable assembly name>`. |
| Entities | `ModelAssemblies`, `OnModelCreating` | Added to the host's EF model, so services use `Set<T>()` like core ones. |
| Migrations | `Migrations` | The module's own context, migrations assemblies and history table (below). |

## Where modules are found

The modules folder is `Modules:Folder` (default `modules`, relative to the application's folder). It holds one subfolder per module:

```text
modules/
  govcon/
    module.json            {"id":"govcon","assembly":"Acme.Workbench.GovCon.dll"}
    Acme.Workbench.GovCon.dll
    (its private dependencies)
```

`module.json` names the module (`id`), its main assembly (a file name in the same folder, never a path) and, only when the assembly has several module classes, the class (`type`).

## Load order and what is checked

For each module folder, in id order, the host does the following. A module that fails a step is skipped with a reason in the log; the rest still load.

1. **Read `module.json`.** A missing or malformed manifest, or an assembly name that is a path, is refused.
2. **Ask the license.** If the license does not include the id, the assembly is **not loaded at all**, and nothing of the module is registered. The log says so once, as a warning.
3. **Load the assembly** into the default load context. The module's folder resolves its private dependencies, but only for assemblies the host does not already have: a module can never replace a host assembly (it must be built against the same Workbench, EF Core and ASP.NET versions and not ship them).
4. **Find the module class** and check that its `Id` equals the manifest's and that its `ContractVersion` equals the host's `ModuleContract.Version`. A module built for another contract version is refused with a message to rebuild it.
5. **Dependencies.** `DependsOn` lists ids that must load first. Modules load in dependency order; a module whose dependency is missing or unlicensed is skipped.
6. **Validate `Describe()`.** Every row must be new and must belong to the module itself: an API path or permission prefix already mapped is a conflict, every permission must match one of the module's own prefixes, every menu entry and permission must resolve to the module, an API key area may only open the module's own paths, and `core` is never a plugin id.
7. **`ConfigureServices`** runs once, against a scratch collection. If it throws, nothing it registered is kept and the module is dropped (its menu, permissions and the rest go with it).
8. **`MapEndpoints`** runs while the API is mapped. If it throws, the module is dropped from the registry, though routes it had already mapped stay until the next restart.

The accepted modules become `ModuleRegistry.Current`, which `ModuleMap`, `PermissionCatalog`, `ApiKeyScopes` and `NavCatalog` read. With none loaded it is empty and those classes answer from their built-in tables alone.

### Failure behavior

* A module that cannot load never stops the host, unless it is listed in `Modules:Required` (a list of ids), in which case the host refuses to start and says why. Use that for a deployment that cannot run without the module.
* Every outcome is logged at startup, one line per module folder: `Loaded` (information), `NotLicensed` (warning), and `MissingAssembly`, `MissingDependency`, `Invalid` or `Failed` (error).
* A licensed module that is missing its assembly changes nothing else: its tables stay in the database, untouched, and its menu entries and API paths are simply absent.

## How a module is licensed

The license key decides which assemblies load. The host reads the install's key from configuration, `Modules:LicenseKey` or else `License:Key`, verifies it offline with the same public key as everywhere else, and loads a module only if the key lists its id. A WB1 key lists every module. An expired key counts until its grace period ends, like the edition does. `License:EditionOverride` set to `Business` (development and the public demo) includes every module.

Two limits to know:

* A key typed into Setup › License is stored per database and cannot be read before the host is built. A self-hosted install that adds a plugin module puts its key in configuration too. A hosted install uses the operator's key, which lists every module the operator runs.
* This decides what the *install* may run. Whether a given *tenant* may use a loaded module is unchanged: `EditionService`, the module gates and the menu still decide that from the tenant's own license or plan.

The key proves entitlement, not that an assembly is genuine. Modules are deployed by the operator, in a folder only the operator can write to, like any other part of the application. Signing module assemblies is a job for the private repository's packaging.

## Database: each module owns its migrations

Decision: **a module has its own `DbContext`, its own migrations assemblies (one per provider) and its own history table.** The host applies them at startup, right after the core migrations, to every database it migrates (the single database, and each tenant's), for both SQLite and PostgreSQL.

How it fits together:

* **Runtime model.** The module's entity configurations (`ModelAssemblies`) are applied to the host's `WorkbenchDbContext`, so the module's services read and write through `Set<T>()` like core ones. The EF model is cached per module set.
* **Migrations.** `Migrations` names the module's context type (a `DbContext` with a public constructor taking a non-generic `DbContextOptions`) and the assembly of its SQLite migrations and of its PostgreSQL migrations. The host builds the options for the configured provider, points them at that assembly and at the module's history table (`__EFMigrationsHistory_<module id>` unless the module says otherwise), and calls `Database.Migrate()`.
* **Core entities.** If a module's entity has a foreign key to a core entity, the module's own context maps that core entity with `ToTable("Name", t => t.ExcludeFromMigrations())`, so the module's migrations create their own tables and never core's.
* **Core is unaffected.** Core's migration projects and snapshots never see a module, so `dotnet ef migrations has-pending-model-changes` on the core contexts stays a valid drift check. Because the *runtime* model has extra entities, the host ignores EF Core's pending-model-changes warning, and only when a module adds entities.

Why not one shared history table, or the core context? One history table would tie every module's schema to core's migration order and snapshot: adding or removing a private module would make core's snapshot differ per customer. Separate histories let a module be added, upgraded or left out independently, at the price of one extra context, one extra migrations assembly pair and one extra history table per module with tables. A module that adds no tables needs none of it.

What the sample covers, and what it does not: the tests run a real module's migration on SQLite end to end (core, then module; the module's history is separate; applying again changes nothing). The PostgreSQL path differs only in the provider call and the assembly name, and is covered by the provider-selection code, not by a PostgreSQL test run here. The real extraction should run each module's `dotnet ef` for both providers in the private repository's CI.

## Seams for core catalogs and engines

These are what a module needs to *add to* core, rather than own a page or a table. All are additive: with no module loaded the product is exactly as before, a module that does not use them is unaffected, and `ModuleContract.Version` stays 1 (every new member is optional). Each was written once so the next module repeats nothing; the sample module (`tests/Workbench.SampleModule`, `SampleSeams.cs`) has one example of each, and `ModuleSeamTests` runs them licensed and unlicensed.

### Catalog rows

`ModuleContribution.Rows` is a list of `IModuleRow` (`Catalog` and `RowKey`). The built-in catalogs keep their own rows and read the loaded modules' rows after them (`RowCatalog<T>`, cached per module set). The row types are the ones in-repo rows already use:

| Catalog (`ModuleCatalogs`) | Row type | Read by |
| --- | --- | --- |
| `custom-list` | `ModuleCustomList(key)` | `CustomizableLists.All`: saved list views |
| `list-source` | `IListSource` (`ListSource<T>`) | `ListSources.All`: charts of list pages on home pages |
| `form` | `BuiltInForms.FormDefinition` | `BuiltInForms.All`: form layouts, required fields, the field catalog |
| `document-number` | `DocumentNumberType.For<T>(...)` | `DocumentNumberTypes.All`: Setup › Numbering |
| `automation-entity` | `AutomationEntity` | `AutomationEntities.All`: triggers, conditions, actions |
| `integration-event` | `WebhookEventInfo` | `WebhookEventCatalog.All`: webhook and connector subscriptions (the module raises the event with its own `IntegrationEventType` through the outbox, which needs no registry) |
| `approval-type` | `ModuleApprovalDocumentType(key, label, amountIsHours, defaultWorkflow)` | `ApprovalDocumentTypes.All`, `Label`, `AmountIsHours`, `ApprovalDefaults.For` |
| `security-event` | `ModuleSecurityEventType(key, description)` | `SecurityEventTypes.All` (the log itself takes any name) |
| `new-action` | `NewAction` | `NewCatalog.Actions`: the "+ New" menu and Ctrl+K |
| `report-source` | `ReportSource` | `ReportSource.All`: the report writer |
| `home-widget` | `ModuleHomeWidget` | the widget picker (below) |
| `status-set` | `StatusSetDefinition` | `StatusSetCatalog.All`: relabelable status sets and sub-statuses (Setup › Lists) |
| `list-only-entity` | `ModuleListOnlyEntity(entityType, fields)` | `BuiltInForms.ListOnly` and `BuiltInEntityFieldCatalog`: a record type that has a list page and custom fields but no form of its own (sales orders) |
| `project-cost-source` | `ProjectCostSource(key, loadAsync)` | `ProjectCostSources.LoadAsync`: cost a module charges to projects, counted as direct cost like an ODC by the financials and the indirect-rate base (material issued from inventory) |

Every row's key must be new in its catalog. The host checks that against the built-in rows (`BuiltInRowKeys.All` in `Workbench.Web`, passed as `ModuleHostOptions.BuiltInRowKeys`) and against earlier modules, and refuses the module with a message otherwise; a blank key is refused too. Rows carry no module id of their own: a row belongs to the module that listed it.

### Widgets, assistant tools, reports

* **Home and dashboard widgets.** List a `ModuleHomeWidget(key, title, description)` row and register an `IHomeWidgetProvider` with the same key. A tile type that is stored on saved pages and stays in core (`KpiKind.Pipeline`) is answered by the provider registered under a fixed key (`kpi.pipeline`) instead of a row; without one the tile says it comes from a module that isn't installed. The picker offers "Module widget" only when a module has listed one; the widget is stored as `WidgetType.Module` with `Config.ModuleKey`. The provider gets the widget and a seed `WidgetDataDto` and returns the seed with one payload set (`Kpi`, `Chart`, `Metric` or `Markdown`) or an `Error`; it runs as the viewer, through the module's permission-checked services. A widget saved while the module was licensed shows "comes from a module that isn't installed" if it is later removed.
* **Assistant tools.** Register an `IAssistantToolContribution`: a `Spec` (build it with `AssistantToolbox.Spec(...)`), an optional `McpArea` (an API key area; without one the tool is for the in-app assistant only) and `RunAsync(JsonElement, IServiceProvider, ct)`. A tool must keep the built-in rules, or it is not offered: a name `list_…`, `get_…` or `read_…`, and a schema with `additionalProperties: false`. Unknown arguments are refused before `RunAsync`; permission, validation and not-found exceptions become error results, like built-in tools. Built-in tools are never replaced.
* **Data reports.** Register an `IStandardReportContribution` (key, name, category, parameters, `Unavailable(access)`, `RunAsync(builder, access, parameters, ct)`, optional `DrillAsync`). It goes through the same authorization, company wording and file export as a built-in report. `StandardReportPage` is still the entry for a page that opens elsewhere.

### Approvals, reconciliation, migration, readers

* **Approvals.** A document type is a `ModuleApprovalDocumentType` row plus an `IApprovalDocumentHandler` in `ConfigureServices` (handlers were already contributed that way). The module builds its own `ApprovalSubject` when it submits. The default workflow is the row's `DefaultWorkflow`, else one step for the administrators. Not covered: `ApprovalBackfill` (routing documents that were pending before workflows existed) only knows the built-in types, which is right for a module added after workflows.
* **Reconciliation.** `IReconciliationSource` answers for one existing `ReconReportKind` (the enum is stored in the database, so a module reuses the kinds that exist: for example billing the AR aging and revenue kinds) and replaces the built-in source, so core never calls the module's services. The report's fields stay in `ReconciliationReports`.
* **Import and migration.** Importers were already contributed (`IImporter` in `ConfigureServices`; `ImportKind` is also an enum, so a module uses existing kinds). `MigrationStepContribution` (a singleton) adds a go-live checklist step after the step it names.
* **Readers.** Core reads parts through `IItemReader` (sourcing, the supplier portal, EDI, AP credits) and vendors through `IVendorReader` (card programs) in `Workbench.Application.Purchasing`. `IItemReader`'s default (`NoItemReader`) knows no items, because parts belong to the materials module, which registers its own (and answers `ReadForPurchaseOrderLinesAsync`, the part a PO line buys through its purchase link); `IVendorReader`'s default reads the built-in vendors.  a module that owns the data registers its own after the host's (the last registration wins). Only the places the audit named were moved. Anything else that reads parts or vendors is a module reading the data it needs, which is the allowed direction.

### The leave kernel

The workforce extraction ([module-extraction-workforce.md](module-extraction-workforce.md)) took the first shape this section used to propose: the records the leave engine reads stay core tables. `Holiday` was already core; `PayGroup`, `PayPeriod` and `EmployeePayProfile` (with `PayFrequency`, `PayType` and `EmploymentType`) are in `Domain/Payroll/PayCalendar.cs`, and the payroll module creates and edits them through its own services. `LeaveEngine.AccrueAsync` and `ReverseAccrualAsync` kept their signatures (the module's payroll run service calls them, so `LeaveEngine` is public). The leave policy page lists pay groups through the core `IPayGroupChoiceService`. A host without the module has no pay groups and leave runs as for people with no pay profile. A module that owns *more* than core's engine reads from a table should do the same: leave the table core's, let the module write it.

## Writing a module

1. Reference `Workbench.Modules.Abstractions` and `Workbench.Contracts` (and Domain/Application for what the services need) with `ExcludeAssets="runtime"`/`Private=false`, so the module never ships a second copy.
2. Implement `IWorkbenchModule`. Put the module's public service interfaces (`I…Service`) in `Describe().ServiceInterfaces` (`ModuleContribution.ServiceInterfacesIn(assembly)` finds them), and its permission keys under a prefix of its own.
3. Publish to `modules/<id>/` with a `module.json`.
4. `tests/Workbench.SampleModule` is a complete small example: a service, an endpoint, a menu entry, a permission, an API key area, an entity and a migration, and one example of each seam above (`SampleSeams.cs`).
5. A module that uses the Ui types in rows (`NewAction`, `IListSource`) references `Workbench.Ui` too, compile-only like the other core assemblies.

## Not in this change

* Only `govcon` (government billing included, through the `IInvoiceFinancing` hook), `capture`, `materials`, `projectcontrols`, the HR and payroll part of `workforce` and the cash, bank files, fixed assets, corporate budgets and sales tax slice of `accounting` ([module-extraction-accounting.md](module-extraction-accounting.md)) and the revenue recognition and live push leaves of `billing` ([module-extraction-billing-leaves.md](module-extraction-billing-leaves.md)) have moved; the other in-repo modules still register through `AddWorkbenchApplication`. The analysis of `billing` is in [module-extraction-billing.md](module-extraction-billing.md); what is left of it (the invoice and payment services) is phase 3b there.
* The worker (`Workbench.Worker`) does not load modules yet; background jobs a module owns need it to. A module registers a hosted service in `ConfigureServices` and it runs in every host that loads the module (the web host; the capture module's SAM.gov search is the first).
* The desktop app reads routable pages from the same registry but does not load modules.
* Reading a license key stored in the database at startup, and signing module assemblies.

## Extraction lessons

From extracting `govcon` ([module-extraction-govcon.md](module-extraction-govcon.md)); the order and effort for the remaining modules are in the pull request report.

* **The seam held for services, endpoints, menu, permissions, API key scopes, entities, migrations and pages.** What needed new extension points was *core pages and core data flows that hosted the module's pieces*: the timesheet header, a button on a setup page, the Reports hub, the sample data, edition feature gates.
* **The expensive coupling is never the module's own files; it is a core engine that calls the module.** Here: GL posting costs labor through total time accounting (the engine stayed), and invoicing calls government financing at ten places (government billing stayed). Look for these first: grep core for the module's namespaces from `Application`, not from `Ui`.
* **Contract namespaces of a moved module must not be core namespaces** or `ModuleMap` assigns them to core.
* **`internal` helpers shared by two modules have to become a real public API**; the first extraction already widened seven types.
* **Migration ownership is a per-module decision made once.** The recipe used here (release ownership with an empty core migration, edit the old creating migrations, idempotent module baseline, compare the previous release's DDL with the baseline) works for any module whose tables only reference core tables.
* **Tests that use core kits need the kits' internals** (`InternalsVisibleTo`) and the module loaded before the first test (a module initializer).
* **When a core engine needs a module's rules, declare the hook in core and give it a no-op default.** Invoicing asked ten questions of government financing; they became one interface with eight methods (`IInvoiceFinancing`, [module-extraction-billing.md](module-extraction-billing.md)), and the shipped billing suite now runs against the default.
* **Keep the records, move the rules.** The receivables ledger (invoices, payments) is read by dozens of core features, so it stays in core even when the *service* that creates it is a paid module. Decide the kernel (records, posting, shared helpers) before moving an engine.
* **UI hosts become slots; a hint beats a service call.** A core page that needs to know "which form does this contract print on" gets it as a field in the data it already loads (`InvoicePrintDto.PreferredForm`), not by injecting the module's service.

From extracting `capture` ([module-extraction-capture.md](module-extraction-capture.md)):

* **Count the catalogs that name the module's records, not the `using` lines.** The audit's 7 core files were about 35: forms, lists, numbering, automation entities, webhook events, approval types, status sets, nav rules, KPI tiles, importers, seeds. The catalog rows made most of them one-line moves.
* **Build every moved module against the stripped core before deleting.** A module can read another's tables (govcon's CAS check read proposal pricing); the answer is a source interface in core with no-op behavior, not a module-to-module reference.
* **A module's DTOs keep their masking.** `FieldMasker` finds `SensitiveField` in any `Workbench.Contracts*` type; a module's contracts keep those namespaces.
* **The scoped styles of a module's pages are linked by the host** (`ModuleHost.StyleSheets`); the build script copies the bundle into the module's `wwwroot`.
* **Registration order is behavior**: service, form requirement guard, permission decorators, then the host's edition gates.

From extracting `materials` ([module-extraction-materials.md](module-extraction-materials.md)):

* **A module can need to wrap a host service, not only add its own.** Materials puts goods received on part lines into stock by decorating the purchase order service; the scratch collection a module registers into cannot see the host's services, so `ModuleServiceDecoration` asks the host to do it.
* **Accounting reads the module too, in places the audit did not list.** Payables turned a quality return into an AP debit memo and refused to void a bill whose line a landed-cost charge used (`IVendorReturnSource`, `IBillLineGuard`); the project financials and the indirect-rate base counted material issued to projects (`ProjectCostSource`). Each became a small interface in core that the module fills and a host without it leaves empty.
* **A core table with a foreign key into the module's tables has to let go of it.** `RfqLine.PartId` pointed at the part table: the column stays (key only), the foreign key is taken out of the old migration that created it, and installs that already have it keep it.
* **Sample data crosses modules too.** A core sample (sourcing) used a vendor the materials sample made; core now makes it if it is missing, and the module's seeds run at two new stages (`AfterHr`, `AfterTravel`) so they keep their place in the order.

From extracting `projectcontrols` ([module-extraction-projectcontrols.md](module-extraction-projectcontrols.md)):

* **A page that lists its tabs from a static array needs a contribution, not just a slot** (`ProjectHubTabContribution` carries the label, the access rule and the position); a page built from `MudTabPanel`s can take a slot whose component renders the panels itself (`contract.tabs`).
* **A saved enum member stays in core; its computation moves.** A dashboard widget type stored on saved dashboards (`WidgetType.EarnedValue`) is computed by the module's `IHomeWidgetProvider` and its data travels in `WidgetDataDto.ModulePayload`.
* **Enforcement that core never called needs no hook.** The ceiling and closed checks were decorators of the time, ODC, expense, AP, purchase order, requisition and invoice services, so `ModuleServiceDecoration` carried them; the questions core *does* ask (a project's ceiling alerts, the hour ceilings at project start, the hours left on a timesheet) are interfaces with empty defaults.
* **A service interface in a namespace core already maps cannot be claimed by a module**: move it to a namespace of the module's own before listing it.
* **A column a later core migration added with a default must carry that default in the module's baseline**, or new and upgraded databases differ.
