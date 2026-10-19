using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Revenue;

namespace Workbench.Application.Billing;

internal static class GovernmentBillingMapping
{
    public static VoucherFormat ToContract(this InvoiceFormat f) => (VoucherFormat)(int)f;

    public static InvoiceFormat ToDomain(this VoucherFormat f) => (InvoiceFormat)(int)f;

    public static FinancingKind ToContract(this ContractFinancing f) => (FinancingKind)(int)f;

    public static ContractFinancing ToDomain(this FinancingKind f) => (ContractFinancing)(int)f;

    public static ProgressPaymentBasis ToContract(this ProgressPaymentCostBasis b) => (ProgressPaymentBasis)(int)b;

    public static ProgressPaymentCostBasis ToDomain(this ProgressPaymentBasis b) => (ProgressPaymentCostBasis)(int)b;

    public static PieeDocumentKind ToContract(this PieeDocumentType t) => (PieeDocumentKind)(int)t;

    public static PieeDocumentType ToDomain(this PieeDocumentKind t) => (PieeDocumentType)(int)t;

    public static PieeState ToContract(this PieeSubmissionStatus s) => (PieeState)(int)s;

    public static PieeSubmissionStatus ToDomain(this PieeState s) => (PieeSubmissionStatus)(int)s;
}

/// <summary>
/// Progress payments based on cost (FAR 52.232-16). A request computes the SF 1443 from the project ledger through a date:
/// incurred cost (burdened at provisional rates, allowable only) from the contract start, less what is still owed to
/// vendors on approved AP bills for the paid-cost basis; the estimate to complete from the projects' approved estimates at
/// completion; previous requests, liquidations and deliveries from the contract's invoices. Creating a request saves those
/// figures and a draft ProgressPayment invoice for line 26; approving that invoice posts it to contract financing (2320).
/// </summary>
public sealed class ProgressPaymentService(
    IWorkbenchDbContextFactory dbFactory,
    BillingCostSource costs,
    AuditRecorder audit,
    TimeProvider clock,
    IOptions<BillingOptions> options) : IProgressPaymentService
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<ProgressPaymentRequestDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ctx = await BillingContext.LoadAsync(db, contractId, cancellationToken);
        var settings = await ContractFinancingLedger.SettingsAsync(db, contractId, cancellationToken);
        var requests = await db.Set<ProgressPaymentRequest>().AsNoTracking().Where(r => r.ContractId == contractId).ToListAsync(cancellationToken);
        var invoices = await InvoicesAsync(db, requests, cancellationToken);
        return requests.OrderByDescending(r => r.Number).Select(r => Dto(r, ctx, settings, invoices)).ToList();
    }

    public async Task<ProgressPaymentRequestDto> GetAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var request = await db.Set<ProgressPaymentRequest>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Progress payment request", requestId);
        var ctx = await BillingContext.LoadAsync(db, request.ContractId, cancellationToken);
        var settings = await ContractFinancingLedger.SettingsAsync(db, request.ContractId, cancellationToken);
        return Dto(request, ctx, settings, await InvoicesAsync(db, [request], cancellationToken));
    }

    public async Task<Sf1443Dto> PreviewAsync(ProgressPaymentPreviewRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (_, _, row) = await BuildAsync(db, request, cancellationToken);
        return Sf1443Calculator.Compute(Inputs(row, await BillingContext.LoadAsync(db, request.ContractId, cancellationToken),
            await ContractFinancingLedger.SettingsAsync(db, request.ContractId, cancellationToken)));
    }

    public async Task<ProgressPaymentRequestDto> CreateAsync(ProgressPaymentPreviewRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (ctx, settings, row) = await BuildAsync(db, request, cancellationToken);
        var form = Sf1443Calculator.Compute(Inputs(row, ctx, settings));
        if (form.Line26Amount <= 0)
        {
            throw new ArgumentException("There's no progress payment to request. " + string.Join(" ", form.Notes));
        }

        row.Amount = form.Line26Amount;
        row.Notes = InvoiceService.Clean(request.Notes, 2000);
        var previous = (await db.Set<ProgressPaymentRequest>().AsNoTracking().Where(r => r.ContractId == ctx.Contract.Id).Select(r => r.CostsThrough).ToListAsync(cancellationToken))
            .DefaultIfEmpty().Max() is { } last && last != default ? last : (DateOnly?)null;
        var (sequence, number) = await InvoiceService.NextNumberAsync(db, ctx, cancellationToken);
        var invoice = new Invoice
        {
            ContractId = ctx.Contract.Id,
            CustomerId = ctx.Contract.CustomerId,
            Sequence = sequence,
            Number = number,
            Type = InvoiceType.ProgressPayment,
            PeriodFrom = previous is { } p && p < row.CostsThrough ? p.AddDays(1) : ctx.Contract.PeriodStart <= row.CostsThrough ? ctx.Contract.PeriodStart : row.CostsThrough,
            PeriodTo = row.CostsThrough,
            InvoiceDate = row.RequestDate,
            PaymentTermsDays = ctx.PaymentTermsDays,
            DueDate = row.RequestDate.AddDays(ctx.PaymentTermsDays),
            Notes = $"Progress payment request no. {row.Number} (SF 1443), costs through {row.CostsThrough:yyyy-MM-dd}.",
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
            UpdatedAtUtc = UtcNow,
        };
        invoice.Lines.Add(new InvoiceLine
        {
            InvoiceId = invoice.Id,
            LineNumber = 1,
            Kind = InvoiceLineKind.ProgressPayment,
            Description = $"Progress payment request no. {row.Number}: {row.ProgressPaymentRate:P0} of eligible costs through {row.CostsThrough:yyyy-MM-dd} (SF 1443 line 26)",
            Quantity = 1,
            Rate = row.Amount,
            Amount = row.Amount,
            IsGenerated = false,
        });
        invoice.Recalculate();
        row.InvoiceId = invoice.Id;
        db.Set<Invoice>().Add(invoice);
        db.Set<ProgressPaymentRequest>().Add(row);
        audit.Record(db, nameof(ProgressPaymentRequest), row.Id, "Created",
            after: new { row.Number, row.CostsThrough, row.Amount, Invoice = invoice.Number, Line11 = form.Line11EligibleCosts, Line19 = form.Line19MaximumEligible, Line25 = form.Line25MaximumPermissible });
        audit.Record(db, InvoiceService.AuditEntityType, invoice.Id, "Generated", after: new { invoice.Number, Type = invoice.Type.ToString(), invoice.Total });
        await db.SaveChangesAsync(cancellationToken);
        return Dto(row, ctx, settings, new Dictionary<Guid, Invoice> { [invoice.Id] = invoice });
    }

    public async Task DeleteAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var request = await db.Set<ProgressPaymentRequest>().FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Progress payment request", requestId);
        var invoice = request.InvoiceId is { } id
            ? await db.Set<Invoice>().Include(i => i.Lines).ThenInclude(l => l.Sources).FirstOrDefaultAsync(i => i.Id == id, cancellationToken)
            : null;
        if (invoice is not null && invoice.Status is not (InvoiceStatus.Draft or InvoiceStatus.Void))
        {
            throw new InvalidOperationException($"Invoice {invoice.Number} for this request is approved. Void it first.");
        }

        if (await db.Set<ProgressPaymentRequest>().AnyAsync(r => r.ContractId == request.ContractId && r.Number > request.Number, cancellationToken))
        {
            throw new InvalidOperationException("Only the latest request can be deleted: later requests count this one as previously requested.");
        }

        db.Set<ProgressPaymentRequest>().Remove(request);
        if (invoice is { Status: InvoiceStatus.Draft })
        {
            db.Set<PieeSubmission>().RemoveRange(await db.Set<PieeSubmission>().Where(p => p.InvoiceId == invoice.Id).ToListAsync(cancellationToken));
            db.Set<InvoiceLine>().RemoveRange(invoice.Lines);
            db.Set<Invoice>().Remove(invoice);
            audit.Record(db, InvoiceService.AuditEntityType, invoice.Id, "DraftDeleted", before: new { invoice.Number, invoice.Total });
        }

        audit.Record(db, nameof(ProgressPaymentRequest), request.Id, "Deleted", before: new { request.Number, request.Amount });
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- computing ----

    private async Task<(BillingContext Ctx, ContractGovernmentBilling Settings, ProgressPaymentRequest Row)> BuildAsync(
        IWorkbenchDbContext db, ProgressPaymentPreviewRequest request, CancellationToken ct)
    {
        var ctx = await BillingContext.LoadAsync(db, request.ContractId, ct);
        var settings = await ContractFinancingLedger.SettingsAsync(db, request.ContractId, ct);
        if (settings is not { Financing: ContractFinancing.ProgressPayments })
        {
            throw new ArgumentException($"Contract {ctx.Contract.ContractNumber} doesn't use progress payments. Turn them on in its billing settings.");
        }

        if (settings.ProgressPaymentRate is <= 0 or > 1 || settings.LiquidationRate is <= 0 or > 1)
        {
            throw new ArgumentException("Set the progress payment rate and the liquidation rate in the contract's billing settings first.");
        }

        var through = request.CostsThrough;
        var requestDate = request.RequestDate ?? Today;
        if (through > requestDate)
        {
            throw new ArgumentException("Costs can only be claimed through the request date or earlier.");
        }

        var o = request.Overrides ?? new Sf1443Overrides();
        if (new[] { o.EstimateToComplete, o.SubcontractorPaid, o.SubcontractorLiquidated, o.SubcontractorApproved, o.CostsOfDelivered, o.PriceOfDelivered, o.UnliquidatedAdvances, o.LiquidatedToDate }
            .Any(v => v is < 0))
        {
            throw new ArgumentException("SF 1443 amounts can't be negative.");
        }

        var start = ctx.Contract.PeriodStart <= through ? ctx.Contract.PeriodStart : through;
        var incurred = 0m;
        foreach (var project in ctx.Projects)
        {
            incurred += (await costs.GetProjectCostAsync(db, project.Id, start, through, RateBasis.Provisional, ct)).Lines.Sum(l => l.TotalCost);
        }

        var unpaid = Math.Min(incurred, await UnpaidVendorCostAsync(db, ctx, through, ct));
        var estimate = await EstimateAtCompletionAsync(db, ctx, through, ct);
        var live = await InvoiceGenerator.PriorInvoicesAsync(db, ctx.Contract.Id, null, approvedOnly: false, ct);
        var price = ContractFinancingLedger.ContractPrice(settings, ctx.Contract);
        var previous = live.Where(i => i.Type == InvoiceType.ProgressPayment).Sum(ContractFinancingLedger.Financing);
        var deliveredPrice = live.Where(i => i.IsBilled).Sum(ContractFinancingLedger.Delivery);
        var etc = o.EstimateToComplete ?? (estimate is { } eac ? Math.Max(0, eac - incurred) : 0);
        var eligible = settings.CostBasis == ProgressPaymentCostBasis.PaidCosts ? incurred - unpaid : incurred;

        // Costs of delivered items: the delivered share of the contract price applied to the estimate at completion.
        var defaultCostsDelivered = price > 0 && deliveredPrice > 0
            ? Math.Min(eligible, Money.Round(deliveredPrice * (incurred + etc > 0 ? (incurred + etc) / price : 0)))
            : 0;

        var number = (await db.Set<ProgressPaymentRequest>().Where(r => r.ContractId == ctx.Contract.Id).MaxAsync(r => (int?)r.Number, ct) ?? 0) + 1;
        var row = new ProgressPaymentRequest
        {
            ContractId = ctx.Contract.Id,
            Number = number,
            RequestDate = requestDate,
            CostsThrough = through,
            IsSmallBusiness = settings.IsSmallBusiness,
            ContractPrice = price,
            ProgressPaymentRate = settings.ProgressPaymentRate,
            LiquidationRate = settings.LiquidationRate,
            CostBasis = settings.CostBasis,
            PaidCosts = incurred - unpaid,
            UnpaidCosts = unpaid,
            IncurredToDate = incurred,
            EstimateToComplete = etc,
            SubcontractorPaid = o.SubcontractorPaid ?? 0,
            SubcontractorLiquidated = o.SubcontractorLiquidated ?? 0,
            SubcontractorApproved = o.SubcontractorApproved ?? 0,
            PreviousRequested = previous,
            CostsOfDelivered = o.CostsOfDelivered ?? defaultCostsDelivered,
            PriceOfDelivered = o.PriceOfDelivered ?? deliveredPrice,
            UnliquidatedAdvances = o.UnliquidatedAdvances ?? 0,
            LiquidatedToDate = o.LiquidatedToDate ?? live.Sum(ContractFinancingLedger.Liquidation),
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        return (ctx, settings, row);
    }

    /// <summary>
    /// What is still owed to vendors on approved or posted AP bills dated through <paramref name="through"/>, for the
    /// contract's project-cost and subcontract lines (each line's share of the bill's unpaid balance).
    /// </summary>
    internal static async Task<decimal> UnpaidVendorCostAsync(IWorkbenchDbContext db, BillingContext ctx, DateOnly through, CancellationToken ct)
    {
        var projectIds = ctx.Projects.Select(p => p.Id).ToList();
        var lines = await (
                from l in db.Set<ApBillLine>().AsNoTracking()
                join b in db.Set<ApBill>().AsNoTracking() on l.BillId equals b.Id
                where l.ProjectId != null && projectIds.Contains(l.ProjectId.Value) && b.InvoiceDate <= through
                      && (b.Status == ApBillStatus.Approved || b.Status == ApBillStatus.Posted) && l.Kind != ApBillLineKind.Account
                select new { l.Amount, l.RetainageAmount, Bill = b.Amount, BillRetainage = b.RetainageAmount, b.PaidAmount })
            .ToListAsync(ct);
        return Money.Round(lines.Sum(l =>
        {
            var payable = l.Bill - l.BillRetainage;
            var unpaidShare = payable > 0 ? Math.Clamp((payable - l.PaidAmount) / payable, 0, 1) : 0;
            return (l.Amount - l.RetainageAmount) * unpaidShare + l.RetainageAmount;
        }));
    }

    /// <summary>The latest approved estimate at completion (cost) of each project, as of the date; null when none has one.</summary>
    private static async Task<decimal?> EstimateAtCompletionAsync(IWorkbenchDbContext db, BillingContext ctx, DateOnly through, CancellationToken ct)
    {
        var projectIds = ctx.Projects.Select(p => p.Id).ToList();
        var estimates = await db.Set<RevenueEstimate>().AsNoTracking()
            .Where(e => projectIds.Contains(e.ProjectId) && e.ClinId == null && e.Status == EstimateStatus.Approved && e.AsOf <= through)
            .ToListAsync(ct);
        return estimates.Count == 0 ? null : estimates.GroupBy(e => e.ProjectId).Sum(g => g.OrderByDescending(e => e.AsOf).First().EstimatedCost);
    }

    private Sf1443Inputs Inputs(ProgressPaymentRequest r, BillingContext ctx, ContractGovernmentBilling? settings)
    {
        var company = options.Value;
        var to = string.Join(", ", new[] { ctx.Customer.Name, settings?.AdminByDodaac is { } admin ? $"admin office {admin}" : ctx.Settings?.AdminOffice }.OfType<string>());
        var from = string.Join(", ", new[]
        {
            string.IsNullOrWhiteSpace(company.CompanyName) ? "Contractor (set Billing:CompanyName)" : company.CompanyName,
            company.RemitToAddress?.Replace("\n", ", "),
            (ctx.Settings?.CageCode ?? company.CageCode) is { Length: > 0 } cage ? $"CAGE {cage}" : null,
        }.OfType<string>());
        return new Sf1443Inputs(to, from, r.IsSmallBusiness, ctx.Contract.ContractNumber, r.ContractPrice, r.ProgressPaymentRate, r.LiquidationRate,
            ctx.Contract.PeriodStart, r.Number, r.RequestDate, r.CostsThrough, r.CostBasis.ToContract(), r.PaidCosts, r.UnpaidCosts, r.IncurredToDate,
            r.EstimateToComplete, r.SubcontractorPaid, r.SubcontractorLiquidated, r.SubcontractorApproved, r.PreviousRequested, r.CostsOfDelivered,
            r.PriceOfDelivered, r.UnliquidatedAdvances, r.LiquidatedToDate);
    }

    private static async Task<Dictionary<Guid, Invoice>> InvoicesAsync(IWorkbenchDbContext db, IEnumerable<ProgressPaymentRequest> requests, CancellationToken ct)
    {
        var ids = requests.Where(r => r.InvoiceId is not null).Select(r => r.InvoiceId!.Value).ToList();
        return await db.Set<Invoice>().AsNoTracking().Where(i => ids.Contains(i.Id)).ToDictionaryAsync(i => i.Id, ct);
    }

    private ProgressPaymentRequestDto Dto(ProgressPaymentRequest r, BillingContext ctx, ContractGovernmentBilling? settings, IReadOnlyDictionary<Guid, Invoice> invoices)
    {
        var invoice = r.InvoiceId is { } id ? invoices.GetValueOrDefault(id) : null;
        var form = Sf1443Calculator.Compute(Inputs(r, ctx, settings));

        // The stored amount is what was billed; the form recomputes from the same figures, so they agree.
        return new ProgressPaymentRequestDto(r.Id, r.ContractId, r.Number, r.RequestDate, r.CostsThrough, r.Amount, r.InvoiceId, invoice?.Number,
            invoice?.Status.ToContract(), r.Notes, r.CreatedBy, r.CreatedAtUtc, form);
    }
}
