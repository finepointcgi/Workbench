using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>
/// Who may do what in business development. Pipeline values are business-sensitive:
/// <list type="bullet">
/// <item>Business development, Finance and administrators see the whole pipeline (accounts, contacts, opportunities, forecast).</item>
/// <item>An opportunity's capture and proposal managers see and edit it (and read its accounts), with or without a role.</item>
/// <item>Administrators edit every opportunity; business development creates them and manages leads, accounts and gates.</item>
/// <item>Everyone else sees nothing.</item>
/// </list>
/// </summary>
public sealed class CrmAccess(AccessControl access, IWorkbenchDbContextFactory dbFactory, SecurityEventLog? securityLog = null)
{
    public string Email => access.Email;

    public bool IsSignedIn => access.IsAuthenticated;

    public Task<PermissionSet> RolesAsync(CancellationToken ct) => access.GetPermissionsAsync(ct);

    public void RequireSignedInOrThrow() => access.RequireSignedIn();

    public static bool IsManager(Opportunity opportunity, string email) =>
        email.Length > 0 && (opportunity.CaptureManagerEmail == email || opportunity.ProposalManagerEmail == email);

    public static bool CanEdit(Opportunity opportunity, PermissionSet roles, string email) => roles.Has(Permission.OpportunitiesEditAll) || IsManager(opportunity, email);

    public static bool CanRead(Opportunity opportunity, PermissionSet roles, string email) => roles.Has(Permission.PipelineView) || IsManager(opportunity, email);

    public async Task<bool> SeesPipelineAsync(CancellationToken ct) => access.IsAuthenticated && (await RolesAsync(ct)).Has(Permission.PipelineView);

    /// <summary>Capture or proposal manager of at least one opportunity.</summary>
    public async Task<bool> ManagesAnyAsync(CancellationToken ct)
    {
        if (!access.IsAuthenticated)
        {
            return false;
        }

        var email = Email;
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<Opportunity>().AnyAsync(o => o.CaptureManagerEmail == email || o.ProposalManagerEmail == email, ct);
    }

    /// <summary>Business development, Finance or administrators; or, with <paramref name="orManager"/>, a manager of some opportunity.</summary>
    public async Task RequirePipelineAsync(string what, CancellationToken ct, bool orManager = false)
    {
        access.RequireSignedIn();
        if (await SeesPipelineAsync(ct) || (orManager && await ManagesAnyAsync(ct)))
        {
            return;
        }

        throw await DeniedAsync($"Only business development, Finance and administrators can {what}.", ct);
    }

    /// <summary>The Business development role (or administrators).</summary>
    public async Task RequireBusinessDevelopmentAsync(string what, CancellationToken ct)
    {
        access.RequireSignedIn();
        if (!(await RolesAsync(ct)).Has(Permission.CaptureManage))
        {
            throw await DeniedAsync($"Only business development and administrators can {what}.", ct);
        }
    }

    /// <summary>Business development, Finance or administrators (changes to shared CRM data: accounts, contacts).</summary>
    public async Task RequireCrmEditorAsync(string what, CancellationToken ct)
    {
        access.RequireSignedIn();
        if (!(await RolesAsync(ct)).Has(Permission.PipelineView))
        {
            throw await DeniedAsync($"Only business development, Finance and administrators can {what}.", ct);
        }
    }

    public Task RequireAsync(string permission, string what, CancellationToken ct) => access.RequireAsync(permission, what, ct);

    public async Task<Opportunity> RequireReadAsync(Guid opportunityId, CancellationToken ct)
    {
        access.RequireSignedIn();
        var opportunity = await LoadAsync(opportunityId, ct);
        if (!CanRead(opportunity, await RolesAsync(ct), Email))
        {
            throw await DeniedAsync("Only business development, Finance, administrators and the opportunity's managers can see it.", ct, opportunity.Number);
        }

        return opportunity;
    }

    public async Task<Opportunity> RequireEditAsync(Guid opportunityId, CancellationToken ct)
    {
        access.RequireSignedIn();
        var opportunity = await LoadAsync(opportunityId, ct);
        if (!CanEdit(opportunity, await RolesAsync(ct), Email))
        {
            throw await DeniedAsync("Only the opportunity's capture and proposal managers (and administrators) can change it.", ct, opportunity.Number);
        }

        return opportunity;
    }

    private async Task<Opportunity> LoadAsync(Guid opportunityId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<Opportunity>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == opportunityId, ct)
            ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
    }

    public async Task<WorkbenchForbiddenException> DeniedAsync(string message, CancellationToken ct, string? target = null)
    {
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(SecurityEventTypes.PermissionDenied, target, SecurityOutcomes.Denied, message, cancellationToken: ct);
        }

        return new WorkbenchForbiddenException(message);
    }
}
