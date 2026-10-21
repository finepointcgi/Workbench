# Simplicity review

October 2026. A walk through the main tasks as each demo persona (`/account/dev-signin`, fresh demo database), counting
clicks and pages and noting where a first-time user would get stuck. The theme (Material 3, `Theme/WorkbenchTheme.cs`,
`wwwroot/theme.css`) was out of scope. Fixes keep WCAG 2.1 AA: new text colours were checked (warning text 5.3:1 on the
surface), new controls have accessible names, and step state is announced in text, not only by icon.

## The question that started it: "Should I allocate hours to the person or to the labor category?"

Both. In that order:

1. **Budget by labor category.** The contract is priced and funded by labor category (PLC/LCAT), so the budget is
   hours of each labor category on each WBS element: "1,200 Systems Engineer II hours on 1.2".
2. **Staff people into those categories.** Each person bills as a labor category. Staffing puts a named person's hours
   a week on a WBS element, in that category, until the budget's hours are covered.
3. **Timesheets charge actual hours by person.** They roll up to the labor category for billing and are compared with
   the labor-category budget.

### Before

- Budget lines (hours × rate by labor category) and resourcing allocations (hours by person, optional labor category)
  both asked for hours. Nothing said how they related or which came first.
- Budgeting: project › Budgets tab › New version. That opened a dialog asking for a name, a kind (defaulting to
  *Baseline*, which staffing then treats as a fallback) and a source. Then pick WBS and category, add a row, type hours,
  save. The empty state said "No budget versions yet".
- Staffing lived on a separate Resourcing page. The heat map's "Add allocation" dialog had no labor category, no budget
  and no warnings. The Project staffing tab compared hours by labor category only (not by WBS element) and was read-only.
  The project page's Team tab sent you to Resourcing.
- Nothing showed unstaffed budget on the project or in the PM's to-dos.
- Terms: allocation / assignment / staffing / "planned" were used for the same thing. "Plan" meant the schedule, the
  budget ("planning rate") and allocations ("who is planned where").

### After

