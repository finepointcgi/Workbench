using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Cash;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Cash;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Cash;
using Workbench.Domain.Purchasing;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for cash management: the operating account (First Capital Bank, ending 4321, linked to 1010)
/// with its CSV mapping, this month's statement imported through the real service (the seeded AR payment, the AP check
/// run and a $25.00 service fee, so the fee is left to book), and an office-rent forecast item. Runs once, after the
/// billing and purchasing seeds.
/// </summary>
internal static class CashSeed
{
    private const string Actor = "pm@example.com";

    /// <summary>The CSV layout of samples/bank/operating-2026-09.csv, saved on the seeded account.</summary>
    public static readonly CsvColumnMapping Mapping = new("Posting Date", "Description", Debit: "Debit", Credit: "Credit", CheckNumber: "Check #", Reference: "Bank Ref", DateFormat: "MM/dd/yyyy");

    public static async Task SeedAsync(WorkbenchDbContext db, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (await db.Set<BankAccount>().AnyAsync(cancellationToken))
        {
            return;
        }

        var cash = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "1010", cancellationToken);
        if (cash is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var protector = new BankAccountNumberProtector(services.GetRequiredService<IDataProtectionProvider>());
        var account = new BankAccount
        {
            Name = "Operating account",
            BankName = "First Capital Bank",
            AccountNumberLast4 = "4321",
            ProtectedAccountNumber = protector.Protect("123456784321"),
            RoutingNumber = "054001725",
            GlAccountId = cash.Id,
            CsvMappingJson = CashSupportJson(Mapping),
            CreatedBy = Actor,
            CreatedAtUtc = now,
        };
        db.Add(account);
        db.Add(new CashForecastItem
        {
            Name = "Office rent",
            Amount = -8_500m,
            Frequency = ForecastFrequency.Monthly,
            StartDate = monthStart.AddMonths(1),
            UpdatedBy = Actor,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        // This month's statement, from what the billing and purchasing seeds paid: the bank shows the AR receipt with its
        // reference, the check by number, and a service fee nobody has booked yet.
        var rows = new List<(DateOnly Date, string Description, decimal Amount, string? Check, string Ref)>();
        var receipts = await db.Set<Payment>().AsNoTracking().Where(p => !p.IsVoid && p.ReceivedOn >= monthStart && p.ReceivedOn <= today).ToListAsync(cancellationToken);
        foreach (var p in receipts.OrderBy(p => p.ReceivedOn))
        {
            rows.Add((p.ReceivedOn, $"DFAS COLUMBUS EFT PMT {p.Reference}".Trim(), p.Amount, null, $"B{p.ReceivedOn:yyMMdd}{rows.Count + 1:0000}"));
        }

        var checks = await db.Set<ApPayment>().AsNoTracking().Where(p => p.Status == ApPaymentStatus.Paid && p.PaidOn >= monthStart && p.PaidOn <= today).ToListAsync(cancellationToken);
        foreach (var p in checks.OrderBy(p => p.PaidOn))
        {
            var isCheck = p.Method == Domain.Purchasing.PaymentMethod.Check;
            rows.Add((p.PaidOn!.Value, isCheck ? $"CHECK {p.Reference}" : $"ACH PMT {p.Reference}", -p.Amount, isCheck ? p.Reference : null, $"B{p.PaidOn:yyMMdd}{rows.Count + 1:0000}"));
        }

        rows.Add((today, "MONTHLY SERVICE FEE", -25m, null, $"B{today:yyMMdd}9001"));

        var csv = new StringBuilder("Posting Date,Description,Debit,Credit,Check #,Bank Ref\n");
        foreach (var r in rows.OrderBy(r => r.Date))
        {
            var amount = Math.Abs(r.Amount).ToString("0.00", CultureInfo.InvariantCulture);
            csv.Append(CultureInfo.InvariantCulture, $"{r.Date:MM/dd/yyyy},\"{r.Description}\",{(r.Amount < 0 ? amount : "")},{(r.Amount > 0 ? amount : "")},{r.Check},{r.Ref}\n");
        }

        var factory = services.GetRequiredService<IWorkbenchDbContextFactory>();
        var statements = new BankStatementService(factory, new AuditRecorder(new SeedUser(), TimeProvider.System), TimeProvider.System);
        await statements.ImportAsync(new ImportStatementRequest(account.Id, $"operating-{today:yyyy-MM}.csv", csv.ToString(), StatementFileFormat.Csv,
            ClosingBalance: rows.Sum(r => r.Amount)), cancellationToken);
    }

    private static string CashSupportJson(CsvColumnMapping mapping) =>
        System.Text.Json.JsonSerializer.Serialize(mapping, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private sealed class SeedUser : ICurrentUser
    {
        public string Email => Actor;
    }
}
