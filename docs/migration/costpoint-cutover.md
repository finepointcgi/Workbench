# Costpoint cut-over plan

How a customer moves from Costpoint (and Smartsheet) to Workbench: what to load, in which order, how the books are
proven to the penny during a parallel month, when to decide go or no-go, and how to back out. The tools are in
Workbench under **Setup › Go-live migration** (`/setup/migration`, Finance and administrators):

- **Checklist**: the steps below, each linked to the import wizard, with its status from the import history,
  prerequisites, and data checks (record counts, open AR/AP tied to the GL).
- **Import wizard** (`/setup/import`): upload → map → validate → dry run → import, every import reversible.
- **Reconciliation** (`/setup/migration/reconcile`): upload a Costpoint report for a period, compare it with
  Workbench row by row, drill into differences, export the differences to .xlsx, and sign each report off.
- **Payroll › Providers**: Paylocity hours export and payroll register layouts, tested against the customer's own files.

Example files for every step are in `samples/import` (imports), `samples/reconcile` (Costpoint reports) and
`samples/payroll` (Paylocity). Nothing is hard-coded to their columns: every file is read through a column mapping
that's suggested from its headers and saved for next time.

## Roles

| Role | Who | Does |
|---|---|---|
| Cut-over lead | Customer controller | Owns the plan, the freeze and the go/no-go decision; signs off reconciliations |
| Costpoint operator | Customer accountant | Runs the Costpoint exports and reports, posts Costpoint's last entries |
| Workbench admin | Workbench implementer | Runs imports and reconciliations, fixes mappings, keeps the issue log |
| Payroll owner | Customer HR/payroll | Confirms the Paylocity import template and register export |
| Project managers | Customer PMs | Review imported projects, WBS, budgets and Smartsheet plans |

## Timeline

Dates are relative to the cut-over date **C** (the first day Workbench is the system of record, normally the first day
of a fiscal month). Opening balances are dated **C − 1**. The parallel month is the first month after C.

| When | What |
|---|---|
| C − 6 weeks | Kick-off. Agree the cut-over date and the parallel month; set both on the checklist. Confirm the fiscal calendar in Workbench covers C − 1 through the parallel month (Setup › Fiscal calendar). Get sample exports of every Costpoint screen/report below and the Paylocity files. |
| C − 5 weeks | **Trial load 1** into a test tenant: run every import with real exports, fix mappings, save them. Load the chart of accounts and check cost classes against Costpoint's pools. |
| C − 4 weeks | Configure Paylocity layouts against the customer's import template and a real register (Payroll › Providers, "test against a sample file"). Set up indirect rate pools and provisional rates, posting rules and billing settings. |
| C − 3 weeks | **Trial load 2** with fresh exports, end to end, including opening balances and open AR/AP. Reconcile the trial balance at the trial cut-over date. Every difference is explained or fixed in the mapping. Roll the trial back or discard the tenant. |
| C − 2 weeks | Train users (timesheets, approvals, expenses, AP entry, billing). PMs review their projects in the trial tenant. |
| C − 1 week | Announce the freeze. Finalize the go-live tenant: users, roles, SSO. |
| C − 3 days | **Master data load**: accounts, orgs, employees and rates, customers, contracts/CLINs, projects/WBS, budgets, vendors. Master-data changes now go into both systems (keep a change log) until C. |
| C − 1 | **Freeze**: no new Costpoint transactions dated before C except the close. Costpoint closes the month. |
| C + 1–5 days | Costpoint month-end close for C − 1 completes. Export the final trial balance, open AR, open AP, fixed assets. **Load opening balances and open items**, tie them out on the checklist, reconcile the trial balance at C − 1 and sign it off. |
| C | Workbench is the system of record for time, expenses, AP, billing and projects. Costpoint keeps receiving the same transactions for the parallel month only (see below). |
| C + 1 month + 5 days | **Parallel month close** in both systems; run all seven reconciliations; go/no-go meeting. |
| Go | Costpoint becomes read-only for history. Archive the reconciliation evidence. |

