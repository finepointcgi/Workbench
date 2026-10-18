using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Application.ContractDetail;
using Workbench.Application.Financials;
using Workbench.Contracts.Billing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Revenue;

namespace Workbench.Application.Revenue;

/// <summary>One revenue unit's result: the method's inputs, revenue recognized to date and billed to date, by revenue account.</summary>
internal sealed record UnitResult(
    Guid? ClinId,
    RevenueMethod Method,
    decimal CostToDate,
    decimal? EstimatedCost,
    decimal? EstimatedRevenue,
    decimal? PercentComplete,
    IReadOnlyDictionary<string, decimal> RecognizedByAccount,
    IReadOnlyDictionary<string, decimal> BilledByAccount,
    decimal ExpectedLoss,
    decimal LossProvision,
    string? Note)
{
    public decimal Recognized => RecognizedByAccount.Values.Sum();

    public decimal Billed => BilledByAccount.Values.Sum();
}

internal sealed record PlannedLine(string Account, decimal Debit, decimal Credit, string Memo);

/// <summary>What a revenue run computes for one project and period, and the entry that brings the ledger to it.</summary>
internal sealed record ProjectComputation(
    Project Project,
    Contract Contract,
    FiscalPeriod Period,
    IReadOnlyList<UnitResult> Units,
    decimal RecognizedToDate,
    decimal BilledToDate,
    decimal PeriodRevenue,
    decimal RevenueAdjustment,
    decimal Unbilled,
    decimal BillingsInExcess,
    decimal LossProvision,
    IReadOnlyList<PlannedLine> Entry,
    IReadOnlyList<string> Notes)
{
    public bool HasEntry => Entry.Count > 0;
}

/// <summary>
/// Computes a project's revenue position at a period end and the journal lines that move the ledger there.
/// <para>
/// Billing already credits revenue when it posts an invoice (by CLIN pricing: 4010 cost-plus, 4020 T&amp;M, 4030 labor
/// hour, 4040 FFP). The revenue run therefore works out, per revenue account, the revenue the project should carry to
/// date (each unit's method), subtracts what other sources booked (invoices, credit memos, accruals, manual entries) and
/// what earlier revenue runs posted, and posts the difference. The offset is the contract position: recognized less
/// billed is AR unbilled (1120) when positive and billings in excess of revenue (2300) when negative. Loss contracts
/// carry a provision (7030 / 2310) for the expected loss not yet in the books. Because every figure is cumulative and
/// read back from the ledger, a new estimate or late cost is a catch-up in the next run, and a run that changes nothing
/// posts nothing.
/// </para>
/// </summary>
public sealed class RevenueEngine(BillingCostSource costs)
{
    public const string ArUnbilled = BillingLedger.ArUnbilled;
    public const string BillingsInExcess = "2300";
    public const string LossProvisionLiability = "2310";
    public const string LossProvisionExpense = "7030";

    /// <summary>Accounts revenue runs credit; the same ones billing credits.</summary>
    public static readonly IReadOnlySet<string> RevenueAccounts = new HashSet<string>(StringComparer.Ordinal) { "4010", "4020", "4030", "4040" };

    private static readonly DateOnly Beginning = new(2000, 1, 1);

    internal async Task<ProjectComputation?> ComputeAsync(IWorkbenchDbContext db, Guid projectId, FiscalPeriod period, CancellationToken ct)
    {
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project?.ContractId is not { } contractId)
        {
            return null;
        }

