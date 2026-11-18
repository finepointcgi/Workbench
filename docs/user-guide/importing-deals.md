# Importing deals from HubSpot, Salesforce or a spreadsheet

!!! info "Optional module: Capture"
    This page describes the **Capture** module, an optional add-on. It appears only when your license includes it. See [Optional modules](../reference/optional-modules.md).

<p class="roles">Administrators, or Finance for companies and contacts. Deals need business development or an administrator.</p>

Bring your existing pipeline across: companies, the people at them, and the deals you're working. It's the same wizard as
the other imports (**Admin › Setup › Import data**, or the **HubSpot, Salesforce or a spreadsheet** card on
**Business development › Settings**): upload, map the columns, validate, dry run, import. Every import can be rolled back.

## Do them in this order

1. **Companies**, then **Contacts**, then **Deals**. Contacts and deals find their company **by name as written in the
   export**, so the company has to be in Workbench first. A deal whose company isn't there is still imported; it keeps the
   company's name as text and you get a warning.
2. Export each list from your system (below), choose **HubSpot**, **Salesforce** or **Plain CSV / Excel**, and upload it.
   Columns are matched by name automatically; change any match on the mapping screen.

## What nothing ever does: overwrite without asking

Before anything changes, the dry run lists every row that matches a record already in Workbench as a **duplicate** and says
what it matched on. By default those rows are **left exactly as they are**. To update them from the file, tick **Update
records that are already in Workbench** on the mapping screen and run the dry run again: only the cells the file fills in
change, blank cells keep what Workbench has, and the dry run shows each change before you import.

| Records | Matched on, in this order |
|---|---|
| Companies | Website domain (`https://www.example.com/about` and `example.com` are the same), then company name |
| Contacts | Email, then first and last name at the same company |
| Deals | The source system's id (HubSpot Record ID, Salesforce Id), then deal name at the same company |

A file that lists the same company, contact or deal twice is reported as an error on the second row.

## From HubSpot

Open the companies, contacts or deals list, choose the properties you want as columns, and **Export** (CSV or Excel). For
contacts and deals include the associated company: HubSpot exports it as an association column. The auto-mapping reads
HubSpot's default property names: *Company name*, *Company domain name*, *Phone number*, *City*, *State/Region*, *Industry*,
*Type*; *First name*, *Last name*, *Email*, *Job title*, *Contact owner*; *Deal name*, *Amount*, *Deal stage*, *Close date*,
*Deal owner*, *Deal type*, *Record ID*.

| HubSpot deal stage (default Sales Pipeline) | Workbench stage |
|---|---|
| Appointment scheduled | Identified |
| Qualified to buy | Qualified |
| Presentation scheduled | Capture (pursuing) |
| Decision maker bought-in | Proposal |
| Contract sent | Submitted |
| Closed won | Won |
| Closed lost | Lost |

## From Salesforce

Either run a report (Accounts, Contacts, Opportunities) and **Export → Details Only**, or export with Data Loader or the
Data Export service. The auto-mapping reads both the API field names (*Name*, *Website*, *BillingCity*, *FirstName*,
*LastName*, *Email*, *Title*, *Account.Name*, *StageName*, *Amount*, *Probability*, *CloseDate*, *Type*, *LeadSource*,
*NextStep*, *IsClosed*, *IsWon*) and the labels a report uses (*Account Name*, *Opportunity Name*, *Stage*,
*Probability (%)*, *Close Date*).

The Data Loader gives a contact or opportunity's company as an `AccountId` (an id, not a name). Include the account **name**
(a report column, or `Account.Name` in a query) so Workbench can find the company.

| Salesforce stage (usual) | Workbench stage |
|---|---|
| Prospecting | Identified |
| Qualification, Needs Analysis | Qualified |
| Value Proposition, Id. Decision Makers, Perception Analysis | Capture (pursuing) |
| Proposal/Price Quote | Proposal |
| Negotiation/Review | Submitted |
| Closed Won | Won |
| Closed Lost | Lost |

## From any spreadsheet

Choose **Plain CSV / Excel**. Common column names are recognized (*Company*, *Website*, *First name*, *Email*, *Deal*,
*Value*, *Stage*, *Expected Close*, *Owner*…); map the rest by hand. A full name can be one column (*Jane Smith* or
*Smith, Jane*). Without an id column, deals are matched on name and company.

## How amounts, stages and dates come across

- **Amount** becomes the deal's **estimated value**; the **close date** is its expected award date (for won and lost deals,
  the date it was decided). Won deals take their amount as the awarded value.
- **Win probability** becomes Pwin (0–100, or 0–1). Without one, the stage's usual odds are used (Qualified 25%, Capture 50%,
  Proposal 70%, Submitted 85%). A deal's go probability is set to 100%, so the weighted pipeline is value × win probability.
- A **custom stage** is read by its words first, then placed by its probability (a warning tells you where it landed). If it
  can't be placed, the row is an error: rename the stage in the file, or map a probability column.
- Salesforce's *IsClosed* and *IsWon* settle a closed deal with an unrecognized stage. A "closed lost reason" column becomes
  the win/loss reason.
- *Existing business*, *Upgrade* and *Renewal* deal types are marked as repeat work; anything else is new.
- The deal **owner** is matched to a Workbench user by email or name; otherwise a warning is shown and the lead is left empty.
- Imported stages are history: they don't go through the pursuit gates, don't create charge codes and don't notify anyone.

Sample files are in `samples/import/hubspot`, `samples/import/salesforce` and `samples/import/generic`.
