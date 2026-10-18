using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>A line the generator proposes, with the hours, ODCs or milestone it bills.</summary>
internal sealed record DraftLine(
    Guid? ClinId,
    InvoiceLineKind Kind,
    string Description,
    Guid? ProjectId,
    Guid? WbsElementId,
    Guid? LaborCategoryId,
    string? OdcType,
    decimal Quantity,
    decimal Rate,
    decimal Amount,
    IReadOnlyList<BilledSource> Sources);

internal sealed record Generation(IReadOnlyList<DraftLine> Lines, IReadOnlyList<string> Warnings, string? RateNote);

/// <summary>A contract with everything billing reads about it.</summary>
internal sealed record BillingContext(
    Contract Contract,
    Customer Customer,
    IReadOnlyList<Clin> Clins,
    IReadOnlyList<Project> Projects,
    IReadOnlyDictionary<Guid, WbsElement> Wbs,
    ContractBillingSettings? Settings,
    CustomerBillingProfile? Profile)
{
    public Clin? Clin(Guid? id) => id is null ? null : Clins.FirstOrDefault(c => c.Id == id);

    public int PaymentTermsDays => Settings?.PaymentTermsDays ?? Profile?.PaymentTermsDays ?? 30;

    public static async Task<BillingContext> LoadAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct)
    {
        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw WorkbenchNotFoundException.For("Contract", contractId);
        var customer = await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Id == contract.CustomerId, ct);
        var clins = (await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId).ToListAsync(ct))
            .OrderBy(c => c.Order).ThenBy(c => c.Number, StringComparer.Ordinal).ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => p.ContractId == contractId).ToListAsync(ct);
        var projectIds = projects.Select(p => p.Id).ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => projectIds.Contains(w.ProjectId)).ToDictionaryAsync(w => w.Id, ct);
        var settings = await db.Set<ContractBillingSettings>().AsNoTracking().FirstOrDefaultAsync(s => s.ContractId == contractId, ct);
        var profile = await db.Set<CustomerBillingProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == contract.CustomerId, ct);
        return new BillingContext(contract, customer, clins, projects, wbs, settings, profile);
    }
}

/// <summary>
/// Works out what an invoice bills. Each approved hour and allowable ODC on a CLIN's WBS is billed once (sources already
/// on another live invoice are skipped); cost-plus bills cumulative allowable cost through the period end less what was
/// billed before, so late approvals and corrections are picked up; FFP bills completed, unbilled milestones.
/// </summary>
public sealed class InvoiceGenerator(BillingCostSource costs)
{
    private static readonly InvoiceLineKind[] CostKinds = [InvoiceLineKind.Labor, InvoiceLineKind.Burden, InvoiceLineKind.Odc];

    public static bool IsTimeAndMaterials(ClinPricing p) => p is ClinPricing.TimeAndMaterials or ClinPricing.LaborHour;

    public static bool IsCostType(ClinPricing p) =>
        p is ClinPricing.CostPlusFixedFee or ClinPricing.CostPlusAwardFee or ClinPricing.CostPlusIncentiveFee or ClinPricing.CostNoFee;

    public static bool Bills(InvoiceType type, ClinPricing p) => type switch
    {
        InvoiceType.TimeAndMaterials => IsTimeAndMaterials(p),
        InvoiceType.CostPlus or InvoiceType.TrueUp => IsCostType(p),
        InvoiceType.FixedPriceMilestone => p == ClinPricing.FirmFixedPrice,
        _ => false,
    };

