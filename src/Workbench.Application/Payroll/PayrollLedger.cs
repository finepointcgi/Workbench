using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Accounting;
using Workbench.Domain.Payroll;

namespace Workbench.Application.Payroll;

/// <summary>
/// The payroll journal for one register, dated the check date:
/// <list type="bullet">
/// <item>Dr 2100 Accrued payroll: the labor the GL accrued from approved timesheets (hours × cost rate), cleared.</item>
/// <item>Dr/Cr 6050 Payroll labor variance: gross wages less that accrual (leave at pay rates vs cost rates, overtime premium, salaried variance).</item>
/// <item>Dr 6020 Payroll taxes and 6030 Health and retirement benefits (the fringe pool): employer taxes and benefits.</item>
/// <item>Cr 2120 Net pay clearing (net pay), 2130 Payroll taxes payable (employee withholding + employer taxes),
/// 2140 Benefits and deductions payable (employee deductions + employer benefits).</item>
/// </list>
/// Expense lines carry the employee's organization. Debits equal credits because net = gross − employee taxes − deductions
/// (checked on import).
/// </summary>
internal static class PayrollLedger
{
    public const string AccruedPayroll = "2100";
    public const string NetPayClearing = "2120";
    public const string TaxesPayable = "2130";
    public const string BenefitsPayable = "2140";
    public const string EmployerTaxes = "6020";
    public const string EmployerBenefits = "6030";
    public const string LaborVariance = "6050";

    /// <summary>Accounts payroll posts to, created if a chart installed before payroll lacks them.</summary>
    private static readonly (string Number, string Name, AccountType Type, string Parent, CostClassification Class, string Description)[] Accounts =
    [
        (AccruedPayroll, "Accrued payroll", AccountType.Liability, "2000", CostClassification.None, "Labor cost from approved timesheets until payroll is paid"),
        (NetPayClearing, "Net pay clearing", AccountType.Liability, "2000", CostClassification.None, "Net pay owed to employees until the payroll provider pays it"),
        (TaxesPayable, "Payroll taxes payable", AccountType.Liability, "2000", CostClassification.None, "Withheld and employer payroll taxes until remitted"),
        (BenefitsPayable, "Benefits and deductions payable", AccountType.Liability, "2000", CostClassification.None, "Employee deductions and employer benefit contributions until remitted"),
        (EmployerTaxes, "Payroll taxes", AccountType.Expense, "6000", CostClassification.Fringe, ""),
        (EmployerBenefits, "Health and retirement benefits", AccountType.Expense, "6000", CostClassification.Fringe, ""),
        (LaborVariance, "Payroll labor variance", AccountType.Expense, "6000", CostClassification.Fringe, "Gross pay less labor accrued at cost rates"),
    ];

    public static async Task<Dictionary<string, Account>> EnsureAccountsAsync(IWorkbenchDbContext db, DateTime utcNow, CancellationToken ct)
    {
        var numbers = Accounts.Select(a => a.Number).ToList();
        var existing = await db.Set<Account>().Where(a => numbers.Contains(a.Number)).ToDictionaryAsync(a => a.Number, ct);
        foreach (var def in Accounts.Where(d => !existing.ContainsKey(d.Number)))
        {
            var parent = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == def.Parent, ct);
            var account = new Account
            {
                Number = def.Number,
                Name = def.Name,
                Type = def.Type,
                NormalBalance = Account.DefaultNormalBalance(def.Type),
                ParentId = parent?.Id,
                CostClass = def.Class,
                Description = def.Description.Length == 0 ? null : def.Description,
                CreatedAtUtc = utcNow,
            };
            db.Set<Account>().Add(account);
            existing[def.Number] = account;
        }

        foreach (var account in existing.Values.Where(a => !a.IsActive))
        {
            throw new ArgumentException($"Payroll posts to account {account.Number} {account.Name}, which is inactive. Reactivate it under Accounting › Chart of accounts.");
        }

        return existing;
    }

    public static async Task<JournalEntry> BuildAsync(
        IWorkbenchDbContext db,
        PayrollRun run,
        PayrollRegister register,
        PayGroup group,
        PayPeriod period,
        IReadOnlyList<PayrollRunService.EmployeeTotals> employees,
        IReadOnlyList<PayrollRegisterLine> lines,
        string actor,
        DateTime utcNow,
        CancellationToken ct)
    {
        var accounts = await EnsureAccountsAsync(db, utcNow, ct);
        var entry = new JournalEntry
        {
            Date = period.CheckDate,
            Source = JournalSource.Payroll,
            SourceId = register.Id,
            Description = $"Payroll {group.Code}: {period.Start:yyyy-MM-dd} – {period.End:yyyy-MM-dd}, check date {period.CheckDate:yyyy-MM-dd}",
            CreatedBy = actor,
            CreatedAtUtc = utcNow,
        };

        void Line(string account, decimal amount, string memo, Guid? org = null, bool credit = false)
        {
            if (amount == 0)
            {
                return;
            }

            // A negative debit is a credit and vice versa.
            var isCredit = credit ^ (amount < 0);
            var value = Math.Abs(amount);
            entry.Lines.Add(new JournalLine
            {
                JournalEntryId = entry.Id,
                LineNumber = entry.Lines.Count + 1,
                AccountId = accounts[account].Id,
                OrgUnitId = org,
                Debit = isCredit ? 0 : value,
                Credit = isCredit ? value : 0,
                Memo = memo,
            });
        }

        foreach (var org in employees.GroupBy(e => e.OrgUnitId).OrderBy(g => g.Key))
        {
            Line(AccruedPayroll, org.Sum(e => e.Accrued), "Clear accrued labor", org.Key);
            Line(LaborVariance, org.Sum(e => e.Gross - e.Accrued), "Gross pay less labor accrued at cost rates", org.Key);
            Line(EmployerTaxes, org.Sum(e => e.EmployerTaxes), "Employer payroll taxes", org.Key);
            Line(EmployerBenefits, org.Sum(e => e.EmployerBenefits), "Employer benefits", org.Key);
        }

        Line(NetPayClearing, lines.Sum(l => l.NetPay), $"Net pay, {lines.Count} check(s)", credit: true);
        Line(TaxesPayable, lines.Sum(l => l.EmployeeTaxes + l.EmployerTaxes), "Employee withholding and employer taxes", credit: true);
        Line(BenefitsPayable, lines.Sum(l => l.Deductions + l.EmployerBenefits), "Employee deductions and employer benefits", credit: true);
        return entry;
    }
}
