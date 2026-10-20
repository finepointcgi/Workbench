using Microsoft.EntityFrameworkCore;
using Workbench.Application.Odcs;
using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Travel;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Travel;

namespace Workbench.Application.Travel;

/// <summary>
/// The cardholder's charges. A charge becomes an ordinary expense line (an <c>OdcEntry</c> on their draft report) whose
/// amount is locked to the charge; receipts, approval, unallowable handling and posting are the expense report's. Posting
/// credits a company-paid program's liability account instead of employee reimbursements (see <c>PostingEngine</c>).
/// </summary>
public sealed class CardChargeService(IWorkbenchDbContextFactory dbFactory, OdcWorkflow workflow, IExpenseReportService reports, TimeProvider clock) : ICardChargeService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<IReadOnlyList<CorporateCardDto>> ListMyCardsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await workflow.FindCurrentEmployeeAsync(db, cancellationToken) is not { } me)
        {
            return [];
        }

        var cards = await db.Set<CorporateCard>().AsNoTracking().Where(c => c.EmployeeId == me.Id).ToListAsync(cancellationToken);
        return await CardProgramService.CardDtosAsync(db, cards, cancellationToken);
    }

    public async Task<IReadOnlyList<CardTransactionDto>> ListMyChargesAsync(bool includeAssigned = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await workflow.FindCurrentEmployeeAsync(db, cancellationToken) is not { } me)
        {
            return [];
        }

        var rows = (await db.Set<CardTransaction>().AsNoTracking().Where(t => t.EmployeeId == me.Id && !t.IsExcluded).ToListAsync(cancellationToken))
            .OrderByDescending(t => t.TransactionDate).ThenBy(t => t.Merchant).ToList();
        var dtos = await CardSupport.ToDtosAsync(db, rows, Today, cancellationToken);
        return includeAssigned ? dtos : dtos.Where(d => d.State == CardChargeState.Unassigned).ToList();
    }

    public async Task<CardChargeSummaryDto> GetMySummaryAsync(CancellationToken cancellationToken = default)
    {
        var charges = (await ListMyChargesAsync(includeAssigned: true, cancellationToken)).Where(c => c.Amount > 0).ToList();
        var unassigned = charges.Where(c => c.State == CardChargeState.Unassigned).ToList();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var programIds = charges.Select(c => c.ProgramId).Distinct().ToList();
        var days = await db.Set<CardProgram>().AsNoTracking().Where(p => programIds.Contains(p.Id)).Select(p => p.UnsubmittedAlertDays).ToListAsync(cancellationToken);
        return new CardChargeSummaryDto(unassigned.Count, unassigned.Sum(c => c.Amount), charges.Count(c => c.IsOverdue), days.Count == 0 ? 30 : days.Min());
    }

    public async Task<OdcEntryDto> AttachToReportAsync(Guid transactionId, AttachCardChargeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Type is not (OdcCostType.Travel or OdcCostType.Other))
        {
            throw new ArgumentException("Card charges go on an expense report as Travel or Other.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await workflow.RequireCurrentEmployeeAsync(db, cancellationToken);
        var charge = await FindMineAsync(db, me.Id, transactionId, cancellationToken);
        if (charge.IsExcluded)
        {
            throw new ArgumentException("Finance excluded this charge, so it can't go on a report.");
        }

        if (charge.OdcEntryId is { } existing && await db.Set<OdcEntry>().AnyAsync(o => o.Id == existing, cancellationToken))
        {
            throw new ArgumentException("This charge is already on an expense report.");
        }

        if (charge.Amount <= 0)
        {
            throw new ArgumentException("Credits and refunds don't go on expense reports; Finance matches them on the card reconciliation page.");
        }

        var report = await db.Set<ExpenseReport>().FirstOrDefaultAsync(r => r.Id == request.ExpenseReportId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Expense report", request.ExpenseReportId);
        await ExpenseReportService.EnsureOwnerAsync(db, workflow, report, cancellationToken);
        OdcWorkflow.EnsureEditable(report.Status, "expense report");
        var description = CardFeeds.Clean(request.Description, 500) ?? charge.Merchant;
        var wbs = await workflow.ValidateLineAsync(db, request.WbsElementId, charge.TransactionDate, charge.Amount, description,
            request.IsUnallowable, request.UnallowableReason, report.EmployeeId, cancellationToken);
        var line = new OdcEntry
        {
            ProjectId = wbs.ProjectId,
            WbsElementId = wbs.Id,
            Type = (OdcType)(int)request.Type,
            IncurredOn = charge.TransactionDate,
            Amount = charge.Amount,
            Description = description,
            Vendor = CardFeeds.Clean(charge.Merchant, 200),
            IsUnallowable = request.IsUnallowable,
            UnallowableReason = request.IsUnallowable ? request.UnallowableReason!.Trim() : null,
            SubmittedBy = workflow.Actor,
            CreatedAtUtc = workflow.UtcNow,
            EmployeeId = report.EmployeeId,
            ExpenseReportId = report.Id,
            Status = report.Status,
        };
        db.Set<OdcEntry>().Add(line);
        charge.OdcEntryId = line.Id;
        charge.AttachedAtUtc = workflow.UtcNow;
        charge.AttachedBy = workflow.Actor;
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, line.Id, "Created", after: OdcWorkflow.Snapshot(line), reason: $"Card charge ••{charge.CardLast4}");
        workflow.Audit.Record(db, CardProgramService.TransactionEntity, charge.Id, "Attached", null, new { line.ExpenseReportId, OdcEntryId = line.Id });
        await db.SaveChangesAsync(cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, line, cancellationToken);
    }

    public async Task DetachAsync(Guid transactionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await workflow.RequireCurrentEmployeeAsync(db, cancellationToken);
        var charge = await FindMineAsync(db, me.Id, transactionId, cancellationToken);
        var line = charge.OdcEntryId is { } id ? await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.Id == id, cancellationToken) : null;
        if (line is not null)
        {
            var report = await db.Set<ExpenseReport>().AsNoTracking().FirstAsync(r => r.Id == line.ExpenseReportId, cancellationToken);
            OdcWorkflow.EnsureEditable(report.Status, "expense report");
            workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, line.Id, "Deleted", before: OdcWorkflow.Snapshot(line), reason: "Card charge taken off the report");
            db.Set<OdcEntry>().Remove(line);
        }

        charge.OdcEntryId = null;
        charge.AttachedAtUtc = null;
        charge.AttachedBy = null;
        workflow.Audit.Record(db, CardProgramService.TransactionEntity, charge.Id, "Detached");
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CardLineDto>> ListReportCardLinesAsync(Guid expenseReportId, CancellationToken cancellationToken = default)
    {
        // Whoever may open the report (the employee, its approvers) may see which of its lines are card charges.
        var detail = await reports.GetReportAsync(expenseReportId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var cards = await TravelComparison.CardLinesAsync(db, detail.Lines.Select(l => l.Id).ToList(), cancellationToken);
        return cards.Select(c => new CardLineDto(c.Key, c.Value.TransactionId, c.Value.Last4, c.Value.Merchant, c.Value.Date, c.Value.Amount, c.Value.CompanyPaid)).ToList();
    }

    private static async Task<CardTransaction> FindMineAsync(IWorkbenchDbContext db, Guid employeeId, Guid transactionId, CancellationToken ct)
    {
        var charge = await db.Set<CardTransaction>().FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        return charge is not null && charge.EmployeeId == employeeId ? charge : throw WorkbenchNotFoundException.For("Card charge", transactionId);
    }
}

