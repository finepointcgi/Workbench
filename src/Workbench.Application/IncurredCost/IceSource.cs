using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Application.IndirectRates;
using Workbench.Application.TotalTime;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.IncurredCost;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.IncurredCost;

// The facts an incurred cost package is built from, loaded once per generation and frozen with the package (as JSON) so
// the schedules of a reviewed, certified or submitted package never move when the books do.

/// <summary>Which part of the claim an expense account's balance belongs to.</summary>
internal static class IceCategory
{
    public const string DirectLabor = "DirectLabor";
    public const string DirectOdc = "DirectOdc";
    public const string Pool = "Pool";
    public const string Unallowable = "Unallowable";
    public const string Applied = "Applied";
    public const string Other = "Other";
}

/// <summary>
/// An expense account's net balance (debits − credits) for the fiscal year. <see cref="Flagged"/> is the part posted by
/// ODCs flagged unallowable to an account that isn't an unallowable one: it's excluded from the claim all the same.
/// </summary>
internal sealed record IceAccount(Guid Id, string Number, string Name, string Category, string? Pool, decimal Balance, decimal Flagged);

internal sealed record IcePool(string Pool, string Name, string Base, int CascadeOrder, decimal? Provisional, decimal? Actual, decimal Applied);

/// <summary>Allowable direct cost on one account for one project (null: posted without a project).</summary>
internal sealed record IceDirect(Guid? ProjectId, Guid AccountId, decimal Amount);

internal sealed record IceProject(Guid Id, string Code, string Name, Guid? ContractId);

internal sealed record IceContract(
    Guid Id, string Number, string Name, string Customer, string Type, string Status, bool IsPrime, DateOnly PeriodStart, DateOnly PeriodEnd,
    decimal Ceiling, decimal Funded, string? ProjectManager, string? AdminOffice, string? PayingOffice, string? CageCode, decimal? FeeRate);

internal sealed record IceFlaggedOdc(Guid Id, DateOnly Date, string Project, string Type, string Description, string? Reason, decimal Amount, string? PostedTo);

/// <summary>
/// Approved direct hours for one project and labor category, at cost (hours × the cost rate on the work date, per entry, by
/// the labor cost method). <paramref name="UncompensatedHours"/> are exempt staff's hours beyond their standard week (included in Hours).
/// </summary>
internal sealed record IceHours(Guid ProjectId, string LaborCategory, decimal Hours, decimal Cost, decimal UncompensatedHours = 0);

/// <summary>
/// Billing for one contract: invoices dated in the year (cost lines vs fee lines) and cumulatively through year end.
/// </summary>
internal sealed record IceBilling(Guid ContractId, decimal YearCost, decimal YearFee, decimal CumulativeCost, decimal CumulativeFee, int Invoices);

/// <summary>Labor billed on T&amp;M and labor-hour invoices dated in the year, per contract and labor category (empty: ODCs/materials).</summary>
internal sealed record IceTmBilled(Guid ContractId, string LaborCategory, decimal Hours, decimal Amount);

internal sealed record IceSubcontract(
    Guid Id, string Number, string Description, string Vendor, string? BusinessSize, string? PrimeContract, string Pricing, string Status,
    DateOnly PeriodStart, DateOnly PeriodEnd, decimal Ceiling, decimal Funded, decimal YearCost, decimal CumulativeCost);

internal sealed record IceSource
{
    public required int FiscalYear { get; init; }
    public required DateOnly Start { get; init; }
    public required DateOnly End { get; init; }
    public required IReadOnlyList<IceAccount> Accounts { get; init; }
    public required IReadOnlyList<IcePool> Pools { get; init; }
    public required IReadOnlyList<IceDirect> Direct { get; init; }
    public required IReadOnlyList<IceProject> Projects { get; init; }
    public required IReadOnlyList<IceContract> Contracts { get; init; }
    public required IReadOnlyList<IceFlaggedOdc> FlaggedOdcs { get; init; }
    public required IReadOnlyList<IceHours> Hours { get; init; }

    /// <summary>Uncompensated hours on every charge (direct, indirect and leave) in the year; null in packages built before total time accounting.</summary>
    public decimal? UncompensatedHoursAll { get; init; }

