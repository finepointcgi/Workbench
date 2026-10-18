---
title: Import data
summary: Move your setup data from Costpoint, Unanet or Smartsheet, check it first, and roll back if needed.
category: Administration
audience: admin
keywords: import costpoint unanet smartsheet csv excel migrate rollback dry run validate mapping
order: 54
---
**Admin › Import** brings in your existing data so you don't have to type it.

## What you can import

From Costpoint (CSV or Excel exports): org units, employees with labor categories and cost rates, customers, contracts with CLINs and funding, projects with their WBS, bill rates and budgets. From Smartsheet (an API token or an `.xlsx` export): sheets with their hierarchy and predecessors, optionally linked to a project's WBS.

## Steps

1. Pick the source and what you're importing, and upload the file (or **Connect** to Smartsheet).
2. **Map** your columns to Workbench's fields. Workbench guesses most of them. Select **Save mapping and validate**.
3. Fix any problems it lists, then **Validate again**.
4. Run a **dry run** to see exactly what would be created or changed, without saving anything.
5. Import it.

Import in this order so each file can find what it refers to: org units, labor categories, employees, customers, contracts, projects, rates, budgets.

## Rolling back

Every import is kept in the history. **Roll back** undoes it, as long as nothing has been added on top since (for example time charged to an imported project).

> [!TIP]
> Sample files with the expected columns are in `samples/import` in the Workbench repository.
