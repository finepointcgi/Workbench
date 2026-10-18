using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Application.Ceilings;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Ceilings;
using Workbench.Domain.Ceilings;
using Workbench.Tests.Billing;
using Workbench.Tests.IndirectRates;

namespace Workbench.Tests.Ceilings;

public sealed class BillingCeilingTests
{
    private static BurdenedCostLine Cost(BillingKit kit, decimal labor, decimal burden) =>
        new(kit.ProjectId, kit.CostWbs.Id, kit.CostWbs.Code, kit.CostWbs.ClinId, 10, labor, burden, 0, 0, 0, 0);

    /// <summary>The billing kit's invoice service with the ceilings module's billing limits and its enforcement decorator.</summary>
    private static IInvoiceService Invoices(BillingKit kit, IBurdenedCostService? cost = null)
    {
        cost ??= kit.Burdened;
        var audit = new AuditRecorder(kit.User, kit.Clock);
        var ledger = new BillingLedger(kit.Gl.Ledger);
        var generator = new InvoiceGenerator(new BillingCostSource(cost is null ? [] : [cost], [new RateCeilingBillingLimits()]), [new RateCeilingBillingLimits()]);
        var accruals = new UnbilledAccruals(generator, ledger, audit);
        var inner = new InvoiceService(kit.Factory, generator, ledger, audit, kit.Clock, new InvoiceDocuments(Options.Create(new BillingOptions { CompanyName = "Test Co" })), accruals);
        var guard = new ChargeGuard(kit.Factory, kit.Publisher, new AccessControl(kit.Factory, kit.User), kit.Clock, NullLogger<ChargeGuard>.Instance);
        return new CeilingCheckingInvoiceService(inner, kit.Factory, guard);
    }

