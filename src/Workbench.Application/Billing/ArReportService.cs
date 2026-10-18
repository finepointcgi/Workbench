using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.Identity;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

public sealed class ArReportService(IWorkbenchDbContextFactory dbFactory, OverdueInvoiceCheck overdue, TimeProvider clock) : IArReportService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>Which aging bucket an open balance falls in, by days past due.</summary>
    public static AgingBuckets Bucket(decimal balance, int daysPastDue) => daysPastDue switch
    {
        <= 0 => AgingBuckets.Zero with { Current = balance },
        <= 30 => AgingBuckets.Zero with { Days1To30 = balance },
        <= 60 => AgingBuckets.Zero with { Days31To60 = balance },
        <= 90 => AgingBuckets.Zero with { Days61To90 = balance },
        _ => AgingBuckets.Zero with { Over90 = balance },
    };

    public static string BucketLabel(int daysPastDue) => daysPastDue switch
    {
        <= 0 => "current",
        <= 30 => "1-30",
        <= 60 => "31-60",
        <= 90 => "61-90",
        _ => "90+",
    };

    public async Task<ArAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var date = asOf ?? Today;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var lookups = await BillingLookups.LoadAsync(db, cancellationToken);
        var open = (await db.Set<Invoice>().AsNoTracking().Where(i => i.InvoiceDate <= date).ToListAsync(cancellationToken))
            .Where(i => i.Balance != 0)
            .ToList();
        int Days(Invoice i) => Math.Max(0, date.DayNumber - i.DueDate.DayNumber);

        var customers = open.GroupBy(i => i.CustomerId)
            .Select(g => new AgingRowDto(g.Key, lookups.Customers.GetValueOrDefault(g.Key)?.Name ?? "", null, null,
                g.Aggregate(AgingBuckets.Zero, (b, i) => b.Add(Bucket(i.Balance, Days(i)))), g.Count()))
            .OrderBy(r => r.CustomerName, StringComparer.Ordinal).ToList();
        var contracts = open.GroupBy(i => (i.CustomerId, i.ContractId))
            .Select(g => new AgingRowDto(g.Key.CustomerId, lookups.Customers.GetValueOrDefault(g.Key.CustomerId)?.Name ?? "", g.Key.ContractId,
                lookups.Contracts.GetValueOrDefault(g.Key.ContractId)?.ContractNumber,
                g.Aggregate(AgingBuckets.Zero, (b, i) => b.Add(Bucket(i.Balance, Days(i)))), g.Count()))
            .OrderBy(r => r.CustomerName, StringComparer.Ordinal).ThenBy(r => r.ContractNumber, StringComparer.Ordinal).ToList();
        var invoices = open.OrderBy(i => i.DueDate).Select(i => lookups.Summary(i, date)).ToList();
        return new ArAgingDto(date, customers, contracts, customers.Aggregate(AgingBuckets.Zero, (b, r) => b.Add(r.Buckets)), invoices);
    }

    public async Task<CustomerStatementDto> GetStatementAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentException("The statement ends before it starts.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Customer", customerId);
        var profile = await db.Set<CustomerBillingProfile>().AsNoTracking().FirstOrDefaultAsync(p => p.CustomerId == customerId, cancellationToken);
        var lookups = await BillingLookups.LoadAsync(db, cancellationToken);
        var invoices = (await db.Set<Invoice>().AsNoTracking().Where(i => i.CustomerId == customerId).ToListAsync(cancellationToken))
            .Where(i => i.IsBilled).ToList();
        var payments = await db.Set<Payment>().AsNoTracking().Where(p => p.CustomerId == customerId && !p.IsVoid).ToListAsync(cancellationToken);

        var opening = invoices.Where(i => i.InvoiceDate < from).Sum(i => i.Total) - payments.Where(p => p.ReceivedOn < from).Sum(p => p.Amount);
        var events = invoices.Where(i => i.InvoiceDate >= from && i.InvoiceDate <= to)
            .Select(i => (Date: i.InvoiceDate, Order: 0, Kind: i.Type == InvoiceType.CreditMemo ? "Credit memo" : "Invoice", Reference: i.Number,
                Description: $"{lookups.Contracts.GetValueOrDefault(i.ContractId)?.ContractNumber} · {i.PeriodFrom:yyyy-MM-dd} to {i.PeriodTo:yyyy-MM-dd}"
                    + (i.Type == InvoiceType.CreditMemo ? "" : $" · due {i.DueDate:yyyy-MM-dd}"),
                Charges: Math.Max(i.Total, 0), Credits: Math.Max(-i.Total, 0), InvoiceId: (Guid?)i.Id))
            .Concat(payments.Where(p => p.ReceivedOn >= from && p.ReceivedOn <= to)
                .Select(p => (Date: p.ReceivedOn, Order: 1, Kind: "Payment", Reference: p.Reference ?? p.Method.ToString(),
                    Description: $"Payment received ({p.Method})", Charges: 0m, Credits: p.Amount, InvoiceId: (Guid?)null)))
            .OrderBy(e => e.Date).ThenBy(e => e.Order).ThenBy(e => e.Reference, StringComparer.Ordinal)
            .ToList();
        var balance = opening;
        var lines = new List<CustomerStatementLineDto>();
        foreach (var e in events)
        {
            balance += e.Charges - e.Credits;
            lines.Add(new CustomerStatementLineDto(e.Date, e.Kind, e.Reference, e.Description, e.Charges, e.Credits, balance, e.InvoiceId));
        }

        var open = invoices.Where(i => i.Balance != 0).OrderBy(i => i.DueDate).ToList();
        var aging = open.Aggregate(AgingBuckets.Zero, (b, i) => b.Add(Bucket(i.Balance, Math.Max(0, Today.DayNumber - i.DueDate.DayNumber))));
        return new CustomerStatementDto(customer.Id, customer.Name, profile?.BillingAddress, from, to, opening, lines, balance, aging,
            open.Select(i => lookups.Summary(i, Today)).ToList());
    }

    public async Task<IReadOnlyList<BillingCustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customers = await db.Set<Customer>().AsNoTracking().ToListAsync(cancellationToken);
        var profiles = await db.Set<CustomerBillingProfile>().AsNoTracking().ToDictionaryAsync(p => p.CustomerId, cancellationToken);
        var open = (await db.Set<Invoice>().AsNoTracking().ToListAsync(cancellationToken)).GroupBy(i => i.CustomerId).ToDictionary(g => g.Key, g => g.Sum(i => i.Balance));
        return customers.OrderBy(c => c.Name, StringComparer.Ordinal)
            .Select(c => new BillingCustomerDto(c.Id, c.Code, c.Name, profiles.GetValueOrDefault(c.Id)?.PaymentTermsDays ?? 30, open.GetValueOrDefault(c.Id)))
            .ToList();
    }

    public Task<int> SendOverdueRemindersAsync(CancellationToken cancellationToken = default) => overdue.RunAsync(cancellationToken);
}