## Freeze rules

- From the freeze until the opening balances are loaded, Costpoint takes only closing entries for C − 1 (accruals,
  allocations, adjustments) and those are recorded in the issue log, because they change the opening balances.
- Master data changed after the master data load (a new employee, a rate change, a new CLIN) is entered in both systems
  and listed in the change log. Re-importing a file is safe: imports match on natural keys and update or skip.
- Timesheets for the week spanning C are split at C: days before C in Costpoint, days from C in Workbench.

## Load order and what each step checks

Follow the checklist; each step names its prerequisites and is blocked until they're done. Every import has a dry run
that changes nothing and lists what will be created, updated or skipped, and a rollback that refuses when something has
started using what it created.

1. **Chart of accounts** (`accounts.csv`): account, name, type (A/L/C/R/E or words), parent, cost class. Summary
   accounts are the ones named as parents and take no postings. Check every expense account's cost class: indirect rates
   use it to build the pools and bases.
2. **Organizations** (`organizations.csv`).
3. **Employees and rates** (`labor-categories.csv`, `employees.csv`, `cost-rates.csv`). Checklist warns when an employee
   has no cost rate (their labor would cost 0).
4. **Customers** (`customers.csv`).
5. **Contracts and CLINs** (`contracts.csv`, `clins.csv`, `bill-rates.csv`). Funding comes in through one executed
   modification per contract.
6. **Projects and WBS** (`projects.csv`, then `budgets.csv`).
7. **Open AP and AR** (`vendors.csv`, `open-ap.csv`, `open-ar.csv`): unpaid vendor invoices and customer invoices at
   C − 1, for their open balance. They are **not posted**: the AR and AP balances come in with the opening trial
   balance, so posting them too would double the GL. Payments recorded after C apply to them and post normally.
   Fully paid rows are skipped; credit balances are refused (enter them as unapplied cash or vendor credits after C).
8. **GL opening balances** (`opening-trial-balance.csv`): the Costpoint trial balance at C − 1 by account × org ×
   project (dotted project ids post to that WBS element), as ending balances or debit/credit columns. It must balance
   to the penny; it's posted as one opening entry dated C − 1 (files over 500 lines are split into parts that net to
   zero on an offset clearing account you choose). Income statement accounts carry the year-to-date amounts when C
   isn't the first day of the fiscal year. Only one opening balance import can stand at a time.
   The checklist then checks: open AR = GL 1110, open AP (excluding retainage) = the AP account, retainage = 2020,
   and the ledger balances at C − 1.
9. **Fixed assets**: the fixed-asset opening balance importer (cost, accumulated depreciation, in-service date), so
   depreciation continues from C without catching up. Until that importer is in your build the step links to the
   assets page; mark it not applicable when the customer has no capitalized assets.
10. **Smartsheet plans**: from the Smartsheet API or .xlsx exports, linked to each project's WBS.
11. **Parallel month**: see below.

## Proving the opening balances

Before go-live work starts in earnest, reconcile the trial balance for the period containing C − 1 against the
Costpoint trial balance for the same date (Reconcile › Trial balance). Expected result: every account matched to the
penny (default tolerance 0.00). Then reconcile AR aging and AP aging as of C − 1. Sign all three off; they are the
opening balance evidence.

## The parallel month

During the first month after C, the same transactions are entered in both systems: timesheets (Workbench is the
source; time is exported or keyed into Costpoint), vendor bills, expense reports, invoices and cash receipts. At month
end both systems close, run their allocations and revenue, and produce the reports below. Reconcile each in Workbench,
with the period set to the parallel month:

