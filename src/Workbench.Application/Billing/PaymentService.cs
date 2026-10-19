using Microsoft.EntityFrameworkCore;
using Workbench.Application.Currencies;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

public sealed class PaymentService(IWorkbenchDbContextFactory dbFactory, BillingLedger ledger, AuditRecorder audit, TimeProvider clock) : IPaymentService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<IReadOnlyList<PaymentDto>> ListAsync(PaymentQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<Payment>().AsNoTracking().Include(p => p.Applications).AsQueryable();
        if (query.CustomerId is { } customerId)
        {
            q = q.Where(p => p.CustomerId == customerId);
        }

        if (query.From is { } from)
        {
            q = q.Where(p => p.ReceivedOn >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(p => p.ReceivedOn <= to);
        }

        var payments = (await q.ToListAsync(cancellationToken))
            .OrderByDescending(p => p.ReceivedOn).ThenByDescending(p => p.CreatedAtUtc)
            .Take(Math.Clamp(query.Take, 1, 2000)).ToList();
        return await DtosAsync(db, payments, cancellationToken);
    }

    public async Task<PaymentDto> GetAsync(Guid paymentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var payment = await db.Set<Payment>().AsNoTracking().Include(p => p.Applications).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Payment", paymentId);
        return (await DtosAsync(db, [payment], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListOpenInvoicesAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoices = (await db.Set<Invoice>().AsNoTracking().Where(i => i.CustomerId == customerId).ToListAsync(cancellationToken))
            .Where(i => i.Balance > 0)
            .OrderBy(i => i.DueDate).ThenBy(i => i.Number, StringComparer.Ordinal)
            .ToList();
        var lookups = await BillingLookups.LoadAsync(db, cancellationToken);
        return invoices.Select(i => lookups.Summary(i, Today)).ToList();
    }

    public async Task<PaymentDto> RecordAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default)
    {
        if (CurrencySupport.NormalizeCode(request.CurrencyCode) is not null)
        {
            await using var check = await dbFactory.CreateAsync(cancellationToken);
            if (CurrencySupport.NormalizeCode(request.CurrencyCode) != await CurrencySupport.FunctionalAsync(check, cancellationToken))
            {
                return await RecordForeignAsync(request, cancellationToken);
            }
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("The amount received must be positive.");
        }

        var applications = (request.Applications ?? []).Where(a => a.Amount != 0).ToList();
        if (applications.Any(a => a.Amount < 0))
        {
            throw new ArgumentException("Applied amounts must be positive.");
        }

        if (applications.GroupBy(a => a.InvoiceId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Apply to each invoice once.");
        }

        if (applications.Sum(a => a.Amount) != request.Amount)
        {
            throw new ArgumentException($"Apply the whole receipt: {applications.Sum(a => a.Amount):C} of {request.Amount:C} is applied.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CustomerId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Customer", request.CustomerId);
        var ids = applications.Select(a => a.InvoiceId).ToList();
        var invoices = await db.Set<Invoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var payment = new Payment
        {
            CustomerId = customer.Id,
            ReceivedOn = request.ReceivedOn,
            Amount = request.Amount,
            Method = request.Method.ToDomain(),
            Reference = InvoiceService.Clean(request.Reference, 100),
            Notes = InvoiceService.Clean(request.Notes, 1000),
            CreatedBy = audit.Actor,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        foreach (var a in applications)
        {
            if (!invoices.TryGetValue(a.InvoiceId, out var invoice) || invoice.CustomerId != customer.Id)
            {
                throw new ArgumentException("Every invoice must belong to this customer.");
            }

            if (invoice.IsForeignCurrency)
            {
                throw new ArgumentException($"Invoice {invoice.Number} is in {invoice.CurrencyCode}; record the receipt in {invoice.CurrencyCode}.");
            }

            if (!invoice.IsOpenReceivable || a.Amount > invoice.Balance)
            {
                throw new ArgumentException($"Invoice {invoice.Number} has {invoice.Balance:C} open; {a.Amount:C} can't be applied.");
            }

            invoice.AmountPaid += a.Amount;
            invoice.UpdatePaymentStatus();
            invoice.UpdatedAtUtc = payment.CreatedAtUtc;
            payment.Applications.Add(new PaymentApplication { PaymentId = payment.Id, InvoiceId = invoice.Id, Amount = a.Amount });
        }

        db.Set<Payment>().Add(payment);
        var entry = await ledger.PostPaymentAsync(db, payment, customer.Name, cancellationToken);
        payment.JournalEntryId = entry.Id;
        audit.Record(db, nameof(Payment), payment.Id, "Recorded",
            after: new { payment.Amount, payment.ReceivedOn, Method = payment.Method.ToString(), Applied = applications.Count, Journal = entry.Number });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [payment], cancellationToken))[0];
    }

    public async Task<PaymentDto> VoidAsync(Guid paymentId, VoidInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        var reason = InvoiceService.Clean(request.Reason, 1000) ?? throw new ArgumentException("A reason is required to void a payment.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var payment = await db.Set<Payment>().Include(p => p.Applications).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Payment", paymentId);
        if (payment.IsVoid)
        {
            throw new InvalidOperationException("This payment is already void.");
        }

        var reversal = await ledger.ReverseAsync(db, payment.JournalEntryId, Today, $"Void of cash receipt: {reason}", cancellationToken);
        var ids = payment.Applications.Select(a => a.InvoiceId).ToList();
        var invoices = await db.Set<Invoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        foreach (var a in payment.Applications)
        {
            var invoice = invoices[a.InvoiceId];
            invoice.AmountPaid -= a.Amount;
            invoice.TransactionPaid -= a.TransactionAmount ?? 0;
            invoice.UpdatePaymentStatus();
        }

        payment.IsVoid = true;
        payment.VoidReason = reason;
        payment.VoidedBy = audit.Actor;
        payment.VoidedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(Payment), payment.Id, "Voided", after: new { Reversal = reversal?.Number }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [payment], cancellationToken))[0];
    }

    /// <summary>
    /// A receipt in a foreign currency (multicurrency): applied in that currency to invoices issued in it. Each application
    /// relieves what the invoice carries at its own rate; the cash is the receipt at today's rate; the difference is the
    /// realized gain or loss.
    /// </summary>
    private async Task<PaymentDto> RecordForeignAsync(RecordPaymentRequest request, CancellationToken cancellationToken)
    {
        var applications = (request.Applications ?? []).Where(a => a.Amount != 0).ToList();
        if (request.Amount <= 0 || applications.Any(a => a.Amount < 0))
        {
            throw new ArgumentException("The amount received and the applied amounts must be positive.");
        }

        if (applications.GroupBy(a => a.InvoiceId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Apply to each invoice once.");
        }

        if (applications.Sum(a => a.Amount) != request.Amount)
        {
            throw new ArgumentException($"Apply the whole receipt: {applications.Sum(a => a.Amount):N2} of {request.Amount:N2} is applied.");
        }

        await CurrencySupport.EnsureGainLossAccountsAsync(dbFactory, clock.GetUtcNow().UtcDateTime, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var currency = await CurrencySupport.ResolveAsync(db, request.CurrencyCode, request.ReceivedOn, request.ExchangeRate, cancellationToken);
        if (currency.RoundTransaction(request.Amount) != request.Amount || applications.Any(a => currency.RoundTransaction(a.Amount) != a.Amount))
        {
            throw new ArgumentException($"{currency.Code} amounts have {currency.Decimals} decimal places.");
        }

        var customer = await db.Set<Customer>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CustomerId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Customer", request.CustomerId);
        var ids = applications.Select(a => a.InvoiceId).ToList();
        var invoices = await db.Set<Invoice>().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, cancellationToken);
        var payment = new Payment
        {
            CustomerId = customer.Id,
            ReceivedOn = request.ReceivedOn,
            CurrencyCode = currency.Code,
            TransactionAmount = request.Amount,
            ExchangeRate = currency.Rate,
            Amount = currency.ToFunctional(request.Amount),
            Method = request.Method.ToDomain(),
            Reference = InvoiceService.Clean(request.Reference, 100),
            Notes = InvoiceService.Clean(request.Notes, 1000),
            CreatedBy = audit.Actor,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        foreach (var a in applications)
        {
            if (!invoices.TryGetValue(a.InvoiceId, out var invoice) || invoice.CustomerId != customer.Id)
            {
                throw new ArgumentException("Every invoice must belong to this customer.");
            }

            if (invoice.CurrencyCode != currency.Code)
            {
                throw new ArgumentException($"Invoice {invoice.Number} is in {invoice.CurrencyCode ?? "the functional currency"}, not {currency.Code}; record a separate receipt for it.");
            }

            if (!invoice.IsOpenReceivable || a.Amount > invoice.TransactionBalance)
            {
                throw new ArgumentException($"Invoice {invoice.Number} has {invoice.TransactionBalance:N2} {currency.Code} open; {a.Amount:N2} can't be applied.");
            }

            var relieved = CurrencyMath.Relieved(a.Amount, invoice.TransactionBalance, invoice.Balance, invoice.ExchangeRate);
            invoice.AmountPaid += relieved;
            invoice.TransactionPaid += a.Amount;
            invoice.UpdatePaymentStatus();
            invoice.UpdatedAtUtc = payment.CreatedAtUtc;
            payment.Applications.Add(new PaymentApplication { PaymentId = payment.Id, InvoiceId = invoice.Id, Amount = relieved, TransactionAmount = a.Amount });
        }

        payment.RealizedGainLoss = payment.Amount - payment.Applications.Sum(a => a.Amount);
        db.Set<Payment>().Add(payment);
        var (realized, _) = await CurrencySupport.GainLossAccountsAsync(db, payment.CreatedAtUtc, cancellationToken);
        var entry = await ledger.PostForeignPaymentAsync(db, payment, customer.Name, invoices, realized.Id, cancellationToken);
        payment.JournalEntryId = entry.Id;
        audit.Record(db, nameof(Payment), payment.Id, "Recorded",
            after: new { payment.Amount, payment.CurrencyCode, payment.TransactionAmount, payment.ExchangeRate, payment.RealizedGainLoss, payment.ReceivedOn, Method = payment.Method.ToString(), Applied = applications.Count, Journal = entry.Number });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [payment], cancellationToken))[0];
    }

    private static async Task<List<PaymentDto>> DtosAsync(IWorkbenchDbContext db, IReadOnlyList<Payment> payments, CancellationToken ct)
    {
        var lookups = await BillingLookups.LoadAsync(db, ct);
        var invoiceIds = payments.SelectMany(p => p.Applications).Select(a => a.InvoiceId).Distinct().ToList();
        var invoices = await db.Set<Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
        var journal = await BillingLookups.JournalNumbersAsync(db, payments.Select(p => p.JournalEntryId), ct);
        return payments.Select(p => new PaymentDto(
                p.Id, p.CustomerId, lookups.Customers.GetValueOrDefault(p.CustomerId)?.Name ?? "", p.ReceivedOn, p.Amount, p.Method.ToContract(),
                p.Reference, p.Notes, p.JournalEntryId, p.JournalEntryId is { } j ? journal.GetValueOrDefault(j) : null, p.IsVoid, p.VoidReason,
                p.CreatedBy, p.CreatedAtUtc,
                p.Applications.Select(a =>
                {
                    var invoice = invoices.GetValueOrDefault(a.InvoiceId);
                    return new PaymentApplicationDto(a.InvoiceId, invoice?.Number ?? "?",
                        invoice is null ? "" : lookups.Contracts.GetValueOrDefault(invoice.ContractId)?.ContractNumber ?? "", a.Amount, a.TransactionAmount);
                }).ToList(),
                p.CurrencyCode, p.TransactionAmount, p.CurrencyCode is null ? 1m : p.ExchangeRate, p.RealizedGainLoss))
            .ToList();
    }
}
