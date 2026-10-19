using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// Earned-but-unbilled revenue at a period's end: what a cost-plus or T&amp;M invoice through the period's last day would
/// bill, counting only approved invoices as billed (drafts aren't billed yet). Fee withholds are left out; they're
/// already in AR unbilled. FFP isn't accrued (it's earned at milestones).
/// </summary>
public sealed class UnbilledAccruals(InvoiceGenerator generator, BillingLedger ledger, AuditRecorder audit, IWorkbenchDbContextFactory? dbFactory = null)
{
    internal sealed record Accrual(FiscalPeriod Period, List<(BillingContext Ctx, DraftLine Line)> Lines, List<string> Notes);

    internal async Task<Accrual> ComputeAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct)
    {
        var contractIds = await db.Set<Clin>().AsNoTracking().Select(c => c.ContractId).Distinct().ToListAsync(ct);
        var perContract = new (List<(BillingContext, DraftLine)> Lines, List<string> Notes)[contractIds.Count];
        if (dbFactory is null)
        {
            for (var i = 0; i < contractIds.Count; i++)
            {
                perContract[i] = await ContractAsync(db, contractIds[i], period, ct);
            }
        }
        else
        {
            // Each contract is an invoice generation's worth of small queries: a few contracts at a time, each on its own context.
            await Parallel.ForEachAsync(Enumerable.Range(0, contractIds.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (i, token) =>
            {
                await using var own = await dbFactory.CreateAsync(token);
                perContract[i] = await ContractAsync(own, contractIds[i], period, token);
            });
        }

        var lines = perContract.SelectMany(c => c.Lines).ToList();
        var notes = perContract.SelectMany(c => c.Notes).Distinct().ToList();
        return new Accrual(period, lines, notes);
    }

    private async Task<(List<(BillingContext, DraftLine)> Lines, List<string> Notes)> ContractAsync(IWorkbenchDbContext db, Guid contractId, FiscalPeriod period, CancellationToken ct)
    {
        var lines = new List<(BillingContext, DraftLine)>();
        var notes = new List<string>();
        var ctx = await BillingContext.LoadAsync(db, contractId, ct);
        if (ctx.Contract.Status == ContractStatus.Closed)
        {
            return (lines, notes);
        }

        foreach (var type in new[] { InvoiceType.CostPlus, InvoiceType.TimeAndMaterials })
        {
            if (!ctx.Clins.Any(c => c.IsExercised && InvoiceGenerator.Bills(type, c.Pricing)))
            {
                continue;
            }

            var gen = await generator.GenerateAsync(db, ctx, type, period.Start, period.End, false, null, approvedOnly: true, ct);
            lines.AddRange(gen.Lines.Where(l => l.Kind is not (InvoiceLineKind.FeeWithhold or InvoiceLineKind.WithholdRelease) && l.Amount != 0).Select(l => (ctx, l)));
            if (gen.RateNote is { } note && !notes.Contains(note))
            {
                notes.Add(note);
            }
        }

        return (lines, notes);
    }

    internal async Task<UnbilledAccrualDto> GetAsync(IWorkbenchDbContext db, Guid fiscalPeriodId, CancellationToken ct)
    {
        var period = await FindPeriodAsync(db, fiscalPeriodId, ct);
        var posted = await db.Set<UnbilledAccrual>().AsNoTracking().FirstOrDefaultAsync(a => a.FiscalPeriodId == period.Id, ct);
        var accrual = await ComputeAsync(db, period, ct);
        return Dto(accrual, posted);
    }

    internal async Task<UnbilledAccrualDto> PostAsync(IWorkbenchDbContext db, Guid fiscalPeriodId, CancellationToken ct)
    {
        var period = await FindPeriodAsync(db, fiscalPeriodId, ct);
        if (await db.Set<UnbilledAccrual>().AsNoTracking().FirstOrDefaultAsync(a => a.FiscalPeriodId == period.Id, ct) is { } existing)
        {
            return Dto(await ComputeAsync(db, period, ct), existing);
        }

        var accrual = await ComputeAsync(db, period, ct);
        var amount = accrual.Lines.Sum(l => l.Line.Amount);
        if (amount <= 0)
        {
            throw new InvalidOperationException($"Nothing is earned and unbilled at the end of {period.Name}.");
        }

        var next = await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Start == period.End.AddDays(1), ct)
            ?? throw new InvalidOperationException($"There's no fiscal period after {period.Name} to reverse the accrual into. Add the next fiscal year first.");
        var row = new UnbilledAccrual { FiscalPeriodId = period.Id, Amount = amount, PostedBy = ledger.Actor, PostedAtUtc = ledger.UtcNow };
        var revenue = accrual.Lines.Select(x => (
            BillingLedger.RevenueAccount(x.Ctx.Clin(x.Line.ClinId)?.Pricing, x.Ctx.Contract.Type),
            x.Line.ProjectId,
            x.Line.WbsElementId,
            x.Line.Amount,
            (string?)$"Unbilled {x.Ctx.Contract.ContractNumber}"));
        var (entry, reversal) = await ledger.PostAccrualAsync(db, row.Id, period.End, next.Start, $"Unbilled revenue accrual · {period.Name}", revenue, ct);
        row.JournalEntryId = entry.Id;
        row.ReversalEntryId = reversal.Id;
        db.Set<UnbilledAccrual>().Add(row);
        audit.Record(db, nameof(UnbilledAccrual), row.Id, "Posted", after: new { Period = period.Name, amount, Entry = entry.Number, Reversal = reversal.Number });
        await db.SaveChangesAsync(ct);
        return Dto(accrual, row);
    }

    private static UnbilledAccrualDto Dto(Accrual a, UnbilledAccrual? posted)
    {
        var lines = a.Lines
            .GroupBy(x => (x.Ctx.Contract.Id, x.Ctx.Contract.ContractNumber, x.Line.ClinId))
            .Select(g =>
            {
                var clin = g.First().Ctx.Clin(g.Key.ClinId);
                return new UnbilledAccrualLineDto(g.Key.Id, g.Key.ContractNumber, clin?.Number,
                    clin is null ? "Not assigned to a CLIN" : $"{clin.Description} ({BillingMapping.Label(clin.Pricing)})", g.Sum(x => x.Line.Amount));
            })
            .OrderBy(l => l.ContractNumber, StringComparer.Ordinal).ThenBy(l => l.ClinNumber, StringComparer.Ordinal)
            .ToList();
        return new UnbilledAccrualDto(a.Period.Id, a.Period.Name, a.Period.End, lines, lines.Sum(l => l.Amount), posted is not null,
            posted?.JournalEntryId, posted?.ReversalEntryId, posted?.PostedBy, posted?.PostedAtUtc, a.Notes);
    }

    private static async Task<FiscalPeriod> FindPeriodAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Fiscal period", id);
}

/// <summary>The period-close checklist item for the (optional) unbilled revenue accrual. It informs; it never blocks the close.</summary>
public sealed class UnbilledAccrualCloseCheck(UnbilledAccruals accruals) : IPeriodCloseCheck
{
    public async Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct)
    {
        var posted = await db.Set<UnbilledAccrual>().AsNoTracking().FirstOrDefaultAsync(a => a.FiscalPeriodId == period.Id, ct);
        var accrual = await accruals.ComputeAsync(db, period, ct);
        var amount = accrual.Lines.Sum(l => l.Line.Amount);
        var summary = posted is not null
            ? $"Unbilled accrual of {posted.Amount:N2} posted (reverses next period)"
            : amount > 0
                ? $"Optional: {amount:N2} earned but unbilled; post the accrual from Billing › Unbilled accrual"
                : "Nothing earned and unbilled at period end";
        var details = accrual.Lines.GroupBy(x => x.Ctx.Contract.ContractNumber)
            .Select(g => $"{g.Key}: {g.Sum(x => x.Line.Amount):N2} unbilled").ToList();
        return new CloseCheckDto(CloseCheckKind.UnbilledAccrual, "Unbilled revenue accrual (optional)", true, 0, summary, details);
    }
}
