# Unanet ongoing API sync — design (not built)

Status: **design only**. Nothing here has been called against a real Unanet site, and no endpoint, field or
authentication detail below is verified. Every statement about Unanet's API is marked **[A#]** and listed in
[Assumptions to verify](#assumptions-to-verify-on-a-real-api-enabled-site). Build this only after a customer with an
API-enabled Unanet site has confirmed them, ideally by sending a few real responses (with names removed).

The file import (see [unanet.md](unanet.md)) already covers the move-over and the parallel month. An API sync is for
the customer who keeps Unanet running next to Workbench for longer (for example Unanet stays the time system for part
of the company) and wants Workbench to stay current without someone exporting files.

## What would be synced, and which way

Workbench is the system of record for what it owns; Unanet is read-only to Workbench unless a direction below says
otherwise. Pull is the default and the only direction proposed for a first version.

| Data | Direction | Reuses | Notes |
|---|---|---|---|
| Organizations, labor categories, people, customers | Unanet → Workbench | Same importers as the file import | Key: employee id / codes, as in the file import. Re-running updates, never duplicates. |
| Projects and tasks | Unanet → Workbench | `UnanetContractImporter`, `UnanetProjectImporter` | Same project → contract + project, task → WBS rules. |
| Assignments, budgets, bill rates | Unanet → Workbench | `UnanetAssignmentImporter`, `UnanetBudgetImporter`, `UnanetBillRateImporter` | |
| Approved timesheets | Unanet → Workbench | `UnanetTimesheetImporter` | Only approved/posted time; skipped when a Workbench timesheet exists for the person and period. |
| Open AR/AP invoices | Unanet → Workbench | `UnanetOpenArImporter`, `UnanetOpenApImporter` | Only for the cut-over snapshot; not a continuing feed. |
| Workbench actuals → Unanet | Workbench → Unanet | — | **Not proposed.** Writing into a system of record the customer is retiring needs its own case; ask first. |

The design principle: **the API is only a different way to get the same rows the file import reads.** A sync run
fetches records, writes them as an in-memory table with Unanet's usual column names, and hands them to the existing
importers. That keeps one set of rules, one dry run, one rollback and one audit trail, and means the file import
remains the fallback when the API is unavailable.

## Shape of the feature

1. **Connection** (Admin › Integrations › Connectors, alongside GitLab and Smartsheet): base URL of the customer's
   Unanet site, an API user, and its credential. The credential is entered by the customer's admin in Workbench's
   secret store (same `IntegrationSecrets` mechanism as the GitLab token), never in files, logs or audit text. Workbench
   staff never handle it. **[A1]**
2. **Test connection** reads one small, harmless thing (for example the site's version or the first page of
   organizations) and reports success in plain words, or the exact Unanet message on failure.
3. **Sync now** (and, later, a schedule): pick what to pull (people, projects, time...), see a **dry run in plain
   words** ("12 people will be added, 3 updated, 40 left alone"), then confirm. Same wizard step as a file import: the
   job appears in import history with source *Unanet (API)* and can be rolled back.
4. **Incremental by date**: time is pulled for a date range (default: since the last successful sync, minus a
   safety overlap of one period), everything else is pulled whole (small) and diffed by the importers.
5. **Parallel-run reconcile** uses the same API to fetch a month's totals instead of an uploaded file, then feeds the
   existing reconcile engine unchanged.

## Endpoints needed (all assumptions)

Unanet publishes a REST API for API-enabled sites. The paths, parameters and field names below are **guesses at the
general shape**, listed so the real contract can be filled in, not copied. **[A2]**

| Need | Likely request **[A2]** | Maps to | Fields we would need **[A3]** |
|---|---|---|---|
| Sign in | A login/token call with the API user, or a basic/token header on every call **[A1]** | Connection | token lifetime, refresh |
| Organizations | `GET …/organizations` (paged) | Org units | code, name, parent code, active |
| Labor categories | `GET …/labor-categories` | Labor categories | code, name, active |
| People | `GET …/people` | Employees, cost rates | employee id, username, name, email, organization, labor category, hire/termination date, supervisor, FLSA status, cost rate and effective date |
| Customers | `GET …/customers` | Customers | code, name, type |
| Projects | `GET …/projects` | Contracts, projects | code, name, type, status, contract number, customer, start/end, value, funded, project manager, organization |
| Tasks | `GET …/projects/{id}/tasks` (or `GET …/tasks?project=`) | WBS elements | code, name, parent task, status, allow-time flag |
| Assignments | `GET …/projects/{id}/assignments` | Charge assignments | person, task, labor category, dates |
| Budgets | `GET …/projects/{id}/budget` | Budget lines | task, labor category, hours, other-cost amount |
| Time | `GET …/timesheets?from=&to=&status=approved` (paged) | Approved timesheets | person, project, task, date, hours, status, approver, approval date, labor category |
| Invoices | `GET …/invoices` and `…/vouchers` (open only) | Open AR / AP | number, project/customer or vendor, dates, amount, balance |

## Mapping, once the data arrives

Identical to the file import (see the mapping table in `unanet.md`). Two API-specific points:

- **Ids vs codes.** An API usually returns internal numeric ids next to the human codes. Workbench keys on the code
  the customer sees (employee id, project code, task code) so the file import and the API sync can be mixed. If a
  code is not unique or not exposed, the sync cannot run for that entity and says so before fetching. **[A3]**
- **Time granularity.** If the API returns weekly sheets with a total per day, they are expanded to the per-day,
  per-task rows the timesheet importer already reads; Workbench's own period setting then regroups them.

## Failure behavior

- Rate limits and timeouts: retry with backoff (the Smartsheet client already has this pattern), then stop with a
  plain message and leave nothing half-written (each importer already refuses a partial timesheet). **[A4]**
- Partial data: a failed page fails the run before the import step; the dry run is only offered on complete data.
- A changed field name or a missing field is reported as "Unanet no longer sends *Hire Date*; map it or contact
  support", not as a stack trace.
- Credentials expired or revoked: the connection shows "needs attention" on the Connectors page and Admin gets one
  notification, not one per failed run.

## Security and data handling

- Read-only API user, scoped by the customer to the data above. We do not need rights to write, to see salaries
  beyond cost rates, or other users' credentials.
- Credentials: secret store only; never logged; redacted in errors. TLS only; the base URL is checked with the same
  outbound-URL guard as webhooks (no private/loopback addresses unless the site is genuinely internal, which is an
  admin setting with a warning). **[A5]**
- Everything fetched is treated as untrusted input like an uploaded file (size limits, same parsing and validation).
- Audit: each run records who started it, what range, and the import job it created.
- Customer data in Unanet responses is not stored beyond the import job's own files (the job keeps the generated
  table, like an uploaded file, so a run can be reviewed and rolled back).

