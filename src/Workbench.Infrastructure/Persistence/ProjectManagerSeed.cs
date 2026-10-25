using Microsoft.EntityFrameworkCore;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Tasks;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for the Project manager role and task assignment: Sam Patel (sam.pm@example.com, Project manager
/// only, not Finance) runs project 1002, an internal project with its own WBS and plan sheet in a workspace Sam owns, and a
/// few tasks are assigned (one to the Viewer, who may still update its status). Runs once, after the other seeds.
/// </summary>
internal static class ProjectManagerSeed
{
    public const string SamEmail = "sam.pm@example.com";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<Project>().AnyAsync(p => p.Code == "1002", cancellationToken)
            || await db.Set<Employee>().FirstOrDefaultAsync(e => e.Email == "pm@example.com", cancellationToken) is not { } pat)
        {
            return;
        }

        var employees = await db.Set<Employee>().ToDictionaryAsync(e => e.Email, cancellationToken);
        var pmCategory = await db.Set<LaborCategory>().FirstOrDefaultAsync(c => c.Code == "PM3", cancellationToken);
        var sam = new Employee
        {
            EmployeeNumber = "E1005", Email = SamEmail, DisplayName = "Sam Patel", OrgUnitId = pat.OrgUnitId, SupervisorId = pat.Id,
            DefaultLaborCategoryId = pmCategory?.Id, HireDate = pat.HireDate,
        };
        db.Add(sam);
        db.Add(new EmployeeCostRate { EmployeeId = sam.Id, EffectiveFrom = sam.HireDate, HourlyCost = 79m });

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var workspace = new Workspace { Name = "1002 Data Platform Pilot" };
        var sheet = new Sheet
        {
            Workspace = workspace,
            Name = "1002 Data Platform Pilot",
            Description = "Plan for project 1002. Its tasks mirror the WBS.",
            SchedulingEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var status = new Column { Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Blocked", "Done"] };
        var percent = new Column { Name = "% Complete", Type = ColumnType.Number, Order = 1 };
        var owner = new Column { Name = "Owner", Type = ColumnType.Person, Order = 2 };
        sheet.Columns.AddRange([status, percent, owner]);

        Item Task(string title, int order, Guid? parent, int startIn, int finishIn, string state, string? wbs, string? person = null) => new()
        {
            Title = title, Order = order, ParentId = parent, Start = today.AddDays(startIn), Finish = today.AddDays(finishIn), CostpointWbsId = wbs,
            Values = new() { [status.Id.ToString()] = state, [percent.Id.ToString()] = state == "Done" ? "100" : state == "In progress" ? "40" : "0", [owner.Id.ToString()] = person },
        };

        var root = Task("Data Platform Pilot", 0, null, -14, 120, "In progress", "1002.01");
        var discovery = Task("Discovery interviews", 0, root.Id, -14, 3, "In progress", "1002.01.01", SamEmail);
        var inventory = Task("Data inventory", 1, root.Id, -7, 6, "Not started", "1002.01.01");
        var build = Task("Pipeline build", 2, root.Id, 7, 60, "Not started", "1002.01.02");
        var pilot = Task("Pilot rollout", 3, root.Id, 61, 120, "Not started", "1002.01.03");
        sheet.Items.AddRange([root, discovery, inventory, build, pilot]);

        var project = new Project
        {
            Code = "1002", Name = "Data Platform Pilot", ProjectManagerId = sam.Id, Status = ProjectStatus.Active,
            Start = today.AddDays(-14), Finish = today.AddDays(120), SheetId = sheet.Id,
        };
        var wbsRoot = new WbsElement { ProjectId = project.Id, Code = "1002.01", Name = "Data Platform Pilot", IsChargeable = false, ItemId = root.Id, Order = 0 };
        var wbsDiscovery = new WbsElement { ProjectId = project.Id, ParentId = wbsRoot.Id, Code = "1002.01.01", Name = "Discovery", ItemId = discovery.Id, Order = 0 };
        var wbsBuild = new WbsElement { ProjectId = project.Id, ParentId = wbsRoot.Id, Code = "1002.01.02", Name = "Build", ItemId = build.Id, Order = 1 };
        var wbsPilot = new WbsElement { ProjectId = project.Id, ParentId = wbsRoot.Id, Code = "1002.01.03", Name = "Pilot rollout", ItemId = pilot.Id, Order = 2 };
        db.AddRange(workspace, sheet, project, wbsRoot, wbsDiscovery, wbsBuild, wbsPilot);
        foreach (var email in new[] { SamEmail, "analyst@example.com", "dev@workbench.local" })
        {
            var id = email == SamEmail ? sam.Id : employees.GetValueOrDefault(email)?.Id;
            if (id is { } employeeId)
            {
                db.AddRange(new[] { wbsDiscovery, wbsBuild, wbsPilot }.Select(w => new ChargeAssignment { WbsElementId = w.Id, EmployeeId = employeeId }));
            }
        }

        db.AddRange(
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = SamEmail, Role = WorkspaceRole.Owner },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = "analyst@example.com", Role = WorkspaceRole.Editor },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = "viewer@example.com", Role = WorkspaceRole.Viewer });

        // Assignments: the Viewer can update their own task's status without edit rights.
        db.AddRange(
            new ItemAssignee { SheetId = sheet.Id, ItemId = inventory.Id, Email = "viewer@example.com", AssignedBy = SamEmail, AssignedAtUtc = now },
            new ItemAssignee { SheetId = sheet.Id, ItemId = inventory.Id, Email = "analyst@example.com", AssignedBy = SamEmail, AssignedAtUtc = now },
            new ItemAssignee { SheetId = sheet.Id, ItemId = build.Id, Email = "dev@workbench.local", AssignedBy = SamEmail, AssignedAtUtc = now });

        // On 1001's plan (Pat's): the analyst is assigned the requirements review.
        var requirements = await db.Items.FirstOrDefaultAsync(i => i.Title == "Requirements review", cancellationToken);
        if (requirements is not null && !await db.Set<ItemAssignee>().AnyAsync(a => a.ItemId == requirements.Id && a.Email == "analyst@example.com", cancellationToken))
        {
            db.Add(new ItemAssignee { SheetId = requirements.SheetId, ItemId = requirements.Id, Email = "analyst@example.com", AssignedBy = "pm@example.com", AssignedAtUtc = now });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
