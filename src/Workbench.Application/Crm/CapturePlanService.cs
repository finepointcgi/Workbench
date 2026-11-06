using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Crm;
using Workbench.Domain.Identity;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>
/// Capture plans, pursuit schedules and Pwin assessments. Who may read or change an opportunity's plan is checked by
/// <c>PermissionCheckingCapturePlanService</c> (the opportunity's own read and edit rules).
/// </summary>
public sealed class CapturePlanService(IWorkbenchDbContextFactory dbFactory, CrmAccess access, AuditRecorder audit, TimeProvider clock) : ICapturePlanService
{
    private const int MaxItemsPerKind = 50;

    /// <summary>The standard color-team schedule: days before the proposal is due.</summary>
    public static readonly IReadOnlyList<(D.MilestoneKind Kind, int DaysBefore)> StandardSchedule =
    [
        (D.MilestoneKind.BlackHat, 45),
        (D.MilestoneKind.BlueTeam, 35),
        (D.MilestoneKind.Kickoff, 28),
        (D.MilestoneKind.PinkTeam, 21),
        (D.MilestoneKind.RedTeam, 10),
        (D.MilestoneKind.GoldTeam, 4),
        (D.MilestoneKind.WhiteGlove, 2),
        (D.MilestoneKind.Submission, 0),
    ];

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<CapturePlanDto> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await db.Set<D.Opportunity>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == opportunityId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        var items = await db.Set<D.OpportunityPlanItem>().AsNoTracking().Where(i => i.OpportunityId == opportunityId)
            .ToListAsync(cancellationToken);
        // Kinds are stored as text, so the database would sort them alphabetically (and differently per provider): sort by the enum here.
        items = items.OrderBy(i => i.Kind).ThenBy(i => i.Order).ToList();
        var milestones = await MilestonesAsync(db, opportunityId, cancellationToken);
        var assessment = await db.Set<D.OpportunityPwinAssessment>().AsNoTracking().FirstOrDefaultAsync(a => a.OpportunityId == opportunityId, cancellationToken);
        var roles = await access.RolesAsync(cancellationToken);
        return new CapturePlanDto(
            opportunityId,
            items.Select(ToDto).ToList(),
            milestones,
            assessment is null ? null : ToDto(assessment),
            await HistoryAsync(db, opportunity, cancellationToken),
            CrmAccess.CanEdit(opportunity, roles, access.Email));
    }

    public async Task<PlanItemDto> AddItemAsync(Guid opportunityId, SavePlanItemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await RequireOpportunityAsync(db, opportunityId, cancellationToken);
        var kind = (D.CapturePlanItemKind)(int)request.Kind;
        var siblings = await db.Set<D.OpportunityPlanItem>().Where(i => i.OpportunityId == opportunityId && i.Kind == kind).Select(i => i.Order).ToListAsync(cancellationToken);
        if (siblings.Count >= MaxItemsPerKind)
        {
            throw new ArgumentException($"A capture plan holds at most {MaxItemsPerKind} entries of each kind.");
        }

        var item = new D.OpportunityPlanItem
        {
            OpportunityId = opportunityId,
            Kind = kind,
            Text = CrmMapping.Required(request.Text, 500, "Text"),
            Detail = CrmMapping.Clean(request.Detail, 2000, "Detail"),
            Order = siblings.Count == 0 ? 0 : siblings.Max() + 1,
            CreatedBy = access.Email,
            CreatedAtUtc = UtcNow,
        };
        db.Set<D.OpportunityPlanItem>().Add(item);
        audit.Record(db, OpportunityService.AuditEntityType, opportunityId, "Capture plan added", after: new { item.Kind, item.Text });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<PlanItemDto> UpdateItemAsync(Guid itemId, SavePlanItemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = await db.Set<D.OpportunityPlanItem>().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Capture plan entry", itemId);
        var before = new { item.Kind, item.Text, item.Detail };
        item.Kind = (D.CapturePlanItemKind)(int)request.Kind;
        item.Text = CrmMapping.Required(request.Text, 500, "Text");
        item.Detail = CrmMapping.Clean(request.Detail, 2000, "Detail");
        audit.Record(db, OpportunityService.AuditEntityType, item.OpportunityId, "Capture plan changed", before, new { item.Kind, item.Text, item.Detail });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = await db.Set<D.OpportunityPlanItem>().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Capture plan entry", itemId);
        db.Set<D.OpportunityPlanItem>().Remove(item);
        audit.Record(db, OpportunityService.AuditEntityType, item.OpportunityId, "Capture plan removed", new { item.Kind, item.Text });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<MilestoneDto> AddMilestoneAsync(Guid opportunityId, SaveMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await RequireOpportunityAsync(db, opportunityId, cancellationToken);
        var milestone = new D.OpportunityMilestone { OpportunityId = opportunityId, Name = "", CreatedBy = access.Email, CreatedAtUtc = UtcNow };
        Apply(milestone, request);
        db.Set<D.OpportunityMilestone>().Add(milestone);
        audit.Record(db, OpportunityService.AuditEntityType, opportunityId, "Milestone added", after: new { milestone.Name, milestone.DueOn });
        await db.SaveChangesAsync(cancellationToken);
        return (await MilestonesAsync(db, opportunityId, cancellationToken)).First(m => m.Id == milestone.Id);
    }

    public async Task<MilestoneDto> UpdateMilestoneAsync(Guid milestoneId, SaveMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadMilestoneAsync(db, milestoneId, cancellationToken);
        var before = new { milestone.Name, milestone.DueOn, milestone.OwnerEmail };
        Apply(milestone, request);
        audit.Record(db, OpportunityService.AuditEntityType, milestone.OpportunityId, "Milestone changed", before, new { milestone.Name, milestone.DueOn, milestone.OwnerEmail });
        await db.SaveChangesAsync(cancellationToken);
        return (await MilestonesAsync(db, milestone.OpportunityId, cancellationToken)).First(m => m.Id == milestone.Id);
    }

    public async Task<MilestoneDto> SetMilestoneDoneAsync(Guid milestoneId, bool done, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadMilestoneAsync(db, milestoneId, cancellationToken);
        milestone.IsDone = done;
        milestone.CompletedAtUtc = done ? UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
        return (await MilestonesAsync(db, milestone.OpportunityId, cancellationToken)).First(m => m.Id == milestone.Id);
    }

    public async Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadMilestoneAsync(db, milestoneId, cancellationToken);
        db.Set<D.OpportunityMilestone>().Remove(milestone);
        audit.Record(db, OpportunityService.AuditEntityType, milestone.OpportunityId, "Milestone removed", new { milestone.Name, milestone.DueOn });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<MilestoneDto>> AddStandardScheduleAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await RequireOpportunityAsync(db, opportunityId, cancellationToken);
        if (opportunity.ProposalDueOn is not { } due)
        {
            throw new ArgumentException("Set the proposal due date first: the schedule counts back from it.");
        }

        var today = DateOnly.FromDateTime(UtcNow);
        var existing = (await db.Set<D.OpportunityMilestone>().Where(m => m.OpportunityId == opportunityId).Select(m => m.Kind).ToListAsync(cancellationToken)).ToHashSet();
        var added = 0;
        foreach (var (kind, daysBefore) in StandardSchedule)
        {
            var on = Workday(due.AddDays(-daysBefore));
            if (existing.Contains(kind) || (on < today && kind != D.MilestoneKind.Submission))
            {
                continue;
            }

            db.Set<D.OpportunityMilestone>().Add(new D.OpportunityMilestone
            {
                OpportunityId = opportunityId,
                Kind = kind,
                Name = CapturePlanLabels.Label((MilestoneKind)(int)kind),
                DueOn = kind == D.MilestoneKind.Submission ? due : on,
                OwnerEmail = kind is D.MilestoneKind.BlackHat or D.MilestoneKind.BlueTeam ? opportunity.CaptureManagerEmail : opportunity.ProposalManagerEmail ?? opportunity.CaptureManagerEmail,
                CreatedBy = access.Email,
                CreatedAtUtc = UtcNow,
            });
            added++;
        }

        if (added > 0)
        {
            audit.Record(db, OpportunityService.AuditEntityType, opportunityId, "Standard schedule added", after: new { Milestones = added, ProposalDue = due });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await MilestonesAsync(db, opportunityId, cancellationToken);
    }

    /// <summary>Reviews land on a weekday: a Saturday moves to Friday, a Sunday to Monday.</summary>
    public static DateOnly Workday(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date,
    };

    public async Task<PwinAssessmentDto> SaveAssessmentAsync(Guid opportunityId, SavePwinAssessmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var f = request.Factors ?? throw new ArgumentException("Score the factors.");
        if (new[] { f.CustomerRelationship, f.SolutionFit, f.PastPerformance, f.PriceToWin, f.CompetitivePosition, f.Teaming }.Any(v => v is < 0 or > 5))
        {
            throw new ArgumentException("Each factor is scored from 0 to 5.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await db.Set<D.Opportunity>().FirstOrDefaultAsync(o => o.Id == opportunityId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        var assessment = await db.Set<D.OpportunityPwinAssessment>().FirstOrDefaultAsync(a => a.OpportunityId == opportunityId, cancellationToken);
        if (assessment is null)
        {
            assessment = new D.OpportunityPwinAssessment { OpportunityId = opportunityId, AssessedBy = access.Email };
            db.Set<D.OpportunityPwinAssessment>().Add(assessment);
        }

        assessment.CustomerRelationship = f.CustomerRelationship;
        assessment.SolutionFit = f.SolutionFit;
        assessment.PastPerformance = f.PastPerformance;
        assessment.PriceToWin = f.PriceToWin;
        assessment.CompetitivePosition = f.CompetitivePosition;
        assessment.Teaming = f.Teaming;
        assessment.Notes = CrmMapping.Clean(request.Notes, 2000, "Notes");
        assessment.AssessedBy = access.Email;
        assessment.AssessedAtUtc = UtcNow;
        if (request.ApplyToOpportunity && opportunity.Pwin != f.SuggestedPwin)
        {
            audit.Record(db, OpportunityService.AuditEntityType, opportunityId, "Pwin assessed", new { opportunity.Pwin }, new { Pwin = f.SuggestedPwin, f.Score });
            opportunity.Pwin = f.SuggestedPwin;
            opportunity.UpdatedAtUtc = UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(assessment);
    }

    private static void Apply(D.OpportunityMilestone milestone, SaveMilestoneRequest request)
    {
        milestone.Kind = (D.MilestoneKind)(int)request.Kind;
        milestone.Name = CrmMapping.Clean(request.Name, 200, "Name") ?? CapturePlanLabels.Label(request.Kind);
        if (request.DueOn == default)
        {
            throw new ArgumentException("Give the milestone a date.");
        }

        milestone.DueOn = request.DueOn;
        milestone.OwnerEmail = CrmMapping.Email(request.OwnerEmail);
        milestone.Notes = CrmMapping.Clean(request.Notes, 1000, "Notes");
    }

    private static async Task<D.Opportunity> RequireOpportunityAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<D.Opportunity>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Opportunity", id);

    private static async Task<D.OpportunityMilestone> LoadMilestoneAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<D.OpportunityMilestone>().FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Milestone", id);

    private static async Task<List<MilestoneDto>> MilestonesAsync(IWorkbenchDbContext db, Guid opportunityId, CancellationToken ct)
    {
        var rows = await db.Set<D.OpportunityMilestone>().AsNoTracking().Where(m => m.OpportunityId == opportunityId).ToListAsync(ct);
        var emails = rows.Select(m => m.OwnerEmail).OfType<string>().Distinct().ToList();
        var names = await db.Set<AppUser>().AsNoTracking().Where(u => emails.Contains(u.Email)).ToDictionaryAsync(u => u.Email, u => u.DisplayName, ct);
        return rows.OrderBy(m => m.DueOn).ThenBy(m => m.Kind)
            .Select(m => new MilestoneDto(m.Id, m.OpportunityId, (MilestoneKind)(int)m.Kind, m.Name, m.DueOn, m.OwnerEmail,
                m.OwnerEmail is { } e ? names.GetValueOrDefault(e) ?? e : null, m.Notes, m.IsDone))
            .ToList();
    }

    /// <summary>Won and lost pursuits with the same agency (the agency and its offices), when the opportunity has one.</summary>
    private static async Task<WinHistoryDto?> HistoryAsync(IWorkbenchDbContext db, D.Opportunity opportunity, CancellationToken ct)
    {
        if (opportunity.AgencyAccountId is not { } agencyId)
        {
            return null;
        }

        // Count from the top-level agency, so a new office of a familiar department still shows our record there.
        var top = agencyId;
        for (var hops = 0; hops < 5; hops++)
        {
            var parent = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => a.Id == top).Select(a => a.ParentId).FirstOrDefaultAsync(ct);
            if (parent is not { } p)
            {
                break;
            }

            top = p;
        }

        var accounts = await CrmAccountService.WithDescendantsAsync(db, top, ct);
        var outcomes = await db.Set<D.Opportunity>().AsNoTracking()
            .Where(o => o.AgencyAccountId != null && accounts.Contains(o.AgencyAccountId.Value) && o.Id != opportunity.Id
                        && (o.Stage == D.OpportunityStage.Won || o.Stage == D.OpportunityStage.Lost))
            .Select(o => o.Stage).ToListAsync(ct);
        var name = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => a.Id == top).Select(a => a.Acronym ?? a.Name).FirstAsync(ct);
        return new WinHistoryDto(name, outcomes.Count(s => s == D.OpportunityStage.Won), outcomes.Count(s => s == D.OpportunityStage.Lost));
    }

    private static PlanItemDto ToDto(D.OpportunityPlanItem i) =>
        new(i.Id, (CapturePlanItemKind)(int)i.Kind, i.Text, i.Detail, i.Order, i.CreatedBy, i.CreatedAtUtc);

    private static PwinAssessmentDto ToDto(D.OpportunityPwinAssessment a)
    {
        var factors = new PwinFactors(a.CustomerRelationship, a.SolutionFit, a.PastPerformance, a.PriceToWin, a.CompetitivePosition, a.Teaming);
        return new PwinAssessmentDto(factors, factors.Score, factors.SuggestedPwin, a.Notes, a.AssessedBy, a.AssessedAtUtc);
    }
}
