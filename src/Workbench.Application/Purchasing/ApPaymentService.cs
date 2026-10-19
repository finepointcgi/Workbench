using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Accounting;
using Workbench.Application.Currencies;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Security;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// Payment runs: choose due bills, create a run, have Finance approve it (not its creator), then record the checks or ACH
/// references. Each payment posts debit accounts payable / credit cash; voiding reverses that entry and reopens the bill.
/// </summary>
public sealed class ApPaymentService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    GeneralLedger ledger,
    AccessControl access,
    SecurityEventLog securityLog,
    IOptions<PurchasingOptions> options,
    TimeProvider clock) : IApPaymentService
{
    private static readonly PaymentRunStatus[] OpenRuns = [PaymentRunStatus.Draft, PaymentRunStatus.Approved];

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<DueBillDto>> ListDueBillsAsync(DueBillQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<ApBill>().AsNoTracking().Where(b => b.Status == ApBillStatus.Posted);
        if (query.VendorId is { } vendorId)
        {
            q = q.Where(b => b.VendorId == vendorId);
        }

        if (query.DueOnOrBefore is { } due)
        {
            q = q.Where(b => b.DueDate <= due);
        }

        var bills = (await q.ToListAsync(cancellationToken)).Where(b => b.Balance > 0).OrderBy(b => b.DueDate).ThenBy(b => b.Number).ToList();
        var scheduled = await ScheduledAsync(db, bills.Select(b => b.Id).ToList(), null, cancellationToken);
        var scheduledTx = await ScheduledTransactionAsync(db, bills.Select(b => b.Id).ToList(), cancellationToken);
        var summaries = await ApBillService.SummariesAsync(db, bills, cancellationToken);
        return summaries.Select(s => new DueBillDto(s, scheduled.GetValueOrDefault(s.Id), scheduledTx.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<IReadOnlyList<PaymentRunDto>> ListRunsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var runs = (await db.Set<ApPaymentRun>().AsNoTracking().ToListAsync(cancellationToken)).OrderByDescending(r => r.Number).ToList();
        return await ToDtosAsync(db, runs, cancellationToken);
    }

    public async Task<PaymentRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RunDtoAsync(db, runId, cancellationToken);
    }

    public async Task<PaymentRunDto> CreateRunAsync(CreatePaymentRunRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Items is null || request.Items.Count == 0)
        {
            throw new ArgumentException("Choose at least one bill to pay.");
        }

        if (request.Items.GroupBy(i => i.BillId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A bill can only be in a run once.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = request.Items.Select(i => i.BillId).ToList();
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        var vendorIds = bills.Values.Select(b => b.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var scheduled = await ScheduledAsync(db, ids, null, cancellationToken);
        var scheduledTx = await ScheduledTransactionAsync(db, ids, cancellationToken);
        var numbers = await db.Set<ApPaymentRun>().Select(r => r.Number).ToListAsync(cancellationToken);
        var run = new ApPaymentRun
        {
            Number = PurchasingSupport.NextNumber(numbers, "PR"),
            PaymentDate = request.PaymentDate,
            Method = request.Method.ToDomain(),
            CreatedBy = access.Email,
            CreatedAtUtc = UtcNow,
        };
        db.Set<ApPaymentRun>().Add(run);
        foreach (var item in request.Items)
        {
            var bill = bills.GetValueOrDefault(item.BillId) ?? throw WorkbenchNotFoundException.For("Bill", item.BillId);
            var vendor = vendors[bill.VendorId];
            PurchasingSupport.EnsureVendorCanTransact(vendor, "paying it");
            if (bill.Status != ApBillStatus.Posted)
            {
                throw new ArgumentException($"Bill {bill.Number} is {bill.Status}; only posted bills can be paid.");
            }

            if (bill.IsForeignCurrency)
            {
                // A foreign bill is paid in its own currency; the liability it relieves is fixed when the payment is recorded.
                var currency = await CurrencySupport.OfAsync(db, bill.CurrencyCode, bill.ExchangeRate, cancellationToken);
                var tx = currency.RoundTransaction(item.Amount);
                if (tx <= 0 || tx != item.Amount)
                {
                    throw new ArgumentException($"The payment on {bill.Number} must be a positive {bill.CurrencyCode} amount with {currency.Decimals} decimal places.");
                }

                var txAvailable = bill.TransactionBalance - scheduledTx.GetValueOrDefault(bill.Id);
                if (tx > txAvailable)
                {
                    throw new ArgumentException($"Bill {bill.Number} has {txAvailable:N2} {bill.CurrencyCode} left to pay (balance {bill.TransactionBalance:N2}, {scheduledTx.GetValueOrDefault(bill.Id):N2} already in open runs).");
                }

                db.Set<ApPayment>().Add(new ApPayment
                {
                    RunId = run.Id, BillId = bill.Id, VendorId = vendor.Id, Method = run.Method,
                    CurrencyCode = bill.CurrencyCode, TransactionAmount = tx, ExchangeRate = bill.ExchangeRate,
                    Amount = CurrencyMath.Relieved(tx, txAvailable, bill.Balance - scheduled.GetValueOrDefault(bill.Id), bill.ExchangeRate),
                });
                continue;
            }

            var amount = PurchasingSupport.Money(item.Amount, $"The payment on {bill.Number}");
            var available = bill.Balance - scheduled.GetValueOrDefault(bill.Id);
            if (amount > available)
            {
                throw new ArgumentException($"Bill {bill.Number} has {available:N2} left to pay (balance {bill.Balance:N2}, {scheduled.GetValueOrDefault(bill.Id):N2} already in open runs).");
            }

            db.Set<ApPayment>().Add(new ApPayment { RunId = run.Id, BillId = bill.Id, VendorId = vendor.Id, Amount = amount, Method = run.Method });
        }

        audit.Record(db, PurchasingSupport.RunEntity, run.Id, "Created", after: new { run.Number, run.PaymentDate, Method = run.Method.ToString(), Bills = request.Items.Count, Total = request.Items.Sum(i => i.Amount) });
        await db.SaveChangesAsync(cancellationToken);
        return await RunDtoAsync(db, run.Id, cancellationToken);
    }

    public async Task<PaymentRunDto> ApproveRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadRunAsync(db, runId, cancellationToken);
        if (run.Status != PaymentRunStatus.Draft)
        {
            throw new InvalidOperationException($"Payment run {run.Number} is {run.Status}.");
        }

        if (PurchasingSupport.SameEmail(run.CreatedBy, access.Email))
        {
            throw new WorkbenchForbiddenException("You can't approve a payment run you created; another Finance user must approve it.");
        }

        run.Status = PaymentRunStatus.Approved;
        run.ApprovedBy = access.Email;
        run.ApprovedAtUtc = UtcNow;
        audit.Record(db, PurchasingSupport.RunEntity, run.Id, "Approved", new { Status = "Draft" }, new { Status = "Approved" });
        await db.SaveChangesAsync(cancellationToken);
        return await RunDtoAsync(db, run.Id, cancellationToken);
    }

    public async Task<PaymentRunDto> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadRunAsync(db, runId, cancellationToken);
        if (!OpenRuns.Contains(run.Status))
        {
            throw new InvalidOperationException($"Payment run {run.Number} is {run.Status}; void individual payments instead.");
        }

        foreach (var payment in await db.Set<ApPayment>().Where(p => p.RunId == run.Id && p.Status == ApPaymentStatus.Scheduled).ToListAsync(cancellationToken))
        {
            payment.Status = ApPaymentStatus.Void;
            payment.VoidReason = "Run cancelled";
            payment.VoidedBy = access.Email;
            payment.VoidedAtUtc = UtcNow;
        }

        var before = run.Status.ToString();
        run.Status = PaymentRunStatus.Cancelled;
        audit.Record(db, PurchasingSupport.RunEntity, run.Id, "Cancelled", new { Status = before }, new { Status = "Cancelled" });
        await db.SaveChangesAsync(cancellationToken);
        return await RunDtoAsync(db, run.Id, cancellationToken);
    }

    public async Task<PaymentRunDto> RecordPaymentsAsync(Guid runId, RecordPaymentsRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await LoadRunAsync(db, runId, cancellationToken);
        if (run.Status != PaymentRunStatus.Approved)
        {
            throw new InvalidOperationException(run.Status == PaymentRunStatus.Draft ? $"Payment run {run.Number} needs approval first." : $"Payment run {run.Number} is {run.Status}.");
        }

        var payments = await db.Set<ApPayment>().Where(p => p.RunId == run.Id && p.Status == ApPaymentStatus.Scheduled).ToListAsync(cancellationToken);
        var references = (request.References ?? []).ToDictionary(r => r.PaymentId, r => r.Reference?.Trim() ?? "");
        foreach (var payment in payments)
        {
            if (!references.TryGetValue(payment.Id, out var reference) || reference.Length == 0)
            {
                throw new ArgumentException(run.Method == PaymentMethod.Check ? "Enter a check number for every payment." : "Enter an ACH reference for every payment.");
            }

            payment.Reference = PurchasingSupport.Required(reference, "The reference", 64);
        }

        if (payments.GroupBy(p => p.Reference, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Each payment needs its own check number or reference.");
        }

        if (run.Method == PaymentMethod.Check)
        {
            var refs = payments.Select(p => p.Reference!).ToList();
            var used = await db.Set<ApPayment>().AsNoTracking()
                .Where(p => p.Method == PaymentMethod.Check && p.RunId != run.Id && p.Reference != null && refs.Contains(p.Reference)).Select(p => p.Reference).FirstOrDefaultAsync(cancellationToken);
            if (used is not null)
            {
                throw new ArgumentException($"Check {used} was already issued.");
            }
        }

        var billIds = payments.Select(p => p.BillId).Distinct().ToList();
        var bills = await db.Set<ApBill>().Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
        var vendorIds = payments.Select(p => p.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var payables = await PurchasingSupport.PayablesAccountAsync(db, cancellationToken);
        var cash = await PurchasingSupport.AccountByNumberAsync(db, options.Value.CashAccountNumber, "cash", cancellationToken);
        var foreign = payments.Any(p => p.CurrencyCode is not null);
        if (foreign)
        {
            await CurrencySupport.EnsureGainLossAccountsAsync(dbFactory, UtcNow, cancellationToken);
        }

        var realized = foreign ? (await CurrencySupport.GainLossAccountsAsync(db, UtcNow, cancellationToken)).Realized : null;
        var functional = foreign ? await CurrencySupport.FunctionalAsync(db, cancellationToken) : CurrencySupport.DefaultFunctional;

        foreach (var payment in payments.OrderBy(p => p.Reference, StringComparer.Ordinal))
        {
            var vendor = vendors[payment.VendorId];
            var bill = bills[payment.BillId];
            PurchasingSupport.EnsureVendorCanTransact(vendor, "paying it");
            if (payment.CurrencyCode is null && payment.Amount > bill.Balance)
            {
                throw new ArgumentException($"Bill {bill.Number} only has {bill.Balance:N2} left to pay.");
            }

            if (payment.CurrencyCode is { } code && payment.TransactionAmount > bill.TransactionBalance)
            {
                throw new ArgumentException($"Bill {bill.Number} only has {bill.TransactionBalance:N2} {code} left to pay.");
            }

            var entry = new JournalEntry
            {
                Date = request.PaidOn,
                Source = JournalSource.AP,
                SourceId = payment.Id,
                Description = $"AP payment {run.Number}: {vendor.Name} {(payment.Method == PaymentMethod.Check ? "check" : "ACH")} {payment.Reference} for {bill.Number}",
                CreatedBy = ledger.Actor,
                CreatedAtUtc = ledger.UtcNow,
            };
            if (payment.CurrencyCode is { } currency)
            {
                // Multicurrency: relieve the liability at the bill's rate, pay the cash at today's rate; the difference is realized.
                var rate = request.ExchangeRates is not null && request.ExchangeRates.TryGetValue(currency, out var given)
                    ? (given > 0 ? CurrencyMath.RoundRate(given) : throw new ArgumentException($"The {currency} rate must be positive."))
                    : (await CurrencySupport.RequireRateAsync(db, currency, functional, request.PaidOn, Domain.Currencies.ExchangeRateType.Spot, cancellationToken)).Rate;
                payment.Amount = CurrencyMath.Relieved(payment.TransactionAmount, bill.TransactionBalance, bill.Balance, bill.ExchangeRate);
                payment.ExchangeRate = rate;
                payment.CashAmount = CurrencyMath.ToFunctional(payment.TransactionAmount, rate);
                payment.RealizedGainLoss = payment.Amount - payment.CashAmount.Value;
                var cashAccount = await CurrencySupport.ForeignCashAccountAsync(db, currency, cancellationToken) ?? cash.Id;
                entry.Lines.Add(CurrencySupport.Line(entry, payables.Id, payment.Amount, $"{vendor.Code} {bill.InvoiceNumber}", currency, payment.TransactionAmount, bill.ExchangeRate));
                entry.Lines.Add(CurrencySupport.Line(entry, cashAccount, -payment.CashAmount.Value, $"{payment.Method} {payment.Reference}", currency, payment.TransactionAmount, rate));
                if (payment.RealizedGainLoss != 0)
                {
                    entry.Lines.Add(CurrencySupport.Line(entry, realized!.Id, -payment.RealizedGainLoss,
                        $"Realized {(payment.RealizedGainLoss > 0 ? "gain" : "loss")}: {currency} {bill.ExchangeRate:0.######} → {rate:0.######}"));
                }

                bill.TransactionPaid += payment.TransactionAmount;
            }
            else
            {
                entry.Lines.Add(new JournalLine { JournalEntryId = entry.Id, LineNumber = 1, AccountId = payables.Id, Debit = payment.Amount, Memo = $"{vendor.Code} {bill.InvoiceNumber}" });
                entry.Lines.Add(new JournalLine { JournalEntryId = entry.Id, LineNumber = 2, AccountId = cash.Id, Credit = payment.Amount, Memo = $"{payment.Method} {payment.Reference}" });
            }

            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, cancellationToken);

            payment.Status = ApPaymentStatus.Paid;
            payment.PaidOn = request.PaidOn;
            payment.PaidBy = access.Email;
            payment.JournalEntryId = entry.Id;
            bill.PaidAmount += payment.Amount;
        }

        run.Status = PaymentRunStatus.Completed;
        run.CompletedAtUtc = UtcNow;
        audit.Record(db, PurchasingSupport.RunEntity, run.Id, "Paid", after: new { PaidOn = request.PaidOn, Payments = payments.Count, Total = payments.Sum(p => p.Amount) });
        await db.SaveChangesAsync(cancellationToken);
        return await RunDtoAsync(db, run.Id, cancellationToken);
    }

    public async Task<PaymentDto> VoidPaymentAsync(Guid paymentId, VoidPaymentRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var payment = await db.Set<ApPayment>().FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Payment", paymentId);
        if (payment.Status == ApPaymentStatus.Void)
        {
            throw new InvalidOperationException("The payment is already void.");
        }

        if (payment.Status == ApPaymentStatus.Paid)
        {
            var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstOrDefaultAsync(e => e.Id == payment.JournalEntryId, cancellationToken)
                ?? throw new InvalidOperationException("The payment's journal entry is missing.");

            // The reversal goes on the payment date when that period is still open, otherwise on today's date.
            var date = payment.PaidOn!.Value;
            var period = await GeneralLedger.PeriodForAsync(db, date, cancellationToken);
            if (period.Status != PeriodStatus.Open)
            {
                date = DateOnly.FromDateTime(UtcNow);
            }

            await ledger.ReverseAsync(db, entry, date, $"Void payment {payment.Reference}: {reason}", cancellationToken);
            var bill = await db.Set<ApBill>().FirstAsync(b => b.Id == payment.BillId, cancellationToken);
            bill.PaidAmount -= payment.Amount;
            bill.TransactionPaid -= payment.CurrencyCode is null ? 0 : payment.TransactionAmount;
        }

        payment.Status = ApPaymentStatus.Void;
        payment.VoidReason = reason;
        payment.VoidedBy = access.Email;
        payment.VoidedAtUtc = UtcNow;
        audit.Record(db, PurchasingSupport.PaymentEntity, payment.Id, "Voided", new { payment.Amount, payment.Reference }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        var run = await RunDtoAsync(db, payment.RunId, cancellationToken);
        return run.Payments.First(p => p.Id == payment.Id);
    }

    public async Task<PurchasingExportFile> ExportAchAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await RunDtoAsync(db, runId, cancellationToken);
        var vendorIds = run.Payments.Select(p => p.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var rows = new List<IEnumerable<object?>> { new object?[] { "Run", "EffectiveDate", "VendorCode", "PayeeName", "RemitTo", "Amount", "Reference", "Bill", "InvoiceNumber" } };
        rows.AddRange(run.Payments.Where(p => p.Status != PaymentState.Void).Select(p => new object?[]
        {
            run.Number, p.PaidOn ?? run.PaymentDate, p.VendorCode, p.VendorName, vendors[p.VendorId].RemitToAddress?.ReplaceLineEndings(" "), p.Amount, p.Reference, p.BillNumber, p.InvoiceNumber,
        }));
        await securityLog.RecordAsync(PurchasingSecurityEvents.ExportPayments, $"payment run {run.Number}", SecurityOutcomes.Success, "ACH CSV", cancellationToken: cancellationToken);
        return new PurchasingExportFile($"{run.Number}-ach.csv", "text/csv", PurchasingSupport.Csv(rows));
    }

    public async Task<PurchasingExportFile> ExportPositivePayAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await RunDtoAsync(db, runId, cancellationToken);
        if (run.Method != PaymentKind.Check)
        {
            throw new ArgumentException("Positive pay lists checks; this run pays by ACH.");
        }

        var rows = new List<IEnumerable<object?>> { new object?[] { "AccountNumber", "CheckNumber", "IssueDate", "Amount", "Payee", "VoidIndicator" } };
        rows.AddRange(run.Payments.Where(p => p.Reference is not null).Select(p => new object?[]
        {
            options.Value.BankAccountNumber, p.Reference, p.PaidOn ?? run.PaymentDate, p.Amount, p.VendorName, p.Status == PaymentState.Void ? "V" : "",
        }));
        await securityLog.RecordAsync(PurchasingSecurityEvents.ExportPayments, $"payment run {run.Number}", SecurityOutcomes.Success, "Positive pay CSV", cancellationToken: cancellationToken);
        return new PurchasingExportFile($"{run.Number}-positive-pay.csv", "text/csv", PurchasingSupport.Csv(rows));
    }

    // ---- Helpers ----

    private static async Task<ApPaymentRun> LoadRunAsync(IWorkbenchDbContext db, Guid runId, CancellationToken ct) =>
        await db.Set<ApPaymentRun>().FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw WorkbenchNotFoundException.For("Payment run", runId);

    /// <summary>Amounts in open (draft or approved) runs not yet paid, per bill.</summary>
    private static async Task<Dictionary<Guid, decimal>> ScheduledAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> billIds, Guid? exceptRunId, CancellationToken ct)
    {
        var rows = await db.Set<ApPayment>().AsNoTracking()
            .Where(p => billIds.Contains(p.BillId) && p.Status == ApPaymentStatus.Scheduled && p.RunId != exceptRunId)
            .Join(db.Set<ApPaymentRun>().Where(r => OpenRuns.Contains(r.Status)), p => p.RunId, r => r.Id, (p, r) => new { p.BillId, p.Amount })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.BillId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
    }

    /// <summary>Amounts in the bill's currency in open runs not yet paid, per foreign bill.</summary>
    private static async Task<Dictionary<Guid, decimal>> ScheduledTransactionAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> billIds, CancellationToken ct)
    {
        var rows = await db.Set<ApPayment>().AsNoTracking()
            .Where(p => billIds.Contains(p.BillId) && p.Status == ApPaymentStatus.Scheduled && p.CurrencyCode != null)
            .Join(db.Set<ApPaymentRun>().Where(r => OpenRuns.Contains(r.Status)), p => p.RunId, r => r.Id, (p, r) => new { p.BillId, p.TransactionAmount })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.BillId).ToDictionary(g => g.Key, g => g.Sum(r => r.TransactionAmount));
    }

    private static async Task<PaymentRunDto> RunDtoAsync(IWorkbenchDbContext db, Guid runId, CancellationToken ct)
    {
        var run = await db.Set<ApPaymentRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct) ?? throw WorkbenchNotFoundException.For("Payment run", runId);
        return (await ToDtosAsync(db, [run], ct))[0];
    }

    private static async Task<IReadOnlyList<PaymentRunDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<ApPaymentRun> runs, CancellationToken ct)
    {
        var runIds = runs.Select(r => r.Id).ToList();
        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => runIds.Contains(p.RunId)).ToListAsync(ct);
        var billIds = payments.Select(p => p.BillId).Distinct().ToList();
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var vendorIds = payments.Select(p => p.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var byRun = payments.ToLookup(p => p.RunId);
        return runs.Select(r => new PaymentRunDto(
            r.Id, r.Number, r.PaymentDate, r.Method.ToContract(), r.Status.ToContract(), r.CreatedBy, r.ApprovedBy, r.ApprovedAtUtc,
            byRun[r.Id].OrderBy(p => vendors[p.VendorId].Code).ThenBy(p => bills[p.BillId].Number).Select(p => new PaymentDto(
                p.Id, r.Id, r.Number, p.BillId, bills[p.BillId].Number, bills[p.BillId].InvoiceNumber, p.VendorId, vendors[p.VendorId].Code, vendors[p.VendorId].Name,
                p.Amount, p.Method.ToContract(), p.Status.ToContract(), p.Reference, p.PaidOn, p.JournalEntryId, p.VoidReason,
                p.CurrencyCode, p.TransactionAmount, p.CurrencyCode is null ? 1m : p.ExchangeRate, p.CashAmount, p.RealizedGainLoss)).ToList())).ToList();
    }
}
