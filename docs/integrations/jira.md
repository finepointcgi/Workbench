# Jira sync

Keeps a project's Jira issues and its plan in step, so a team whose engineers live in Jira doesn't retype anything. Works with
Jira Cloud (an account email and an API token). Jira Data Center follows behind the same client interface (`IJiraClient`).

## Setting it up

1. **Admin › Integrations › Jira** (Manage integrations): the Jira address (`https://yourcompany.atlassian.net`), the email of the
   Atlassian account the token belongs to, and an API token (id.atlassian.com › Security › API tokens). *Test connection* checks the
   address, the email and the token and says what to do when one is wrong. The token is encrypted at rest, never shown again and never
   written to a log; a blank token on a later save keeps the saved one. Give the account access to the Jira projects you will link.
2. **Project › Setup › Jira** (project setup: Finance, administrators, the project's manager): pick the Jira project (search, or type
   its key such as `RAD`), optionally limit it to **one epic** (`RAD-12`) or an extra **JQL filter**, choose who wins a date
   conflict, and *Link and bring in the issues*. If the plan has neither a Status nor a % Complete column, linking adds a Status column
   (Not started / In progress / Done) so Done has somewhere to live.

## What maps to what

| Jira | Workbench |
| --- | --- |
| Issue | A plan task (a top-level row at the end of the plan; move it wherever it belongs, the link follows) |
| Summary | Title. **Jira wins.** |
| Assignee | The task's assignee, matched by email, else by a display name only one person has. **Jira wins.** An assignee Workbench doesn't know is a note on the panel, never an error. |
| Status in Jira's *Done* category | Done (the Status column's Done option, or 100% when the plan only has % Complete). Any other status is not done. |
| Due date | The task's finish (and start, when the task had none). **Both ways; see below.** |
| Original estimate | Shown on the panel and written to a plan Number column named *Estimate (hours)*, *Estimated hours*, *Estimate* or *Hours* when the plan has one. Display only: it doesn't change staffing. |
| Issue key | A link on the task ("Linked to Jira RAD-12 · In Review · synced 3 min ago") and on the project's panel |
| Worklogs | **Never imported.** Time stays in Workbench: timesheets are the cost record. |

## Who wins, in plain words

- **Title and assignee: Jira wins.** Workbench takes Jira's whenever that issue is synced (a webhook, the poll, *Sync now*). A title
  edited here is replaced the next time the issue changes in Jira; the task's activity log keeps the old title.
- **Done flows both ways.** Done in Jira marks the task Done; marking a task Done moves the issue to a status in Jira's Done category
  (Workbench asks Jira which transition leads there). If both sides changed it, Jira's state is kept and the panel says so. **Reopening a
  task is not sent to Jira**; the panel notes it so nobody is surprised. If the issue's workflow has no way to Done from where it is, or a
  required field blocks it, the panel says exactly that and what to do (Jira's own wording).
- **Due dates flow both ways.** A date changed on one side goes to the other. If *both* changed since the last sync, the project's
  setting decides: **Workbench wins (the default)** or **Jira wins**; the panel notes which date was kept. Change it any time on the panel
  (*Rules*). A date cleared in Jira doesn't clear the plan's.
- **New tasks:** with *Create a Jira issue for each task added here from now on* on, a task added in Workbench after linking creates an
  issue (type `Task` unless set). Only leaf tasks added after linking; the plan's existing tasks are never pushed. Each created issue
  carries a label `workbench-{task id}`, so a retried or crashed create finds its issue instead of making a second one.

An issue linked to a task never creates a second task: links are unique per issue and per task, replays and concurrent syncs are
absorbed, and an issue deleted in Jira only marks its task "was linked" (the task stays).

## How it runs

- **Jira → Workbench:** a webhook (`POST /webhooks/jira/{linkId}`) names the issue, then the sync *reads that issue from Jira* (the
  payload's values are never trusted). A **poll** every `Jira:ReconcileMinutes` (default 10) reads issues changed since the last poll
  (`updated >= -Nm`, with minutes of overlap, so time zones never matter) and also sends Workbench changes no event reported. *Sync now*
  re-reads every issue. The poll is the fallback: with no webhook at all, nothing is missed, only later.
- **Workbench → Jira:** plan changes become integration outbox events (`jira.task_changed`) delivered to the hidden **Jira sync**
  connector, so they are durable and retried with backoff like every delivery (Admin › Integrations › Deliveries). A change that
  doesn't concern Jira (a rename, a note) makes no Jira call at all.
- **Fails soft:** a Jira that is down, slow or rate limiting (429) never blocks the plan. The panel shows "Jira isn't answering" with
  the reason and "Nobody is blocked", retryable failures are retried by the outbox, and the next poll catches up. A revoked token or a
  project the account can no longer see is "Needs attention" with what to do.
- **Last synced and errors:** the panel and each task show when they last synced and any error.

## Setting up the webhook (optional, for seconds instead of minutes)

The panel's *Set up the webhook* shows the URL and a per-link secret. In **Jira Cloud** (Settings › System › WebHooks › Create a
WebHook): paste the URL and the secret, tick *Issue: created, updated, deleted*, and filter with `project = RAD`. Jira signs each call
with the secret (`X-Hub-Signature: sha256=…`, the HMAC-SHA256 of the body). An **automation rule** ("Send web request") can instead set
the header `X-Workbench-Token` to the secret. Calls with neither are refused (401); replays (`X-Atlassian-Webhook-Identifier`, else the
body's hash) are ignored; secrets are compared in constant time. Unlinking deletes the secret.

## Permissions

The same as GitLab: Admin › Integrations › Jira needs *Manage integrations* and is never open to API keys. A project's panel is read by
anyone who can see the project's plan; linking, changing the rules, unlinking, *Sync now*, the webhook secret and closing notes need
project setup (Finance, administrators, the project's manager). Everything the sync changes is attributed to the actor **Jira** in the
task's activity log.

## Not in this version

Jira Data Center, custom-field estimates (story points), sub-task hierarchy, comments and attachments, and worklogs-as-time (by design).
