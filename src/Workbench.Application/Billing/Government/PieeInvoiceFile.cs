using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;

namespace Workbench.Application.Billing;

/// <summary>A line item on a PIEE document: one per CLIN/SLIN for deliveries and cost vouchers.</summary>
internal sealed record PieeLineItem(string? ItemNumber, string Description, decimal Quantity, string UnitOfMeasure, decimal UnitPrice, decimal Amount);

/// <summary>A performance-based payment event billed on a PBP document.</summary>
internal sealed record PieeEvent(int Number, string Description, DateOnly CompletedOn, decimal Amount, string? Evidence);

/// <summary>Everything a PIEE invoice document carries, gathered from the invoice, the contract and its settings.</summary>
internal sealed record PieeDocument(
    PieeDocumentType Type,
    InvoiceType InvoiceType,
    InvoiceStatus InvoiceStatus,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    bool IsFinal,
    string ContractNumber,
    string? CageCode,
    string? Uei,
    string VendorName,
    string? IssueByDodaac,
    string? AdminByDodaac,
    string? PayOfficeDodaac,
    string? ShipToDodaac,
    IReadOnlyList<PieeLineItem> Lines,
    decimal Gross,
    decimal Liquidation,
    decimal Net,
    Sf1443Dto? ProgressPayment,
    IReadOnlyList<PieeEvent> Events);

/// <summary>
/// The PIEE (WAWF) invoice import file: validation of the fields PIEE requires, and the XML document. The layout follows
/// the PIEE XML import's structure (header, routing DoDAACs, line items, and the SF 1443 or event detail for financing
/// documents); it is generated for upload, never sent from here.
/// </summary>
internal static partial class PieeInvoiceFile
{
    public const string SchemaVersion = "1.0";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Label(PieeDocumentType type) => type switch
    {
        PieeDocumentType.TwoInOne => "2-in-1",
        PieeDocumentType.CostVoucher => "Cost Voucher",
        PieeDocumentType.Combo => "Combo",
        PieeDocumentType.ProgressPayment => "Progress Payment",
        PieeDocumentType.PerformanceBasedPayment => "Performance Based Payment",
        _ => type.ToString(),
    };

    /// <summary>The document for an invoice: financing types have their own; cost-type vouchers are Cost Vouchers; deliveries use the contract's choice.</summary>
    public static PieeDocumentType DocumentFor(InvoiceType type, PieeDocumentType deliveryDocument) => type switch
    {
        InvoiceType.ProgressPayment => PieeDocumentType.ProgressPayment,
        InvoiceType.PerformanceBased => PieeDocumentType.PerformanceBasedPayment,
        InvoiceType.CostPlus or InvoiceType.TrueUp => PieeDocumentType.CostVoucher,
        _ => deliveryDocument is PieeDocumentType.TwoInOne or PieeDocumentType.Combo or PieeDocumentType.CostVoucher ? deliveryDocument : PieeDocumentType.TwoInOne,
    };