| Report | Costpoint report | Keys | Compared | Default tolerance |
|---|---|---|---|---|
| Trial balance | Trial Balance | Account | Ending balance (and optionally opening, period debits/credits) | 0.00 |
| Project cost by element | Project Status / Cost by Element | Project or WBS | Direct labor, fringe, overhead, G&A, ODCs, total | 0.00 |
| Labor distribution | Labor Distribution | Employee × project | Hours, labor cost | 0.00 |
| AR aging | AR Aging | Customer | Current … over 90, total | 0.00 |
| AP aging | AP Aging | Vendor | Current … over 90, total, retainage | 0.00 |
| Revenue by project | Project Revenue | Project | Revenue | 0.00 |
| Indirect rates | Pool Rate / Allocation | Pool | Pool cost, base, rate (year to date) | 0.00; rates 0.0001 |

How a comparison works:

- Upload the Costpoint report (.csv or .xlsx, title rows above the headers are fine). Columns are matched to fields
  from their headers; adjust and save the mapping (it's offered automatically the next time a file with those
  columns is uploaded). Debit and credit columns map to one signed amount (column − minus column); percentages use a
  scale of 0.01; key rewrites translate Costpoint names (FRINGE BENEFITS → Fringe); total rows are skipped by prefix.
- Rows are matched on their keys (trimmed, case-insensitive, optionally ignoring punctuation and leading zeros);
  file rows with the same key are added up. Summary accounts and parent WBS elements are compared only when the
  Costpoint report lists them.
- A row is **Matched** when every compared amount is within its tolerance (absolute, or a percentage of the Costpoint
  amount), **Different** otherwise, or **Only in Costpoint / Only in Workbench**. Unreadable file rows are listed.
- Click any row for the detail on both sides: ledger lines, time entries, invoices, bills or rate trend in Workbench,
  and the Costpoint file rows that made up the key.
- **Export .xlsx** gives the summary, the differences, every row and the unreadable rows, for the issue log.
- **Sign off** records who and when; a run with differences needs a note explaining them. Signed runs never change; a
  new run needs its own sign-off. The evidence board shows each report's latest run and latest sign-off.

Typical explained differences: timing (an entry posted in one system after the other closed), rounding of burden at
a different level (fix by aligning the allocation level, not by widening tolerances), aging buckets defined from
invoice date vs due date (compare the totals, then the buckets with an agreed tolerance), and Costpoint pools that
include accounts Workbench classifies differently (fix the account's cost class).

## Go / no-go criteria

Go when all of these are true:

1. Opening balances: trial balance at C − 1, AR aging and AP aging matched to the penny and signed off.
2. Parallel month: all seven reconciliations signed off; any difference over $1.00 (or any rate difference over
   0.0001) is explained in the sign-off note and has an owner and a correcting entry planned.
3. Payroll: the parallel month's Paylocity hours export was accepted by Paylocity without manual edits, and the
   register imported and reconciled (gross explained by labor at pay rates).
4. Billing: every invoice for the parallel month generated in Workbench matches the Costpoint invoice amount by
   contract and CLIN.
5. Every checklist step is done or marked not applicable with a reason.
6. Users: all timesheets for the parallel month were entered and approved in Workbench; no open P1 issues.

No-go if any opening balance reconciliation can't be signed off, or the parallel month has unexplained differences in
the trial balance or project cost. Decide a new parallel month and repeat.

## Rollback

- **Before go**: every import can be rolled back from Import › History, newest first (later jobs that use earlier
  records block the rollback until they are rolled back too). The opening balance entry rolls back with its import
  job. If many jobs need undoing, provision a fresh tenant and reload instead: the saved mappings make a reload fast.
- **After go, within the parallel month**: Costpoint still has every transaction, so the fallback is to keep running
  Costpoint and stop entering in Workbench. Export Workbench's time and AP entered since C (Reports, CSV) and key the
  difference into Costpoint.
- **After go and after the parallel month**: corrections are journal entries (reversals plus new entries) in Workbench;
  posted entries are never edited or deleted.

## Evidence kept

- Import jobs with their files, mappings, dry runs and every record they touched (Import › History).
- Reconciliation runs with the uploaded Costpoint file, the mapping and tolerances used, every compared row and the
  sign-off (who, when, note), exportable to .xlsx.
- The audit log for mappings, settings, step marks and sign-offs (Setup › Audit log).
