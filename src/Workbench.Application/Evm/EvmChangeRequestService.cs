using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Evm;
using Workbench.Domain.Evm;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Evm;

public sealed class EvmChangeRequestService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IEvmChangeRequestService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<EvmChangeRequestDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EvmEngine.RequireProjectAsync(db, projectId, cancellationToken);
        var codes = await CodesAsync(db, projectId, cancellationToken);
        var requests = await db.Set<EvmChangeRequest>().AsNoTracking().Include(c => c.Lines).Where(c => c.ProjectId == projectId).ToListAsync(cancellationToken);
        return requests.OrderByDescending(c => c.Number).Select(c => ToDto(c, codes)).ToList();
    }

    public async Task<EvmChangeRequestDto> GetAsync(Guid changeRequestId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var request = await db.Set<EvmChangeRequest>().AsNoTracking().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == changeRequestId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Change request", changeRequestId);
        return ToDto(request, await CodesAsync(db, request.ProjectId, cancellationToken));
    }

    public async Task<EvmChangeRequestDto> CreateAsync(Guid projectId, CreateChangeRequestRequest request, CancellationToken cancellationToken = default)
    {
        var title = request.Title.Required("Title");
        var justification = request.Justification.Required("Justification");
        if (title.Length > 200 || justification.Length > 4000)
        {
            throw new ArgumentException("The title is limited to 200 characters and the justification to 4000.");
        }

        if (request.Lines is not { Count: > 0 })
        {
            throw new ArgumentException("Add at least one change.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await EvmEngine.RequireProjectAsync(db, projectId, cancellationToken);
        var baseline = await db.Set<EvmBaseline>().AsNoTracking().FirstOrDefaultAsync(b => b.ProjectId == projectId, cancellationToken)
            ?? throw new ArgumentException("This project has no performance measurement baseline yet.");
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, cancellationToken);
        var existing = await db.Set<EvmWorkPackage>().AsNoTracking().Where(w => w.BaselineId == baseline.Id).Select(w => w.WbsElementId).ToListAsync(cancellationToken);

        var change = new EvmChangeRequest
        {
            ProjectId = projectId,
            BaselineId = baseline.Id,
            Number = (await db.Set<EvmChangeRequest>().Where(c => c.ProjectId == projectId).Select(c => (int?)c.Number).MaxAsync(cancellationToken) ?? 0) + 1,
            Title = title,
            Justification = justification,
            Status = EvmChangeRequestStatus.Pending,
            RequestedBy = audit.Actor,
            RequestedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        var order = 0;
        foreach (var line in request.Lines)
        {
            if (!Enum.IsDefined(line.Source) || (line.NewTechnique is { } t && !Enum.IsDefined(t)))
            {
                throw new ArgumentException("Unknown budget source or technique.");
            }

            if (line.WbsElementId is { } id)
            {
                if (!wbs.ContainsKey(id))
                {
                    throw new ArgumentException("Every change must be on a WBS element of this project.");
                }

                if (!existing.Contains(id) && !EvmEngine.IsLeaf(wbs, id))
                {
                    throw new ArgumentException($"{wbs[id].Code} has children; new work packages go on WBS leaves.");
                }

                if (!existing.Contains(id) && (line.NewStart is null || line.NewFinish is null))
                {
                    throw new ArgumentException($"{wbs[id].Code} has no work package yet; give its planned start and finish.");
                }

                if (line.NewTechnique is EvTechnique.Apportioned or EvTechnique.Milestones)
                {
                    throw new ArgumentException("Change to milestones or apportioned from the Baseline tab before the first close, or choose another technique.");
                }
            }
            else
            {
                if (line.Source == EvmBudgetSourceKind.UndistributedBudget)
                {
                    throw new ArgumentException("Moving undistributed budget into undistributed budget changes nothing; pick a work package.");
                }

                if (line.NewTechnique is not null || line.NewStart is not null || line.NewFinish is not null)
                {
                    throw new ArgumentException("Dates and techniques belong to a work package; pick a WBS element.");
                }
            }

            if (line.BudgetChange == 0 && line.NewTechnique is null && line.NewStart is null && line.NewFinish is null)
            {
                throw new ArgumentException("Each change needs a budget amount, new dates or a new technique.");
            }

            if (line.NewStart is { } s && line.NewFinish is { } f && f < s)
            {
                throw new ArgumentException("A work package can't finish before it starts.");
            }

            change.Lines.Add(new EvmChangeRequestLine
            {
                ChangeRequestId = change.Id,
                Order = order++,
                WbsElementId = line.WbsElementId,
                Source = (EvmBudgetSource)(int)line.Source,
                BudgetChange = Financials.Money.Round(line.BudgetChange),
                NewTechnique = line.NewTechnique is { } nt ? (EarnedValueTechnique)(int)nt : null,
                NewStart = line.NewStart,
                NewFinish = line.NewFinish,
            });
        }

        db.Set<EvmChangeRequest>().Add(change);
        audit.Record(db, nameof(EvmChangeRequest), change.Id, "Requested", after: new { change.Number, change.Title, Lines = change.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(change, wbs.ToDictionary(w => w.Key, w => w.Value.Code));
    }

    public async Task<EvmChangeRequestDto> ApproveAsync(Guid changeRequestId, DecideChangeRequestRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var change = await LoadPendingAsync(db, changeRequestId, cancellationToken);
        var baseline = await db.Set<EvmBaseline>().FirstAsync(b => b.Id == change.BaselineId, cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == change.ProjectId).ToDictionaryAsync(w => w.Id, cancellationToken);
        var packages = await db.Set<EvmWorkPackage>().Include(w => w.Milestones).Where(w => w.BaselineId == baseline.Id).ToListAsync(cancellationToken);
        var touched = change.Lines.Where(l => l.WbsElementId is not null).Select(l => l.WbsElementId!.Value).ToHashSet();
        var before = Snapshot(baseline, packages.Where(w => touched.Contains(w.WbsElementId)), wbs);

        foreach (var line in change.Lines.OrderBy(l => l.Order))
        {
            switch (line.Source)
            {
                case EvmBudgetSource.ManagementReserve:
                    baseline.ManagementReserve -= line.BudgetChange;
                    break;
                case EvmBudgetSource.UndistributedBudget:
                    baseline.UndistributedBudget -= line.BudgetChange;
                    break;
            }

            if (line.WbsElementId is not { } wbsId)
            {
                baseline.UndistributedBudget += line.BudgetChange;
                continue;
            }

            var wp = packages.FirstOrDefault(w => w.WbsElementId == wbsId);
            if (wp is null)
            {
                wp = new EvmWorkPackage
                {
                    BaselineId = baseline.Id,
                    WbsElementId = wbsId,
                    ControlAccountWbsId = EvmEngine.ControlAccountOf(wbs, wbsId, baseline.ControlAccountLevel),
                    Technique = line.NewTechnique ?? EarnedValueTechnique.PercentComplete,
                    PlannedStart = line.NewStart ?? throw new ArgumentException("A new work package needs a planned start."),
                    PlannedFinish = line.NewFinish ?? throw new ArgumentException("A new work package needs a planned finish."),
                };
                packages.Add(wp);
                db.Set<EvmWorkPackage>().Add(wp);
            }

            wp.Budget += line.BudgetChange;
            if (wp.Budget < 0)
            {
                throw new ArgumentException($"{wbs[wbsId].Code}'s budget would go below zero.");
            }

            if (line.NewTechnique is { } technique)
            {
                if (technique == EarnedValueTechnique.LevelOfEffort && packages.Any(w => w.ApportionedToId == wp.Id))
                {
                    throw new ArgumentException($"Work packages are apportioned to {wbs[wbsId].Code}, so it must stay discrete.");
                }

                wp.Technique = technique;
                wp.ApportionedToId = null;
            }

            wp.PlannedStart = line.NewStart ?? wp.PlannedStart;
            wp.PlannedFinish = line.NewFinish ?? wp.PlannedFinish;
            if (wp.PlannedFinish < wp.PlannedStart)
            {
                throw new ArgumentException($"{wbs[wbsId].Code} would finish before it starts.");
            }
        }

        if (baseline.ManagementReserve < 0)
        {
            throw new ArgumentException("There isn't enough management reserve for this change.");
        }

        if (baseline.UndistributedBudget < 0)
        {
            throw new ArgumentException("There isn't enough undistributed budget for this change.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        baseline.Revision++;
        baseline.UpdatedAtUtc = now;
        change.Status = EvmChangeRequestStatus.Approved;
        change.DecidedBy = audit.Actor;
        change.DecidedAtUtc = now;
        change.DecisionNote = Note(request.Note);
        change.AppliedRevision = baseline.Revision;
        var after = Snapshot(baseline, packages.Where(w => touched.Contains(w.WbsElementId)), wbs);
        change.BeforeJson = JsonSerializer.Serialize(before, Json);
        change.AfterJson = JsonSerializer.Serialize(after, Json);
        audit.Record(db, nameof(EvmChangeRequest), change.Id, "Approved", before, after, change.DecisionNote);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(change, wbs.ToDictionary(w => w.Key, w => w.Value.Code));
    }

    public async Task<EvmChangeRequestDto> RejectAsync(Guid changeRequestId, DecideChangeRequestRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var change = await LoadPendingAsync(db, changeRequestId, cancellationToken);
        change.Status = EvmChangeRequestStatus.Rejected;
        change.DecidedBy = audit.Actor;
        change.DecidedAtUtc = clock.GetUtcNow().UtcDateTime;
        change.DecisionNote = Note(request.Note);
        audit.Record(db, nameof(EvmChangeRequest), change.Id, "Rejected", reason: change.DecisionNote);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(change, await CodesAsync(db, change.ProjectId, cancellationToken));
    }

    private static async Task<EvmChangeRequest> LoadPendingAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var change = await db.Set<EvmChangeRequest>().Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw WorkbenchNotFoundException.For("Change request", id);
        return change.Status == EvmChangeRequestStatus.Pending
            ? change
            : throw new ArgumentException($"BCR-{change.Number:000} was already {change.Status.ToString().ToLowerInvariant()}.");
    }

    private static string? Note(string? note) =>
        string.IsNullOrWhiteSpace(note) ? null : note.Trim().Length > 2000 ? throw new ArgumentException("The note is limited to 2000 characters.") : note.Trim();

    private static EvmBaselineSnapshotDto Snapshot(EvmBaseline baseline, IEnumerable<EvmWorkPackage> packages, IReadOnlyDictionary<Guid, WbsElement> wbs) =>
        new(baseline.Revision, baseline.ManagementReserve, baseline.UndistributedBudget, packages
            .OrderBy(w => wbs.GetValueOrDefault(w.WbsElementId)?.Code, StringComparer.Ordinal)
            .Select(w => new EvmWorkPackageSnapshotDto(w.WbsElementId, wbs.GetValueOrDefault(w.WbsElementId)?.Code ?? "?", (EvTechnique)(int)w.Technique,
                w.Budget, w.PlannedStart, w.PlannedFinish))
            .ToList());

    private static async Task<Dictionary<Guid, string>> CodesAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct) =>
        await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, w => w.Code, ct);

    private static EvmChangeRequestDto ToDto(EvmChangeRequest c, IReadOnlyDictionary<Guid, string> codes) =>
        new(c.Id, c.ProjectId, c.Number, c.Title, c.Justification, (EvmChangeStatus)(int)c.Status, c.RequestedBy, c.RequestedAtUtc,
            c.DecidedBy, c.DecidedAtUtc, c.DecisionNote, c.AppliedRevision,
            c.Lines.OrderBy(l => l.Order).Select(l => new EvmChangeLineDto(l.WbsElementId, l.WbsElementId is { } id ? codes.GetValueOrDefault(id) : null,
                (EvmBudgetSourceKind)(int)l.Source, l.BudgetChange, l.NewTechnique is { } t ? (EvTechnique)(int)t : null, l.NewStart, l.NewFinish)).ToList(),
            c.BeforeJson is null ? null : JsonSerializer.Deserialize<EvmBaselineSnapshotDto>(c.BeforeJson, Json),
            c.AfterJson is null ? null : JsonSerializer.Deserialize<EvmBaselineSnapshotDto>(c.AfterJson, Json));
}
