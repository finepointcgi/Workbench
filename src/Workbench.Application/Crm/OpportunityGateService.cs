using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Identity;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>The gates every organization starts with (created the first time gates are used): Shipley-style pursuit and bid decisions.</summary>
public static class GateDefaults
{
    public static IReadOnlyList<D.GateDefinition> Create(DateTime now) =>
    [
        Gate("Qualification", D.OpportunityStage.Qualified, 50, false, now, "Is this real, and is it for us?",
            ("The requirement is real, funded or likely to be funded", 3), ("It fits our capabilities and NAICS codes", 3), ("We can meet the set-aside and size standard", 2)),
        Gate("Pursuit decision (gate 1)", D.OpportunityStage.Capture, 60, true, now, "Invest capture effort and open a B&P charge code?",
            ("We know the customer and the decision makers", 3), ("Our capabilities and past performance are relevant", 3), ("Our competitive position is credible", 2),
            ("Funding and timing are identified", 2), ("Capture resources are available", 1)),
        Gate("Bid decision (gate 2)", D.OpportunityStage.Proposal, 70, true, now, "Write a proposal?",
            ("Win strategy and discriminators are clear", 3), ("We can price to win at an acceptable margin", 3), ("Key personnel and staffing are identified", 2),
            ("Teaming agreements are in place", 2), ("The B&P budget and proposal team are approved", 2)),
    ];

    /// <summary>Adds the default gates when the organization has none yet (tracked and saved here).</summary>
    public static async Task EnsureAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        if (await db.Set<D.GateDefinition>().AnyAsync(ct))
        {
            return;
        }

