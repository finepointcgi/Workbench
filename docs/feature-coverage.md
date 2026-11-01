# Feature coverage against Costpoint, Smartsheet, monday.com and MS Project

Checked 1 Nov 2026 against master (`e265907`). This replaces the parity table in the product plan, which dates from 18 Oct and
lists nine Costpoint capabilities as missing that now have screens.

## How to read this

- **Present** means the code has a screen, endpoint or model for it, found by reading routes and files. It does not mean the
  feature is as deep as the competitor's, or that anyone has used it outside tests.
- **Not found** means a search by the feature's usual names found nothing. Names differ, so each of these needs a person to
  confirm before it goes on a roadmap. Items marked **Confirmed** are stated outright in the product (for example, a dialog that
  says it cannot read `.mpp`).
- The competitor feature lists come from general knowledge, not from re-reading the vendors' current documentation.

## Costpoint

All 12 capabilities the plan listed as missing or partial now have a surface:

| Capability | Where |
|---|---|
| Multi-company, intercompany, consolidation, multicurrency | `/setup/companies`, `/accounting/consolidated`, `/setup/currencies`, `/accounting/foreign-currency`, `/accounting/revaluation` |
| Revenue recognition | `/accounting/revenue` (setup, estimates, per-project, reports) |
| Ceilings, funding alerts, encumbrances, limitation notices | ceilings service and charge guard |
| Project and contract closeout | closeout service |
| Mobile time and expense | `/m` phone app (web, installable); desktop app project exists |
| HR, benefits, compensation, onboarding | `/hr/*` |
| Inventory, MRP, manufacturing and shop floor | `/materials/*` (about 40 screens) |
| Earned value (CPI, SPI) | EVM module, dashboard widget |
| CRM and proposal pricing | `/bd/*`, `/pricing/*` |
| Incurred cost submission | `/accounting/incurred-cost` |
| Report writer | `/reports/writer` |
| Configurable approval workflows | `/setup/approval-workflows` |

Also present: GL, journals, close, AP, AR, billing (T&M, cost-plus, FFP, SF 1443), purchasing with RFQs and a supplier portal,
subcontracts, fixed assets, cash and bank reconciliation, payroll providers, leave, per diem and travel, DCAA / CAS / CPSR
readiness pages, EDI, and importers for Costpoint, Smartsheet and Unanet.

**Not found, needs confirming:** WAWF / iRAPT electronic invoice submission; Service Contract Act and Davis-Bacon wage
determinations; subcontracting-plan (ISR/SSR) reports.

## MS Project

Present: Gantt, all four dependency types with lead and lag, task constraints, project calendars, critical path and float, up
to 11 baselines, status date, deadlines, milestones, percent complete, MS Project XML import and export, earned value, resource
allocations and utilisation, "try changes" what-if on the Plan tab.

| Gap | Status |
|---|---|
| Resource leveling | Not found |
| Task types (fixed units / work / duration) and effort-driven scheduling | Not found |
| Split tasks | Not found |
| Recurring tasks | Not found |
| Subprojects, master project, cross-project links | Not found |
| Network diagram, task usage and resource usage views | Not found |
| Timeline (summary) view | Not found |
| Reading `.mpp` files | **Confirmed** not supported (XML only, said in the import dialog) |

## Smartsheet

Present: grid with 18+ column types, formulas (about 80 functions including `COLLECT`, `INDEX`, `VLOOKUP`, `SUMIFS`), cross-sheet
references, linked rows, conditional formatting, filters, sorts, saved and shared views, grid / board / calendar / Gantt
views, dashboards (17 widget types), sheet reports, public forms with a resettable link, sheet automations (alerts, approvals,
field changes, webhooks), templates and copy, comments with @mentions, attachments, sheet permissions, Smartsheet importer.

| Gap | Status |
|---|---|
| Update requests (ask a person to fill in specific cells by email) | Not found |
| Proofing | Not found |
| Publish a sheet or dashboard as a read-only link | Not found (forms and the customer view have links) |
| Guest or external collaborators on sheets | Not found |
| Scheduled import and export (Data Shuttle) | Not found |
| Packaging sheets and dashboards as an app (WorkApps) | Not found |

## monday.com

Present: boards (board view), timeline-style Gantt, calendar, dashboards, forms, automations with templates, mirrored and
linked items, formula columns, time tracking, workload through Resourcing, templates, AI assistant, API keys and webhooks.

| Gap | Status |
|---|---|
| Importer from monday.com | Not found |
| Timeline view as a view type on any board | Not found |
| Collaborative docs and whiteboards | Not found (rich-text widget only) |
| Guest access | Not found |
| Wide integration catalog (Google, Outlook, Zoom, Salesforce, GitHub, Zapier, Power Automate) | Not found. Present: Jira, GitLab, Slack, Teams, calendar feed, Unanet, Costpoint, public API |
| iOS and Android store apps | Not found. Phone web app and a desktop app project exist |

## Platform

Present: SSO and SCIM, roles and custom roles, audit log, multi-tenant hosting with a database per tenant, PostgreSQL, API keys,
dark mode, undo in grids, global search, notifications bell and email, calendar feed.

Not found: translations (English only), offline use.

## What the checking could not tell us

Nothing here says how well any feature works for a real customer. Gaps in depth would show only in use with real data, which is
why the importers and integrations still need trying against real exports and real services.
