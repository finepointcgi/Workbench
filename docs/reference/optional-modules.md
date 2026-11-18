# Optional modules

Workbench has a free core and a set of **optional modules**. The core is everything most teams need to run projects: time, expenses, approvals, projects and planning, the general ledger, invoicing and receivables, payables and purchasing, resourcing and leave, reports, sheets, dashboards, the API and the assistant. The modules add specialist areas on top.

A module is installed separately and unlocked by your license key. If your license does not include a module, its menu entries, pages and API routes are simply not there. Nothing else changes, and your data is never touched. Adding a module later makes its area appear after a restart.

## The modules

| Module | What it adds |
| --- | --- |
| **Government contracting** (`govcon`) | Government-contractor compliance: DCAA timekeeping and incurred cost, CMMC assessment and the related SF forms. |
| **Billing** (`billing`) | Revenue recognition and estimates, government billing (SF 1443 progress payments) and live push to QuickBooks and Xero. |
| **Capture** (`capture`) | Business development: the opportunity pipeline, accounts, lead import and proposal pricing. |
| **Materials** (`materials`) | Parts and inventory, manufacturing, quality and engineering changes, sales orders and shipping. |
| **Project controls** (`projectcontrols`) | Earned value, project risk register and contract ceilings. |
| **Workforce** (`workforce`) | HR administration, the directory and org chart, and payroll including the Paylocity integration. |
| **Accounting** (`accounting`) | Cash and bank reconciliation, fixed assets, corporate budgets and planning. |
| **Enterprise** (`enterprise`) | The Costpoint sync, Jira and GitLab integrations and the enterprise administration features. |

## How to tell whether you have one

Pages in this guide that describe a module start with an **Optional module** note. If you cannot find the menu entry the page describes, your license does not include that module, or it is licensed but not installed (the server log then says so at start-up). Ask your administrator, or see [Editions](../editions.md) for how licenses work and [Modules and pricing](../commercial/modules-and-pricing.md) for the plans.

## For administrators

Modules live in the `modules/` folder next to the application, one folder per module. The license key is checked before a module's code is loaded, so an unlicensed module is never loaded. See [License refresh](../admin-guide/license-refresh.md) for connected installs and [Hosting and operations](../admin-guide/hosting.md) for where the folder goes.
