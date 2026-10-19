using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>Splits voucher lines into the SF 1034 (first page) and SF 1035 continuation sheets, carrying totals forward.</summary>
internal static class Sf1034Paginator
{
    public const int FirstPageLines = 8;
    public const int ContinuationLines = 22;

    public static IReadOnlyList<VoucherPageDto> Paginate(IReadOnlyList<VoucherLineDto> lines, int firstPage = FirstPageLines, int perContinuation = ContinuationLines)
    {
        var chunks = new List<List<VoucherLineDto>> { lines.Take(firstPage).ToList() };
        for (var skip = firstPage; skip < lines.Count; skip += perContinuation)
        {
            chunks.Add(lines.Skip(skip).Take(perContinuation).ToList());
        }

        var pages = new List<VoucherPageDto>();
        var running = 0m;
        for (var i = 0; i < chunks.Count; i++)
        {
            var total = chunks[i].Sum(l => l.Amount);
            pages.Add(new VoucherPageDto(i + 1, chunks.Count, i > 0, chunks[i], running, total, running + total));
            running += total;
        }

        return pages;
    }
}

/// <summary>
/// Government billing settings per contract, the contract financing position, the SF 1034/1035 voucher and the PIEE file.
/// </summary>
public sealed partial class GovernmentBillingService(
    IWorkbenchDbContextFactory dbFactory,
    IInvoiceService invoices,
    IProgressPaymentService progressPayments,
    IEnumerable<IPieeSubmitter> submitters,
    AuditRecorder audit,
    TimeProvider clock,
    IOptions<BillingOptions> options,
    Security.SecurityEventLog? securityLog = null) : IGovernmentBillingService
{
    private readonly IPieeSubmitter _submitter = submitters.LastOrDefault() ?? new ManualPieeSubmitter();

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<GovernmentBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ctx = await BillingContext.LoadAsync(db, contractId, cancellationToken);
        return SettingsDto(ctx, await ContractFinancingLedger.SettingsAsync(db, contractId, cancellationToken));
    }

    public async Task<GovernmentBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveGovernmentBillingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProgressPaymentRate is < 0 or > 1 || request.LiquidationRate is < 0 or > 1)
        {
            throw new ArgumentException("Rates are fractions between 0 and 1 (0.80 = 80%).");
        }

        if (request.ContractPrice is <= 0)
        {
            throw new ArgumentException("The contract price must be more than zero (or leave it blank to use the contract ceiling).");
        }

        if (request.Financing == FinancingKind.ProgressPayments && (request.ProgressPaymentRate <= 0 || request.LiquidationRate <= 0))
        {
            throw new ArgumentException("Progress payments need a progress payment rate (for example 80%, or 90% for a small business) and a liquidation rate.");
        }

        var codes = new[] { request.IssueByDodaac, request.AdminByDodaac, request.PayOfficeDodaac, request.ShipToDodaac }.Select(Code).ToArray();
        if (codes.Any(c => c is not null && !DodaacPattern().IsMatch(c)))
        {
            throw new ArgumentException("A DoDAAC is 6 letters or digits.");
        }

        if (request.DeliveryDocument is not (PieeDocumentKind.TwoInOne or PieeDocumentKind.Combo or PieeDocumentKind.CostVoucher))
        {
            throw new ArgumentException("Delivery invoices go to PIEE as a 2-in-1, a Combo or a Cost Voucher.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ctx = await BillingContext.LoadAsync(db, contractId, cancellationToken);
        var settings = await db.Set<ContractGovernmentBilling>().FirstOrDefaultAsync(s => s.ContractId == contractId, cancellationToken);
        var financing = request.Financing.ToDomain();
        if ((settings?.Financing ?? ContractFinancing.None) != financing)
        {
            var live = await InvoiceGenerator.PriorInvoicesAsync(db, contractId, null, approvedOnly: false, cancellationToken);
            if (live.Any(i => ContractFinancingLedger.IsFinancing(i.Type) || ContractFinancingLedger.Liquidation(i) > 0))
            {
                throw new InvalidOperationException("This contract already has financing or liquidation on its invoices, so its financing can't change. Void those invoices first.");
            }
        }

        var before = settings is null ? null : Snapshot(settings);
        if (settings is null)
        {
            settings = new ContractGovernmentBilling { ContractId = contractId };
            db.Set<ContractGovernmentBilling>().Add(settings);
        }

        settings.Format = request.Format.ToDomain();
        settings.Financing = financing;
        settings.IsSmallBusiness = request.IsSmallBusiness;
        settings.ProgressPaymentRate = request.ProgressPaymentRate;
        settings.LiquidationRate = request.LiquidationRate;
        settings.CostBasis = request.CostBasis.ToDomain();
        settings.ContractPrice = request.ContractPrice;
        settings.IssueByDodaac = codes[0];
        settings.AdminByDodaac = codes[1];
        settings.PayOfficeDodaac = codes[2];
        settings.ShipToDodaac = codes[3];
        settings.DeliveryDocument = request.DeliveryDocument.ToDomain();
        settings.UpdatedBy = audit.Actor;
        settings.UpdatedAtUtc = UtcNow;
        audit.Record(db, nameof(ContractGovernmentBilling), contractId, "Saved", before, Snapshot(settings));
        await db.SaveChangesAsync(cancellationToken);
        return SettingsDto(ctx, settings);
    }

    public async Task<FinancingPositionDto> GetFinancingAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ctx = await BillingContext.LoadAsync(db, contractId, cancellationToken);
        var settings = await ContractFinancingLedger.SettingsAsync(db, contractId, cancellationToken);
        var live = await InvoiceGenerator.PriorInvoicesAsync(db, contractId, null, approvedOnly: false, cancellationToken);
        var billed = live.Where(i => i.IsBilled).ToList();
        var drafts = live.Where(i => i.Status == InvoiceStatus.Draft).ToList();
        var activity = live
            .Where(i => ContractFinancingLedger.Financing(i) != 0 || ContractFinancingLedger.Liquidation(i) != 0)
            .OrderBy(i => i.InvoiceDate).ThenBy(i => i.Sequence)
            .Select(i => new FinancingActivityDto(i.Id, i.Number, i.InvoiceDate, i.Type.ToContract(), i.Status.ToContract(),
                ContractFinancingLedger.Financing(i), ContractFinancingLedger.Liquidation(i), ContractFinancingLedger.Delivery(i)))
            .ToList();
        var financed = billed.Sum(ContractFinancingLedger.Financing);
        var liquidated = billed.Sum(ContractFinancingLedger.Liquidation);
        return new FinancingPositionDto(
            contractId,
            (settings?.Financing ?? ContractFinancing.None).ToContract(),
            settings is null ? 0 : await ContractFinancingLedger.LiquidationRateAsync(db, settings, ctx.Contract, cancellationToken),
            financed,
            liquidated,
            financed - liquidated,
            drafts.Sum(ContractFinancingLedger.Financing),
            drafts.Sum(ContractFinancingLedger.Liquidation),
            billed.Sum(ContractFinancingLedger.Delivery),
            activity);
    }

    public async Task<VoucherDto> GetVoucherAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        var print = await invoices.GetPrintAsync(invoiceId, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
        var ctx = await BillingContext.LoadAsync(db, invoice.ContractId, cancellationToken);
        var settings = await ContractFinancingLedger.SettingsAsync(db, invoice.ContractId, cancellationToken);
        var isCostType = invoice.Type is InvoiceType.CostPlus or InvoiceType.TrueUp or InvoiceType.TimeAndMaterials;

        // Cost-type and T&M vouchers show the amount claimed to date next to this period's, per CLIN, cost element, WBS and labor rate.
        var cumulative = new Dictionary<(Guid?, InvoiceLineKind, Guid?, Guid?, decimal), decimal>();
        if (isCostType)
        {
            var prior = (await InvoiceGenerator.PriorInvoicesAsync(db, invoice.ContractId, invoice.Id, approvedOnly: true, cancellationToken))
                .Where(i => !invoice.IsBilled || i.ApprovedAtUtc <= invoice.ApprovedAtUtc);
            foreach (var l in prior.SelectMany(i => i.Lines).Concat(invoice.Status == InvoiceStatus.Void ? [] : invoice.Lines))
            {
                var key = (l.ClinId, l.Kind, l.WbsElementId, l.LaborCategoryId, l.Kind == InvoiceLineKind.Labor ? l.Rate : 0m);
                cumulative[key] = cumulative.GetValueOrDefault(key) + l.Amount;
            }
        }

        var byId = invoice.Lines.ToDictionary(l => l.Id);
        var lines = print.Invoice.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var hourly = l.Kind == InvoiceLineType.Labor && l.Rate != 0 && l.Quantity != 0;
            decimal? cum = isCostType && byId.TryGetValue(l.Id, out var d) ? cumulative.GetValueOrDefault((d.ClinId, d.Kind, d.WbsElementId, d.LaborCategoryId, d.Kind == InvoiceLineKind.Labor ? d.Rate : 0m)) : null;
            return new VoucherLineDto(l.ClinNumber, l.Description, hourly ? l.Quantity : null, hourly ? "HR" : null, hourly ? l.Rate : null, l.Amount, cum);
        }).ToList();

        var liquidation = ContractFinancingLedger.Liquidation(invoice);
        var request = invoice.Type == InvoiceType.ProgressPayment
            ? await db.Set<ProgressPaymentRequest>().AsNoTracking().Where(r => r.InvoiceId == invoice.Id).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken)
            : null;
        var company = options.Value;
        return new VoucherDto(
            print,
            (settings?.Format ?? InvoiceFormat.Custom).ToContract(),
            isCostType,
            PaymentKind(invoice),
            Sf1034Paginator.Paginate(lines),
            invoice.Total + liquidation,
            liquidation,
            company.Uei,
            ctx.Settings?.CageCode ?? company.CageCode,
            request);
    }

    public async Task<PieeStatusDto> GetPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (invoice, document) = await DocumentAsync(db, invoiceId, cancellationToken);
        var row = await db.Set<PieeSubmission>().AsNoTracking().FirstOrDefaultAsync(p => p.InvoiceId == invoiceId, cancellationToken);
        return StatusDto(invoice, document, row);
    }

    public async Task<BillingExportFile> ExportPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (invoice, file, _) = await ExportCoreAsync(db, invoiceId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await LogExportAsync(file, invoice, cancellationToken);
        return file;
    }

    public async Task<PieeSubmitResultDto> SubmitPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (invoice, file, document) = await ExportCoreAsync(db, invoiceId, cancellationToken);
        var row = await RowAsync(db, invoice, document, cancellationToken);
        var result = await _submitter.SubmitAsync(new PieeOutgoingDocument(invoice.Id, invoice.Number, PieeInvoiceFile.Label(document.Type), file), cancellationToken);
        if (result.Transmitted)
        {
            Transition(row, PieeSubmissionStatus.Submitted, result.Reference, null);
        }

        audit.Record(db, InvoiceService.AuditEntityType, invoice.Id, "PieeSubmit",
            after: new { Submitter = _submitter.Name, result.Transmitted, result.Reference, Status = row.Status.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        await LogExportAsync(file, invoice, cancellationToken);
        return new PieeSubmitResultDto(StatusDto(invoice, document, row), result.Transmitted, result.Message, file);
    }

    public async Task<PieeStatusDto> SetPieeStatusAsync(Guid invoiceId, SetPieeStatusRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (invoice, document) = await DocumentAsync(db, invoiceId, cancellationToken);
        var row = await RowAsync(db, invoice, document, cancellationToken);
        var status = request.Status.ToDomain();
        var reference = InvoiceService.Clean(request.Reference, 100);
        var reason = InvoiceService.Clean(request.Reason, 1000);
        switch (status)
        {
            case PieeSubmissionStatus.Submitted:
                if (PieeInvoiceFile.Validate(document) is { Count: > 0 } issues)
                {
                    throw new InvalidOperationException("Fix these before marking it submitted: " + string.Join(" ", issues.Select(i => i.Message)));
                }

                if (row.Status == PieeSubmissionStatus.Accepted)
                {
                    throw new InvalidOperationException("PIEE already accepted this invoice.");
                }

                break;
            case PieeSubmissionStatus.Accepted when row.Status != PieeSubmissionStatus.Submitted:
                throw new InvalidOperationException("Mark the invoice submitted before recording that PIEE accepted it.");
            case PieeSubmissionStatus.Rejected when row.Status != PieeSubmissionStatus.Submitted:
                throw new InvalidOperationException("Mark the invoice submitted before recording that PIEE rejected it.");
            case PieeSubmissionStatus.Rejected when reason is null:
                throw new ArgumentException("Enter the reason PIEE gave for rejecting the invoice.");
            case PieeSubmissionStatus.NotSubmitted when row.Status == PieeSubmissionStatus.Accepted:
                throw new InvalidOperationException("PIEE accepted this invoice; its status can't be reset.");
        }

        var before = new { Status = row.Status.ToString(), row.Reference, row.RejectionReason };
        Transition(row, status, reference, reason);
        audit.Record(db, InvoiceService.AuditEntityType, invoice.Id, "PieeStatus", before, new { Status = row.Status.ToString(), row.Reference, row.RejectionReason }, reason);
        if (status == PieeSubmissionStatus.Submitted)
        {
            // Submitting in PIEE is sending the invoice.
            var tracked = await db.Set<Invoice>().FirstAsync(i => i.Id == invoice.Id, cancellationToken);
            if (tracked.SentAtUtc is null)
            {
                tracked.SentAtUtc = UtcNow;
                tracked.SentBy = audit.Actor;
                tracked.UpdatePaymentStatus();
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return StatusDto(invoice, document, row);
    }

    // ---- helpers ----

    private void Transition(PieeSubmission row, PieeSubmissionStatus status, string? reference, string? reason)
    {
        row.Status = status;
        row.StatusChangedAtUtc = UtcNow;
        row.StatusChangedBy = audit.Actor;
        if (status == PieeSubmissionStatus.Submitted)
        {
            row.SubmittedAtUtc = UtcNow;
            row.SubmittedBy = audit.Actor;
            row.Reference = reference ?? row.Reference;
            row.RejectionReason = null;
        }
        else if (status == PieeSubmissionStatus.Rejected)
        {
            row.RejectionReason = reason;
        }
        else if (status == PieeSubmissionStatus.NotSubmitted)
        {
            row.SubmittedAtUtc = null;
            row.SubmittedBy = null;
            row.RejectionReason = null;
        }
    }

    private async Task<(Invoice Invoice, BillingExportFile File, PieeDocument Document)> ExportCoreAsync(IWorkbenchDbContext db, Guid invoiceId, CancellationToken ct)
    {
        var (invoice, document) = await DocumentAsync(db, invoiceId, ct);
        var issues = PieeInvoiceFile.Validate(document);
        if (issues.Count > 0)
        {
            throw new InvalidOperationException("The PIEE file can't be made yet. " + string.Join(" ", issues.Select(i => $"{i.Field}: {i.Message}")));
        }

        var file = new BillingExportFile(PieeInvoiceFile.FileName(document), "application/xml", PieeInvoiceFile.ToXml(document, UtcNow));
        var row = await RowAsync(db, invoice, document, ct);
        row.FileName = file.FileName;
        row.ExportedAtUtc = UtcNow;
        row.ExportedBy = audit.Actor;
        row.DocumentType = document.Type;
        return (invoice, file, document);
    }

    private async Task LogExportAsync(BillingExportFile file, Invoice invoice, CancellationToken ct)
    {
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(Contracts.Security.SecurityEventTypes.ExportFinancialCsv, file.FileName, detail: $"PIEE invoice file, {invoice.Lines.Count} lines", cancellationToken: ct);
        }
    }

    private static async Task<PieeSubmission> RowAsync(IWorkbenchDbContext db, Invoice invoice, PieeDocument document, CancellationToken ct)
    {
        var row = db.Set<PieeSubmission>().Local.FirstOrDefault(p => p.InvoiceId == invoice.Id)
            ?? await db.Set<PieeSubmission>().FirstOrDefaultAsync(p => p.InvoiceId == invoice.Id, ct);
        if (row is null)
        {
            row = new PieeSubmission { InvoiceId = invoice.Id, DocumentType = document.Type };
            db.Set<PieeSubmission>().Add(row);
        }

        return row;
    }

    private async Task<(Invoice Invoice, PieeDocument Document)> DocumentAsync(IWorkbenchDbContext db, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).ThenInclude(l => l.Sources).FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
            ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
        invoice.Lines.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));
        var ctx = await BillingContext.LoadAsync(db, invoice.ContractId, ct);
        var settings = await ContractFinancingLedger.SettingsAsync(db, invoice.ContractId, ct);
        var type = PieeInvoiceFile.DocumentFor(invoice.Type, settings?.DeliveryDocument ?? PieeDocumentType.TwoInOne);
        var company = options.Value;

        var lines = invoice.Lines.Where(l => l.Kind != InvoiceLineKind.Liquidation && !ContractFinancingLedger.IsFinancingLine(l.Kind))
            .GroupBy(l => l.ClinId)
            .Select(g =>
            {
                var clin = ctx.Clin(g.Key);
                var amount = g.Sum(l => l.Amount);
                return new PieeLineItem(clin?.Number, clin?.Description ?? g.First().Description, 1, "LO", amount, amount);
            })
            .OrderBy(l => l.ItemNumber, StringComparer.Ordinal)
            .ToList();

        Sf1443Dto? form = null;
        if (invoice.Type == InvoiceType.ProgressPayment
            && await db.Set<ProgressPaymentRequest>().AsNoTracking().Where(r => r.InvoiceId == invoice.Id).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct) is { } requestId)
        {
            form = (await progressPayments.GetAsync(requestId, ct)).Form;
        }

        var events = new List<PieeEvent>();
        var eventIds = invoice.Lines.SelectMany(l => l.Sources).Where(s => s.Kind == BilledSourceKind.PerformanceEvent).Select(s => s.SourceId).ToList();
        if (eventIds.Count > 0)
        {
            events = (await db.Set<PerformanceBasedPaymentEvent>().AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToListAsync(ct))
                .OrderBy(e => e.Order)
                .Select(e => new PieeEvent(e.Order, e.Description, e.CompletedDate ?? invoice.PeriodTo, e.Amount, e.Evidence))
                .ToList();
        }

        var liquidation = ContractFinancingLedger.Liquidation(invoice);
        var document = new PieeDocument(type, invoice.Type, invoice.Status, invoice.Number, invoice.InvoiceDate, invoice.PeriodFrom, invoice.PeriodTo,
            invoice.ReleasesWithhold, ctx.Contract.ContractNumber, Code(ctx.Settings?.CageCode ?? company.CageCode), company.Uei,
            string.IsNullOrWhiteSpace(company.CompanyName) ? "Contractor" : company.CompanyName,
            settings?.IssueByDodaac, settings?.AdminByDodaac, settings?.PayOfficeDodaac, settings?.ShipToDodaac,
            lines, invoice.Total + liquidation, liquidation, invoice.Total, form, events);
        return (invoice, document);
    }

    private PieeStatusDto StatusDto(Invoice invoice, PieeDocument document, PieeSubmission? row) => new(
        invoice.Id,
        invoice.Number,
        document.Type.ToContract(),
        (row?.Status ?? PieeSubmissionStatus.NotSubmitted).ToContract(),
        PieeInvoiceFile.Validate(document),
        row?.FileName,
        row?.ExportedAtUtc,
        row?.Reference,
        row?.SubmittedAtUtc,
        row?.SubmittedBy,
        row?.RejectionReason,
        row?.StatusChangedAtUtc,
        row?.StatusChangedBy,
        _submitter.Name);

    private static string PaymentKind(Invoice invoice) => invoice.Type switch
    {
        InvoiceType.ProgressPayment => "Progress",
        InvoiceType.PerformanceBased => "Performance-based",
        InvoiceType.CreditMemo => "Credit",
        InvoiceType.CostPlus or InvoiceType.TrueUp => invoice.ReleasesWithhold ? "Final" : "Provisional",
        _ => invoice.ReleasesWithhold ? "Final" : "Partial",
    };

    private GovernmentBillingSettingsDto SettingsDto(BillingContext ctx, ContractGovernmentBilling? s) => new(
        ctx.Contract.Id,
        ctx.Contract.ContractNumber,
        (s?.Format ?? InvoiceFormat.Custom).ToContract(),
        (s?.Financing ?? ContractFinancing.None).ToContract(),
        s?.IsSmallBusiness ?? false,
        s?.ProgressPaymentRate ?? 0,
        s?.LiquidationRate ?? 0,
        (s?.CostBasis ?? ProgressPaymentCostBasis.PaidCosts).ToContract(),
        s?.ContractPrice,
        ContractFinancingLedger.ContractPrice(s, ctx.Contract),
        s?.IssueByDodaac,
        s?.AdminByDodaac,
        s?.PayOfficeDodaac,
        s?.ShipToDodaac,
        (s?.DeliveryDocument ?? PieeDocumentType.TwoInOne).ToContract(),
        ctx.Settings?.CageCode ?? options.Value.CageCode);

    private static object Snapshot(ContractGovernmentBilling s) => new
    {
        Format = s.Format.ToString(),
        Financing = s.Financing.ToString(),
        s.IsSmallBusiness,
        s.ProgressPaymentRate,
        s.LiquidationRate,
        CostBasis = s.CostBasis.ToString(),
        s.ContractPrice,
        s.IssueByDodaac,
        s.AdminByDodaac,
        s.PayOfficeDodaac,
        s.ShipToDodaac,
        DeliveryDocument = s.DeliveryDocument.ToString(),
    };

    private static string? Code(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    [GeneratedRegex("^[A-Z0-9]{6}$")]
    private static partial Regex DodaacPattern();
}
