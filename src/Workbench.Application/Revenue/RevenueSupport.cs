using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Revenue;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Revenue;

namespace Workbench.Application.Revenue;

internal static class RevenueSupport
{
    public const string SetupEntity = nameof(RevenueSetup);
    public const string EstimateEntity = nameof(RevenueEstimate);
    public const string DeliveryEntity = nameof(RevenueDelivery);
    public const string RunEntity = nameof(RevenueRun);

    public static RevenueMethodKind ToContract(this RevenueMethod m) => (RevenueMethodKind)(int)m;
    public static RevenueMethod ToDomain(this RevenueMethodKind m) => (RevenueMethod)(int)m;
    public static EstimateState ToContract(this EstimateStatus s) => (EstimateState)(int)s;
    public static EstimateStatus ToDomain(this EstimateState s) => (EstimateStatus)(int)s;
    public static RevenueRunState ToContract(this RevenueRunStatus s) => (RevenueRunState)(int)s;

    public static string Label(RevenueMethod m) => m switch
    {
        RevenueMethod.Billings => "Revenue = billings",
        RevenueMethod.CostToCost => "Cost-to-cost % complete",
        RevenueMethod.TimeAndMaterialsBilled => "T&M as billed",
        RevenueMethod.TimeAndMaterialsIncurred => "T&M as incurred",
        RevenueMethod.CostPlusFee => "Cost plus fee",
        RevenueMethod.FixedPriceMilestones => "FFP milestones",
        RevenueMethod.FixedPriceUnits => "FFP units delivered",
        _ => m.ToString(),
    };

    private sealed record AccountDef(string Number, string Name, AccountType Type, string Parent, CostClassification Class, string Description);

    private static readonly AccountDef[] Accounts =
    [
        new(RevenueEngine.BillingsInExcess, "Billings in excess of revenue", AccountType.Liability, "2000", CostClassification.None,
            "Contract liabilities: billed ahead of revenue recognized (revenue runs)"),
        new(RevenueEngine.LossProvisionLiability, "Provision for contract losses", AccountType.Liability, "2000", CostClassification.None,
            "Expected losses on loss contracts not yet incurred (revenue runs)"),
        new(RevenueEngine.LossProvisionExpense, "Unallowable - provision for contract losses", AccountType.Expense, "7000", CostClassification.Unallowable,
            "FAR 31.205-23: losses on other contracts are unallowable"),
    ];

    /// <summary>Adds the revenue recognition accounts when a chart exists without them (charts installed earlier). Saves.</summary>
    public static async Task EnsureAccountsAsync(IWorkbenchDbContext db, DateTime utcNow, CancellationToken ct)
    {
        if (!await db.Set<Account>().AnyAsync(ct))
        {
            return;
        }

        var existing = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Number, ct);
        var missing = Accounts.Where(d => !existing.ContainsKey(d.Number)).ToList();
        foreach (var def in missing)
        {
            db.Set<Account>().Add(new Account
            {
                Number = def.Number,
                Name = def.Name,
                Type = def.Type,
                NormalBalance = Account.DefaultNormalBalance(def.Type),
                ParentId = existing.TryGetValue(def.Parent, out var parent) ? parent.Id : null,
                CostClass = def.Class,
                Description = def.Description,
                CreatedAtUtc = utcNow,
            });
        }

