---
title: Users and roles
summary: Invite people, give them the right role, and deactivate them when they leave.
category: Administration
audience: admin
keywords: users roles invite admin finance timekeeper resource manager deactivate access review workspace
order: 51
---
Open **Admin › People and access › Users and security**.

## Invite someone

On the **Invites** tab, enter their work email and name, pick their roles, and select **Invite**. They get the roles when they first sign in with that email. Invitations expire after 7 days. If your identity provider adds people automatically (SCIM), you don't need invites.

## Roles

Everyone is an **Employee**: they enter their own time, expenses and leave, and see the projects they work on. Add roles for more:

| Role | What it adds |
| --- | --- |
| Timekeeper | Sees everyone's timesheets. |
| Resource manager | Plans staffing across all projects. |
| Finance | Accounting, billing, payables, cash, payroll, revenue and setup. |
| Admin | Everything, including users, security and approval workflows. |

Some access comes from what people do rather than a role: supervisors approve their team's time, and a project's manager sees its money.

Sheets have their own access through workspaces: Viewer, Commenter, Editor or Owner.

## Link a user to an employee

A user is matched to their employee record by email. If the emails differ, link them on the **Users** tab.

## When someone leaves

**Deactivate** the user. They can't sign in, their open sessions end at once, and everything they did stays in the history. Users are never deleted.

## Access reviews

Every quarter, start a review on the **Access reviews** tab, mark each person **Keep** or **Revoke**, and complete it. The completed review is kept as evidence for auditors.
