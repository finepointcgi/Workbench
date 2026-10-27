# Walking the live demo as each role (Oct 27 2026)

The deployed demo (Cloud Run), signed in as Alex (employee), Sam (project manager), Pat (Finance) and Dana (administrator), on
screens at about 800 px wide. Looking, not clicking through every flow: what read well, what didn't, what was wrong.

## Fixed in this round
- **¤ instead of $ on money.** "Lodging above the per diem (max ¤258.00/night)", "revenue ¤960,000, expenses ¤663,120" next to
  "$1,623,120.00". The server had no locale (a container) and formatted with the invariant culture; the test suite set en-US
  globally, which hid it. The server now runs in en-US (`WorkbenchCulture`), with a test that formats as the invariant culture first.
- **The welcome tour followed people off Home.** Opened on Home, then clicking a to-do carried the card onto the plan page. It now
  puts itself away when you leave Home (not marked finished) and comes back next time you're on Home.
- **Two ways to pick a contract on New project.** The "A contract" start and the "Contract (optional)" box did different things;
  a contract picked in the box with "Nothing (a blank plan)" made a project with no CLIN rows. Picking a contract in either now
  starts from it.

## Read well
- Home is short and says what to do next in plain words, with one button each (a timesheet due Nov 1, receipts to add, a plan to build).
- The project steps (Funding, Labor categories, Project manager, Plan, Sign-off, Executing) say where a project is.
- The expense report page: Submit up top, receipts marked Required on the line that needs one.
- A plan waiting for Finance is in the Approvals inbox, named, with Approve, Return and Reject.
- Leave: one screen, says approved days fill the timesheet.

## Still confusing (not fixed yet), roughly in order of how much it matters
1. **Approvals show up three times on an approver's Home**: the "Review 3 approvals" to-do, the "Waiting on you" card listing the same
   three, and a separate "Gate decision" to-do for one of them.
2. **Adding people to a plan** is at the very bottom of a long page, below the schedule grid, in a seven-column row
   (Person, Task, Labor category, Hours, From, To) whose Task defaults to "Whole project". The Assignees column in the grid above
   is a second way in.
3. **My time** opens with a paragraph about exempt staff, uncompensated hours and "Hybrid" before the grid; the grid starts below
   the fold.
4. **Employees see an "Allowable" column** on their expense reports (a contract-cost term).
5. **Old wording on Sam's Home**: "Assign people to 1002 — Nobody is assigned yet… until there's a budget, they're the plan" (a
   project with no plan row, from the older seed) beside "Plan 5102" for the new flow.
6. Small: "Verify 1 life event(s)"; a plan page header with a dozen controls (Import/export, Automate, Share, Try changes, By labor category…).
