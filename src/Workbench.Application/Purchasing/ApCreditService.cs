using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Accounting;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// Everything around credit and debit memos: "credit this bill", applying and unapplying credit, vendor refunds, retainage
/// releases, debit memos from quality returns, and a document's full trail. The memos themselves are entered, approved and
/// posted by <see cref="IApBillService"/> (the same workflow and segregation of duties as bills), which this calls through
/// its decorators.
/// </summary>
public sealed class ApCreditService(
    IWorkbenchDbContextFactory dbFactory,
    IApBillService bills,
    AuditRecorder audit,
    GeneralLedger ledger,
    AccessControl access,
    IOptions<PurchasingOptions> options,
    TimeProvider clock,
    IOptions<Materials.MaterialsOptions>? materialsOptions = null,
    Customization.DocumentNumbers? documentNumbers = null) : IApCreditService
{
    private Customization.DocumentNumbers DocNumbers => documentNumbers ?? new(dbFactory, clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    private string Me => access.Email;

    // ---- Credit this bill ----

    public async Task<BillDto> CreditBillAsync(Guid billId, CreditBillRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Kind is not (BillKind.CreditMemo or BillKind.DebitMemo))
        {
            throw new ArgumentException("Choose a credit memo or a debit memo.");
        }

        SaveBillRequest save;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var bill = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Bill", billId);
            if (bill.Kind != ApDocumentKind.Bill || bill.Status != ApBillStatus.Posted)
            {
                throw new ArgumentException($"{ApCredits.Title(bill)} isn't a posted bill, so it can't be credited.");
            }

            if (bill.IsForeignCurrency)
            {
                throw new ArgumentException($"{bill.Number} is in {bill.CurrencyCode}; credits are in dollars, so enter the credit memo by hand at its dollar amount.");
            }

            var lines = bill.Lines.Where(l => l.Kind != ApBillLineKind.Retainage).OrderBy(l => l.LineNumber).ToList();
            var credited = await ApCredits.CreditedByLineAsync(db, lines.Select(l => l.Id).ToList(), null, cancellationToken);
            decimal Left(ApBillLine l) => l.Amount - credited.GetValueOrDefault(l.Id).Amount;
            var picks = request.Lines is { Count: > 0 } chosen
                ? chosen.Select(c => (Line: lines.FirstOrDefault(l => l.Id == c.BillLineId) ?? throw new ArgumentException("That line isn't on this bill."), c.Amount)).ToList()
                : lines.Select(l => (Line: l, Amount: Left(l))).Where(x => x.Amount > 0).ToList();
            if (picks.Count == 0 || picks.All(p => p.Amount == 0))
            {
                throw new ArgumentException($"Everything on {bill.Number} is already credited.");
            }

            var number = request.Kind == BillKind.DebitMemo ? request.CreditNumber : string.IsNullOrWhiteSpace(request.CreditNumber) ? $"CR-{bill.InvoiceNumber}" : request.CreditNumber;
            save = new SaveBillRequest(bill.VendorId, number ?? "", request.Date ?? Today,
                picks.Where(p => p.Amount != 0).Select(p => new SaveBillLineRequest(p.Line.Kind.ToContract(), p.Line.Description, p.Amount,
                    p.Line.WbsElementId, p.Line.OdcType?.ToContract(), p.Line.SubcontractId, null, p.Line.AccountId, p.Line.OrgUnitId, CreditsLineId: p.Line.Id)).ToList(),
                Description: PurchasingSupport.Optional(request.Reason, "The reason", 500) ?? $"Credit of {bill.Number} ({bill.InvoiceNumber})",
                Kind: request.Kind, CreditsBillId: bill.Id);
        }

        var memo = await bills.CreateAsync(save, cancellationToken);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            audit.Record(db, PurchasingSupport.BillEntity, billId, "CreditStarted", after: new { Memo = memo.Bill.Number, memo.Bill.Amount });
            await db.SaveChangesAsync(cancellationToken);
        }

        return memo;
    }

    // ---- Applying credit ----

    public async Task<IReadOnlyList<OpenCreditDto>> ListOpenCreditsAsync(Guid? vendorId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var open = await ApCredits.OpenCreditsAsync(db, vendorId is { } v ? [v] : null, cancellationToken);
        var scheduled = await ApCredits.ScheduledByCreditAsync(db, open.Select(c => c.Id).ToList(), cancellationToken);
        var summaries = await ApBillService.SummariesAsync(db, open, cancellationToken);
        return summaries.Select(s => new OpenCreditDto(s, scheduled.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<BillDto> ApplyAsync(Guid creditId, ApplyCreditRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Items is not { Count: > 0 })
        {
            throw new ArgumentException("Choose at least one bill to apply the credit to.");
        }

        if (request.Items.GroupBy(i => i.BillId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A bill can only be listed once.");
        }

        var date = request.AppliedOn ?? Today;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var credit = await db.Set<ApBill>().FirstOrDefaultAsync(b => b.Id == creditId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Credit", creditId);
            var ids = request.Items.Select(i => i.BillId).ToList();
            var targets = await db.Set<ApBill>().Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
            var reserved = (await ApCredits.ScheduledByCreditAsync(db, [credit.Id], cancellationToken)).GetValueOrDefault(credit.Id);
            var inRuns = await InRunsAsync(db, ids, cancellationToken);
            foreach (var item in request.Items)
            {
                var bill = targets.GetValueOrDefault(item.BillId) ?? throw WorkbenchNotFoundException.For("Bill", item.BillId);
                ApCredits.EnsureApplicable(credit, bill);
                if (date < credit.InvoiceDate || date < bill.InvoiceDate)
                {
                    throw new ArgumentException($"Credit can't be applied before both {credit.Number} and {bill.Number} are dated.");
                }

                var amount = PurchasingSupport.Money(item.Amount, $"The amount for {bill.Number}");
                if (amount > credit.Balance - reserved)
                {
                    throw new ArgumentException($"{ApCredits.Title(credit)} has {credit.Balance - reserved:N2} of credit left" + (reserved > 0 ? $" ({reserved:N2} is netted in an open payment run)." : "."));
                }

                var open = bill.Balance - inRuns.GetValueOrDefault(bill.Id);
                if (amount > open)
                {
                    throw new ArgumentException($"Bill {bill.Number} has {open:N2} open" + (inRuns.GetValueOrDefault(bill.Id) > 0 ? " (the rest is in an open payment run)." : "."));
                }

                ApCredits.Apply(db, credit, bill, amount, date, Me, UtcNow);
                audit.Record(db, PurchasingSupport.BillEntity, credit.Id, "CreditApplied", after: new { Bill = bill.Number, Amount = amount, AppliedOn = date });
                audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "CreditApplied", after: new { Credit = credit.Number, Amount = amount, AppliedOn = date });
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return await bills.GetAsync(creditId, cancellationToken);
    }

    public async Task<BillDto> UnapplyAsync(Guid applicationId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        Guid creditId;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var application = await db.Set<ApCreditApplication>().FirstOrDefaultAsync(a => a.Id == applicationId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Credit application", applicationId);
            switch (application.Status)
            {
                case ApCreditApplicationStatus.Reversed:
                    throw new InvalidOperationException("That credit was already taken back.");
                case ApCreditApplicationStatus.Scheduled:
                    throw new InvalidOperationException("That credit is netted in an open payment run; cancel the run to release it.");
            }

            var credit = await db.Set<ApBill>().FirstAsync(b => b.Id == application.CreditId, cancellationToken);
            var bill = await db.Set<ApBill>().FirstAsync(b => b.Id == application.BillId, cancellationToken);
            application.Status = ApCreditApplicationStatus.Reversed;
            application.ReversedBy = Me;
            application.ReversedAtUtc = UtcNow;
            application.ReversalReason = reason;
            credit.AppliedAmount -= application.Amount;
            bill.AppliedAmount -= application.Amount;
            audit.Record(db, PurchasingSupport.BillEntity, credit.Id, "CreditUnapplied", before: new { Bill = bill.Number, application.Amount }, reason: reason);
            audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "CreditUnapplied", before: new { Credit = credit.Number, application.Amount }, reason: reason);
            await db.SaveChangesAsync(cancellationToken);
            creditId = credit.Id;
        }

        return await bills.GetAsync(creditId, cancellationToken);
    }

    /// <summary>Payments in open runs per bill (credits the runs net are already in <see cref="ApBill"/>'s reservations).</summary>
    private static async Task<Dictionary<Guid, decimal>> InRunsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> billIds, CancellationToken ct)
    {
        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => billIds.Contains(p.BillId) && p.Status == ApPaymentStatus.Scheduled)
            .Join(db.Set<ApPaymentRun>().Where(r => r.Status == PaymentRunStatus.Draft || r.Status == PaymentRunStatus.Approved), p => p.RunId, r => r.Id, (p, r) => new { p.BillId, p.Amount })
            .ToListAsync(ct);
        var credits = await ApCredits.ScheduledByBillAsync(db, billIds, ct);
        return payments.Select(p => (p.BillId, p.Amount)).Concat(credits.Select(c => (BillId: c.Key, Amount: c.Value)))
            .GroupBy(x => x.BillId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
    }

    // ---- Refunds ----

    public async Task<BillDto> RecordRefundAsync(Guid creditId, RecordRefundRequest request, CancellationToken cancellationToken = default)
    {
        var reference = PurchasingSupport.Required(request.Reference, "The vendor's check number or deposit reference", 64);
        var amount = PurchasingSupport.Money(request.Amount, "The refund");
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var credit = await db.Set<ApBill>().FirstOrDefaultAsync(b => b.Id == creditId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Credit", creditId);
            if (!credit.IsCredit || credit.Status != ApBillStatus.Posted)
            {
                throw new ArgumentException($"{ApCredits.Title(credit)} isn't a posted credit, so there's nothing to refund.");
            }

            if (request.ReceivedOn < credit.InvoiceDate)
            {
                throw new ArgumentException($"The refund can't be before {credit.Number} ({credit.InvoiceDate:yyyy-MM-dd}).");
            }

            var reserved = (await ApCredits.ScheduledByCreditAsync(db, [credit.Id], cancellationToken)).GetValueOrDefault(credit.Id);
            if (amount > credit.Balance - reserved)
            {
                throw new ArgumentException($"{ApCredits.Title(credit)} has {credit.Balance - reserved:N2} of credit left to refund.");
            }

            var vendor = await PurchasingSupport.FindVendorAsync(db, credit.VendorId, cancellationToken);
            var payables = await PurchasingSupport.PayablesAccountAsync(db, cancellationToken);
            var cash = await PurchasingSupport.AccountByNumberAsync(db, options.Value.CashAccountNumber, "cash", cancellationToken);
            var refund = new ApRefund
            {
                CreditId = credit.Id, VendorId = credit.VendorId, Amount = amount, ReceivedOn = request.ReceivedOn, Reference = reference, RecordedBy = Me, RecordedAtUtc = UtcNow,
            };
            var entry = new JournalEntry
            {
                CompanyId = credit.CompanyId,
                Date = request.ReceivedOn,
                Source = JournalSource.AP,
                SourceId = refund.Id,
                Description = $"Vendor refund: {vendor.Name} {reference} for {credit.Number}",
                CreatedBy = ledger.Actor,
                CreatedAtUtc = ledger.UtcNow,
            };
            entry.Lines.Add(new JournalLine { JournalEntryId = entry.Id, LineNumber = 1, AccountId = cash.Id, Debit = amount, Memo = $"Refund {reference}" });
            entry.Lines.Add(new JournalLine { JournalEntryId = entry.Id, LineNumber = 2, AccountId = payables.Id, Credit = amount, Memo = $"{vendor.Code} {credit.InvoiceNumber}" });
            db.Set<JournalEntry>().Add(entry);
            await ledger.PostAsync(db, entry, cancellationToken);
            refund.JournalEntryId = entry.Id;
            db.Set<ApRefund>().Add(refund);
            credit.PaidAmount += amount;
            audit.Record(db, PurchasingSupport.BillEntity, credit.Id, "Refunded", after: new { refund.Amount, refund.Reference, refund.ReceivedOn, Entry = entry.Number });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await bills.GetAsync(creditId, cancellationToken);
    }

    public async Task<BillDto> VoidRefundAsync(Guid refundId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        Guid creditId;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var refund = await db.Set<ApRefund>().FirstOrDefaultAsync(r => r.Id == refundId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Refund", refundId);
            if (refund.Status == ApRefundStatus.Void)
            {
                throw new InvalidOperationException("The refund is already void.");
            }

            var entry = await db.Set<JournalEntry>().Include(e => e.Lines).FirstAsync(e => e.Id == refund.JournalEntryId, cancellationToken);
            var date = (await GeneralLedger.PeriodForAsync(db, refund.ReceivedOn, cancellationToken)) is { Status: PeriodStatus.Open } ? refund.ReceivedOn : Today;
            await ledger.ReverseAsync(db, entry, date, $"Void refund {refund.Reference}: {reason}", cancellationToken);
            var credit = await db.Set<ApBill>().FirstAsync(b => b.Id == refund.CreditId, cancellationToken);
            credit.PaidAmount -= refund.Amount;
            refund.Status = ApRefundStatus.Void;
            refund.VoidReason = reason;
            refund.VoidedBy = Me;
            refund.VoidedAtUtc = UtcNow;
            audit.Record(db, PurchasingSupport.BillEntity, credit.Id, "RefundVoided", before: new { refund.Amount, refund.Reference }, reason: reason);
            await db.SaveChangesAsync(cancellationToken);
            creditId = credit.Id;
        }

        return await bills.GetAsync(creditId, cancellationToken);
    }

    // ---- Retainage ----

    public async Task<IReadOnlyList<RetainageBillDto>> ListRetainageAsync(Guid subcontractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Subcontract>().AnyAsync(s => s.Id == subcontractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Subcontract", subcontractId);
        }

        return await ApCredits.RetainageAsync(db, subcontractId, null, cancellationToken);
    }

    public async Task<BillDto> ReleaseRetainageAsync(ReleaseRetainageRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Items is not { Count: > 0 })
        {
            throw new ArgumentException("Choose the bills whose retainage to release.");
        }

        if (request.Items.GroupBy(i => i.BillId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A bill can only be listed once.");
        }

        Guid releaseId;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var sub = await db.Set<Subcontract>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == request.SubcontractId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Subcontract", request.SubcontractId);
            if (sub.Status == SubcontractStatus.Draft)
            {
                throw new ArgumentException($"Subcontract {sub.Number} is a draft; nothing is held on it.");
            }

            var vendor = await PurchasingSupport.FindVendorAsync(db, sub.VendorId, cancellationToken);
            PurchasingSupport.EnsureVendorCanTransact(vendor, "releasing retainage");

            // The subcontract's funding and ceiling still apply: released retainage is paid against billed to date.
            var billed = (await SubcontractTotals.LoadAsync(db, [sub.Id], cancellationToken)).GetValueOrDefault(sub.Id)?.Billed ?? 0;
            if (billed > sub.CeilingValue)
            {
                throw new ArgumentException($"Subcontract {sub.Number} is billed {billed:N2}, over its {sub.CeilingValue:N2} ceiling. Add a modification before releasing retainage.");
            }

            string? overrideReason = null;
            if (billed > sub.FundedValue)
            {
                overrideReason = PurchasingSupport.Optional(request.FundingOverrideReason, "The override reason", 1000)
                    ?? throw new ArgumentException($"Subcontract {sub.Number} is billed {billed:N2}, over its {sub.FundedValue:N2} funded value. Finance can release with a reason.");
            }

            var ids = request.Items.Select(i => i.BillId).ToList();
            var open = (await ApCredits.RetainageAsync(db, sub.Id, ids, cancellationToken)).ToDictionary(r => r.BillId);
            var sources = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines).Where(b => ids.Contains(b.Id)).ToDictionaryAsync(b => b.Id, cancellationToken);
            if (sources.Values.Select(b => b.CompanyId).Distinct().Count() > 1)
            {
                throw new ArgumentException("Those bills belong to different companies; release each company's retainage separately.");
            }

            var release = new ApBill
            {
                Kind = ApDocumentKind.RetainageRelease,
                Number = await DocNumbers.NextAsync(Customization.DocumentNumberTypes.ApBill, cancellationToken: cancellationToken),
                InvoiceNumber = "",
                VendorId = vendor.Id,
                InvoiceDate = request.Date,
                DueDate = request.Date.AddDays(vendor.PaymentTermsDays),
                EnteredBy = Me,
                CreatedAtUtc = UtcNow,
                FundingOverrideBy = overrideReason is null ? null : Me,
                FundingOverrideReason = overrideReason,
            };
            release.InvoiceNumber = PurchasingSupport.Optional(request.InvoiceNumber, "The vendor's invoice number", 64) ?? release.Number;
            release.Description = PurchasingSupport.Optional(request.Description, "Description", 500) ?? $"Retainage release on subcontract {sub.Number}";
            foreach (var item in request.Items)
            {
                var row = open.GetValueOrDefault(item.BillId) ?? throw new ArgumentException($"{sources.GetValueOrDefault(item.BillId)?.Number ?? "That bill"} isn't a posted bill holding retainage on {sub.Number}.");
                var amount = PurchasingSupport.Money(item.Amount, $"The release on {row.Number}");
                if (amount > row.Open)
                {
                    throw new ArgumentException($"{row.Number} has {row.Open:N2} of retainage left to release (held {row.Held:N2}"
                        + (row.Credited > 0 ? $", credited {row.Credited:N2}" : "") + (row.Released > 0 ? $", released {row.Released:N2}" : "")
                        + (row.Pending > 0 ? $", {row.Pending:N2} in releases waiting for approval" : "") + ").");
                }

                var source = sources[item.BillId];
                release.CompanyId = source.CompanyId;
                release.Lines.Add(new ApBillLine
                {
                    BillId = release.Id,
                    LineNumber = release.Lines.Count + 1,
                    Kind = ApBillLineKind.Retainage,
                    Description = $"Retainage on {source.Number} (invoice {source.InvoiceNumber})",
                    Amount = amount,
                    ProjectId = sub.ProjectId ?? source.Lines.FirstOrDefault(l => l.SubcontractId == sub.Id)?.ProjectId,
                    SubcontractId = sub.Id,
                    ReleasesBillId = source.Id,
                });
            }

            release.Amount = release.Lines.Sum(l => l.Amount);
            db.Set<ApBill>().Add(release);
            audit.Record(db, PurchasingSupport.BillEntity, release.Id, "Created", after: new { release.Number, Kind = release.Kind.ToString(), release.Amount, Subcontract = sub.Number, release.FundingOverrideReason });
            await db.SaveChangesAsync(cancellationToken);
            releaseId = release.Id;
        }

        // Approved like a bill: the subcontract's project manager (or Finance), never the person releasing it.
        return await bills.SubmitAsync(releaseId, new SubmitBillRequest(ConfirmPossibleDuplicate: true), cancellationToken);
    }

    // ---- Quality returns ----

    public async Task<BillDto> CreateFromQualityReturnAsync(Guid qualityDebitMemoId, CancellationToken cancellationToken = default)
    {
        SaveBillRequest save;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var quality = await db.Set<Domain.Quality.VendorDebitMemo>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == qualityDebitMemoId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Debit memo", qualityDebitMemoId);
            if (quality.Status != Domain.Quality.DebitMemoStatus.Open)
            {
                throw new InvalidOperationException($"{quality.Number} is already {quality.Status.ToString().ToLowerInvariant()}.");
            }

            if (await db.Set<ApBill>().AsNoTracking().Where(b => b.QualityDebitMemoId == quality.Id && b.Status != ApBillStatus.Void).Select(b => b.Number).FirstOrDefaultAsync(cancellationToken) is { } existing)
            {
                throw new InvalidOperationException($"{quality.Number} is already on AP debit memo {existing}.");
            }

            // The return debited the receiving clearing account (the PO line's account), so the memo credits it back.
            var poLine = quality.PurchaseOrderLineId is { } lid ? await db.Set<PurchaseOrderLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == lid, cancellationToken) : null;
            var accountNumber = materialsOptions?.Value.ReceivedNotInvoicedAccountNumber ?? "1405";
            var account = poLine?.AccountId
                ?? await db.Set<Account>().AsNoTracking().Where(a => a.Number == accountNumber && a.IsActive).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(cancellationToken)
                ?? throw new ArgumentException($"No active receiving clearing account {accountNumber}; set up the materials accounts first.");
            var org = poLine?.OrgUnitId ?? await db.Set<OrgUnit>().AsNoTracking().OrderBy(o => o.Code).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(cancellationToken)
                ?? throw new ArgumentException("Add an organization first.");
            var part = await db.Set<Domain.Materials.Part>().AsNoTracking().Where(p => p.Id == quality.PartId).Select(p => p.Number).FirstOrDefaultAsync(cancellationToken);
            var ncr = await db.Set<Domain.Quality.Nonconformance>().AsNoTracking().Where(n => n.Id == quality.NonconformanceId).Select(n => n.Number).FirstOrDefaultAsync(cancellationToken);
            var description = $"Returned {quality.Quantity:0.####} × {part} ({ncr}{(quality.ReturnAuthorization is { } rma ? $", RMA {rma}" : "")})";
            save = new SaveBillRequest(quality.VendorId, quality.ReturnAuthorization is { } ra ? $"RMA {ra}" : quality.Number, quality.Date < Today ? Today : quality.Date,
                [new SaveBillLineRequest(BillLineKind.Account, description, quality.Amount, AccountId: account, OrgUnitId: org)],
                Description: $"Return to vendor {quality.Number}", Kind: BillKind.DebitMemo);
        }

        var memo = await bills.CreateAsync(save, cancellationToken);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var bill = await db.Set<ApBill>().FirstAsync(b => b.Id == memo.Bill.Id, cancellationToken);
            bill.QualityDebitMemoId = qualityDebitMemoId;
            var quality = await db.Set<Domain.Quality.VendorDebitMemo>().FirstAsync(m => m.Id == qualityDebitMemoId, cancellationToken);
            quality.Status = Domain.Quality.DebitMemoStatus.Applied;
            quality.AppliedBy = Me;
            quality.AppliedAtUtc = UtcNow;
            quality.AppliedReference = $"AP {bill.Number}";
            audit.Record(db, "VendorDebitMemo", quality.Id, "Applied", new { Status = "Open" }, new { quality.AppliedReference });
            await db.SaveChangesAsync(cancellationToken);
        }

        return await bills.GetAsync(memo.Bill.Id, cancellationToken);
    }

    // ---- Trail ----

    public async Task<BillTrailDto> GetTrailAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bill = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Bill", billId);

        var related = new List<RelatedDocumentDto>();
        RelatedDocumentDto Doc(ApBill b, string relation) => new(b.Id, b.Number, b.Kind.ToContract(), b.InvoiceNumber, b.InvoiceDate, b.Amount, b.Status.ToContract(), relation);
        if (bill.CreditsBillId is { } original && await db.Set<ApBill>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == original, cancellationToken) is { } credited)
        {
            related.Add(Doc(credited, "Credits this bill"));
        }

        foreach (var memo in await db.Set<ApBill>().AsNoTracking().Where(b => b.CreditsBillId == bill.Id).OrderBy(b => b.Number).ToListAsync(cancellationToken))
        {
            related.Add(Doc(memo, memo.Kind == ApDocumentKind.DebitMemo ? "Debit memo against this bill" : "Credit memo against this bill"));
        }

        var releasedIds = bill.Lines.Where(l => l.ReleasesBillId != null).Select(l => l.ReleasesBillId!.Value).Distinct().ToList();
        foreach (var source in await db.Set<ApBill>().AsNoTracking().Where(b => releasedIds.Contains(b.Id)).OrderBy(b => b.Number).ToListAsync(cancellationToken))
        {
            related.Add(Doc(source, "Retainage released from"));
        }

        var releases = await db.Set<ApBillLine>().AsNoTracking().Where(l => l.ReleasesBillId == bill.Id).Select(l => l.BillId).Distinct().ToListAsync(cancellationToken);
        foreach (var release in await db.Set<ApBill>().AsNoTracking().Where(b => releases.Contains(b.Id)).OrderBy(b => b.Number).ToListAsync(cancellationToken))
        {
            related.Add(Doc(release, "Retainage release"));
        }

        var applications = await ApplicationDtosAsync(db, db.Set<ApCreditApplication>().AsNoTracking().Where(a => a.BillId == bill.Id || a.CreditId == bill.Id), cancellationToken);
        var refunds = (await db.Set<ApRefund>().AsNoTracking().Where(r => r.CreditId == bill.Id).ToListAsync(cancellationToken)).OrderBy(r => r.ReceivedOn)
            .Select(r => new RefundDto(r.Id, r.CreditId, r.Amount, r.ReceivedOn, r.Reference, r.Status.ToContract(), r.JournalEntryId, r.RecordedBy, r.VoidReason)).ToList();

        // Journal entries: the AP entry, line ODCs and their void offsets, use tax, payments, refunds, and every reversal of them.
        var sources = new Dictionary<Guid, string> { [bill.Id] = ApCredits.Label(bill.Kind) switch { "bill" => "Bill posting", var k => $"{char.ToUpperInvariant(k[0])}{k[1..]} posting" } };
        foreach (var line in bill.Lines)
        {
            if (line.OdcEntryId is { } odc)
            {
                sources[odc] = $"Project cost, line {line.LineNumber}";
            }

            if (line.ReversalOdcEntryId is { } offset)
            {
                sources[offset] = $"Void: project cost back off, line {line.LineNumber}";
            }
        }

        if (await db.Set<Domain.SalesTax.ApBillTax>().AsNoTracking().Where(t => t.BillId == bill.Id).Select(t => (Guid?)t.Id).FirstOrDefaultAsync(cancellationToken) is { } taxId)
        {
            sources[taxId] = "Use tax";
        }

        foreach (var p in await db.Set<ApPayment>().AsNoTracking().Where(p => p.BillId == bill.Id).Select(p => new { p.Id, p.Reference }).ToListAsync(cancellationToken))
        {
            sources[p.Id] = $"Payment {p.Reference}";
        }

        foreach (var r in refunds)
        {
            sources[r.Id] = $"Refund {r.Reference}";
        }

        var ids = sources.Keys.ToList();
        var entries = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => e.SourceId != null && ids.Contains(e.SourceId!.Value) && e.Source != JournalSource.Reversal && e.Status != JournalStatus.Draft).ToListAsync(cancellationToken);
        var entryIds = entries.Select(e => e.Id).ToList();
        var reversals = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => e.ReversalOfId != null && entryIds.Contains(e.ReversalOfId!.Value)).ToListAsync(cancellationToken);
        var journal = entries.Select(e => new BillJournalDto(e.Id, e.Number, e.Date, e.Description, e.Status.ToString(), e.TotalDebit, sources[e.SourceId!.Value]))
            .Concat(reversals.Select(e => new BillJournalDto(e.Id, e.Number, e.Date, e.Description, e.Status.ToString(), e.TotalDebit,
                $"Reversal: {sources[entries.First(o => o.Id == e.ReversalOfId).SourceId!.Value]}")))
            .OrderBy(e => e.Date).ThenBy(e => e.Number, StringComparer.Ordinal).ToList();

        var history = (await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == PurchasingSupport.BillEntity && a.EntityId == bill.Id).ToListAsync(cancellationToken))
            .OrderBy(a => a.OccurredAtUtc).Select(a => new BillHistoryDto(a.OccurredAtUtc, a.Actor, a.Action, a.Reason)).ToList();
        return new BillTrailDto(bill.Id, related, applications, refunds, journal, history);
    }

    internal static async Task<IReadOnlyList<CreditApplicationDto>> ApplicationDtosAsync(IWorkbenchDbContext db, IQueryable<ApCreditApplication> query, CancellationToken ct)
    {
        var rows = await query.ToListAsync(ct);
        if (rows.Count == 0)
        {
            return [];
        }

        var docIds = rows.SelectMany(a => new[] { a.CreditId, a.BillId }).Distinct().ToList();
        var docs = await db.Set<ApBill>().AsNoTracking().Where(b => docIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);
        var vendorIds = rows.Select(a => a.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Code, ct);
        var runIds = rows.Where(a => a.RunId != null).Select(a => a.RunId!.Value).Distinct().ToList();
        var runs = await db.Set<ApPaymentRun>().AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, ct);
        return rows.OrderBy(a => a.AppliedOn).ThenBy(a => a.AppliedAtUtc).Select(a =>
        {
            var credit = docs[a.CreditId];
            var bill = docs[a.BillId];
            return new CreditApplicationDto(a.Id, a.CreditId, credit.Number, credit.Kind.ToContract(), credit.InvoiceNumber, a.BillId, bill.Number, bill.InvoiceNumber,
                a.VendorId, vendors.GetValueOrDefault(a.VendorId) ?? "", a.Amount, a.AppliedOn, a.Status.ToContract(), a.RunId, a.RunId is { } r ? runs.GetValueOrDefault(r) : null,
                a.AppliedBy, a.ReversalReason);
        }).ToList();
    }
}
