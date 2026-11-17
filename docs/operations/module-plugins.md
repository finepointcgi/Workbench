# License modules as plugins

A license module (billing, accounting, capture, workforce, project controls, materials, government contracting, enterprise) can live outside this repository as a set of assemblies. The host finds them in a folder at startup, loads the ones the license includes, and the product then behaves as if the module had always been built in: its services, API paths, pages, menu entries, permissions, API key scopes, tables and migrations all take part.

The `govcon` module was the first to move out ([module-extraction-govcon.md](module-extraction-govcon.md)); the other modules are still built in. With an empty modules folder the product is the shipped product, without government contracting. This page is the contract a module author builds against and the rules the host follows.

## What a module contributes

A module is a class that implements `IWorkbenchModule` (`src/Workbench.Modules.Abstractions`, the one assembly a module repository references besides the contracts). The host asks it for:

| What | Where in the interface | How the host uses it |
| --- | --- | --- |
| The license module id | `Id` (a `ModuleIds` value, such as `govcon`) | Checked against the license key before the assembly is loaded. |
| The `ModuleMap` rows | `Describe()`: `ApiSegments`, `PermissionPrefixes`, `NavSections`, `NavGroups`, `NavPages`, `ServiceInterfaces` | `ModuleMap.ForApiSegment`, `ForPermission`, `ForNav`, `ForService` read the built-in table and then these rows. |
| Service gates | `ServiceInterfaces` | `EditionGates` wraps every change on each interface like an in-repo service. |
| Menu entries | `Describe().NavEntries`, `NewNavSections` | `NavCatalog.Entries` and `Sections` are the built-in list plus these. Their module is the module that listed them. |
| Permissions | `Describe().Permissions`, `PermissionAreas` | `PermissionCatalog.All`, `Find` and the roles page read the built-in catalog plus these; `GrantedTo` roles hold them. |
| API key scopes | `Describe().ApiKeyAreas`, `ApiKeySegments` | `ApiKeyScopes.Areas` and `Check` read the built-in areas plus these. A module may only open its own API paths to keys. |
| Blazor pages | `Describe().RoutableAssemblies` | Passed to the router and to `MapRazorComponents` as additional assemblies. |
| Services | `ConfigureServices(services, context)` | Runs after the core services and before the permission and edition decorators. |
| API endpoints | `MapEndpoints(api)` | `api` is the `/api/v1` group: already authorized, with the host's error mapping. |
| Edition feature gates | `Describe().FeatureGates` | Changes on the module's services that also need an edition feature (`ModuleFeatureGate`), applied with the edition gates. |
| Places on core pages | `UiSlotContribution` registered in `ConfigureServices` | A core page renders `<UiSlot Name="..."/>`; every contribution with that name is rendered there (slots: `timesheet.summary`, `setup.migration.actions`). |
| Reports hub pages | `StandardReportPage` registered in `ConfigureServices` | Listed under Reports as a page to open. |
| Sample data | `IModuleSeed` registered in `ConfigureServices` | Run by the development seed at its `ModuleSeedStage`. |
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

## Writing a module

1. Reference `Workbench.Modules.Abstractions` and `Workbench.Contracts` (and Domain/Application for what the services need) with `ExcludeAssets="runtime"`/`Private=false`, so the module never ships a second copy.
2. Implement `IWorkbenchModule`. Put the module's public service interfaces (`I…Service`) in `Describe().ServiceInterfaces` (`ModuleContribution.ServiceInterfacesIn(assembly)` finds them), and its permission keys under a prefix of its own.
3. Publish to `modules/<id>/` with a `module.json`.
4. `tests/Workbench.SampleModule` is a complete small example: a service, an endpoint, a menu entry, a permission, an API key area, an entity and a migration.

## Not in this change

* Only `govcon` has moved (and not its government billing); the other in-repo modules still register through `AddWorkbenchApplication`.
* The worker (`Workbench.Worker`) does not load modules yet; background jobs a module owns need it to.
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
