# A project's life: demo script and test

Two things to show a customer, and one test that keeps them honest.

## The staged projects (demo data)

Four projects, one at each step of a project's life, so you can open any stage without walking there. Sign in as **Pat
Morgan** (`pm@example.com`, Finance) unless it says otherwise. Open **Projects**, then the project's **Plan** tab: the strip
under the approval bar shows the steps and marks the next one.

| Project | Stage | What to show | Who has the next step |
| --- | --- | --- | --- |
| **5101** Lifecycle 1: set up | Finance has just created it | No funding, no labor categories, no manager: the strip says **Funding** is next. Use **Change the ceiling or funding** and **Labor categories**, then assign Sam as manager. | Finance |
| **5102** Lifecycle 2: planning | Funded, two categories, Sam is manager | Sign in as **Sam Patel** (`sam.pm@example.com`): the home to-do says *Plan 5102*. Add people on the plan: the category list limits who fits. Submit is open (setup is done). | Sam |
| **5103** Lifecycle 3: awaiting approval | Sam's plan is with Finance | Pat has the *Review the plan* to-do. Open it, see the people and hours, **Approve**: the project turns Active and the baseline is set. | Finance |
| **5104** Lifecycle 4: executing | Plan approved | Baseline (40 h) and working budget, project Active, every step ticked. Make a change to the people: the plan shows *Changed since approval* against the baseline. | Sam, then Finance |

The existing project **1001** is the long-running one: months of approved time, burden allocations, invoices, a revenue run,
a bank statement with a fee to book. Use it for the numbers (costs, billing, reconciliation); use 5101 to 5104 for the process.

## How the pieces reconcile (talking points)

- **Time to ledger:** approving a timesheet posts its labor to the ledger on its own. Reopening reverses it.
- **Ledger to project financials:** Accounting › Reconciliation compares, per WBS element, allowable cost in the ledger with the
  project financials' actuals. It should always read *Reconciled*.
- **Burden:** the allocation run applies fringe, overhead and G&A to the period's labor. The close checklist wants it posted.
- **Billing:** a T&M invoice is approved hours times the contract's bill rate. Approving it posts AR and revenue; a payment closes it.
- **Bank:** import the statement, auto-match (payment references, check numbers, amount and date), book the fees, complete.
  Anything in the ledger that isn't on the statement yet (today's receipt) is a *deposit in transit*, not an error.
- **Close:** the checklist's bank check wants a reconciliation **through the period's end**, so it clears at month end.

## The golden-path test

`tests/Workbench.Tests/GoldenPath/GoldenPathTests.cs` walks one contract through all of it on the real web host and the demo
seed, each step as the person who does it, over the HTTP API:

contract, rate, CLIN, funding, project (Planning, with the contract's categories), WBS and people, submit, approval (Active),
time, approval, ledger, burden run, invoice, payment, bank reconciliation, close checklist.

After each hand-off it checks the numbers against each other: the ledger's labor equals the project financials' actual
labor; the invoice equals hours times the bill rate; the ledger-to-financials reconciliation still agrees after billing and
payment; the bank reconciliation completes with the receipt in transit and the bank fee booked; nothing of the chain is left
unposted. `LifecycleDemoTests` checks the four staged projects above.

Run just these two:

```bash
dotnet test tests/Workbench.Tests -c Release --filter "FullyQualifiedName~GoldenPath|FullyQualifiedName~LifecycleDemo"
```

### What writing it found

- Staffing a task that had already ended said "The end date is before the start date." It now says when the task ended and what to do (fixed).
- A project still being planned wasn't in anyone's timesheet picker, with nothing to say where it was. The timesheet now lists
  "not open yet" codes with why (the plan isn't approved, or the project starts later) (fixed).
- The sample data's dates come from the server's UTC clock and the app's "today" from the local one, so on a Sunday evening in
  the US they are a day apart (the seed's "this week" is next week's Monday). The golden-path test reads the timesheet period
  from the app instead of assuming a date.