        var contract = await db.Set<Contract>().AsNoTracking().FirstAsync(c => c.Id == contractId, ct);
        var end = period.End;
        var notes = new List<string>();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId).ToDictionaryAsync(c => c.Id, ct);
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToDictionaryAsync(w => w.Id, ct);
        var setups = await db.Set<RevenueSetup>().AsNoTracking().Where(s => s.ProjectId == projectId).ToListAsync(ct);
        var clinUnits = setups.Where(s => s.ClinId is not null).Select(s => s.ClinId!.Value).ToHashSet();
        var projectSetup = setups.FirstOrDefault(s => s.ClinId is null);
        var estimates = (await db.Set<RevenueEstimate>().AsNoTracking()
                .Where(e => e.ProjectId == projectId && e.Status == EstimateStatus.Approved && e.AsOf <= end).ToListAsync(ct))
            .GroupBy(e => e.ClinId)
            .ToDictionary(g => g.Key ?? Guid.Empty, g => g.OrderBy(e => e.AsOf).ThenBy(e => e.DecidedAtUtc).Last());

        string AccountFor(Guid? clinId) => BillingLedger.RevenueAccount(clinId is { } c && clins.TryGetValue(c, out var clin) ? clin.Pricing : null, contract.Type);
        Guid? UnitOf(Guid wbsId) => wbs.TryGetValue(wbsId, out var w) && w.ClinId is { } c && clinUnits.Contains(c) ? c : null;
        Guid? ClinOf(Guid wbsId) => wbs.TryGetValue(wbsId, out var w) ? w.ClinId : null;

        // What the ledger already holds for the project.
        var ledger = await LedgerAsync(db, projectId, end, ct);
        var priorRevenue = await RevenueThroughAsync(db, projectId, period.Start.AddDays(-1), ct);
        var billedByClin = await BilledByClinAsync(db, contractId, projectId, clinUnits, end, ct);

        // Allowable burdened cost through the period end, from the contract (or project) start.
        var from = project.Start is { } s && s < contract.PeriodStart ? s : contract.PeriodStart;
        from = from < Beginning ? Beginning : from;
        var cost = await costs.GetProjectCostAsync(db, projectId, from, end, RateBasis.Provisional, ct);
        if (!costs.HasIndirectRates)
        {
            notes.Add(BillingCostSource.NotConfiguredNote);
        }

        var units = new List<UnitResult>();
        foreach (var unit in new Guid?[] { null }.Concat(clinUnits.OrderBy(c => clins.GetValueOrDefault(c)?.Number, StringComparer.Ordinal).Select(c => (Guid?)c)))
        {
            var setup = unit is null ? projectSetup : setups.First(x => x.ClinId == unit);
            var method = setup?.Method ?? RevenueMethod.Billings;
            var unitCost = cost.Lines.Where(l => UnitOf(l.WbsElementId) == unit).ToList();
            var costToDate = unitCost.Sum(l => l.TotalCost);

            // Billed to date for the unit: a CLIN unit's invoices; the project unit gets the rest of the ledger's revenue.
            var billed = new Dictionary<string, decimal>(StringComparer.Ordinal);
            if (unit is { } clinId)
            {
                billed[AccountFor(clinId)] = billedByClin.GetValueOrDefault(clinId);
            }
            else
            {
                foreach (var (account, amount) in ledger.OtherRevenue)
                {
                    billed[account] = amount;
                }

                foreach (var c in clinUnits)
                {
                    billed[AccountFor(c)] = billed.GetValueOrDefault(AccountFor(c)) - billedByClin.GetValueOrDefault(c);
                }
            }

            var estimate = estimates.GetValueOrDefault(unit ?? Guid.Empty);
            string? note = null;
            decimal? pct = null;
            Dictionary<string, decimal> recognized;
            void FallBack(string why)
            {
                note = $"{why}; recognized as billed.";
                recognized = new Dictionary<string, decimal>(billed, StringComparer.Ordinal);
            }

            switch (method)
            {
                case RevenueMethod.CostToCost when estimate is not null:
                    var (p, revenue) = RevenueCalculator.CostToCost(costToDate, estimate.EstimatedCost, estimate.EstimatedRevenue);
                    pct = p;
                    recognized = new Dictionary<string, decimal>(StringComparer.Ordinal) { [AccountFor(unit)] = revenue };
                    break;
                case RevenueMethod.CostToCost:
                    recognized = [];
                    FallBack("No approved estimate at completion");
                    break;
                case RevenueMethod.CostPlusFee:
                    recognized = unitCost
                        .GroupBy(l => AccountFor(unit ?? ClinOf(l.WbsElementId)))
                        .ToDictionary(g => g.Key, g => g.Sum(l => RevenueCalculator.CostPlusFee(l.TotalCost,
                            setup?.FeeRate ?? (l.ClinId is { } c && clins.TryGetValue(c, out var clin) ? clin.FeeRate ?? 0 : 0))), StringComparer.Ordinal);
                    break;
                case RevenueMethod.TimeAndMaterialsIncurred:
                    recognized = await TimeAndMaterialsAsync(db, contract, wbs.Keys.Where(id => UnitOf(id) == unit).ToList(),
                        id => AccountFor(unit ?? ClinOf(id)), setup?.OdcMarkupRate ?? 0, end, notes, ct);
                    break;
                case RevenueMethod.FixedPriceMilestones:
                    var milestoneClins = unit is { } mc
                        ? new List<Guid> { mc }
                        : wbs.Values.Where(w => w.ClinId is { } c && !clinUnits.Contains(c)).Select(w => w.ClinId!.Value).Distinct().ToList();
                    if (milestoneClins.Count == 0)
                    {
                        milestoneClins = clins.Values.Where(c => c.Pricing == ClinPricing.FirmFixedPrice && !clinUnits.Contains(c.Id)).Select(c => c.Id).ToList();
                    }

                    recognized = (await db.Set<BillingMilestone>().AsNoTracking()
                            .Where(m => m.ContractId == contractId && milestoneClins.Contains(m.ClinId) && m.CompletedDate != null && m.CompletedDate <= end)
                            .Select(m => new { m.ClinId, m.Amount }).ToListAsync(ct))
                        .GroupBy(m => AccountFor(m.ClinId))
                        .ToDictionary(g => g.Key, g => g.Sum(m => m.Amount), StringComparer.Ordinal);
                    break;
                case RevenueMethod.FixedPriceUnits when setup?.UnitPrice is { } unitPrice:
                    var delivered = (await db.Set<RevenueDelivery>().AsNoTracking()
                        .Where(d => d.ProjectId == projectId && d.ClinId == unit && d.DeliveredOn <= end).Select(d => d.Quantity).ToListAsync(ct)).Sum();
                    recognized = new Dictionary<string, decimal>(StringComparer.Ordinal)
                    {
                        [AccountFor(unit)] = RevenueCalculator.UnitsDelivered(delivered, unitPrice, setup.TransactionPrice),
                    };
                    note = $"{delivered:0.####} unit(s) delivered at {unitPrice:N2}";
                    break;
                case RevenueMethod.FixedPriceUnits:
                    recognized = [];
                    FallBack("No unit price set");
                    break;
                default:
                    recognized = new Dictionary<string, decimal>(billed, StringComparer.Ordinal);
                    break;
            }

            var recognizedTotal = recognized.Values.Sum();
            var loss = estimate is null ? 0 : RevenueCalculator.ExpectedLoss(estimate.EstimatedCost, estimate.EstimatedRevenue);
            var provision = RevenueCalculator.LossProvision(loss, costToDate, recognizedTotal);
            if (loss > 0)
            {
                note = (note is null ? "" : note + " ") + $"Loss contract: expected loss {loss:N2}.";
            }

            units.Add(new UnitResult(unit, method, costToDate, estimate?.EstimatedCost, estimate?.EstimatedRevenue, pct, recognized, billed, loss, provision, note));
        }

        // Bring each revenue account to its target: target less other sources less earlier runs.
        var target = units.SelectMany(u => u.RecognizedByAccount).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value), StringComparer.Ordinal);
        var entry = new List<PlannedLine>();
        var memo = $"Revenue {project.Code} · {period.Name}";
        var adjustment = 0m;
        foreach (var account in target.Keys.Concat(ledger.OtherRevenue.Keys).Concat(ledger.RunRevenue.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var delta = Money.Round(target.GetValueOrDefault(account) - ledger.OtherRevenue.GetValueOrDefault(account) - ledger.RunRevenue.GetValueOrDefault(account));
            adjustment += delta;
            Add(entry, account, -delta, memo);
        }

        var recognizedToDate = target.Values.Sum();
        var billedToDate = ledger.OtherRevenue.Values.Sum();
        var (unbilled, inExcess) = RevenueCalculator.Position(recognizedToDate, billedToDate);
        Add(entry, ArUnbilled, unbilled - ledger.Unbilled, "Earned, not yet billed");
        Add(entry, BillingsInExcess, -(inExcess - ledger.BillingsInExcess), "Billed in excess of revenue");
        var provisionTotal = units.Sum(u => u.LossProvision);
        var provisionDelta = provisionTotal - ledger.LossProvision;
        Add(entry, LossProvisionExpense, provisionDelta, "Provision for contract loss");
        Add(entry, LossProvisionLiability, -provisionDelta, "Provision for contract loss");

        return new ProjectComputation(project, contract, period, units, recognizedToDate, billedToDate, Money.Round(recognizedToDate - priorRevenue),
            adjustment, unbilled, inExcess, provisionTotal, entry, notes.Distinct().ToList());
    }

    private static void Add(List<PlannedLine> lines, string account, decimal signedDebit, string memo)
    {
        signedDebit = Money.Round(signedDebit);
        if (signedDebit != 0)
        {
            lines.Add(new PlannedLine(account, Math.Max(signedDebit, 0), Math.Max(-signedDebit, 0), memo));
        }
    }

    /// <summary>What the ledger holds for a project through <paramref name="end"/>: revenue by account from other sources and from revenue runs, and the runs' balance sheet positions.</summary>
    internal sealed record LedgerPosition(
        IReadOnlyDictionary<string, decimal> OtherRevenue,
        IReadOnlyDictionary<string, decimal> RunRevenue,
        decimal Unbilled,
        decimal BillingsInExcess,
        decimal LossProvision);

    internal static async Task<LedgerPosition> LedgerAsync(IWorkbenchDbContext db, Guid projectId, DateOnly end, CancellationToken ct)
    {
        var tracked = RevenueAccounts.Concat([ArUnbilled, BillingsInExcess, LossProvisionLiability]).ToList();
        var lines = (await LinesAsync(db, null, end, tracked, ct)).Where(l => l.ProjectId == projectId).ToList();
        var other = lines.Where(l => RevenueAccounts.Contains(l.Account) && !l.IsRun)
            .GroupBy(l => l.Account).ToDictionary(g => g.Key, g => g.Sum(l => l.Credit - l.Debit), StringComparer.Ordinal);
        var runs = lines.Where(l => l.IsRun).ToList();
        var runRevenue = runs.Where(l => RevenueAccounts.Contains(l.Account))
            .GroupBy(l => l.Account).ToDictionary(g => g.Key, g => g.Sum(l => l.Credit - l.Debit), StringComparer.Ordinal);
        return new LedgerPosition(other, runRevenue,
            runs.Where(l => l.Account == ArUnbilled).Sum(l => l.Debit - l.Credit),
            runs.Where(l => l.Account == BillingsInExcess).Sum(l => l.Credit - l.Debit),
            runs.Where(l => l.Account == LossProvisionLiability).Sum(l => l.Credit - l.Debit));
    }

    /// <summary>
    /// A posted ledger line on one of the tracked accounts, with the project it belongs to. Invoice lines billed across
    /// projects (fees, CLIN-level ODC lines) carry no project segment; they belong to the invoice contract's primary
    /// project (its lowest project code). <see cref="IsRun"/> marks revenue-run entries and their reversals;
    /// <see cref="OriginalSource"/>/<see cref="OriginalSourceId"/> are the reversed entry's for a reversal.
    /// </summary>
    internal sealed record LedgerLine(
        Guid? ProjectId,
        string Account,
        DateOnly Date,
        JournalSource OriginalSource,
        Guid? OriginalSourceId,
        bool IsRun,
        decimal Debit,
        decimal Credit);

    internal static async Task<List<LedgerLine>> LinesAsync(IWorkbenchDbContext db, DateOnly? since, DateOnly end, IReadOnlyCollection<string> accounts, CancellationToken ct)
    {
        var numbers = accounts.ToList();
        var raw = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                join a in db.Set<Account>().AsNoTracking() on l.AccountId equals a.Id
                where e.Status != JournalStatus.Draft && e.Date <= end && (since == null || e.Date >= since) && numbers.Contains(a.Number)
                select new { l.ProjectId, a.Number, e.Date, e.Source, e.SourceId, e.ReversalOfId, l.Debit, l.Credit })
            .ToListAsync(ct);

        var reversedIds = raw.Where(l => l.ReversalOfId != null).Select(l => l.ReversalOfId!.Value).Distinct().ToList();
        var reversed = reversedIds.Count == 0
            ? []
            : await db.Set<JournalEntry>().AsNoTracking().Where(e => reversedIds.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => (e.Source, e.SourceId), ct);
        (JournalSource Source, Guid? SourceId) Original(JournalSource source, Guid? sourceId, Guid? reversalOf) =>
            reversalOf is { } r && reversed.TryGetValue(r, out var o) ? o : (source, sourceId);

        // Project-less billing lines: invoice → contract → primary project.
        var invoiceIds = raw.Where(l => l.ProjectId == null)
            .Select(l => Original(l.Source, l.SourceId, l.ReversalOfId))
            .Where(o => o.Source == JournalSource.Billing && o.SourceId != null).Select(o => o.SourceId!.Value).Distinct().ToList();
        var primary = new Dictionary<Guid, Guid>();
        if (invoiceIds.Count > 0)
        {
            var invoices = await db.Set<Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).Select(i => new { i.Id, i.ContractId }).ToListAsync(ct);
            var contractIds = invoices.Select(i => i.ContractId).Distinct().ToList();
            var projects = (await db.Set<Project>().AsNoTracking().Where(p => p.ContractId != null && contractIds.Contains(p.ContractId.Value))
                    .Select(p => new { p.Id, p.Code, ContractId = p.ContractId!.Value }).ToListAsync(ct))
                .GroupBy(p => p.ContractId).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Code, StringComparer.Ordinal).First().Id);
            foreach (var i in invoices)
            {
                if (projects.TryGetValue(i.ContractId, out var projectId))
                {
                    primary[i.Id] = projectId;
                }
            }
        }

        return raw.Select(l =>
        {
            var o = Original(l.Source, l.SourceId, l.ReversalOfId);
            var projectId = l.ProjectId ?? (o.Source == JournalSource.Billing && o.SourceId is { } inv && primary.TryGetValue(inv, out var p) ? p : null);
            return new LedgerLine(projectId, l.Number, l.Date, o.Source, o.SourceId, o.Source == JournalSource.Revenue, l.Debit, l.Credit);
        }).ToList();
    }

    /// <summary>Revenue booked for a project (every source) through <paramref name="end"/>.</summary>
    internal static async Task<decimal> RevenueThroughAsync(IWorkbenchDbContext db, Guid projectId, DateOnly end, CancellationToken ct) =>
        (await RevenueByProjectAsync(db, end, ct)).GetValueOrDefault(projectId);

    internal static async Task<Dictionary<Guid, decimal>> RevenueByProjectAsync(IWorkbenchDbContext db, DateOnly end, CancellationToken ct) =>
        (await LinesAsync(db, null, end, RevenueAccounts.ToList(), ct)).Where(l => l.ProjectId != null)
            .GroupBy(l => l.ProjectId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Credit - l.Debit));

    /// <summary>
    /// Billed to date on CLINs with their own revenue unit: approved invoice lines (credit memos negative) dated through
    /// <paramref name="end"/>, on the project or on no single project. Fee withholds and releases aren't revenue.
    /// </summary>
    private static async Task<Dictionary<Guid, decimal>> BilledByClinAsync(IWorkbenchDbContext db, Guid contractId, Guid projectId, IReadOnlySet<Guid> clinIds, DateOnly end, CancellationToken ct)
    {
        if (clinIds.Count == 0)
        {
            return [];
        }

        var ids = clinIds.ToList();
        var billedStatuses = new[] { InvoiceStatus.Approved, InvoiceStatus.Sent, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid };
        var lines = await (
                from l in db.Set<InvoiceLine>().AsNoTracking()
                join i in db.Set<Invoice>().AsNoTracking() on l.InvoiceId equals i.Id
                where i.ContractId == contractId && billedStatuses.Contains(i.Status) && i.InvoiceDate <= end
                      && l.ClinId != null && ids.Contains(l.ClinId.Value) && (l.ProjectId == null || l.ProjectId == projectId)
                      && l.Kind != InvoiceLineKind.FeeWithhold && l.Kind != InvoiceLineKind.WithholdRelease
                select new { ClinId = l.ClinId!.Value, l.Amount })
            .ToListAsync(ct);
        return lines.GroupBy(l => l.ClinId).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
    }

    /// <summary>T&amp;M as incurred: approved hours × the contract's bill rate for the labor category on the work date, plus allowable approved ODCs with the markup.</summary>
    private static async Task<Dictionary<string, decimal>> TimeAndMaterialsAsync(
        IWorkbenchDbContext db, Contract contract, List<Guid> wbsIds, Func<Guid, string> accountFor, decimal markup, DateOnly end, List<string> notes, CancellationToken ct)
    {
        var hours = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Approved && wbsIds.Contains(e.WbsElementId) && e.WorkDate <= end && e.Hours != 0
                select new { e.WbsElementId, e.LaborCategoryId, e.WorkDate, e.Hours })
            .ToListAsync(ct);
        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<ContractLaborRate>().AsNoTracking().Where(r => r.ContractId == contract.Id).ToListAsync(ct))
        {
            rates.Add(r.LaborCategoryId, r.EffectiveFrom, r.HourlyBillRate);
        }

        var priced = hours.Select(h => (h.WbsElementId, h.Hours, Rate: rates.Find(h.LaborCategoryId, h.WorkDate))).ToList();
        var unpriced = priced.Where(p => p.Rate is null).Sum(p => p.Hours);
        if (unpriced != 0)
        {
            notes.Add($"{unpriced:0.##} h on {contract.ContractNumber} have no bill rate and aren't recognized.");
        }

        var odcs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => wbsIds.Contains(o.WbsElementId) && o.Status == OdcStatus.Approved && !o.IsUnallowable && o.IncurredOn <= end)
            .Select(o => new { o.WbsElementId, o.Amount }).ToListAsync(ct);

        var result = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var g in priced.Where(p => p.Rate is not null).GroupBy(p => accountFor(p.WbsElementId)))
        {
            result[g.Key] = RevenueCalculator.TimeAndMaterials(g.Select(p => (p.Hours, p.Rate!.Value)), 0, 0);
        }

        foreach (var g in odcs.GroupBy(o => accountFor(o.WbsElementId)))
        {
            result[g.Key] = result.GetValueOrDefault(g.Key) + RevenueCalculator.TimeAndMaterials([], g.Sum(o => o.Amount), markup);
        }

        return result;
    }

    /// <summary>A contract's value and funding: active leaf CLINs when it has CLINs, else the typed-in totals.</summary>
    internal static (decimal Value, decimal Funded) ContractValue(Contract contract, IReadOnlyList<Clin> clins)
    {
        if (clins.Count == 0)
        {
            return (contract.CeilingValue, contract.FundedValue);
        }

        var leaves = clins.Where(c => !ClinRules.HasSlins(c, clins)).ToList();
        return (leaves.Where(c => c.IsActive()).Sum(c => c.CeilingValue), leaves.Sum(c => c.FundedValue));
    }
}
