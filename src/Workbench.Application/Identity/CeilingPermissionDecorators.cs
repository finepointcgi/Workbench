using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Closeout;
using Workbench.Domain.Ceilings;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Identity;

// Ceilings and closeout: read by Finance, Admin and the managers of the contract's projects (the project's manager for
// project ceilings and closeouts); every change is Finance or Admin, except that a project manager may grant and revoke
// overrides on ceilings that allow it and cover a project they manage.

public static class CeilingPermissionRegistration
{
    public static IServiceCollection AddWorkbenchCeilingPermissions(this IServiceCollection services)
    {
        services.Decorate<ICeilingService, PermissionCheckingCeilingService>();
        services.Decorate<ICloseoutService, PermissionCheckingCloseoutService>();
        return services;
    }
}

public sealed class PermissionCheckingCeilingService(ICeilingService inner, ProjectAccess projects, IWorkbenchDbContextFactory dbFactory) : ICeilingService
{
    private const string Manage = "manage ceilings";

    public async Task<ContractCeilingStatusDto> GetContractStatusAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.RequireContractFundingAsync(contractId, cancellationToken);
        return await inner.GetContractStatusAsync(contractId, cancellationToken);
    }

    public async Task<ProjectCeilingStatusDto> GetProjectStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await projects.RequireProjectFinancialsAsync(projectId, "see this project's ceilings", cancellationToken);
        return await inner.GetProjectStatusAsync(projectId, cancellationToken);
    }

    public async Task<CeilingStatusDto> GetAsync(Guid ceilingId, CancellationToken cancellationToken = default)
    {
        await RequireReadAsync(ceilingId, cancellationToken);
        return await inner.GetAsync(ceilingId, cancellationToken);
    }

    public async Task<CeilingDto> CreateAsync(SaveCeilingRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<CeilingDto> UpdateAsync(Guid ceilingId, SaveCeilingRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.UpdateAsync(ceilingId, request, cancellationToken);
    }

    public async Task<CeilingDto> DeactivateAsync(Guid ceilingId, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.DeactivateAsync(ceilingId, cancellationToken);
    }

    public async Task<IReadOnlyList<CeilingOverrideDto>> ListOverridesAsync(Guid ceilingId, CancellationToken cancellationToken = default)
    {
        await RequireReadAsync(ceilingId, cancellationToken);
        return await inner.ListOverridesAsync(ceilingId, cancellationToken);
    }

    public async Task<CeilingOverrideDto> GrantOverrideAsync(Guid ceilingId, GrantCeilingOverrideRequest request, CancellationToken cancellationToken = default)
    {
        await RequireOverrideAsync(ceilingId, cancellationToken);
        return await inner.GrantOverrideAsync(ceilingId, request, cancellationToken);
    }

    public async Task<CeilingOverrideDto> RevokeOverrideAsync(Guid overrideId, CeilingReasonRequest request, CancellationToken cancellationToken = default)
    {
        projects.Access.RequireSignedIn();
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var ceilingId = await db.Set<CeilingOverride>().AsNoTracking().Where(o => o.Id == overrideId).Select(o => (Guid?)o.CeilingId).FirstOrDefaultAsync(cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Ceiling override", overrideId);
            await RequireOverrideAsync(ceilingId, cancellationToken);
        }

        return await inner.RevokeOverrideAsync(overrideId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<RateCeilingDto>> ListRateCeilingsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.RequireContractFundingAsync(contractId, cancellationToken);
        return await inner.ListRateCeilingsAsync(contractId, cancellationToken);
    }

    public async Task<RateCeilingDto> CreateRateCeilingAsync(Guid contractId, SaveRateCeilingRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.CreateRateCeilingAsync(contractId, request, cancellationToken);
    }

    public async Task<RateCeilingDto> DeactivateRateCeilingAsync(Guid rateCeilingId, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.DeactivateRateCeilingAsync(rateCeilingId, cancellationToken);
    }

    public async Task<int> CheckAlertsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync("check ceiling alerts", cancellationToken);
        return await inner.CheckAlertsAsync(contractId, cancellationToken);
    }

    private async Task<Ceiling> LoadAsync(Guid ceilingId, CancellationToken ct)
    {
        projects.Access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<Ceiling>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == ceilingId, ct) ?? throw WorkbenchNotFoundException.For("Ceiling", ceilingId);
    }

    private async Task RequireReadAsync(Guid ceilingId, CancellationToken ct)
    {
        var c = await LoadAsync(ceilingId, ct);
        if (c.ProjectId is { } p && c.Level is not (CeilingLevel.Contract or CeilingLevel.Clin))
        {
            await projects.RequireProjectFinancialsAsync(p, "see this ceiling", ct);
        }
        else if (c.ContractId is { } contract)
        {
            await projects.RequireContractFundingAsync(contract, ct);
        }
    }

    private async Task RequireOverrideAsync(Guid ceilingId, CancellationToken ct)
    {
        var c = await LoadAsync(ceilingId, ct);
        if ((await projects.Access.GetOrgRolesAsync(ct)).IsFinance())
        {
            return;
        }

        var allowed = false;
        if (c.ProjectManagerMayOverride)
        {
            if (c.Level is CeilingLevel.Contract or CeilingLevel.Clin)
            {
                var managed = await projects.ManagedContractIdsAsync(ct);
                allowed = c.ContractId is { } id && managed is not null && managed.Contains(id);
            }
            else if (c.ProjectId is { } p)
            {
                allowed = await projects.CanReadProjectFinancialsAsync(p, ct);
            }
        }

        if (!allowed)
        {
            throw new WorkbenchForbiddenException(c.ProjectManagerMayOverride
                ? "Only Finance, administrators and the manager of a project under this ceiling can grant or revoke its overrides."
                : "Only Finance and administrators can grant or revoke overrides on this ceiling.");
        }
    }
}