    /// <summary>Everything that would stop PIEE accepting the file, in plain words. Empty when it can be exported.</summary>
    public static IReadOnlyList<PieeIssueDto> Validate(PieeDocument d)
    {
        var issues = new List<PieeIssueDto>();
        void Add(string field, string message) => issues.Add(new PieeIssueDto(field, message));

        if (d.InvoiceStatus is InvoiceStatus.Draft)
        {
            Add("Status", "Approve the invoice before sending it to PIEE.");
        }
        else if (d.InvoiceStatus is InvoiceStatus.Void)
        {
            Add("Status", "A void invoice can't be sent to PIEE.");
        }

        if (d.InvoiceType == InvoiceType.CreditMemo)
        {
            Add("Document type", "Credit memos aren't sent as PIEE invoices. Ask the paying office to adjust the original invoice.");
        }

        if (!InvoiceNumberPattern().IsMatch(d.InvoiceNumber))
        {
            Add("Invoice number", "PIEE invoice numbers are 1 to 22 letters, digits or dashes.");
        }

        if (Compact(d.ContractNumber).Length != 13)
        {
            Add("Contract number", $"\"{d.ContractNumber}\" isn't a 13-character contract number (PIID), for example W56HZV-26-C-0042.");
        }

        if (d.CageCode is null || !CagePattern().IsMatch(d.CageCode))
        {
            Add("CAGE code", "Enter the contractor's 5-character CAGE code in the contract's billing settings.");
        }

        Dodaac(d.IssueByDodaac, "Issue-by DoDAAC", "the contracting office that issued the contract");
        Dodaac(d.AdminByDodaac, "Admin-by DoDAAC", "the contract administration office (often DCMA)");
        Dodaac(d.PayOfficeDodaac, "Pay office DoDAAC", "the paying office (often DFAS)");
        if (d.Type == PieeDocumentType.Combo)
        {
            Dodaac(d.ShipToDodaac, "Ship-to DoDAAC", "where the supplies were shipped (a Combo includes the receiving report)");
        }

        if (d.Type is PieeDocumentType.ProgressPayment)
        {
            if (d.ProgressPayment is null)
            {
                Add("SF 1443", "A progress payment document needs the SF 1443 request behind the invoice.");
            }
        }
        else if (d.Type is PieeDocumentType.PerformanceBasedPayment)
        {
            if (d.Events.Count == 0)
            {
                Add("Events", "A performance-based payment document needs at least one completed event.");
            }
        }
        else
        {
            if (d.Lines.Count == 0)
            {
                Add("Line items", "The invoice has no line items to send.");
            }

            foreach (var line in d.Lines.Where(l => l.ItemNumber is null || !ClinPattern().IsMatch(l.ItemNumber)))
            {
                Add("Line items", line.ItemNumber is null
                    ? $"\"{line.Description}\" isn't on a CLIN. PIEE line items are CLINs or SLINs (0001, 0001AA)."
                    : $"\"{line.ItemNumber}\" isn't a CLIN or SLIN number PIEE accepts (four digits, optionally two letters: 0001, 0001AA).");
            }
        }

        if (d.Net <= 0 && d.InvoiceType != InvoiceType.CreditMemo)
        {
            Add("Amount", "The amount due must be more than zero.");
        }

        return issues;

        void Dodaac(string? value, string field, string what)
        {
            if (value is null || !DodaacPattern().IsMatch(value))
            {
                Add(field, $"Enter the 6-character DoDAAC of {what} in the contract's billing settings.");
            }
        }
    }

    public static string FileName(PieeDocument d) => $"piee-{d.InvoiceNumber}.xml";

