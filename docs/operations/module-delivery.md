# Delivering the paid modules to a customer

The eight paid modules (`govcon`, `billing`, `capture`, `materials`, `projectcontrols`, `workforce`, `accounting`, `enterprise`) are not in this repository's build. They are built in a separate private repository ("Workbench modules") and reach an install as folders in the host's `modules/` directory. This page is for the operator who delivers them. The loader and the contract are in [module-plugins.md](module-plugins.md).

## What a customer gets

One folder per module, `modules/<id>/`, produced by the private repository's `scripts/build-all.sh`:

```text
modules/capture/
  module.json                         {"id":"capture","assembly":"Workbench.Module.Capture.dll","contractVersion":1,"core":">=1.0.0 <2.0.0","version":"1.0.0"}
  Workbench.Module.Capture.dll        the module, its contracts, its pages and its two migrations assemblies (SQLite, PostgreSQL)
  Workbench.Module.Capture.Contracts.dll, ...Ui.dll, ...Migrations.Sqlite.dll, ...Migrations.PostgreSql.dll
  wwwroot/                            static files and the scoped styles of the pages
```

A release is a tag in the private repository. Its GitHub release holds one zip per module (`workbench-module-<id>-<version>.zip`, containing `<id>/...`), the bundle `workbench-modules-<version>.zip` (containing `modules/<id>/...` for all eight) and `SHA256SUMS`. **A module must be built against the same Workbench release as the host runs** (the contract version, EF Core and ASP.NET versions): the release says which core ref it was built against, and `module.json` carries the contract version and the core range it was tested with. The host refuses a module built for another contract version, with a message to rebuild it.

## Self-hosted

1. Get the zip for the modules the customer bought (from the private release, or from a private feed the operator mirrors them to) and check it against `SHA256SUMS`.
2. Unzip it next to the application: the host reads `modules/` beside its binaries (`Modules:Folder` changes that; in the container image, mount a volume at `/app/modules`). Dropping the folder in is the whole install.
3. Put the customer's license key in configuration: `Modules:LicenseKey` (or `License:Key`). A key typed into Setup › License is stored per database and cannot be read before the host starts, so it does not load modules. The key must list the module's id; a module the key does not list is skipped **before its assembly is loaded**.
4. Restart. The log has one line per folder (`Loaded`, or a warning that the key does not include it, or an error with the reason), and one warning for every module the key lists that has no folder: `Module X is in the license but is not installed ...`. The host applies the modules' migrations after core's, to every database it migrates.

To add a module later, drop its folder in and add it to the key (the license server issues a new key on a seat or module change; the connected install refreshes it, and the key in configuration is the operator's to update). To remove one, delete the folder: its tables and data stay in the database untouched, and its menu entries and API paths are simply absent. `Modules:Required` (a list of ids) makes the host refuse to start when a module is missing or fails to load, for a deployment that cannot run without it.

Upgrades: replace the host and the module folders **together** from the same release.

## Hosted

The operator runs one host (or several) with its own `modules/` folder holding every module the hosted plans sell, and **one** license key in the host's configuration that lists them all (the operator's key). Which *tenant* may use a loaded module is decided per tenant from its plan (`EditionService` and the module gates), not from the folder: a tenant whose plan lacks a module sees none of its menu entries and its services refuse changes, once the module is *enforced* in `ModuleCatalog` (today only `govcon` and `enterprise`, and only for their named features; the others ship open to every tenant of a host that has them, until the price list is decided). A host without a module's folder has nothing of it for any tenant.

The Worker (`Workbench.Worker`, scheduled jobs for hosted tenants) and the desktop app do not load modules yet; background jobs a module owns run in the web host.

## How the license server fits

The license server (`src/Workbench.LicenseServer`) sells and signs the keys; the host verifies them offline. The module ids it can write into a key are `Modules.All` there: the eight above and `ai` (reserved, loads nothing). That list must equal `ModuleCatalog` minus `core`; a test (`The_module_ids_the_server_sells_are_the_catalogs_paid_modules...`) fails when either changes alone. WB1 keys list every module; WB2 keys list the ids they carry; the retired ids `finance` (now `billing` + `accounting`) and `controls` (now `enterprise`) in an old key are expanded when it is read, and the server refuses to issue them. The private repository's `Workbench.Modules.Licensing.Tests` runs all of this against the real built modules: an unlicensed module's assembly is not in the process, a listed module without a folder starts cleanly, a missing assembly is reported and the rest load.

Selling a module is therefore: add the Stripe price line to the license server's configuration, put the module's folder in the customer's `modules/`, and let the key list its id.
