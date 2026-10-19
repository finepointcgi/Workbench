# Performance at customer size

Can Workbench run a mid-size government contractor? This page records the load test (October 2026): the data, the
scenarios, what was slow, what was fixed, what is still slow, and how to size a self-hosted server.

**Short answer.** At 465 concurrently active users on five years of history, interactive requests answer in 150–400 ms
at p50 and 200–660 ms at p95, with no errors. The server uses about 2 of 12 cores. Before the fixes, pages took 17–19 s
at p50, with 25–30 s at p95 and time-outs. Project pages (the hub, a project's financials) and the Finance reports are
still over target under full load. They are listed under [Limits found](#limits-found).

## What was tested

### Hardware

| | |
|---|---|
| Machine | Apple M4 Pro, 12 cores, 48 GB RAM, macOS; app, database and load generator on the same machine |
| App | Workbench.Web, .NET 10, Release build, run natively; `ASPNETCORE_ENVIRONMENT=Development` (for the dev sign-in), rate limiting off |
| Database | PostgreSQL 17 in Docker Desktop (VM: 8 vCPU, 3.8 GB), `tests/load/docker-compose.yml`: shared_buffers 2 GB, work_mem 32 MB, effective_cache_size 6 GB, max_connections 300, pg_stat_statements |
| Load | k6 2.3 on the same machine (it takes some of the CPU too) |

The app runs in Development because the load test signs in through the dev sign-in page. Development adds detailed
errors and the dev pages; the application code is the same as in Production.

### Data: `tools/Workbench.LoadData`

```bash
docker compose -f tests/load/docker-compose.yml up -d
dotnet run -c Release --project tools/Workbench.LoadData -- --reset true --today 2026-10-18
```

The generator migrates the database, then builds the contractor below. Setup data and five fiscal years of history are
inserted directly; every journal entry is planned the way `PostingEngine` and `BillingLedger` plan theirs, numbered,
balanced and audited. Allocation runs, revenue setup and revenue runs go through the real services, and so do last
week's timesheets (each employee opens, fills, certifies and submits theirs). It takes about 7 minutes, then checks that
every entry balances and that the trial balance balances (it does: 684,257 lines, debits = credits = $2,404,692,526.89).
Options: `--employees`, `--years`, `--seed`, `--today`, `--services false`, `--connection`.

| | |
|---|---|
| Organization | 20 org units (4 divisions × 3 departments, Finance, HR, IT); 500 employees (CEO, 4 VPs, 45 approving managers, 450 staff; 10% hired and 8% left during the history); sign-in accounts with Admin, Finance, Timekeeper and ResourceManager roles |
| Contracts | 8 customers, 25 contracts (CPFF, T&M, LH, FFP), 50 CLINs, 282 executed funding modifications, bill rates by labor category per year |
| Projects | 60 direct projects of 49 WBS elements each, plus overhead, G&A, B&P, fringe and leave codes: 2,964 WBS elements; a plan sheet per project tied to its WBS; 7,922 charge assignments; baseline and current budgets phased by quarter (114,912 lines) |
| Sheets | 200 sheets with 35,800 rows: the 60 plans, 8 trackers of 2,000 rows, 20 of 500, 50 of 100, 62 of 30 |
| History (Oct 2021–Oct 2026) | 119,698 timesheets, 894,157 time entries, 596,001 daily-entry records; 14,247 ODCs and expense lines; 300 AP bills and their payments; 1,270 invoices with 663,672 billed sources; 1,248 customer payments; payroll and reimbursement disbursements; 152,923 journal entries; 122,277 approval trips; 392,021 audit rows; 61 allocation runs and 565 revenue runs; periods closed through August 2026 |
| Pending | 471 timesheets for last week waiting for approval |

The database is 1.15 GB. The biggest tables are TimeEntry (210 MB), BilledSource (195 MB), GlJournalLines (152 MB),
AuditEvent (131 MB) and TimeDayRecord (121 MB).

### Scenarios: `tests/load/workbench.js` (k6)

```bash
tests/load/export-fixtures.sh workbench_load   # who signs in, which periods and sheets
tests/load/run-all.sh after                     # fresh copy of the database, server, k6, counters, pg_stat_statements
```

Every scenario ramps up over 30 s and then holds for 4 minutes. The figures below leave out the first 45 s (sign-ins
and cold caches). Each virtual user signs in once and keeps its session.

| Scenario | Users | What each one does |
|---|---|---|
| time | 300 | Opens `/time`, opens this week's timesheet, saves a day of hours every 2–6 s, re-reads it |
| approvals | 50 | Opens `/approvals`, approves up to 5 items (each approval posts the labor to the ledger), checks the badge count |
| pm | 30 | `/projects`, a project page, its financials tab and the financial APIs, with 2–10 s of thinking between steps |
| finance | 5 | Takes turns at the period close checklist, the project status report (every project), the revenue preview, the ICE schedule build, and the trial balance |
| sheets | 40 + 40 | Editors load a 500–2,000 row grid and edit a cell every 3–7 s; listeners on the same sheets over `/hubs/sheets` time each live update |

Pages are loaded the way a browser first loads them: the server prerenders the Blazor page, and its data loads run.
The interactive circuit afterwards is measured separately (see [Blazor circuits](#blazor-circuits)).

**Database time** comes from the new opt-in `Server-Timing` header (`Diagnostics:ServerTiming=true`). An EF Core
interceptor (`DbTiming`) adds up command and reader time per request, the header carries it, and each request's
total, database time and command count are logged under `Workbench.Diagnostics.ServerTiming`. pg_stat_statements gives
the per-statement view.

## Results

The same script, data and machine were used for both runs. **Before** is `main` with only the timing hook added; **after** is this branch.

### Interactive requests (p50 / p95 ms; database p95 ms; queries per request)

| Request | Before p50 / p95 | After p50 / p95 | DB p95 before → after | Queries before → after |
|---|---|---|---|---|
| page `/time` | 17,374 / 28,018 | 384 / 664 | 2,999 → 50 | 83 → 48 |
| POST open my timesheet | 2,345 / 3,228 | 172 / 337 | 2,870 → 6 | 47 → 16 |
| PUT timesheet entries (save a day) | 2,829 / 3,836 | 338 / 610 | 3,284 → 13 | 60 → 30 |
| GET timesheet | 2,436 / 3,319 | 158 / 318 | 2,900 → 6 | 46 → 16 |
| page `/approvals` | 17,124 / 29,316 | 384 / 653 | 305 → 36 | 44 → 33 |
| GET approvals inbox | 16,603 / 28,404 | 87 / 204 | 116 → 22 | 10 → 4 |
| POST approve (posts the labor entry) | 483 / 853 | 357 / 642 | 496 → 86 | 30 |
| page `/sheets/{id}` (2,000 rows) | 18,680 / 26,272 | 372 / 655 | 547 → 117 | 52 → 46 |
| GET sheet items (2,000 rows) | 298 / 547 | 97 / 249 | 440 → 80 | 7 |
| PUT sheet item | 392 / 701 | 255 / 486 | 437 → 75 | 19 |
| page `/projects` (PM) | 18,898 / 25,606 | 866 / 1,468 | 1,418 → 630 | 76 → 70 |
| page `/projects/{id}` | 16,934 / 29,403 | 959 / 1,545 | 716 → 325 | 91 → 85 |
| page `/projects/{id}/financials` | 16,848 / 26,070 | 3,375 / 4,859 | 2,566 → 1,372 | 215 |
| GET financials summary / WBS / time-phased | 285–492 / 487–831 | 233–447 / 414–826 | 345–599 → 186–315 | 15–21 |

Live sheet updates reached the 40 listeners in 16 ms at p50 and 69 ms at p95 (before: 13 / 38 ms).

### Big reports

| | Alone, before | Alone, after | Under full load, after (p50 / p95) |
|---|---|---|---|
| Period close checklist | 21.7 s (often over 60 s under load) | 2.5 s | 14.1 / 17.0 s |
| Project status report, every project | 28.4 s (65–113 s under load) | 4.6 s | 25.5 / 33.9 s |
| Revenue preview | 34.4 s | 1.4 s | 7.3 / 9.5 s |
| ICE schedule build | 6.6–12.7 s | 0.4 s | 1.3 / 1.9 s |
| Trial balance | 4.3–7.1 s | 0.09 s | 0.47 / 0.64 s |
| Projects hub as Finance (all 65 projects) | 7.9 s | 2.1 s | — |
| A revenue run (60 projects, in the generator) | 10–20 s | — | — |

"Alone" means one user on an idle server (`tests/load/bench.js`). The long pages already show a progress bar while they
load (period close, standard reports, revenue runs, ICE).

### Throughput and resources (4-minute hold)

| | Before | After |
|---|---|---|
| Requests completed | 13,252 | 26,587 |
| Errors | 0.04% (period close timed out) | 0.00% |
| App CPU (of 12 cores), average / peak | 4.4 / 5.5 cores | 1.9 / 2.9 cores |
| App working set, average / peak | 4.4 / 5.9 GB | 2.4 / 2.9 GB |
| Allocations | 1.06 GB/s | 0.95 GB/s |
| GC pause share | 6.7% | 6.7% |

Before the fixes, the server was mostly waiting, not working: requests queued behind one lock and behind very large
queries.

## What was slow, and the fixes

All of these are in this branch. Except where noted, behaviour is unchanged.

1. **Every page waited on one lock.** The nav bar's approvals badge calls `IApprovalInboxService.CountMineAsync`, which
   first runs `ApprovalBackfill`. The backfill scans for documents submitted before approval workflows existed, behind
   one lock per tenant. With hundreds of users, every page queued behind the others' identical scans: pages took
   25–30 s while their own database time was 1–2 s. Now a caller that finds a backfill already running waits for it to
   finish instead of starting another. *Behaviour:* a pre-workflow document (seeded or imported) created while a
   backfill is running appears on the next inbox load.
2. **Charge codes loaded the whole WBS.** `IProjectService.ListChargeCodesAsync` read all 2,964 elements, every CLIN and
   every assignment on every call, and the timesheet page called it once per day of the week (83,932 calls in one
   run). It now reads only the open, chargeable leaves of active projects that need no assignment or that the employee
   is assigned to, and then applies the same rules to each date. A new default-implemented interface member,
   `ListChargeCodesForDatesAsync`, answers for a whole week in one pass; the timesheet detail and submit use it.
3. **"Recently used" codes scanned everyone's entries.** That query now starts from the employee's own timesheets.
4. **The unposted check (period close) loaded the entire ledger.** `PostingEngine.ListUnpostedAsync` read every
   approved timesheet with its entries (892k rows) and every posted labor and ODC entry with its lines (501k rows),
   then matched them with nested `Any` loops, which is quadratic. It now reads only the period's sources and their
   entries, matches them with hashed lookups, and reads lines only for entries that need a reversal. This took period
   close from more than 60 s to 2.5 s.
5. **The trial balance, income statement and balance sheet read every journal line.** They now sum per account and
   period in SQL (at most accounts × periods rows). The ledger detail, project cost and export reports still read lines.
6. **Revenue read every project's ledger lines for each project.** `RevenueEngine.LinesAsync` now takes the project
   (plus the project-less billing lines it may own), and one read gives both the ledger position and the prior revenue.
   The read-only callers (revenue preview, period close check) compute four projects at a time, each on its own
   context. Posting a run stays sequential.
7. **A three-level include repeated invoice data for every billed source.** `InvoiceGenerator.PriorInvoicesAsync`
   loaded invoices → lines → sources in one join (about 33,000 rows per call). It now loads invoices, then lines with
   their sources, in two queries. This was the only EF multiple-collection-include warning the load test raised.
   `AsSplitQuery` isn't available in Application, which doesn't reference the relational provider, so the split is
   written out by hand.
8. **Round trips in sequence.** The project status report, the unbilled accrual and the Projects hub list each made
   dozens of small queries per project or contract, one after another. They now fetch four projects (or contracts) at a
   time, each on its own context, and still assemble the results in order.
9. **Next journal number.** `MAX("Sequence")` now matches the filter of the unique index (`Sequence > 0`), so the
   maximum comes from the index instead of a scan.
10. **Index.** `TimeEntry(WorkDate)`: period close, date-range labor reports and the unposted check filter time by date
    alone. The configuration is updated; regenerate the baseline after merging. No migration is committed.

Paging: the lists that grow without limit (journal, AP bills, payments, invoices, audit) already take a `Take`. The one
growing list found without one is `ITimesheetService.ListMyTimesheetsAsync`: every timesheet of the employee, with
entries, about 52 a year. It is cheap at five years (about 15 ms), so it is noted here rather than changed.

## Blazor circuits

Eight browser tabs on a 2,000-row sheet, measured as the live GC heap after a forced collection (`dotnet-gcdump`),
added 68.6 MB: **about 8.6 MB per circuit**. That is a worst case; lighter pages hold less. Each circuit keeps the
sheet's 2,000 `ItemDto`s and its render tree. Disconnected circuits are kept for 3 minutes (the ASP.NET Core default)
in case the browser reconnects, so a user who reloads holds two circuits for a while. For 300 open sessions, plan on
1.5–2.5 GB of server memory for circuits alone, on top of the request load above.

## Limits found

- **Project financials re-read a project's whole labor history on every view.** The financials tab, the hub's money
  columns, the burdened cost service and the project status report each load every approved time entry of the project
  (around 15,000 rows at this size) and price each entry in memory. After the fixes, this is the largest share of
  database time: about 11,000 calls at 88 ms each in one run. The financials tab makes 215 queries and takes 3.4 s at
  p50 under load. The fix is to aggregate cost per element and month in SQL, or keep a monthly cost summary.
  **This is the main limit for more than about 50 project managers working at once.**
- **The Finance reports compete with each other.** Each report is under 5 s on its own, but five Finance users running
  them back to back keep 15–20 heavy queries in flight, and then the project status report takes 25–34 s. Running heavy
  reports in the background with a server-wide limit would keep them predictable.
- **Finance's Projects hub** computes every project's financials (2.1 s alone).
- **The dev sign-in page lists every user.** It is a test artifact, not a production path.
- **The load generator shares the machine,** so absolute numbers are somewhat pessimistic. Network latency between the
  app and the database (Docker's virtual network here) dominates the many-small-queries paths. Put the app and the
  database in the same zone.

## Sizing for self-hosting

The table assumes this mix of work (mostly time entry, plus approvers, project managers, a few Finance users and sheet
users) and about five years of history at roughly 2,000 time entries per employee per year. "Active users" means
people using the app at the same time, not named accounts. A 500-employee contractor typically has 150–250 active users
at the Friday time-entry peak.

| Active users | App server | PostgreSQL | Notes |
|---|---|---|---|
| up to 100 | 2 vCPU, 4 GB | 2 vCPU, 4 GB RAM (1 GB shared_buffers), 20 GB SSD | One small VM can host both |
| 250 | 4 vCPU, 8 GB | 4 vCPU, 8 GB (2 GB shared_buffers), 50 GB SSD | |
| 500 | 4–8 vCPU, 12 GB | 8 vCPU, 16 GB (4 GB shared_buffers), 100 GB SSD | Measured here: 465 active users used 1.9 cores average and 2.9 cores peak, and 2.9 GB, before circuits |

**Per 100 active users:** about 0.5 app core (provision 1 vCPU), 0.6 GB of app memory plus up to 0.9 GB for their open
Blazor circuits, and 1 database vCPU with 2 GB of RAM. Add one more app vCPU for each Finance user who runs the big
reports at the same time as everyone else.

Database growth is about 230 MB per year for 500 employees, mostly time, billed sources, journal lines and audit rows.
Keep shared_buffers at 25% of the database server's RAM and effective_cache_size at 75%. Give Docker hosts a
`/dev/shm` of at least 1 GB (parallel queries fail on the default 64 MB). Allow at least 200 connections (Npgsql's pool
defaults to 100 per process).

## Reproducing

1. `docker compose -f tests/load/docker-compose.yml up -d` (PostgreSQL on port 5444).
2. Generate the data: `dotnet run -c Release --project tools/Workbench.LoadData -- --reset true` (database `workbench_load`).
3. Export the fixtures: `tests/load/export-fixtures.sh workbench_load`.
4. Run: `tests/load/run-all.sh <label>`. Each run copies `workbench_load` to `workbench_load_run`, so approvals and time
   entry start from the same data every time. It publishes and starts the server and writes the results to
   `/tmp/workbench-load`: k6 CSV, summary, counters, pg_stat_statements and the server log. `WEB_DIR=<published
   folder>` tests another build.
5. Time the big reports alone: `k6 run tests/load/bench.js`.
6. Summarize any run: `python3 -I tests/load/summarize.py <run>-k6.csv --skip 45 --md`.