- **Overview › "Set up this project"** (for Finance, admins, resource managers and the project's manager) shows three
  steps with progress: *Contract and WBS → Budget by labor category → Staff people*. Each step has one plain sentence,
  a status ("0 of 520 hours left are staffed; 520 have nobody yet") and a button to the right tab. It disappears when
  all three are done.
- **Budget tab** (was "Budgets"): an empty project shows "No budget yet" with **Start the budget**, which makes an empty
  *Current* version in one click, and **Start from the plan sheet**. The new-version dialog defaults to Current and
  explains each kind.
- **Staffing tab** (replaces Team, which now sits below it). It shows budgeted, charged, left and staffed hours with the
  gap, first per labor category and then per WBS element × labor category. Each row has the people on it and a
  **Staff** button. Hours charged where there's no budget show as their own row.
- **Staff a person** starts from the row's labor category and WBS element. People who bill as that category come first,
  with a check and how booked they are. Hours default to their free hours and weeks to whatever closes the gap. It warns,
  without blocking, when the person doesn't qualify for the category, when the hours run past the budget, when the
  category isn't budgeted there, or when the contract has no bill rate for it.
- **Unstaffed budget and over-allocation** show as Overview alerts and PM to-dos. Examples: "400 budgeted hours on 1002
  have nobody staffed: 400 h of SE2 Systems Engineer II on 1002.01.02", "1001 is staffed 64 hours over its budget",
  "2 people on 1002 are booked past their hours". Projects without a labor budget make one combined to-do.
- **Timesheets** start with the WBS elements you're staffed on that week as rows (empty, not pre-filled).
- Sam's flow on project 1002, measured: Overview › Start the budget (2 clicks) › add 2 rows (8 clicks) › type hours ›
  Save › Staffing tab › Staff (2 clicks per row, everything pre-filled). About 20 clicks on 3 tabs of one page, down
  from two pages and about 30 clicks, with no guidance before.

### Terminology (one word each)

| Idea | Word in the UI | Not |
|---|---|---|
| A job title the contract prices and bills by | **Labor category** (glossary: "contracts may call it a PLC or LCAT") | PLC, LCAT, position |
| Hours by labor category (and ODC dollars) per WBS element | **Budget** | plan |
| The schedule: tasks, dates, who does them | **Plan** (the plan sheet) | budget |
| A person's hours a week on a project, in their labor category | **Staffing / staffed hours** | allocation, planned, assigned |
| Permission to charge a WBS element | **Can charge** (setup: charge assignments) | assigned |

Glossary entries (`Ui/Help/Glossary.cs`): `labor-category`, `budget`, `plan`, `staffing`, `allocation-hours` (now
"Staffed hours"), `voucher`. Code and API names (`ResourceAllocation`, `/resourcing/allocations`) are unchanged.
"Allocation" still means indirect cost allocation in accounting, which is its standard meaning.

## Persona walks

Clicks count from Home, after sign-in. "Pages" are distinct routes.

### Alex, employee

| Task | Clicks / pages | Notes |
|---|---|---|
| Enter a timesheet | 1 click to the grid (Home › Enter time), type, Save, Certify and submit + dialog: ~4 clicks, 1 page | Good. **Fixed:** codes Alex is staffed on now appear as rows. The welcome tour opens over the grid on first visit. |
| Submit an expense | Home to-do › report (1 click), add lines and receipts in the line dialog, Submit: ~6+ clicks, 2 pages | Two separate to-dos for the same card charges ("Submit 1 overdue card charge" and "Add 2 card charges to an expense report"). **Fixed:** the list page explains reports, receipts, approval and card charges. |
| Request leave | My work › Leave (2 clicks), inline form, Send: ~5 clicks, 1 page | Good: one page, plain explanation, empty state says what happens next. |

### Sam, project manager

| Task | Clicks / pages | Notes |
|---|---|---|
| Set up, budget and staff a project | See "After" above: ~20 clicks, 1 page with 3 tabs | **Fixed** (this work). |
| Read its health | Projects › project (2 clicks), Overview | Good: plain-language tiles with glossary hovers, health chip, alerts that link to the tab. **Fixed:** staffing alerts. |
| Approve time | Approvals (1 click) | Sam approves nothing: time goes to each person's supervisor. A PM who isn't the supervisor can't approve project time. See the open list. |

### Pat, Finance

| Task | Clicks / pages | Notes |
|---|---|---|
| Close the month | Finance › Accounting › Period close (3 clicks); fix each failing check elsewhere | The checklist is thorough, but its guidance named menu paths ("Run it under Accounting › Indirect rates › Allocation runs"). **Fixed:** each failing check has a button to where it's fixed. |
| Invoice | Finance › Billing & AR › Invoices › New invoice (4 clicks) | Fine. |
| Pay bills | Finance › Payables & purchasing › Bills; paying is a different menu item (Payment runs) | Overdue bills weren't connected to paying them. **Fixed:** "Pay bills" button on Bills. "Voucher" (Costpoint jargon) is now "Bill #", with the old word in its tooltip. |
| Home | 8 to-dos this week | **Fixed:** three identical "Budget X by labor category" to-dos are now one. |

### Dana, administrator

| Task | Clicks / pages | Notes |
|---|---|---|
| Invite a user | Admin › People and access › Users and security › Invites tab › form (4–5 clicks) | The Users tab, where you'd look first, has no Invite button. Its table has 11 role toggle columns. Left alone: the custom-roles session is changing this screen. |
| Change settings | Admin › Organization (2 clicks) | "Organization" holds only the timesheet cadence. The Setup checklist (14 steps, ticks itself) is clear and links everywhere. |

## Fixed in this pass

1. Plan-and-staff flow: setup steps, Budget one-click start, Staffing tab, Staff a person with qualification and
   budget warnings, overview alerts, PM to-dos (service: `IProjectStaffingService`, tests in
   `tests/Workbench.Tests/Resourcing/ProjectStaffingTests.cs`).
2. One word per idea across Resourcing, the project page, Home's "This week" strip, the menu and the glossary.
3. Project tabs follow the setup order (Overview, Plan, Budget, Staffing, Financials, Costs, …). Team merged into
   Staffing. Resourcing's Project staffing tab reuses the same panel instead of a second, read-only table. Old URLs
   (`/team`, `/budget`) still work.
4. Timesheets start with the rows you're staffed on.
5. Period close links each failing check to where it's fixed.
6. Bills: Pay bills button; "Bill #" instead of "Voucher".
7. Approvals intro: one sentence instead of a list of the old module pages.
8. Expense list: what a report is and what happens after submitting.
9. Combined to-do for several unbudgeted projects.
10. Glossary terms inside paragraphs (`<Term>` in `MudText`) no longer break the paragraph in two when prerendered (a
    tooltip `<div>` inside a `<p>`). Fixed in 9 places, so each sentence reads as one.

## Still to do (prioritized)

1. **Too many menu items for Finance.** Pat (Finance + PM) sees about 110 links in 15 groups, including Materials, HR
   administration, BD and Compliance. Proposal: a role "workspace" switch (Projects / Finance / People / Materials) that
   shows one section's menu at a time, and hide modules the organization hasn't set up (e.g. Materials with no parts).
   Owner: navigation (`NavCatalog.cs`).
2. **Project-based time approval.** Let a workflow route a timesheet's project lines to the project's manager as well as
   (or instead of) the supervisor. Sam's "approve time" task has no path today. Owner: approval workflows.
3. **Invite from the Users tab.** Add an "Invite someone" primary button on Users and collapse the 11 role columns into
   one "Roles" cell with an edit dialog. Coordinate with the custom-roles work.
4. **Welcome tour placement.** It opens over the main content (the timesheet grid, the project page) on each persona's
   first page. Start it from the help menu or as a small dismissible banner instead.
5. **Duplicate card-charge to-dos.** Merge "Submit N overdue card charges" and "Add N card charges to an expense
   report" into one item with the overdue count in the reason.
6. **Duplicate approval paths.** Bills has its own "Approval queue" next to the unified Approvals inbox (and likewise
   time, ODCs and requisitions). Keep the module queues only for bulk actions the inbox can't do, or link them from it.
7. **Rename "Organization" setup.** It holds timesheet periods only. Call it "Timesheet periods" until it holds more.
8. **Employee menu trims.** Alex sees "My budgets" and "Report writer". Show "My budgets" only to budget owners and
   the report writer only to people who have saved a report or are Finance.
9. **Company switcher for employees.** "All companies" sits in every user's header. Hide it for people in one company.
10. **Staffing tab polish.** Edit a person's hours in place (today: remove and staff again, or use the heat map); phase
    staffing against budget periods when the budget is phased; show utilization next to each candidate.
11. **Budget editor.** Adding a labor row takes 3 clicks per row (WBS, category, Add). Allow picking several categories
    at once, or start a new budget with a row per leaf WBS element × the contract's labor categories.
12. **Wide tables at 800 px.** The Users table, invoice list and staffing slots scroll sideways in a narrow window. Use
    `Breakpoint.Sm` card rows like the Approvals inbox.
