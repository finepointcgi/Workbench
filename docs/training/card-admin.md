# Workbench quick reference: Administrator

**Help:** **?** on any page · **Search:** `Ctrl+K` · Guide: Help center › *Getting started: set up a new organization*

## Setup order (Admin › Setup › Getting started)

Sign-in (SSO) and two admins → Organization and fiscal calendar → **Import** or create: org units → labor categories →
people → customers → contracts → projects and WBS → chart of accounts, cost and bill rates, indirect rates, bank
accounts, payroll provider, billing details → users and roles → approval workflows → pilot.

## Users: Admin › Users and security

| Task | Where |
| --- | --- |
| Invite someone | **Invites** › email, name, roles › **Invite** (expires in 7 days; not needed with SCIM) |
| Change roles | **Users** › the person |
| Link to an employee record | **Users** (when emails differ) |
| Someone leaves | **Deactivate** (never delete; sessions end at once) |
| Quarterly access review | **Access reviews** › **Start review** › Keep / Revoke › **Complete review** |

**Roles:** Employee (everyone) · Timekeeper (sees all timesheets) · Resource manager (staffing) · Finance (books and
setup) · Admin (everything). Supervisors and project managers get their extra access from People and Projects.

## Single sign-on

IT registers Workbench in the identity provider (OpenID Connect; redirect `https://your-address/signin-oidc`) and gives
the server the authority, client id and client secret. Optional SCIM provisioning at `https://your-address/scim/v2`.
Sessions: 30 minutes idle, 12 hours maximum.

## Approval workflows: Admin › Approval workflows

Pick a document type › edit **Steps** (approver, **Condition**, self-approval, reminders, escalation) › **Publish new
version**. In-flight items keep their version. **Reset to built-in default** to undo.

## Import: Admin › Import

Upload › map › **Save mapping and validate** › dry run › import. **Roll back** from the history (until something is
built on top).

## Support requests: Admin › Support requests

You're notified of each report. Open it › set status › reply › **Save**; the reporter is notified. Reports are also
emailed to the support address configured on the server (`Support:Email`).
