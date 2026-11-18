# Paylocity

!!! info "Optional module: Workforce"
    This page describes the **Workforce** module, an optional add-on. It appears only when your license includes it. See [Optional modules](../reference/optional-modules.md).

<p class="roles">Finance · HR · Administrators</p>

Workbench works with Paylocity through **files**, so it needs no access to Paylocity's API. Workbench sends Paylocity
the *hours* from approved timesheets, never pay amounts or taxes: Paylocity pays at the rates it holds and calculates
the taxes. Workbench then reads Paylocity's payroll register back to reconcile and post the payroll journal (see
[Payroll](../finance/payroll.md)).

!!! warning "Not verified against Paylocity's real import"
    Paylocity doesn't publish the column layout of its hours import. Workbench's default layout follows the public
    pieces (the Pay Entry fields and Paylocity's data converter requirements) but the exact headers and order are an
    assumption. Before the first live payroll, compare it with your own Paylocity import template: **Payroll › Providers**
    has a layout editor that takes a sample of your template and shows which columns differ. Test with a one-employee
    batch in Paylocity first.

## What each pay period sends

One line per employee and earning code, only for employees with a Paylocity id:

| Workbench | Paylocity column | Notes |
| --- | --- | --- |
| Pay group's company code | Company Code | Set on **Payroll › Pay groups**. |
| Payroll profile's Paylocity id | Employee Id | Employees without one are left out and flagged. |
| Regular hours | Det `E`, earning code `REG` | Salaried employees' regular hours are not sent (Paylocity pays the salary) unless you turn that on. |
| Overtime hours | earning code `OT` | Non-exempt employees, over 40 worked hours in a Monday–Sunday week. Leave doesn't count toward the 40. |
| Leave: PTO, holiday, sick … | earning codes such as `PTO`, `HOL` | One per leave charge code. |
| Org unit | Cost Center 1 | The org unit's code, or the Paylocity cost center you map it to. |

Hours come only from **approved** timesheets. Someone with an unapproved or missing timesheet, no Paylocity id, or an
unmapped leave code is flagged, and the export is refused until you tick *Export anyway* (their unapproved hours are
then simply left out).

Not sent: bonuses, per diem and other dollar amounts. Workbench exports hours; enter dollar earnings in Paylocity.
Project and job allocations are not sent either; Paylocity receives one cost center per employee.

## Set up the mapping

Open **Payroll › Paylocity** (Finance and administrators).

1. **Companies**: the Paylocity company code each pay group files under.
2. **Earning codes**: the Paylocity earning code for regular, overtime and each leave type (up to five characters).
3. **Cost centers**: the Paylocity cost center for each org unit. Leave blank to send the org unit's own code.
4. **Employee ids**: who is ready, who has no id, and duplicates. Set ids on **Payroll › Employee profiles**, or import
   them from the roster (below).

## Export a pay period

On **Payroll**, open the run for the pay group and period, check the hours, and download the Paylocity hours file. Import it in
Paylocity at *Payroll › Pay Entry*. Every export is kept under **Payroll › Paylocity › Export history** with:

- the file name, number of employees and lines, and total hours;
- a **checksum** (SHA-256 of the file): compare it with the file you uploaded;
- who exported it and when, and whether it was sent by file or API.

**A pay period is never exported twice by accident.** Importing two hours files in Paylocity would pay the hours twice.
Once a period has been exported (even if its run was voided), exporting again needs a note saying why, for example *"Paylocity
rejected the first file"*. The note is kept with the new export, marked *again*, and appears in the audit log.

## Import people from Paylocity

**People › Import from Paylocity** (HR and administrators) adds and updates employees from a Paylocity employee list.

1. In Paylocity, export your employee list as CSV. Needed: **Employee Id**, a name (First Name and Last Name, or Employee Name),
   and for new people an email and hire date. Optional: termination date, status, department, job title, FLSA status,
   full/part time. Other columns, such as pay rates and Social Security numbers, are ignored and never saved. (Remove them
   from the report anyway.)
2. Choose the **fields Workbench owns**. An import never changes these for someone already in Workbench.
3. Choose the file and, to link Paylocity ids, the pay group. Select **Preview**: it lists what would be created, updated,
   left alone or rejected, and why. Nothing changes yet.
4. Select **Import**.

How people are matched, in order: the **Paylocity id** on their payroll profile, their **email**, their **employee number**.
If the id and the email point at different people, or a person is already linked to a different id, the row is an error: nothing
is guessed. Rows with errors are skipped; the rest import. Importing the same file again changes nothing.

**Terminations and rehires.** The preview points out people terminated in Paylocity who are still active in Workbench (and
any time entered after the termination date), and people active in Paylocity whose Workbench record is terminated (a
rehire). These change nothing unless you tick **Apply terminations** or **Apply rehires**. People in Workbench with a Paylocity id
who are missing from the file are listed and left alone. People already terminated in Paylocity are not created.

New people get a personnel record and, with a pay group, a payroll profile with **no pay rate**: enter pay under compensation.
Choosing a pay group needs payroll setup rights; HR users without them can still import but ids aren't linked.

## Permissions and audit

| What | Who |
| --- | --- |
| Mapping (view, change) | Payroll setup (Finance, administrators) |
| Export history and downloads, running exports | Run payroll (Finance, administrators) |
| Roster import and its settings | Manage personnel records (HR, administrators) |

Changes to cost centers and owned fields, every export, and every employee created or updated are in the
[audit log](audit-and-support.md); exports and roster imports are also security events. The API is under
`/api/v1/payroll/paylocity` and belongs to the *People and payroll* area for API keys.

## Sources

Paylocity's public documentation used for the layout: Data Converter setup form
(`docs.paylocity.com/forms/New_Client_Forms/Converter_Setup_Form.pdf`), Pay Entry API
(`developer.paylocity.com/integrations/reference/payentryv2_postpayentryimport`) and the Web Link import guide
(`docs.paylocity.com/weblink/PDFs/Web_Link_Manual_Imports.pdf`). None of them defines the hours CSV or an employee export
file, which is why both are configurable and previewed.
