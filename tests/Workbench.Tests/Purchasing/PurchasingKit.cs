using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

/// <summary>
/// The real registrations over <see cref="ProjectAccountingFixture"/> (pm@ manages project 1001; period 2026-10 open) with
/// the default chart installed, an org unit, vendors ACME (subcontractor), PRIMA (1099) and BOLT (1099), and an active
/// subcontract with ACME on 1001: ceiling 20,000, funded 10,000, 10% retainage. Calls run as finance@ unless switched.
/// </summary>
internal sealed class PurchasingKit : IAsyncDisposable
{
    public static readonly DateOnly Day = ProjectAccountingFixture.Day;

    private PurchasingKit(SeededHost seeded, ProjectAccountingFixture fx)
    {
        Seeded = seeded;
        Fx = fx;
    }

    public SeededHost Seeded { get; }
    public ProjectAccountingFixture Fx { get; }
    public Guid OrgId { get; private set; }
    public VendorDto Acme { get; private set; } = default!;
    public VendorDto Prima { get; private set; } = default!;
    public VendorDto Bolt { get; private set; } = default!;
    public SubcontractDto Sub { get; private set; } = default!;
    public IReadOnlyDictionary<string, Guid> Accounts { get; private set; } = new Dictionary<string, Guid>();

    public IVendorService Vendors => Get<IVendorService>();
    public ISubcontractService Subcontracts => Get<ISubcontractService>();
    public IApBillService Bills => Get<IApBillService>();
    public IApPaymentService Payments => Get<IApPaymentService>();
    public IApReportService Reports => Get<IApReportService>();

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public PurchasingKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public static async Task<PurchasingKit> CreateAsync(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configure = null)
    {
        var seeded = await SeededHost.CreateAsync(configure);
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var kit = new PurchasingKit(seeded, fx);
        kit.As(Finance);
        await kit.Get<IChartOfAccountsService>().InstallDefaultChartAsync();
        kit.Accounts = (await kit.Get<IChartOfAccountsService>().ListAccountsAsync()).ToDictionary(a => a.Number, a => a.Id);
        await using (var db = await kit.Factory.CreateAsync())
        {
            var org = new OrgUnit { Code = "OPS", Name = "Operations" };
            db.Set<OrgUnit>().Add(org);
            await db.SaveChangesAsync();
            kit.OrgId = org.Id;
        }

        kit.Acme = await kit.Vendors.CreateAsync(new SaveVendorRequest("ACME", "Acme Federal", Uei: "ACMEFED00001", BusinessSize: "Small", SocioEconomicCategories: ["SDVOSB"], PaymentTermsDays: 30));
        kit.Prima = await kit.Vendors.CreateAsync(new SaveVendorRequest("PRIMA", "Primavera Reseller LLC", Is1099: true, BusinessSize: "Small", PaymentTermsDays: 15));
        kit.Bolt = await kit.Vendors.CreateAsync(new SaveVendorRequest("BOLT", "Bolt Consulting", Is1099: true, BusinessSize: "Large", PaymentTermsDays: 30));
        var sub = await kit.Subcontracts.CreateAsync(new SaveSubcontractRequest(kit.Acme.Id, "SUB-1", "Engineering support", SubcontractPricing.TimeAndMaterials,
            new DateOnly(2026, 1, 1), new DateOnly(2027, 12, 31), 20_000m, fx.Contract1, fx.Project1, RetainagePercent: 10m));
        await kit.Subcontracts.AddFundingAsync(sub.Subcontract.Id, new AddSubcontractFundingRequest("M001", Day, 10_000m, 0, "Initial funding"));
        kit.Sub = (await kit.Subcontracts.ActivateAsync(sub.Subcontract.Id)).Subcontract;
        return kit;
    }

    public IWorkbenchDbContextFactory Factory => Get<IWorkbenchDbContextFactory>();

    public Guid Account(string number) => Accounts.TryGetValue(number, out var id) ? id : throw new KeyNotFoundException(number);

    public static SaveBillLineRequest ProjectLine(Guid wbs, decimal amount, string description = "Server parts", OdcCostType type = OdcCostType.Materials) =>
        new(BillLineKind.ProjectCost, description, amount, WbsElementId: wbs, OdcType: type);

    public SaveBillLineRequest OverheadLine(decimal amount, string account = "6230", string description = "Software subscription") =>
        new(BillLineKind.Account, description, amount, AccountId: Account(account), OrgUnitId: OrgId);

    public SaveBillLineRequest SubLine(decimal amount, Guid? odcId = null) =>
        new(BillLineKind.Subcontract, "Engineering hours", amount, WbsElementId: odcId is null ? Fx.Wbs1 : null, SubcontractId: Sub.Id, OdcEntryId: odcId);

    /// <summary>Creates and submits a bill as finance@.</summary>
    public async Task<BillDto> SubmitAsync(Guid vendorId, string invoice, params SaveBillLineRequest[] lines) =>
        await SubmitAsync(new SaveBillRequest(vendorId, invoice, Day, lines));

    public async Task<BillDto> SubmitAsync(SaveBillRequest request, SubmitBillRequest? submit = null)
    {
        As(Finance);
        var bill = await Bills.CreateAsync(request);
        return await Bills.SubmitAsync(bill.Bill.Id, submit ?? new SubmitBillRequest());
    }

    /// <summary>Enters (finance@) and approves (pm@ for project lines, admin@ for the rest) a bill, which posts it.</summary>
    public async Task<BillDto> PostedBillAsync(SaveBillRequest request)
    {
        var bill = await SubmitAsync(request);
        if (bill.Lines.Any(l => l.RoutedTo == Owner))
        {
            As(Owner);
            bill = await Bills.ApproveAsync(bill.Bill.Id);
        }

        if (bill.Bill.Status == BillState.PendingApproval)
        {
            As(Admin);
            bill = await Bills.ApproveAsync(bill.Bill.Id);
        }

        As(Finance);
        Assert.Equal(BillState.Posted, bill.Bill.Status);
        return bill;
    }

    /// <summary>Creates (finance@), approves (admin@) and pays a run.</summary>
    public async Task<PaymentRunDto> PayAsync(DateOnly paidOn, string firstReference, params (Guid BillId, decimal Amount)[] items)
    {
        As(Finance);
        var run = await Payments.CreateRunAsync(new CreatePaymentRunRequest(paidOn, PaymentKind.Check, items.Select(i => new PaymentRunItemRequest(i.BillId, i.Amount)).ToList()));
        As(Admin);
        await Payments.ApproveRunAsync(run.Id);
        var n = long.Parse(firstReference);
        var paid = await Payments.RecordPaymentsAsync(run.Id, new RecordPaymentsRequest(paidOn, run.Payments.Select(p => new PaymentReferenceRequest(p.Id, (n++).ToString())).ToList()));
        As(Finance);
        return paid;
    }

    public async Task<List<JournalEntry>> EntriesAsync(JournalSource? source = null)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => source == null || e.Source == source).OrderBy(e => e.Sequence).ToListAsync();
    }

    /// <summary>Net debit-positive balance of an account over all posted lines.</summary>
    public async Task<decimal> BalanceAsync(string number) =>
        (await EntriesAsync()).SelectMany(e => e.Lines).Where(l => l.AccountId == Account(number)).Sum(l => l.Debit - l.Credit);

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();
}
