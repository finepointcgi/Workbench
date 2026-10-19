# Administrator training: one hour

**Audience:** whoever sets up the organization, users and access. **Goal:** leave able to follow the getting-started
order, invite users and assign roles, understand single sign-on, adjust an approval workflow, run an import, and handle
support requests.

**Before the session:** a fresh demo database. Trainees sign in as `dev@workbench.local` (Dana Lee, Administrator).
Hand out the [administrator quick-reference card](card-admin.md). Read the in-app article
*Getting started: set up a new organization* beforehand (Help center › Administration).

| Time | Topic | What happens |
| --- | --- | --- |
| 0:00–0:05 | Welcome | The administrator's job in the pilot. |
| 0:05–0:15 | Getting started | The setup checklist and the getting-started guide: what order, who to involve. |
| 0:15–0:27 | Users and roles | Invites, roles, linking to employees, deactivating, access reviews. Exercise A1. |
| 0:27–0:32 | Single sign-on | What IT configures (OpenID Connect, SCIM); sessions and sign-out. Discussion only. |
| 0:32–0:42 | Approval workflows | Steps, conditions, self-approval, reminders, publishing a version. Exercise A2. |
| 0:42–0:52 | Imports | Upload, map, validate, dry run, import, roll back. Exercise A3. |
| 0:52–0:57 | Support requests | Receiving reports, replying, status; the support address. Exercise A4. |
| 0:57–1:00 | Questions | |

## Key messages

- Keep at least two administrators.
- Deactivate, never delete; history stays intact.
- Built-in approval rules work for most companies. Change one thing at a time and publish.
- Always dry-run an import first; every import can be rolled back until something is built on top of it.
- Reply to support requests in the app: the person is notified and sees your reply under My support requests.
