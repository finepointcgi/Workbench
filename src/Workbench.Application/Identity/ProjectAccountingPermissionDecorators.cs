using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.Identity;

// Organization-role checks in front of the project-accounting services. Anyone signed in can read setup data
// (employees, contracts, projects, WBS, the fiscal calendar) because timesheets and plans need it. Rates are
// sensitive both ways; setup writes, fiscal close and the audit log are for Finance and Admin.

public sealed class PermissionCheckingPeopleService(IPeopleService inner, AccessControl access) : IPeopleService
{
    public Task<IReadOnlyList<EmployeeDto>> ListEmployeesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListEmployeesAsync(includeInactive, cancellationToken);
    }

    public Task<EmployeeDto?> GetEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetEmployeeAsync(employeeId, cancellationToken);
    }

    public Task<EmployeeDto?> GetCurrentEmployeeAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetCurrentEmployeeAsync(cancellationToken);
    }

    public async Task<EmployeeDto> CreateEmployeeAsync(SaveEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("add employees", cancellationToken);
        return await inner.CreateEmployeeAsync(request, cancellationToken);
    }

    public async Task<EmployeeDto> UpdateEmployeeAsync(Guid employeeId, SaveEmployeeRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change employees", cancellationToken);
        return await inner.UpdateEmployeeAsync(employeeId, request, cancellationToken);
    }

    public Task<IReadOnlyList<OrgUnitDto>> ListOrgUnitsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListOrgUnitsAsync(cancellationToken);
    }

    public async Task<OrgUnitDto> CreateOrgUnitAsync(CreateOrgUnitRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("add organization units", cancellationToken);
        return await inner.CreateOrgUnitAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<LaborCategoryDto>> ListLaborCategoriesAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListLaborCategoriesAsync(cancellationToken);
    }

    public async Task<LaborCategoryDto> CreateLaborCategoryAsync(SaveLaborCategoryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up labor categories", cancellationToken);
        return await inner.CreateLaborCategoryAsync(request, cancellationToken);
    }

    public async Task<LaborCategoryDto> UpdateLaborCategoryAsync(Guid laborCategoryId, SaveLaborCategoryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up labor categories", cancellationToken);
        return await inner.UpdateLaborCategoryAsync(laborCategoryId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<CostRateDto>> ListCostRatesAsync(Guid employeeId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("see cost rates", cancellationToken);
        return await inner.ListCostRatesAsync(employeeId, cancellationToken);
    }

    public async Task<CostRateDto> AddCostRateAsync(Guid employeeId, AddCostRateRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change cost rates", cancellationToken);
        return await inner.AddCostRateAsync(employeeId, request, cancellationToken);
    }
}

/// <summary>
/// Anyone signed in can list contracts (pickers and labels need their numbers and names), but ceiling and funded values
/// are funding data: only Finance, administrators and the managers of a project on the contract see them; everyone else
/// gets zeros.
/// </summary>
public sealed class PermissionCheckingContractService(IContractService inner, AccessControl access, ProjectAccess projects) : IContractService
{
    public Task<IReadOnlyList<CustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListCustomersAsync(cancellationToken);
    }

    public async Task<CustomerDto> CreateCustomerAsync(SaveCustomerRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up customers", cancellationToken);
        return await inner.CreateCustomerAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<ContractDto>> ListContractsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var contracts = await inner.ListContractsAsync(cancellationToken);
        var managed = await projects.ManagedContractIdsAsync(cancellationToken);
        return managed is null ? contracts : contracts.Select(c => WithoutFunding(c, managed)).ToList();
    }

    public async Task<ContractDto?> GetContractAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var contract = await inner.GetContractAsync(contractId, cancellationToken);
        var managed = contract is null ? null : await projects.ManagedContractIdsAsync(cancellationToken);
        return contract is null || managed is null ? contract : WithoutFunding(contract, managed);
    }

    private static ContractDto WithoutFunding(ContractDto contract, IReadOnlySet<Guid> managed) =>
        managed.Contains(contract.Id) ? contract : contract with { CeilingValue = 0, FundedValue = 0 };

    public async Task<ContractDto> CreateContractAsync(SaveContractRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up contracts", cancellationToken);
        return await inner.CreateContractAsync(request, cancellationToken);
    }

    public async Task<ContractDto> UpdateContractAsync(Guid contractId, SaveContractRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change contracts", cancellationToken);
        return await inner.UpdateContractAsync(contractId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<BillRateDto>> ListBillRatesAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("see bill rates", cancellationToken);
        return await inner.ListBillRatesAsync(contractId, cancellationToken);
    }

    public async Task<BillRateDto> AddBillRateAsync(Guid contractId, AddBillRateRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change bill rates", cancellationToken);
        return await inner.AddBillRateAsync(contractId, request, cancellationToken);
    }
}

