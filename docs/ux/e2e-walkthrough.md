# End-to-end walkthrough: one contract, award to reports

October 24 2026 (a Saturday). One T&M contract taken through its whole life by each role in turn, in the built-in browser,
on a fresh demo database (SQLite, its own file, port 5461; nothing real, dev sign-in only). The point was the joins between
features, not the features themselves: where a person has to know something the screen doesn't tell them, where the next
step isn't offered, and where one feature's output isn't the next one's input.

| Role | Demo user | Did |
| --- | --- | --- |
| Finance | Pat Morgan (`pm@example.com`, Finance + PM) | Contract W91QUZ-27-C-0042, bill rates, CLIN 0001, funding, project 4202, plan approval, timesheet and expense approval, invoice, revenue, close checklist, reports |
| PM | Sam Patel (`sam.pm@example.com`) | WBS and people on the Plan tab, submit |
| Employee | Alex Rivera (`analyst@example.com`) | Time on 4202.01.01, an expense report |

The walk: **contract → CLIN → funding → project → WBS + people → submit → approve → time → approve → expense → approve →
invoice → revenue → close checklist → project status (EAC) → incurred cost**. Every step completed, two only with a manual
workaround (A2 and A3 below).

## Clicks to done

Counted as clicks plus fields filled, on the fastest path once you know it.

| Step | Who | Clicks + fields | Notes |
| --- | --- | --- | --- |
| New contract (number, customer, name, type, PoP, ceiling, funded) | Finance | 12 | |
| Three bill rates | Finance | 15 | One dialog per rate |
| CLIN 0001 | Finance | 7 | Now 5: number and pricing are filled in |
| Fund the CLIN (modification P00001) | Finance | 8 | The funded value typed on the contract form doesn't count |
| Project from the contract, PM picked | Finance | 6 | Now 3 from the contract page's **Start a project** |
| WBS: three rows under the CLIN | PM | 9 | 3 to type, 3 to indent, 3 durations |
| People grid: three people | PM | 15 | Plus a remove-and-re-add (B3) |
| Submit plan | PM | 2 | |
| Approve plan | Finance | 2 | From the home to-do. Good |
| Let the employee charge (workaround, A2) | Finance | 10 | |
| Timesheet | Employee | 8 | Fill, fix what the ceiling refused (B5), certify |
| Approve timesheet | Supervisor | 1 | 3 to read the flags first |
| Expense report with one line | Employee | 12 | |
| Approve expense | Supervisor | 1 | |
| Invoice: preview, draft, approve and post | Finance | 6 | Good: contract type picked the invoice type, the funding check is right there |
| Revenue run | Finance | 1 | But see B6 |
| Project status report (ETC/EAC) | Finance | 2 | |
| Incurred cost | Finance | 3 | Found through search, not Reports (C5) |

About **120 clicks and fields** for the whole life of one small contract, about 50 of them before the PM sees anything.

## Fixed on this branch

Each has a test that fails without the fix.

