using Microsoft.EntityFrameworkCore;
using Workbench.Application.Cash;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Cash;

public sealed class ForecastTests
{
    /// <summary>Wednesday 2026-10-14: week 1 is Monday 10/12 to Sunday 10/18.</summary>
    private static readonly FixedClock Clock = new(new DateTime(2026, 10, 14, 15, 0, 0));

    private static CashForecastService Service(CashKit kit, TimeProvider clock) =>
        new(kit.Factory, new AuditRecorder(kit.Get<ICurrentUser>(), clock), clock);

    [Fact]
    public async Task Forecast_buckets_receivables_payables_payroll_and_recurring_items_by_week()
    {
        await using var kit = await CashKit.CreateAsync();
        await kit.ReceiptAsync(new DateOnly(2026, 10, 2), 20_000m, "EFT OPENING");
        await using (var db = await kit.Factory.CreateAsync())
        {
            var customer = await db.Set<Customer>().Select(c => c.Id).FirstAsync();
            Invoice Inv(string number, DateOnly due, decimal total, InvoiceStatus status, decimal paid = 0) => new()
            {
                Number = number, Sequence = int.Parse(number[4..]) + 1, CustomerId = customer, ContractId = kit.Fx.Contract1, Type = InvoiceType.TimeAndMaterials, Status = status,
                InvoiceDate = due.AddDays(-30), DueDate = due, Total = total, AmountPaid = paid, CreatedBy = Finance,
            };

            // History: this customer paid its last invoice five days late.
            var old = Inv("INV-0", new DateOnly(2026, 9, 1), 4_000m, InvoiceStatus.Paid, 4_000m);
            var payment = new Payment { CustomerId = customer, ReceivedOn = new DateOnly(2026, 9, 6), Amount = 4_000m, CreatedBy = Finance };
            payment.Applications.Add(new PaymentApplication { PaymentId = payment.Id, InvoiceId = old.Id, Amount = 4_000m });
            db.Set<Invoice>().AddRange(old, Inv("INV-1", new DateOnly(2026, 10, 30), 10_000m, InvoiceStatus.Sent), Inv("INV-2", new DateOnly(2026, 10, 1), 3_000m, InvoiceStatus.Sent));
            db.Set<Payment>().Add(payment);

            // AP: a 2,000 bill due 10/28 with 500 of it in an approved run paying 10/20.
            var vendor = new Vendor { Code = "FCST", Name = "Forecast vendor" };
            db.Set<Vendor>().Add(vendor);
            var bill = new ApBill { VendorId = vendor.Id, Number = "V-1", InvoiceNumber = "A-1", InvoiceDate = new DateOnly(2026, 9, 28), DueDate = new DateOnly(2026, 10, 28),
                Status = ApBillStatus.Posted, Amount = 2_000m, EnteredBy = Finance };
            var run = new ApPaymentRun { Number = "PR-000001", PaymentDate = new DateOnly(2026, 10, 20), Status = PaymentRunStatus.Approved, CreatedBy = Finance };
            db.Set<ApBill>().Add(bill);
            db.Set<ApPaymentRun>().Add(run);
            db.Set<ApPayment>().Add(new ApPayment { RunId = run.Id, BillId = bill.Id, VendorId = bill.VendorId, Amount = 500m, Status = ApPaymentStatus.Scheduled });
            await db.SaveChangesAsync();
        }

        var service = Service(kit, Clock);
        await service.SaveSettingsAsync(new CashSettingsDto(40_000m, 14, new DateOnly(2026, 10, 16), 5));
        await service.CreateItemAsync(new SaveForecastItemRequest("Office rent", -8_500m, ForecastRepeat.Monthly, new DateOnly(2026, 11, 1)));

        var f = await service.GetForecastAsync();
        Assert.Equal(13, f.Weeks.Count);
        Assert.Equal((new DateOnly(2026, 10, 12), new DateOnly(2026, 10, 18)), (f.Weeks[0].Start, f.Weeks[0].End));
        Assert.Equal(20_000m, f.OpeningBalance);
        ForecastWeekDto W(int n) => f.Weeks[n - 1];

        // Week 1: the overdue invoice (due 10/1, expected now) and the 10/16 payroll.
        Assert.Equal((3_000m, 0m, -40_000m, 0m, -17_000m), (W(1).Receivables, W(1).Payables, W(1).Payroll, W(1).Other, W(1).Closing));
        // Week 2: the approved run on 10/20.
        Assert.Equal((-500m, -17_500m), (W(2).Payables, W(2).Closing));
        Assert.Contains(f.Lines, l => l.Week == 2 && l.Kind == ForecastLineKind.PaymentRun && l.Amount == -500m);
        // Week 3: the rest of the bill (due 10/28), payroll 10/30 and rent 11/1.
        Assert.Equal((-1_500m, -40_000m, -8_500m, -67_500m), (W(3).Payables, W(3).Payroll, W(3).Other, W(3).Closing));
        // Week 4: INV-1, due 10/30, expected five days later (11/4).
        Assert.Equal(10_000m, W(4).Receivables);
        Assert.Equal(new DateOnly(2026, 11, 4), f.Lines.Single(l => l.Description.StartsWith("INV-1", StringComparison.Ordinal)).ExpectedDate);
        // Payroll every two weeks; rent on the 1st of each month in the horizon.
        Assert.Equal([1, 3, 5, 7, 9, 11, 13], f.Lines.Where(l => l.Kind == ForecastLineKind.Payroll).Select(l => l.Week));
        Assert.Equal([3, 8, 12], f.Lines.Where(l => l.Kind == ForecastLineKind.Recurring).Select(l => l.Week));
        Assert.Equal(20_000m + 13_000m - 2_000m - 7 * 40_000m - 3 * 8_500m, W(13).Closing);
        Assert.All(f.Weeks.Skip(1), w => Assert.Equal(f.Weeks[w.Week - 2].Closing, w.Opening));
        Assert.Equal("Configured amount per payroll", f.PayrollBasis);
    }

    [Fact]
    public async Task Without_a_configured_amount_payroll_comes_from_recent_accruals()
    {
        await using var kit = await CashKit.CreateAsync();
        await kit.Get<IPostingService>().PostBacklogAsync(); // the fixture's approved timesheets accrue payroll on 10/14

        var f = await Service(kit, new FixedClock(new DateTime(2026, 10, 21, 15, 0, 0))).GetForecastAsync();
        Assert.True(f.PayrollPerCycle > 0);
        Assert.StartsWith("Average accrued payroll", f.PayrollBasis);
        Assert.Contains(f.Lines, l => l.Kind == ForecastLineKind.Payroll && l.Amount == -f.PayrollPerCycle);
    }

    [Fact]
    public async Task Cash_position_is_the_gl_balance_per_bank_account()
    {
        await using var kit = await CashKit.CreateAsync();
        await kit.ReceiptAsync(new DateOnly(2026, 10, 2), 1_500m, "EFT 1");
        await kit.VendorPaymentAsync(new DateOnly(2026, 10, 3), 400m, "1001");
        var position = await kit.Forecast.GetPositionAsync();
        var row = Assert.Single(position.Accounts);
        Assert.Equal((1_100m, "••••4321", "1010"), (row.GlBalance, row.MaskedAccountNumber, row.GlAccountNumber));
        Assert.Equal(1_100m, position.Total);
    }
}
