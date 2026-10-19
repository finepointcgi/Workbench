using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// Performance-based payment events (FAR 32.10). Completing one needs evidence; completed, unbilled events go on the next
/// PerformanceBased invoice (generated like FFP milestones), and deliveries liquidate them.
/// </summary>
public sealed class PerformanceBasedPaymentService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit) : IPerformanceBasedPaymentService
{
    public async Task<IReadOnlyList<PerformanceEventDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        var events = await db.Set<PerformanceBasedPaymentEvent>().AsNoTracking().Where(e => e.ContractId == contractId).ToListAsync(cancellationToken);
        return await DtosAsync(db, events, cancellationToken);
    }

    public async Task<PerformanceEventDto> CreateAsync(Guid contractId, SavePerformanceEventRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await ValidateAsync(db, contractId, null, request, cancellationToken);
        var order = await db.Set<PerformanceBasedPaymentEvent>().Where(e => e.ContractId == contractId).MaxAsync(e => (int?)e.Order, cancellationToken) ?? 0;
        var row = new PerformanceBasedPaymentEvent
        {
            ContractId = contractId,
            ClinId = request.ClinId,
            Order = order + 1,
            Description = request.Description.Trim(),
            Criteria = InvoiceService.Clean(request.Criteria, 2000),
            Amount = request.Amount,
            PlannedDate = request.PlannedDate,
        };
        db.Set<PerformanceBasedPaymentEvent>().Add(row);
        audit.Record(db, nameof(PerformanceBasedPaymentEvent), row.Id, "Created", after: new { row.Description, row.Amount, row.PlannedDate });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [row], cancellationToken))[0];
    }

    public async Task<PerformanceEventDto> UpdateAsync(Guid eventId, SavePerformanceEventRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await LoadUnbilledAsync(db, eventId, cancellationToken);
        await ValidateAsync(db, row.ContractId, row.Id, request, cancellationToken);
        var before = new { row.Description, row.Amount, row.PlannedDate, row.Criteria, row.ClinId };
        row.Description = request.Description.Trim();
        row.Criteria = InvoiceService.Clean(request.Criteria, 2000);
        row.Amount = request.Amount;
        row.PlannedDate = request.PlannedDate;
        row.ClinId = request.ClinId;
        audit.Record(db, nameof(PerformanceBasedPaymentEvent), row.Id, "Updated", before, new { row.Description, row.Amount, row.PlannedDate, row.Criteria, row.ClinId });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [row], cancellationToken))[0];
    }

    public async Task<PerformanceEventDto> CompleteAsync(Guid eventId, CompletePerformanceEventRequest request, CancellationToken cancellationToken = default)
    {
        var evidence = InvoiceService.Clean(request.Evidence, 2000)
            ?? throw new ArgumentException("Describe the evidence that the event was accomplished (for example the test report or acceptance memo).");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await LoadUnbilledAsync(db, eventId, cancellationToken);
        row.CompletedDate = request.CompletedDate;
        row.CompletedBy = audit.Actor;
        row.Evidence = evidence;
        row.EvidenceReference = InvoiceService.Clean(request.EvidenceReference, 500);
        audit.Record(db, nameof(PerformanceBasedPaymentEvent), row.Id, "Completed", after: new { row.CompletedDate, row.Evidence, row.EvidenceReference });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [row], cancellationToken))[0];
    }

    public async Task<PerformanceEventDto> ReopenAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await LoadUnbilledAsync(db, eventId, cancellationToken);
        var before = new { row.CompletedDate, row.Evidence };
        row.CompletedDate = null;
        row.CompletedBy = null;
        row.Evidence = null;
        row.EvidenceReference = null;
        audit.Record(db, nameof(PerformanceBasedPaymentEvent), row.Id, "Reopened", before);
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [row], cancellationToken))[0];
    }

    public async Task DeleteAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await LoadUnbilledAsync(db, eventId, cancellationToken);
        db.Set<PerformanceBasedPaymentEvent>().Remove(row);
        audit.Record(db, nameof(PerformanceBasedPaymentEvent), row.Id, "Deleted", before: new { row.Description, row.Amount });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<PerformanceBasedPaymentEvent> LoadUnbilledAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var row = await db.Set<PerformanceBasedPaymentEvent>().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Performance-based payment event", id);
        return row.InvoiceId is null ? row : throw new InvalidOperationException("This event is on an invoice. Void or delete that invoice to change it.");
    }

    private static async Task ValidateAsync(IWorkbenchDbContext db, Guid contractId, Guid? eventId, SavePerformanceEventRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 500)
        {
            throw new ArgumentException("An event needs a description of up to 500 characters.");
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("An event needs an amount above zero.");
        }

        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct) ?? throw WorkbenchNotFoundException.For("Contract", contractId);
        var settings = await ContractFinancingLedger.SettingsAsync(db, contractId, ct);
        if (settings?.Financing != ContractFinancing.PerformanceBased)
        {
            throw new ArgumentException($"Contract {contract.ContractNumber} doesn't use performance-based payments. Turn them on in its billing settings first.");
        }

        if (request.ClinId is { } clinId && !await db.Set<Clin>().AnyAsync(c => c.Id == clinId && c.ContractId == contractId, ct))
        {
            throw new ArgumentException("That CLIN isn't on this contract.");
        }

        var price = ContractFinancingLedger.ContractPrice(settings, contract);
        var others = (await db.Set<PerformanceBasedPaymentEvent>().AsNoTracking().Where(e => e.ContractId == contractId && e.Id != eventId)
            .Select(e => e.Amount).ToListAsync(ct)).Sum();
        var cap = Math.Round(price * ContractFinancingLedger.PerformanceBasedCap, 2);
        if (others + request.Amount > cap)
        {
            throw new ArgumentException($"Performance-based payments can total at most 90% of the contract price ({cap:C}); events would total {others + request.Amount:C}.");
        }
    }

    private static async Task<List<PerformanceEventDto>> DtosAsync(IWorkbenchDbContext db, IReadOnlyList<PerformanceBasedPaymentEvent> events, CancellationToken ct)
    {
        var clinIds = events.Where(e => e.ClinId is not null).Select(e => e.ClinId!.Value).Distinct().ToList();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => clinIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, ct);
        var invoiceIds = events.Where(e => e.InvoiceId is not null).Select(e => e.InvoiceId!.Value).Distinct().ToList();
        var invoices = await db.Set<Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        return events.OrderBy(e => e.Order).ThenBy(e => e.PlannedDate)
            .Select(e => new PerformanceEventDto(e.Id, e.ContractId, e.ClinId, e.ClinId is { } c ? clins.GetValueOrDefault(c) : null, e.Order, e.Description,
                e.Criteria, e.Amount, e.PlannedDate, e.CompletedDate, e.CompletedBy, e.Evidence, e.EvidenceReference, e.InvoiceId,
                e.InvoiceId is { } i ? invoices.GetValueOrDefault(i) : null))
            .ToList();
    }
}
