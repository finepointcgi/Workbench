# Company type and wording

<p class="roles">Administrators</p>

Workbench started as a government-contracting system, so its default words are federal: contract, CLIN, funded value,
ceiling, period of performance, contracting officer. If you are not a government contractor, set your **company type**
and Workbench uses your words and keeps the federal pages out of your way.

## Choose it

Open **Setup › Organization** (the Setup page asks the first time) and pick one:

| Company type | What it means | Words you see |
|---|---|---|
| **Government contractor** (default) | Federal and prime/sub work. Nothing changes. | contract, CLIN, funded value, ceiling, period of performance, contracting officer |
| **Professional services** | Consulting, agencies, IT services. | agreement, billing line, authorized budget, not-to-exceed, term, client contact |
| **Engineering and construction** | Design, build and field work. | contract, pay item, authorized budget, not-to-exceed, contract duration, owner's representative |

*Customer* and *proposal* stay the same for everyone. Only an Admin can change the type, and the change is audited.

## What changes

- **Wording** on the high-traffic screens: the agreements/contracts list and page, the project start flow, the project
  Overview and Setup tab, the won-opportunity handoff, My Work to-dos, notifications and the menu.
- **Funding and ceiling inputs** read as optional for commercial types ("Not-to-exceed (optional)"); leave them empty
  when there is no cap.
- **Federal pages are hidden** from the menu and Ctrl+K: DCAA readiness, Purchasing system review (CPSR), CAS
  disclosure statement, CMMC assessment, Incurred cost and SAM.gov Leads.

## Show government contracting features

The switch under the company type decides whether those federal pages show. It follows the type (on for a government
contractor, off for the others) until you set it yourself. A professional-services firm with one federal subcontract can
turn it on and keep the pages.

## What does not change

Your data, the API and every page address work the same whatever the setting. Hiding is only about what the menu
shows; someone who opens `admin/cmmc` directly still gets the page if they have the permission. Some deeper screens
still use federal words (see below), and switching the type back restores the original wording at once.

## Not yet reworded

Wording is applied to the screens above. Federal terms remain in: invoice and billing pages (WAWF, SF-1034 style
language), the Ceilings and Closeout panels, the CLIN and funding dialogs, reports and their column names, the help
articles, emails with fixed templates, the command palette titles and breadcrumbs.
