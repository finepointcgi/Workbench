# GitLab sync

Links a project's plan (WBS, schedule tasks and assignments) to a GitLab project, both ways. Works with gitlab.com and
self-managed GitLab, including air-gapped servers with an internal certificate authority.

## Setting it up

1. **Admin › Integrations › GitLab** (Manage integrations): the GitLab address and an access token with the `api` scope (personal,
   group or project token). *Test connection* checks the address, the certificate, the token and its scope. A self-managed server
   with an internal certificate: switch on "uses a certificate from our own certificate authority" and paste the CA's PEM. A server
   on a private network also needs the operator setting `GitLab:AllowedPrivateHosts` (docs/operations/configuration.md).
2. **Project › Setup › GitLab** (project setup: Finance, administrators, the project's manager): pick the GitLab project, choose what
   phases become (epics when the group has them, else milestones) and how many summary levels are phases, and *Link and send the plan*.
   Linking adds a "% Complete" and a "Notes" column to the plan when it has none.

## What maps to what

| Workbench | GitLab |
| --- | --- |
| Detail task | Issue |
| Summary task at a phase level | Epic (nested like the plan; Premium/Ultimate) or milestone (`1001.01 Design`; every tier) |
| Task's WBS code | Label `WBS::1001.01.02` (other labels are left alone) |
| Finish | Issue due date; epic/milestone start and due dates |
| Start, WBS, hours | Also written in a block at the end of the description, which Workbench rewrites |
| People staffed on the task (Assign people) and its Assignees | Issue assignees (only the first on Free, which allows one) |
| Hours staffed on the task | Issue time estimate |
| Dependencies | "blocks" links (Premium/Ultimate) or "relates to" |
| Notes column | Description (above Workbench's block) |
| % Complete and Status | From GitLab: closed = 100% and a done status; otherwise child tasks weighted by weight, else the checklist, else Workbench's value |

Time logged in GitLab never goes to timesheets.

## Who owns what

- **Workbench owns the schedule:** dates, dependencies, WBS structure and codes, hours. A GitLab edit is put back and noted.
- **GitLab owns work state:** open/closed and progress. A Workbench edit is put back and noted. Issues someone adds in GitLab under a
  linked epic or milestone wait under *New in GitLab* until someone adds them to the plan (or not).
- **Shared:** title, description, assignees. The side that changed wins; when both changed since the last sync, the later edit wins
  and the other value is noted. People staffed in Assign people can't be unassigned from GitLab.

Nothing is dropped silently: every change goes into the task's activity log (actor "GitLab"), and every override is a note on the
project's GitLab panel with *Use Workbench's* / *Use GitLab's* / *OK, dismiss* where that makes sense.

## How it runs

- Plan changes become integration outbox events (`gitlab.task_changed`) delivered to the hidden **GitLab sync** connector, so they
  are durable and retried with backoff like every delivery (Admin › Integrations › Deliveries and Connectors show them).
- GitLab's webhooks (issue, milestone and epic events) post to `/webhooks/gitlab/{linkId}` with the link's secret in `X-Gitlab-Token`.
  A call is recorded (`gitlab.pull`) and answered at once; the outbox then reads the issue from GitLab. Replays (same
  `Idempotency-Key`, `X-Gitlab-Event-UUID` or body) are ignored. Without the Maintainer role Workbench can't add the webhook; the panel
  shows the URL and secret to add by hand.
- A reconcile every `GitLab:ReconcileMinutes` reads what changed in GitLab and sends what changed here (staffing, dependencies,
  anything missed). *Sync now* does it immediately and re-reads every issue.
- Rate limits: `RateLimit-Remaining`/`Retry-After` pause calls to that server; waiting changes go once the window resets.
- Each issue carries a hidden marker (`workbench-task:{id}`), so a retried create or a re-link finds its issue instead of making a duplicate.
- *Unlink* stops the sync and removes the webhooks Workbench added; issues and tasks stay. Deleting a task closes its issue with a note.
