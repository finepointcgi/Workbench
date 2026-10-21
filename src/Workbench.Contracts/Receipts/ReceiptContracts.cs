using Workbench.Contracts.Odcs;

namespace Workbench.Contracts.Receipts;

// Mirrors Workbench.Domain.Files.ReceiptKind / ReceiptReadSource by name and value.
public enum ReceiptDocumentKind { Receipt, LodgingFolio, CardStatement, Other }

public enum ReceiptReadMethod { None, TextLayer, Assistant }

/// <summary>What a charge on a receipt is. Lodging folios split into room, lodging taxes and everything else.</summary>
public enum ReceiptChargeKind { Room, LodgingTax, Parking, Food, Phone, Fee, Other }

/// <summary>One charge read from a receipt (a folio's room night, tax, parking, room service, ...).</summary>
public sealed record ReceiptChargeDto(DateOnly? Date, string Label, decimal Amount, ReceiptChargeKind Kind);

/// <summary>Names used in <see cref="ReceiptExtractionDto.FromReceipt"/>: the fields that came from the receipt.</summary>
public static class ReceiptFields
{
    public const string Vendor = "vendor";
    public const string Location = "location";
    public const string Date = "date";
    public const string CheckIn = "checkIn";
    public const string CheckOut = "checkOut";
    public const string Nights = "nights";
    public const string NightlyRate = "nightlyRate";
    public const string RoomTotal = "roomTotal";
    public const string LodgingTaxes = "lodgingTaxes";
    public const string OtherCharges = "otherCharges";
    public const string Total = "total";
}

/// <summary>
/// What was read from a receipt. Every value is a suggestion: the expense line dialog fills its fields from it and the
/// person confirms or edits them before anything is saved. <see cref="FromReceipt"/> names the fields that came from the
/// receipt (<see cref="ReceiptFields"/>); <see cref="ReadBy"/> says, per field, whether the text layer or the assistant read it.
/// </summary>
public sealed record ReceiptExtractionDto(
    ReceiptDocumentKind Kind,
    ReceiptReadMethod Method,
    string? Vendor,
    string? City,
    string? State,
    DateOnly? Date,
    DateOnly? CheckIn,
    DateOnly? CheckOut,
    int? Nights,
    decimal? NightlyRate,
    decimal? RoomTotal,
    decimal? LodgingTaxes,
    decimal? OtherCharges,
    decimal? Total,
    IReadOnlyList<ReceiptChargeDto> Charges,
    IReadOnlyList<string> FromReceipt,
    IReadOnlyDictionary<string, ReceiptReadMethod> ReadBy,
    IReadOnlyList<string> Notes)
{
    public static ReceiptExtractionDto Empty(IReadOnlyList<string>? notes = null) =>
        new(ReceiptDocumentKind.Receipt, ReceiptReadMethod.None, null, null, null, null, null, null, null, null, null, null, null, null,
            [], [], new Dictionary<string, ReceiptReadMethod>(), notes ?? []);

    public bool IsLodging => Kind == ReceiptDocumentKind.LodgingFolio;

    public bool Found(string field) => FromReceipt.Contains(field);

    public string? Location => City is null ? null : State is null ? City : $"{City}, {State}";

    /// <summary>Room charges plus lodging taxes: the lodging line's amount.</summary>
    public decimal? LodgingAmount => RoomTotal is null && LodgingTaxes is null ? null : (RoomTotal ?? 0m) + (LodgingTaxes ?? 0m);

    /// <summary>The non-lodging charges grouped by kind (parking, room service, ...), for "add as separate lines".</summary>
    public IReadOnlyList<ReceiptChargeDto> OtherChargeGroups => Charges
        .Where(c => c.Kind is not (ReceiptChargeKind.Room or ReceiptChargeKind.LodgingTax))
        .GroupBy(c => c.Kind)
        .Select(g => new ReceiptChargeDto(g.Min(c => c.Date), g.Count() == 1 ? g.First().Label : ReceiptChargeLabels.For(g.Key), g.Sum(c => c.Amount), g.Key))
        .ToList();
}

public static class ReceiptChargeLabels
{
    public static string For(ReceiptChargeKind kind) => kind switch
    {
        ReceiptChargeKind.Room => "Room",
        ReceiptChargeKind.LodgingTax => "Lodging taxes",
        ReceiptChargeKind.Parking => "Parking",
        ReceiptChargeKind.Food => "Food and drink",
        ReceiptChargeKind.Phone => "Phone and internet",
        ReceiptChargeKind.Fee => "Fees",
        _ => "Other charges",
    };
}

/// <summary>One night of a stay against the GSA lodging maximum for that date and place.</summary>
public sealed record LodgingNightCheckDto(DateOnly Date, decimal Rate, decimal LodgingMax)
{
    public bool IsOver => Rate > LodgingMax;

