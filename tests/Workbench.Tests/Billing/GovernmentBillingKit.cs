using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Billing;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Billing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Tests.Billing;

/// <summary>
/// <see cref="BillingKit"/> plus the government billing services. Contract W1's FFP CLIN 0004 is the delivery CLIN
/// (funding and ceiling raised to 200,000 here); the contract price for financing is set to 200,000.
/// </summary>
internal sealed class GovernmentBillingKit
{
    public static readonly DateOnly Through = new(2026, 10, 31);

    private GovernmentBillingKit(BillingKit billing)
    {
        Billing = billing;
        var audit = new AuditRecorder(billing.User, billing.Clock);
        var options = Options.Create(new BillingOptions { CompanyName = "Test Co", CageCode = "1ABC2", Uei = "ABCDEFGH1234" });
        ProgressPayments = new ProgressPaymentService(billing.Factory, new BillingCostSource(billing.Burdened is null ? [] : [billing.Burdened]), audit, billing.Clock, options);
        Events = new PerformanceBasedPaymentService(billing.Factory, audit);
        Government = new GovernmentBillingService(billing.Factory, billing.Invoices, ProgressPayments, [new ManualPieeSubmitter()], audit, billing.Clock, options);
    }

    public BillingKit Billing { get; }
    public ProgressPaymentService ProgressPayments { get; }
    public PerformanceBasedPaymentService Events { get; }
    public GovernmentBillingService Government { get; }
    public InvoiceService Invoices => Billing.Invoices;
    public Guid ContractId => Billing.ContractId;

    public static async Task<GovernmentBillingKit> CreateAsync()
    {
        var billing = await BillingKit.CreateAsync();
        await billing.SetFundingAsync(billing.FfpClin, 200_000m, 200_000m);
        return new GovernmentBillingKit(billing);
    }

    /// <summary>Progress payments at 80% / 80% on a 200,000 price, paid-cost basis unless said otherwise.</summary>
    public Task<GovernmentBillingSettingsDto> UseProgressPaymentsAsync(ProgressPaymentBasis basis = ProgressPaymentBasis.PaidCosts, decimal rate = 0.80m, decimal liquidation = 0.80m) =>
        Government.SaveSettingsAsync(ContractId, new SaveGovernmentBillingSettingsRequest(VoucherFormat.Sf1034, FinancingKind.ProgressPayments, false, rate, liquidation, basis, 200_000m));

    public Task<GovernmentBillingSettingsDto> UsePerformanceBasedAsync(decimal liquidationRate = 0) =>
        Government.SaveSettingsAsync(ContractId, new SaveGovernmentBillingSettingsRequest(VoucherFormat.Sf1034, FinancingKind.PerformanceBased, false, 0, liquidationRate,
            ContractPrice: 200_000m));

    /// <summary>100,000 of burdened cost on the cost-plus WBS (60,000 labor, 30,000 burden, 10,000 ODC).</summary>
    public void SetCosts(decimal labor = 60_000m)
    {
        var k = Billing;
        k.Burdened!.Set(RateBasis.Provisional,
            new BurdenedCostLine(k.ProjectId, k.CostWbs.Id, k.CostWbs.Code, k.CostClin.Id, 600, labor, 10_000m, 10_000m, 10_000m, 10_000m, 0));
    }

    /// <summary>An approved AP bill on the project of <paramref name="amount"/>, <paramref name="paid"/> of it paid.</summary>
    public async Task AddVendorBillAsync(decimal amount, decimal paid)
    {
        await using var db = await Billing.Factory.CreateAsync();
        var vendor = new Vendor { Code = "ACME", Name = "Acme Supply" };
        var bill = new ApBill
        {
            VendorId = vendor.Id,
            Number = "V-000001",
            InvoiceNumber = "A-1",
            InvoiceDate = new DateOnly(2026, 10, 20),
            DueDate = new DateOnly(2026, 11, 19),
            Status = ApBillStatus.Posted,
            Amount = amount,
            PaidAmount = paid,
            EnteredBy = "finance@example.com",
            CreatedAtUtc = BillingKit.Now,
        };
        bill.Lines.Add(new ApBillLine
        {
            BillId = bill.Id,
            LineNumber = 1,
            Kind = ApBillLineKind.ProjectCost,
            Description = "Parts",
            Amount = amount,
            ProjectId = Billing.ProjectId,
            WbsElementId = Billing.CostWbs.Id,
            Status = ApLineStatus.Approved,
        });
        db.Set<Vendor>().Add(vendor);
        db.Set<ApBill>().Add(bill);
        await db.SaveChangesAsync();
    }

    /// <summary>A completed FFP milestone on CLIN 0004 and an approved delivery invoice for it.</summary>
    public async Task<InvoiceDto> DeliverAsync(decimal amount, string description = "First article delivered")
    {
        var milestone = await Billing.Milestones.CreateAsync(ContractId, new SaveMilestoneRequest(Billing.FfpClin.Id, description, amount, new DateOnly(2026, 10, 15)));
        await Billing.Milestones.CompleteAsync(milestone.Id, new CompleteMilestoneRequest(new DateOnly(2026, 10, 20)));
        var draft = await Invoices.GenerateDraftAsync(Billing.Request(InvoiceKind.FixedPriceMilestone));
        return await Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest());
    }

    /// <summary>Makes W1 exportable to PIEE: a 13-character PIID, CAGE and the routing DoDAACs.</summary>
    public async Task MakePieeReadyAsync(PieeDocumentKind delivery = PieeDocumentKind.TwoInOne)
    {
        await using (var db = await Billing.Factory.CreateAsync())
        {
            var contract = await db.Set<Contract>().FirstAsync(c => c.Id == ContractId);
            contract.ContractNumber = "W56HZV-26-C-0042";
            var settings = await db.Set<ContractBillingSettings>().FirstAsync(s => s.ContractId == ContractId);
            settings.CageCode = "1ABC2";
            await db.SaveChangesAsync();
        }

        var current = await Government.GetSettingsAsync(ContractId);
        await Government.SaveSettingsAsync(ContractId, new SaveGovernmentBillingSettingsRequest(current.Format, current.Financing, current.IsSmallBusiness,
            current.ProgressPaymentRate, current.LiquidationRate, current.CostBasis, current.ContractPrice, "W56HZV", "S0512A", "HQ0337", "W25G1U", delivery));
    }

    /// <summary>Net debit (credit negative) on an account across posted entries.</summary>
    public async Task<decimal> BalanceAsync(string account)
    {
        await using var db = await Billing.Factory.CreateAsync();
        var id = await db.Set<Account>().Where(a => a.Number == account).Select(a => a.Id).FirstAsync();
        var lines = await db.Set<JournalLine>().Where(l => l.AccountId == id).Select(l => new { l.Debit, l.Credit }).ToListAsync();
        return lines.Sum(l => l.Debit - l.Credit);
    }
}