| What | Where | Test |
| --- | --- | --- |
| **Indenting plan rows didn't move their charge codes.** Rows typed with "Type a task and press Enter" land at the top level; indenting them under the CLIN with Tab left their WBS elements top-level (4202.02, 4202.03 …) and the CLIN row chargeable, while the grid's WBS column already showed 4202.01.01. Time would have been charged to the wrong codes. Indent, outdent and duration edits (`PlanScheduleService`) now run the same WBS sync as every other plan edit. | `Application/Scheduling/PlanScheduleService.cs` | `ProjectPlanTests.Indenting_and_outdenting_rows_moves_their_charge_codes_with_them` |
| **Plan-made charge codes had no CLIN, so invoices left their cost out.** A row under "CLIN 0001" made a code that "isn't tied to a CLIN"; the invoice generator drops cost on a code without a CLIN. New rows, and rows moved under another, now take the CLIN of the nearest summary above them when they have none (Finance's own mapping is kept). Codes made before this fix aren't backfilled: map them in Project › Setup › Charge codes. | `Application/Scheduling/PlanWbsSync.cs` | same test |
| Plan approval showed sign-in emails ("Submitted by sam.pm@example.com", the Finance to-do "sam.pm@example.com submitted it"). Now names. `ProjectPlanDto` gains `SubmittedByName`/`ReviewedByName`; the emails stay for the can't-approve-your-own check. | `ProjectPlanService.cs`, `PlanApprovalBar.razor` | `ProjectPlanTests.The_pm_submits_finance_returns_or_approves…` |
| After **Approve**, the header still said "No budget" and the cards "no budget yet: the plan is the budget" until a reload. The approval bar now tells the page, which reloads the health chip and the hours/cost cards. | `PlanApprovalBar.razor`, `PlanPeoplePanel.razor`, `ProjectHubPage.razor` | (UI wiring; checked in the browser: "700 h of 680 budgeted" became "700 h of 700 budgeted" on Approve, no reload) |
| No way from a contract to its project: **Start a project** on the contract page, and New › Project on a contract's page starts from that contract. The project name defaults to the contract's. | `ContractPage.razor`, `NewMenu.razor`, `NewProjectForm.razor` | `ContractToProjectTests.New_project_from_a_contract_starts_from_it_with_its_name` |
| Contract type and CLIN pricing pickers showed code names ("T&M · TimeAndMaterials", "CPFF · CostPlusFixedFee"). Now "T&M · Time and materials". | `SetupFormat.Name`, `ClinFormat.Name` | `ContractToProjectTests.Contract_types_and_clin_pricing_are_spelled_out_not_code_names` |
| A T&M contract's new CLIN defaulted to CPFF with an empty number. It now takes the contract's type and the next number (0001, 0002 …). | `ClinDialog.razor`, `ClinsTab.razor` | `ContractToProjectTests.A_new_clin_follows_the_contracts_type_and_takes_the_next_number` |
| The employee's submitted banner read **"Submitted, waiting on _waitingOn"** (the field's name passed as a string). | `Time/TimePage.razor` | `TimePageStatusTests` |
| After picking a code under "Add a charge code", the picker showed the code's id (a GUID). | `Time/TimesheetGrid.razor` | `TimesheetGridTests.Picking_a_code_adds_its_row_and_leaves_the_picker_empty` |
| Project › Setup › "Charge codes and who can charge" said "Add tasks on the Plan tab and make them charge codes there", a step that no longer exists. | `ProjectSetupTab.razor` | (copy) |

## Done since (October 25 to 26)

| Item | What changed |
| --- | --- |
| A1 | A new contract no longer asks for a funded value that was thrown away; fund each CLIN from its row (the Fund button). |
| A2 | The timesheet lists codes you're on that aren't open yet, with why ("1002 opens when Finance approves its plan", "starts Nov 2"). |
| A4 | A project started from a contract gets its revenue method from the contract's type (T&M earns at the bill rate as worked, cost-plus earns cost plus fee, fixed price on milestones). |
| B1, B2 | People follow their task's dates unless set by hand; hours that don't fit are flagged; one list of people per task. |
| B3 | Typing a task into a plan with a single CLIN row puts it under the CLIN. |
| B4 | Submit and Approve say what's still wrong with the plan first (overbooked people, hours that don't fit, failing schedule checks, past funding) and ask. |
| B7 | Post backlog asks before posting to the ledger. |
| B5 | The welcome tour opens on Home. Someone who arrives on another page first isn't interrupted, and gets it when they reach Home. |
| C1 (part) | The Approvals inbox shows who submitted by name (the email is the tooltip). |
| C9 | A plan waiting for Finance's review is in the Approvals inbox (and its badge) with everything else Finance decides: approve, or reject to send it back with the reason. It is no longer a separate to-do. |
| New | The Setup getting-started list matches the project lifecycle (fund the CLINs, start a project from the contract, first approved plan; no separate "charge assignments" step). An administrator of a company still being set up gets one to-do on Home with the next step, until every step is done. |
| C-close | On the period close checklist, "Items waiting to post" has a **Post these now** button. |
| New | Once the plan is approved, changes to people or hours are one to-do ("Send the changes to the plan"), not two staffing gaps against a budget that is the plan. |
| A5 | Already available: Setup lets a project's manager approve time on their projects. |

Open: A3, B6, B8, B9 and the rest of the C items below.

## Still to do, ranked

Ranked by how likely a new customer is to hit it and how badly it stops them. **A** blocks or silently does the wrong
thing, **B** costs real time or needs a workaround, **C** is polish. Each has steps to reproduce on a fresh demo database
(`/account/dev-signin`).

### A. Blocks the flow or gets the numbers wrong