public sealed class PermissionCheckingCloseoutService(ICloseoutService inner, ProjectAccess projects) : ICloseoutService
{
    private const string Manage = "manage closeout";

    public async Task<CloseoutDto> GetAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        if (scope == CloseoutScope.Contract)
        {
            await projects.RequireContractFundingAsync(id, cancellationToken);
        }
        else
        {
            await projects.RequireProjectFinancialsAsync(id, "see this project's closeout", cancellationToken);
        }

        return await inner.GetAsync(scope, id, cancellationToken);
    }

    public async Task<CloseoutDto> MarkPhysicallyCompleteAsync(CloseoutScope scope, Guid id, PhysicallyCompleteRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.MarkPhysicallyCompleteAsync(scope, id, request, cancellationToken);
    }

    public async Task<CloseoutDto> StartClosingAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.StartClosingAsync(scope, id, cancellationToken);
    }

    public async Task<CloseoutDto> CloseAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.CloseAsync(scope, id, cancellationToken);
    }

    public async Task<CloseoutDto> ReopenAsync(CloseoutScope scope, Guid id, CloseoutReasonRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.ReopenAsync(scope, id, request, cancellationToken);
    }

    public async Task<CloseoutDto> SaveRecordsAsync(CloseoutScope scope, Guid id, SaveCloseoutRecordsRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.SaveRecordsAsync(scope, id, request, cancellationToken);
    }

    public async Task<CloseoutDto> WaiveAsync(CloseoutScope scope, Guid id, WaiveCloseoutItemRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.WaiveAsync(scope, id, request, cancellationToken);
    }

    public async Task<CloseoutDto> RemoveWaiverAsync(CloseoutScope scope, Guid id, CloseoutCheck check, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.RemoveWaiverAsync(scope, id, check, cancellationToken);
    }

    public async Task<CloseoutDto> MarkFinalInvoiceAsync(Guid contractId, MarkFinalInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.MarkFinalInvoiceAsync(contractId, request, cancellationToken);
    }

    public async Task<CloseoutDto> RecordDeobligationAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await projects.Access.RequireFinanceAsync(Manage, cancellationToken);
        return await inner.RecordDeobligationAsync(contractId, cancellationToken);
    }
}
