# Unanet integration

How a company on Unanet moves to Workbench, or runs the two side by side for a month. This page says what Unanet data
a GovCon needs to bring over, how Unanet gives it to you, how each Unanet concept lands in Workbench, and what is
built today versus designed for later.

> **Verification status.** This was written without access to a live Unanet site. What is stated about Unanet's
> screens, report names and column headings is common knowledge of how Unanet sites are set up, but **every Unanet
> site is configured differently** (custom fields, renamed columns, different project types). That is why nothing is
> hard-coded to a column: every file goes through a column mapping that is suggested from the headers and can be changed
> by the user before anything is imported. Anything about Unanet's REST API is a **marked assumption** to confirm
> against the customer's own site (see [Part 3](#part-3-ongoing-api-sync-design-not-built)). No endpoint listed here
> has been called.

## Where the data comes from

| Route | What it is | Status |
|---|---|---|
| **Report / Excel / CSV export** | Every Unanet list and report can be run and exported to Excel or CSV by an administrator (People, Projects, Tasks, Customers, Organizations, Labor Categories, Time, AR/AP invoices). | **Built** (Part 1 and 2) |
| **Unanet's bulk import/export templates** | Unanet sites also use CSV templates to load people, projects and time. The same files work in reverse as exports. | Same importer; headings are matched by name |
| **Unanet REST API** | Only on API-enabled sites; needs an API user and the site's base URL. | **Design only** (Part 3), not built |

File export is the right first route: it needs no credentials from Workbench's side, the customer's administrator
already knows how to produce the files, and the dry run shows exactly what will change before anything is written.

## How Unanet concepts map to Workbench

This table is shown in plain words in the import wizard before a Unanet import is committed
(**Setup › Import › Unanet**), and again on the checklist at **Setup › Go-live migration › Moving from Unanet**.

| In Unanet | In Workbench | Notes |
|---|---|---|
| **Organization** | **Org unit** | Parent organization becomes the parent org unit. |
| **Person** (login, employee id, name, email) | **Employee** | Key is the Unanet *Employee ID* (falls back to *Username*). Email is required: it links the person to their sign-in. |
| **Labor category** | **Labor category** | The person's default labor category and the labor category on budgets and bill rates. |
| **Person's cost / salary rate** | **Employee cost rate** (effective-dated) | Rates are append-only; a changed rate needs a new effective date. |
| **Customer** | **Customer** | |
| **Project** | **Contract + project** | A Unanet project holds both a customer agreement and the place time is charged. Workbench keeps those apart: the *contract* (type, period of performance, value, funding) and the *project* (what people charge). The contract number is the Unanet *Contract Number* when present, otherwise the *Project Code*. |
| **Project type** (T&M, FFP, Cost…) | **Contract type** | |
| **Task** (and sub-task) | **WBS element** | WBS code is `<project>.<task>`, nested by the task's parent. This is also the charge code on timesheets. |
| **Contract line / CLIN** (where the site tracks them) | **CLIN / SLIN** | Optional file. |
| **Project labor category + bill rate** | **Bill rate** (effective-dated, per contract) | |
| **Project / task budget** (hours by labor category, other costs) | **Budget lines** | |
| **Project assignment** (who may charge) | **Charge assignment** | Restricts who can charge a WBS element, as in Unanet. |
| **Timesheet** (approved time) | **Approved timesheet** with its time entries | Brought in as *history* only: no approval workflow is started, no GL entries are posted. |
| **AR invoice** (unpaid) | **Open AR invoice** | Not posted: the balance comes with the opening trial balance, as for Costpoint. |
| **AP invoice / voucher** (unpaid) | **Open AP bill** | Same. |
| **Vendor** | **Vendor** | |

What does **not** come over, on purpose: Unanet's GL history (bring the trial balance through the opening-balance
import instead), unapproved or rejected time, workflow and approval history, and Unanet user passwords/roles
(Workbench has its own sign-in and roles).

## Part 1 — File import (built)

Pick **Unanet** in the import wizard (`/setup/import`). Each step takes one Unanet export; headings are pre-matched to
Unanet's usual spellings and anything unmatched is picked from a drop-down. The wizard shows the mapping table above,
then validates, runs a **dry run** in plain words ("12 people will be added, 3 updated, 40 left alone"), and imports.
Every import can be rolled back and is in the audit trail.

Suggested order (the Unanet page under *Go-live migration* tracks it):

1. **Organizations** → org units
2. **Labor categories**
3. **People** → employees (with default labor category, supervisor and current cost rate)
4. **Cost rate history** (optional)
5. **Customers**
6. **Projects** → contracts (from the project rows)
7. **Contract lines (CLINs)** (optional)
8. **Projects and tasks** → projects and WBS tree
9. **Bill rates** (optional)
10. **Budgets**
11. **Assignments**
12. **Vendors**, **Open AP**, **Open AR**
13. **Timesheets** (approved time for the fiscal year so far)

Example files (invented, not real data) are in `samples/import/unanet`.

### Timesheets

The Unanet time export has one row per person, project/task and day (or per week with day columns; both are
supported: a *Date* column, or a *Week ending* date with *Mon…Sun* hour columns, are read). Rows are grouped into
Workbench timesheet periods using **the period setting configured in Workbench** (weekly, biweekly, semi-monthly or
monthly), so Unanet weekly sheets are regrouped when Workbench uses a different cadence. A sheet is imported as
**Approved**, approved-by and approved-at taken from the file when present. Rules, all reported per row:

- the person must exist (import people first), the task must exist (import projects and tasks first);
- only approved/billed/posted time is brought in (a *Status* column is used when mapped; other statuses are skipped
  with a warning, never imported silently);
- an employee/period that already has a timesheet in Workbench is **skipped**, not merged, so a re-run is safe;
- time is not posted to the ledger: labor cost reaches the GL in the opening balances; the history only feeds hours
  reports, project cost-to-date and the first period close.

### Reconciling the parallel month

See [Part 2](#part-2-parallel-run-reconcile-built). Both are described again in
[the cut-over plan](../migration/costpoint-cutover.md), whose timeline, roles and freeze rules apply unchanged to a
Unanet cut-over.

## Part 2 — Parallel-run reconcile (built)

**Setup › Go-live migration › Moving from Unanet › Reconcile a month** (`/setup/migration/unanet/reconcile`) compares a closed month in Unanet with the same month in Workbench:

- **Upload** Unanet's *project cost* or *timesheet totals* report for the month (hours and cost by project and person).
- Workbench totals approved time for the same dates and compares **by project and by person**, showing both numbers,
  the difference, and the **likely cause in plain words** ("Unanet has 8.0 more hours: the person's timesheet for the
  week of 03/04 isn't in Workbench", "cost differs but hours match: the person's rate differs by $x/hour", "this
  project isn't in Workbench").
- Differences can be signed off with a note, the same way as the Costpoint reconciliations; each run keeps the file,
  the mapping and the result, and exports to .xlsx.
- Two reports: **timesheet totals by person and project**, and **project cost by project** (hours and labor cost).
  Costs use the labor cost rates in force on each day. Unanet reconciliations do not count towards the Costpoint
  go-live evidence board.
- Example files with planted differences: `samples/reconcile/unanet`.

## Part 3 — Ongoing API sync (design, not built)

See the design in [Unanet ongoing API sync — design (not built)](https://github.com/finepointcgi/Workbench/blob/master/docs/integrations/unanet-api-sync-design.md). It lists the Unanet endpoints that would be
needed, what Workbench would do with each, and every assumption that has to be checked on a real, API-enabled Unanet
site before any code is written.