    /// <summary>The labor cost method(s) in effect during the year, as plain text (null in older packages).</summary>
    public string? LaborCostMethod { get; init; }
    public required IReadOnlyList<IceBilling> Billing { get; init; }
    public required IReadOnlyList<IceTmBilled> TmBilled { get; init; }
    public required IReadOnlyList<IceSubcontract> Subcontracts { get; init; }

    /// <summary>Total of the invoices (Invoice.Total) dated in the year that count as billed: the AR register.</summary>
    public required decimal InvoiceRegisterTotal { get; init; }

    /// <summary>Net debits on every expense account for the year, straight from the journal lines (the trial balance).</summary>
    public required decimal GlExpenseTotal { get; init; }

    /// <summary>Cumulative claimed cost per contract (id) from the prior fiscal year's package, when there is one.</summary>
    public required IReadOnlyDictionary<Guid, decimal> PriorCumulative { get; init; }

    public required bool PriorPackageFound { get; init; }
    public required int DraftJournalEntries { get; init; }
    public required int OpenPeriods { get; init; }
    public required int Periods { get; init; }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    public static IceSource Deserialize(string json) => JsonSerializer.Deserialize<IceSource>(json, Json)
        ?? throw new InvalidOperationException("The package's schedules can't be read.");
}

internal static class IceSourceLoader
{
    private static readonly InvoiceStatus[] Billed = [InvoiceStatus.Approved, InvoiceStatus.Sent, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid];

    private static readonly InvoiceLineKind[] FeeKinds = [InvoiceLineKind.Fee, InvoiceLineKind.FeeWithhold, InvoiceLineKind.WithholdRelease];

    public static async Task<IceSource> LoadAsync(IWorkbenchDbContext db, int fiscalYear, CancellationToken ct)
    {
        var year = await db.Set<FiscalYear>().AsNoTracking().FirstOrDefaultAsync(y => y.Year == fiscalYear, ct)
            ?? throw new ArgumentException($"FY{fiscalYear} isn't in the fiscal calendar.");
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().Where(p => p.FiscalYearId == year.Id).ToListAsync(ct);
        var periodIds = periods.Select(p => p.Id).ToList();

        var book = await RateBook.LoadAsync(db, ct);
        var map = await PoolAccountMap.LoadAsync(db, book.Setups, ct);
        var poolOf = map.Pools.SelectMany(kv => kv.Value.Select(id => (id, kv.Key))).ToDictionary(x => x.id, x => x.Key);

        var lines = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where e.Status != JournalStatus.Draft && periodIds.Contains(e.FiscalPeriodId)
                select new { e.Source, e.SourceId, l.AccountId, l.ProjectId, Net = l.Debit - l.Credit })
            .ToListAsync(ct);

        // ODCs flagged unallowable (FAR 31.205): whatever account they posted to, their lines are out of the claim.
        var flaggedOdcs = await (
                from o in db.Set<OdcEntry>().AsNoTracking()
                join p in db.Set<Project>().AsNoTracking() on o.ProjectId equals p.Id
                where o.IsUnallowable && o.Status == OdcStatus.Approved && o.IncurredOn >= year.Start && o.IncurredOn <= year.End
                select new { o.Id, o.IncurredOn, p.Code, o.Type, o.Description, o.UnallowableReason, o.Amount })
            .ToListAsync(ct);
        var allFlaggedIds = (await db.Set<OdcEntry>().AsNoTracking().Where(o => o.IsUnallowable).Select(o => o.Id).ToListAsync(ct)).ToHashSet();
        bool IsFlagged(JournalSource source, Guid? sourceId) => source == JournalSource.Odc && sourceId is { } id && allFlaggedIds.Contains(id);

        string CategoryOf(Account a) =>
            a.CostClass == CostClassification.Unallowable ? IceCategory.Unallowable
            : a.CostClass == CostClassification.AppliedBurden ? IceCategory.Applied
            : map.DirectLabor.Contains(a.Id) ? IceCategory.DirectLabor
            : map.DirectOdc.Contains(a.Id) ? IceCategory.DirectOdc
            : poolOf.ContainsKey(a.Id) ? IceCategory.Pool
            : IceCategory.Other;

