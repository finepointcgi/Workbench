# Unanet import samples (invented data)

A made-up company ("Harborview Federal Solutions") exported the way a Unanet site typically exports lists and reports.
Nothing here is real customer data. The files belong together: import them in the order of
`docs/integrations/unanet.md` (organizations, labor categories, people, cost rates, customers, projects as contracts,
CLINs, projects and tasks, bill rates, budgets, assignments, vendors, open AP, open AR, timesheets).

- `projects.csv` feeds two steps (contracts, then projects and WBS). Project rows have no task; task rows name the
  project and the task. Overhead, bid and proposal, and leave projects are internal and get no contract.
- `timesheets.csv` is September 2026 approved time (one row per person, task and day) plus two *Submitted* rows that
  are deliberately left out. Week-ending layouts with a column per weekday are also read.
- `samples/reconcile/unanet` has Unanet month reports for the parallel-run reconcile.
