---
title: Sheets, boards and Gantt basics
summary: Work with plans and trackers as a grid, a card board, a calendar or a Gantt chart.
category: Sheets and dashboards
audience: everyone
keywords: sheet grid board kanban calendar gantt plan columns rows comments attachments share ms project microsoft project indent outdent summary duration predecessors excel import export
order: 30
---
A sheet is a shared table, like a spreadsheet built for teamwork. Every sheet can be shown four ways using the view buttons at the top.

## Grid

Rows are items (tasks, risks, requests), columns are their details. Click a cell to edit it, or press Enter. Columns have types (text, number, date, dropdown, person, checkbox) so entries stay tidy. Formula columns calculate for you.

Open a row's details to add **comments** (type @ to mention someone) and **attachments**, and to see its activity history.

## Plans that work like Microsoft Project

On a project's plan (any sheet with scheduling on) the grid works the way MS Project does:

- **Indent and outdent.** In the Title column press **Tab** to indent a row (it becomes a subtask of the row above) and **Shift+Tab** to outdent it, or use Alt+Shift+→/← or the row menu. An indented task moves to start with the task above it at its new level (with its new summary task when it's the first subtask) and keeps its duration. Outdenting keeps the row where it is: the tasks below it at the same level become its subtasks.
- **Summary tasks.** A task with subtasks is a summary: its start, finish, duration and % complete come from its subtasks (earliest start, latest finish, % weighted by duration), so you don't edit them directly.
- **Duration** is in working days, Monday to Friday less company holidays. Type it and the finish follows from the start. **0** makes a milestone.
- **Predecessors** use the task IDs in the left column, as in MS Project: `3`, `3, 5`, or `5SS+2d` (start-to-start with 2 days' lag; FF and SF work too). A task can't start before its predecessors allow, so moving one task pushes the tasks that wait on it.
- **Outline numbers** (1, 1.1, 1.2) show before each title; collapse or expand summaries with the arrow.
- **Insert** adds a row above the selected one; the row menu also adds rows below and subtasks.

### Excel

Use **Excel** at the top of the plan:

- **Export plan to Excel** downloads the plan with the task names indented and grouped by outline level: ID, outline number and level, WBS code, name, duration, start, finish, predecessors, % complete, resource names and notes.
- **Import plan from Excel** reads a file in the same layout. You'll see what it adds, changes and removes, and any rows with problems, before anything is saved. The file is the whole plan, so rows you delete in Excel are removed. Importing an export you didn't change changes nothing. Only people who can edit the plan can import.
- **Download blank template** gets an empty workbook with the columns and instructions.

## Board

Each card is a row, grouped by a dropdown column such as Status. Drag a card to another column to change it.

## Calendar

Rows with a date appear on the calendar. Drag them to reschedule.

## Gantt

Tasks with start and end dates appear as bars. Drag a bar to move it, or its edge to change its length. Link tasks to show that one must finish before another starts; the critical path is highlighted. A baseline lets you compare today's plan with the approved one.

## Who can do what

Access comes from the sheet's workspace: **Viewers** read, **Commenters** also comment, **Editors** change rows and columns, and **Owners** manage the workspace. Some columns are marked "Finance and owners only" and are hidden from everyone else.

> [!TIP]
> A project's plan sheet and its tasks (the WBS) are the same tree: a new task on the plan can become a charge code once Finance makes it chargeable.
