# Accounting export (QuickBooks, Xero and files)

<p class="roles">Finance and administrators (permission: Export to accounting software)</p>

Many small firms keep QuickBooks or Xero as their books and use Workbench for time, projects and billing. **Admin › Integrations ›
Accounting export** makes the files those packages import, from what Workbench holds: journal entries, customer invoices, customer
payments, vendor bills and the chart of accounts. You can download a file and import it there, or connect QuickBooks Online or Xero and
[push straight into the books](accounting-live-push.md) (journal entries, invoices and bills; not tested against the real products).

## Formats

| Format | Package | Can carry | Imported with |
|---|---|---|---|
| QuickBooks Online (CSV) | QuickBooks | Journal entries, invoices, bills, chart of accounts | Settings › Import data |
| QuickBooks Desktop (IIF) | QuickBooks | Journal entries, invoices, bills, chart of accounts | File › Utilities › Import › IIF Files |
| Xero (CSV) | Xero | Journal entries (manual journals), invoices, bills, chart of accounts | Each import screen in Xero |
| Generic CSV and Excel | Any | Everything above and customer payments | Spreadsheet, other packages, your accountant |

Customer payments are in the generic files only: neither package has a documented file import for them that carries what invoice a
payment applies to. Vendor payments are not exported; record them in the package.

## The first export, step by step

1. **Export the chart of accounts** and import it into the package first (or map Workbench accounts to accounts you already have).
2. Open **Account mapping**, choose QuickBooks or Xero, and give each Workbench account, customer, vendor, project (class) and invoice line
   type the name it has in the package. **Fill blanks with Workbench names** is a good start if you imported the chart from Workbench.
3. On **Export**, choose the format and kind and the period, then **Preview**. Fix what it flags, then **Export and download**.
4. Import the file. Check the **control total** in the history against the package.

## Account mapping

Mappings are saved per package (QuickBooks and Xero each have their own) and shared by every format for that package.

| Workbench | Maps to |
|---|---|
| GL accounts | QuickBooks account name (sub-accounts as `Parent:Sub`); Xero account name and **code** |
| Customers and vendors | The customer or vendor name in the package |
| Projects | QuickBooks **class**; Xero **tracking option**, with the tracking category's name in the code column |
| Invoice line types (labor, ODC, fee, sales tax, ...) | QuickBooks product or service; not used by Xero (it takes the revenue account) |
| Tax code | The tax code or rate name that exported lines carry. Workbench bills sales tax as its own invoice line, so every line carries the same "no tax" code |

Anything **in use and unmapped** (accounts, customers, vendors, items) stops the export: the package would not recognise Workbench's
names. The preview lists them. You can choose **Export even if some names aren't mapped**; the file then uses Workbench's names, the run is
marked in the history, and you fix the names in the package. Unmapped projects and the tax code only produce warnings. Generic files never need mappings.

## Nothing is exported twice

The rule is the point of this page:

- Each record (journal entry, invoice, bill, payment, account) is remembered per package once it has been exported.
- The next export of **any** period, even an overlapping one, includes only records not yet exported. An entry posted late into a period you
  already exported is therefore picked up by the next export, and nothing is sent twice.
- A period where everything has already been exported is refused, naming how many records were exported.
- **Export again** is for when you deleted the first import or lost the file. It needs a **note**, includes everything in the period again,
  and the run is marked *Again* in the history. Downloading a file from the **History** tab gives back exactly the same bytes.
- Records a format can't carry (credit memos in a QuickBooks CSV, for example) are listed in the warnings and are **not** marked, so another
  format can still take them.
- Two people exporting at the same moment can't both take a record; the second gets a conflict and nothing is saved.
- If you void an invoice, payment or bill in Workbench after exporting it, the next export warns you so you can void or delete it in the package.

Each run records who, when, the period, format, row and record counts, a control total, any warnings and the SHA-256 of the file, and appears in
the security log (`export.accounting`) and the audit trail.

## What each file contains

