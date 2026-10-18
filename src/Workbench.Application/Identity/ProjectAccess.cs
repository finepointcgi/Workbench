using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Identity;

/// <summary>
/// Project-accounting access rules that depend on who manages or supervises what, on top of <see cref="AccessControl"/>'s
/// organization roles. Finance and Admin pass every check here; a project manager reads their own projects' financials
/// and the funding of contracts their projects are on; a supervisor sees their direct reports' time.
/// </summary>
public sealed class ProjectAccess(IWorkbenchDbContextFactory dbFactory, AccessControl access) : IFinancialAccessService
{
    public AccessControl Access => access;

    public async Task<bool> CanReadProjectFinancialsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        if (!access.IsAuthenticated)
        {
            return false;
        }

        if ((await access.GetOrgRolesAsync(cancellationToken)).IsFinance())
        {
            return true;
        }

        var me = await access.GetEmployeeIdAsync(cancellationToken);
        if (me is null)
        {
            return false;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<Project>().AsNoTracking().AnyAsync(p => p.Id == projectId && p.ProjectManagerId == me, cancellationToken);
    }

    public async Task<bool> CanReadContractFundingAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        if (!access.IsAuthenticated)
        {
            return false;
        }

        var managed = await ManagedContractIdsAsync(cancellationToken);
        return managed is null || managed.Contains(contractId);
    }

    /// <summary>Throws <see cref="WorkbenchForbiddenException"/> unless the user is Finance, Admin or the project's manager.</summary>
    public async Task RequireProjectFinancialsAsync(Guid projectId, string what, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (!await CanReadProjectFinancialsAsync(projectId, cancellationToken))
        {
            throw new WorkbenchForbiddenException($"Only Finance, administrators and the project's manager can {what}.");
        }
    }

    public async Task RequireContractFundingAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (!await CanReadContractFundingAsync(contractId, cancellationToken))
        {
            throw new WorkbenchForbiddenException("Only Finance, administrators and the managers of its projects can see a contract's funding.");
        }
    }

    /// <summary>Contracts with a project the user manages, or null for Finance and Admin (every contract).</summary>
    public async Task<IReadOnlySet<Guid>?> ManagedContractIdsAsync(CancellationToken cancellationToken = default)
    {
        if ((await access.GetOrgRolesAsync(cancellationToken)).IsFinance())
        {
            return null;
        }

        var me = await access.GetEmployeeIdAsync(cancellationToken);
        if (me is null)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = await db.Set<Project>().AsNoTracking()
            .Where(p => p.ProjectManagerId == me && p.ContractId != null)
            .Select(p => p.ContractId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    /// <summary>The project a budget version belongs to, or null when the version doesn't exist.</summary>
    public async Task<Guid?> ProjectOfBudgetVersionAsync(Guid versionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<BudgetVersion>().AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => (Guid?)v.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Employees whose hours the user may see in timekeeping reports: null (everyone) for Timekeeper, Finance and Admin;
    /// otherwise the user and their direct reports.
    /// </summary>
    public async Task<IReadOnlySet<Guid>?> VisibleTimeEmployeeIdsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if ((await access.GetOrgRolesAsync(cancellationToken)).CanViewAllTimesheets())
        {
            return null;
        }

        var me = await access.GetEmployeeIdAsync(cancellationToken);
        if (me is null)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reports = await db.Set<Employee>().AsNoTracking().Where(e => e.SupervisorId == me).Select(e => e.Id).ToListAsync(cancellationToken);
        return reports.Append(me.Value).ToHashSet();
    }

    /// <summary>True when the user supervises the employee with this id or this email.</summary>
    public async Task<bool> SupervisesAsync(Guid? employeeId, string? email, CancellationToken cancellationToken = default)
    {
        var me = await access.GetEmployeeIdAsync(cancellationToken);
        if (me is null || (employeeId is null && string.IsNullOrWhiteSpace(email)))
        {
            return false;
        }

        var normalized = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<Employee>().AsNoTracking()
            .AnyAsync(e => e.SupervisorId == me && (e.Id == employeeId || e.Email == normalized), cancellationToken);
    }
}