        db.Set<D.GateDefinition>().AddRange(Create(DateTime.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    private static D.GateDefinition Gate(string name, D.OpportunityStage to, int passing, bool approval, DateTime now, string description, params (string Text, int Weight)[] criteria)
    {
        var gate = new D.GateDefinition { Name = name, ToStage = to, PassingScore = passing, RequiresApproval = approval, Description = description, UpdatedAtUtc = now, UpdatedBy = "system" };
        gate.Criteria.AddRange(criteria.Select((c, i) => new D.GateCriterion { GateDefinitionId = gate.Id, Text = c.Text, Weight = c.Weight, Order = i }));
        return gate;
    }
}

/// <summary>
/// Go/no-go gates and their reviews. Reviews needing approval go through the "Opportunity gate" workflow (default: a member
/// of Business development other than the submitter, else the submitter's supervisor; administrators may decide).
/// </summary>
public sealed class OpportunityGateService(
    IWorkbenchDbContextFactory dbFactory,
    CrmAccess access,
    AuditRecorder audit,
    INotificationPublisher notifications,
    TimeProvider clock,
    ApprovalEngine? approvals = null) : IOpportunityGateService
{
    public const string AuditEntityType = "OpportunityGateReview";

    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<GateDefinitionDto>> ListGatesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await GateDefaults.EnsureAsync(db, cancellationToken);
        var gates = await db.Set<D.GateDefinition>().AsNoTracking().Include(g => g.Criteria).Where(g => includeInactive || g.IsActive).ToListAsync(cancellationToken);
        return gates.OrderBy(g => g.ToStage).ThenBy(g => g.Name).Select(Map).ToList();
    }

    public async Task<GateDefinitionDto> CreateGateAsync(SaveGateDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await GateDefaults.EnsureAsync(db, cancellationToken);
        var gate = new D.GateDefinition { Name = "" };
        await ApplyAsync(db, gate, request, cancellationToken);
        db.Set<D.GateDefinition>().Add(gate);
        audit.Record(db, "GateDefinition", gate.Id, "Created", after: GateSnapshot(gate));
        await db.SaveChangesAsync(cancellationToken);
        return Map(gate);
    }

    public async Task<GateDefinitionDto> UpdateGateAsync(Guid id, SaveGateDefinitionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var gate = await db.Set<D.GateDefinition>().Include(g => g.Criteria).FirstOrDefaultAsync(g => g.Id == id, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Gate", id);
        var before = GateSnapshot(gate);
        db.Set<D.GateCriterion>().RemoveRange(gate.Criteria);
        gate.Criteria.Clear();
        await ApplyAsync(db, gate, request, cancellationToken);
        audit.Record(db, "GateDefinition", gate.Id, "Updated", before, GateSnapshot(gate));
        await db.SaveChangesAsync(cancellationToken);
        return Map(gate);
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, D.GateDefinition gate, SaveGateDefinitionRequest r, CancellationToken ct)
    {
        gate.Name = CrmMapping.Required(r.Name, 200, "Name");
        gate.Description = CrmMapping.Clean(r.Description, 1000, "Description");
        var to = r.ToStage.ToDomain();
        if (to is D.OpportunityStage.Identified or >= D.OpportunityStage.Won)
        {
            throw new ArgumentException("A gate leads into Qualified, Capture, Proposal or Submitted.");
        }

        if (r.IsActive && await db.Set<D.GateDefinition>().AnyAsync(g => g.Id != gate.Id && g.IsActive && g.ToStage == to, ct))
        {
            throw new ArgumentException($"There's already an active gate into {to}; deactivate it first.");
        }

        if (r.PassingScore is < 0 or > 100)
        {
            throw new ArgumentException("The passing score is a percentage from 0 to 100.");
        }

        if (r.Criteria is not { Count: > 0 } || r.Criteria.Count > 50)
        {
            throw new ArgumentException("A gate has 1 to 50 checklist questions.");
        }

        gate.ToStage = to;
        gate.PassingScore = r.PassingScore;
        gate.RequiresApproval = r.RequiresApproval;
        gate.IsActive = r.IsActive;
        gate.UpdatedAtUtc = UtcNow;
        gate.UpdatedBy = access.Email;
        var tracked = db.Set<D.GateDefinition>().Local.Any(g => g.Id == gate.Id);
        var order = 0;
        foreach (var c in r.Criteria)
        {
            if (c.Weight is < 1 or > 10)
            {
                throw new ArgumentException("Question weights are 1 to 10.");
            }

            var criterion = new D.GateCriterion
            {
                GateDefinitionId = gate.Id, Text = CrmMapping.Required(c.Text, 300, "Question"), Guidance = CrmMapping.Clean(c.Guidance, 500, "Guidance"),
                Weight = c.Weight, Order = order++,
            };
            gate.Criteria.Add(criterion);
            if (tracked)
            {
                // Client-generated keys: a new row reached through a tracked parent must be added explicitly.
                db.Set<D.GateCriterion>().Add(criterion);
            }
        }
    }

    public async Task<IReadOnlyList<GateReviewDto>> ListReviewsAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reviews = await db.Set<D.OpportunityGateReview>().AsNoTracking().Include(r => r.Scores).Where(r => r.OpportunityId == opportunityId).ToListAsync(cancellationToken);
        return await MapAsync(db, reviews.OrderByDescending(r => r.SubmittedAtUtc).ToList(), cancellationToken);
    }

    public async Task<GateReviewDto> SubmitAsync(Guid opportunityId, SubmitGateReviewRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var opportunity = await db.Set<D.Opportunity>().FirstOrDefaultAsync(o => o.Id == opportunityId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Opportunity", opportunityId);
        var gate = await db.Set<D.GateDefinition>().AsNoTracking().Include(g => g.Criteria).FirstOrDefaultAsync(g => g.Id == request.GateDefinitionId && g.IsActive, cancellationToken)
            ?? throw new ArgumentException("Choose an active gate.");
        if (opportunity.Stage >= D.OpportunityStage.Won)
        {
            throw new ArgumentException($"{opportunity.Number} already has an outcome.");
        }

        if (request.Recommendation == GateRecommendation.Go && gate.ToStage <= opportunity.Stage)
        {
            throw new ArgumentException($"{opportunity.Number} is already past the {gate.Name} gate.");
        }

        if (await db.Set<D.OpportunityGateReview>().AnyAsync(r => r.OpportunityId == opportunityId && r.Status == D.GateReviewStatus.Pending, cancellationToken))
        {
            throw new ArgumentException("A gate decision on this opportunity is already waiting for approval.");
        }

        var scores = request.Scores ?? [];
        var byCriterion = scores.GroupBy(s => s.CriterionId).ToDictionary(g => g.Key, g => g.Last());
        if (gate.Criteria.Any(c => !byCriterion.ContainsKey(c.Id)))
        {
            throw new ArgumentException("Score every question on the checklist.");
        }

        if (byCriterion.Values.Any(s => s.Score is < 0 or > 5))
        {
            throw new ArgumentException("Scores are 0 (not at all) to 5 (fully).");
        }

        var now = UtcNow;
        var review = new D.OpportunityGateReview
        {
            OpportunityId = opportunityId, GateDefinitionId = gate.Id, GateName = gate.Name, ToStage = gate.ToStage, Recommendation = request.Recommendation.ToDomain(),
            PassingScore = gate.PassingScore, Notes = CrmMapping.Clean(request.Notes, 2000, "Notes"), Status = D.GateReviewStatus.Pending,
            SubmittedBy = access.Email, SubmittedAtUtc = now,
        };
        foreach (var criterion in gate.Criteria.OrderBy(c => c.Order))
        {
            var input = byCriterion[criterion.Id];
            review.Scores.Add(new D.OpportunityGateScore
            {
                ReviewId = review.Id, CriterionId = criterion.Id, Text = criterion.Text, Weight = criterion.Weight, Score = input.Score,
                Comment = CrmMapping.Clean(input.Comment, 500, "Comment"),
            });
        }

        review.ScorePercent = PipelineMath.GateScore(review.Scores.Select(s => (s.Score, s.Weight)));
        db.Set<D.OpportunityGateReview>().Add(review);
        audit.Record(db, AuditEntityType, review.Id, "Submitted", after: ReviewSnapshot(review));
        IReadOnlyList<string> notify = [];
        if (gate.RequiresApproval)
        {
            var route = await _approvals.StartAsync(db, await SubjectAsync(db, review, opportunity, cancellationToken), cancellationToken);
            notify = route.NotifyEmails;
        }
        else
        {
            await ApplyDecisionAsync(db, review, opportunity, approved: true, access.Email, "No approval needed", cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (notify.Count > 0)
        {
            await notifications.PublishAsync(notify, CrmNotificationKinds.GateSubmitted, $"Gate decision: {opportunity.Number} {opportunity.Name}",
                $"{gate.Name}: {(review.Recommendation == D.GateRecommendation.Go ? "Go" : "No-go")}, score {review.ScorePercent:0.#}% (passing {gate.PassingScore}%)",
                link: Link(opportunity.Id));
        }

        return (await MapAsync(db, [review], cancellationToken))[0];
    }

    public Task<GateReviewDto> ApproveAsync(Guid reviewId, DecideGateRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(reviewId, approve: true, request?.Comment, cancellationToken);

    public Task<GateReviewDto> RejectAsync(Guid reviewId, DecideGateRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(reviewId, approve: false, request?.Comment, cancellationToken);

    private async Task<GateReviewDto> DecideAsync(Guid reviewId, bool approve, string? comment, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var review = await db.Set<D.OpportunityGateReview>().Include(r => r.Scores).FirstOrDefaultAsync(r => r.Id == reviewId, ct)
            ?? throw WorkbenchNotFoundException.For("Gate review", reviewId);
        var opportunity = await db.Set<D.Opportunity>().FirstAsync(o => o.Id == review.OpportunityId, ct);
        if (review.Status != D.GateReviewStatus.Pending)
        {
            throw new ArgumentException($"This gate decision is already {review.Status.ToString().ToLowerInvariant()}.");
        }

        if (review.SubmittedBy == access.Email)
        {
            throw new WorkbenchForbiddenException("You can't approve your own gate decision.");
        }

        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.OpportunityGate, review.Id, access.Email,
            c => SubjectAsync(db, review, opportunity, c), ct);
        if (!check.CanDecide)
        {
            throw new WorkbenchForbiddenException("This gate decision isn't waiting on you.");
        }

        var note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (!approve && note is null)
        {
            throw new ArgumentException("Give a reason for rejecting the gate decision.");
        }

        var outcome = await _approvals.DecideAsync(db, check, approve ? ApprovalDecisionKind.Approve : ApprovalDecisionKind.Reject, access.Email, note, ct);
        if (approve && !outcome.IsComplete)
        {
            audit.Record(db, AuditEntityType, review.Id, "StepApproved", null, new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName)) }, note);
            await db.SaveChangesAsync(ct);
            await notifications.PublishAsync(outcome.NotifyEmails, CrmNotificationKinds.GateSubmitted, $"Gate decision: {opportunity.Number} {opportunity.Name}",
                review.GateName, link: Link(opportunity.Id));
            return (await MapAsync(db, [review], ct))[0];
        }

        await ApplyDecisionAsync(db, review, opportunity, approve, access.Email, note, ct);
        await db.SaveChangesAsync(ct);
        await notifications.PublishAsync(new[] { review.SubmittedBy, opportunity.CaptureManagerEmail, opportunity.ProposalManagerEmail }.Where(e => e != access.Email),
            CrmNotificationKinds.GateDecided,
            $"{review.GateName} {(approve ? "approved" : "rejected")}: {opportunity.Number} {opportunity.Name}",
            approve ? (review.Recommendation == D.GateRecommendation.Go ? $"Now in {review.ToStage}." : "Recorded as no-bid.") : note, link: Link(opportunity.Id));
        return (await MapAsync(db, [review], ct))[0];
    }

    /// <summary>Approved: a Go moves the opportunity into the gate's stage; a No-go records it as no-bid. Rejected: nothing moves.</summary>
    private async Task ApplyDecisionAsync(IWorkbenchDbContext db, D.OpportunityGateReview review, D.Opportunity opportunity, bool approved, string actor, string? note, CancellationToken ct)
    {
        review.Status = approved ? D.GateReviewStatus.Approved : D.GateReviewStatus.Rejected;
        review.DecidedBy = actor;
        review.DecidedAtUtc = UtcNow;
        review.DecisionComment = CrmMapping.Clip(note, 1000);
        audit.Record(db, AuditEntityType, review.Id, approved ? "Approved" : "Rejected", null, ReviewSnapshot(review), note);
        if (!approved || opportunity.Stage >= D.OpportunityStage.Won)
        {
            return;
        }

        if (review.Recommendation == D.GateRecommendation.Go)
        {
            if (opportunity.Stage < review.ToStage)
            {
                await OpportunityService.MoveAsync(db, opportunity, review.ToStage, actor, $"{review.GateName}: go ({review.ScorePercent:0.#}%)", audit, UtcNow, ct);
            }
        }
        else
        {
            opportunity.OutcomeReason = D.OutcomeReason.NotAligned;
            opportunity.OutcomeNotes = CrmMapping.Clip($"No-go at {review.GateName}{(review.Notes is null ? "" : $": {review.Notes}")}", 2000);
            opportunity.DecidedOn = DateOnly.FromDateTime(UtcNow);
            await OpportunityService.MoveAsync(db, opportunity, D.OpportunityStage.NoBid, actor, $"{review.GateName}: no-go", audit, UtcNow, ct);
        }
    }

    public async Task<GateReviewDto> CancelAsync(Guid reviewId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var review = await db.Set<D.OpportunityGateReview>().Include(r => r.Scores).FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Gate review", reviewId);
        if (review.SubmittedBy != access.Email && !(await access.RolesAsync(cancellationToken)).Has(Permission.OpportunitiesEditAll))
        {
            throw new WorkbenchForbiddenException("Only whoever submitted the gate decision (or an administrator) can withdraw it.");
        }

        if (review.Status != D.GateReviewStatus.Pending)
        {
            throw new ArgumentException($"This gate decision is already {review.Status.ToString().ToLowerInvariant()}.");
        }

        review.Status = D.GateReviewStatus.Cancelled;
        review.DecidedBy = access.Email;
        review.DecidedAtUtc = UtcNow;
        await _approvals.CancelAsync(db, ApprovalDocumentTypes.OpportunityGate, review.Id, access.Email, "Withdrawn", cancellationToken);
        audit.Record(db, AuditEntityType, review.Id, "Cancelled", null, ReviewSnapshot(review));
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync(db, [review], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<GateReviewDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default)
    {
        if (!access.IsSignedIn)
        {
            return [];
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = (await _approvals.ActionableAsync(db, ApprovalDocumentTypes.OpportunityGate, access.Email, cancellationToken))
            .Select(x => x.Instance.DocumentId).ToHashSet();
        var reviews = await db.Set<D.OpportunityGateReview>().AsNoTracking().Include(r => r.Scores)
            .Where(r => r.Status == D.GateReviewStatus.Pending && ids.Contains(r.Id)).ToListAsync(cancellationToken);
        return await MapAsync(db, reviews.OrderBy(r => r.SubmittedAtUtc).ToList(), cancellationToken);
    }

    private static string Link(Guid opportunityId) => $"bd/opportunities/{opportunityId}?tab=gates";

    /// <summary>How a gate review looks to the approval engine: the amount is the opportunity's estimated value (so routing can depend on size).</summary>
    internal static async Task<ApprovalSubject> SubjectAsync(IWorkbenchDbContext db, D.OpportunityGateReview review, D.Opportunity opportunity, CancellationToken ct)
    {
        var submitter = await EmployeeLinks.FindForUserAsync(db, review.SubmittedBy, ct);
        var recommendation = review.Recommendation == D.GateRecommendation.Go ? "Go" : "No-go";
        return new ApprovalSubject(ApprovalDocumentTypes.OpportunityGate, review.Id, $"{opportunity.Number} {opportunity.Name}: {review.GateName}",
            review.SubmittedBy, Link(opportunity.Id), opportunity.EstimatedValue,
            $"{recommendation} · score {review.ScorePercent:0.#}% (passing {review.PassingScore}%)", submitter?.Id, []);
    }

    private async Task<List<GateReviewDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<D.OpportunityGateReview> reviews, CancellationToken ct)
    {
        var ids = reviews.Select(r => r.OpportunityId).Distinct().ToList();
        var names = await db.Set<D.Opportunity>().AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => $"{o.Number} {o.Name}", ct);
        var mine = access.IsSignedIn && reviews.Any(r => r.Status == D.GateReviewStatus.Pending)
            ? (await _approvals.ActionableAsync(db, ApprovalDocumentTypes.OpportunityGate, access.Email, ct)).Select(x => x.Instance.DocumentId).ToHashSet()
            : [];
        return reviews.Select(r => new GateReviewDto(r.Id, r.OpportunityId, names.GetValueOrDefault(r.OpportunityId) ?? "", r.GateDefinitionId, r.GateName,
            r.ToStage.ToContract(), r.Recommendation.ToContract(), r.ScorePercent, r.PassingScore, r.Notes, r.Status.ToContract(), r.SubmittedBy, r.SubmittedAtUtc,
            r.DecidedBy, r.DecidedAtUtc, r.DecisionComment,
            r.Scores.Select(s => new GateScoreDto(s.CriterionId, s.Text, s.Weight, s.Score, s.Comment)).ToList(),
            r.Status == D.GateReviewStatus.Pending && mine.Contains(r.Id))).ToList();
    }

    private static GateDefinitionDto Map(D.GateDefinition g) =>
        new(g.Id, g.Name, g.ToStage.ToContract(), g.Description, g.PassingScore, g.RequiresApproval, g.IsActive,
            g.Criteria.OrderBy(c => c.Order).Select(c => new GateCriterionDto(c.Id, c.Text, c.Guidance, c.Weight, c.Order)).ToList());

    private static object GateSnapshot(D.GateDefinition g) =>
        new { g.Name, ToStage = g.ToStage.ToString(), g.PassingScore, g.RequiresApproval, g.IsActive, Criteria = g.Criteria.Select(c => new { c.Text, c.Weight }).ToList() };

    private static object ReviewSnapshot(D.OpportunityGateReview r) =>
        new { r.GateName, ToStage = r.ToStage.ToString(), Recommendation = r.Recommendation.ToString(), r.ScorePercent, Status = r.Status.ToString(), r.DecidedBy };
}

/// <summary>Lets the approvals inbox decide gate reviews through the gate service (so its checks run).</summary>
public sealed class OpportunityGateApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.OpportunityGate;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken cancellationToken) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IOpportunityGateService>(services)
            .ApproveAsync(documentId, new DecideGateRequest(comment), cancellationToken);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken cancellationToken) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IOpportunityGateService>(services)
            .RejectAsync(documentId, new DecideGateRequest(reason), cancellationToken);
}