    public decimal Over => Math.Max(0m, Rate - LodgingMax);
}

/// <summary>
/// The nightly room rate against the GSA per diem lodging maximum (taxes excluded, as GSA rates are). The place is the
/// expense report's own per diem or travel destination when it covers the stay, else the folio's city.
/// </summary>
public sealed record LodgingRateCheckDto(
    string Location,
    string RateDescription,
    bool IsStandardFallback,
    IReadOnlyList<LodgingNightCheckDto> Nights,
    bool OverIsUnallowable,
    IReadOnlyList<string> Notes)
{
    public int NightsOver => Nights.Count(n => n.IsOver);

    public decimal AmountOver => Nights.Sum(n => n.Over);

    public bool IsOver => NightsOver > 0;

    public decimal? LodgingMax => Nights.Count == 0 ? null : Nights.Max(n => n.LodgingMax);
}

/// <summary>Re-checks lodging after the person edits the suggested values.</summary>
public sealed record LodgingRateCheckRequest(DateOnly CheckIn, int Nights, decimal NightlyRate, string? City = null, string? State = null, Guid? ExpenseReportId = null);

/// <summary>An expense line (or card charge line) that uses a file. The same file can back several lines.</summary>
public sealed record ReceiptUseDto(
    Guid LineId,
    Guid? ExpenseReportId,
    string? ExpenseReportTitle,
    DateOnly IncurredOn,
    string Description,
    decimal Amount,
    OdcApprovalState Status);

public sealed record ReceiptDto(
    Guid Id,
    string FileName,
    string ContentType,
    long SizeBytes,
    DateTime UploadedAtUtc,
    string UploadedBy,
    ReceiptDocumentKind Kind,
    string? Vendor,
    string? Location,
    DateOnly? DocumentDate,
    decimal? Total,
    ReceiptReadMethod ReadMethod,
    IReadOnlyList<ReceiptUseDto> Uses)
{
    public bool IsInUse => Uses.Count > 0;

    public bool IsImage => ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public bool IsPdf => string.Equals(ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A library file with what was read from it, and, for a lodging folio, the per diem check.</summary>
public sealed record ReceiptReadingDto(ReceiptDto Receipt, ReceiptExtractionDto Extraction, LodgingRateCheckDto? LodgingCheck);

/// <summary>Library search: by name or vendor text, date range and amount range. Unattached files come first.</summary>
public sealed record ReceiptQuery(
    string? Search = null,
    DateOnly? From = null,
    DateOnly? To = null,
    decimal? MinAmount = null,
    decimal? MaxAmount = null,
    bool UnattachedOnly = false,
    int Take = 200);

/// <summary>The owner's corrections to a library file's details (what it is, who from, when, how much).</summary>
public sealed record UpdateReceiptRequest(ReceiptDocumentKind Kind, string? Vendor = null, DateOnly? DocumentDate = null, decimal? Total = null, string? Location = null);

/// <summary>
/// "My receipts": the files the signed-in person uploaded (receipts, folios, card statements), stored once and attached to
/// any number of their expense lines (<see cref="SaveExpenseLineRequest.ReceiptFileId"/>). People see only their own
/// library; approvers and Finance can open a file that's attached to an expense line they can see. A file in use can't be
/// deleted, and a file that was ever attached is kept (hidden) when removed.
/// </summary>
public interface IReceiptLibraryService
{
    Task<IReadOnlyList<ReceiptDto>> ListMineAsync(ReceiptQuery? query = null, CancellationToken cancellationToken = default);

    Task<ReceiptDto> GetAsync(Guid fileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a file to the library and reads it: the PDF text layer first, then (only when an admin turned the assistant on)
    /// the assistant for what the text didn't give. <paramref name="expenseReportId"/> gives the per diem check its destination.
    /// </summary>
    Task<ReceiptReadingDto> UploadAsync(string fileName, Stream content, Guid? expenseReportId = null, CancellationToken cancellationToken = default);

    /// <summary>What was read from a library file (stored when it was uploaded), with a fresh per diem check.</summary>
    Task<ReceiptReadingDto> ReadAsync(Guid fileId, Guid? expenseReportId = null, CancellationToken cancellationToken = default);

    Task<LodgingRateCheckDto> CheckLodgingAsync(LodgingRateCheckRequest request, CancellationToken cancellationToken = default);

    Task<ReceiptDto> UpdateAsync(Guid fileId, UpdateReceiptRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes a file from the library. Refused while any line uses it.</summary>
    Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default);

    Task<ReceiptContent> OpenAsync(Guid fileId, CancellationToken cancellationToken = default);

    string GetContentUrl(Guid fileId);
}