**Journal entries.** Posted entries (and reversals) dated in the period. Entries made by invoices, payments and bills are left out unless you
ask for them, because the invoice and bill files already carry those; exporting both would count them twice. Draft entries are never exported.
Line classes come from the line's project.

**Invoices.** Approved, sent, partly paid and paid invoices dated in the period, one line per invoice line. Each line's revenue account is the account
Workbench posted it to. Credit memos are left out of the QuickBooks and Xero files (neither invoice import takes them) and kept in the generic files.

**Bills.** Posted bills, with the bill's GL account lines; project cost lines use the account their posting rule gives. Vendor credits are left out of the package files.

**Foreign currency and intercompany.** Everything is exported in the functional currency; the preview warns when documents are in another currency
or entries carry due-to/due-from lines.

## Package limits the files respect (and warn about)

- QuickBooks Online: up to 1,000 rows and 100 invoices (bills) per import; no negative invoice lines or credit memos; invoices can't be imported when
  automated sales tax is on; turn account numbers off before importing journal entries; lines on Accounts Receivable or Payable need a customer or vendor name.
- Xero: manual journals up to 300 lines per import, and no bank or system (receivable, payable) accounts in them; imported invoices and bills arrive as drafts.
- QuickBooks Desktop: names are limited to 31 (accounts) and 41 (customers, vendors) characters; accounts and items must exist first.

Export a shorter period when a warning says the file is over a limit.

## Layout sources and what has not been verified

The layouts follow the vendors' public documentation where it could be read, and the code comments name each source:

- QuickBooks Online: Intuit help articles for importing [journal entries](https://quickbooks.intuit.com/learn-support/en-us/help-article/import-export-data-files/import-journal-entries-quickbooks-online/L4tQBwbs7_US_en_US),
  [invoices](https://quickbooks.intuit.com/learn-support/en-us/help-article/import-export-data-files/import-multiple-invoices/L7E9Xrd8l_US_en_US),
  [bills](https://quickbooks.intuit.com/learn-support/en-us/help-articles/importing-your-bills/00/261324) and the
  [chart of accounts](https://quickbooks.intuit.com/learn-support/en-us/getting-started/import-chart-of-accounts/00/263167).
  Intuit prints the required fields and rules but not the sample files' exact header text; QuickBooks maps columns on screen, so the headers are for people.
- QuickBooks Desktop: Intuit's [IIF overview](https://quickbooks.intuit.com/learn-support/en-us/help-article/list-management/iif-overview-import-kit-sample-files-headers/L5CZIpJne_US_en_US).
  The per-transaction field lists are in Intuit's IIF Import Kit, which was not available when this was built.
- Xero: Xero Central's import articles could not be read when this was built, so the layouts follow third-party write-ups of Xero's templates.

!!! warning "Not tested against the real products"
    No file has been imported into QuickBooks Online, QuickBooks Desktop or Xero. Before relying on a format, import a small file into a test
    company and compare it with the product's own downloadable sample. Specific points to check: the QuickBooks Online account type spellings and
    column headers, QuickBooks Desktop's IIF field lists and amount signs, Xero's exact template headers, the Xero account type names and its
    handling of the date style, and how a duplicate journal or invoice number is treated.

## Pushing directly

QuickBooks Online and Xero can also be fed over their APIs, with the same mapping, preview, history and never-twice rule: see [Push to QuickBooks Online and Xero](accounting-live-push.md).
The file formats stay as the offline fallback.

## For developers

- Add a format: one `IAccountingExporter` in `Application/AccountingExport/Formats` and one registration line in `AccountingExportRegistration`;
  add a golden file per kind (`UPDATE_GOLDEN=1 dotnet test --filter ExportLayoutTests`) and read the diff.
- API: `/api/v1/accounting-export` (`formats`, `preview`, `export`, `runs`, `runs/{id}/file`, `mappings/{target}`), in the API key area **Accounting**.
- The scheduled per-company **General ledger export** connector (Admin › Integrations › Connectors) is separate: it sends a company's journal on a
  schedule over SFTP and doesn't keep the idempotency record described here.