        var expense = map.Accounts.Values.Where(a => a.Type == AccountType.Expense).ToDictionary(a => a.Id);
        var expenseLines = lines.Where(l => expense.ContainsKey(l.AccountId)).ToList();
        var balances = expenseLines.GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Net));
        var flagged = expenseLines.Where(l => IsFlagged(l.Source, l.SourceId)).GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Net));
        var accounts = expense.Values
            .Where(a => balances.ContainsKey(a.Id) || poolOf.ContainsKey(a.Id))
            .Select(a =>
            {
                var category = CategoryOf(a);
                return new IceAccount(a.Id, a.Number, a.Name, category, poolOf.TryGetValue(a.Id, out var pool) ? pool.ToString() : null,
                    balances.GetValueOrDefault(a.Id), category == IceCategory.Unallowable ? 0 : flagged.GetValueOrDefault(a.Id));
            })
            .OrderBy(a => a.Number, StringComparer.Ordinal)
            .ToList();

        var direct = expenseLines
            .Where(l => (map.DirectLabor.Contains(l.AccountId) || map.DirectOdc.Contains(l.AccountId)) && !IsFlagged(l.Source, l.SourceId))
            .GroupBy(l => (l.ProjectId, l.AccountId))
            .Select(g => new IceDirect(g.Key.ProjectId, g.Key.AccountId, g.Sum(l => l.Net)))
            .Where(d => d.Amount != 0)
            .ToList();

        var postedTo = expenseLines.Where(l => IsFlagged(l.Source, l.SourceId) && l.Net > 0)
            .GroupBy(l => l.SourceId!.Value)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.Select(l => expense[l.AccountId].Number).Distinct()));

        var pools = book.Setups.Select(s => new IcePool(s.Pool.ToString(), s.Name, s.Base.ToString(), s.CascadeOrder,
                book.Rate(s.Pool, fiscalYear, IndirectRateBasis.Provisional), book.Rate(s.Pool, fiscalYear, IndirectRateBasis.Actual),
                -lines.Where(l => l.AccountId == s.AbsorbedAccountId).Sum(l => l.Net)))
            .ToList();

        var projects = await db.Set<Project>().AsNoTracking().Select(p => new IceProject(p.Id, p.Code, p.Name, p.ContractId)).ToListAsync(ct);
        var contracts = await LoadContractsAsync(db, ct);
        var (hours, uncompensatedAll) = await LoadHoursAsync(db, year, ct);
        var laborMethod = await LaborMethodTextAsync(db, year, ct);
        var (billing, tm, register) = await LoadBillingAsync(db, year, ct);
        var subcontracts = await LoadSubcontractsAsync(db, year, ct);

        var prior = await db.Set<IncurredCostSubmission>().AsNoTracking().FirstOrDefaultAsync(s => s.FiscalYear == fiscalYear - 1, ct);
        var priorCumulative = new Dictionary<Guid, decimal>();
        if (prior is not null && prior.SchedulesJson.Length > 0)
        {
            var inputs = await db.Set<IncurredCostInput>().AsNoTracking().Where(i => i.SubmissionId == prior.Id).ToListAsync(ct);
            var built = IceBuilder.Build(IceSource.Deserialize(prior.SchedulesJson), IceInputs.From(inputs));
            foreach (var row in built.Schedules.First(s => s.Code == "I").Rows.Where(r => r.Kind == Contracts.IncurredCost.IceRowKind.Detail))
            {
                if (Guid.TryParse(row.Key, out var contractId))
                {
                    priorCumulative[contractId] = row.Number("cumulative");
                }
            }
        }

        return new IceSource
        {
            FiscalYear = fiscalYear,
            Start = year.Start,
            End = year.End,
            Accounts = accounts,
            Pools = pools,
            Direct = direct,
            UncompensatedHoursAll = uncompensatedAll,
            LaborCostMethod = laborMethod,
            Projects = projects,
            Contracts = contracts,
            FlaggedOdcs = flaggedOdcs.OrderBy(o => o.IncurredOn).ThenBy(o => o.Code, StringComparer.Ordinal)
                .Select(o => new IceFlaggedOdc(o.Id, o.IncurredOn, o.Code, o.Type.ToString(), o.Description, o.UnallowableReason, Money.Round(o.Amount), postedTo.GetValueOrDefault(o.Id)))
                .ToList(),
            Hours = hours,
            Billing = billing,
            TmBilled = tm,
            Subcontracts = subcontracts,
            InvoiceRegisterTotal = register,
            GlExpenseTotal = expenseLines.Sum(l => l.Net),
            PriorCumulative = priorCumulative,
            PriorPackageFound = prior is not null,
            DraftJournalEntries = await db.Set<JournalEntry>().AsNoTracking().CountAsync(e => e.Status == JournalStatus.Draft && periodIds.Contains(e.FiscalPeriodId), ct),
            OpenPeriods = periods.Count(p => p.Status != PeriodStatus.Closed),
            Periods = periods.Count,
        };
    }

    private static async Task<List<IceContract>> LoadContractsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var contracts = await db.Set<Contract>().AsNoTracking().ToListAsync(ct);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var settings = await db.Set<ContractBillingSettings>().AsNoTracking().ToListAsync(ct);
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.FeeRate != null).ToListAsync(ct);
        var managers = await (
                from p in db.Set<Project>().AsNoTracking()
                join e in db.Set<Employee>().AsNoTracking() on p.ProjectManagerId equals e.Id
                where p.ContractId != null
                select new { p.ContractId, e.DisplayName })
            .ToListAsync(ct);
        return contracts.Select(c =>
            {
                var s = settings.FirstOrDefault(x => x.ContractId == c.Id);
                var fee = clins.Where(x => x.ContractId == c.Id).Select(x => x.FeeRate).FirstOrDefault();
                var pm = string.Join(", ", managers.Where(m => m.ContractId == c.Id).Select(m => m.DisplayName).Distinct().Order(StringComparer.Ordinal));
                return new IceContract(c.Id, c.ContractNumber, c.Name, customers.GetValueOrDefault(c.CustomerId, ""), c.Type.ToString(), c.Status.ToString(), c.IsPrime,
                    c.PeriodStart, c.PeriodEnd, c.CeilingValue, c.FundedValue, pm.Length == 0 ? null : pm, s?.AdminOffice, s?.PayingOffice, s?.CageCode, fee);
            })
            .OrderBy(c => c.Number, StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<(List<IceHours> Hours, decimal UncompensatedAll)> LoadHoursAsync(IWorkbenchDbContext db, FiscalYear year, CancellationToken ct)
    {
        var entries = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                join w in db.Set<WbsElement>().AsNoTracking() on e.WbsElementId equals w.Id
                where t.Status == TimesheetStatus.Approved && w.Kind == ChargeKind.Direct && e.WorkDate >= year.Start && e.WorkDate <= year.End && e.Hours != 0
                select new { w.ProjectId, e.LaborCategoryId, t.EmployeeId, e.WorkDate, e.Hours, e.Id })
            .ToListAsync(ct);
        var sheetIds = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => t.Status == TimesheetStatus.Approved && t.PeriodEnd >= year.Start && t.PeriodStart <= year.End).Select(t => t.Id).ToListAsync(ct);
        var book = await LaborCostBook.LoadAsync(db, sheetIds, ct);
        var uncompensatedAll = book.Sheets.Values.SelectMany(s => s.Cost.Entries)
            .Where(e => e.WorkDate >= year.Start && e.WorkDate <= year.End).Sum(e => e.UncompensatedHours);

        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => $"{c.Code} {c.Name}", ct);
        var hours = entries
            .GroupBy(e => (e.ProjectId, e.LaborCategoryId))
            .Select(g => new IceHours(g.Key.ProjectId, categories.GetValueOrDefault(g.Key.LaborCategoryId, "(no labor category)"), g.Sum(e => e.Hours),
                g.Sum(e => book.Cost(e.Id)), g.Sum(e => book.UncompensatedHours(e.Id))))
            .ToList();
        return (hours, uncompensatedAll);
    }

    /// <summary>The labor cost method(s) in effect during the year, e.g. "Standard" or "Standard until 2026-04-06, then Hybrid".</summary>
    private static async Task<string> LaborMethodTextAsync(IWorkbenchDbContext db, FiscalYear year, CancellationToken ct)
    {
        var policies = await LaborCostPolicies.LoadAsync(db, ct);
        var parts = new List<string> { Contracts.TotalTime.LaborCostMethods.Label((Contracts.TotalTime.LaborCostMethodKind)(int)LaborCostPolicies.MethodOn(policies, year.Start)) };
        foreach (var p in policies.Where(p => p.EffectiveFrom > year.Start && p.EffectiveFrom <= year.End))
        {
            parts.Add($"from {p.EffectiveFrom:yyyy-MM-dd} {Contracts.TotalTime.LaborCostMethods.Label((Contracts.TotalTime.LaborCostMethodKind)(int)p.Method)}");
        }

        return string.Join(", then ", parts);
    }

    private static async Task<(List<IceBilling>, List<IceTmBilled>, decimal)> LoadBillingAsync(IWorkbenchDbContext db, FiscalYear year, CancellationToken ct)
    {
        var invoices = await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines)
            .Where(i => Billed.Contains(i.Status) && i.InvoiceDate <= year.End)
            .ToListAsync(ct);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => $"{c.Code} {c.Name}", ct);
        bool InYear(Invoice i) => i.InvoiceDate >= year.Start;

        var billing = invoices.GroupBy(i => i.ContractId).Select(g =>
            {
                var year_ = g.Where(InYear).SelectMany(i => i.Lines).ToList();
                var all = g.SelectMany(i => i.Lines).ToList();
                return new IceBilling(g.Key,
                    year_.Where(l => !FeeKinds.Contains(l.Kind)).Sum(l => l.Amount), year_.Where(l => FeeKinds.Contains(l.Kind)).Sum(l => l.Amount),
                    all.Where(l => !FeeKinds.Contains(l.Kind)).Sum(l => l.Amount), all.Where(l => FeeKinds.Contains(l.Kind)).Sum(l => l.Amount),
                    g.Count(InYear));
            })
            .ToList();

        var tm = invoices.Where(i => InYear(i) && i.Type == InvoiceType.TimeAndMaterials)
            .SelectMany(i => i.Lines.Select(l => (i.ContractId, Line: l)))
            .GroupBy(x => (x.ContractId, Category: x.Line.Kind == InvoiceLineKind.Labor ? categories.GetValueOrDefault(x.Line.LaborCategoryId ?? Guid.Empty, "(no labor category)") : ""))
            .Select(g => new IceTmBilled(g.Key.ContractId, g.Key.Category, g.Where(x => x.Line.Kind == InvoiceLineKind.Labor).Sum(x => x.Line.Quantity), g.Sum(x => x.Line.Amount)))
            .ToList();

        return (billing, tm, invoices.Where(InYear).Sum(i => i.Total));
    }

    private static async Task<List<IceSubcontract>> LoadSubcontractsAsync(IWorkbenchDbContext db, FiscalYear year, CancellationToken ct)
    {
        var subs = await db.Set<Subcontract>().AsNoTracking().ToListAsync(ct);
        if (subs.Count == 0)
        {
            return [];
        }

        var vendors = await db.Set<Vendor>().AsNoTracking().ToDictionaryAsync(v => v.Id, ct);
        var contracts = await db.Set<Contract>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct);
        var costs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.SubcontractId != null && o.Status == OdcStatus.Approved && !o.IsUnallowable && o.IncurredOn <= year.End)
            .Select(o => new { o.SubcontractId, o.IncurredOn, o.Amount })
            .ToListAsync(ct);
        return subs.Select(s =>
            {
                var own = costs.Where(c => c.SubcontractId == s.Id).ToList();
                var vendor = vendors.GetValueOrDefault(s.VendorId);
                return new IceSubcontract(s.Id, s.Number, s.Description, vendor?.Name ?? "", vendor?.BusinessSize,
                    s.PrimeContractId is { } pc ? contracts.GetValueOrDefault(pc) : null, s.Pricing.ToString(), s.Status.ToString(),
                    s.PeriodStart, s.PeriodEnd, s.CeilingValue, s.FundedValue,
                    Money.Round(own.Where(c => c.IncurredOn >= year.Start).Sum(c => c.Amount)), Money.Round(own.Sum(c => c.Amount)));
            })
            .OrderBy(s => s.Number, StringComparer.Ordinal)
            .ToList();
    }
}