    /// <summary>The XML import file. Call <see cref="Validate"/> first; this doesn't refuse.</summary>
    public static string ToXml(PieeDocument d, DateTime generatedAtUtc)
    {
        var header = new XElement("Header",
            new XElement("DocumentType", Label(d.Type)),
            new XElement("ContractNumber", Compact(d.ContractNumber)),
            new XElement("InvoiceNumber", d.InvoiceNumber),
            new XElement("InvoiceDate", D(d.InvoiceDate)),
            new XElement("ServicePeriodStart", D(d.PeriodFrom)),
            new XElement("ServicePeriodEnd", D(d.PeriodTo)),
            new XElement("FinalInvoice", d.IsFinal ? "Y" : "N"),
            new XElement("Vendor",
                new XElement("CageCode", d.CageCode ?? ""),
                new XElement("Uei", d.Uei ?? ""),
                new XElement("Name", d.VendorName)),
            new XElement("Routing",
                new XElement("IssueByDoDAAC", d.IssueByDodaac ?? ""),
                new XElement("AdminByDoDAAC", d.AdminByDodaac ?? ""),
                new XElement("PayOfficeDoDAAC", d.PayOfficeDodaac ?? ""),
                d.ShipToDodaac is null ? null : new XElement("ShipToDoDAAC", d.ShipToDodaac)),
            new XElement("Amounts",
                new XElement("Gross", M(d.Gross)),
                new XElement("Liquidation", M(d.Liquidation)),
                new XElement("NetAmount", M(d.Net))));

        var body = new List<XElement> { header };
        if (d.Lines.Count > 0)
        {
            body.Add(new XElement("LineItems", d.Lines.Select((l, i) => new XElement("LineItem",
                new XAttribute("sequence", i + 1),
                new XElement("ItemNumber", l.ItemNumber ?? ""),
                new XElement("Description", l.Description),
                new XElement("Quantity", l.Quantity.ToString("0.####", Inv)),
                new XElement("UnitOfMeasure", l.UnitOfMeasure),
                new XElement("UnitPrice", l.UnitPrice.ToString("0.00##", Inv)),
                new XElement("Amount", M(l.Amount))))));
        }

        if (d.ProgressPayment is { } f)
        {
            body.Add(new XElement("ProgressPaymentRequest",
                new XAttribute("form", "SF1443"),
                new XElement("RequestNumber", f.RequestNumber),
                new XElement("RequestDate", D(f.RequestDate)),
                new XElement("CostsThrough", D(f.CostsThrough)),
                new XElement("SmallBusiness", f.IsSmallBusiness ? "Y" : "N"),
                new XElement("ContractPrice", M(f.ContractPrice)),
                new XElement("ProgressPaymentRate", R(f.ProgressPaymentRate)),
                new XElement("LiquidationRate", R(f.LiquidationRate)),
                Item("11", f.Line11EligibleCosts), Item("12a", f.Line12aIncurredToDate), Item("12b", f.Line12bEstimateToComplete),
                Item("13", f.Line13), Item("14a", f.Line14aSubPaid), Item("14b", f.Line14bSubLiquidated), Item("14c", f.Line14cSubUnliquidated),
                Item("14d", f.Line14dSubApproved), Item("14e", f.Line14eSubEligible), Item("15", f.Line15), Item("16", f.Line16), Item("17", f.Line17),
                Item("18", f.Line18PreviousRequested), Item("19", f.Line19MaximumEligible), Item("20a", f.Line20aCostsDelivered),
                Item("20b", f.Line20bCostsUndelivered), Item("20c", f.Line20c), Item("20d", f.Line20dSubEligible), Item("20e", f.Line20eLimitation),
                Item("21a", f.Line21aPriceDelivered), Item("21b", f.Line21bPriceUndelivered), Item("21c", f.Line21c), Item("21d", f.Line21dUnliquidatedAdvances),
                Item("21e", f.Line21eLimitation), Item("22", f.Line22MaximumUnliquidated), Item("23", f.Line23Liquidated), Item("24", f.Line24Unliquidated),
                Item("25", f.Line25MaximumPermissible), Item("26", f.Line26Amount)));
        }

        if (d.Events.Count > 0)
        {
            body.Add(new XElement("PerformanceBasedPayments", d.Events.Select(e => new XElement("Event",
                new XElement("EventNumber", e.Number),
                new XElement("Description", e.Description),
                new XElement("CompletedDate", D(e.CompletedOn)),
                new XElement("Amount", M(e.Amount)),
                e.Evidence is null ? null : new XElement("Evidence", e.Evidence)))));
        }

        var doc = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("PieeInvoiceImport",
                new XAttribute("version", SchemaVersion),
                new XAttribute("generated", generatedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv)),
                new XElement("Invoice", body)));
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(new Utf8StringWriter(sb), new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
        {
            doc.Save(writer);
        }

        return sb.ToString();

        static XElement Item(string line, decimal amount) => new("Line", new XAttribute("item", line), M(amount));
    }

    public static string Compact(string contractNumber) => new(contractNumber.Where(char.IsLetterOrDigit).ToArray());

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    private static string M(decimal m) => m.ToString("0.00", Inv);

    private static string R(decimal r) => r.ToString("0.####", Inv);

    [GeneratedRegex("^[A-Za-z0-9-]{1,22}$")]
    private static partial Regex InvoiceNumberPattern();

    [GeneratedRegex("^[A-Z0-9]{5}$")]
    private static partial Regex CagePattern();

    [GeneratedRegex("^[A-Z0-9]{6}$")]
    private static partial Regex DodaacPattern();

    [GeneratedRegex("^[0-9]{4}([A-Z]{2})?$")]
    private static partial Regex ClinPattern();

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}

/// <summary>A PIEE file on its way out.</summary>
public sealed record PieeOutgoingDocument(Guid InvoiceId, string InvoiceNumber, string DocumentType, BillingExportFile File);

/// <summary>What a submitter did: <see cref="Transmitted"/> only when the file actually reached PIEE.</summary>
public sealed record PieeSubmitResult(bool Transmitted, string? Reference, string Message);

/// <summary>
/// Hands a PIEE file to PIEE. The built-in <see cref="ManualPieeSubmitter"/> sends nothing; register another
/// implementation (for example an EDI/SFTP gateway) to transmit. The last registration wins.
/// </summary>
public interface IPieeSubmitter
{
    string Name { get; }

    Task<PieeSubmitResult> SubmitAsync(PieeOutgoingDocument document, CancellationToken cancellationToken = default);
}

/// <summary>No live connection: the person uploads the file in PIEE and records the status by hand.</summary>
public sealed class ManualPieeSubmitter : IPieeSubmitter
{
    public string Name => "Manual upload";

    public Task<PieeSubmitResult> SubmitAsync(PieeOutgoingDocument document, CancellationToken cancellationToken = default) =>
        Task.FromResult(new PieeSubmitResult(false, null,
            $"Nothing was sent. Upload {document.File.FileName} in PIEE as a {document.DocumentType} document, then mark the invoice submitted with the PIEE document number."));
}