/// <summary>
/// Keeps card lines and trip links consistent through the expense report service: a card line's amount is locked to the
/// charge, and removing the line (or the report) puts the charge back in the cardholder's unassigned list.
/// </summary>
public sealed class TravelCardExpenseReportGuard(IExpenseReportService inner, IWorkbenchDbContextFactory dbFactory) : IExpenseReportService
{
    public Task<IReadOnlyList<ExpenseReportDto>> ListMyReportsAsync(CancellationToken cancellationToken = default) => inner.ListMyReportsAsync(cancellationToken);

    public Task<ExpenseReportDetailDto> GetReportAsync(Guid reportId, CancellationToken cancellationToken = default) => inner.GetReportAsync(reportId, cancellationToken);

    public Task<ExpenseReportDto> CreateReportAsync(SaveExpenseReportRequest request, CancellationToken cancellationToken = default) => inner.CreateReportAsync(request, cancellationToken);

    public Task<ExpenseReportDto> UpdateReportAsync(Guid reportId, SaveExpenseReportRequest request, CancellationToken cancellationToken = default) =>
        inner.UpdateReportAsync(reportId, request, cancellationToken);

    public async Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        List<Guid> lineIds;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            lineIds = await db.Set<OdcEntry>().AsNoTracking().Where(o => o.ExpenseReportId == reportId).Select(o => o.Id).ToListAsync(cancellationToken);
        }

        await inner.DeleteReportAsync(reportId, cancellationToken);
        await using var after = await dbFactory.CreateAsync(cancellationToken);
        await ReleaseAsync(after, lineIds, cancellationToken);
        foreach (var trip in await after.Set<TravelAuthorization>().Where(t => t.ExpenseReportId == reportId).ToListAsync(cancellationToken))
        {
            trip.ExpenseReportId = null;
        }

        await after.SaveChangesAsync(cancellationToken);
    }

    public Task<OdcEntryDto> AddLineAsync(Guid reportId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default) => inner.AddLineAsync(reportId, request, cancellationToken);

    public async Task<OdcEntryDto> UpdateLineAsync(Guid reportId, Guid lineId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var charge = await db.Set<CardTransaction>().AsNoTracking().FirstOrDefaultAsync(t => t.OdcEntryId == lineId, cancellationToken);
            if (charge is not null && request.Amount != charge.Amount)
            {
                throw new ArgumentException($"This line is a card charge (••{charge.CardLast4}), so its amount is locked at {charge.Amount:C}. "
                    + "Mark the part that isn't business as unallowable, or take the charge off the report.");
            }
        }

        return await inner.UpdateLineAsync(reportId, lineId, request, cancellationToken);
    }

    public async Task RemoveLineAsync(Guid reportId, Guid lineId, CancellationToken cancellationToken = default)
    {
        await inner.RemoveLineAsync(reportId, lineId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await ReleaseAsync(db, [lineId], cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<OdcEntryDto> AttachReceiptAsync(Guid reportId, Guid lineId, string fileName, Stream content, CancellationToken cancellationToken = default) =>
        inner.AttachReceiptAsync(reportId, lineId, fileName, content, cancellationToken);

    public Task<ExpenseReportDto> SubmitAsync(Guid reportId, CancellationToken cancellationToken = default) => inner.SubmitAsync(reportId, cancellationToken);

    private static async Task ReleaseAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> lineIds, CancellationToken ct)
    {
        if (lineIds.Count == 0)
        {
            return;
        }

        foreach (var charge in await db.Set<CardTransaction>().Where(t => t.OdcEntryId != null && lineIds.Contains(t.OdcEntryId.Value)).ToListAsync(ct))
        {
            (charge.OdcEntryId, charge.AttachedAtUtc, charge.AttachedBy) = (null, null, null);
        }
    }
}
