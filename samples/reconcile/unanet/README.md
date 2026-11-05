# Unanet parallel-run samples (invented data)

Reports for September 2026 that match `samples/import/unanet` with a few differences planted on purpose, so the
reconcile page has something to explain:

| File | Planted difference | What Workbench should say |
|---|---|---|
| `timesheet-totals.csv` | H1003 on HV-2201 has 8 more hours in Unanet | Unanet has 8 more hours (about a day) |
| | H1004 on HV-2201 costed at $43.00 instead of $41.75 | Hours match, cost rate is different |
| | H1007 (Quinn Alvarez) isn't a person in Workbench | Person isn't in Workbench, import people |
| | H1005 on HV-9999: project isn't in Workbench | Project isn't in Workbench |
| | H1006 / HV-LV (Labor Day) left out | Workbench has hours Unanet doesn't list |
| `project-cost.csv` | Totals of the above, by project | HV-2201 differs; HV-9999 isn't in Workbench |
