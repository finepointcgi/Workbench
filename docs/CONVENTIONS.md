# Workbench conventions

Read this before adding a feature. Several sessions build features in parallel on separate
branches, so these rules keep merges small and predictable.

## Adding a feature end to end

1. **Domain** (`src/Workbench.Domain`): new entities in their own file (e.g. `Domain/Comments/Comment.cs`).
   Derive from `Entity` (v7 GUID ids). Use `DateTime` UTC fields named `*Utc` (SQLite can't order by `DateTimeOffset`).
2. **Persistence** (`src/Workbench.Infrastructure/Persistence/Configurations`): one
   `IEntityTypeConfiguration<T>` per entity in a new file. They are discovered automatically.
   Do **not** edit `WorkbenchDbContext.cs` or add properties to `IWorkbenchDbContext`; use `db.Set<T>()`.
3. **Contracts** (`src/Workbench.Contracts/<Feature>/`): DTO records, request records and a service
   interface. The UI only ever depends on these. Throw `WorkbenchNotFoundException` for missing things and
   `ArgumentException` for invalid input.
4. **Application** (`src/Workbench.Application/<Feature>/`): the in-process implementation. Use
   `IWorkbenchDbContextFactory` and create one context per operation (`await using var db = ...`).
   Register it with **one line** in `Application/DependencyInjection.cs`.
5. **API** (`src/Workbench.Api/<Feature>Endpoints.cs`): `internal static void Map<Feature>Endpoints(this RouteGroupBuilder v1)`,
   plus **one line** in `WorkbenchApi.cs`. The group filter already maps not-found → 404 and argument errors → 400.
6. **API client** (`src/Workbench.ApiClient/Http<Feature>Service.cs`): implement the same interface over HTTP using
   `ApiResponses.ReadAsync` / `EnsureOkAsync`, plus **one line** in `ApiClient/DependencyInjection.cs`.
   This is what the desktop app uses, so every UI-facing interface needs one.
7. **UI** (`src/Workbench.Ui`): components inject contract interfaces only, never EF Core or Application types.
8. **Tests** (`tests/Workbench.Tests/<Feature>/`): service tests with `TestDb.CreateFactory()`; component tests with bUnit
   (`await using var ctx = new BunitContext(); ctx.JSInterop.Mode = JSRuntimeMode.Loose; ctx.Services.AddMudServices();`).

## Sheet views

- Views live in `src/Workbench.Ui/Views/` and inherit `SheetViewBase` (`Sheet`, `Items`, `Changed`).
- Edit through `IItemService` / `IColumnService`, then `await Changed.InvokeAsync()` so the page reloads.
- Put view-specific subcomponents in their own folder (`Ui/Grid`, `Ui/Board`, `Ui/Calendar`, `Ui/Gantt`).
- To open an item's details panel from any view, navigate to `sheets/{sheetId}?item={itemId}`.
- Cell values are invariant strings keyed by column id: Number/Currency `1234.5`, Date `yyyy-MM-dd`,
  Checkbox `true`/`false`, Dropdown the option text, Person an email, CostpointLookup the Costpoint id.

## MudBlazor 9 notes

- `MudText` uses `HtmlTag="h1"` (not `Component`). `MudTabs` uses `TabPanelsClass` (not `PanelClass`).
- Avoid `Color.Secondary` for quiet text (it is pink); use `Class="mud-text-secondary"`.
- Dialogs: `[CascadingParameter] IMudDialogInstance MudDialog`, `DialogService.ShowAsync<T>()`, `await dialog.Result`.
- Treat MudBlazor analyzer warnings (`MUD0002`) as errors: they flag removed parameters.

## Build and verify

```bash
dotnet test Workbench.Server.slnf
dotnet run --project src/Workbench.Web --launch-profile http   # http://localhost:5104
```

`Workbench.Server.slnf` excludes the desktop app, which needs Xcode (macOS) or Windows to build.
The dev database is `src/Workbench.Web/workbench.db` (SQLite, created and seeded on start). Delete it after
schema changes; migrations come later.

## Shared files

Only append to these, one line per feature, never reorder: `Application/DependencyInjection.cs`,
`ApiClient/DependencyInjection.cs`, `Api/WorkbenchApi.cs`, `Ui/Layout/NavMenu.razor`, `Ui/_Imports.razor`.
Don't change existing contracts in `Workbench.Contracts/Sheets`; if a feature needs more, add new records or
interfaces next to them and say so in the commit message.