/// <summary>
/// Projects and their WBS: anyone signed in reads them. Finance and administrators set up every project. The Project manager
/// role creates projects (as their manager) and sets up the ones they manage: details, WBS and charge assignments. A
/// project on a contract that a project manager creates is a request: it stays in Planning until Finance activates it, and
/// project managers can't change a project's contract or hand it to another manager.
/// </summary>
public sealed class PermissionCheckingProjectService(
    IProjectService inner, AccessControl access, ProjectAccess projects, IWorkbenchDbContextFactory dbFactory,
    Notifications.INotificationPublisher? notifications = null) : IProjectService
{
    public Task<IReadOnlyList<ProjectDto>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListProjectsAsync(cancellationToken);
    }

    public Task<ProjectDto?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetProjectAsync(projectId, cancellationToken);
    }

    public Task<ProjectDto?> GetProjectForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetProjectForSheetAsync(sheetId, cancellationToken);
    }

    public async Task<ProjectDto> CreateProjectAsync(SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        var roles = await access.GetOrgRolesAsync(cancellationToken);
        if (roles.IsFinance())
        {
            return await AfterSaveAsync(await inner.CreateProjectAsync(request, cancellationToken), cancellationToken);
        }

        if (!roles.HasFlag(OrgRoles.ProjectManager))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and project managers can set up projects.");
        }

        var me = await access.GetEmployeeIdAsync(cancellationToken)
            ?? throw new WorkbenchForbiddenException("You need an employee record to manage projects. Ask Finance to add you under People.");
        if (request.ProjectManagerId is { } other && other != me)
        {
            throw new WorkbenchForbiddenException("Project managers create projects they manage themselves; Finance can set another manager.");
        }

        // On a contract, it's a request: Planning (not chargeable) until Finance activates it.
        var isRequest = request.ContractId is not null;
        request = request with { ProjectManagerId = me, Status = isRequest ? ProjectState.Planning : request.Status };
        var created = await AfterSaveAsync(await inner.CreateProjectAsync(request, cancellationToken), cancellationToken);
        if (isRequest)
        {
            await NotifyFinanceOfRequestAsync(created, cancellationToken);
        }

        return created;
    }

    public async Task<ProjectDto> UpdateProjectAsync(Guid projectId, SaveProjectRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (!(await access.GetOrgRolesAsync(cancellationToken)).IsFinance())
        {
            await projects.RequireProjectSetupAsync(projectId, "change project setup", cancellationToken);
            var current = await inner.GetProjectAsync(projectId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Project", projectId);
            if (request.ContractId != current.ContractId)
            {
                throw new WorkbenchForbiddenException("Only Finance can move a project to another contract.");
            }

            if (request.ProjectManagerId != current.ProjectManagerId)
            {
                throw new WorkbenchForbiddenException("Only Finance can hand a project to another manager.");
            }

            if (current.ContractId is not null && current.Status == ProjectState.Planning && request.Status != ProjectState.Planning)
            {
                throw new WorkbenchForbiddenException("Finance activates projects on a contract. It stays in Planning until they do.");
            }
        }

        return await AfterSaveAsync(await inner.UpdateProjectAsync(projectId, request, cancellationToken), cancellationToken);
    }

    public Task<IReadOnlyList<WbsElementDto>> ListWbsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListWbsAsync(projectId, cancellationToken);
    }

    public async Task<WbsElementDto> CreateWbsElementAsync(Guid projectId, SaveWbsElementRequest request, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectSetupAsync(projectId, "set up the WBS", cancellationToken);
        return await inner.CreateWbsElementAsync(projectId, request, cancellationToken);
    }

    public async Task<WbsElementDto> UpdateWbsElementAsync(Guid wbsElementId, SaveWbsElementRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await projects.RequireProjectSetupAsync(await projects.ProjectOfWbsElementAsync(wbsElementId, cancellationToken), "change the WBS", cancellationToken);
        return await inner.UpdateWbsElementAsync(wbsElementId, request, cancellationToken);
    }

    public Task<IReadOnlyList<ChargeAssignmentDto>> ListAssignmentsAsync(Guid wbsElementId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListAssignmentsAsync(wbsElementId, cancellationToken);
    }

    public async Task<ChargeAssignmentDto> AddAssignmentAsync(Guid wbsElementId, AddChargeAssignmentRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await projects.RequireProjectSetupAsync(await projects.ProjectOfWbsElementAsync(wbsElementId, cancellationToken), "assign people to charge codes", cancellationToken);
        return await inner.AddAssignmentAsync(wbsElementId, request, cancellationToken);
    }

    public async Task RemoveAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await projects.RequireProjectSetupAsync(await projects.ProjectOfAssignmentAsync(assignmentId, cancellationToken), "remove charge assignments", cancellationToken);
        await inner.RemoveAssignmentAsync(assignmentId, cancellationToken);
    }

    public Task<IReadOnlyList<ChargeCodeDto>> ListChargeCodesAsync(Guid employeeId, DateOnly date, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListChargeCodesAsync(employeeId, date, cancellationToken);
    }

    public Task<IReadOnlyDictionary<DateOnly, IReadOnlyList<ChargeCodeDto>>> ListChargeCodesForDatesAsync(
        Guid employeeId, IReadOnlyCollection<DateOnly> dates, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListChargeCodesForDatesAsync(employeeId, dates, cancellationToken);
    }

    /// <summary>The project's manager becomes Owner of its plan sheet's workspace (when they have the role).</summary>
    private async Task<ProjectDto> AfterSaveAsync(ProjectDto project, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await ProjectWorkspaceOwnership.EnsureForProjectAsync(db, project.Id, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return project;
    }

    private async Task NotifyFinanceOfRequestAsync(ProjectDto project, CancellationToken cancellationToken)
    {
        if (notifications is null)
        {
            return;
        }

        List<string> finance;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            finance = await db.Set<Domain.Identity.AppUser>().AsNoTracking().Where(u => u.IsActive && u.IsFinance).Select(u => u.Email).ToListAsync(cancellationToken);
        }

        var me = access.Email;
        await notifications.PublishAsync(finance.Where(e => !string.Equals(e, me, StringComparison.OrdinalIgnoreCase)), ProjectRequestKind,
            $"{me} requested project {project.Code} {project.Name}",
            $"It's on contract {project.ContractNumber} and stays in Planning until you activate it.",
            link: $"setup/projects/{project.Id}");
    }

    /// <summary>The notification kind for a project manager's project request.</summary>
    public const string ProjectRequestKind = "project.request";
}