/// <summary>
/// Tells Finance (and administrators) when an invoice goes past due, and again as it moves into each later aging bucket.
/// Idempotent: a reminder with the same link (invoice + bucket) counts as sent.
/// </summary>
public sealed class OverdueInvoiceCheck(IWorkbenchDbContextFactory dbFactory, INotificationPublisher publisher, TimeProvider clock)
{
    public const string Kind = "invoice.overdue";

    public static string LinkFor(Guid invoiceId, string bucket) => $"billing/invoices/{invoiceId}?aging={bucket}";

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var overdue = (await db.Set<Invoice>().AsNoTracking().Where(i => i.DueDate < today).ToListAsync(cancellationToken))
            .Where(i => i.Balance > 0).ToList();
        if (overdue.Count == 0)
        {
            return 0;
        }

        var finance = await db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive && (u.IsFinance || u.IsAdmin)).Select(u => u.Email).ToListAsync(cancellationToken);
        if (finance.Count == 0)
        {
            return 0;
        }

        var contracts = await db.Set<Contract>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ContractNumber, cancellationToken);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var sent = 0;
        foreach (var invoice in overdue)
        {
            var days = today.DayNumber - invoice.DueDate.DayNumber;
            var link = LinkFor(invoice.Id, ArReportService.BucketLabel(days));
            var already = (await db.Set<Notification>().AsNoTracking().Where(n => n.Kind == Kind && n.Link == link).Select(n => n.RecipientEmail).ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recipients = finance.Where(e => !already.Contains(e)).ToList();
            if (recipients.Count == 0)
            {
                continue;
            }

            sent += (await publisher.PublishAsync(recipients, Kind,
                $"Invoice {invoice.Number} is {days} day{(days == 1 ? "" : "s")} past due",
                $"{customers.GetValueOrDefault(invoice.CustomerId)} · {contracts.GetValueOrDefault(invoice.ContractId)} · {invoice.Balance:C} open, due {invoice.DueDate:yyyy-MM-dd}",
                link)).Count;
        }

        return sent;
    }
}

/// <summary>Runs <see cref="OverdueInvoiceCheck"/> every few hours; the check is idempotent.</summary>
public sealed class OverdueInvoiceBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<OverdueInvoiceBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(4), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var sent = await scope.ServiceProvider.GetRequiredService<OverdueInvoiceCheck>().RunAsync(ct);
                    if (sent > 0)
                    {
                        logger.LogInformation("Sent {Count} overdue invoice reminders", sent);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Overdue invoice check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
