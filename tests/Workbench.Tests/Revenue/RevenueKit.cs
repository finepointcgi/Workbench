using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.Billing;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Revenue;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Revenue;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Billing;

namespace Workbench.Tests.Revenue;

/// <summary>
/// Revenue services over <see cref="BillingKit"/> (contract W1, CPFF; project 1001 with CPFF, cost, T&amp;M and FFP CLINs;
/// FY2026 and FY2027 open), as finance@.
/// </summary>
internal sealed class RevenueKit
{
    private RevenueKit(BillingKit billing)
    {
        Billing = billing;
        var audit = new AuditRecorder(billing.User, billing.Clock);
        Engine = new RevenueEngine(new BillingCostSource(billing.Burdened is null ? [] : [billing.Burdened]));
        Runs = new RevenueRunService(billing.Factory, Engine, billing.Gl.Ledger, audit);
        Setup = new RevenueSetupService(billing.Factory, audit, billing.Clock);
        Reports = new RevenueReportService(billing.Factory);
        CloseCheck = new RevenueCloseCheck(Engine);
        Close = new PeriodCloseService(billing.Factory, billing.Gl.Engine, audit, [CloseCheck]);
    }

    public BillingKit Billing { get; }
    public RevenueEngine Engine { get; }
    public RevenueRunService Runs { get; }
    public RevenueSetupService Setup { get; }
    public RevenueReportService Reports { get; }
    public RevenueCloseCheck CloseCheck { get; }
    public PeriodCloseService Close { get; }

    public Guid ProjectId => Billing.ProjectId;

    public static async Task<RevenueKit> CreateAsync(bool withIndirectRates = true) => new(await BillingKit.CreateAsync(withIndirectRates));

    public async Task<Guid> PeriodAsync(string name)
    {
        await using var db = await Billing.Factory.CreateAsync();
        return (await db.Set<FiscalPeriod>().FirstAsync(p => p.Name == name)).Id;
    }

    public async Task ClosePeriodAsync(string name)
    {
        await using var db = await Billing.Factory.CreateAsync();
        (await db.Set<FiscalPeriod>().FirstAsync(p => p.Name == name)).Status = PeriodStatus.Closed;
        await db.SaveChangesAsync();
    }

    /// <summary>Burdened cost (fake indirect rates): CLIN 0001's WBS and CLIN 0002's travel WBS.</summary>
    public void SetCost(decimal costPlusWbs, decimal travelWbs = 0)
    {
        var lines = new List<BurdenedCostLine> { Line(Billing.CostWbs, costPlusWbs) };
        if (travelWbs != 0)
        {
            lines.Add(Line(Billing.OdcWbs, travelWbs));
        }

        Billing.Burdened!.Set(RateBasis.Provisional, [.. lines]);
    }

    private BurdenedCostLine Line(WbsElement wbs, decimal total) => new(ProjectId, wbs.Id, wbs.Code, wbs.ClinId, 0, total, 0, 0, 0, 0, 0);

    public Task SetupAsync(RevenueMethodKind method, Guid? clinId = null, decimal? fee = null, decimal? markup = null, decimal? price = null, decimal? unitPrice = null) =>
        Setup.SaveSetupAsync(new SaveRevenueSetupRequest(ProjectId, method, clinId, fee, markup, price, unitPrice));

    public async Task<EstimateDto> ApprovedEstimateAsync(DateOnly asOf, decimal cost, decimal revenue, Guid? clinId = null)
    {
        var e = await Setup.SubmitEstimateAsync(new SubmitEstimateRequest(ProjectId, asOf, cost, revenue, clinId));
        return await Setup.ApproveEstimateAsync(e.Id, new DecideEstimateRequest("OK"));
    }

    public async Task<PostRevenueRunResultDto> PostAsync(string period) => await Runs.PostAsync(await PeriodAsync(period), new PostRevenueRunRequest());

    public Task<List<JournalEntry>> EntriesAsync(JournalSource? source = null) => Billing.Gl.EntriesAsync(source);

    public Task<decimal> BalanceAsync(string account) => Billing.Gl.BalanceAsync(account);

    /// <summary>Net credit on revenue accounts 4010–4040 over every posted line (all sources).</summary>
    public async Task<decimal> RevenueAsync()
    {
        var total = 0m;
        foreach (var a in RevenueEngine.RevenueAccounts)
        {
            total -= await BalanceAsync(a);
        }

        return total;
    }
}
