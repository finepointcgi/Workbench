# Automation (Admin › Automation)

Build "when this happens, if these are true, do that" without code, with one builder and one engine for everything: records
across every module (contracts, projects, people, timesheets, expenses, invoices, bills, purchasing, business development) and the
rows of each sheet. Administrators manage record automations; a sheet's editors automate their own sheet.

Open it from **Admin › Automation** (`/admin/automation`). Tabs: **Automations** (every automation, sheet ones included with a link
to their sheet; filter by "Across modules" or "On sheets"), **Run log**, **Recipes**, **Approval workflows** and **Settings** (pause
everything, loop protection limits). On a sheet, the **Automate** button opens the sheet recipes and the sheet's automations; the
**Automation** tab lists them with their recent runs.

## Sheet automations

The same builder at `/sheets/{id}/automations/…`, with the sheet's columns:

- **When**: an item is added; an item is added by a form; a column changes (optionally from and/or to a value); a date arrives
  (Start, Finish or a Date column, on the day or N days before or after; checked hourly, once per row and date); on a recurring
  schedule (daily, weekly or monthly from an hour, for each row matching the conditions).
- **If**: conditions on the row's columns (all or any, nested groups), compared ignoring case and spaces.
- **Then**: set a column, assign someone (Person column, they're notified), notify (in the app, email by preference), send an email,
  add a sub-item, create an item (here or in another sheet), move or copy the row with its sub-items to another sheet (values go to
  columns with the same name and kind), request approval through a record approval workflow (the decision, "Approved" or
  "Rejected", can be written to a column, so a "when Approval changes to Approved" automation can follow), give a to-do.
  Text can use `{Title}`, `{Start}`, `{Finish}`, `{Today}` and `{Column name}`.
- It acts as **whoever saved it last**: changes show as `Workbench Automation (rule "Name", author email)`; actions on another sheet
  need the author to edit it (checked when saved and when it runs); date and schedule triggers stop while the author can't edit
  the sheet or is deactivated. Loop protection: chains stop after 3 steps (or the tenant's lower limit), each automation runs at
  most the tenant's hourly limit per row, and "pause every automation" stops sheet automations too.
- **Excel imports** (the plan's Import from Excel) run the sheet's automations on every row the file added or changed, as one batch:
  notifications and emails are collected and sent once per automation and person ("Notes changed: 12 rows from the Excel import").
- Sheet rules made before the unified builder were moved into sheet automations by a data migration (same ids, history kept in the
  run log, date triggers that already fired don't fire again). The `/sheets/{id}/rules` API still works on them.

## Recipes

Plain sentences with blanks to fill in, like "When **Status** changes to **Done**, notify **Owner**". Choosing one opens the builder
with the sentence on top; filling in a blank sets up the steps below, which you can still change before saving.

- Sheets: status changes → notify; date arrives → set status (move to In progress); added by a form → assign; added → notify;
  status → move or copy to another sheet; N days before a date → remind unless done; weekly nudge on open rows; blocked → add a
  sub-item; status → email; status → request approval.
- Records: funding drops below n% → notify the PM; timesheet submitted more than n days late → remind the supervisor; timesheet n
  days overdue → remind; timesheet approvals waiting n days → escalate; expense lines over $n without a receipt → refuse; expense
  reports over $n → tell Finance; bill approved → notify someone; project over n% of budget; n days before a contract ends →
  closeout to-do; requisitions over $n → extra approval; approved invoices → a channel; opportunity stage changes → capture managers.

## Building one

The builder has three steps.

### 1. When (the trigger)

| Trigger | Fires when | Notes |
| --- | --- | --- |
| Something happens to a record | A record is created, updated, has its status changed, is submitted, approved, rejected or deleted. | Pick the record type and one or more events. |
| A field changes | A field of the record changes, optionally *from* and/or *to* a value. | Creation doesn't count as a change. |
| A Workbench event | One of the webhook events (`timesheet.submitted`, `invoice.approved`, `shipment.shipped`, `rfq.awarded`, ...). | Same catalog as Admin › Integrations. |
| On a schedule | Daily, weekly (a weekday) or monthly (a day), at an hour of the organization's day. | With a record type, it runs once for each record. |
| A date is coming up or has passed | A record's date field is N days away (before) or N days past (after). | Built-in date fields. |
| A threshold is crossed | Contract funding remaining below X%; project cost over Y% of the current budget; a timesheet not submitted N days after its period ends; a document waiting N business days for approval. | Fires once when a record crosses, and again only after it has come back. |
| Run by hand or by another automation | Someone presses **Run now**, or another automation's "Start another automation" action. | |

### 2. If (conditions)

Conditions compare a field of the record (built-in fields, computed figures such as *Funding remaining (%)* or *Largest line
without a receipt*, and admin-defined custom fields) with a value: is, is not, greater/less than, contains, starts with, is
empty, is one of. They can also check **the person's role** (whoever made the change), and **the record's company, org unit or
project**. Group them with **all (AND)** or **any (OR)**, and nest groups. No conditions means it acts every time.

Numbers and dates compare as numbers and dates; text ignores case. Fields the automation's user can't see (for example contract
values for someone outside Finance) count as "no access", and the condition doesn't match.

### 3. Then (actions)

| Action | What it does |
| --- | --- |
| Notify people | An in-app notification, emailed as each person prefers. Send to an email, everyone in a role, a person named in a field, or a related person (the submitter, the employee, their supervisor, the project manager, the current approvers or their supervisors). People aren't told about their own changes. |
| Set a field | Changes a custom field or one of the few descriptive built-in fields modules don't depend on (such as an expense report's purpose or an invoice's notes). |
| Create a to-do | Adds a to-do to each recipient's home page, optionally due in N days. |
| Require an approval | Routes the record through a **record approval workflow** (create one under Approval workflows). Approvers decide in their Approvals inbox; the decision fires the record's "approved" or "rejected" automations. |
| Post to Teams or Slack | Posts a message through a Teams or Slack connector (Admin › Integrations). |
| Call a webhook | Sends a signed POST (event `automation.webhook`) with the record's fields to a webhook endpoint, retried like any delivery. |
| Start another automation | Runs a "by hand" automation on the same record, as the same user. |
| Refuse the change | A **validation rule**: the change is refused before it's saved, with your message (for example "Expense over $75 needs a receipt"). Only for record events and field changes, and on its own. |

Titles, messages and values can use `{fieldKey}` tokens (for example `{contractNumber}`, `{fundingRemainingPercent}`), plus
`{record}`, `{link}`, `{actor}`, `{automation}` and `{today}`.

## Safety

- **Acts as someone, never more.** An automation acts as the person whose change fired it (schedules and thresholds: whoever last
  published it), or as a designated service user. Every action is checked against that user's own roles and access: it can only
  set fields they could change, and only share records they could see. A refused action is logged as "Denied". You can't pick a
  service user with roles you don't have.
- **Loop protection.** Changes an automation makes can fire other automations; the chain stops at the chain depth limit (5), when
  it comes back to the same automation and record, or when one automation has run on one record too often in an hour (20). Both
  limits are on the Settings tab.
- **Dry run.** "Test it" runs the definition on the page against a record you pick and shows each condition's result and what each
  action would do. Nothing changes; dry runs of saved automations are in the run log.
- **Run log.** Every run: what fired it, the record, who it acted as, the inputs, each condition's result, each action's outcome and
  any error. Kept a year (runs whose conditions didn't match: 30 days).
- **Switches.** Each automation has an on/off switch; **Pause every automation** on the Settings tab stops them all at once
  (validation rules included).
- **Versions.** Every save publishes a new version with an optional note; runs record the version they used, and you can restore an
  earlier version (as a new one). Deleting hides the automation and keeps its history. Every change is in the audit log.

## Approval workflows

Approval workflows (Setup › Approval workflows, also a tab here) are editable for every document type: steps in order (the same
order runs in parallel), conditions (amount, policy threshold, company, project, contract, org unit, cost type, unallowable or
account charges), reminders after N business days with escalation to the approver's supervisor and/or **to an org role or named
people, who can then decide**, fallbacks, self-approval rules and delegation (Approvals › Delegations). Built-in defaults are
unchanged. **Record approval workflows** (for the "Require an approval" action) are added at the bottom of the list.

The development data sets up three record recipes: funding under 25%, receipts over $75 and timesheet approval escalation.

## Permissions

Managing automations ("manage automations") and running one by hand ("run automation") are for administrators until the
permission catalog lands; then they're the `automations.manage` and `automations.run` (or `automations.run:{id}`) permissions.
The API (`/api/v1/automations`) is closed to API keys.
