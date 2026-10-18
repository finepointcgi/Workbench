using System.Net.Mail;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Domain.Approvals;

namespace Workbench.Application.Approvals;

/// <summary>
/// Out-of-office delegation: between the dates the delegate may decide whatever waits on the delegator (the task
/// records it was on their behalf). Anyone delegates their own approvals; administrators may set one for someone else.
/// </summary>
public sealed class ApprovalDelegationService(IWorkbenchDbContextFactory dbFactory, ApprovalEngine engine, AccessControl access, AuditRecorder audit) : IApprovalDelegationService
{
    public async Task<IReadOnlyList<ApprovalDelegationDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = access.Email;
        var q = db.Set<ApprovalDelegation>().AsNoTracking();
        if (!(await access.GetOrgRolesAsync(cancellationToken)).IsAdmin())
        {
            q = q.Where(d => d.DelegatorEmail == me || d.DelegateEmail == me);
        }

        return (await q.ToListAsync(cancellationToken)).OrderByDescending(d => d.StartsOn).ThenByDescending(d => d.CreatedAtUtc).Select(ToDto).ToList();
    }

    public async Task<ApprovalDelegationDto> CreateAsync(SaveApprovalDelegationRequest request, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        ArgumentNullException.ThrowIfNull(request);
        var me = access.Email;
        var delegator = string.IsNullOrWhiteSpace(request.DelegatorEmail) ? me : ApprovalEngine.Normalize(request.DelegatorEmail);
        if (delegator != me && !(await access.GetOrgRolesAsync(cancellationToken)).IsAdmin())
        {
            throw new WorkbenchForbiddenException("Only administrators can delegate someone else's approvals.");
        }

        var to = ApprovalEngine.Normalize(request.DelegateEmail);
        if (to.Length == 0 || to.Length > 320 || !MailAddress.TryCreate(to, out _))
        {
            throw new ArgumentException("Enter the delegate's email.");
        }

        if (to == delegator)
        {
            throw new ArgumentException("You can't delegate to yourself.");
        }

        if (request.EndsOn < request.StartsOn)
        {
            throw new ArgumentException("The delegation must end on or after the day it starts.");
        }

        if (request.EndsOn.DayNumber - request.StartsOn.DayNumber > 366)
        {
            throw new ArgumentException("A delegation can last at most a year.");
        }

        if (request.EndsOn < engine.Today)
        {
            throw new ArgumentException("The delegation would already be over.");
        }

        if (request.DocumentType is { } type && !ApprovalDocumentTypes.All.Contains(type))
        {
            throw new ArgumentException($"Unknown document type \"{type}\".");
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason?.Length > 500)
        {
            throw new ArgumentException("The reason can be at most 500 characters.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var delegation = new ApprovalDelegation
        {
            DelegatorEmail = delegator,
            DelegateEmail = to,
            StartsOn = request.StartsOn,
            EndsOn = request.EndsOn,
            DocumentType = request.DocumentType,
            Reason = reason,
            CreatedBy = me,
            CreatedAtUtc = engine.UtcNow,
        };
        db.Set<ApprovalDelegation>().Add(delegation);
        audit.Record(db, nameof(ApprovalDelegation), delegation.Id, "Created", after: new { delegation.DelegatorEmail, delegation.DelegateEmail, delegation.StartsOn, delegation.EndsOn, delegation.DocumentType }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(delegation);
    }

    public async Task<ApprovalDelegationDto> RevokeAsync(Guid delegationId, CancellationToken cancellationToken = default)
    {
        RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var delegation = await db.Set<ApprovalDelegation>().FirstOrDefaultAsync(d => d.Id == delegationId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Delegation", delegationId);
        var me = access.Email;
        if (delegation.DelegatorEmail != me && delegation.CreatedBy != me && !(await access.GetOrgRolesAsync(cancellationToken)).IsAdmin())
        {
            throw new WorkbenchForbiddenException("Only the person who delegated (or an administrator) can end a delegation.");
        }

        if (delegation.RevokedAtUtc is null)
        {
            delegation.RevokedAtUtc = engine.UtcNow;
            audit.Record(db, nameof(ApprovalDelegation), delegation.Id, "Revoked");
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToDto(delegation);
    }

    private void RequireSignedIn()
    {
        if (!access.IsAuthenticated)
        {
            throw new UnauthorizedAccessException("Sign in to manage delegations.");
        }
    }

    private ApprovalDelegationDto ToDto(ApprovalDelegation d) => new(d.Id, d.DelegatorEmail, d.DelegateEmail, d.StartsOn, d.EndsOn, d.DocumentType, d.Reason,
        d.CreatedBy, d.CreatedAtUtc, d.RevokedAtUtc, d.IsActiveOn(engine.Today));
}