## Build order (when approved)

1. Connection + test (no data).
2. Read-only fetch of organizations, labor categories, people and customers into the existing importers.
3. Projects, tasks, assignments, budgets.
4. Approved time by date range.
5. Schedule + notifications.
6. API source for the parallel-run reconcile.

Each step ships with a fake Unanet server in tests (recorded, with invented data), the way the Smartsheet client is
tested, so nothing in the test suite needs a real site.

## Assumptions to verify on a real API-enabled site

| # | Assumption | Why it matters |
|---|---|---|
| A1 | The customer's Unanet site has the REST API enabled, and an API user can be created with read-only access. Authentication is a token or basic credential given to that user. | No API access means file import stays the only route. |
| A2 | Paths, query parameters (paging, date filters, status filters) and the base URL layout are as in the table, or can be mapped onto it. | The table is a sketch; the real contract replaces it. |
| A3 | Each entity exposes the fields listed, including a stable human code, and approved time can be filtered by date range and approval status. | The importers need these to match and to skip unapproved time. |
| A4 | Rate limits and page sizes are documented and large enough for a fiscal year of time (hundreds of thousands of rows) to be pulled by date range. | Determines chunking and how long a sync takes. |
| A5 | The customer's site is reachable from Workbench's servers (public URL or allow-listed), and the customer's security team accepts a read-only API user. | Self-hosted Workbench may sit inside the customer network; SaaS needs an allow-listed IP. |
| A6 | Custom fields (extra columns many Unanet sites add) are exposed by the API. | Customers will expect their own columns to come across. |
| A7 | Approved time can be re-read without changing (immutable once approved), or changes after approval are flagged. | Decides whether re-syncing time can silently alter history; Workbench would refuse to change an existing approved timesheet either way. |
| A8 | Cost rates are available to the API user. | Many sites restrict rates to HR; without them cost reports would be wrong. |

Questions for the first customer conversation: is the API enabled; who can create an API user; can we see one
response for each endpoint with names removed; which custom fields matter; and do they need a continuing sync at all,
or does the parallel month plus file import cover them?
