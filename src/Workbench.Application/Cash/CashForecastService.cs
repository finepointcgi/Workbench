using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Cash;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Cash;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Cash;

public sealed class CashForecastService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : ICashForecastService
{
    /// <summary>How many recent pay cycles the payroll estimate averages.</summary>
    public const int PayrollHistoryCycles = 4;

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<CashPositionDto> GetPositionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<BankAccount>().AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Name).ToListAsync(cancellationToken);
        var dtos = await BankAccountService.ToDtosAsync(db, accounts, cancellationToken);
        return new CashPositionDto(Today, dtos.Select(a => new CashPositionRowDto(a.Id, a.Name, a.MaskedAccountNumber, a.GlAccountNumber, a.GlBalance,
            a.LastStatementBalance, a.LastStatementDate, a.ReconciledThrough)).ToList());
    }

    public async Task<CashForecastDto> GetForecastAsync(int weeks = 13, CancellationToken cancellationToken = default)
    {
        weeks = Math.Clamp(weeks, 1, 52);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var today = Today;
        var start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday of this week
        var end = start.AddDays(7 * weeks - 1);
        var notes = new List<string>();
        var lines = new List<ForecastLineDto>();
        int Week(DateOnly date) => Math.Clamp((date.DayNumber - start.DayNumber) / 7 + 1, 1, weeks);
        void Add(DateOnly date, ForecastLineKind kind, string description, decimal amount)
        {
            var when = date < today ? today : date; // overdue lands in week 1
            if (when <= end && amount != 0)
            {
                lines.Add(new ForecastLineDto(Week(when), when, kind, description, amount));
            }
        }

        // Opening: the GL cash balances of the active bank accounts.
        var bankAccounts = await db.Set<BankAccount>().AsNoTracking().Where(a => a.IsActive).ToListAsync(cancellationToken);
        var glIds = bankAccounts.Select(a => a.GlAccountId).Distinct().ToList();
        if (glIds.Count == 0)
        {
            notes.Add("No bank accounts are set up, so the forecast starts from zero. Add one under Cash › Accounts.");
        }

        var opening = (await CashLedger.BalancesAsync(db, glIds, null, cancellationToken)).Values.Sum();

        // Receivables: open invoices by due date, shifted by the customer's average days late on past payments.
        var invoices = (await db.Set<Invoice>().AsNoTracking().ToListAsync(cancellationToken)).Where(i => i.Balance > 0).ToList();
        var lateness = await AverageDaysLateAsync(db, cancellationToken);
        var customers = await db.Set<Domain.ProjectAccounting.Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        foreach (var invoice in invoices)
        {
            var late = lateness.GetValueOrDefault(invoice.CustomerId);
            var expected = invoice.DueDate.AddDays(late);
            Add(expected, ForecastLineKind.Receivable,
                $"{invoice.Number} {customers.GetValueOrDefault(invoice.CustomerId, "Customer")} (due {invoice.DueDate:MM/dd}{(late == 0 ? "" : $", {late:+0;-0} days typical")})",
                invoice.Balance);
        }

        if (invoices.Count > 0 && invoices.Any(i => !lateness.ContainsKey(i.CustomerId)))
        {
            notes.Add("Customers with no payment history are expected on the due date.");
        }

        // Payables: approved payment runs on their payment date; the rest of each open bill on its due date.
        var scheduled = await (
                from p in db.Set<ApPayment>().AsNoTracking()
                join r in db.Set<ApPaymentRun>().AsNoTracking() on p.RunId equals r.Id
                where p.Status == ApPaymentStatus.Scheduled && r.Status == PaymentRunStatus.Approved
                select new { p.BillId, p.Amount, r.Number, r.PaymentDate })
            .ToListAsync(cancellationToken);
        foreach (var run in scheduled.GroupBy(s => new { s.Number, s.PaymentDate }))
        {
            Add(run.Key.PaymentDate, ForecastLineKind.PaymentRun, $"Payment run {run.Key.Number} ({run.Count()} payment{(run.Count() == 1 ? "" : "s")})", -run.Sum(s => s.Amount));
        }

        var inRuns = scheduled.GroupBy(s => s.BillId).ToDictionary(g => g.Key, g => g.Sum(s => s.Amount));
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => b.Status == ApBillStatus.Posted || b.Status == ApBillStatus.Approved).ToListAsync(cancellationToken);
        var vendors = await db.Set<Vendor>().AsNoTracking().ToDictionaryAsync(v => v.Id, v => v.Name, cancellationToken);
        foreach (var bill in bills.Where(b => !b.IsCredit)) // Credits come off bills when a payment run nets them, so they aren't cash on their own.
        {
            var remaining = bill.Balance - inRuns.GetValueOrDefault(bill.Id);
            if (remaining > 0)
            {
                Add(bill.DueDate, ForecastLineKind.Payable, $"{bill.Number} {vendors.GetValueOrDefault(bill.VendorId, "Vendor")} {bill.InvoiceNumber} (due {bill.DueDate:MM/dd})", -remaining);
            }
        }

        // Payroll: the configured amount, or the average accrued payroll per recent pay cycle.
        var settings = await CashSupport.SettingsAsync(db, cancellationToken);
        var cycle = Math.Clamp(settings.PayCycleDays, 7, 31);
        var anchor = settings.PayDateAnchor ?? NextFriday(today);
        var (perCycle, basis) = settings.PayrollAmount is { } fixedAmount
            ? (fixedAmount, "Configured amount per payroll")
            : await PayrollFromAccrualsAsync(db, today, anchor, cycle, cancellationToken);
        if (perCycle > 0)
        {
            var payday = anchor;
            while (payday.AddDays(-cycle) >= today)
            {
                payday = payday.AddDays(-cycle);
            }

            while (payday < today)
            {
                payday = payday.AddDays(cycle);
            }

            for (; payday <= end; payday = payday.AddDays(cycle))
            {
                Add(payday, ForecastLineKind.Payroll, $"Payroll {payday:MM/dd}", -perCycle);
            }
        }
        else
        {
            notes.Add("No payroll in the forecast: there are no recent payroll accruals and no configured amount (set one under Forecast settings).");
        }

        // Recurring manual items.
        foreach (var item in await db.Set<CashForecastItem>().AsNoTracking().Where(i => i.IsActive).ToListAsync(cancellationToken))
        {
            foreach (var date in Occurrences(item, today, end))
            {
                Add(date, ForecastLineKind.Recurring, item.Name, item.Amount);
            }
        }

        var result = new List<ForecastWeekDto>();
        var balance = opening;
        for (var w = 1; w <= weeks; w++)
        {
            var inWeek = lines.Where(l => l.Week == w).ToList();
            decimal Sum(params ForecastLineKind[] kinds) => inWeek.Where(l => kinds.Contains(l.Kind)).Sum(l => l.Amount);
            var receivables = Sum(ForecastLineKind.Receivable);
            var payables = Sum(ForecastLineKind.Payable, ForecastLineKind.PaymentRun);
            var payroll = Sum(ForecastLineKind.Payroll);
            var other = Sum(ForecastLineKind.Recurring);
            var closing = balance + receivables + payables + payroll + other;
            result.Add(new ForecastWeekDto(w, start.AddDays(7 * (w - 1)), start.AddDays(7 * w - 1), balance, receivables, payables, payroll, other, closing));
            balance = closing;
        }

        return new CashForecastDto(today, opening, result, lines.OrderBy(l => l.ExpectedDate).ThenBy(l => l.Kind).ToList(), perCycle, cycle, basis, notes);
    }

    /// <summary>
    /// Per customer: the average days between due date and receipt over past payment applications, weighted by amount
    /// (negative when they pay early).
    /// </summary>
    internal static async Task<Dictionary<Guid, int>> AverageDaysLateAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var history = await (
                from a in db.Set<PaymentApplication>().AsNoTracking()
                join p in db.Set<Payment>().AsNoTracking() on a.PaymentId equals p.Id
                join i in db.Set<Invoice>().AsNoTracking() on a.InvoiceId equals i.Id
                where !p.IsVoid && a.Amount > 0
                select new { i.CustomerId, p.ReceivedOn, i.DueDate, a.Amount })
            .ToListAsync(ct);
        return history.GroupBy(h => h.CustomerId).ToDictionary(
            g => g.Key,
            g => (int)Math.Round(g.Sum(h => (h.ReceivedOn.DayNumber - h.DueDate.DayNumber) * h.Amount) / g.Sum(h => h.Amount), MidpointRounding.AwayFromZero));
    }

    /// <summary>Accrued payroll credited (net of reversals) in each of the last few pay cycles, averaged over the cycles that had any.</summary>
    private static async Task<(decimal PerCycle, string Basis)> PayrollFromAccrualsAsync(IWorkbenchDbContext db, DateOnly today, DateOnly anchor, int cycle, CancellationToken ct)
    {
        var rule = await db.Set<PostingRule>().AsNoTracking().FirstOrDefaultAsync(r => r.Kind == PostingRuleKind.AccruedPayroll && r.Key == "", ct);
        if (rule is null)
        {
            return (0m, "No accrued payroll account is mapped");
        }

        // The cycles that ended before today, lined up on the paydays.
        var cycleEnd = anchor;
        while (cycleEnd >= today)
        {
            cycleEnd = cycleEnd.AddDays(-cycle);
        }

        var since = cycleEnd.AddDays(-cycle * PayrollHistoryCycles + 1);
        var rows = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where l.AccountId == rule.AccountId && e.Status != JournalStatus.Draft && e.Date >= since && e.Date < today
                    && (e.Source == JournalSource.Timesheet || e.Source == JournalSource.Payroll || e.Source == JournalSource.Reversal)
                select new { e.Date, Net = l.Credit - l.Debit })
            .ToListAsync(ct);
        var byCycle = rows.GroupBy(r => (r.Date.DayNumber - since.DayNumber) / cycle).Select(g => g.Sum(r => r.Net)).Where(v => v > 0).ToList();
        if (byCycle.Count == 0)
        {
            return (0m, "No payroll accruals in recent pay cycles");
        }

        var average = decimal.Round(byCycle.Average(), 2);
        return (average, $"Average accrued payroll over {byCycle.Count} recent {cycle}-day pay cycle{(byCycle.Count == 1 ? "" : "s")}");
    }

    internal static IEnumerable<DateOnly> Occurrences(CashForecastItem item, DateOnly from, DateOnly to)
    {
        var last = item.EndDate is { } e && e < to ? e : to;
        if (item.Frequency == ForecastFrequency.Once)
        {
            if (item.StartDate >= from && item.StartDate <= last)
            {
                yield return item.StartDate;
            }

            yield break;
        }

        for (var n = 0; ; n++)
        {
            var date = item.Frequency switch
            {
                ForecastFrequency.Weekly => item.StartDate.AddDays(7 * n),
                ForecastFrequency.Biweekly => item.StartDate.AddDays(14 * n),
                _ => item.StartDate.AddMonths(n),
            };
            if (date > last)
            {
                yield break;
            }

            if (date >= from)
            {
                yield return date;
            }
        }
    }

    private static DateOnly NextFriday(DateOnly today) => today.AddDays(((int)DayOfWeek.Friday - (int)today.DayOfWeek + 7) % 7);

    public async Task<IReadOnlyList<ForecastItemDto>> ListItemsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<CashForecastItem>().AsNoTracking().ToListAsync(cancellationToken))
            .OrderByDescending(i => i.IsActive).ThenBy(i => i.Name).Select(ToDto).ToList();
    }

    public async Task<ForecastItemDto> CreateItemAsync(SaveForecastItemRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = new CashForecastItem { Name = "", UpdatedBy = audit.Actor };
        Apply(item, request);
        db.Set<CashForecastItem>().Add(item);
        audit.Record(db, nameof(CashForecastItem), item.Id, "Created", after: ToDto(item));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<ForecastItemDto> UpdateItemAsync(Guid itemId, SaveForecastItemRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = await db.Set<CashForecastItem>().FirstOrDefaultAsync(i => i.Id == itemId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Forecast item", itemId);
        var before = ToDto(item);
        Apply(item, request);
        audit.Record(db, nameof(CashForecastItem), item.Id, "Updated", before, ToDto(item));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(item);
    }

    public async Task<CashSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return ToDto(await CashSupport.SettingsAsync(db, cancellationToken));
    }

    public async Task<CashSettingsDto> SaveSettingsAsync(CashSettingsDto settings, CancellationToken cancellationToken = default)
    {
        if (settings.PayrollAmount is < 0)
        {
            throw new ArgumentException("The payroll amount is the cash paid out per payroll, a positive number (leave it empty to use recent accruals).");
        }

        if (settings.PayCycleDays is < 7 or > 31)
        {
            throw new ArgumentException("The pay cycle is 7 to 31 days (7 weekly, 14 biweekly).");
        }

        if (settings.MatchDateWindowDays is < 0 or > 31)
        {
            throw new ArgumentException("The matching date window is 0 to 31 days.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<CashSettings>().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            row = new CashSettings();
            db.Set<CashSettings>().Add(row);
        }

        var before = ToDto(row);
        row.PayrollAmount = settings.PayrollAmount is { } p ? decimal.Round(p, 2) : null;
        row.PayCycleDays = settings.PayCycleDays;
        row.PayDateAnchor = settings.PayDateAnchor;
        row.MatchDateWindowDays = settings.MatchDateWindowDays;
        row.UpdatedBy = audit.Actor;
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(CashSettings), row.Id, "Updated", before, ToDto(row));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    private void Apply(CashForecastItem item, SaveForecastItemRequest request)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            throw new ArgumentException("Give the item a name (up to 200 characters).");
        }

        if (request.Amount == 0)
        {
            throw new ArgumentException("Enter an amount: positive for money coming in, negative for money going out.");
        }

        if (request.EndDate is { } e && e < request.StartDate)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        item.Name = name;
        item.Amount = decimal.Round(request.Amount, 2);
        item.Frequency = request.Frequency.ToDomain();
        item.StartDate = request.StartDate;
        item.EndDate = request.EndDate;
        item.IsActive = request.IsActive;
        item.UpdatedBy = audit.Actor;
        item.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
    }

    private static ForecastItemDto ToDto(CashForecastItem i) => new(i.Id, i.Name, i.Amount, i.Frequency.ToContract(), i.StartDate, i.EndDate, i.IsActive);

    private static CashSettingsDto ToDto(CashSettings s) => new(s.PayrollAmount, s.PayCycleDays, s.PayDateAnchor, s.MatchDateWindowDays);
}