    /// <summary>
    /// The lines for <paramref name="type"/> over the period. <paramref name="excludeInvoiceId"/> is the draft being
    /// regenerated (its own sources don't count as billed). With <paramref name="approvedOnly"/>, only approved invoices
    /// count as billed (the unbilled accrual); otherwise drafts hold their sources too.
    /// </summary>
    internal async Task<Generation> GenerateAsync(
        IWorkbenchDbContext db, BillingContext ctx, InvoiceType type, DateOnly from, DateOnly to, bool releaseWithhold,
        Guid? excludeInvoiceId, bool approvedOnly, CancellationToken ct)
    {
        if (to < from)
        {
            throw new ArgumentException("The billing period ends before it starts.");
        }

        var prior = await PriorInvoicesAsync(db, ctx.Contract.Id, excludeInvoiceId, approvedOnly, ct);
        var warnings = new List<string>();
        var clins = ctx.Clins.Where(c => Bills(type, c.Pricing)).ToList();
        foreach (var unexercised in clins.Where(c => !c.IsExercised))
        {
            warnings.Add($"CLIN {unexercised.Number} is an unexercised option and isn't billed.");
        }

        clins = clins.Where(c => c.IsExercised).ToList();
        if (clins.Count == 0 && type != InvoiceType.CreditMemo)
        {
            warnings.Add($"Contract {ctx.Contract.ContractNumber} has no exercised {Label(type)} CLINs.");
            return new Generation([], warnings, null);
        }

        var result = type switch
        {
            InvoiceType.TimeAndMaterials => await TimeAndMaterialsAsync(db, ctx, clins, from, to, prior, warnings, ct),
            InvoiceType.CostPlus => await CostAsync(db, ctx, clins, InvoiceType.CostPlus, from, to, releaseWithhold, prior, warnings, ct),
            InvoiceType.TrueUp => await CostAsync(db, ctx, clins, InvoiceType.TrueUp, from, to, false, prior, warnings, ct),
            InvoiceType.FixedPriceMilestone => await MilestonesAsync(db, ctx, clins, to, excludeInvoiceId, warnings, ct),
            _ => throw new ArgumentException("Credit memos are created from an approved invoice."),
        };
        return result with { Lines = Order(ctx, result.Lines) };
    }

