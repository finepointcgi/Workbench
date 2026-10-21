using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Materials;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Quality;
using Workbench.Domain.Approvals;
using Workbench.Domain.Materials;
using Workbench.Domain.Quality;

namespace Workbench.Application.Quality;

/// <summary>
/// Engineering change orders. A draft names the affected parts and the BOM (and part) revisions that replace the
/// current ones, why, the effectivity and what happens to stock and open work orders; it goes through the "Engineering
/// change" workflow. Approved changes become effective on their date (at approval when the date has come, hourly
/// otherwise, and a work order starting on or after the date is released on the new BOM even before): the new revisions
/// become active and open work orders on the old revision are flagged for review. Serial or lot effectivity takes effect at
/// approval and decides the as-designed configuration of each end item.
/// </summary>
public sealed class EngineeringChangeService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    INotificationPublisher notifications,
    IOptions<QualityOptions> options,
    TimeProvider clock,
    ApprovalEngine? approvals = null) : IEngineeringChangeService
{
    public const string NotificationKind = "quality.eco";

    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<IReadOnlyList<EcoSummaryDto>> ListAsync(EcoQuery query, CancellationToken cancellationToken = default)
    {
        query ??= new EcoQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var status = query.Status?.ToDomain();
        var ids = query.PartId is { } pid
            ? await db.Set<EngineeringChangeItem>().AsNoTracking().Where(i => i.PartId == pid || i.NewPartId == pid).Select(i => i.ChangeId).ToListAsync(cancellationToken)
            : null;
        var rows = await db.Set<EngineeringChange>().AsNoTracking().Include(c => c.Items)
            .Where(c => (status == null || c.Status == status) && (ids == null || ids.Contains(c.Id))
                && (!query.OpenOnly || c.Status == EngineeringChangeStatus.Draft || c.Status == EngineeringChangeStatus.PendingApproval || c.Status == EngineeringChangeStatus.Approved))
            .ToListAsync(cancellationToken);
        return await SummariesAsync(db, rows.OrderByDescending(c => c.Number, StringComparer.Ordinal).ToList(), cancellationToken);
    }

    public async Task<EcoDto> GetAsync(Guid ecoId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, ecoId, cancellationToken);
    }

    public async Task<EcoDto> SaveAsync(Guid? ecoId, SaveEcoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var eco = ecoId is { } id
            ? await LoadAsync(db, id, cancellationToken)
            : new EngineeringChange { Number = "", Title = "", Description = "", CreatedBy = audit.Actor, CreatedAtUtc = UtcNow };
        if (eco.Status != EngineeringChangeStatus.Draft)
        {
            throw new InvalidOperationException($"{eco.Number} is {Label(eco.Status).ToLowerInvariant()}; only drafts can be changed.");
        }

        var before = ecoId is null ? null : Snapshot(eco);
        eco.Title = PurchasingSupport.Required(request.Title, "Title", 200);
        eco.Description = PurchasingSupport.Required(request.Description, "Description of the change", 4000);
        eco.Reason = request.Reason.ToDomain();
        eco.EffectivityKind = request.EffectivityKind.ToDomain();
        eco.EffectiveDate = null;
        eco.EffectiveSerialOrLot = null;
        if (eco.EffectivityKind == EffectivityKind.Date)
        {
            eco.EffectiveDate = request.EffectiveDate ?? throw new ArgumentException("Choose the date the change takes effect.");
        }
        else
        {
            eco.EffectiveSerialOrLot = MaterialRules.Code(request.EffectiveSerialOrLot, "The first serial or lot built to the change", 64);
        }

        eco.StockDisposition = request.StockDisposition.ToDomain();
        eco.WorkOrderDisposition = request.WorkOrderDisposition.ToDomain();
        eco.CostImpact = request.CostImpact >= 0 && decimal.Round(request.CostImpact, 2) == request.CostImpact
            ? request.CostImpact : throw new ArgumentException("The cost impact is zero or more, in dollars and cents.");
        var items = request.Items ?? [];
        if (items.Count == 0)
        {
            throw new ArgumentException("Add the parts the change affects.");
        }

        if (items.Count > 50 || items.Select(i => i.PartId).Distinct().Count() != items.Count)
        {
            throw new ArgumentException("List each affected part once (at most 50).");
        }

        db.Set<EngineeringChangeItem>().RemoveRange(eco.Items);
        eco.Items.Clear();
        foreach (var input in items)
        {
            eco.Items.Add(await ItemAsync(db, eco, input, cancellationToken));
        }

        eco.UpdatedAtUtc = UtcNow;
        if (ecoId is null)
        {
            eco.Number = (await Customization.DocumentNumbers.NextInUnitOfWorkAsync(db, Customization.DocumentNumberTypes.EngineeringChange, null, DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken)).Number;
            db.Set<EngineeringChange>().Add(eco);
        }
        else
        {
            db.Set<EngineeringChangeItem>().AddRange(eco.Items);
        }

        audit.Record(db, QualityRules.EcoEntity, eco.Id, ecoId is null ? "Created" : "Updated", before, Snapshot(eco));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, eco.Id, cancellationToken);
    }

    private static async Task<EngineeringChangeItem> ItemAsync(IWorkbenchDbContext db, EngineeringChange eco, SaveEcoItemRequest input, CancellationToken ct)
    {
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == input.PartId, ct) ?? throw WorkbenchNotFoundException.For("Part", input.PartId);
        if (input.ToBomId is null && input.NewPartId is null)
        {
            throw new ArgumentException($"{part.Display}: choose the new BOM revision or the new part revision.");
        }

        Part? newPart = null;
        if (input.NewPartId is { } npid)
        {
            newPart = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == npid, ct) ?? throw WorkbenchNotFoundException.For("Part", npid);
            if (newPart.Number != part.Number || newPart.Id == part.Id)
            {
                throw new ArgumentException($"The new part revision must be another revision of {part.Number}.");
            }
        }

        if (input.ToBomId is { } bid)
        {
            var bom = await db.Set<BillOfMaterial>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid, ct) ?? throw WorkbenchNotFoundException.For("BOM", bid);
            if (bom.PartId != part.Id && bom.PartId != newPart?.Id)
            {
                throw new ArgumentException($"BOM revision {bom.Revision} isn't a BOM of {part.Display}.");
            }

            if (bom.IsActive)
            {
                throw new ArgumentException($"BOM revision {bom.Revision} of {part.Display} is already active; copy it to a new revision for the change.");
            }
        }

        var from = await db.Set<BillOfMaterial>().AsNoTracking().Where(b => b.PartId == part.Id && b.IsActive).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        return new EngineeringChangeItem
        {
            ChangeId = eco.Id, PartId = part.Id, FromBomId = from, ToBomId = input.ToBomId, NewPartId = input.NewPartId, Note = QualityRules.Clip(input.Note, 500),
        };
    }

    public async Task<EcoDto> SubmitAsync(Guid ecoId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var eco = await LoadAsync(db, ecoId, cancellationToken);
        if (eco.Status != EngineeringChangeStatus.Draft)
        {
            throw new InvalidOperationException($"{eco.Number} is {Label(eco.Status).ToLowerInvariant()}.");
        }

        foreach (var item in eco.Items.Where(i => i.ToBomId != null))
        {
            if (await db.Set<BillOfMaterial>().AnyAsync(b => b.Id == item.ToBomId && b.IsActive, cancellationToken))
            {
                throw new ArgumentException("A new BOM revision on the change is already active; edit the change.");
            }
        }

        eco.Status = EngineeringChangeStatus.PendingApproval;
        eco.SubmittedBy = audit.Actor;
        eco.SubmittedAtUtc = UtcNow;
        eco.DecidedBy = null;
        eco.DecidedAtUtc = null;
        eco.DecisionComment = null;
        eco.UpdatedAtUtc = UtcNow;
        audit.Record(db, QualityRules.EcoEntity, eco.Id, "Submitted", new { Status = nameof(EngineeringChangeStatus.Draft) }, new { Status = eco.Status.ToString() });
        var route = await _approvals.StartAsync(db, await SubjectAsync(db, eco, cancellationToken), cancellationToken);
        IReadOnlyList<string> notify = route.NotifyEmails;
        List<WorkOrder> flagged = [];
        if (route.Instance.Status == ApprovalInstanceStatus.Approved)
        {
            flagged = await MarkApprovedAsync(db, eco, audit.Actor, "No approval needed", cancellationToken);
            notify = [];
        }

        await db.SaveChangesAsync(cancellationToken);
        if (notify.Count > 0)
        {
            await notifications.PublishAsync(notify, NotificationKind, $"Engineering change to approve: {eco.Number} {eco.Title}", ReasonLabel(eco.Reason), link: Link(eco.Id));
        }

        await NotifyFlaggedAsync(eco, flagged);
        return await DetailAsync(db, eco.Id, cancellationToken);
    }

    public Task<EcoDto> ApproveAsync(Guid ecoId, DecideEcoRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(ecoId, approve: true, request?.Comment, cancellationToken);

    public Task<EcoDto> RejectAsync(Guid ecoId, DecideEcoRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(ecoId, approve: false, request?.Comment, cancellationToken);

    private async Task<EcoDto> DecideAsync(Guid ecoId, bool approve, string? comment, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var eco = await LoadAsync(db, ecoId, ct);
        if (eco.Status != EngineeringChangeStatus.PendingApproval)
        {
            throw new InvalidOperationException($"{eco.Number} isn't waiting for approval.");
        }

        if (eco.SubmittedBy == audit.Actor)
        {
            throw new WorkbenchForbiddenException("You can't approve an engineering change you submitted.");
        }

        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.EngineeringChange, eco.Id, audit.Actor, c => SubjectAsync(db, eco, c), ct);
        if (!check.CanDecide)
        {
            throw new WorkbenchForbiddenException("This engineering change isn't waiting on you.");
        }

        var note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (!approve && note is null)
        {
            throw new ArgumentException("Give a reason for sending the change back.");
        }

        var outcome = await _approvals.DecideAsync(db, check, approve ? ApprovalDecisionKind.Approve : ApprovalDecisionKind.Reject, audit.Actor, note, ct);
        if (approve && !outcome.IsComplete)
        {
            audit.Record(db, QualityRules.EcoEntity, eco.Id, "StepApproved", null, new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName)) }, note);
            await db.SaveChangesAsync(ct);
            await notifications.PublishAsync(outcome.NotifyEmails, NotificationKind, $"Engineering change to approve: {eco.Number} {eco.Title}", ReasonLabel(eco.Reason),
                link: Link(eco.Id));
            return await DetailAsync(db, eco.Id, ct);
        }

        List<WorkOrder> flagged = [];
        if (approve)
        {
            flagged = await MarkApprovedAsync(db, eco, audit.Actor, note, ct);
        }
        else
        {
            eco.Status = EngineeringChangeStatus.Draft;
            eco.DecidedBy = audit.Actor;
            eco.DecidedAtUtc = UtcNow;
            eco.DecisionComment = QualityRules.Clip(note, 1000);
            eco.UpdatedAtUtc = UtcNow;
            audit.Record(db, QualityRules.EcoEntity, eco.Id, "Rejected", new { Status = nameof(EngineeringChangeStatus.PendingApproval) }, new { Status = eco.Status.ToString() }, note);
        }

        await db.SaveChangesAsync(ct);
        if (eco.SubmittedBy is { } submitter && submitter != audit.Actor)
        {
            await notifications.PublishAsync(submitter, NotificationKind,
                $"{eco.Number} {(approve ? Label(eco.Status).ToLowerInvariant() : "sent back")}: {eco.Title}", approve ? null : note, link: Link(eco.Id));
        }

        await NotifyFlaggedAsync(eco, flagged);
        return await DetailAsync(db, eco.Id, ct);
    }

    private async Task<List<WorkOrder>> MarkApprovedAsync(IWorkbenchDbContext db, EngineeringChange eco, string actor, string? note, CancellationToken ct)
    {
        eco.Status = EngineeringChangeStatus.Approved;
        eco.DecidedBy = actor;
        eco.DecidedAtUtc = UtcNow;
        eco.DecisionComment = QualityRules.Clip(note, 1000);
        eco.UpdatedAtUtc = UtcNow;
        audit.Record(db, QualityRules.EcoEntity, eco.Id, "Approved", new { Status = nameof(EngineeringChangeStatus.PendingApproval) }, new { Status = eco.Status.ToString() }, note);
        return IsDue(eco, Today) ? await MakeEffectiveAsync(db, eco, ct) : [];
    }

    private static bool IsDue(EngineeringChange eco, DateOnly asOf) =>
        eco.Status == EngineeringChangeStatus.Approved && (eco.EffectivityKind == EffectivityKind.SerialOrLot || eco.EffectiveDate <= asOf);

    /// <summary>The new revisions become active; open work orders on the old revision are flagged for review.</summary>
    private async Task<List<WorkOrder>> MakeEffectiveAsync(IWorkbenchDbContext db, EngineeringChange eco, CancellationToken ct)
    {
        var flagged = new List<WorkOrder>();
        foreach (var item in eco.Items)
        {
            if (item.ToBomId is { } bid)
            {
                var bom = await db.Set<BillOfMaterial>().FirstAsync(b => b.Id == bid, ct);
                foreach (var other in await db.Set<BillOfMaterial>().Where(b => b.PartId == bom.PartId && b.Id != bom.Id && b.IsActive).ToListAsync(ct))
                {
                    other.IsActive = false;
                    other.UpdatedAtUtc = UtcNow;
                }

                bom.IsActive = true;
                bom.UpdatedAtUtc = UtcNow;
                audit.Record(db, MaterialRules.BomEntity, bom.Id, "Activated", after: new { bom.Revision, EngineeringChange = eco.Number });
            }

            if (item.NewPartId is { } npid)
            {
                var part = await db.Set<Part>().FirstAsync(p => p.Id == npid, ct);
                if (!part.IsActive)
                {
                    part.IsActive = true;
                    part.UpdatedAtUtc = UtcNow;
                    audit.Record(db, MaterialRules.PartEntity, part.Id, "Released", after: new { part.Number, part.Revision, EngineeringChange = eco.Number });
                }
            }

            var orders = await db.Set<WorkOrder>()
                .Where(w => w.PartId == item.PartId && (w.Status == WorkOrderStatus.Planned || w.Status == WorkOrderStatus.Released || w.Status == WorkOrderStatus.InProgress)
                    && (item.ToBomId == null || w.BomId != item.ToBomId))
                .ToListAsync(ct);
            foreach (var wo in orders)
            {
                if (!await db.Set<EngineeringChangeWorkOrderReview>().AnyAsync(r => r.ChangeId == eco.Id && r.WorkOrderId == wo.Id, ct))
                {
                    db.Set<EngineeringChangeWorkOrderReview>().Add(new EngineeringChangeWorkOrderReview
                    {
                        ChangeId = eco.Id, WorkOrderId = wo.Id, Status = WorkOrderReviewStatus.Open, FlaggedAtUtc = UtcNow,
                    });
                    flagged.Add(wo);
                }
            }
        }

        eco.Status = EngineeringChangeStatus.Effective;
        eco.EffectiveAtUtc = UtcNow;
        eco.UpdatedAtUtc = UtcNow;
        audit.Record(db, QualityRules.EcoEntity, eco.Id, "Effective", new { Status = nameof(EngineeringChangeStatus.Approved) },
            new { Status = eco.Status.ToString(), WorkOrdersFlagged = flagged.Select(w => w.Number).ToList() });
        return flagged;
    }

    private async Task NotifyFlaggedAsync(EngineeringChange eco, IReadOnlyList<WorkOrder> flagged)
    {
        if (flagged.Count == 0)
        {
            return;
        }

        var to = new[] { eco.CreatedBy, eco.SubmittedBy }.Concat(flagged.Select(w => w.CreatedBy)).Where(e => e is not null && e != audit.Actor);
        await notifications.PublishAsync(to, NotificationKind, $"{eco.Number} is effective: review {flagged.Count} open work order{(flagged.Count == 1 ? "" : "s")}",
            string.Join(", ", flagged.Select(w => w.Number)), link: Link(eco.Id));
    }

    public async Task<EcoDto> CancelAsync(Guid ecoId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var eco = await LoadAsync(db, ecoId, cancellationToken);
        if (eco.Status is EngineeringChangeStatus.Effective or EngineeringChangeStatus.Cancelled)
        {
            throw new InvalidOperationException($"{eco.Number} is {Label(eco.Status).ToLowerInvariant()}; an effective change is undone by another change.");
        }

        var before = eco.Status.ToString();
        if (eco.Status == EngineeringChangeStatus.PendingApproval)
        {
            await _approvals.CancelAsync(db, ApprovalDocumentTypes.EngineeringChange, eco.Id, audit.Actor, "Cancelled", cancellationToken);
        }

        eco.Status = EngineeringChangeStatus.Cancelled;
        eco.UpdatedAtUtc = UtcNow;
        audit.Record(db, QualityRules.EcoEntity, eco.Id, "Cancelled", new { Status = before }, new { Status = eco.Status.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, eco.Id, cancellationToken);
    }

    public async Task<int> ApplyDueAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var date = asOf ?? Today;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var due = (await db.Set<EngineeringChange>().Include(c => c.Items).Where(c => c.Status == EngineeringChangeStatus.Approved).ToListAsync(cancellationToken))
            .Where(c => IsDue(c, date)).OrderBy(c => c.EffectiveDate).ThenBy(c => c.Number, StringComparer.Ordinal).ToList();
        var flagged = new List<(EngineeringChange, List<WorkOrder>)>();
        foreach (var eco in due)
        {
            flagged.Add((eco, await MakeEffectiveAsync(db, eco, cancellationToken)));
        }

        if (due.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var (eco, orders) in flagged)
            {
                await NotifyFlaggedAsync(eco, orders);
            }
        }

        return due.Count;
    }

    public async Task<IReadOnlyList<WorkOrderReviewDto>> ListOpenWorkOrderReviewsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<EngineeringChangeWorkOrderReview>().AsNoTracking().Where(r => r.Status == WorkOrderReviewStatus.Open).ToListAsync(cancellationToken);
        return await ReviewsAsync(db, rows, cancellationToken);
    }

    public async Task<WorkOrderReviewDto> MarkWorkOrderReviewedAsync(Guid reviewId, ReviewWorkOrderRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var review = await db.Set<EngineeringChangeWorkOrderReview>().FirstOrDefaultAsync(r => r.Id == reviewId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Work order review", reviewId);
        if (review.Status == WorkOrderReviewStatus.Reviewed)
        {
            throw new InvalidOperationException("That work order is already reviewed.");
        }

        review.Status = WorkOrderReviewStatus.Reviewed;
        review.ReviewedBy = audit.Actor;
        review.ReviewedAtUtc = UtcNow;
        review.Note = PurchasingSupport.Optional(request?.Note, "Note", 1000);
        audit.Record(db, MaterialRules.WorkOrderEntity, review.WorkOrderId, "ChangeReviewed", after: new { review.ChangeId, review.Note });
        await db.SaveChangesAsync(cancellationToken);
        return (await ReviewsAsync(db, [review], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<EcoSummaryDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = (await _approvals.ActionableAsync(db, ApprovalDocumentTypes.EngineeringChange, audit.Actor, cancellationToken)).Select(x => x.Instance.DocumentId).ToHashSet();
        var rows = await db.Set<EngineeringChange>().AsNoTracking().Include(c => c.Items)
            .Where(c => c.Status == EngineeringChangeStatus.PendingApproval && ids.Contains(c.Id)).ToListAsync(cancellationToken);
        return await SummariesAsync(db, rows, cancellationToken);
    }

    // ---- Revision history and configuration ----

    public async Task<RevisionHistoryDto> RevisionHistoryAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == partId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", partId);
        var revisions = await db.Set<Part>().AsNoTracking().Where(p => p.Number == part.Number).ToListAsync(cancellationToken);
        var ids = revisions.Select(p => p.Id).ToList();
        var boms = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).Where(b => ids.Contains(b.PartId)).ToListAsync(cancellationToken);
        var items = await db.Set<EngineeringChangeItem>().AsNoTracking().Where(i => ids.Contains(i.PartId) || (i.NewPartId != null && ids.Contains(i.NewPartId.Value)))
            .ToListAsync(cancellationToken);
        var changeIds = items.Select(i => i.ChangeId).Distinct().ToList();
        var changes = await db.Set<EngineeringChange>().AsNoTracking().Where(c => changeIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var bomRevs = boms.ToDictionary(b => b.Id, b => b.Revision);
        var partRevs = revisions.ToDictionary(p => p.Id, p => p.Revision);
        var events = new List<RevisionEventDto>();
        foreach (var p in revisions)
        {
            events.Add(new RevisionEventDto(p.CreatedAtUtc, "Part revision", p.Revision.Length == 0 ? "—" : p.Revision,
                $"{p.Description}{(p.IsActive ? "" : " (inactive)")}", null, null, p.IsActive));
        }

        foreach (var b in boms)
        {
            var owner = partRevs.GetValueOrDefault(b.PartId);
            events.Add(new RevisionEventDto(b.CreatedAtUtc, "BOM revision", b.Revision,
                $"{b.Components.Count} component{(b.Components.Count == 1 ? "" : "s")}{(string.IsNullOrEmpty(owner) || b.PartId == part.Id ? "" : $" (part rev {owner})")}{(b.IsActive ? ", active" : "")}",
                null, null, b.IsActive));
        }

        foreach (var item in items)
        {
            if (!changes.TryGetValue(item.ChangeId, out var c) || c.Status is EngineeringChangeStatus.Draft or EngineeringChangeStatus.Cancelled)
            {
                continue;
            }

            var to = item.ToBomId is { } t ? $"BOM {bomRevs.GetValueOrDefault(t)}" : null;
            var newPart = item.NewPartId is { } n ? $"part rev {partRevs.GetValueOrDefault(n)}" : null;
            var what = string.Join(" and ", new[] { to, newPart }.OfType<string>());
            var effectivity = c.EffectivityKind == EffectivityKind.Date ? $"from {c.EffectiveDate:MMM d, yyyy}" : $"from serial/lot {c.EffectiveSerialOrLot}";
            events.Add(new RevisionEventDto(c.EffectiveAtUtc ?? c.DecidedAtUtc ?? c.SubmittedAtUtc ?? c.CreatedAtUtc,
                c.Status == EngineeringChangeStatus.Effective ? "Change effective" : $"Change {Label(c.Status).ToLowerInvariant()}",
                item.ToBomId is { } tb ? bomRevs.GetValueOrDefault(tb) ?? "" : item.NewPartId is { } np ? partRevs.GetValueOrDefault(np) ?? "" : "",
                $"{c.Title}: {what} {effectivity} ({ReasonLabel(c.Reason).ToLowerInvariant()})", c.Id, c.Number, false));
        }

        var active = boms.FirstOrDefault(b => b.PartId == part.Id && b.IsActive)?.Revision;
        return new RevisionHistoryDto(part.Id, part.Display, part.Description, active, events.OrderByDescending(e => e.AtUtc).ToList());
    }

    public async Task<IReadOnlyList<ConfigurationUnitDto>> ListUnitsAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == partId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", partId);
        var built = await BuiltUnitsAsync(db, part.Id, cancellationToken);
        var result = new List<ConfigurationUnitDto>();
        foreach (var t in built.GroupBy(t => (t.Serial, t.Lot)).Select(g => g.First()).Take(200))
        {
            var config = await ConfigurationCoreAsync(db, part, t, cancellationToken);
            result.Add(new ConfigurationUnitDto(t.Serial, t.Lot, config.WorkOrderNumber, t.Date, config.AsBuiltBomRevision, config.AsDesignedBomRevision, config.Matches));
        }

        return result;
    }

    public async Task<ConfigurationDto> ConfigurationAsync(Guid partId, string? serial = null, string? lot = null, CancellationToken cancellationToken = default)
    {
        var s = (serial ?? "").Trim().ToUpperInvariant();
        var l = (lot ?? "").Trim().ToUpperInvariant();
        if (s.Length == 0 && l.Length == 0)
        {
            throw new ArgumentException("Enter the serial or lot number of the unit.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == partId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", partId);
        var t = (await BuiltUnitsAsync(db, part.Id, cancellationToken)).FirstOrDefault(x => s.Length > 0 ? x.Serial == s : x.Lot == l)
            ?? throw new WorkbenchNotFoundException($"No completed unit of {part.Display} {(s.Length > 0 ? $"serial {s}" : $"lot {l}")} was found.");
        return await ConfigurationCoreAsync(db, part, t, cancellationToken);
    }

    /// <summary>Completions (to stock or delivered to a project) of the part, newest first, leaving out rework orders (they rebuild a unit, not build it).</summary>
    private static async Task<List<InventoryTransaction>> BuiltUnitsAsync(IWorkbenchDbContext db, Guid partId, CancellationToken ct)
    {
        var rows = await db.Set<InventoryTransaction>().AsNoTracking()
            .Where(t => t.PartId == partId && t.WorkOrderId != null && (t.Type == InventoryTransactionType.WorkOrderCompletion || t.Type == InventoryTransactionType.WorkOrderShipment)
                && (t.Serial != "" || t.Lot != ""))
            .ToListAsync(ct);
        var woIds = rows.Select(t => t.WorkOrderId!.Value).Distinct().ToList();
        var rework = (await db.Set<WorkOrderComponent>().AsNoTracking().Where(c => woIds.Contains(c.WorkOrderId) && c.PartId == partId).Select(c => c.WorkOrderId).ToListAsync(ct)).ToHashSet();
        return rows.Where(t => !rework.Contains(t.WorkOrderId!.Value)).OrderByDescending(t => t.Number, StringComparer.Ordinal).ToList();
    }

    private static async Task<ConfigurationDto> ConfigurationCoreAsync(IWorkbenchDbContext db, Part part, InventoryTransaction completion, CancellationToken ct)
    {
        var wo = await db.Set<WorkOrder>().AsNoTracking().FirstAsync(w => w.Id == completion.WorkOrderId, ct);
        var unit = completion.Serial.Length > 0 ? completion.Serial : completion.Lot;

        // As designed: the BOM revision in effect for this unit, from the effective engineering changes in order.
        var changes = await db.Set<EngineeringChangeItem>().AsNoTracking().Where(i => i.PartId == part.Id && i.ToBomId != null)
            .Join(db.Set<EngineeringChange>().AsNoTracking().Where(c => c.Status == EngineeringChangeStatus.Effective), i => i.ChangeId, c => c.Id, (i, c) => new { Item = i, Change = c })
            .ToListAsync(ct);
        Guid? designed = null;
        string? by = null;
        foreach (var x in changes.OrderBy(x => x.Change.EffectiveAtUtc))
        {
            designed ??= x.Item.FromBomId;
            var applies = x.Change.EffectivityKind == EffectivityKind.Date
                ? x.Change.EffectiveDate <= completion.Date
                : string.CompareOrdinal(unit, x.Change.EffectiveSerialOrLot) >= 0;
            if (applies)
            {
                designed = x.Item.ToBomId;
                by = x.Change.Number;
            }
        }

        designed ??= wo.BomId;
        var bomIds = new[] { designed, wo.BomId }.OfType<Guid>().Distinct().ToList();
        var boms = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).Where(b => bomIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var designedQty = designed is { } d && boms.TryGetValue(d, out var db1)
            ? db1.Components.Where(c => c.IsEffectiveOn(completion.Date)).GroupBy(c => c.ComponentPartId).ToDictionary(g => g.Key, g => g.Sum(c => c.QuantityPer))
            : [];

        // As built: what was issued to the work order (net of returns) per unit, with the lots and serials traced to this unit.
        var issued = await db.Set<InventoryTransaction>().AsNoTracking()
            .Where(t => t.WorkOrderId == wo.Id && (t.Type == InventoryTransactionType.IssueToWorkOrder || t.Type == InventoryTransactionType.ReturnFromWorkOrder)).ToListAsync(ct);
        var units = wo.Quantity == 0 ? 1 : wo.Quantity;
        var builtQty = issued.GroupBy(t => t.PartId).ToDictionary(g => g.Key, g => Math.Round(-g.Sum(t => t.Quantity) / units, 4));
        var traced = await db.Set<WorkOrderGenealogy>().AsNoTracking().Where(g => g.CompletionTransactionId == completion.Id).Select(g => g.ComponentTransactionId).ToListAsync(ct);
        var tracedRows = issued.Where(t => traced.Contains(t.Id)).ToList();
        var partIds = designedQty.Keys.Concat(builtQty.Keys).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var lines = partIds.Select(id =>
        {
            var want = designedQty.GetValueOrDefault(id);
            var got = builtQty.GetValueOrDefault(id);
            var status = want == got ? "Matches" : got == 0 ? "Missing" : want == 0 ? "Not in the design" : "Different quantity";
            var lots = tracedRows.Where(t => t.PartId == id).Select(t => t.Serial.Length > 0 ? $"S/N {t.Serial}" : $"Lot {t.Lot}").Distinct().ToList();
            return new ConfigurationLineDto(id, parts[id].Display, parts[id].Description, want, got, lots, status);
        }).OrderBy(l => l.PartNumber, StringComparer.Ordinal).ToList();
        return new ConfigurationDto(part.Id, part.Display, completion.Serial, completion.Lot, wo.Number, completion.Date,
            wo.BomId is { } built && boms.TryGetValue(built, out var b2) ? b2.Revision : null,
            designed is { } dd && boms.TryGetValue(dd, out var b3) ? b3.Revision : null, by, lines);
    }

    // ---- Subject and mapping ----

    internal async Task<ApprovalSubject> SubjectAsync(IWorkbenchDbContext db, EngineeringChange eco, CancellationToken ct)
    {
        var submitter = eco.SubmittedBy is { } s ? await EmployeeLinks.FindForUserAsync(db, s, ct) : null;
        var partIds = eco.Items.Select(i => i.PartId).ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).Select(p => p.Number).ToListAsync(ct);
        return new ApprovalSubject(ApprovalDocumentTypes.EngineeringChange, eco.Id, $"{eco.Number} {eco.Title}", eco.SubmittedBy ?? "", Link(eco.Id), eco.CostImpact,
            $"{ReasonLabel(eco.Reason)} · {string.Join(", ", parts)}", submitter?.Id, [new ApprovalCharge(null, eco.CostImpact, submitter?.OrgUnitId, "Materials")],
            options.Value.EcoFinanceThreshold);
    }

    public static string Link(Guid ecoId) => $"materials/engineering-changes/{ecoId}";

    public static string Label(EngineeringChangeStatus s) => s switch
    {
        EngineeringChangeStatus.PendingApproval => "Waiting for approval",
        _ => s.ToString(),
    };

    public static string ReasonLabel(EngineeringChangeReason r) => r switch
    {
        EngineeringChangeReason.DefectCorrection => "Defect correction",
        EngineeringChangeReason.DesignImprovement => "Design improvement",
        EngineeringChangeReason.CostReduction => "Cost reduction",
        EngineeringChangeReason.CustomerRequest => "Customer request",
        _ => r.ToString(),
    };

    private static async Task<EngineeringChange> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<EngineeringChange>().Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Engineering change", id);

    private static object Snapshot(EngineeringChange c) => new
    {
        c.Title, Reason = c.Reason.ToString(), Effectivity = c.EffectivityKind.ToString(), c.EffectiveDate, c.EffectiveSerialOrLot, c.CostImpact,
        Items = c.Items.Select(i => new { i.PartId, i.ToBomId, i.NewPartId }).ToList(),
    };

    private static async Task<List<EcoSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<EngineeringChange> rows, CancellationToken ct)
    {
        var partIds = rows.SelectMany(c => c.Items.Select(i => i.PartId)).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Display, ct);
        var ids = rows.Select(c => c.Id).ToList();
        var open = (await db.Set<EngineeringChangeWorkOrderReview>().AsNoTracking().Where(r => ids.Contains(r.ChangeId) && r.Status == WorkOrderReviewStatus.Open)
            .Select(r => r.ChangeId).ToListAsync(ct)).GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        return rows.Select(c => new EcoSummaryDto(c.Id, c.Number, c.Title, c.Reason.ToContract(), c.Status.ToContract(), c.EffectivityKind.ToContract(), c.EffectiveDate,
            c.EffectiveSerialOrLot, c.Items.Select(i => parts.GetValueOrDefault(i.PartId) ?? "").ToList(), open.GetValueOrDefault(c.Id), c.CreatedAtUtc, c.EffectiveAtUtc)).ToList();
    }

    private static async Task<List<WorkOrderReviewDto>> ReviewsAsync(IWorkbenchDbContext db, IReadOnlyList<EngineeringChangeWorkOrderReview> rows, CancellationToken ct)
    {
        var woIds = rows.Select(r => r.WorkOrderId).Distinct().ToList();
        var orders = await db.Set<WorkOrder>().AsNoTracking().Where(w => woIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var partIds = orders.Values.Select(w => w.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Display, ct);
        return rows.Select(r =>
        {
            var wo = orders[r.WorkOrderId];
            return new WorkOrderReviewDto(r.Id, wo.Id, wo.Number, parts.GetValueOrDefault(wo.PartId) ?? "", wo.Status.ToContract(), r.Status.ToContract(), r.FlaggedAtUtc,
                r.ReviewedBy, r.Note);
        }).OrderBy(r => r.Status).ThenBy(r => r.WorkOrderNumber, StringComparer.Ordinal).ToList();
    }

    private async Task<EcoDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var c = await db.Set<EngineeringChange>().AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct)
            ?? throw WorkbenchNotFoundException.For("Engineering change", id);
        var summary = (await SummariesAsync(db, [c], ct))[0];
        var partIds = c.Items.SelectMany(i => new[] { (Guid?)i.PartId, i.NewPartId }).OfType<Guid>().Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var bomIds = c.Items.SelectMany(i => new[] { i.FromBomId, i.ToBomId }).OfType<Guid>().Distinct().ToList();
        var boms = await db.Set<BillOfMaterial>().AsNoTracking().Where(b => bomIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Revision, ct);
        var reviews = await ReviewsAsync(db, await db.Set<EngineeringChangeWorkOrderReview>().AsNoTracking().Where(r => r.ChangeId == c.Id).ToListAsync(ct), ct);
        var canDecide = c.Status == EngineeringChangeStatus.PendingApproval && c.SubmittedBy != audit.Actor
            && await _approvals.CanDecideAsync(db, ApprovalDocumentTypes.EngineeringChange, c.Id, audit.Actor, x => SubjectAsync(db, c, x), ct);
        return new EcoDto(summary, c.Description, c.StockDisposition.ToContract(), c.WorkOrderDisposition.ToContract(), c.CostImpact, c.CreatedBy, c.SubmittedBy,
            c.SubmittedAtUtc, c.DecidedBy, c.DecidedAtUtc, c.DecisionComment,
            c.Items.Select(i =>
            {
                var p = parts[i.PartId];
                return new EcoItemDto(i.Id, p.Id, p.Number, p.Revision, p.Description, i.FromBomId, i.FromBomId is { } f ? boms.GetValueOrDefault(f) : null,
                    i.ToBomId, i.ToBomId is { } t ? boms.GetValueOrDefault(t) : null, i.NewPartId, i.NewPartId is { } n ? parts.GetValueOrDefault(n)?.Revision : null, i.Note);
            }).ToList(),
            reviews, canDecide, c.Status == EngineeringChangeStatus.Draft);
    }
}

/// <summary>Lets the approvals inbox decide engineering changes through the change service (so its checks and the effectivity switch run).</summary>
public sealed class EngineeringChangeApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.EngineeringChange;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken cancellationToken) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IEngineeringChangeService>(services)
            .ApproveAsync(documentId, new DecideEcoRequest(comment), cancellationToken);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken cancellationToken) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IEngineeringChangeService>(services)
            .RejectAsync(documentId, new DecideEcoRequest(reason), cancellationToken);
}
