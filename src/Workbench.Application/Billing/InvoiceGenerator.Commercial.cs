using Workbench.Application.Financials;
using Workbench.Contracts;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Microsoft.EntityFrameworkCore;

namespace Workbench.Application.Billing;

// The commercial half of the invoice generator: agreements with a billing plan and no CLINs. The lines come out as the same
// DraftLine records as government billing, so approval, the funding check (no CLIN, so nothing to check), the GL posting,
// revenue recognition and aging all work on them unchanged.
public sealed partial class InvoiceGenerator
{
    private async Task<Generation> CommercialAsync(
        IWorkbenchDbContext db, BillingContext ctx, InvoiceType type, DateOnly from, DateOnly to, List<Invoice> prior,
        Guid? excludeInvoiceId, List<string> warnings, CancellationToken ct)
    {
        var plan = ctx.Plan;
        var number = ctx.Contract.ContractNumber;
        switch (type)
        {
            case InvoiceType.TimeAndMaterials when plan is { Model: AgreementBillingModel.TimeAndMaterials }:
            {
                var tm = await TimeAndMaterialsAsync(db, ctx, [], from, to, prior, warnings, ct, withoutClins: true);
                return tm with { Lines = Order(ctx, tm.Lines) };
            }

            case InvoiceType.FixedPriceMilestone when plan is { Model: AgreementBillingModel.FixedFeeMilestones }:
                return await PlanMilestonesAsync(db, ctx, to, excludeInvoiceId, warnings, ct);

            case InvoiceType.Retainer when plan is { Model: AgreementBillingModel.Retainer }:
            case InvoiceType.Recurring when plan is { Model: AgreementBillingModel.Recurring }:
                return await PeriodChargeAsync(db, ctx, plan, type, from, to, prior, warnings, ct);

            case InvoiceType.TimeAndMaterials or InvoiceType.FixedPriceMilestone:
                warnings.Add($"Contract {number} has no CLINs and isn't set up for {Label(type)} billing. Set its billing model on the contract's billing page.");
                return new Generation([], warnings, null);

            default:
                throw new ArgumentException($"Contract {number} isn't set up for {Label(type)} billing. Set its billing model on the contract's billing page.");
        }
    }

    /// <summary>The single project of the agreement, when it has exactly one (the line's revenue then carries it).</summary>
    private static Guid? OnlyProject(BillingContext ctx) => ctx.Projects.Count == 1 ? ctx.Projects[0].Id : null;

    private static async Task<Generation> PlanMilestonesAsync(
        IWorkbenchDbContext db, BillingContext ctx, DateOnly to, Guid? excludeInvoiceId, List<string> warnings, CancellationToken ct)
    {
        var milestones = await db.Set<AgreementMilestone>().AsNoTracking().Where(m => m.ContractId == ctx.Contract.Id).ToListAsync(ct);
        foreach (var late in milestones.Where(m => m.CompletedDate is null && m.TargetDate <= to))
        {
            warnings.Add($"Milestone \"{late.Description}\" was due {late.TargetDate:yyyy-MM-dd} but isn't complete, so it isn't billed.");
        }

        var lines = milestones
            .Where(m => m.CompletedDate is { } done && done <= to && (m.InvoiceId is null || m.InvoiceId == excludeInvoiceId))
            .OrderBy(m => m.Order).ThenBy(m => m.TargetDate)
            .Select(m => new DraftLine(null, InvoiceLineKind.Milestone, $"{m.Description} (completed {m.CompletedDate:yyyy-MM-dd})",
                OnlyProject(ctx), null, null, null, 1, m.Amount, m.Amount,
                [new BilledSource { Kind = BilledSourceKind.PlanMilestone, SourceId = m.Id, Date = m.CompletedDate!.Value, Quantity = 1, Rate = m.Amount, Amount = m.Amount, Description = m.Description }]))
            .ToList();
        return new Generation(lines, warnings, null);
    }

    /// <summary>One retainer or recurring period: the plan's amount (and the retainer's overage), once per period.</summary>
    private static async Task<Generation> PeriodChargeAsync(
        IWorkbenchDbContext db, BillingContext ctx, AgreementBillingPlan plan, InvoiceType type, DateOnly from, DateOnly to,
        List<Invoice> prior, List<string> warnings, CancellationToken ct)
    {
        var period = AgreementPeriods.Find(plan, from);
        if (period is not { } p || p.To != to)
        {
            var first = AgreementPeriods.Period(plan, 0);
            throw new ArgumentException(
                $"This agreement bills each {AgreementPeriods.Label(plan.Cadence)} from {plan.StartDate:yyyy-MM-dd} (the first period is {first.From:yyyy-MM-dd} to {first.To:yyyy-MM-dd}). " +
                $"{from:yyyy-MM-dd} to {to:yyyy-MM-dd} isn't one of its periods.");
        }

        var already = prior.FirstOrDefault(i => i.Type == type && i.PeriodFrom == p.From);
        if (already is not null)
        {
            warnings.Add($"{AgreementPeriods.Describe(p.From, p.To)} is already billed on invoice {already.Number}.");
            return new Generation([], warnings, null);
        }

        var amount = AgreementPeriods.AmountFor(plan, p.Index);
        var kind = type == InvoiceType.Retainer ? InvoiceLineKind.Retainer : InvoiceLineKind.Recurring;
        var label = plan.Description ?? (type == InvoiceType.Retainer ? "Retainer" : "Recurring service");
        var text = $"{label}, {AgreementPeriods.Describe(p.From, p.To)}";
        if (amount != plan.Amount)
        {
            text += $" (prorated, ends {p.To:yyyy-MM-dd})";
        }

        var lines = new List<DraftLine>();
        RetainerHours? hours = null;
        if (type == InvoiceType.Retainer && plan.UnusedHours != UnusedHoursPolicy.Off && plan.IncludedHours is > 0)
        {
            hours = await AgreementPeriods.HoursAsync(db, ctx, plan, p.Index, ct);
            var carried = hours.Value.Carried > 0 ? $" ({hours.Value.Included:0.##} included + {hours.Value.Carried:0.##} carried over)" : "";
            text += $" · {hours.Value.Used:0.##} of {hours.Value.Available:0.##} hours used{carried}";
            if (hours.Value.Unused > 0)
            {
                warnings.Add($"{hours.Value.Unused:0.##} retainer hours were unused in {AgreementPeriods.Describe(p.From, p.To)}: " +
                    (plan.UnusedHours == UnusedHoursPolicy.Rollover ? "they carry into the next period." : "they expire."));
            }
        }

        lines.Add(new DraftLine(null, kind, text, OnlyProject(ctx), null, null, null, 1, amount, amount, []));
        if (hours is { Over: > 0 } over)
        {
            if (plan.OverageRate is > 0 and var rate)
            {
                lines.Add(new DraftLine(null, kind, $"Hours over the retainer, {AgreementPeriods.Describe(p.From, p.To)}", OnlyProject(ctx), null, null, null,
                    over.Over, rate, Money.Round(over.Over * rate), []));
            }
            else
            {
                warnings.Add($"{over.Over:0.##} hours were used over the retainer; the agreement has no overage rate, so they aren't billed.");
            }
        }

        return new Generation(lines, warnings, null);
    }
}
