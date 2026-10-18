# Workbench

A Smartsheet / Asana-style work management app with native Deltek Costpoint integration.
Built with .NET 10, Blazor and MudBlazor. Runs in the browser and as a desktop app.

Product and technical plan: https://claude.ai/code/artifact/6cda230a-5649-42ac-9dc0-5ec505da3921

## Layout

| Project | Purpose |
| --- | --- |
| `src/Workbench.Web` | ASP.NET Core host: Blazor (Interactive Server), REST API, static assets |
| `src/Workbench.Ui` | Razor class library with all pages and components, shared by web and desktop |
| `src/Workbench.Desktop` | .NET MAUI Blazor Hybrid shell for Windows and macOS (thin client over the API) |
| `src/Workbench.Contracts` | DTOs and service interfaces the UI depends on (`ISheetService`, ...) |
| `src/Workbench.Application` | Use cases implementing the contracts in-process |
| `src/Workbench.Domain` | Entities: workspace, sheet, column, item |
| `src/Workbench.Infrastructure` | EF Core (SQLite for dev, PostgreSQL for deployments) |
| `src/Workbench.Api` | Minimal API endpoints (`/api/v1/...`) mapped by the web host |
| `src/Workbench.ApiClient` | HTTP implementations of the contracts, used by the desktop app |
| `src/Workbench.Costpoint` | `ICostpointSource` adapter interface and Costpoint record types |
| `src/Workbench.Worker` | Background service for Costpoint sync and automations |
| `tests/Workbench.Tests` | xUnit + bUnit tests |

The UI never references EF Core. The web host binds the contracts to `Workbench.Application`;
the desktop app binds them to `Workbench.ApiClient`, so both run the same components.

## Run

Web (creates and seeds `workbench.db` in Development):

```bash
dotnet run --project src/Workbench.Web --launch-profile https
```

Build and test everything except the desktop app:

```bash
dotnet test Workbench.Server.slnf
```

Desktop: start the web host first, then build on the matching OS. macOS needs full Xcode;
Windows needs the Windows App SDK. Set `WORKBENCH_SERVER_URL` to point at another server
(default `https://localhost:7154/`).

```bash
dotnet build src/Workbench.Desktop -t:Run
```

## Configuration

- `Database:Provider`: `Sqlite` (default) or `PostgreSQL`
- `ConnectionStrings:Workbench`: connection string for the chosen provider
- `Costpoint:Source`: `None` (default), `Csv`, `Rest` or `Database` (see `src/Workbench.Costpoint/README.md`)
- `Costpoint:Csv:Folder`: folder with the Costpoint CSV exports (Development uses `samples/costpoint`)
- `Costpoint:SyncInterval`: worker sync interval (default 24 hours)
- `Email:Mode`: `Pickup` (write `.eml` files to `Email:PickupFolder`, default `App_Data/mail`; Development default) or `Smtp`
  (`Email:Smtp:Host`, `Port`, `EnableSsl`, `UserName`, `Password`); `Email:From` sets the sender
- `Notifications:PublicBaseUrl`: makes links in emails absolute; `Notifications:DigestHourLocal`: daily digest hour (default 7)
