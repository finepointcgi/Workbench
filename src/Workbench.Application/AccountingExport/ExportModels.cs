using Workbench.Contracts.AccountingExport;
using Workbench.Domain.Accounting;

namespace Workbench.Application.AccountingExport;

/// <summary>A name in the destination package: what the mapping says, or Workbench's own name when nothing is mapped yet.</summary>
public readonly record struct Resolved(string Name, string? Code, bool IsMapped);

public sealed record JournalLineRow(int LineNumber, string AccountNumber, Resolved Account, decimal Debit, decimal Credit, string? Memo, Resolved? Class);

public sealed record JournalDoc(Guid Id, string Number, DateOnly Date, string Description, string Source, IReadOnlyList<JournalLineRow> Lines);

public sealed record InvoiceLineRow(int LineNumber, string Kind, Resolved Item, string AccountNumber, Resolved Account, string Description, decimal Quantity, decimal Rate, decimal Amount, Resolved? Class, Resolved TaxCode);

/// <summary>An invoice or credit memo. Amounts are in the functional currency; a credit memo's lines and total are negative, as Workbench keeps them.</summary>
public sealed record InvoiceDoc(
    Guid Id, string Number, Resolved Customer, DateOnly Date, DateOnly DueDate, int TermsDays, string? Reference, string? Notes, decimal Total,
    bool IsCreditMemo, string ReceivableAccountNumber, Resolved ReceivableAccount, IReadOnlyList<InvoiceLineRow> Lines);

public sealed record PaymentApplicationRow(string InvoiceNumber, decimal Amount);

public sealed record PaymentDoc(
    Guid Id, Resolved Customer, DateOnly Date, decimal Amount, string Method, string? Reference, string? Notes,
    string DepositAccountNumber, Resolved DepositAccount, Resolved ReceivableAccount, IReadOnlyList<PaymentApplicationRow> Applications);

public sealed record BillLineRow(int LineNumber, string AccountNumber, Resolved Account, string Description, decimal Amount, Resolved? Class, Resolved TaxCode);

/// <summary>A vendor bill, or a vendor credit when <see cref="IsCredit"/> (amounts stay positive).</summary>
public sealed record BillDoc(
    Guid Id, string Number, Resolved Vendor, string VendorInvoiceNumber, DateOnly Date, DateOnly DueDate, int TermsDays, string? Description,
    decimal Amount, bool IsCredit, string PayablesAccountNumber, Resolved PayablesAccount, IReadOnlyList<BillLineRow> Lines);

public sealed record AccountDoc(Guid Id, string Number, string Name, AccountType Type, string? ParentNumber, Resolved Destination, string? Description, bool IsActive);

/// <summary>Everything one export needs, already resolved through the target's mappings. Only one list is filled, the one for <see cref="Kind"/>.</summary>
public sealed class ExportDataset
{
    public required AccountingExportKind Kind { get; init; }
    public IReadOnlyList<JournalDoc> Journals { get; init; } = [];
    public IReadOnlyList<InvoiceDoc> Invoices { get; init; } = [];
    public IReadOnlyList<PaymentDoc> Payments { get; init; } = [];
    public IReadOnlyList<BillDoc> Bills { get; init; } = [];
    public IReadOnlyList<AccountDoc> Accounts { get; init; } = [];

    /// <summary>Loader warnings (documents skipped, currencies, voids since the last export...).</summary>
    public List<string> Warnings { get; } = [];

    public int RecordCount => Kind switch
    {
        AccountingExportKind.JournalEntries => Journals.Count,
        AccountingExportKind.Invoices => Invoices.Count,
        AccountingExportKind.Payments => Payments.Count,
        AccountingExportKind.Bills => Bills.Count,
        _ => Accounts.Count,
    };
}

public sealed record ExportInput(AccountingExportKind Kind, DateOnly From, DateOnly To, ExportDataset Data, DateTime GeneratedUtc);

/// <summary>The output of one exporter: the file and what is in it. <see cref="ControlTotal"/> is total debits (journals) or document totals.</summary>
public sealed record ExportFile(
    string FileName, string ContentType, byte[] Content, int RowCount, int RecordCount, decimal ControlTotal, IReadOnlyList<string> Warnings,
    IReadOnlyCollection<Guid>? SkippedIds = null)
{
    /// <summary>Records the format can't carry (credit memos in a package without an importer for them): never marked exported, listed in the warnings.</summary>
    public IReadOnlyCollection<Guid> Skipped { get; } = SkippedIds ?? [];
}

/// <summary>
/// Turns an <see cref="ExportDataset"/> into the file one package imports. One implementation per format id; register with
/// <c>AddScoped&lt;IAccountingExporter, T&gt;()</c>. A live push to QuickBooks or Xero (OAuth + their REST APIs) would be another
/// implementation of <see cref="AccountingLive.IAccountingPublisher"/> (Application/AccountingLive), which takes the same <see cref="ExportInput"/>.
/// </summary>
public interface IAccountingExporter
{
    AccountingFormatDto Format { get; }

    /// <summary>Renders <paramref name="input"/>. Throws <see cref="NotSupportedException"/> for a kind the format can't carry.</summary>
    ExportFile Export(ExportInput input);
}
