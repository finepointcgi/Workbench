using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

public sealed class BillingMilestoneService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit) : IBillingMilestoneService
{
    public async Task<IReadOnlyList<BillingMilestoneDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        var milestones = await db.Set<BillingMilestone>().AsNoTracking().Where(m => m.ContractId == contractId).ToListAsync(cancellationToken);
        return await DtosAsync(db, milestones, cancellationToken);
    }

    public async Task<BillingMilestoneDto> CreateAsync(Guid contractId, SaveMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await ValidateAsync(db, contractId, request, cancellationToken);
        var order = await db.Set<BillingMilestone>().Where(m => m.ContractId == contractId).MaxAsync(m => (int?)m.Order, cancellationToken) ?? -1;
        var milestone = new BillingMilestone
        {
            ContractId = contractId,
            ClinId = request.ClinId,
            Description = request.Description.Trim(),
            Amount = request.Amount,
            PlannedDate = request.PlannedDate,
            Order = order + 1,
        };
        db.Set<BillingMilestone>().Add(milestone);
        audit.Record(db, nameof(BillingMilestone), milestone.Id, "Created", after: new { milestone.Description, milestone.Amount, milestone.PlannedDate });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [milestone], cancellationToken))[0];
    }

    public async Task<BillingMilestoneDto> UpdateAsync(Guid milestoneId, SaveMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadUnbilledAsync(db, milestoneId, cancellationToken);
        await ValidateAsync(db, milestone.ContractId, request, cancellationToken);
        var before = new { milestone.Description, milestone.Amount, milestone.PlannedDate, milestone.ClinId };
        milestone.ClinId = request.ClinId;
        milestone.Description = request.Description.Trim();
        milestone.Amount = request.Amount;
        milestone.PlannedDate = request.PlannedDate;
        audit.Record(db, nameof(BillingMilestone), milestone.Id, "Updated", before, new { milestone.Description, milestone.Amount, milestone.PlannedDate, milestone.ClinId });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [milestone], cancellationToken))[0];
    }

    public async Task<BillingMilestoneDto> CompleteAsync(Guid milestoneId, CompleteMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadUnbilledAsync(db, milestoneId, cancellationToken);
        milestone.CompletedDate = request.CompletedDate;
        milestone.CompletedBy = audit.Actor;
        audit.Record(db, nameof(BillingMilestone), milestone.Id, "Completed", after: new { request.CompletedDate });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [milestone], cancellationToken))[0];
    }

    public async Task<BillingMilestoneDto> ReopenAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadUnbilledAsync(db, milestoneId, cancellationToken);
        var before = new { milestone.CompletedDate };
        milestone.CompletedDate = null;
        milestone.CompletedBy = null;
        audit.Record(db, nameof(BillingMilestone), milestone.Id, "Reopened", before);
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [milestone], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadUnbilledAsync(db, milestoneId, cancellationToken);
        db.Set<BillingMilestone>().Remove(milestone);
        audit.Record(db, nameof(BillingMilestone), milestone.Id, "Deleted", before: new { milestone.Description, milestone.Amount });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task ValidateAsync(IWorkbenchDbContext db, Guid contractId, SaveMilestoneRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 500)
        {
            throw new ArgumentException("A milestone needs a description of up to 500 characters.");
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("A milestone's amount must be positive.");
        }

        var clin = await db.Set<Clin>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ClinId && c.ContractId == contractId, ct)
            ?? throw new ArgumentException("Choose a CLIN on this contract.");
        if (clin.Pricing != ClinPricing.FirmFixedPrice)
        {
            throw new ArgumentException($"CLIN {clin.Number} isn't firm fixed price; milestones are billed on FFP CLINs.");
        }
    }

    private static async Task<BillingMilestone> LoadUnbilledAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var milestone = await db.Set<BillingMilestone>().FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Milestone", id);
        return milestone.InvoiceId is null
            ? milestone
            : throw new InvalidOperationException("This milestone is on an invoice. Void or delete that invoice first.");
    }

    private static async Task<List<BillingMilestoneDto>> DtosAsync(IWorkbenchDbContext db, IReadOnlyList<BillingMilestone> milestones, CancellationToken ct)
    {
        var clinIds = milestones.Select(m => m.ClinId).Distinct().ToList();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => clinIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        var invoiceIds = milestones.Where(m => m.InvoiceId is not null).Select(m => m.InvoiceId!.Value).Distinct().ToList();
        var invoices = await db.Set<Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        return milestones.OrderBy(m => m.Order).ThenBy(m => m.PlannedDate)
            .Select(m => new BillingMilestoneDto(m.Id, m.ContractId, m.ClinId, clins.GetValueOrDefault(m.ClinId, "?"), m.Description, m.Amount, m.PlannedDate,
                m.CompletedDate, m.CompletedBy, m.InvoiceId, m.InvoiceId is { } i ? invoices.GetValueOrDefault(i) : null, m.Order))
            .ToList();
    }
}