    private static async Task AddAsync(BillingKit kit, object row)
    {
        await using var db = await kit.Factory.CreateAsync();
        ((Microsoft.EntityFrameworkCore.DbContext)db).Add(row);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_blocking_billing_ceiling_refuses_generating_an_invoice_past_it_and_the_preview_warns()
    {
        var kit = await BillingKit.CreateAsync();
        var invoices = Invoices(kit);
        await AddAsync(kit, new Ceiling { Level = CeilingLevel.Clin, ContractId = kit.ContractId, ClinId = kit.TmClin.Id, BillingCeiling = 1_000m, Enforcement = CeilingEnforcement.Block, CreatedBy = "t" });

        var preview = await invoices.PreviewAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        Assert.Contains(preview.Warnings, w => w.Contains("CLIN 0003") && w.Contains("past the ceiling of $1,000.00"));
        await Assert.ThrowsAsync<CeilingExceededException>(() => invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials)));
    }

    [Fact]
    public async Task Approving_an_invoice_counts_what_was_already_billed_against_the_contract_ceiling()
    {
        var kit = await BillingKit.CreateAsync();
        var invoices = Invoices(kit);
        kit.Burdened!.Set(RateBasis.Provisional, Cost(kit, 1_000m, 0));
        var first = await invoices.GenerateDraftAsync(kit.Request(InvoiceKind.CostPlus)); // 1,000 + 7% fee − withhold
        await invoices.ApproveAsync(first.Summary.Id, new ApproveInvoiceRequest());

        var t = await invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        await AddAsync(kit, new Ceiling { Level = CeilingLevel.Contract, ContractId = kit.ContractId, BillingCeiling = 2_000m, Enforcement = CeilingEnforcement.Block, CreatedBy = "t" });
        await Assert.ThrowsAsync<CeilingExceededException>(() => invoices.ApproveAsync(t.Summary.Id, new ApproveInvoiceRequest()));
    }

    [Fact]
    public async Task Fee_rate_and_fee_amount_caps_limit_the_fee_billed()
    {
        var kit = await BillingKit.CreateAsync();
        var invoices = Invoices(kit);
        kit.Burdened!.Set(RateBasis.Provisional, Cost(kit, 1_000m, 650m));
        await AddAsync(kit, new RateCeiling { ContractId = kit.ContractId, Kind = RateCeilingKind.FeeRate, MaxRate = 0.05m, CreatedBy = "t" });

        var preview = await invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus));
        Assert.Equal(82.50m, Assert.Single(preview.Lines, l => l.Kind == InvoiceLineType.Fee).Amount); // 5% of 1,650, not 7%
        Assert.Contains(preview.Warnings, w => w.Contains("fee rate capped at 5.00%"));

        await AddAsync(kit, new RateCeiling { ContractId = kit.ContractId, ClinId = kit.CostClin.Id, Kind = RateCeilingKind.FeeAmount, MaxAmount = 50m, CreatedBy = "t" });
        var capped = await invoices.GenerateDraftAsync(kit.Request(InvoiceKind.CostPlus));
        Assert.Equal(50m, Assert.Single(capped.Lines, l => l.Kind == InvoiceLineType.Fee).Amount);
        await invoices.ApproveAsync(capped.Summary.Id, new ApproveInvoiceRequest());

        // November: the fee ceiling is used up.
        kit.Burdened.Set(RateBasis.Provisional, Cost(kit, 2_000m, 1_300m));
        var november = await invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus, BillingKit.October.AddMonths(1), BillingKit.OctoberEnd.AddMonths(1)));
        Assert.Equal(0m, Assert.Single(november.Lines, l => l.Kind == InvoiceLineType.Fee).Amount);
    }

    [Fact]
    public async Task Billing_applies_an_indirect_rate_ceiling_at_provisional_rates_through_the_cascade()
    {
        var kit = await IndirectRatesKit.CreateAsync();
        await kit.WorkedExampleAsync(); // fringe 300, overhead 650, G&A 245 at 30% / 50% / 10%
        var source = new BillingCostSource([kit.Burdened], [new RateCeilingBillingLimits()]);
        await using var db = await kit.Factory.CreateAsync();
        var before = await source.GetProjectCostAsync(db, kit.Project.Id, IndirectRatesKit.October, IndirectRatesKit.October.AddMonths(1).AddDays(-1), RateBasis.Provisional, default);
        Assert.Equal(650m, before.Lines.Sum(l => l.Overhead));

        db.Set<RateCeiling>().Add(new RateCeiling { ContractId = kit.Contract.Id, Kind = RateCeilingKind.Overhead, MaxRate = 0.40m, CreatedBy = "t" });
        db.Set<RateCeiling>().Add(new RateCeiling { ContractId = kit.Contract.Id, Kind = RateCeilingKind.Fringe, MaxRate = 0.25m, FiscalYear = 2026, CreatedBy = "t" }); // another year: no effect
        await db.SaveChangesAsync();

        var after = await source.GetProjectCostAsync(db, kit.Project.Id, IndirectRatesKit.October, IndirectRatesKit.October.AddMonths(1).AddDays(-1), RateBasis.Provisional, default);
        var line = Assert.Single(after.Lines);
        Assert.Equal((300m, 520m, 232m), (line.Fringe, line.Overhead, line.GeneralAndAdministrative)); // (1,000 + 300) × 40%; (1,000 + 300 + 520 + 500) × 10%
        Assert.Equal(0.40m, Assert.Single(after.Rates).Overhead);

        // The cost reports themselves keep the real rates.
        Assert.Equal(650m, (await kit.Burdened.GetProjectCostAsync(kit.Project.Id, IndirectRatesKit.October, IndirectRatesKit.October.AddMonths(1).AddDays(-1), RateBasis.Provisional)).Lines.Sum(l => l.Overhead));
    }

    [Fact]
    public async Task Regenerating_a_draft_past_a_blocking_billing_ceiling_is_refused()
    {
        var kit = await BillingKit.CreateAsync();
        var invoices = Invoices(kit);
        var draft = await invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        var clinTotal = draft.Lines.Where(l => l.ClinId == kit.TmClin.Id).Sum(l => l.Amount);
        var ceiling = new Ceiling { Level = CeilingLevel.Clin, ContractId = kit.ContractId, ClinId = kit.TmClin.Id, BillingCeiling = clinTotal - 1m, Enforcement = CeilingEnforcement.Block, CreatedBy = "t" };
        await AddAsync(kit, ceiling);

        await Assert.ThrowsAsync<CeilingExceededException>(() => invoices.RegenerateDraftAsync(draft.Summary.Id));

        // A warning ceiling lets it through.
        await using (var db = await kit.Factory.CreateAsync())
        {
            (await db.Set<Ceiling>().SingleAsync()).Enforcement = CeilingEnforcement.Warn;
            await db.SaveChangesAsync();
        }

        Assert.Equal(draft.Summary.Total, (await invoices.RegenerateDraftAsync(draft.Summary.Id)).Summary.Total);
    }

    [Fact]
    public async Task Adding_or_removing_draft_lines_past_a_blocking_billing_ceiling_is_refused()
    {
        var kit = await BillingKit.CreateAsync();
        var invoices = Invoices(kit);
        var draft = await invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        var clinTotal = draft.Lines.Where(l => l.ClinId == kit.TmClin.Id).Sum(l => l.Amount);
        await AddAsync(kit, new Ceiling { Level = CeilingLevel.Clin, ContractId = kit.ContractId, ClinId = kit.TmClin.Id, BillingCeiling = clinTotal + 50m, Enforcement = CeilingEnforcement.Block, CreatedBy = "t" });

        var ex = await Assert.ThrowsAsync<CeilingExceededException>(() => invoices.AddLineAsync(draft.Summary.Id, new AddInvoiceLineRequest(kit.TmClin.Id, "Rush fee", 100m)));
        Assert.Contains("this line would bring billed to date", ex.Message);

        var credit = await invoices.AddLineAsync(draft.Summary.Id, new AddInvoiceLineRequest(kit.TmClin.Id, "Discount", -30m)); // decreases always pass
        var withRush = await invoices.AddLineAsync(draft.Summary.Id, new AddInvoiceLineRequest(kit.TmClin.Id, "Rush fee", 70m)); // total + 40
        var creditLine = withRush.Lines.Single(l => l.Description == "Discount");

        // Without the discount it would bill total + 70.
        await Assert.ThrowsAsync<CeilingExceededException>(() => invoices.RemoveLineAsync(draft.Summary.Id, creditLine.Id));
        var rushLine = withRush.Lines.Single(l => l.Description == "Rush fee");
        await invoices.RemoveLineAsync(draft.Summary.Id, rushLine.Id); // removing a charge is always allowed
        Assert.Equal(credit.Summary.Total, (await invoices.GetAsync(draft.Summary.Id)).Summary.Total);
    }

    [Fact]
    public async Task Without_a_billing_ceiling_draft_edits_are_unchanged()
    {
        var kit = await BillingKit.CreateAsync();
        var invoices = Invoices(kit);
        var draft = await invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        var added = await invoices.AddLineAsync(draft.Summary.Id, new AddInvoiceLineRequest(kit.TmClin.Id, "Rush fee", 1_000_000m));
        Assert.Equal(draft.Summary.Total + 1_000_000m, added.Summary.Total);
        await invoices.RegenerateDraftAsync(draft.Summary.Id);
    }

    [Fact]
    public async Task A_fee_rate_cap_for_one_fiscal_year_applies_to_the_fee_on_that_years_cost()
    {
        var kit = await BillingKit.CreateAsync();
        var cost = new DatedCost(kit);
        var invoices = Invoices(kit, cost);
        cost.Add(new DateOnly(2026, 9, 15), 1_000m);  // FY2026
        cost.Add(new DateOnly(2026, 10, 15), 1_000m); // FY2027
        await AddAsync(kit, new RateCeiling { ContractId = kit.ContractId, Kind = RateCeilingKind.FeeRate, MaxRate = 0.05m, FiscalYear = 2027, CreatedBy = "t" });

        var preview = await invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus, new DateOnly(2026, 9, 1), BillingKit.OctoberEnd));
        var fees = preview.Lines.Where(l => l.Kind == InvoiceLineType.Fee).ToList();
        Assert.Equal(70.00m, Assert.Single(fees, l => l.Description.Contains("FY2026")).Amount); // 7%, not capped
        Assert.Equal(50.00m, Assert.Single(fees, l => l.Description.Contains("FY2027")).Amount); // 5%
        Assert.Contains(preview.Warnings, w => w.Contains("fee on FY2027 cost") && w.Contains("fee rate capped at 5.00% for FY2027"));
    }

    [Fact]
    public async Task A_fee_amount_cap_for_one_fiscal_year_counts_only_the_fee_billed_on_that_years_cost_including_late_approvals()
    {
        var kit = await BillingKit.CreateAsync();
        var cost = new DatedCost(kit);
        var invoices = Invoices(kit, cost);
        cost.Add(new DateOnly(2026, 9, 15), 1_000m);  // FY2026
        cost.Add(new DateOnly(2026, 10, 15), 1_000m); // FY2027
        await AddAsync(kit, new RateCeiling { ContractId = kit.ContractId, ClinId = kit.CostClin.Id, Kind = RateCeilingKind.FeeAmount, MaxAmount = 40m, FiscalYear = 2026, CreatedBy = "t" });

        var first = await invoices.GenerateDraftAsync(kit.Request(InvoiceKind.CostPlus, new DateOnly(2026, 9, 1), BillingKit.OctoberEnd));
        var fees = first.Lines.Where(l => l.Kind == InvoiceLineType.Fee).ToList();
        Assert.Equal((40m, 70m), (fees.Single(l => l.Description.Contains("FY2026")).Amount, fees.Single(l => l.Description.Contains("FY2027")).Amount));
        await invoices.ApproveAsync(first.Summary.Id, new ApproveInvoiceRequest());

        // November: $100 of September time approved late (FY2026, whose fee is used up) and $500 of November time (FY2027).
        cost.Add(new DateOnly(2026, 9, 20), 100m);
        cost.Add(new DateOnly(2026, 11, 10), 500m);
        var november = await invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus, BillingKit.October.AddMonths(1), BillingKit.OctoberEnd.AddMonths(1)));
        fees = november.Lines.Where(l => l.Kind == InvoiceLineType.Fee).ToList();
        Assert.Equal(0m, fees.Single(l => l.Description.Contains("FY2026")).Amount);
        Assert.Equal(35m, fees.Single(l => l.Description.Contains("FY2027")).Amount); // 7% of 500, no cap that year
        Assert.Equal(600m, november.Lines.Where(l => l.Kind == InvoiceLineType.Labor).Sum(l => l.Amount));
    }

    [Fact]
    public async Task Fee_caps_for_every_year_keep_one_fee_line()
    {
        var kit = await BillingKit.CreateAsync();
        var cost = new DatedCost(kit);
        var invoices = Invoices(kit, cost);
        cost.Add(new DateOnly(2026, 9, 15), 1_000m);
        cost.Add(new DateOnly(2026, 10, 15), 1_000m);
        await AddAsync(kit, new RateCeiling { ContractId = kit.ContractId, Kind = RateCeilingKind.FeeRate, MaxRate = 0.05m, CreatedBy = "t" });

        var preview = await invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus, new DateOnly(2026, 9, 1), BillingKit.OctoberEnd));
        var fee = Assert.Single(preview.Lines, l => l.Kind == InvoiceLineType.Fee);
        Assert.Equal((100m, "Fee 5.00% on 2,000.00"), (fee.Amount, fee.Description));
    }

    /// <summary>Direct labor on the cost-plus WBS by date, no burden: each window gets the labor dated in it.</summary>
    private sealed class DatedCost(BillingKit kit) : IBurdenedCostService
    {
        private readonly List<(DateOnly Date, decimal Labor)> _entries = [];

        public void Add(DateOnly date, decimal labor) => _entries.Add((date, labor));

        public Task<BurdenedCostResult> GetProjectCostAsync(Guid projectId, DateOnly from, DateOnly to, RateBasis basis, CancellationToken cancellationToken = default)
        {
            var labor = _entries.Where(e => e.Date >= from && e.Date <= to).Sum(e => e.Labor);
            List<BurdenedCostLine> lines = projectId == kit.ProjectId && labor != 0 ? [new(kit.ProjectId, kit.CostWbs.Id, kit.CostWbs.Code, kit.CostWbs.ClinId, 10, labor, 0, 0, 0, 0, 0)] : [];
            return Task.FromResult(new BurdenedCostResult(lines, [new AppliedRates(basis, 2027, 0, 0, 0)]));
        }
    }
}