        if (missing.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    public static async Task<FiscalPeriod> FindPeriodAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<FiscalPeriod>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Fiscal period", id);

    public static async Task<Project> FindProjectAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Project", id);

    /// <summary>Names for building DTOs: projects, contracts, CLINs, periods and entry numbers.</summary>
    internal sealed class Names
    {
        public Dictionary<Guid, Project> Projects { get; private init; } = [];
        public Dictionary<Guid, Contract> Contracts { get; private init; } = [];
        public Dictionary<Guid, Clin> Clins { get; private init; } = [];
        public Dictionary<Guid, FiscalPeriod> Periods { get; private init; } = [];
        public Dictionary<Guid, string> Entries { get; private init; } = [];

        public static async Task<Names> LoadAsync(IWorkbenchDbContext db, IEnumerable<Guid> entryIds, CancellationToken ct)
        {
            var ids = entryIds.Distinct().ToList();
            return new Names
            {
                Projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, ct),
                Contracts = await db.Set<Contract>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct),
                Clins = await db.Set<Clin>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct),
                Periods = await db.Set<FiscalPeriod>().AsNoTracking().ToDictionaryAsync(p => p.Id, ct),
                Entries = ids.Count == 0 ? [] : await db.Set<JournalEntry>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct),
            };
        }

        public string? Clin(Guid? id) => id is { } c && Clins.TryGetValue(c, out var clin) ? clin.Number : null;

        public string? ContractNumber(Guid projectId) =>
            Projects.TryGetValue(projectId, out var p) && p.ContractId is { } c && Contracts.TryGetValue(c, out var contract) ? contract.ContractNumber : null;
    }

    public static RevenueSetupDto ToDto(this RevenueSetup s, Names names)
    {
        var project = names.Projects[s.ProjectId];
        var contract = project.ContractId is { } c ? names.Contracts.GetValueOrDefault(c) : null;
        return new RevenueSetupDto(s.Id, s.ProjectId, project.Code, project.Name, contract?.Id, contract?.ContractNumber, s.ClinId, names.Clin(s.ClinId),
            s.Method.ToContract(), s.FeeRate, s.OdcMarkupRate, s.TransactionPrice, s.UnitPrice, s.Notes, s.UpdatedBy, s.UpdatedAtUtc);
    }

    public static EstimateDto ToDto(this RevenueEstimate e, Names names) =>
        new(e.Id, e.ProjectId, names.Projects.GetValueOrDefault(e.ProjectId)?.Code ?? "", e.ClinId, names.Clin(e.ClinId), e.AsOf, e.EstimatedCost, e.EstimatedRevenue,
            e.Notes, e.Status.ToContract(), e.SubmittedBy, e.SubmittedAtUtc, e.DecidedBy, e.DecidedAtUtc, e.DecisionNote);

    public static RevenueDeliveryDto ToDto(this RevenueDelivery d, Names names) =>
        new(d.Id, d.ProjectId, d.ClinId, names.Clin(d.ClinId), d.DeliveredOn, d.Quantity, d.Description, d.RecordedBy, d.RecordedAtUtc);

    public static RevenueLineDto ToDto(this RevenueRunLine l, Names names) =>
        new(l.ClinId, names.Clin(l.ClinId), l.Method.ToContract(), l.CostToDate, l.EstimatedCost, l.EstimatedRevenue, l.PercentComplete,
            l.RecognizedToDate, l.BilledToDate, l.ExpectedLoss, l.LossProvision, l.Note);

    public static RevenueLineDto ToDto(this UnitResult u, Names names) =>
        new(u.ClinId, names.Clin(u.ClinId), u.Method.ToContract(), u.CostToDate, u.EstimatedCost, u.EstimatedRevenue, u.PercentComplete,
            u.Recognized, u.Billed, u.ExpectedLoss, u.LossProvision, u.Note);

    public static RevenueRunDto ToDto(this RevenueRun r, Names names)
    {
        var project = names.Projects.GetValueOrDefault(r.ProjectId);
        return new RevenueRunDto(r.Id, r.FiscalPeriodId, names.Periods.GetValueOrDefault(r.FiscalPeriodId)?.Name ?? "", r.ProjectId, project?.Code ?? "", project?.Name ?? "",
            r.Status.ToContract(), r.RecognizedToDate, r.PeriodRevenue, r.BilledToDate, r.RevenueAdjustment, r.UnbilledBalance, r.BillingsInExcessBalance, r.LossProvisionBalance,
            r.JournalEntryId, r.JournalEntryId is { } je ? names.Entries.GetValueOrDefault(je) : null,
            r.ReversalEntryId, r.ReversalEntryId is { } re ? names.Entries.GetValueOrDefault(re) : null,
            r.PostedBy, r.PostedAtUtc, r.ReversedBy, r.ReversedAtUtc, r.ReversalReason,
            r.Lines.OrderBy(l => l.ClinId is null ? "" : names.Clin(l.ClinId), StringComparer.Ordinal).Select(l => l.ToDto(names)).ToList());
    }

    public static async Task<List<RevenueRunDto>> RunDtosAsync(IWorkbenchDbContext db, IQueryable<RevenueRun> query, CancellationToken ct)
    {
        var runs = await query.AsNoTracking().Include(r => r.Lines).ToListAsync(ct);
        var names = await Names.LoadAsync(db, runs.SelectMany(r => new[] { r.JournalEntryId, r.ReversalEntryId }).OfType<Guid>(), ct);
        return runs
            .OrderByDescending(r => names.Periods.GetValueOrDefault(r.FiscalPeriodId)?.Start)
            .ThenBy(r => names.Projects.GetValueOrDefault(r.ProjectId)?.Code, StringComparer.Ordinal)
            .ThenByDescending(r => r.PostedAtUtc)
            .Select(r => r.ToDto(names)).ToList();
    }

    public static string Truncate(string? text, int max) => text is null ? "" : text.Length > max ? text[..max] : text;
}
