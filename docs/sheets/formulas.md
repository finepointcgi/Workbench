# Sheet formulas, linked rows, forms, templates and reports

## Formulas

A column can calculate its value (column dialog › "Calculate with a formula"). Values are worked out on every read and
never stored. "Check" works the formula out on the first row and explains problems in plain words.

| Write | Means |
|---|---|
| `[Hours]` or `[Hours]@row` | this row's value |
| `CHILDREN([Hours])` | the values in this row's child rows |
| `[Status]:[Status]` | the whole column, every row in outline order |
| `[Code]:[Rate]` | several adjacent columns (Title, Start, Finish come first), for VLOOKUP and INDEX |
| `{Open risks}` | a named cross-sheet reference (column dialog › "Another sheet" › "Reference another sheet…") |
| `@cell` | inside a criterion, the value being tested: `COUNTIFS([Due]:[Due], @cell < TODAY())` |

Renaming a column rewrites every formula on the sheet that names it. Cross-sheet references and lookups keep columns by
id, so renaming them in the other sheet changes nothing. Renaming onto another column's name is refused.

Errors: `#REF!` (no such column or reference), `#DIV/0!`, `#VALUE!` (wrong kind of value), `#NAME?` (unreadable formula),
`#CIRCULAR!`, `#NO MATCH` (MATCH/VLOOKUP found nothing), `#NO ACCESS` (reads a sheet or column the viewer can't see).
The grid shows the explanation as a tooltip (`FormulaErrors.Explain`, `FormulaErrorText.Explain`).

Functions (the formula help lists each with its syntax): SUM, AVG, AVERAGE, MEDIAN, MIN, MAX, LARGE, SMALL, PRODUCT, ROUND,
ROUNDUP, ROUNDDOWN, INT, ABS, SQRT, MOD, CEILING, FLOOR · COUNT, COUNTBLANK, COUNTIF, COUNTIFS, SUMIF, SUMIFS, AVERAGEIF,
AVERAGEIFS, COLLECT · IF, AND, OR, NOT, IFERROR, ISBLANK, ISERROR, ISNUMBER, ISTEXT, ISDATE, ISBOOLEAN, CONTAINS, HAS ·
INDEX, MATCH, VLOOKUP, JOIN, DISTINCT · TODAY, DATE, YEAR, MONTH, DAY, WEEKDAY, WEEKNUMBER, YEARDAY, DATEDIFF, NETDAYS,
NETWORKDAYS, NETWORKDAY, WORKDAY · LEN, LEFT, RIGHT, MID, FIND, UPPER, LOWER, TRIM, SUBSTITUTE, REPLACE, CONCATENATE, VALUE ·
CHILDREN. Notes: MATCH and VLOOKUP default to the closest match (as in Smartsheet and Excel); pass 0 / FALSE for exact.
JOIN separates with ", " unless told otherwise. A blank cell never passes an `@cell <`/`>` comparison.

## Linked rows and lookups

- **Link to row** (`ColumnKind.LinkedRow`): `Options[0]` is the other sheet's id; the cell holds item ids, comma-separated.
  Pick rows in the cell's dialog (Enter or F2). You can only link to a sheet you can see.
- **Lookup** (`ColumnKind.Lookup`): `Options` = [link column id, field key (`title`, `start`, `finish` or a column id)].
  Read-only, computed on read, so it follows the other sheet live (the sheet page also listens to linked sheets).
- Viewers who can't see the other sheet get no links and blank lookups; restricted columns there stay hidden too.
  Their full-set updates keep the stored links.

## Forms

Sheet › More › Forms. A form asks for Title plus chosen columns (labels, help, required) and adds a row per response
through the normal item pipeline (activity, live updates, "row added" sheet rules). Audience: workspace members, anyone
signed in (`/forms/{id}`), or anyone with the public link (`/f/{token}`, server-rendered, no scripts). Public needs the
workspace's Owner role and the `sheets.forms.public` permission. Formula, lookup, link and restricted columns can't be
asked; Person columns aren't asked publicly. Public responses: 5 per client (address hash) per 10 minutes, 200 per form
per hour, a hidden honeypot field; signed in: 30 per person per 10 minutes. Each response notifies the form's creator
and raises the `sheet.form_submitted` integration event (ids only).

## Templates and copies

New sheet offers a blank sheet, five built-in templates (project schedule, issue tracker, RAID log, contract deliverables,
onboarding checklist) and the workspace templates you can see. Sheet › More › "Save as template" (Editor) saves columns,
references and optionally rows (dates rebase to the day a sheet is made). "Copy sheet" (Viewer) copies columns, formulas,
references and optionally rows with values and dependencies, into the same workspace when you can edit it. Columns and
values hidden from you are never copied.

## Reports across sheets

`/sheet-reports`: pick sheets, columns (by name), filters (`today`, `today+7` work for dates) and a sort; share with a
workspace or keep private. Every run is live and as the viewer: sheets they can't see are left out and counted, hidden
columns are blank, links show the linked rows' titles, and each row links back to its item.
