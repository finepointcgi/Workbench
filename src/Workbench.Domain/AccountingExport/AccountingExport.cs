namespace Workbench.Domain.AccountingExport;

// Accounting exports: hand Workbench's financial data to QuickBooks or Xero (or a generic file) so a firm can keep its books
// there. Targets, kinds and format ids are stored as the contract enums' names / ids so a reader never needs the contract types.

/// <summary>
/// What a Workbench GL account, customer, vendor, tax code, class (project) or invoice item is called in the destination
/// package. One row per target × kind × source key; the target is the package (QuickBooks, Xero or Generic), not a file format,
/// so QuickBooks Online CSV and QuickBooks Desktop IIF share a mapping.
/// </summary>
public sealed class AccountingMapping : Entity
{
    public required string Target { get; set; }

    /// <summary>Account, Customer, Vendor, TaxCode, Class or Item.</summary>
    public required string MapKind { get; set; }

    /// <summary>The Workbench side: account number, customer or vendor code, project code, tax code key or invoice line kind.</summary>
    public required string SourceKey { get; set; }

    public required string SourceName { get; set; }

    /// <summary>The name in the destination (QuickBooks "Account Name", Xero contact or tracking option, ...).</summary>
    public required string DestinationName { get; set; }

    /// <summary>The code in the destination where it has one (Xero account code, tax type).</summary>
    public string? DestinationCode { get; set; }

    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>One export run: who, when, which period and format, the counts and a SHA-256 of the file. Never edited or deleted.</summary>
public sealed class AccountingExportRun : Entity
{
    public required string Target { get; set; }
    public required string FormatId { get; set; }

    /// <summary>ChartOfAccounts, JournalEntries, Invoices, Payments or Bills.</summary>
    public required string Kind { get; set; }

    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }

    /// <summary>Set when the run was limited to one company; null covers every company the exporter could see.</summary>
    public Guid? CompanyId { get; set; }

    public required string FileName { get; set; }
    public required string ContentType { get; set; }

    /// <summary>Data rows in the file (journal lines, invoice lines, ...).</summary>
    public int RowCount { get; set; }

    /// <summary>Documents in the file (journal entries, invoices, ...).</summary>
    public int RecordCount { get; set; }

    /// <summary>Total debits (journals) or document totals (invoices, payments, bills): a figure to tie to the destination after import.</summary>
    public decimal ControlTotal { get; set; }

    public required string Checksum { get; set; }

    /// <summary>JSON array of warning texts shown at export time.</summary>
    public string? WarningsJson { get; set; }

    public int WarningCount { get; set; }

    /// <summary>Exported although some accounts, customers or vendors had no mapping.</summary>
    public bool UnmappedAllowed { get; set; }

    public int UnmappedCount { get; set; }

    /// <summary>Journal export only: subledger entries (source Billing, AP) were included.</summary>
    public bool IncludedSubledgerJournals { get; set; }

    /// <summary>An explicit "export again": some or all records had been exported before.</summary>
    public bool IsRepeat { get; set; }

    public string? Note { get; set; }

    /// <summary>Where the file bytes are kept (blob store), so it can be downloaded again exactly as it was.</summary>
    public string? BlobKey { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// The fact that a record (journal entry, invoice, payment, bill or account) was exported to a target. At most one row per
/// target × kind × record: the unique index is what keeps two overlapping exports from both taking the same record.
/// </summary>
public sealed class AccountingExportedRecord : Entity
{
    public required string Target { get; set; }
    public required string Kind { get; set; }
    public Guid RecordId { get; set; }

    /// <summary>The record's number (JE-000042, invoice number, ...), for messages.</summary>
    public required string RecordNumber { get; set; }

    public Guid FirstRunId { get; set; }
    public Guid LastRunId { get; set; }
    public int ExportCount { get; set; }
    public DateTime FirstExportedAtUtc { get; set; }
    public DateTime LastExportedAtUtc { get; set; }
}
