---
title: Approval workflows
summary: Change who approves timesheets, expenses, purchases and bills, and when.
category: Administration
audience: admin
keywords: approval workflow routing approver steps conditions delegation reminders escalation
order: 53
---
Every kind of request (timesheets, expense reports, vendor costs, requisitions, bills, invoices, journal entries) follows a workflow. The built-in ones match how most contractors work: supervisors approve time, project managers approve project costs, Finance approves large purchases.

## Change a workflow

1. Open **Admin › Approval workflows** and pick the kind of request.
2. Edit the **Steps**. Each step has an approver: the supervisor, the project manager of each charged project, an org unit's manager, everyone with a role (for example Finance), or named people.
3. Add a **Condition** to a step when it should only apply sometimes, for example "amount over $10,000" or "has unallowable costs".
4. Decide what happens when someone would approve their own request (skip to their supervisor is the usual choice).
5. Optionally set reminders after a number of business days, and escalation to the approver's supervisor.
6. Select **Publish new version**.

Requests already on their way keep the version they started with. **Reset to built-in default** goes back to the original rules.

## Org-unit approvers

Set a manager for each org unit under **Org-unit approvers** if your steps route by department.

## Delegation

Approvers who'll be away can delegate from their **Approvals** page. See [Approve timesheets, expenses and requests](help:approving).
