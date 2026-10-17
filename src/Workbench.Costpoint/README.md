# Workbench.Costpoint

Read-only adapters for Deltek Costpoint. Costpoint is the system of record: Workbench mirrors projects,
WBS, employees, labor categories, budgets and labor actuals, and never writes anything back.

`ICostpointSource` is the port. `Workbench.Application` (`CostpointSyncService`) pulls from it and upserts the
`Cp*` mirror tables. This project depends on nothing else in Workbench.

## Choosing an adapter

| `Costpoint:Source` | Adapter | Status |
| --- | --- | --- |
| `None` (default) | `NullCostpointSource` | Returns no data |
| `Csv` | `CsvFolderCostpointSource` | File exchange, implemented |
| `Rest` | `RestCostpointSource` | Stub: throws `NotSupportedException` (no Deltek API docs yet) |
| `Database` | `DatabaseViewCostpointSource` | Stub: throws `NotSupportedException` (no view definitions yet) |

Other settings:

- `Costpoint:Csv:Folder`: folder holding the CSV exports. A relative path is looked up from the current
  directory and then each parent, so `samples/costpoint` works from the repo root or a project folder.
- `Costpoint:SyncInterval`: how often `Workbench.Worker` syncs (default `1.00:00:00`).

## CSV exports

Put six UTF-8 files in the folder. Each has a header row; columns are matched by name (case-insensitive,
any order) and extra columns are ignored. Fields follow RFC 4180 (quote values containing commas).

- A **missing file** or a **missing required column** fails the whole sync, so a half-delivered export
  never empties the mirror.
- A **bad row** (missing required value, bad number, date or period, wrong field count) is skipped. It is
  logged and counted as "skipped" on the sync run.

Formats: dates `yyyy-MM-dd`, fiscal periods `yyyy-MM`, numbers invariant (`1234.5`, no currency symbols or
thousands separators), booleans `Y`/`N`, `true`/`false` or `1`/`0`.

### projects.csv

| Column | Required | Notes |
| --- | --- | --- |
| `project_id` | yes | Costpoint `PROJ_ID`, e.g. `1001.01` |
| `name` | yes | |
| `org_id` | | Owning organization |
| `start_date`, `end_date` | | `yyyy-MM-dd` |
| `active` | | Default `Y` |

### wbs.csv

| Column | Required | Notes |
| --- | --- | --- |
| `project_id` | yes | |
| `wbs_id` | yes | WBS element / charge code, e.g. `1001.01.01` |
| `parent_wbs_id` | | Blank for top-level elements |
| `name` | yes | |
| `level` | | Default `1` |
| `charge_allowed` | | Default `Y` |

### employees.csv

| Column | Required | Notes |
| --- | --- | --- |
| `employee_id` | yes | |
| `name` | yes | |
| `email` | | Matches Workbench Person columns |
| `org_id` | | Home organization |
| `labor_category_id` | | |
| `active` | | Default `Y` |

### labor_categories.csv

| Column | Required |
| --- | --- |
| `labor_category_id` | yes |
| `name` | yes |

### budgets.csv

| Column | Required | Notes |
| --- | --- | --- |
| `project_id` | yes | |
| `wbs_id` | | Blank for a project-level budget |
| `fiscal_period` | | `yyyy-MM`, or blank / `TOTAL` for a whole-life budget |
| `hours` | yes | |
| `amount` | yes | Dollars |

### actuals.csv

| Column | Required | Notes |
| --- | --- | --- |
| `project_id` | yes | |
| `wbs_id` | yes | |
| `fiscal_period` | yes | `yyyy-MM` |
| `hours` | yes | Labor hours |
| `cost` | yes | Burdened labor cost, dollars |

Rows with the same project, WBS and period (in budgets or actuals) are summed, so a per-employee export
works as well as a summarized one.

Samples matching the seeded "Integrated Master Schedule" sheet are in `samples/costpoint/`.
