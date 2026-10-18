using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

public sealed class ProjectService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit) : IProjectService
{
    public async Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contracts = await db.Set<Contract>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ContractNumber, cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().OrderBy(p => p.Code).ToListAsync(cancellationToken);
        return projects.Select(p => p.ToDto(p.ContractId is { } c ? contracts.GetValueOrDefault(c) : null)).ToList();
    }

    public async Task<ProjectDto?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken);
        return project is null ? null : await ToDtoAsync(db, project, cancellationToken);
    }

    public async Task<ProjectDto?> GetProjectForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.SheetId == sheetId, cancellationToken);
        return project is null ? null : await ToDtoAsync(db, project, cancellationToken);
    }

    public async Task<ProjectDto> CreateProjectAsync(SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = new Project { Code = "", Name = "" };
        await ApplyAsync(db, project, request, cancellationToken);
        db.Set<Project>().Add(project);
        var dto = await ToDtoAsync(db, project, cancellationToken);
        audit.Record(db, nameof(Project), project.Id, "Created", after: dto);
        await db.SaveChangesAsync(cancellationToken);
        return dto;
    }

    public async Task<ProjectDto> UpdateProjectAsync(Guid projectId, SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        var before = await ToDtoAsync(db, project, cancellationToken);
        await ApplyAsync(db, project, request, cancellationToken);
        var after = await ToDtoAsync(db, project, cancellationToken);
        audit.Record(db, nameof(Project), project.Id, "Updated", before, after);
        await db.SaveChangesAsync(cancellationToken);
        return after;
    }

    public async Task<IReadOnlyList<WbsElementDto>> ListWbsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        var elements = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync(cancellationToken);
        return elements.OrderBy(w => w.Code, StringComparer.Ordinal).Select(w => w.ToDto()).ToList();
    }

    public async Task<WbsElementDto> CreateWbsElementAsync(Guid projectId, SaveWbsElementRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        var element = new WbsElement { ProjectId = projectId, Code = "", Name = "" };
        await ApplyAsync(db, element, request, cancellationToken);
        element.Order = await db.Set<WbsElement>().CountAsync(w => w.ProjectId == projectId && w.ParentId == request.ParentId, cancellationToken);
        db.Set<WbsElement>().Add(element);
        audit.Record(db, nameof(WbsElement), element.Id, "Created", after: element.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return element.ToDto();
    }

    public async Task<WbsElementDto> UpdateWbsElementAsync(Guid wbsElementId, SaveWbsElementRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var element = await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Id == wbsElementId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("WBS element", wbsElementId);
        var before = element.ToDto();
        await ApplyAsync(db, element, request, cancellationToken);
        audit.Record(db, nameof(WbsElement), element.Id, "Updated", before, element.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return element.ToDto();
    }

    public async Task<IReadOnlyList<ChargeAssignmentDto>> ListAssignmentsAsync(Guid wbsElementId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<ChargeAssignment>().AsNoTracking().Where(a => a.WbsElementId == wbsElementId).ToListAsync(cancellationToken);
        return rows.Select(a => a.ToDto()).ToList();
    }

    public async Task<ChargeAssignmentDto> AddAssignmentAsync(Guid wbsElementId, AddChargeAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        if (request.From is { } from && request.To is { } to && to < from)
        {
            throw new ArgumentException("Assignment end can't be before its start.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<WbsElement>().AnyAsync(w => w.Id == wbsElementId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("WBS element", wbsElementId);
        }

        if (!await db.Set<Employee>().AnyAsync(e => e.Id == request.EmployeeId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Employee", request.EmployeeId);
        }

        var assignment = new ChargeAssignment
        {
            WbsElementId = wbsElementId,
            EmployeeId = request.EmployeeId,
            LaborCategoryId = request.LaborCategoryId,
            From = request.From,
            To = request.To,
        };
        db.Set<ChargeAssignment>().Add(assignment);
        audit.Record(db, nameof(ChargeAssignment), assignment.Id, "Created", after: assignment.ToDto());
        await db.SaveChangesAsync(cancellationToken);
        return assignment.ToDto();
    }

    public async Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var assignment = await db.Set<ChargeAssignment>().FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Charge assignment", assignmentId);
        db.Set<ChargeAssignment>().Remove(assignment);
        audit.Record(db, nameof(ChargeAssignment), assignment.Id, "Deleted", before: assignment.ToDto());
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ChargeCodeDto>> ListChargeCodesAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Employee", employeeId);
        if (!employee.IsActiveOn(date))
        {
            return [];
        }

        var projects = await db.Set<Project>().AsNoTracking()
            .Where(p => p.Status == ProjectStatus.Active)
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var elements = await db.Set<WbsElement>().AsNoTracking().ToListAsync(cancellationToken);
        var parents = elements.Where(w => w.ParentId is not null).Select(w => w.ParentId!.Value).ToHashSet();
        var assignments = await db.Set<ChargeAssignment>().AsNoTracking()
            .Where(a => a.EmployeeId == employeeId)
            .ToListAsync(cancellationToken);

        var codes = new List<ChargeCodeDto>();
        foreach (var element in elements.Where(w => w.IsOpen && w.IsChargeable && !parents.Contains(w.Id)))
        {
            if (!projects.TryGetValue(element.ProjectId, out var project)
                || (project.Start is { } start && date < start)
                || (project.Finish is { } finish && date > finish))
            {
                continue;
            }

            var assignment = assignments.FirstOrDefault(a => a.WbsElementId == element.Id
                && (a.From is null || a.From <= date) && (a.To is null || a.To >= date));
            if (element.RequiresAssignment && assignment is null)
            {
                continue;
            }

            // An employee with no default labor category can only charge codes whose assignment names one.
            if ((assignment?.LaborCategoryId ?? employee.DefaultLaborCategoryId) is not { } laborCategoryId)
            {
                continue;
            }

            codes.Add(new ChargeCodeDto(element.Id, element.Code, element.Name, project.Id, project.Code, project.Name,
                (ChargeCodeKind)(int)element.Kind, laborCategoryId));
        }

        return codes.OrderBy(c => c.Kind).ThenBy(c => c.Code, StringComparer.Ordinal).ToList();
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, Project project, SaveProjectRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code.Required("Code");
        if (await db.Set<Project>().AnyAsync(p => p.Id != project.Id && p.Code == code, cancellationToken))
        {
            throw new ArgumentException($"Project code '{code}' is already used.");
        }

        if (request.ContractId is { } contractId && !await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        if (request.Start is { } start && request.Finish is { } finish && finish < start)
        {
            throw new ArgumentException("Project finish can't be before its start.");
        }

        project.Code = code;
        project.Name = request.Name.Required("Name");
        project.ContractId = request.ContractId;
        project.ProjectManagerId = request.ProjectManagerId;
        project.Status = (ProjectStatus)(int)request.Status;
        project.Start = request.Start;
        project.Finish = request.Finish;
    }

    private static async Task ApplyAsync(IWorkbenchDbContext db, WbsElement element, SaveWbsElementRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code.Required("Code");
        if (await db.Set<WbsElement>().AnyAsync(w => w.Id != element.Id && w.Code == code, cancellationToken))
        {
            throw new ArgumentException($"WBS code '{code}' is already used.");
        }

        if (request.ParentId is { } parentId)
        {
            var parent = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == parentId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("WBS element", parentId);
            if (parent.ProjectId != element.ProjectId)
            {
                throw new ArgumentException("A WBS element's parent must be in the same project.");
            }

            if (!code.StartsWith(parent.Code + ".", StringComparison.Ordinal))
            {
                throw new ArgumentException($"WBS code '{code}' must start with its parent's code '{parent.Code}.'.");
            }

            for (Guid? cursor = parentId; cursor is { } id;)
            {
                if (id == element.Id)
                {
                    throw new ArgumentException("A WBS element can't be placed under itself or one of its descendants.");
                }

                cursor = (await db.Set<WbsElement>().AsNoTracking().FirstAsync(w => w.Id == id, cancellationToken)).ParentId;
            }
        }

        element.ParentId = request.ParentId;
        element.Code = code;
        element.Name = request.Name.Required("Name");
        element.Kind = (ChargeKind)(int)request.Kind;
        element.IsChargeable = request.IsChargeable;
        element.IsOpen = request.IsOpen;
        element.RequiresAssignment = request.RequiresAssignment;
    }

    private static async Task<ProjectDto> ToDtoAsync(IWorkbenchDbContext db, Project project, CancellationToken cancellationToken)
    {
        var contractNumber = project.ContractId is { } id
            ? await db.Set<Contract>().AsNoTracking().Where(c => c.Id == id).Select(c => c.ContractNumber).FirstOrDefaultAsync(cancellationToken)
            : null;
        return project.ToDto(contractNumber);
    }
}
