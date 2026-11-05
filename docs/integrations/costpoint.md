# Costpoint integration

Workbench replaces Deltek Costpoint, but a customer doesn't switch overnight. This page says what Workbench does with
Costpoint today, what was added for a real conversion and a parallel month, and what is deliberately not built.
There is **no live Costpoint connection**: Costpoint hands over Excel/CSV reports, and everything below reads those files.

## What already existed

| Area | Where | What it does |
|---|---|---|
| Importers (Setup › Import) | `Application/Imports/Costpoint` | Organizations, labor categories, employees, cost rates, customers, contracts, CLINs and funding, projects with WBS, bill rates, budgets/EACs, fixed assets, **chart of accounts**, **vendors**, **open AP bills**, **open AR invoices** and the **GL opening trial balance** (account × org × project, balanced to the penny, one opening entry). Every import: upload → column mapping with Costpoint's column names pre-filled → validate → dry run in plain words → import → roll back; audited, repeatable on natural keys. |
| Go-live checklist | Setup › Go-live migration (`MigrationService`) | The steps in dependency order with status from the import history, prerequisites and tie-outs (open AR = GL, open AP = GL, ledger balances at C−1). |
| Reconciliation | `Application/Migration` (`ReconciliationEngine`, `ReconciliationSources`) | Seven Costpoint reports compared with Workbench (trial balance, project cost, labor distribution, AR/AP aging, revenue, indirect rates): saved column mappings, tolerances, drill-down to ledger lines, `.xlsx` export, sign-off with a note. |
| Plan vs actual from Costpoint extracts | `Workbench.Costpoint`, `Application/Costpoint` | A read-only CSV-folder source that feeds "Plan vs actual" on a sheet; separate from the importers. |
| Cut-over plan | `docs/migration/costpoint-cutover.md` | Timeline, freeze rules, go/no-go, rollback. |

## What was missing, and what was added

| Gap in a real conversion | Added |
|---|---|
| **Fiscal calendar** (years and periods, including a 13th adjusting period) had to be keyed by hand, yet the opening balances post into a period. | Import **Fiscal calendar** (`FY_CD, PD_NO, PD_START_DT, PD_END_DT`). Twelve-month years are created like Setup › Fiscal calendar does; other shapes are taken as given with a warning. A year Workbench already has is left alone if identical and reported in words if it differs. Periods arrive open. |
| **Indirect rates** (provisional billing, actual, budget/target, forward pricing by pool and year). | Import **Indirect rates**. Costpoint's pool names (`FRINGE BENEFITS`, `OVHD`, `G & A`) are recognized; `31.2%`, `0.312` or a column titled `Rate %` all work, and an implausible value is refused with a hint rather than guessed. A changed rate supersedes the old one (history kept); rollback restores it. |
| **Pool structure** (which GL accounts feed each pool, allocation base, cascade order). | Import **Indirect pools and bases**. The accounts listed become the pool's explicit accounts; rollback restores the previous list. A pool that doesn't exist yet needs its applied/contra accounts in the file, otherwise the row says to install the default pools first. |
| The checklist didn't mention any of it. | New checklist step **Fiscal calendar and indirect rates** (never blocks other steps; done when any of the three is imported). |
| **The parallel month** had seven reconciliations and a lot of clicking, but no help saying *why* a row differs. | **Costpoint parallel run** (`/setup/migration/parallel-run`): see below. |

### The parallel run

Finance picks the month (the checklist's parallel month by default), uploads Costpoint's **trial balance** and **project cost**
report, and Workbench:

1. maps the columns by itself (saved mapping reused every month; if the balance column can't be found it says so and sends
   you to the full Reconcile screen);
2. compares by account and by project through the existing reconciliation engine (so tolerances, drill-down, `.xlsx` export
   and **sign-off** are the same evidence as before: "Open entries and sign off");
3. gives every difference a **likely cause in plain words** and a next step:

| Cause shown | When |
|---|---|
| Rounding | under a dollar |
| From an earlier month | the opening balance already differed (reconcile last month first) |
| Not posted yet | draft journal entries on that account sum to the gap, or lower labor/ODC while timesheets/vendor bills of the month are unapproved/unposted |
| Timing | entries posted after the month ended, or dated within five days after it, sum to the gap; ODC differences |
| One entry | a single posted entry equals the gap (named) |
| Rate difference | direct costs agree but fringe/overhead/G&A differ; shows both implied rates |
| Hours or rates | direct labor differs |
| Missing in Workbench / in Costpoint | row on one side only (an account not in the chart, a project at another level) |
| Needs a look | nothing explains it; open the detail |

It also shows what is still open in Workbench for the month (draft entries, unapproved timesheets, unposted bills), a verdict
line (`Waiting for Costpoint` → `Matched` / `N differences` → `Signed off`), and the months compared so far.
Causes are hints computed from the ledger, not conclusions: the drill-down always shows the entries.
API: `GET /api/v1/migration/parallel-run?periodId=`, `POST …/parallel-run/upload`, `GET …/parallel-run/runs/{id}/causes`
(Finance and administrators only).

## Deliberately left out

- **Employee labor / timesheet history and posted GL activity by prior period.** Prior-period activity would have to be posted
  to the GL again and would double what the opening balances already carry. Year-to-date amounts come in with the opening trial
  balance (income-statement accounts carry year-to-date); approved timesheets start at the cut-over. Revisit if a customer needs
  prior-period project cost reports from Workbench.
- **Opening balances by prior fiscal period / comparative trial balances.**
- **A live Costpoint connection** (no API is assumed) and **labor, AR/AP aging and revenue causes** in the parallel run: those
  reports are still compared on the Reconcile screen, just without the plain-words cause.
- **Writing back to Costpoint.** See the design below.

## Optional one-way GL export to Costpoint (design only, not built)

Goal: a customer keeps Costpoint as the book of record while Workbench does time, plans and billing; each closed month
Workbench produces journal entries Costpoint imports.

I could **not verify Costpoint's journal-entry import layout offline** (field order, fixed-width vs delimited, date format,
mandatory fields such as journal type, period/sub-period, org/account/project segments or batch controls differ by Costpoint
version and by how the customer configured its import). Guessing a layout would produce files that fail or, worse, post wrong.
So nothing is generated. The design:

- **Source**: posted Workbench entries of a closed fiscal period, grouped per source (timesheet labor, ODC, AP, billing…),
  with a running sequence per month so a re-export is idempotent and a correction is a new reversing file.
- **Layout is configuration, not code**: a saved *export layout* (columns, order, date format, delimiter, account/org/project
  mapping, journal type) set up once against the customer's own Costpoint import template, with a "test against a sample file"
  step — the same approach as the existing Paylocity layouts (Payroll › Providers). Workbench ships no assumed Deltek layout.
- **Safety**: balanced per file and per entry or the export refuses; an export marks its entries as exported (no double
  export); the file and its totals are kept as evidence next to the reconciliation.
- **Prerequisite to build**: one real Costpoint journal-entry import template from the customer (or Deltek's import
  documentation) and an agreed account/org/project mapping.
