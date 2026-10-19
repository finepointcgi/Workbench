using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Imports.Costpoint;

/// <summary>One open customer invoice carried over from Costpoint.</summary>
public sealed record ConvertedInvoice(
    string Number,
    Guid ContractId,
    Guid CustomerId,
    InvoiceType Type,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    decimal AmountPaid,
    string Notes,
    IReadOnlyList<ConvertedInvoiceLine> Lines);

public sealed record ConvertedInvoiceLine(string Description, decimal Amount, Guid? ClinId, Guid? ProjectId);

/// <summary>One open vendor bill carried over from Costpoint (only its open balance).</summary>
public sealed record ConvertedBill(
    string VoucherNumber,
    Guid VendorId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    decimal RetainageAmount,
    string Description,
    IReadOnlyList<ConvertedBillLine> Lines);

public sealed record ConvertedBillLine(string Description, decimal Amount, Guid? AccountId, Guid? OrgUnitId);

/// <summary>
/// Writes open AR invoices and AP bills carried over at cut-over. These are subledger detail only: the receivable and
/// payable balances come into the GL through the opening trial balance, so neither is posted (no journal entry) and
/// neither goes through approval. Payments recorded after cut-over apply to them and post as usual. This is the one
/// place imports write billing and payables rows directly, because the regular services would post them a second time.
/// </summary>
public sealed class OpenItemConversion(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock)
{
    public async Task<HashSet<string>> InvoiceNumbersAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<Invoice>().AsNoTracking().Select(i => i.Number).ToListAsync(cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Voucher numbers and (vendor, vendor invoice number) pairs already in AP.</summary>
    public async Task<(HashSet<string> Vouchers, HashSet<(Guid, string)> VendorInvoices)> BillKeysAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => b.Status != ApBillStatus.Void)
            .Select(b => new { b.Number, b.VendorId, b.InvoiceNumber }).ToListAsync(cancellationToken);
        return (bills.Select(b => b.Number).ToHashSet(StringComparer.OrdinalIgnoreCase),
            bills.Select(b => (b.VendorId, b.InvoiceNumber.ToUpperInvariant())).ToHashSet());
    }

    /// <summary>Creates the invoice (approved, unposted) and returns its id and its lines' ids.</summary>
    public async Task<(Guid Id, IReadOnlyList<Guid> LineIds)> CreateInvoiceAsync(ConvertedInvoice source, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<Invoice>().AnyAsync(i => i.Number == source.Number, cancellationToken))
        {
            throw new ArgumentException($"Invoice {source.Number} is already in Workbench.");
        }

        var sequence = (await db.Set<Invoice>().Where(i => i.ContractId == source.ContractId).Select(i => (int?)i.Sequence).MaxAsync(cancellationToken) ?? 0) + 1;
        var now = clock.GetUtcNow().UtcDateTime;
        var invoice = new Invoice
        {
            ContractId = source.ContractId,
            CustomerId = source.CustomerId,
            Sequence = sequence,
            Number = source.Number,
            Type = source.Type,
            Status = InvoiceStatus.Sent,
            PeriodFrom = source.InvoiceDate,
            PeriodTo = source.InvoiceDate,
            InvoiceDate = source.InvoiceDate,
            DueDate = source.DueDate,
            PaymentTermsDays = Math.Max(0, source.DueDate.DayNumber - source.InvoiceDate.DayNumber),
            AmountPaid = source.AmountPaid,
            Notes = source.Notes,
            CreatedBy = audit.Actor,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ApprovedBy = audit.Actor,
            ApprovedAtUtc = now,
            SentBy = audit.Actor,
            SentAtUtc = now,
        };
        var number = 1;
        foreach (var line in source.Lines)
        {
            invoice.Lines.Add(new InvoiceLine
            {
                InvoiceId = invoice.Id,
                LineNumber = number++,
                ClinId = line.ClinId,
                ProjectId = line.ProjectId,
                Kind = InvoiceLineKind.Adjustment,
                Description = line.Description.Length > 500 ? line.Description[..500] : line.Description,
                Quantity = 1,
                Rate = line.Amount,
                Amount = line.Amount,
                IsGenerated = false,
            });
        }

        invoice.Recalculate();
        invoice.UpdatePaymentStatus();
        db.Set<Invoice>().Add(invoice);
        audit.Record(db, nameof(Invoice), invoice.Id, "Converted", after: new { invoice.Number, invoice.Total, invoice.AmountPaid, invoice.InvoiceDate, invoice.DueDate });
        await db.SaveChangesAsync(cancellationToken);
        return (invoice.Id, invoice.Lines.Select(l => l.Id).ToList());
    }

    /// <summary>Creates the bill (posted, without a journal entry) and returns its id and its lines' ids.</summary>
    public async Task<(Guid Id, IReadOnlyList<Guid> LineIds)> CreateBillAsync(ConvertedBill source, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<ApBill>().AnyAsync(b => b.Number == source.VoucherNumber, cancellationToken))
        {
            throw new ArgumentException($"Voucher {source.VoucherNumber} is already in Workbench.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var bill = new ApBill
        {
            VendorId = source.VendorId,
            Number = source.VoucherNumber,
            InvoiceNumber = source.InvoiceNumber,
            InvoiceDate = source.InvoiceDate,
            DueDate = source.DueDate,
            Description = source.Description.Length > 500 ? source.Description[..500] : source.Description,
            Status = ApBillStatus.Posted,
            RetainageAmount = source.RetainageAmount,
            EnteredBy = audit.Actor,
            CreatedAtUtc = now,
            SubmittedAtUtc = now,
            PostedAtUtc = now,
        };
        var number = 1;
        foreach (var line in source.Lines)
        {
            bill.Lines.Add(new ApBillLine
            {
                BillId = bill.Id,
                LineNumber = number++,
                Kind = ApBillLineKind.Account,
                Description = line.Description.Length > 500 ? line.Description[..500] : line.Description,
                Amount = line.Amount,
                AccountId = line.AccountId,
                OrgUnitId = line.OrgUnitId,
                Status = ApLineStatus.Approved,
                DecidedBy = audit.Actor,
                DecidedAtUtc = now,
            });
        }

        bill.Amount = bill.Lines.Sum(l => l.Amount);
        db.Set<ApBill>().Add(bill);
        audit.Record(db, nameof(ApBill), bill.Id, "Converted", after: new { bill.Number, bill.InvoiceNumber, bill.Amount, bill.RetainageAmount, bill.DueDate });
        await db.SaveChangesAsync(cancellationToken);
        return (bill.Id, bill.Lines.Select(l => l.Id).ToList());
    }

    /// <summary>Workbench's invoice type for a contract type (overridable per row).</summary>
    public static InvoiceType DefaultType(ContractType type) => type switch
    {
        ContractType.FirmFixedPrice => InvoiceType.FixedPriceMilestone,
        ContractType.TimeAndMaterials or ContractType.LaborHour => InvoiceType.TimeAndMaterials,
        _ => InvoiceType.CostPlus,
    };
}
