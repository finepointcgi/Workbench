using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// What an employee may requisition: WBS elements of projects they manage or have a charge assignment on, and account
/// lines for their own organization. Finance and administrators may requisition anything.
/// </summary>
public sealed class RequisitionAccess(IWorkbenchDbContextFactory dbFactory, AccessControl access)
{
    /// <summary>Projects the user may charge, or null for Finance and Admin (all of them).</summary>
    public async Task<IReadOnlySet<Guid>?> ProjectIdsAsync(CancellationToken ct)
    {
        if ((await access.GetOrgRolesAsync(ct)).IsFinance())
        {
            return null;
        }

        var me = await access.GetEmployeeIdAsync(ct);
        if (me is null)
        {
            return new HashSet<Guid>();
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var managed = await db.Set<Project>().AsNoTracking().Where(p => p.ProjectManagerId == me).Select(p => p.Id).ToListAsync(ct);
        var assigned = await db.Set<ChargeAssignment>().AsNoTracking().Where(a => a.EmployeeId == me)
            .Join(db.Set<WbsElement>(), a => a.WbsElementId, w => w.Id, (a, w) => w.ProjectId).ToListAsync(ct);
        return managed.Concat(assigned).ToHashSet();
    }

    /// <summary>The organizations the user may charge account lines to, or null for Finance and Admin (all of them).</summary>
    public async Task<IReadOnlySet<Guid>?> OrgIdsAsync(CancellationToken ct)
    {
        if ((await access.GetOrgRolesAsync(ct)).IsFinance())
        {
            return null;
        }

        var me = await access.GetEmployeeIdAsync(ct);
        await using var db = await dbFactory.CreateAsync(ct);
        var org = me is null ? null : await db.Set<Employee>().AsNoTracking().Where(e => e.Id == me).Select(e => e.OrgUnitId).FirstOrDefaultAsync(ct);
        return org is { } o ? new HashSet<Guid> { o } : new HashSet<Guid>();
    }

    /// <summary>Throws <see cref="WorkbenchForbiddenException"/> when a line charges a project or organization the user may not.</summary>
    public async Task RequireChargesAsync(SaveRequisitionRequest request, CancellationToken ct)
    {
        var projects = await ProjectIdsAsync(ct);
        if (projects is null)
        {
            return;
        }

        var orgs = await OrgIdsAsync(ct) ?? new HashSet<Guid>();
        if (request.OrgUnitId is { } reqOrg && !orgs.Contains(reqOrg))
        {
            throw new WorkbenchForbiddenException("You can only requisition for your own organization.");
        }

        var wbsIds = (request.Lines ?? []).Where(l => l.WbsElementId != null).Select(l => l.WbsElementId!.Value).Distinct().ToList();
        await using var db = await dbFactory.CreateAsync(ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        foreach (var line in request.Lines ?? [])
        {
            if (line.WbsElementId is { } wid && wbs.TryGetValue(wid, out var element) && !projects.Contains(element.ProjectId))
            {
                throw new WorkbenchForbiddenException($"You aren't assigned to the project of {element.Code}.");
            }

            if (line.AccountId is not null && line.OrgUnitId is { } org && !orgs.Contains(org))
            {
                throw new WorkbenchForbiddenException("Account lines can only be charged to your own organization.");
            }

            if (line.AccountId is not null && line.OrgUnitId is null && request.OrgUnitId is null && orgs.Count == 0)
            {
                throw new WorkbenchForbiddenException("You aren't in an organization, so you can only requisition for projects you're assigned to.");
            }
        }
    }
}

/// <summary>
/// Purchase requisitions: entry by any employee, routed by the requisition approval workflow (Setup › Approval
/// workflows). By default: the project managers of the projects charged (never the requester), then Finance when the
/// total is over <see cref="PurchasingOptions.RequisitionFinanceThreshold"/>, a line charges a GL account, or a project
/// has no other manager. Every change and decision is audited.
/// </summary>
public sealed class RequisitionService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    AccessControl access,
    RequisitionAccess requisitionAccess,
    INotificationPublisher notifications,
    IOptions<PurchasingOptions> options,
    TimeProvider clock,
    ApprovalEngine? approvals = null,
    Customization.DocumentNumbers? documentNumbers = null) : IRequisitionService
{
    private Customization.DocumentNumbers DocNumbers => documentNumbers ?? new(dbFactory, clock);

    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private string Me => access.Email;

    public async Task<IReadOnlyList<RequisitionSummaryDto>> ListAsync(RequisitionQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var finance = (await access.GetOrgRolesAsync(cancellationToken)).IsFinance();
        var me = Me;
        var q = db.Set<PurchaseRequisition>().AsNoTracking().Include(r => r.Lines).AsQueryable();
        if (query.Mine || !finance)
        {
            q = query.Mine
                ? q.Where(r => r.RequesterEmail == me)
                : q.Where(r => r.RequesterEmail == me || r.Lines.Any(l => l.ApproverEmail == me));
        }

        if (query.Status is { } status)
        {
            var s = status.ToDomain();
            q = q.Where(r => r.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(r => r.Number.Contains(term) || r.Justification.Contains(term) || r.Lines.Any(l => l.Description.Contains(term)));
        }

        var list = (await q.ToListAsync(cancellationToken)).OrderByDescending(r => r.CreatedAtUtc).Take(1000).ToList();
        return await SummariesAsync(db, list, cancellationToken);
    }

    public async Task<RequisitionDto> GetAsync(Guid requisitionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, requisitionId, cancellationToken);
    }

    public async Task<RequisitionOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        var projects = await requisitionAccess.ProjectIdsAsync(cancellationToken);
        var orgs = await requisitionAccess.OrgIdsAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var activeProjects = await db.Set<Project>().AsNoTracking().Where(p => p.Status == ProjectStatus.Active).ToDictionaryAsync(p => p.Id, cancellationToken);
        var elements = await db.Set<WbsElement>().AsNoTracking().ToListAsync(cancellationToken);
        var parents = elements.Where(w => w.ParentId != null).Select(w => w.ParentId!.Value).ToHashSet();
        var charges = elements
            .Where(w => w.IsOpen && w.IsChargeable && !parents.Contains(w.Id) && activeProjects.ContainsKey(w.ProjectId) && (projects is null || projects.Contains(w.ProjectId)))
            .Select(w => new RequisitionChargeOption(w.Id, w.ProjectId, activeProjects[w.ProjectId].Code, w.Code, w.Name))
            .OrderBy(c => c.WbsCode, StringComparer.Ordinal).ToList();
        var accounts = await db.Set<Account>().AsNoTracking().Where(a => a.IsActive && (a.Type == AccountType.Expense || a.Type == AccountType.Asset)).ToListAsync(cancellationToken);
        var accountParents = accounts.Where(a => a.ParentId != null).Select(a => a.ParentId!.Value).ToHashSet();
        var orgUnits = await db.Set<OrgUnit>().AsNoTracking().Where(o => orgs == null || orgs.Contains(o.Id)).OrderBy(o => o.Code).ToListAsync(cancellationToken);
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => v.Status == VendorStatus.Active).OrderBy(v => v.Name).ToListAsync(cancellationToken);
        var meId = await access.GetEmployeeIdAsync(cancellationToken);
        var myOrg = meId is null ? null : await db.Set<Employee>().AsNoTracking().Where(e => e.Id == meId).Select(e => e.OrgUnitId).FirstOrDefaultAsync(cancellationToken);
        return new RequisitionOptionsDto(
            charges,
            accounts.Where(a => !accountParents.Contains(a.Id) && a.CostClass != CostClassification.AppliedBurden).OrderBy(a => a.Number, StringComparer.Ordinal)
                .Select(a => new RequisitionAccountOption(a.Id, a.Number, a.Name)).ToList(),
            orgUnits.Select(o => new RequisitionOrgOption(o.Id, o.Code, o.Name)).ToList(),
            vendors.Select(v => new RequisitionVendorOption(v.Id, v.Code, v.Name)).ToList(),
            myOrg,
            options.Value.RequisitionFinanceThreshold);
    }

    public async Task<RequisitionDto> CreateAsync(SaveRequisitionRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var meId = await access.GetEmployeeIdAsync(cancellationToken);
        var req = new PurchaseRequisition
        {
            Number = await DocNumbers.NextAsync(Customization.DocumentNumberTypes.Requisition, cancellationToken: cancellationToken),
            RequesterEmail = Me,
            RequesterEmployeeId = meId,
            Justification = "",
            CreatedAtUtc = UtcNow,
        };
        await ApplyAsync(db, req, request, meId, cancellationToken);
        db.Set<PurchaseRequisition>().Add(req);
        audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, "Created", after: Snapshot(req));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, req.Id, cancellationToken);
    }

    public async Task<RequisitionDto> UpdateAsync(Guid requisitionId, SaveRequisitionRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var req = await LoadAsync(db, requisitionId, cancellationToken);
        EnsureEditable(req);
        var before = Snapshot(req);
        db.Set<PurchaseRequisitionLine>().RemoveRange(req.Lines);
        req.Lines.Clear();
        await ApplyAsync(db, req, request, req.RequesterEmployeeId, cancellationToken);
        db.Set<PurchaseRequisitionLine>().AddRange(req.Lines);
        audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, "Updated", before, Snapshot(req));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, req.Id, cancellationToken);
    }

    public async Task<RequisitionDto> SubmitAsync(Guid requisitionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var req = await LoadAsync(db, requisitionId, cancellationToken);
        EnsureEditable(req);
        if (req.Lines.Count == 0)
        {
            throw new ArgumentException("Add at least one line before submitting.");
        }

        // Re-check the charging: WBS elements and accounts may have closed since the draft was saved.
        foreach (var line in req.Lines)
        {
            await ProcurementSupport.ValidateChargeAsync(db, $"Line {line.LineNumber}", line.WbsElementId, line.OdcType, line.AccountId, line.OrgUnitId, req.OrgUnitId, cancellationToken);
        }

        foreach (var line in req.Lines)
        {
            line.ApprovedBy = null;
            line.ApprovedAtUtc = null;
        }

        var route = await _approvals.StartAsync(db, Subject(req), cancellationToken);
        foreach (var line in req.Lines)
        {
            // A line goes to its project's manager, or (no manager, the requester's own project, an account) to Finance.
            line.ApproverEmail = line.ProjectId is { } pid ? route.ProjectApprover(pid) : null;
        }

        req.NeedsFinance = route.Tasks.Any(t => t.Status != Domain.Approvals.ApprovalTaskStatus.Skipped && !IsProjectTask(t));
        req.Stage = StageOf(route.Active);
        req.Status = RequisitionStatus.PendingApproval;
        req.SubmittedAtUtc = UtcNow;
        req.RejectedBy = null;
        req.RejectionReason = null;
        audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, "Submitted", after: new
        {
            req.Total,
            Stage = req.Stage.ToString(),
            req.NeedsFinance,
            Routes = req.Lines.Select(l => l.ApproverEmail ?? PurchasingSupport.FinanceRoute).Distinct(),
        });
        await db.SaveChangesAsync(cancellationToken);
        await NotifyStageAsync(req, route.NotifyEmails);
        return await DetailAsync(db, req.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<RequisitionSummaryDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pending = await db.Set<PurchaseRequisition>().AsNoTracking().Include(r => r.Lines)
            .Where(r => r.Status == RequisitionStatus.PendingApproval).ToListAsync(cancellationToken);
        var mine = new List<PurchaseRequisition>();
        foreach (var req in pending.OrderBy(r => r.SubmittedAtUtc))
        {
            if (await CanDecideAsync(db, req, cancellationToken))
            {
                mine.Add(req);
            }
        }

        return await SummariesAsync(db, mine, cancellationToken);
    }

    public async Task<RequisitionDto> ApproveAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var comment = PurchasingSupport.Optional(request?.Comment, "The comment", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var req = await LoadPendingAsync(db, requisitionId, cancellationToken);
        var stage = req.Stage!.Value;
        var check = await RequireDecidableAsync(db, req, cancellationToken);
        var outcome = await _approvals.DecideAsync(db, check, ApprovalDecisionKind.Approve, Me, comment, cancellationToken);
        var lines = LinesOf(req, outcome.Decided);
        foreach (var line in lines)
        {
            line.ApprovedBy = Me;
            line.ApprovedAtUtc = UtcNow;
        }

        if (stage == RequisitionStage.ProjectManager)
        {
            foreach (var projectId in lines.Select(l => l.ProjectId).Distinct())
            {
                db.Set<RequisitionApproval>().Add(new RequisitionApproval
                {
                    RequisitionId = req.Id, Stage = stage, Decision = "Approved", DecidedBy = Me, DecidedAtUtc = UtcNow, ProjectId = projectId, Comment = comment,
                });
            }
        }
        else
        {
            db.Set<RequisitionApproval>().Add(new RequisitionApproval
            {
                RequisitionId = req.Id, Stage = stage, Decision = "Approved", DecidedBy = Me, DecidedAtUtc = UtcNow, Comment = comment,
            });
        }

        if (outcome.IsComplete)
        {
            foreach (var line in req.Lines.Where(l => l.ApprovedBy is null))
            {
                line.ApprovedBy = Me;
                line.ApprovedAtUtc = UtcNow;
            }

            Approve(req);
        }
        else if (outcome.Activated.Count > 0)
        {
            req.Stage = StageOf(outcome.Activated);
        }

        audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, req.Status == RequisitionStatus.Approved ? "Approved" : "StageApproved",
            after: new { Stage = stage.ToString(), Lines = lines.Select(l => l.LineNumber), Next = req.Stage?.ToString() }, reason: comment);
        await db.SaveChangesAsync(cancellationToken);

        if (req.Status == RequisitionStatus.Approved)
        {
            await notifications.PublishAsync(req.RequesterEmail, "purchasing.requisition_approved", $"Requisition {req.Number} was approved",
                $"{req.Total:C}: {req.Justification}", link: $"purchasing/requisitions/{req.Id}");
            await notifications.PublishAsync((await FinanceUsersAsync(db, cancellationToken)).Where(e => !PurchasingSupport.SameEmail(e, Me)),
                "purchasing.requisition_to_order", $"Requisition {req.Number} is ready to order", $"{req.Total:C}", link: "purchasing/orders");
        }
        else if (outcome.Activated.Count > 0)
        {
            await NotifyStageAsync(req, outcome.NotifyEmails);
        }

        return await DetailAsync(db, req.Id, cancellationToken);
    }

    public async Task<RequisitionDto> RejectAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Comment, "A reason", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var req = await LoadPendingAsync(db, requisitionId, cancellationToken);
        var check = await RequireDecidableAsync(db, req, cancellationToken);
        var outcome = await _approvals.DecideAsync(db, check, ApprovalDecisionKind.Reject, Me, reason, cancellationToken);
        db.Set<RequisitionApproval>().Add(new RequisitionApproval
        {
            RequisitionId = req.Id,
            Stage = req.Stage!.Value,
            Decision = "Rejected",
            DecidedBy = Me,
            DecidedAtUtc = UtcNow,
            ProjectId = req.Stage == RequisitionStage.ProjectManager ? LinesOf(req, outcome.Decided).Select(l => l.ProjectId).FirstOrDefault() : null,
            Comment = reason,
        });
        req.Status = RequisitionStatus.Rejected;
        req.Stage = null;
        req.RejectedBy = Me;
        req.RejectionReason = reason;
        audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, "Rejected", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        await notifications.PublishAsync(req.RequesterEmail, "purchasing.requisition_rejected", $"{Me} rejected requisition {req.Number}", reason,
            link: $"purchasing/requisitions/{req.Id}");
        return await DetailAsync(db, req.Id, cancellationToken);
    }

    public async Task<RequisitionDto> CancelAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Optional(request?.Comment, "The reason", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var req = await LoadAsync(db, requisitionId, cancellationToken);
        var finance = (await access.GetOrgRolesAsync(cancellationToken)).IsFinance();
        if (!finance && !PurchasingSupport.SameEmail(req.RequesterEmail, Me))
        {
            throw new WorkbenchForbiddenException("Only the requester or Finance can cancel a requisition.");
        }

        if (req.Status is RequisitionStatus.Ordered or RequisitionStatus.Cancelled || req.Lines.Any(l => l.PurchaseOrderLineId != null))
        {
            throw new InvalidOperationException($"Requisition {req.Number} is {req.Status}{(req.Lines.Any(l => l.PurchaseOrderLineId != null) ? " and partly ordered" : "")}; it can't be cancelled.");
        }

        req.Status = RequisitionStatus.Cancelled;
        req.Stage = null;
        await _approvals.CancelAsync(db, ApprovalDocumentTypes.Requisition, req.Id, Me, reason ?? "Cancelled", cancellationToken);
        audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, "Cancelled", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, req.Id, cancellationToken);
    }

    // ---- Rules ----

    private void Approve(PurchaseRequisition req)
    {
        req.Status = RequisitionStatus.Approved;
        req.Stage = null;
        req.ApprovedAtUtc = UtcNow;
    }

    private ApprovalSubject Subject(PurchaseRequisition req) => ApprovalSubjects.Requisition(req, options.Value.RequisitionFinanceThreshold);

    private static bool IsProjectTask(Domain.Approvals.ApprovalTask t) => t.ScopeKey?.StartsWith("project:", StringComparison.Ordinal) == true;

    /// <summary>The module's stage for the active tasks: project managers while any project approval is pending, else Finance.</summary>
    private static RequisitionStage StageOf(IEnumerable<Domain.Approvals.ApprovalTask> active) =>
        active.Any(IsProjectTask) ? RequisitionStage.ProjectManager : RequisitionStage.Finance;

    /// <summary>The lines a set of decided tasks covers: a project task its project's lines routed to a PM.</summary>
    private static List<PurchaseRequisitionLine> LinesOf(PurchaseRequisition req, IEnumerable<Domain.Approvals.ApprovalTask> tasks)
    {
        var projects = tasks.Where(IsProjectTask).Select(t => t.ScopeKey!).ToHashSet();
        return req.Lines.Where(l => l.ApproverEmail != null && l.ApprovedBy == null && l.ProjectId is { } p && projects.Contains(ApprovalEngine.ProjectScope(p))).ToList();
    }

    /// <summary>Whether the current user may decide the requisition now (never their own).</summary>
    private async Task<bool> CanDecideAsync(IWorkbenchDbContext db, PurchaseRequisition req, CancellationToken ct) =>
        req.Status == RequisitionStatus.PendingApproval && !PurchasingSupport.SameEmail(req.RequesterEmail, Me)
        && await _approvals.CanDecideAsync(db, ApprovalDocumentTypes.Requisition, req.Id, Me, _ => Task.FromResult(Subject(req)), ct);

    private async Task<ApprovalCheck> RequireDecidableAsync(IWorkbenchDbContext db, PurchaseRequisition req, CancellationToken ct)
    {
        if (PurchasingSupport.SameEmail(req.RequesterEmail, Me))
        {
            throw new WorkbenchForbiddenException("You can't approve or reject your own requisition.");
        }

        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.Requisition, req.Id, Me, _ => Task.FromResult(Subject(req)), ct);
        return check.CanDecide
            ? check
            : throw new WorkbenchForbiddenException(req.Stage == RequisitionStage.Finance
                ? "This requisition is waiting on Finance."
                : "None of this requisition's lines are waiting on you.");
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, PurchaseRequisition req, SaveRequisitionRequest r, Guid? requesterId, CancellationToken ct)
    {
        req.Justification = PurchasingSupport.Required(r?.Justification, "A justification", 2000);
        if (r!.Lines is null || r.Lines.Count == 0)
        {
            throw new ArgumentException("A requisition needs at least one line.");
        }

        if (r.Lines.Count > 100)
        {
            throw new ArgumentException("A requisition can have at most 100 lines.");
        }

        var defaultOrg = r.OrgUnitId;
        if (defaultOrg is null && requesterId is { } rid)
        {
            defaultOrg = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == rid).Select(e => e.OrgUnitId).FirstOrDefaultAsync(ct);
        }

        if (defaultOrg is { } org && !await db.Set<OrgUnit>().AnyAsync(o => o.Id == org, ct))
        {
            throw WorkbenchNotFoundException.For("Organization", org);
        }

        req.OrgUnitId = defaultOrg;
        foreach (var input in r.Lines)
        {
            var n = req.Lines.Count + 1;
            var prefix = $"Line {n}";
            var charge = await ProcurementSupport.ValidateChargeAsync(db, prefix, input.WbsElementId, input.OdcType?.ToDomain(), input.AccountId, input.OrgUnitId, defaultOrg, ct);
            if (input.SuggestedVendorId is { } vid && !await db.Set<Vendor>().AnyAsync(v => v.Id == vid, ct))
            {
                throw WorkbenchNotFoundException.For("Vendor", vid);
            }

            var quantity = ProcurementSupport.Quantity(input.Quantity, $"{prefix}: quantity");
            var unit = ProcurementSupport.UnitPrice(input.UnitCost, $"{prefix}: unit cost");
            req.Lines.Add(new PurchaseRequisitionLine
            {
                RequisitionId = req.Id,
                LineNumber = n,
                Description = PurchasingSupport.Required(input.Description, $"{prefix}: description", 500),
                Quantity = quantity,
                UnitCost = unit,
                Amount = ProcurementSupport.Extend(quantity, unit),
                ProjectId = charge.ProjectId,
                WbsElementId = charge.WbsElementId,
                OdcType = charge.OdcType,
                AccountId = charge.AccountId,
                OrgUnitId = charge.OrgUnitId,
                NeededBy = input.NeededBy,
                SuggestedVendorId = input.SuggestedVendorId,
            });
        }

        req.Total = req.Lines.Sum(l => l.Amount);
    }

    private void EnsureEditable(PurchaseRequisition req)
    {
        if (req.Status is not (RequisitionStatus.Draft or RequisitionStatus.Rejected))
        {
            throw new InvalidOperationException($"Requisition {req.Number} is {req.Status} and can't be changed.");
        }

        if (!PurchasingSupport.SameEmail(req.RequesterEmail, Me))
        {
            throw new WorkbenchForbiddenException("Only the requester can change a requisition.");
        }
    }

    private async Task NotifyStageAsync(PurchaseRequisition req, IReadOnlyList<string> to) =>
        await notifications.PublishAsync(to.Where(e => !PurchasingSupport.SameEmail(e, req.RequesterEmail) && !PurchasingSupport.SameEmail(e, Me)),
            "purchasing.requisition_approval", $"{req.RequesterEmail} sent requisition {req.Number} for your approval",
            $"{req.Total:C}: {req.Justification}", link: $"purchasing/requisitions/{req.Id}");

    private static async Task<IReadOnlyList<string>> FinanceUsersAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        await db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive && (u.IsFinance || u.IsAdmin)).Select(u => u.Email).ToListAsync(ct);

    private static async Task<PurchaseRequisition> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<PurchaseRequisition>().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Requisition", id);

    private static async Task<PurchaseRequisition> LoadPendingAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var req = await LoadAsync(db, id, ct);
        return req.Status == RequisitionStatus.PendingApproval && req.Stage is not null
            ? req
            : throw new InvalidOperationException($"Requisition {req.Number} is {req.Status}, not waiting for approval.");
    }

    private static object Snapshot(PurchaseRequisition r) => new
    {
        r.Number,
        r.Justification,
        r.OrgUnitId,
        r.Total,
        Lines = r.Lines.Select(l => new { l.LineNumber, l.Description, l.Quantity, l.UnitCost, l.Amount, l.WbsElementId, l.AccountId, l.OrgUnitId, l.SuggestedVendorId }),
    };

    // ---- DTOs ----

    private static async Task<IReadOnlyList<RequisitionSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<PurchaseRequisition> list, CancellationToken ct)
    {
        var emails = list.Select(r => r.RequesterEmail).Distinct().ToList();
        var names = await db.Set<Employee>().AsNoTracking().Where(e => emails.Contains(e.Email)).ToDictionaryAsync(e => e.Email, e => e.DisplayName, ct);
        return list.Select(r => Summary(r, names.GetValueOrDefault(r.RequesterEmail))).ToList();
    }

    private static RequisitionSummaryDto Summary(PurchaseRequisition r, string? name) => new(
        r.Id, r.Number, r.RequesterEmail, name, r.Justification, r.Status.ToContract(), r.Stage?.ToContract(), r.Total, r.CreatedAtUtc, r.SubmittedAtUtc, r.Lines.Count);

    private async Task<RequisitionDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var r = await db.Set<PurchaseRequisition>().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Requisition", id);
        var lookups = await ProcurementSupport.Lookups.LoadAsync(db,
            r.Lines.Select(l => l.ProjectId), r.Lines.Select(l => l.WbsElementId), r.Lines.Select(l => l.AccountId),
            r.Lines.Select(l => l.OrgUnitId).Append(r.OrgUnitId), r.Lines.Select(l => l.SuggestedVendorId), ct);
        var poLineIds = r.Lines.Where(l => l.PurchaseOrderLineId != null).Select(l => l.PurchaseOrderLineId!.Value).ToList();
        var poNumbers = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => poLineIds.Contains(l.Id))
            .Join(db.Set<PurchaseOrder>(), l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l.Id, o.Number }).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        var history = await db.Set<RequisitionApproval>().AsNoTracking().Where(a => a.RequisitionId == id).OrderBy(a => a.DecidedAtUtc).ToListAsync(ct);
        var historyProjects = await ProcurementSupport.Lookups.LoadAsync(db, history.Select(h => h.ProjectId), [], [], [], [], ct);
        var name = await db.Set<Employee>().AsNoTracking().Where(e => e.Email == r.RequesterEmail).Select(e => e.DisplayName).FirstOrDefaultAsync(ct);
        var canDecide = access.IsAuthenticated && await CanDecideAsync(db, r, ct);
        return new RequisitionDto(
            Summary(r, name),
            r.OrgUnitId,
            lookups.Org(r.OrgUnitId),
            r.NeedsFinance,
            r.RejectedBy,
            r.RejectionReason,
            r.Lines.OrderBy(l => l.LineNumber).Select(l => LineDto(l, lookups, poNumbers)).ToList(),
            history.Select(h => new RequisitionApprovalDto(h.Stage.ToContract(), h.Decision, h.DecidedBy, h.DecidedAtUtc, historyProjects.Project(h.ProjectId), h.Comment)).ToList(),
            r.Status is RequisitionStatus.Draft or RequisitionStatus.Rejected && PurchasingSupport.SameEmail(r.RequesterEmail, Me),
            canDecide);
    }

    internal static RequisitionLineDto LineDto(PurchaseRequisitionLine l, ProcurementSupport.Lookups lookups, IReadOnlyDictionary<Guid, string> poNumbers) => new(
        l.Id, l.LineNumber, l.Description, l.Quantity, l.UnitCost, l.Amount,
        l.ProjectId, lookups.Project(l.ProjectId), l.WbsElementId, lookups.WbsCode(l.WbsElementId), l.OdcType?.ToContract(),
        l.AccountId, lookups.Account(l.AccountId), l.OrgUnitId, lookups.Org(l.OrgUnitId),
        l.NeededBy, l.SuggestedVendorId, lookups.VendorName(l.SuggestedVendorId),
        l.ApproverEmail ?? PurchasingSupport.FinanceRoute, l.ApprovedBy,
        l.PurchaseOrderLineId, l.PurchaseOrderLineId is { } p ? poNumbers.GetValueOrDefault(p) : null);
}
