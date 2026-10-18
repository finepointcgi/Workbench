using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Revenue;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Revenue;

namespace Workbench.Application.Revenue;

/// <summary>Revenue setup per project or CLIN, estimates at completion (submit → approve/reject) and FFP deliveries. Every change is audited.</summary>
public sealed class RevenueSetupService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IRevenueSetupService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<RevenueProjectSummaryDto>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var names = await RevenueSupport.Names.LoadAsync(db, [], cancellationToken);
        var setups = await db.Set<RevenueSetup>().AsNoTracking().ToListAsync(cancellationToken);
        var estimates = await db.Set<RevenueEstimate>().AsNoTracking().ToListAsync(cancellationToken);
        return names.Projects.Values.Where(p => p.ContractId is { } c && names.Contracts.ContainsKey(c))
            .OrderBy(p => p.Code, StringComparer.Ordinal)
            .Select(p =>
            {
                var contract = names.Contracts[p.ContractId!.Value];
                var current = estimates.Where(e => e.ProjectId == p.Id && e.ClinId == null && e.Status == EstimateStatus.Approved)
                    .OrderBy(e => e.AsOf).ThenBy(e => e.DecidedAtUtc).LastOrDefault();
                return new RevenueProjectSummaryDto(p.Id, p.Code, p.Name, contract.Id, contract.ContractNumber, BillingMapping.Label(contract.Type),
                    setups.Where(s => s.ProjectId == p.Id).OrderBy(s => names.Clin(s.ClinId) ?? "", StringComparer.Ordinal).Select(s => s.ToDto(names)).ToList(),
                    current?.ToDto(names), estimates.Count(e => e.ProjectId == p.Id && e.Status == EstimateStatus.Submitted));
            })
            .ToList();
    }

    public async Task<ProjectRevenueDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await RevenueSupport.FindProjectAsync(db, projectId, cancellationToken);
        var runs = await RevenueSupport.RunDtosAsync(db, db.Set<RevenueRun>().Where(r => r.ProjectId == projectId), cancellationToken);
        var names = await RevenueSupport.Names.LoadAsync(db, [], cancellationToken);
        var contract = project.ContractId is { } c ? names.Contracts.GetValueOrDefault(c) : null;
        var clins = names.Clins.Values.Where(x => x.ContractId == contract?.Id).OrderBy(x => x.Order).ThenBy(x => x.Number, StringComparer.Ordinal).ToList();
        decimal? suggested = contract is null ? null : RevenueEngine.ContractValue(contract, clins).Value;
        var setups = await db.Set<RevenueSetup>().AsNoTracking().Where(s => s.ProjectId == projectId).ToListAsync(cancellationToken);
        var estimates = await db.Set<RevenueEstimate>().AsNoTracking().Where(e => e.ProjectId == projectId).ToListAsync(cancellationToken);
        var deliveries = await db.Set<RevenueDelivery>().AsNoTracking().Where(d => d.ProjectId == projectId).ToListAsync(cancellationToken);
        return new ProjectRevenueDto(project.Id, project.Code, project.Name, contract?.Id, contract?.ContractNumber, (contract is null ? null : BillingMapping.Label(contract.Type)),
            clins.Select(x => new RevenueClinOptionDto(x.Id, x.Number, x.Description, BillingMapping.Label(x.Pricing))).ToList(),
            setups.FirstOrDefault(s => s.ClinId is null)?.TransactionPrice ?? suggested,
            setups.OrderBy(s => names.Clin(s.ClinId) ?? "", StringComparer.Ordinal).Select(s => s.ToDto(names)).ToList(),
            estimates.OrderByDescending(e => e.AsOf).ThenByDescending(e => e.SubmittedAtUtc).Select(e => e.ToDto(names)).ToList(),
            deliveries.OrderByDescending(d => d.DeliveredOn).ThenByDescending(d => d.RecordedAtUtc).Select(d => d.ToDto(names)).ToList(),
            runs, CanManage: true);
    }

    public async Task<RevenueSetupDto> SaveSetupAsync(SaveRevenueSetupRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await RevenueSupport.FindProjectAsync(db, request.ProjectId, cancellationToken);
        await ValidateUnitAsync(db, project, request.ClinId, cancellationToken);
        if (!Enum.IsDefined(request.Method))
        {
            throw new ArgumentException("Choose a revenue method.");
        }

        Rate(request.FeeRate, "The fee rate");
        Rate(request.OdcMarkupRate, "The ODC markup");
        if (request.TransactionPrice is < 0 || request.UnitPrice is < 0)
        {
            throw new ArgumentException("Prices can't be negative.");
        }

        if (request.Method == RevenueMethodKind.FixedPriceUnits && request.UnitPrice is not > 0)
        {
            throw new ArgumentException("FFP units delivered needs a unit price.");
        }

        var setup = await db.Set<RevenueSetup>().FirstOrDefaultAsync(s => s.ProjectId == project.Id && s.ClinId == request.ClinId, cancellationToken);
        var before = setup is null ? null : Snapshot(setup);
        if (setup is null)
        {
            setup = new RevenueSetup { ProjectId = project.Id, ClinId = request.ClinId, UpdatedBy = audit.Actor };
            db.Set<RevenueSetup>().Add(setup);
        }

        setup.Method = request.Method.ToDomain();
        setup.FeeRate = request.FeeRate;
        setup.OdcMarkupRate = request.OdcMarkupRate;
        setup.TransactionPrice = request.TransactionPrice;
        setup.UnitPrice = request.UnitPrice;
        setup.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : RevenueSupport.Truncate(request.Notes.Trim(), 1000);
        setup.UpdatedBy = audit.Actor;
        setup.UpdatedAtUtc = UtcNow;
        audit.Record(db, RevenueSupport.SetupEntity, setup.Id, before is null ? "Created" : "Updated", before, Snapshot(setup));
        await db.SaveChangesAsync(cancellationToken);
        return setup.ToDto(await RevenueSupport.Names.LoadAsync(db, [], cancellationToken));
    }

    public async Task DeleteSetupAsync(Guid setupId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var setup = await db.Set<RevenueSetup>().FirstOrDefaultAsync(s => s.Id == setupId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Revenue setup", setupId);
        db.Set<RevenueSetup>().Remove(setup);
        audit.Record(db, RevenueSupport.SetupEntity, setup.Id, "Deleted", Snapshot(setup));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EstimateDto>> ListEstimatesAsync(EstimateState? status = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wanted = status?.ToDomain();
        var rows = await db.Set<RevenueEstimate>().AsNoTracking().Where(e => wanted == null || e.Status == wanted).ToListAsync(cancellationToken);
        var names = await RevenueSupport.Names.LoadAsync(db, [], cancellationToken);
        return rows.OrderByDescending(e => e.SubmittedAtUtc).Select(e => e.ToDto(names)).ToList();
    }

    public async Task<EstimateDto> SubmitEstimateAsync(SubmitEstimateRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await RevenueSupport.FindProjectAsync(db, request.ProjectId, cancellationToken);
        await ValidateUnitAsync(db, project, request.ClinId, cancellationToken);
        if (request.EstimatedCost <= 0 || request.EstimatedRevenue < 0)
        {
            throw new ArgumentException("Enter the estimated cost at completion (more than zero) and the estimated revenue.");
        }

        if (decimal.Round(request.EstimatedCost, 2) != request.EstimatedCost || decimal.Round(request.EstimatedRevenue, 2) != request.EstimatedRevenue)
        {
            throw new ArgumentException("Estimates are in whole cents.");
        }

        var estimate = new RevenueEstimate
        {
            ProjectId = project.Id,
            ClinId = request.ClinId,
            AsOf = request.AsOf,
            EstimatedCost = request.EstimatedCost,
            EstimatedRevenue = request.EstimatedRevenue,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : RevenueSupport.Truncate(request.Notes.Trim(), 2000),
            SubmittedBy = audit.Actor,
            SubmittedAtUtc = UtcNow,
        };
        db.Set<RevenueEstimate>().Add(estimate);
        audit.Record(db, RevenueSupport.EstimateEntity, estimate.Id, "Submitted", after: Snapshot(estimate));
        await db.SaveChangesAsync(cancellationToken);
        return estimate.ToDto(await RevenueSupport.Names.LoadAsync(db, [], cancellationToken));
    }

    public Task<EstimateDto> ApproveEstimateAsync(Guid estimateId, DecideEstimateRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(estimateId, EstimateStatus.Approved, request.Note, cancellationToken);

    public Task<EstimateDto> RejectEstimateAsync(Guid estimateId, DecideEstimateRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Note))
        {
            throw new ArgumentException("Say why the estimate is rejected.");
        }

        return DecideAsync(estimateId, EstimateStatus.Rejected, request.Note, cancellationToken);
    }

    private async Task<EstimateDto> DecideAsync(Guid estimateId, EstimateStatus status, string? note, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var estimate = await db.Set<RevenueEstimate>().FirstOrDefaultAsync(e => e.Id == estimateId, ct) ?? throw WorkbenchNotFoundException.For("Estimate", estimateId);
        if (estimate.Status != EstimateStatus.Submitted)
        {
            throw new InvalidOperationException($"This estimate is already {estimate.Status.ToString().ToLowerInvariant()}.");
        }

        estimate.Status = status;
        estimate.DecidedBy = audit.Actor;
        estimate.DecidedAtUtc = UtcNow;
        estimate.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : RevenueSupport.Truncate(note.Trim(), 1000);
        audit.Record(db, RevenueSupport.EstimateEntity, estimate.Id, status.ToString(), new { Status = nameof(EstimateStatus.Submitted) }, Snapshot(estimate), estimate.DecisionNote);
        await db.SaveChangesAsync(ct);
        return estimate.ToDto(await RevenueSupport.Names.LoadAsync(db, [], ct));
    }

    public async Task<RevenueDeliveryDto> RecordDeliveryAsync(RecordDeliveryRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await RevenueSupport.FindProjectAsync(db, request.ProjectId, cancellationToken);
        await ValidateUnitAsync(db, project, request.ClinId, cancellationToken);
        if (request.Quantity == 0)
        {
            throw new ArgumentException("Enter the quantity delivered (negative to correct an earlier entry).");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new ArgumentException("Describe what was delivered.");
        }

        var delivery = new RevenueDelivery
        {
            ProjectId = project.Id,
            ClinId = request.ClinId,
            DeliveredOn = request.DeliveredOn,
            Quantity = request.Quantity,
            Description = RevenueSupport.Truncate(request.Description.Trim(), 500),
            RecordedBy = audit.Actor,
            RecordedAtUtc = UtcNow,
        };
        db.Set<RevenueDelivery>().Add(delivery);
        audit.Record(db, RevenueSupport.DeliveryEntity, delivery.Id, "Recorded", after: new { delivery.DeliveredOn, delivery.Quantity, delivery.Description });
        await db.SaveChangesAsync(cancellationToken);
        return delivery.ToDto(await RevenueSupport.Names.LoadAsync(db, [], cancellationToken));
    }

    /// <summary>The project must be on a contract, and a CLIN must belong to that contract.</summary>
    private static async Task ValidateUnitAsync(IWorkbenchDbContext db, Project project, Guid? clinId, CancellationToken ct)
    {
        if (project.ContractId is null)
        {
            throw new ArgumentException($"Project {project.Code} isn't on a contract, so it has no revenue to recognize.");
        }

        if (clinId is { } id && !await db.Set<Clin>().AnyAsync(c => c.Id == id && c.ContractId == project.ContractId, ct))
        {
            throw new ArgumentException("That CLIN isn't on the project's contract.");
        }
    }

    private static void Rate(decimal? rate, string what)
    {
        if (rate is < 0 or > 10)
        {
            throw new ArgumentException($"{what} is a fraction (0.07 = 7%) between 0 and 10.");
        }
    }

    private static object Snapshot(RevenueSetup s) => new { Method = s.Method.ToString(), s.ClinId, s.FeeRate, s.OdcMarkupRate, s.TransactionPrice, s.UnitPrice, s.Notes };

    private static object Snapshot(RevenueEstimate e) => new { e.AsOf, e.ClinId, e.EstimatedCost, e.EstimatedRevenue, Status = e.Status.ToString(), e.Notes };
}
