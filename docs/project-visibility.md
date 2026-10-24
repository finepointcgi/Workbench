# Project visibility

People who aren't on a project can't see it: not in the project list, the Projects hub, search, reports, dashboards,
the assistant, notifications or the API. The rule lives in one place,
`src/Workbench.Application/Identity/ProjectVisibility.cs`, and every read path asks it.

## Who is "on" a project

Someone is on a project when any of these hold:

1. **They manage it.** `Project.ProjectManagerId` is their employee record.
2. **They're assigned to it.** A resource allocation on the project, a charge assignment on one of its WBS elements, or
   a task assignment (`ItemAssignee`) on its plan sheet.
3. **They're a member of its plan's workspace.** A project links to one plan sheet (`Project.SheetId`). Membership of
   the workspace holding that sheet counts, at any workspace role.
4. **They charge to it, or may.** They have time, an expense or other ODC, or a travel authorization on it. Or they
   may charge it now: an active project with an open, chargeable lowest-level code that needs no assignment. That's
   the same eligibility the timesheet's charge-code list uses (`ProjectService.ListChargeCodesForDatesAsync`), so
   indirect and leave projects are visible to everyone.
5. **They approve its documents.** This covers a timesheet, expense report, ODC or travel authorization that charges the
   project, or an approval step scoped to it (`project:{id}`). It counts once the task has reached them (any status
   but Waiting), whether they're named on it, decided it, or are the assignee's current delegate.

## Who sees more

| Role | Projects visible |
| --- | --- |
| Admin, Finance | Every project |
| Resource manager | Every project. They staff work across the portfolio; money still follows the financials rule. |
| Project manager (role) | The projects they're on (rule 1 covers theirs) |
| Timekeeper | The projects they're on. Reviewing every timesheet shows the codes on those timesheets, not the projects. |
| Business development, Pricing | The projects they're on. The pipeline and pricing modules keep their own role rules. |
| HR, Materials | The projects they're on. Their modules keep their own role rules. |
| Employee | The projects they're on |
| System user, trusted background reader | Every project |

Company access applies on top. A project in a company the user can't use, or outside the company picked in the
switcher, stays hidden whatever the role or rule.

## Hidden means not found

A hidden project is `WorkbenchNotFoundException` (HTTP 404). `IProjectService.GetProjectAsync` returns null. It's never
"forbidden", so its existence doesn't leak. Once a project is visible, the finer rules still apply and may say
forbidden. For example, an employee who charges to a project can open it but can't read its financials.

## Where it's enforced

The rule is enforced in the permission decorators (`AddWorkbenchIdentity`), not in the UI:

- **Project list and pages** (`PermissionCheckingProjectService`): the list is filtered. Get and get-for-sheet return
  null when the project is hidden. WBS and charge-assignment lists are not found.
- **Contracts** (`PermissionCheckingContractService`, `VisibleContractIdsAsync`): a contract is listed and found when a
  project on it is visible. Everyone who sees every project sees every contract. A contract with no project yet is
  listed for those who may create projects (so a PM can request the first one). Funding stays Finance, Admin and the
  managers of a project on it.
- **Charge codes:** a person's list is theirs, their supervisor's and their timekeeper's, Finance's and Admin's
  (`time.view_all`). Anyone else is refused (403, logged), so the list can't be used to learn who works on what. A
  timesheet opened by its approver carries no charge codes.
- **Projects hub** (`ProjectHubService`, `PermissionCheckingProjectHubService`): the list, the project page, and the
  team, financials, budgets, revenue, earned value, ceilings, CLINs, commitments, narratives and workload tabs. These go
  through `ProjectAccess`, which checks visibility first.
- **Project costs:** `PermissionCheckingOdcService` checks visibility on ODC lists and on vendor ODC entry.
- **Resourcing** (`ResourcingAccess` and its decorators): staffing, open demand, allocation lists, the heat map's project
  filter, and allocation changes.
- **Command palette and search:** the palette reads `IProjectService`.
- **Assistant tools:** they run under the user's decorated services. Narratives use `ProjectAccess`.
- **Reports:** the report writer's Projects and WBS source is filtered (`ReportAccessContext.VisibleProjects`). In
  standard reports, a hidden project as a parameter or drill-down is not found.
- **Dashboards and widgets:** they read `IProjectService` and `ProjectAccess`.
- **My work and to-dos:** these are already limited to your own sheets, tasks and managed projects.
- **Notifications** (`ProjectScopedNotificationService`): the list leaves out notifications that link to a project you
  can no longer see.
- **API and API client:** the endpoints call the same decorated services, so hidden projects return 404.

## Adding a project-scoped read

Inject `ProjectVisibility`, or build on `ProjectAccess` / `ResourcingAccess`, in the feature's permission decorator:

- **One project:** `RequireVisibleAsync(projectId)` or `RequireVisibleWbsAsync(wbsId)`.
- **A list:** `FilterAsync(items, x => x.ProjectId)`.
- **A query that needs the ids:** `VisibleProjectIdsAsync()` (null means unrestricted).
