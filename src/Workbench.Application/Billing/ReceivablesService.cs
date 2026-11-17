using Microsoft.EntityFrameworkCore;
using Workbench.Application.Integrations;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;
using Workbench.Domain.Billing;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// Receivables beyond recording a payment: customer credits (overpayments kept against AR), write-offs with a reason, the
/// aging with credits beside it, and payment reminders. Cash and write-offs post through <see cref="BillingLedger"/> in the
/// same unit of work as the change, so the GL's AR account always equals what the aging shows.
/// </summary>
public sealed class ReceivablesService(
    IWorkbenchDbContextFactory dbFactory, IArReportService aging, IPaymentService payments, BillingLedger ledger, AuditRecorder audit, TimeProvider clock,
    OverdueInvoiceCheck overdue, DueSoonInvoiceCheck dueSoon) : IReceivablesService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<ReceivablesAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var report = await aging.GetAgingAsync(asOf, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var date = report.AsOf;
        var credits = (await db.Set<Payment>().AsNoTracking().Where(p => !p.IsVoid && p.UnappliedAmount > 0 && p.ReceivedOn <= date).ToListAsync(cancellationToken))
            .GroupBy(p => p.CustomerId).ToList();
        var names = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var summary = credits.Select(g => new CustomerCreditSummaryDto(g.Key, names.GetValueOrDefault(g.Key) ?? "", g.Sum(p => p.UnappliedAmount)))
            .OrderBy(c => c.CustomerName, StringComparer.Ordinal).ToList();
        var total = summary.Sum(c => c.Credit);
        return new ReceivablesAgingDto(report, summary, total, report.Totals.Total - total);
    }

    public async Task<IReadOnlyList<CustomerCreditDto>> ListCreditsAsync(Guid? customerId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<Payment>().AsNoTracking().Where(p => !p.IsVoid && p.UnappliedAmount > 0);
        if (customerId is { } c)
        {
            q = q.Where(p => p.CustomerId == c);
        }

        var names = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, cancellationToken);
        return (await q.ToListAsync(cancellationToken))
            .OrderBy(p => p.ReceivedOn).ThenBy(p => p.CreatedAtUtc)
            .Select(p => new CustomerCreditDto(p.Id, p.CustomerId, names.GetValueOrDefault(p.CustomerId) ?? "", p.ReceivedOn, p.Reference, p.Amount, p.UnappliedAmount))
            .ToList();
    }

    public async Task<PaymentDto> ApplyCreditAsync(Guid paymentId, ApplyCreditRequest request, CancellationToken cancellationToken = default)
    {
        var applications = (request.Applications ?? []).Where(a => a.Amount != 0).ToList();
        if (applications.Count == 0 || applications.Any(a => a.Amount < 0))
        {
            throw new ArgumentException("Choose the invoices to apply the credit to, with positive amounts.");
        }

        if (applications.GroupBy(a => a.InvoiceId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Apply to each invoice once.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var payment = await db.Set<Payment>().Include(p => p.Applications).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Payment", paymentId);
        if (payment.IsVoid)
        {
            throw new InvalidOperationException("This payment is void.");
        }

        var total = applications.Sum(a => a.Amount);
        if (total > payment.UnappliedAmount)
        {
            throw new ArgumentException($"{total:C} is more than the {payment.UnappliedAmount:C} of this receipt still unapplied.");
        }

        var ids = applications.Select(a => a.InvoiceId).ToList();
        var invoices = await db.Set<Invoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        foreach (var a in applications)
        {
            if (!invoices.TryGetValue(a.InvoiceId, out var invoice) || invoice.CustomerId != payment.CustomerId)
            {
                throw new ArgumentException("Every invoice must belong to the customer who paid.");
            }

            if (invoice.IsForeignCurrency)
            {
                throw new ArgumentException($"Invoice {invoice.Number} is in {invoice.CurrencyCode}; credits are applied to functional-currency invoices.");
            }

            if (!invoice.IsOpenReceivable || a.Amount > invoice.Balance)
            {
                throw new ArgumentException($"Invoice {invoice.Number} has {invoice.Balance:C} open; {a.Amount:C} can't be applied.");
            }

            invoice.AmountPaid += a.Amount;
            invoice.UpdatePaymentStatus();
            invoice.UpdatedAtUtc = UtcNow;
            if (invoice.Status == InvoiceStatus.Paid)
            {
                IntegrationOutbox.Raise(db, Domain.Integrations.IntegrationEventType.InvoicePaid, IntegrationEventPayloads.Invoice(invoice), audit.Actor, UtcNow);
            }

            var existing = payment.Applications.FirstOrDefault(x => x.InvoiceId == invoice.Id);
            if (existing is not null)
            {
                existing.Amount += a.Amount;
            }
            else
            {
                var application = new PaymentApplication { PaymentId = payment.Id, InvoiceId = invoice.Id, Amount = a.Amount };
                payment.Applications.Add(application);
                db.Set<PaymentApplication>().Add(application);
            }
        }

        payment.UnappliedAmount -= total;
        audit.Record(db, nameof(Payment), payment.Id, "CreditApplied", after: new { Applied = total, payment.UnappliedAmount, Invoices = applications.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await payments.GetAsync(payment.Id, cancellationToken);
    }

    public async Task<WriteOffDto> WriteOffAsync(Guid invoiceId, WriteOffInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        var reason = InvoiceService.Clean(request.Reason, 1000) ?? throw new ArgumentException("A reason is required to write off an invoice.");
        if (request.Amount <= 0)
        {
            throw new ArgumentException("The amount written off must be positive.");
        }

        await using (var setup = await dbFactory.CreateAsync(cancellationToken))
        {
            await BillingLedger.EnsureBadDebtAccountAsync(setup, UtcNow, cancellationToken);
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await db.Set<Invoice>().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
        if (invoice.IsForeignCurrency)
        {
            throw new ArgumentException($"Invoice {invoice.Number} is in {invoice.CurrencyCode}. Write-offs of foreign-currency invoices aren't supported yet.");
        }

        if (!invoice.IsOpenReceivable)
        {
            throw new InvalidOperationException($"Invoice {invoice.Number} isn't an open receivable.");
        }

        if (request.Amount > invoice.Balance)
        {
            throw new ArgumentException($"Invoice {invoice.Number} has {invoice.Balance:C} open; {request.Amount:C} can't be written off.");
        }

        var customer = await db.Set<Customer>().AsNoTracking().FirstAsync(c => c.Id == invoice.CustomerId, cancellationToken);
        var writeOff = new InvoiceWriteOff
        {
            CompanyId = invoice.CompanyId,
            InvoiceId = invoice.Id,
            CustomerId = invoice.CustomerId,
            WrittenOffOn = request.Date ?? Today,
            Amount = request.Amount,
            Reason = reason,
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        db.Set<InvoiceWriteOff>().Add(writeOff);
        var entry = await ledger.PostWriteOffAsync(db, writeOff, invoice.Number, customer.Name, cancellationToken);
        writeOff.JournalEntryId = entry.Id;
        invoice.AmountWrittenOff += request.Amount;
        invoice.UpdatePaymentStatus();
        invoice.UpdatedAtUtc = UtcNow;
        audit.Record(db, nameof(InvoiceWriteOff), writeOff.Id, "WrittenOff", after: new { invoice.Number, writeOff.Amount, Journal = entry.Number }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [writeOff], cancellationToken))[0];
    }

    public async Task<WriteOffDto> ReverseWriteOffAsync(Guid writeOffId, ReverseWriteOffRequest request, CancellationToken cancellationToken = default)
    {
        var reason = InvoiceService.Clean(request.Reason, 1000) ?? throw new ArgumentException("A reason is required to reverse a write-off.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var writeOff = await db.Set<InvoiceWriteOff>().FirstOrDefaultAsync(w => w.Id == writeOffId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Write-off", writeOffId);
        if (writeOff.IsReversed)
        {
            throw new InvalidOperationException("This write-off is already reversed.");
        }

        var invoice = await db.Set<Invoice>().FirstAsync(i => i.Id == writeOff.InvoiceId, cancellationToken);
        var reversal = await ledger.ReverseAsync(db, writeOff.JournalEntryId, Today, $"Reversal of write-off: {reason}", cancellationToken);
        invoice.AmountWrittenOff -= writeOff.Amount;
        invoice.UpdatePaymentStatus();
        invoice.UpdatedAtUtc = UtcNow;
        writeOff.IsReversed = true;
        writeOff.ReversalReason = reason;
        writeOff.ReversedBy = audit.Actor;
        writeOff.ReversedAtUtc = UtcNow;
        audit.Record(db, nameof(InvoiceWriteOff), writeOff.Id, "Reversed", after: new { invoice.Number, writeOff.Amount, Reversal = reversal?.Number }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [writeOff], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<WriteOffDto>> ListWriteOffsAsync(Guid? invoiceId = null, Guid? customerId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<InvoiceWriteOff>().AsNoTracking().AsQueryable();
        if (invoiceId is { } i)
        {
            q = q.Where(w => w.InvoiceId == i);
        }

        if (customerId is { } c)
        {
            q = q.Where(w => w.CustomerId == c);
        }

        var rows = (await q.ToListAsync(cancellationToken)).OrderByDescending(w => w.WrittenOffOn).ThenByDescending(w => w.CreatedAtUtc).ToList();
        return await DtosAsync(db, rows, cancellationToken);
    }

    public async Task<int> SendPaymentRemindersAsync(CancellationToken cancellationToken = default) =>
        await dueSoon.RunAsync(cancellationToken) + await overdue.RunAsync(cancellationToken);

    private static async Task<List<WriteOffDto>> DtosAsync(IWorkbenchDbContext db, IReadOnlyList<InvoiceWriteOff> rows, CancellationToken ct)
    {
        var invoiceIds = rows.Select(w => w.InvoiceId).Distinct().ToList();
        var invoices = await db.Set<Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var journal = await BillingLookups.JournalNumbersAsync(db, rows.Select(w => w.JournalEntryId), ct);
        return rows.Select(w => new WriteOffDto(w.Id, w.InvoiceId, invoices.GetValueOrDefault(w.InvoiceId) ?? "?", w.CustomerId, customers.GetValueOrDefault(w.CustomerId) ?? "",
                w.WrittenOffOn, w.Amount, w.Reason, w.JournalEntryId is { } j ? journal.GetValueOrDefault(j) : null, w.IsReversed, w.ReversalReason, w.CreatedBy, w.CreatedAtUtc))
            .ToList();
    }
}

/// <summary>
/// Tells Finance about invoices coming due (within the organization's reminder window), once per invoice. Idempotent: a
/// reminder with the same link counts as sent. Overdue invoices are <see cref="OverdueInvoiceCheck"/>'s.
/// </summary>
public sealed class DueSoonInvoiceCheck(IWorkbenchDbContextFactory dbFactory, INotificationPublisher publisher, TimeProvider clock)
{
    public const string Kind = "invoice.due_soon";

    public static string LinkFor(Guid invoiceId) => $"billing/invoices/{invoiceId}?reminder=due-soon";

    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var days = await db.Set<BillingOrganizationSettings>().AsNoTracking().Select(s => (int?)s.ReminderDaysBeforeDue).FirstOrDefaultAsync(cancellationToken) ?? 3;
        if (days <= 0)
        {
            return 0;
        }

        var horizon = today.AddDays(days);
        var coming = (await db.Set<Invoice>().AsNoTracking().Where(i => i.DueDate >= today && i.DueDate <= horizon).ToListAsync(cancellationToken))
            .Where(i => i.Balance > 0).ToList();
        if (coming.Count == 0)
        {
            return 0;
        }

        var finance = await Identity.RoleStore.EmailsWithPermissionAsync(db, Permission.BillingReportsView, cancellationToken);
        if (finance.Count == 0)
        {
            return 0;
        }

        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, cancellationToken);
        var sent = 0;
        foreach (var invoice in coming)
        {
            var link = LinkFor(invoice.Id);
            var already = (await db.Set<Notification>().AsNoTracking().Where(n => n.Kind == Kind && n.Link == link).Select(n => n.RecipientEmail).ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recipients = finance.Where(e => !already.Contains(e)).ToList();
            if (recipients.Count == 0)
            {
                continue;
            }

            var left = invoice.DueDate.DayNumber - today.DayNumber;
            sent += (await publisher.PublishAsync(recipients, Kind,
                left == 0 ? $"Invoice {invoice.Number} is due today" : $"Invoice {invoice.Number} is due in {left} day{(left == 1 ? "" : "s")}",
                $"{customers.GetValueOrDefault(invoice.CustomerId)} · {invoice.Balance:C} open, due {invoice.DueDate:yyyy-MM-dd}",
                link)).Count;
        }

        return sent;
    }
}

/// <summary>The organization's invoice terms, remittance details, reminder window and default layout (Finance and Admin).</summary>
public sealed class BillingSettingsService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : IBillingSettingsService
{
    public async Task<BillingSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return Dto(await db.Set<BillingOrganizationSettings>().AsNoTracking().FirstOrDefaultAsync(cancellationToken));
    }

    public async Task<BillingSettingsDto> SaveAsync(SaveBillingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.DefaultPaymentTermsDays is < 0 or > 365)
        {
            throw new ArgumentException("Payment terms are 0 to 365 days.");
        }

        if (request.ReminderDaysBeforeDue is < 0 or > 60)
        {
            throw new ArgumentException("Remind 0 to 60 days before an invoice is due (0 turns the reminder off).");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await db.Set<BillingOrganizationSettings>().FirstOrDefaultAsync(cancellationToken);
        var before = settings is null ? null : new { settings.DefaultPaymentTermsDays, settings.ReminderDaysBeforeDue, settings.DefaultLayout };
        if (settings is null)
        {
            settings = new BillingOrganizationSettings();
            db.Set<BillingOrganizationSettings>().Add(settings);
        }

        settings.LegalName = InvoiceService.Clean(request.LegalName, 200);
        settings.RemitToAddress = InvoiceService.Clean(request.RemitToAddress, 1000);
        settings.RemittanceInstructions = InvoiceService.Clean(request.RemittanceInstructions, 2000);
        settings.PaymentTermsText = InvoiceService.Clean(request.PaymentTermsText, 1000);
        settings.DefaultPaymentTermsDays = request.DefaultPaymentTermsDays;
        settings.InvoiceFooter = InvoiceService.Clean(request.InvoiceFooter, 1000);
        settings.BillingContact = InvoiceService.Clean(request.BillingContact, 500);
        settings.ReminderDaysBeforeDue = request.ReminderDaysBeforeDue;
        settings.DefaultLayout = request.DefaultLayout.ToDomain();
        settings.UpdatedBy = audit.Actor;
        settings.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(BillingOrganizationSettings), settings.Id, before is null ? "Created" : "Updated", before,
            new { settings.DefaultPaymentTermsDays, settings.ReminderDaysBeforeDue, settings.DefaultLayout });
        await db.SaveChangesAsync(cancellationToken);
        return Dto(settings);
    }

    private static BillingSettingsDto Dto(BillingOrganizationSettings? s) => s is null
        ? new BillingSettingsDto(null, null, null, null, 30, null, null, 3, InvoiceLayoutKind.Government)
        : new BillingSettingsDto(s.LegalName, s.RemitToAddress, s.RemittanceInstructions, s.PaymentTermsText, s.DefaultPaymentTermsDays,
            s.InvoiceFooter, s.BillingContact, s.ReminderDaysBeforeDue, s.DefaultLayout.ToContract());
}
