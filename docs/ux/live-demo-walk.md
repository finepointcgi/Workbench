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

## Fixed in round 12 (was "still confusing")
1. Approvals on Home: the "Review N approvals" to-do is left out while the "Waiting on you" card is on the page, and a gate decision that is
   in the inbox no longer has a to-do of its own.
2. Adding people to a plan: an "Add a person" button in the plan's toolbar jumps to the People grid with the cursor in its empty row.
3. My time: the exempt-staff policy note is folded behind "What does this mean?"; the three numbers stay.
4. Expense reports: "Allowable" is off by default in the list, and the report footer only mentions it when it differs from the total.
5. The old "Assign people" to-do no longer says "until there's a budget, they're the plan".
6. "Verify 1 life event"; on a project's Plan tab, Share, Automate and the import/export items are one "More" menu.

## Still confusing (not fixed yet)
- Someone other than me has not done contract-to-invoice cold; that is the next real test.
- The people grid's Task column still defaults to "Whole project".

## Finance month-end walk (round 13)
Fixed:
- **A wrong or old link showed a blank white page** on the live demo (and locally): the not-found screen existed but never rendered.
  Page loads now go to "We can't find that page"; API calls still get a bare 404.
- **Period close**: "Unposted items = 0" read like a failing number; it now reads "Everything approved is posted". The 14 unposted
  items were listed twice (checklist and table); they're in the table only. A "not approved" line now says how many are waiting in
  Approvals and how many were never submitted (the owner has to submit them), since "Open approvals" can't help with those.
- The new-person row on the plan says "Whole project (or pick a task)".
Still open:
- Finance Home has four KPI tiles plus a long to-do (staffing gaps on a project Finance doesn't run); "Utilization 26%, target 100%" has no
  context.
- Finance's menu lists about ten pages under Finance before any is explained.