**A1. The funded value on the contract form is thrown away; initial funding is a "modification".**
New › Contract asks for Ceiling and Funded. Add a CLIN and the contract's ceiling silently becomes the CLIN total (here
$1.2M → $1.0M) and its funded value becomes "Sum of CLIN funding; change it with a modification": $0, with the CLIN's
status chip saying **Funded** and Remaining $0. To fund the award you open Modifications › New modification › P00001 › Add
change › Funding › pick the CLIN (not defaulted, though there is only one) › amount › Execute › confirm. An award isn't a
modification, and nothing on the form says the $400K typed there won't count.
*Repro:* Pat › New › Contract, ceiling 1,200,000, funded 400,000, create › CLINs › Add CLIN value 1,000,000 › Details tab.
*Suggest:* when the first CLIN is added, offer "Fund CLIN 0001 with the $400,000 from the award?" (one click), and record
it as the basic award (not P00001). Chip "Not funded" when funded is 0.

**A2. The approved plan doesn't let anyone charge before the plan's own dates, and nothing says when they can.**
Project start comes from the contract's PoP start, moved to the next Monday when it's a weekend (Oct 24 → Oct 26); new
rows start on the project start and are 1 day long; people's dates default to their task's. So on award day nobody can
charge the new project, and the employee's timesheet simply doesn't list 4202, with no hint ("4202 opens Oct 26 for
you"). In the walkthrough Finance had to backdate the project start (no warning that it now precedes the PoP) and then
use Setup › Charge codes and who can charge to give Alex a charge assignment from Oct 19 (about 10 clicks); moving the
task's start in the plan grid didn't take and said nothing. There are two separate "assignment" lists (the plan's people,
and Setup's charge assignments) and the plan's people don't appear in the second ("No one is assigned").
*Repro:* create the project from a contract whose PoP starts today; assign Alex on the Plan tab; sign in as Alex › Time ›
Add a charge code: 4202 is missing.
*Suggest:* show "not open yet: from Nov 2" lines for assigned-but-future codes in the picker; warn when a project or
assignment starts outside the contract PoP; show plan people in Setup's charge-assignment list (read-only, "from the plan").

**A3. "Fill from my staffing" fills hours that the hour ceiling then refuses.**
Alex › Time › Fill from my staffing filled 34 h on 1001.01.02; Certify and submit failed with "would bring hours to 213 h,
past the ceiling of 196 h. The ceiling blocks it: ask Finance". The employee had to work out which cells to move.
*Repro:* demo data as is: Alex › Time › Fill from my staffing › Certify and submit.
*Suggest:* fill should stop at the ceiling and say so ("13.75 h left on 1001.01.02 before its ceiling").

**A4. A new T&M project recognizes revenue as billings.**
Accounting › Revenue: 4202 shows method "Revenue = billings", the fallback for a project without a revenue setup. A T&M
contract should earn hours × rate as worked (unbilled hours are revenue); here earned-but-unbilled labor is invisible until
invoiced. Also the page says "0 project(s) to post" while 1001 shows "10,448.05 more to post".
*Repro:* create a project from a T&M contract, approve time, open Accounting › Revenue.
*Suggest:* create the revenue setup when the project is started from a contract (T&M/LH → T&M, CPxx → cost plus fee,
FFP → milestones), and count "changed since" rows in the to-post total.

**A5. The PM never approves time charged to their project.**
Timesheets go to the supervisor ("Your supervisor approves them"). Sam, 4202's manager, sees "Nothing is waiting on you"
while hours land on his project. The task brief assumed the PM approves; the product's default doesn't.
*Repro:* Alex submits time on 4202 › Sam › Approvals.
*Suggest:* a default timesheet workflow step "Project manager (for hours on their projects)", or at least a PM to-do
"4 h charged to 4202 this week" with the flags.

### B. Costly or confusing

**B1. People grid: a row saves before the task is picked, and then the task can't be changed.**
Type a person and hours, Tab: the row saves on "Whole project (or a code not on the plan)" and the task is plain text
from then on. The only fix is the remove icon and re-adding.
*Repro:* Sam › 4202 › Plan › People › type Alex Rivera, Tab twice, 160, Tab.
*Suggest:* save once the row is left (or once task is set), and keep the task a picker for whole-project rows.

**B2. New tasks are 1 day long, and people inherit that.**
160 h on a 1-day task shows "Over their hours"; typed assignment dates are pulled back to the task's day without a word;
lengthening the task afterwards leaves the people on the old day (still overbooked).
*Repro:* add a row, assign 160 h, then set the task to 35 days: the person's dates don't follow.
*Suggest:* default a new row under a CLIN to the CLIN's period (or the project's), and move people's dates with their task.

**B3. Quick-add puts rows at the top level, beside the CLIN.**
"Type a task and press Enter" adds siblings of "CLIN 0001"; each must be indented. With one CLIN they belong under it.
*Repro:* new project from a contract › Plan › type three tasks.
*Suggest:* add under the selected row's summary, or under the only CLIN.

**B4. Submit and approve don't mention problems.**
Submit is enabled on an empty $0 plan and with "2 people overbooked · 3 fail" schedule checks; Finance's Approve doesn't
show them either. Approving makes the plan the baseline, so mistakes get locked in.
*Repro:* submit with an overbooked person.
*Suggest:* list the open warnings in the submit/approve confirmation.

**B5. Every new user gets the welcome tour on their first page,** in the middle of whatever they came to do (the PM came
from the "Plan it" to-do). *Suggest:* start it from Help, or only on Home.

**B6. Invoice prerequisites appear only at invoice time.** The invoice asks for the CAGE code, issue/admin/pay-office
DoDAACs and a ship-to tax jurisdiction; none were asked for when the contract was created. "Created by pm@example.com;
approved by pm@example.com" with no separation-of-duties prompt. A draft shows "Balance $0.00" under a $665 total.
*Suggest:* a "Billing details" step on the contract (with the checklist), names not emails, Balance = total on drafts.

**B7. "Post backlog" posts immediately.** On Accounting › Period close it sits in the tab row next to "Currency
revaluation" and looks like a tab; one click posted 11 items to the general ledger with no confirmation.
*Suggest:* a button with a confirm ("Post 11 approved items to the GL?").

**B8. Bill rates one at a time.** Three labor categories took 15 clicks; real contracts have 10–40. *Suggest:* a grid with
paste, and "copy rates from another contract".

**B9. Two date formats.** The contract form took `2026-10-24`; the expense report dialog rejected it ("Not a valid date
time") and wanted `10/24/2026`. The expense line's date isn't defaulted from a one-day trip.

### C. Polish and wording

- **C1. Emails instead of names** still in: Approvals › From column (`analyst@example.com`), invoice "Created by",
  revenue runs "by pm@example.com", ICE "generated by seed@workbench.local".
- **C2. Plan tab summary on an empty plan:** "Finishes Oct 26, 2026 · no room to slip" (PoP ends Oct 31, 2027) and
  "Schedule check 8 pass · 1 fail" before anything is planned. Planned cost card right after approval says "$10,487 left
  in the labor budget".
- **C3. New project dialog:** "Give the project a name." shows as soon as "A contract" is picked; the PM picker lists
  every user (HR, Materials) with no hint who manages projects.
- **C4. Breadcrumb** "Setup / Setup / Contracts / New contract" plus a stray "Setup" link above the heading.
- **C5. Reports hub doesn't list EAC or incurred cost.** EAC is under Accounting › Revenue › Estimates at completion (and a
  PM submits it from the project's revenue page, which isn't linked from the project); ICE is under Finance › Revenue &
  compliance. Search finds both. The approved plan doesn't seed an EAC. The ICE contractor block (name, UEI, address) is
  empty though Billing settings has them.
- **C6. Timesheet flags:** "Differs from allocations: 4202 has no allocation for this person" when Alex is on the approved
  plan from a later date. Say "not staffed this week (on the plan from Nov 2)".
- **C7. Expense report:** "Submitted 2026-10-24 13:12 UTC" shown to the employee; local time reads better.
- **C8. Home:** the funding to-do in "Today" shows "Due Sat Apr 24" (the projected run-out date, not a due date).
- **C9. Finance's plan review** is a to-do but not in the Approvals inbox with everything else.
- **C10. Setup › Charge codes:** the first click on a WBS element only highlights it; the panel opens on the second.
- **C11.** Every contract page load logs "Burdened cost unavailable; CLIN funding uses direct cost … Sign in first"
  (prerender runs the calculation without a user).

## Notes for the next walkthrough

- Run on a weekday, or set the contract's PoP to start a week back, or step 3 needs the workaround in A2.
- The browser automation's text typing doesn't fire key events, so typing into a selected grid cell (type-to-replace) and
  autocomplete filtering looked broken; they work with real keys. Native date inputs and MudSelects were set by key
  presses or picking an option.
- To run it again: `dotnet build src/Workbench.Web`, then start the app with `ASPNETCORE_ENVIRONMENT=Development`,
  `ASPNETCORE_URLS=http://localhost:5461` and `ConnectionStrings__Workbench` / `ConnectionStrings__Catalog` pointing at
  new SQLite files outside the repo (`dotnet run --no-build --project src/Workbench.Web --no-launch-profile`); it migrates
  and seeds the demo data on first start.
