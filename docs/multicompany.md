# Multicompany

Workbench can keep the books of several legal companies (a parent and its subsidiaries, or sister companies) in one
tenant, like Costpoint's multicompany setup. Each company has its own ledger and closes its own periods; people can be
limited to some companies; work charged across companies books intercompany balances automatically; and the
consolidated statements eliminate them.

A tenant always has the **default company** (`Company.DefaultId`, code `MAIN` until renamed). With only that company
nothing changes: there's no company switcher, and every record belongs to it.

## What belongs to a company and what's shared

The line: **transactions and the things that own money belong to one company; reference data that describes the
outside world or the workforce's skills is shared.**

| Company-owned (`CompanyId`) | Why |
|---|---|
| Journal entries and journal lines | The ledger. A line's company is the ledger it posts to; an entry can span companies (intercompany). |
| Per-company period close (`CompanyPeriodClose`) | Subsidiaries close on their own schedule. A period closed for the tenant is closed for all. |
| Contracts and projects | A contract is signed by one legal entity; its projects (and their cost) are that company's. |
| AR invoices and receipts | Billed by, and paid to, the contract's company. A receipt pays one company's invoices. |
| AP bills and payment runs | Owed and paid by one company. A payment run pays one company's bills. |
| Bank accounts | Owned by one company. |
| Fixed assets | Owned and depreciated by one company. |
| Employees (home company) | The employer of record: labor cost is accrued (payroll) there, wherever the hours are charged. |
| Indirect pool setups | Pools are a company's cost structure. (See "Not yet" below.) |

| Shared by every company | Why |
|---|---|
| Chart of accounts | One chart keeps consolidation simple. An account can be limited to one company (`Account.CompanyId`); the ledger then refuses it elsewhere. |
| Fiscal calendar | Companies share periods so consolidated statements line up. `Company.FiscalYearStartMonth` must match it. |
| Vendors and customers | The same agency or supplier deals with several companies. |
| Labor categories, org units, people records | Skills and structure are the same across companies; an employee's company is an attribute, not a separate person. |
| Posting rules | The same cost maps to the same account in every company. |

Every company's home currency is `USD` (`Company.HomeCurrencyCode`); the multicurrency work owns it from here.

## How a record gets its company

- Migration: every `CompanyId` column is added with the default company's id as its default, and the default company is
  model seed data, so existing rows land in it.
- New records go to the company the user is working in (the app bar switcher), or the default company when they work in
  all of them. Services that create company-owned roots (contracts, projects without a contract, employees, AP bills,
  bank accounts, fixed assets, manual journal entries) are stamped by the company decorators.
- Derived records take their parent's company: a project its contract's, an invoice its contract's, a receipt its
  invoices', a payment run its bills', an asset capitalized from a bill the bill's.
- Records made in the wrong company are moved under **Setup › Companies › Move records**. Anything with posted ledger
  activity stays where it is.

## Intercompany

`GeneralLedger.PostAsync` applies `CompanyLedger` to every entry from every module:

1. Each line posts to its project's company, else to the entry's company. The entry's company is set by the posting
   module (a timesheet: the employee's home company; a bill, invoice, receipt or asset: its company), else it's the
   first project line's company, else the default company.
2. If the lines span companies, each other company's net is moved to the intercompany accounts configured for the pair
   (`IntercompanyAccount`, one row each way; **Setup › Companies › Intercompany accounts**). Example: Jordan (FED)
   charges 8 hours at $58 to MAIN's project 1001:

   | Company | Account | Debit | Credit |
   |---|---|---|---|
   | MAIN | 5010 Direct labor (project 1001) | 464.00 | |
   | MAIN | 2180 Due to affiliated companies (FED) | | 464.00 |
   | FED | 2100 Accrued payroll | | 464.00 |
   | FED | 1180 Due from affiliated companies (MAIN) | 464.00 | |

3. The generated lines carry `IntercompanyCompanyId` (the counterparty). Each company's lines balance on their own.
4. Inactive companies, and companies that closed the period, refuse the posting. A pair without intercompany accounts
   refuses it too, with a message pointing at the setup. Adding a company sets up 1180/2180 to every other company when
   the default chart's accounts exist.

## Consolidation

`IConsolidationService` builds each company's trial balance, income statement or balance sheet with the same rules as
the single-company reports, then an eliminations column: every line whose company and counterparty are both in the
group, reversed. The total is the companies plus the eliminations. The report lists the eliminations and warns when two
companies disagree about what they owe each other, or when balances with a company outside the group aren't eliminated.

## Access

- Finance and Admin manage companies, intercompany accounts, record moves, company period close and consolidation.
- Admin decides who may use which company (`UserCompanyAccess`; no rows = every company).
- What a user sees is their companies, narrowed to the one picked in the switcher (`CompanyScope.View.Visible`). The
  company decorators (`Identity/CompanyPermissionDecorators.cs`, outermost) make records of other companies not found
  and drop them from lists for journal entries, contracts, projects, invoices, receipts, AP bills and payment runs,
  assets and bank accounts. The ledger reports (`AccountingReportService`) filter their sums by the same companies.
- Charge codes and people aren't filtered: anyone may charge time to another company's project.

## Not yet

- Indirect pools carry a company, but the allocation run still allocates the tenant's pools (lines inherit their
  projects' companies, so burden applied across companies books intercompany balances).
- Payroll registers post to the default company; Paylocity company codes per company are a follow-up.
- Read models outside the services above (projects hub, report writer, dashboards, project financials) show every
  company's records to people their role lets see them.
- Intercompany billing (one company invoicing another at a markup) and multicurrency translation.
