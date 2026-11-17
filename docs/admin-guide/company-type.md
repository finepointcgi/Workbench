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

- **Wording** across the product: agreements/contracts and their tabs (billing lines, authorized budget, not-to-exceed
  limits, change orders, closeout), the project hub and its tabs, billing and invoice pages, reports (titles, column
  headings and notes), dashboard widgets, the glossary and its tooltips, subcontracts, revenue, the Plan approval bar,
  My Work to-dos, notifications and emails, snackbar messages (including validation messages from the server), the menu,
  breadcrumbs, the New menu and Ctrl+K.
- **Server-built text** follows the type where it is stored or sent: notification titles and bodies (so emails and the
  daily digest too), to-do titles, report titles and headings, and the work-breakdown names made when a project starts
  from an agreement ("Billing line 0001").
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

## What still reads federal

- **Federal forms and filings** (SF 1034, SF 1443, DD 250, WAWF/PIEE, performance-based payments, progress payments,
  the incurred cost submission) keep their official names. Those pages only matter to a federal contract.
- **Cost-accounting references** on shared pages (the "Unallowable cost (FAR 31.205)" label on expenses, DCAA-approved
  rate examples, weighted guidelines, government-furnished property) keep their regulation names.
- **Words people typed**: names, labels, descriptions and notes you entered, and imported data, are never reworded.
- **The in-app help articles**: the glossary and its tooltips follow the type, the long articles do not yet.
- **Static columns** in a few lists ("No contract" in the projects list, "of ceiling" in the agreements list).

## For developers: adding or changing a term

All wording lives in one table, `CompanyTerms` (`src/Workbench.Contracts/Company/CompanyTerms.cs`). To reword a concept
add one row: the key (lower case, plural as its own row) and three words, government, professional services,
engineering and construction. A type with no entry of its own uses the government word.

```csharp
["task order"] = ("task order", "work order", "work order"),
```

Use it in three places:

- **Razor**: `[CascadingParameter] ITerms? Tm` and `@Tm.Term("contract")` for one word or `@Tm.Reword("Add the CLINs first")`
  for a sentence. `Tm` is null in tests without a layout, which reads as the government wording. Dialogs get it too.
  Strings inside a static field initializer can't reach `Tm`; move them into a property or reword where they are shown.
- **Server code** that words text for a person (notifications, to-dos, report titles, names it writes): load the terms with
  `CompanyProfileService.LoadTermsAsync(dbFactory)` and call `ReplaceTerms`. `Notifier`, `MyTodoService` and
  `StandardReportService` already do. Messages shown through `Snackbar.Add` are reworded for you.
- **A federal-only page**: set `Government: true` on its `NavEntry` so commercial types do not see it.

A guard test (`CompanyWordingGuardTests`) fails when a Razor literal names a federal term without going through `Tm`,
and when menu, report or panel text still reads federal in commercial mode. If a literal is genuinely federal-only, add
it to `tests/Workbench.Tests/CompanyTypes/company-wording-allowlist.txt` (a file path, or `path|text`) and say why.
