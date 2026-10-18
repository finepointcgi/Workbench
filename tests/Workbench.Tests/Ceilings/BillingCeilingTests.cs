using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    private static IInvoiceService Invoices(BillingKit kit)
    {
        var audit = new AuditRecorder(kit.User, kit.Clock);
        var ledger = new BillingLedger(kit.Gl.Ledger);
        var generator = new InvoiceGenerator(new BillingCostSource(kit.Burdened is null ? [] : [kit.Burdened], [new RateCeilingBillingLimits()]), [new RateCeilingBillingLimits()]);
        var accruals = new UnbilledAccruals(generator, ledger, audit);
        var inner = new InvoiceService(kit.Factory, generator, ledger, audit, kit.Clock, new InvoiceDocuments(Options.Create(new BillingOptions { CompanyName = "Test Co" })), accruals);
        var guard = new ChargeGuard(kit.Factory, kit.Publisher, new AccessControl(kit.Factory, kit.User), kit.Clock, NullLogger<ChargeGuard>.Instance);
        return new CeilingCheckingInvoiceService(inner, guard);
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
}