    /// <summary>Live invoices of the contract with their lines and sources (not void, not the excluded draft).</summary>
    internal static async Task<List<Invoice>> PriorInvoicesAsync(IWorkbenchDbContext db, Guid contractId, Guid? excludeInvoiceId, bool approvedOnly, CancellationToken ct)
    {
        var invoices = await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).ThenInclude(l => l.Sources)
            .Where(i => i.ContractId == contractId && i.Status != InvoiceStatus.Void && i.Id != excludeInvoiceId)
            .ToListAsync(ct);
        return invoices.Where(i => !approvedOnly || i.IsBilled).ToList();
    }

    private static async Task<Generation> TimeAndMaterialsAsync(
        IWorkbenchDbContext db, BillingContext ctx, List<Clin> clins, DateOnly from, DateOnly to, List<Invoice> prior, List<string> warnings, CancellationToken ct)
    {
        var clinIds = clins.Select(c => c.Id).ToHashSet();
        var clinOfWbs = ctx.Wbs.Values.Where(w => w.ClinId is { } c && clinIds.Contains(c)).ToDictionary(w => w.Id, w => w.ClinId!.Value);
        var wbsIds = clinOfWbs.Keys.ToList();
        var billed = prior.SelectMany(i => i.Lines).SelectMany(l => l.Sources).Where(s => s.ReleasedAtUtc is null)
            .Select(s => (s.Kind, s.SourceId)).ToHashSet();

        var entries = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Approved && wbsIds.Contains(e.WbsElementId) && e.WorkDate <= to && e.Hours != 0
                select new { e.Id, e.WbsElementId, e.LaborCategoryId, e.WorkDate, e.Hours, t.EmployeeId })
            .ToListAsync(ct);
        var unbilled = entries.Where(e => !billed.Contains((BilledSourceKind.TimeEntry, e.Id))).ToList();

        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<ContractLaborRate>().AsNoTracking().Where(r => r.ContractId == ctx.Contract.Id).ToListAsync(ct))
        {
            rates.Add(r.LaborCategoryId, r.EffectiveFrom, r.HourlyBillRate);
        }

        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        string CategoryLabel(Guid id) => categories.TryGetValue(id, out var c) ? $"{c.Code} {c.Name}" : "Unknown labor category";

        var lines = new List<DraftLine>();
        var priced = unbilled.Select(e => (Entry: e, Rate: rates.Find(e.LaborCategoryId, e.WorkDate))).ToList();
        foreach (var missing in priced.Where(p => p.Rate is null).GroupBy(p => p.Entry.LaborCategoryId))
        {
            warnings.Add($"No bill rate for {CategoryLabel(missing.Key)} on {missing.Min(m => m.Entry.WorkDate):yyyy-MM-dd}: " +
                $"{missing.Sum(m => m.Entry.Hours):0.##} h not billed. Add the rate on the contract.");
        }

        var earlier = priced.Where(p => p.Rate is not null && p.Entry.WorkDate < from).ToList();
        if (earlier.Count > 0)
        {
            warnings.Add($"Includes {earlier.Sum(p => p.Entry.Hours):0.##} h dated before {from:yyyy-MM-dd} that weren't billed before (late approvals).");
        }

        foreach (var g in priced.Where(p => p.Rate is not null)
                     .GroupBy(p => (Clin: clinOfWbs[p.Entry.WbsElementId], p.Entry.LaborCategoryId, Rate: p.Rate!.Value)))
        {
            var hours = g.Sum(p => p.Entry.Hours);
            var projects = g.Select(p => ctx.Wbs[p.Entry.WbsElementId].ProjectId).Distinct().ToList();
            var wbs = g.Select(p => p.Entry.WbsElementId).Distinct().ToList();
            var sources = g.Select(p => new BilledSource
            {
                Kind = BilledSourceKind.TimeEntry,
                SourceId = p.Entry.Id,
                Date = p.Entry.WorkDate,
                EmployeeId = p.Entry.EmployeeId,
                LaborCategoryId = p.Entry.LaborCategoryId,
                WbsElementId = p.Entry.WbsElementId,
                Quantity = p.Entry.Hours,
                Rate = g.Key.Rate,
                Amount = Money.Round(p.Entry.Hours * g.Key.Rate),
            }).ToList();
            lines.Add(new DraftLine(g.Key.Clin, InvoiceLineKind.Labor, CategoryLabel(g.Key.LaborCategoryId),
                projects.Count == 1 ? projects[0] : null, wbs.Count == 1 ? wbs[0] : null, g.Key.LaborCategoryId, null,
                hours, g.Key.Rate, Money.Round(hours * g.Key.Rate), sources));
        }

        var odcs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => wbsIds.Contains(o.WbsElementId) && o.Status == OdcStatus.Approved && o.IncurredOn <= to)
            .ToListAsync(ct);
        var unallowable = odcs.Where(o => o.IsUnallowable && o.IncurredOn >= from).ToList();
        if (unallowable.Count > 0)
        {
            warnings.Add($"{unallowable.Count} unallowable ODC(s) excluded ({unallowable.Sum(o => o.Amount):C}).");
        }

        var billableOdcs = odcs.Where(o => !o.IsUnallowable && !billed.Contains((BilledSourceKind.Odc, o.Id))).ToList();
        foreach (var g in billableOdcs.GroupBy(o => (Clin: clinOfWbs[o.WbsElementId], o.Type)))
        {
            var amount = g.Sum(o => o.Amount);
            var projects = g.Select(o => o.ProjectId).Distinct().ToList();
            var wbs = g.Select(o => o.WbsElementId).Distinct().ToList();
            lines.Add(new DraftLine(g.Key.Clin, InvoiceLineKind.Odc, $"{OdcLabel(g.Key.Type)} (at cost)",
                projects.Count == 1 ? projects[0] : null, wbs.Count == 1 ? wbs[0] : null, null, g.Key.Type.ToString(), 1, amount, amount,
                g.Select(o => new BilledSource
                {
                    Kind = BilledSourceKind.Odc,
                    SourceId = o.Id,
                    Date = o.IncurredOn,
                    EmployeeId = o.EmployeeId,
                    WbsElementId = o.WbsElementId,
                    Quantity = 1,
                    Rate = o.Amount,
                    Amount = o.Amount,
                    Description = o.Description + (o.Vendor is null ? "" : $" ({o.Vendor})"),
                }).ToList()));

            if (g.Key.Type == OdcType.Materials && ctx.Settings?.MaterialHandlingRate is > 0 and var mh)
            {
                lines.Add(new DraftLine(g.Key.Clin, InvoiceLineKind.MaterialHandling, $"Material handling {mh:P2}",
                    projects.Count == 1 ? projects[0] : null, wbs.Count == 1 ? wbs[0] : null, null, null, 1, mh, Money.Round(amount * mh), []));
            }
        }

        return new Generation(lines, warnings, null);
    }

    private async Task<Generation> CostAsync(
        IWorkbenchDbContext db, BillingContext ctx, List<Clin> clins, InvoiceType type, DateOnly from, DateOnly to, bool releaseWithhold,
        List<Invoice> prior, List<string> warnings, CancellationToken ct)
    {
        var lines = new List<DraftLine>();
        if (type == InvoiceType.TrueUp && !costs.HasIndirectRates)
        {
            warnings.Add("A true-up needs actual indirect rates, and indirect rates aren't configured.");
            return new Generation(lines, warnings, BillingCostSource.NotConfiguredNote);
        }

        // Cost-plus bills cumulative cost less billed cost, over a window that covers every earlier cost-plus invoice.
        // A true-up compares the year's cost at actual rates with what the year's invoices billed.
        var priorCost = prior.Where(i => type == InvoiceType.CostPlus
                ? i.Type == InvoiceType.CostPlus
                : (i.Type is InvoiceType.CostPlus or InvoiceType.TrueUp) && i.PeriodFrom >= from && i.PeriodTo <= to)
            .ToList();
        var windowFrom = type == InvoiceType.TrueUp
            ? from
            : new[] { from, ctx.Contract.PeriodStart }.Concat(priorCost.Select(i => i.PeriodFrom)).Min();
        var basis = type == InvoiceType.TrueUp ? RateBasis.Actual : RateBasis.Provisional;

        var clinIds = clins.Select(c => c.Id).ToHashSet();
        var cost = new List<BurdenedCostLine>();
        var rates = new List<AppliedRates>();
        foreach (var project in ctx.Projects)
        {
            var result = await costs.GetProjectCostAsync(db, project.Id, windowFrom, to, basis, ct);
            cost.AddRange(result.Lines.Select(l => l with { ClinId = l.ClinId ?? ctx.Wbs.GetValueOrDefault(l.WbsElementId)?.ClinId }));
            rates.AddRange(result.Rates);
        }

        var rateNote = BillingCostSource.RateNote(new BurdenedCostResult([], rates.Distinct().ToList()), costs.HasIndirectRates);
        cost = cost.Where(l => l.ClinId is { } c && clinIds.Contains(c)).ToList();
        var unallowable = cost.Sum(l => l.UnallowableExcluded);
        if (unallowable > 0)
        {
            warnings.Add($"Unallowable cost excluded: {unallowable:C}.");
        }

        var billedLines = priorCost.SelectMany(i => i.Lines).Where(l => l.WbsElementId is not null && CostKinds.Contains(l.Kind)).ToList();
        var wbsIds = cost.Select(l => l.WbsElementId)
            .Concat(billedLines.Where(l => l.ClinId is { } c && clinIds.Contains(c)).Select(l => l.WbsElementId!.Value))
            .Distinct()
            .OrderBy(id => ctx.Wbs.GetValueOrDefault(id)?.Code, StringComparer.Ordinal)
            .ToList();
        foreach (var wbsId in wbsIds)
        {
            var element = ctx.Wbs.GetValueOrDefault(wbsId);
            var here = cost.Where(l => l.WbsElementId == wbsId).ToList();
            var billedHere = billedLines.Where(l => l.WbsElementId == wbsId).ToList();
            var clinId = here.FirstOrDefault()?.ClinId ?? billedHere.First().ClinId;
            decimal Billed(InvoiceLineKind kind) => billedHere.Where(l => l.Kind == kind).Sum(l => l.Amount);
            var labor = here.Sum(l => l.DirectLabor) - Billed(InvoiceLineKind.Labor);
            var hours = here.Sum(l => l.DirectLaborHours) - billedHere.Where(l => l.Kind == InvoiceLineKind.Labor).Sum(l => l.Quantity);
            var burden = here.Sum(l => l.Fringe + l.Overhead + l.GeneralAndAdministrative) - Billed(InvoiceLineKind.Burden);
            var odc = here.Sum(l => l.DirectOdc) - Billed(InvoiceLineKind.Odc);
            var label = element is null ? "WBS" : $"{element.Code} {element.Name}";
            var prefix = type == InvoiceType.TrueUp ? "True-up: " : "";
            if (labor != 0)
            {
                lines.Add(new DraftLine(clinId, InvoiceLineKind.Labor, $"{prefix}Direct labor, {label}", element?.ProjectId, wbsId, null, null, hours, 0, labor, []));
            }

            if (burden != 0)
            {
                lines.Add(new DraftLine(clinId, InvoiceLineKind.Burden, $"{prefix}Indirect cost (fringe, overhead, G&A), {label}", element?.ProjectId, wbsId, null, null, 1, 0, burden, []));
            }

            if (odc != 0)
            {
                lines.Add(new DraftLine(clinId, InvoiceLineKind.Odc, $"{prefix}Direct ODCs, {label}", element?.ProjectId, wbsId, null, null, 1, 0, odc, []));
            }
        }

        if (type == InvoiceType.CostPlus && lines.Count > 0 && windowFrom < from)
        {
            warnings.Add($"Cost is billed cumulatively from {windowFrom:yyyy-MM-dd}: cost approved late for earlier periods is included.");
        }

        // Fee on cost, less the withhold (none on the final invoice, which releases what was withheld).
        var settings = ctx.Settings;
        var billedInvoices = prior.Where(i => i.IsBilled).ToList();
        foreach (var clin in clins)
        {
            var costHere = lines.Where(l => l.ClinId == clin.Id).Sum(l => l.Amount);
            if (clin.Pricing != ClinPricing.CostNoFee && clin.FeeRate is > 0 and var feeRate && costHere != 0)
            {
                var fee = Money.Round(costHere * feeRate);
                lines.Add(new DraftLine(clin.Id, InvoiceLineKind.Fee, $"{(type == InvoiceType.TrueUp ? "True-up: fee" : "Fee")} {feeRate:P2} on {costHere:N2}",
                    null, null, null, null, 1, feeRate, fee, []));
                if (!releaseWithhold && fee > 0 && settings?.FeeWithholdRate is > 0 and var withholdRate)
                {
                    var withhold = Money.Round(fee * withholdRate);
                    if (settings.FeeWithholdCap is { } cap)
                    {
                        var held = WithheldToDate(billedInvoices, null);
                        withhold = Math.Max(0, Math.Min(withhold, cap - held));
                    }

                    if (withhold > 0)
                    {
                        lines.Add(new DraftLine(clin.Id, InvoiceLineKind.FeeWithhold, $"Fee withheld {withholdRate:P0} until final invoice",
                            null, null, null, null, 1, withholdRate, -withhold, []));
                    }
                }
            }

            if (releaseWithhold)
            {
                var held = WithheldToDate(billedInvoices, clin.Id);
                if (held > 0)
                {
                    lines.Add(new DraftLine(clin.Id, InvoiceLineKind.WithholdRelease, "Release of fee withheld (final invoice)", null, null, null, null, 1, held, held, []));
                }
            }
        }

        return new Generation(lines, warnings, rateNote);
    }

    /// <summary>Fee withheld on billed invoices less what has been released (one CLIN, or all with null).</summary>
    internal static decimal WithheldToDate(IEnumerable<Invoice> billed, Guid? clinId)
    {
        var lines = billed.SelectMany(i => i.Lines).Where(l => clinId is null || l.ClinId == clinId).ToList();
        return -lines.Where(l => l.Kind == InvoiceLineKind.FeeWithhold).Sum(l => l.Amount) - lines.Where(l => l.Kind == InvoiceLineKind.WithholdRelease).Sum(l => l.Amount);
    }

    private static async Task<Generation> MilestonesAsync(
        IWorkbenchDbContext db, BillingContext ctx, List<Clin> clins, DateOnly to, Guid? excludeInvoiceId, List<string> warnings, CancellationToken ct)
    {
        var clinIds = clins.Select(c => c.Id).ToList();
        var milestones = await db.Set<BillingMilestone>().AsNoTracking()
            .Where(m => m.ContractId == ctx.Contract.Id && clinIds.Contains(m.ClinId))
            .ToListAsync(ct);
        foreach (var late in milestones.Where(m => m.CompletedDate is null && m.PlannedDate <= to))
        {
            warnings.Add($"Milestone \"{late.Description}\" was planned for {late.PlannedDate:yyyy-MM-dd} but isn't complete, so it isn't billed.");
        }

        var lines = milestones
            .Where(m => m.CompletedDate is { } done && done <= to && (m.InvoiceId is null || m.InvoiceId == excludeInvoiceId))
            .OrderBy(m => m.Order).ThenBy(m => m.PlannedDate)
            .Select(m => new DraftLine(m.ClinId, InvoiceLineKind.Milestone, $"Milestone: {m.Description} (completed {m.CompletedDate:yyyy-MM-dd})",
                null, null, null, null, 1, m.Amount, m.Amount,
                [new BilledSource { Kind = BilledSourceKind.Milestone, SourceId = m.Id, Date = m.CompletedDate!.Value, Quantity = 1, Rate = m.Amount, Amount = m.Amount, Description = m.Description }]))
            .ToList();
        return new Generation(lines, warnings, null);
    }

    /// <summary>
    /// The funding check for each CLIN on the invoice: billed on approved invoices (credit memos net) plus this invoice,
    /// against funded value and ceiling.
    /// </summary>
    internal static IReadOnlyList<ClinFundingCheckDto> CheckFunding(BillingContext ctx, IEnumerable<(Guid? ClinId, decimal Amount)> lines, IEnumerable<Invoice> prior)
    {
        var billed = prior.Where(i => i.IsBilled).SelectMany(i => i.Lines).ToList();
        return lines.Where(l => l.ClinId is not null).GroupBy(l => l.ClinId!.Value)
            .Select(g =>
            {
                var clin = ctx.Clin(g.Key);
                var previous = billed.Where(l => l.ClinId == g.Key).Sum(l => l.Amount);
                var mine = g.Sum(l => l.Amount);
                var funded = clin?.FundedValue ?? 0;
                var ceiling = clin?.CeilingValue ?? 0;
                var cumulative = previous + mine;
                var level = mine <= 0 ? FundingCheckLevel.Ok
                    : ceiling > 0 && cumulative > ceiling ? FundingCheckLevel.OverCeiling
                    : cumulative > funded ? FundingCheckLevel.OverFunded
                    : FundingCheckLevel.Ok;
                return new ClinFundingCheckDto(g.Key, clin?.Number ?? "?", clin?.Description ?? "", clin is null ? "" : BillingMapping.Label(clin.Pricing), funded, ceiling, previous, mine, level);
            })
            .OrderBy(f => ctx.Clin(f.ClinId)?.Order ?? int.MaxValue).ThenBy(f => f.ClinNumber, StringComparer.Ordinal)
            .ToList();
    }

    internal static IEnumerable<string> FundingWarnings(IEnumerable<ClinFundingCheckDto> funding) =>
        funding.Where(f => f.Level != FundingCheckLevel.Ok).Select(f => f.Level == FundingCheckLevel.OverCeiling
            ? $"CLIN {f.ClinNumber}: billed to date would be {f.CumulativeBilled:C}, past the ceiling of {f.CeilingValue:C}. This can't be approved."
            : $"CLIN {f.ClinNumber}: billed to date would be {f.CumulativeBilled:C}, past the funded value of {f.FundedValue:C}. Approval needs a Finance override with a reason.");

    private static List<DraftLine> Order(BillingContext ctx, IReadOnlyList<DraftLine> lines) =>
        lines.OrderBy(l => ctx.Clin(l.ClinId)?.Order ?? int.MaxValue)
            .ThenBy(l => ctx.Clin(l.ClinId)?.Number ?? "", StringComparer.Ordinal)
            .ThenBy(l => KindOrder(l.Kind))
            .ThenBy(l => l.Description, StringComparer.Ordinal)
            .ToList();

    internal static int KindOrder(InvoiceLineKind kind) => kind switch
    {
        InvoiceLineKind.Labor => 0,
        InvoiceLineKind.Burden => 1,
        InvoiceLineKind.Odc => 2,
        InvoiceLineKind.MaterialHandling => 3,
        InvoiceLineKind.Milestone => 4,
        InvoiceLineKind.Fee => 5,
        InvoiceLineKind.FeeWithhold => 6,
        InvoiceLineKind.WithholdRelease => 7,
        _ => 8,
    };

    public static string Label(InvoiceType type) => type switch
    {
        InvoiceType.TimeAndMaterials => "T&M / labor-hour",
        InvoiceType.CostPlus => "cost-plus",
        InvoiceType.FixedPriceMilestone => "FFP milestone",
        InvoiceType.TrueUp => "cost-plus (true-up)",
        _ => "credit memo",
    };

    private static string OdcLabel(OdcType type) => type switch
    {
        OdcType.Subcontract => "Subcontract",
        _ => type.ToString(),
    };
}
