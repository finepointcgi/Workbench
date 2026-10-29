using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// Where Finance starts invoicing: each contract with approved, unbilled time, expenses or completed milestones, with the
/// amount the draft invoice will hold. It runs the invoice generator itself (the draft's own calculation, with drafts
/// holding their sources), so the number here is the number on the draft; nothing is recomputed.
/// </summary>
public sealed class ReadyToBill(InvoiceGenerator generator)
{
    private static readonly InvoiceType[] Types = [InvoiceType.TimeAndMaterials, InvoiceType.CostPlus, InvoiceType.FixedPriceMilestone];

    internal async Task<ReadyToBillDto> GetAsync(IWorkbenchDbContext db, DateOnly today, CancellationToken ct)
    {
        var contractIds = await db.Set<Clin>().AsNoTracking().Select(c => c.ContractId).Distinct().ToListAsync(ct);
        var rows = new List<ReadyToBillRowDto>();
        foreach (var id in contractIds)
        {
            if (await RowAsync(db, id, today, ct) is { } row)
            {
                rows.Add(row);
            }
        }

        // Oldest work first; contracts whose only work lacks a rate (no date) last.
        return new ReadyToBillDto(today, rows
            .OrderBy(r => r.OldestWorkDate ?? DateOnly.MaxValue).ThenBy(r => r.ContractNumber, StringComparer.Ordinal).ToList());
    }

    private async Task<ReadyToBillRowDto?> RowAsync(IWorkbenchDbContext db, Guid contractId, DateOnly today, CancellationToken ct)
    {
        var ctx = await BillingContext.LoadAsync(db, contractId, ct);
        if (ctx.Contract.Status == ContractStatus.Closed)
        {
            return null;
        }

        var prior = await InvoiceGenerator.PriorInvoicesAsync(db, contractId, null, approvedOnly: true, ct);
        var from = ctx.Contract.PeriodStart < today ? ctx.Contract.PeriodStart : today;
        var found = new List<(InvoiceType Type, Generation Gen, DateOnly? Oldest)>();
        foreach (var type in Types.Where(t => ctx.Clins.Any(c => c.IsExercised && InvoiceGenerator.Bills(t, c.Pricing))))
        {
            // Exactly what PreviewAsync/GenerateDraftAsync build for a new draft: drafts hold their sources, so work on a draft
            // already isn't counted twice.
            var gen = await generator.GenerateAsync(db, ctx, type, from, today, false, null, approvedOnly: false, ct);
            gen = await ContractFinancingLedger.WithLiquidationAsync(db, ctx, type, gen, [], null, ct);
            var dated = gen.Lines.SelectMany(l => l.Sources).Select(s => (DateOnly?)s.Date).Min();
            // Cost-plus bills cumulative cost with no per-entry sources: it is unbilled since the last cost-plus invoice's period.
            if (type == InvoiceType.CostPlus && gen.Lines.Any(l => l.Amount != 0 && l.Sources.Count == 0))
            {
                var since = prior.Where(i => i.Type == InvoiceType.CostPlus).Select(i => (DateOnly?)i.PeriodTo.AddDays(1)).Max() ?? ctx.Contract.PeriodStart;
                dated = dated is { } d && d < since ? d : since;
            }

            found.Add((type, gen, dated));
        }

        var missingRates = found.SelectMany(f => f.Gen.Warnings).Where(w => w.StartsWith(InvoiceGenerator.NoBillRate, StringComparison.Ordinal)).ToList();
        var worked = found.Where(f => f.Gen.Lines.Any(l => l.Amount != 0)).ToList();
        if (worked.Count == 0 && missingRates.Count == 0)
        {
            return null;
        }

        var link = $"billing/contracts/{contractId}";
        var blockers = new List<ReadyToBillBlockerDto>();
        var draft = await db.Set<Invoice>().AsNoTracking().Where(i => i.ContractId == contractId && i.Status == InvoiceStatus.Draft)
            .OrderBy(i => i.CreatedAtUtc).Select(i => new { i.Id, i.Number }).FirstOrDefaultAsync(ct);
        if (draft is not null)
        {
            blockers.Add(new($"Invoice {draft.Number} is still a draft. Finish and approve it first; this list shows only the work not on it.", $"billing/invoices/{draft.Id}", "Open the draft"));
        }

        foreach (var warning in missingRates)
        {
            blockers.Add(new($"{warning.TrimEnd('.')}.", link, "Add the rate"));
        }

        if (string.IsNullOrWhiteSpace(ctx.Profile?.BillingAddress))
        {
            blockers.Add(new($"{ctx.Customer.Name} has no billing address yet. Add it in the contract's billing settings.", link, "Add the address"));
        }

        var funding = InvoiceGenerator.CheckFunding(ctx, worked.SelectMany(f => f.Gen.Lines).Select(l => (l.ClinId, l.Amount)), prior);
        foreach (var warning in InvoiceGenerator.FundingWarnings(funding))
        {
            blockers.Add(new(warning, link, "See funding"));
        }

        var oldest = found.Select(f => f.Oldest).Where(d => d is not null).Min();
        var start = oldest is { } o ? new DateOnly(o.Year, o.Month, 1) : (DateOnly?)null;
        var drafts = start is { } s
            ? worked.Select(f => new ReadyToBillDraftDto(f.Type.ToContract(), s, today, f.Gen.Lines.Sum(l => l.Amount))).ToList()
            : [];
        return new ReadyToBillRowDto(contractId, ctx.Contract.ContractNumber, ctx.Contract.Name, ctx.Customer.Id, ctx.Customer.Name,
            drafts.Sum(d => d.Amount), oldest, Covers(worked.SelectMany(f => f.Gen.Lines)), blockers, drafts);
    }

    private static string Covers(IEnumerable<DraftLine> lines)
    {
        var kinds = lines.Where(l => l.Amount != 0).Select(l => l.Kind).ToHashSet();
        var parts = new List<string>();
        if (kinds.Overlaps([InvoiceLineKind.Labor, InvoiceLineKind.Burden]))
        {
            parts.Add("time");
        }

        if (kinds.Overlaps([InvoiceLineKind.Odc, InvoiceLineKind.MaterialHandling]))
        {
            parts.Add("expenses");
        }

        if (kinds.Contains(InvoiceLineKind.Milestone))
        {
            parts.Add("milestones");
        }

        var text = string.Join(", ", parts);
        return text.Length == 0 ? "" : char.ToUpperInvariant(text[0]) + text[1..];
    }
}