/// <summary>
/// Plan sheet syncing changes the WBS and the plan, so it's project setup (Finance, Admin, or the project's manager with
/// the role). A project manager's new plan sheet goes in a workspace of its own, named after the project, which they own.
/// </summary>
public sealed class PermissionCheckingPlanSyncService(
    IPlanSyncService inner, ProjectAccess projects, IWorkbenchDbContextFactory dbFactory, SheetWorkspacePreference preference) : IPlanSyncService
{
    public async Task<ProjectDto> CreatePlanSheetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectSetupAsync(projectId, "create the plan sheet", cancellationToken);
        if (!(await projects.Access.GetOrgRolesAsync(cancellationToken)).IsFinance())
        {
            preference.WorkspaceId = await ProjectWorkspaceAsync(projectId, cancellationToken);
        }

        ProjectDto project;
        try
        {
            project = await inner.CreatePlanSheetAsync(projectId, cancellationToken);
        }
        finally
        {
            preference.WorkspaceId = null;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await ProjectWorkspaceOwnership.EnsureForProjectAsync(db, projectId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return project;
    }

    public Task<PlanSyncReport> DiffAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        return inner.DiffAsync(projectId, cancellationToken);
    }

    public async Task<PlanSyncReport> ApplyAsync(Guid projectId, ApplyPlanSyncRequest request, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectSetupAsync(projectId, "sync the plan and the WBS", cancellationToken);
        return await inner.ApplyAsync(projectId, request, cancellationToken);
    }

    /// <summary>The workspace named after the project ("1002 Data platform"), created with the manager as Owner.</summary>
    private async Task<Guid> ProjectWorkspaceAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Domain.ProjectAccounting.Project>().AsNoTracking().FirstAsync(p => p.Id == projectId, cancellationToken);
        var name = $"{project.Code} {project.Name}";
        name = name.Length > 200 ? name[..200] : name;
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Name == name, cancellationToken);
        if (workspace is null)
        {
            workspace = db.Workspaces.Add(new Domain.Workspace { Name = name }).Entity;
        }

        await ProjectWorkspaceOwnership.MakeOwnerAsync(db, workspace.Id, projects.Access.Email, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return workspace.Id;
    }
}

public sealed class PermissionCheckingFiscalCalendarService(IFiscalCalendarService inner, AccessControl access) : IFiscalCalendarService
{
    public Task<IReadOnlyList<FiscalPeriodDto>> ListPeriodsAsync(int? fiscalYear = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListPeriodsAsync(fiscalYear, cancellationToken);
    }

    public Task<FiscalPeriodDto?> GetPeriodForDateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetPeriodForDateAsync(date, cancellationToken);
    }

    public async Task<IReadOnlyList<FiscalPeriodDto>> CreateFiscalYearAsync(int fiscalYear, DateOnly start, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("set up fiscal years", cancellationToken);
        return await inner.CreateFiscalYearAsync(fiscalYear, start, cancellationToken);
    }

    public async Task<FiscalPeriodDto> SetPeriodStatusAsync(Guid periodId, FiscalPeriodState status, string reason, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("close or reopen fiscal periods", cancellationToken);
        return await inner.SetPeriodStatusAsync(periodId, status, reason, cancellationToken);
    }
}

public sealed class PermissionCheckingAuditLogService(IAuditLogService inner, AccessControl access) : IAuditLogService
{
    public async Task<IReadOnlyList<AuditEventDto>> ListAsync(string? entityType = null, Guid? entityId = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("read the audit log", cancellationToken);
        return await inner.ListAsync(entityType, entityId, limit, cancellationToken);
    }
}
