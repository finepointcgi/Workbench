using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Application.Accounting;
using Workbench.Application.Approvals;
using Workbench.Application.Currencies;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.Odcs;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// AP bills: entry with duplicate checks, approval routing (project and subcontract lines to the project manager, account
/// lines to Finance, never to the person who entered the bill), and posting. Posting reuses the ODC path for cost lines
/// (an approved vendor ODC debits project cost and credits AP, so project financials and the GL agree, and a linked ODC that
/// already posted is never posted again) and writes one AP entry for account lines and retainage.
/// </summary>
public sealed class ApBillService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    GeneralLedger ledger,
    PostingEngine engine,
    AccessControl access,
    INotificationPublisher notifications,
    IOptions<PurchasingOptions> options,
    TimeProvider clock,
    ILogger<ApBillService> logger,
    ApprovalEngine? approvals = null,
    SalesTax.UseTaxAccrual? useTax = null) : IApBillService
{
    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private string Me => access.Email;

    public async Task<IReadOnlyList<BillSummaryDto>> ListAsync(BillQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<ApBill>().AsNoTracking().AsQueryable();
        if (query.Status is { } status)
        {
            var s = status.ToDomain();
            q = q.Where(b => b.Status == s);
        }

        if (query.VendorId is { } vendorId)
        {
            q = q.Where(b => b.VendorId == vendorId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(b => b.Number.Contains(term) || b.InvoiceNumber.Contains(term) || (b.Description != null && b.Description.Contains(term)));
        }

        var bills = (await q.ToListAsync(cancellationToken))
            .OrderByDescending(b => b.InvoiceDate).ThenByDescending(b => b.Number).Take(Math.Clamp(query.Take, 1, 2000)).ToList();
        return await SummariesAsync(db, bills, cancellationToken);
    }

    public async Task<BillDto> GetAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, billId, cancellationToken);
    }

    public async Task<IReadOnlyList<BillDuplicateDto>> CheckDuplicatesAsync(DuplicateCheckRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DuplicatesAsync(db, request.VendorId, request.InvoiceNumber, request.InvoiceDate, request.Amount, request.ExcludeBillId, cancellationToken);
    }

    public async Task<BillDto> CreateAsync(SaveBillRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var numbers = await db.Set<ApBill>().Select(b => b.Number).ToListAsync(cancellationToken);
        var bill = new ApBill { Number = PurchasingSupport.NextNumber(numbers, "V"), InvoiceNumber = "", EnteredBy = Me, CreatedAtUtc = UtcNow };
        await ApplyAsync(db, bill, request, cancellationToken);
        db.Set<ApBill>().Add(bill);
        audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "Created", after: Snapshot(bill));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, bill.Id, cancellationToken);
    }

    public async Task<BillDto> UpdateAsync(Guid billId, SaveBillRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bill = await LoadAsync(db, billId, cancellationToken);
        EnsureEditable(bill);
        var before = Snapshot(bill);
        db.Set<ApBillLine>().RemoveRange(bill.Lines);
        bill.Lines.Clear();
        await ApplyAsync(db, bill, request, cancellationToken);

        // Lines have client-generated keys, so add them explicitly rather than relying on change detection.
        db.Set<ApBillLine>().AddRange(bill.Lines);
        audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "Updated", before, Snapshot(bill));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, bill.Id, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bill = await LoadAsync(db, billId, cancellationToken);
        if (bill.Status != ApBillStatus.Draft)
        {
            throw new InvalidOperationException($"Bill {bill.Number} is {bill.Status}; only drafts can be deleted. Void it instead.");
        }

        db.Set<ApBill>().Remove(bill);
        audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "DraftDeleted", before: Snapshot(bill));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<BillDto> SubmitAsync(Guid billId, SubmitBillRequest request, CancellationToken cancellationToken = default)
    {
        var notify = new List<(IReadOnlyList<string> To, string Title)>();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bill = await LoadAsync(db, billId, cancellationToken);
        EnsureEditable(bill);
        if (bill.Lines.Count == 0)
        {
            throw new ArgumentException("Add at least one line before submitting.");
        }

        var vendor = await PurchasingSupport.FindVendorAsync(db, bill.VendorId, cancellationToken);
        PurchasingSupport.EnsureVendorCanTransact(vendor, "submitting bills");

        // Re-check the lines: WBS, accounts and subcontracts may have changed since the draft was saved.
        foreach (var line in bill.Lines)
        {
            await ValidateLineAsync(db, bill, vendor, line, cancellationToken);
        }

        var duplicates = await DuplicatesAsync(db, bill.VendorId, bill.InvoiceNumber, bill.InvoiceDate, bill.Amount, bill.Id, cancellationToken);
        if (duplicates.Count > 0 && !request.ConfirmPossibleDuplicate)
        {
            throw new ArgumentException($"Possible duplicate of {string.Join(", ", duplicates.Select(d => $"{d.Number} ({d.Reason.ToLowerInvariant()})"))}. Confirm to submit anyway.");
        }

        bill.DuplicateConfirmedBy = duplicates.Count > 0 ? Me : null;
        await CheckSubcontractFundingAsync(db, bill, request.FundingOverrideReason, cancellationToken);

        // Three-way match (procure-to-pay): mismatches are recorded now and block approval until they clear or Finance overrides.
        await ThreeWayMatch.EvaluateAsync(db, bill, options.Value.MatchPriceTolerancePercent, cancellationToken);

        // Routing: the AP bill workflow (Setup › Approval workflows). By default project lines go to their PM and
        // account lines (and lines no other PM can take) to Finance, in parallel.
        foreach (var line in bill.Lines)
        {
            line.Status = ApLineStatus.Pending;
            line.DecidedBy = null;
            line.DecidedAtUtc = null;
        }

        var route = await _approvals.StartAsync(db, await ApprovalSubjects.ApBillAsync(db, bill, cancellationToken), cancellationToken);
        foreach (var line in bill.Lines)
        {
            line.ApproverEmail = line.Kind == ApBillLineKind.Account || line.ProjectId is not { } pid ? null : route.ProjectApprover(pid);
        }

        bill.Status = ApBillStatus.PendingApproval;
        bill.SubmittedAtUtc = UtcNow;
        bill.RejectionReason = null;
        audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "Submitted", after: new
        {
            bill.Amount,
            Routes = bill.Lines.Select(l => l.ApproverEmail ?? PurchasingSupport.FinanceRoute).Distinct(),
            bill.FundingOverrideReason,
            bill.DuplicateConfirmedBy,
            MatchProblems = bill.Lines.Where(l => l.MatchProblem != null).Select(l => new { l.LineNumber, l.MatchProblem }),
        });
        await db.SaveChangesAsync(cancellationToken);

        var pms = bill.Lines.Where(l => l.ApproverEmail != null).Select(l => l.ApproverEmail!).Distinct().ToList();
        if (pms.Count > 0)
        {
            notify.Add((pms, $"{bill.EnteredBy} sent a {vendor.Name} bill for your approval"));
        }

        var others = route.NotifyEmails.Where(e => !pms.Contains(e, StringComparer.OrdinalIgnoreCase)).ToList();
        if (others.Count > 0)
        {
            notify.Add((others, $"{vendor.Name} bill {bill.InvoiceNumber} needs Finance approval"));
        }

        foreach (var (to, title) in notify)
        {
            await notifications.PublishAsync(to.Where(e => !PurchasingSupport.SameEmail(e, Me)), "ap.bill_approval", title,
                $"{bill.Number}: {bill.Amount:C} due {bill.DueDate:yyyy-MM-dd}", link: $"purchasing/bills/{bill.Id}");
        }

        return await DetailAsync(db, bill.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<BillApprovalItemDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bills = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines).Where(b => b.Status == ApBillStatus.PendingApproval).ToListAsync(cancellationToken);
        var linkedSubmitters = await LinkedOdcSubmittersAsync(db, bills.SelectMany(b => b.Lines), cancellationToken);
        var mine = new List<(ApBill Bill, List<ApBillLine> Lines)>();
        foreach (var bill in bills)
        {
            var lines = await ApprovableLinesAsync(db, bill, linkedSubmitters, cancellationToken);
            if (lines.Count > 0)
            {
                mine.Add((bill, lines));
            }
        }

        mine = mine.OrderBy(x => x.Bill.DueDate).ToList();
        var summaries = (await SummariesAsync(db, mine.Select(x => x.Bill).ToList(), cancellationToken)).ToDictionary(s => s.Id);
        var lookups = await LineLookups.LoadAsync(db, mine.SelectMany(x => x.Lines), cancellationToken);
        return mine.Select(x => new BillApprovalItemDto(summaries[x.Bill.Id], x.Lines.OrderBy(l => l.LineNumber).Select(lookups.ToDto).ToList())).ToList();
    }

    public async Task<BillDto> ApproveAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        bool complete;
        ApBill bill;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            bill = await LoadAsync(db, billId, cancellationToken);
            if (bill.Status != ApBillStatus.PendingApproval)
            {
                throw new InvalidOperationException($"Bill {bill.Number} is {bill.Status}, not waiting for approval.");
            }

            if (PurchasingSupport.SameEmail(bill.EnteredBy, Me))
            {
                throw new WorkbenchForbiddenException("You can't approve a bill you entered.");
            }

            var submitters = await LinkedOdcSubmittersAsync(db, bill.Lines, cancellationToken);
            var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.ApBill, bill.Id, Me,
                ct => ApprovalSubjects.ApBillAsync(db, bill, ct), cancellationToken);
            var lines = LinesOf(bill, check.Actionable.Select(a => a.Task), submitters);
            if (lines.Count == 0)
            {
                throw new WorkbenchForbiddenException(bill.Lines.Any(l => l.Status == ApLineStatus.Pending && l.OdcEntryId is { } o && PurchasingSupport.SameEmail(submitters.GetValueOrDefault(o), Me))
                    ? "You can't approve a cost you entered yourself."
                    : "None of this bill's lines are routed to you.");
            }

            // Three-way match: receipts may have arrived since submission, so re-check; mismatches block unless Finance overrode them.
            await ThreeWayMatch.EvaluateAsync(db, bill, options.Value.MatchPriceTolerancePercent, cancellationToken);
            var blocked = ThreeWayMatch.Blocking(lines);
            if (blocked.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                throw new ArgumentException("Three-way match failed: " + string.Join(" ", blocked.Select(l => $"Line {l.LineNumber}: {l.MatchProblem}"))
                    + " Receive the goods, correct the bill, or have Finance override under Purchasing › Match exceptions.");
            }

            var outcome = await _approvals.DecideAsync(db, check, ApprovalDecisionKind.Approve, Me, null, cancellationToken);
            foreach (var line in outcome.IsComplete ? bill.Lines.Where(l => l.Status == ApLineStatus.Pending).ToList() : lines)
            {
                line.Status = ApLineStatus.Approved;
                line.DecidedBy = Me;
                line.DecidedAtUtc = UtcNow;
            }

            complete = outcome.IsComplete;
            if (complete)
            {
                bill.Status = ApBillStatus.Approved;
            }

            audit.Record(db, PurchasingSupport.BillEntity, bill.Id, complete ? "Approved" : "LinesApproved", after: new { Lines = lines.Select(l => l.LineNumber) });
            await db.SaveChangesAsync(cancellationToken);
        }

        if (complete)
        {
            await PostCoreAsync(billId, cancellationToken);
        }

        await using var read = await dbFactory.CreateAsync(cancellationToken);
        var result = await DetailAsync(read, billId, cancellationToken);
        if (complete)
        {
            await notifications.PublishAsync(bill.EnteredBy, "ap.bill_approved",
                result.Bill.Status == BillState.Posted ? $"Bill {bill.Number} was approved and posted" : $"Bill {bill.Number} was approved but couldn't post",
                result.Bill.PostingProblem ?? $"{result.Bill.VendorName} {bill.InvoiceNumber}: {bill.Amount:C}", link: $"purchasing/bills/{bill.Id}");
        }

        return result;
    }

    public async Task<BillDto> RejectAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bill = await LoadAsync(db, billId, cancellationToken);
        if (bill.Status != ApBillStatus.PendingApproval)
        {
            throw new InvalidOperationException($"Bill {bill.Number} is {bill.Status}, not waiting for approval.");
        }

        if (PurchasingSupport.SameEmail(bill.EnteredBy, Me))
        {
            throw new WorkbenchForbiddenException("You can't reject a bill you entered; edit or void it instead.");
        }

        var submitters = await LinkedOdcSubmittersAsync(db, bill.Lines, cancellationToken);
        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.ApBill, bill.Id, Me, ct => ApprovalSubjects.ApBillAsync(db, bill, ct), cancellationToken);
        if (LinesOf(bill, check.Actionable.Select(a => a.Task), submitters).Count == 0)
        {
            throw new WorkbenchForbiddenException("None of this bill's lines are routed to you.");
        }

        await _approvals.DecideAsync(db, check, ApprovalDecisionKind.Reject, Me, reason, cancellationToken);

        bill.Status = ApBillStatus.Rejected;
        bill.RejectionReason = reason;
        foreach (var line in bill.Lines)
        {
            line.Status = ApLineStatus.Pending;
        }

        audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "Rejected", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        await notifications.PublishAsync(bill.EnteredBy, "ap.bill_rejected", $"{Me} rejected bill {bill.Number}", reason, link: $"purchasing/bills/{bill.Id}");
        return await DetailAsync(db, bill.Id, cancellationToken);
    }

    public async Task<BillDto> PostAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var bill = await LoadAsync(db, billId, cancellationToken);
            if (bill.Status != ApBillStatus.Approved)
            {
                throw new InvalidOperationException(bill.Status == ApBillStatus.Posted ? $"Bill {bill.Number} is already posted." : $"Bill {bill.Number} isn't approved yet.");
            }
        }

        await PostCoreAsync(billId, cancellationToken);
        await using var read = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(read, billId, cancellationToken);
    }

    public async Task<BillDto> VoidAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bill = await LoadAsync(db, billId, cancellationToken);
        if (bill.Status is not (ApBillStatus.Draft or ApBillStatus.Rejected or ApBillStatus.PendingApproval))
        {
            throw new InvalidOperationException(bill.Status == ApBillStatus.Void
                ? $"Bill {bill.Number} is already void."
                : $"Bill {bill.Number} is {bill.Status}; approved and posted bills are corrected with a credit, not voided.");
        }

        bill.Status = ApBillStatus.Void;
        bill.VoidReason = reason;
        await _approvals.CancelAsync(db, ApprovalDocumentTypes.ApBill, bill.Id, Me, reason, cancellationToken);
        audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "Voided", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, bill.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<LinkableOdcDto>> ListLinkableOdcsAsync(Guid vendorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken);
        var linked = await LinkedOdcIdsAsync(db, null, cancellationToken);
        var odcs = (await db.Set<OdcEntry>().AsNoTracking()
                .Where(o => o.ExpenseReportId == null && o.Status != OdcStatus.Rejected && (o.VendorId == vendorId || (o.VendorId == null && o.Vendor == vendor.Name)))
                .ToListAsync(cancellationToken))
            .Where(o => !linked.Contains(o.Id) && !PostingEngine.IsEmployeeExpense(o))
            .OrderBy(o => o.IncurredOn).ToList();
        var wbsIds = odcs.Select(o => o.WbsElementId).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var projectIds = odcs.Select(o => o.ProjectId).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        return odcs.Select(o => new LinkableOdcDto(o.Id, projects.GetValueOrDefault(o.ProjectId) ?? "", wbs.GetValueOrDefault(o.WbsElementId)?.Code ?? "",
            o.Type.ToContract(), o.IncurredOn, o.Amount, o.Description, o.InvoiceNumber, o.Status.ToContract())).ToList();
    }

    public async Task<IReadOnlyList<OdcBillLinkDto>> GetOdcLinksAsync(IReadOnlyCollection<Guid> odcEntryIds, CancellationToken cancellationToken = default)
    {
        if (odcEntryIds.Count == 0)
        {
            return [];
        }

        var ids = odcEntryIds.Distinct().ToList();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var odcs = await db.Set<OdcEntry>().AsNoTracking().Where(o => ids.Contains(o.Id)).Select(o => new { o.Id, o.VendorId }).ToListAsync(cancellationToken);
        var vendorIds = odcs.Where(o => o.VendorId != null).Select(o => o.VendorId!.Value).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
        var bills = (await db.Set<ApBillLine>().AsNoTracking().Where(l => l.OdcEntryId != null && ids.Contains(l.OdcEntryId!.Value))
                .Join(db.Set<ApBill>().Where(b => b.Status != ApBillStatus.Void), l => l.BillId, b => b.Id, (l, b) => new { OdcId = l.OdcEntryId!.Value, b.Id, b.Number })
                .ToListAsync(cancellationToken))
            .GroupBy(x => x.OdcId).ToDictionary(g => g.Key, g => g.First());
        return odcs.Where(o => o.VendorId != null || bills.ContainsKey(o.Id)).Select(o =>
        {
            var vendor = o.VendorId is { } v ? vendors.GetValueOrDefault(v) : null;
            var bill = bills.GetValueOrDefault(o.Id);
            return new OdcBillLinkDto(o.Id, vendor?.Id, vendor?.Code, vendor?.Name, bill?.Id, bill?.Number);
        }).ToList();
    }

    // ---- Posting ----

    /// <summary>
    /// Posts an approved bill. Idempotent: the ODCs are created once (and linked), the ODC engine skips an ODC that already
    /// has an entry, and the AP entry is only written when the bill has none. A failure leaves the bill approved with
    /// <see cref="ApBill.PostingProblem"/> set, so Finance can fix the cause and retry.
    /// </summary>
    private async Task PostCoreAsync(Guid billId, CancellationToken ct)
    {
        var problems = new List<string>();
        List<Guid> odcIds;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            var bill = await LoadAsync(db, billId, ct);
            var vendor = await PurchasingSupport.FindVendorAsync(db, bill.VendorId, ct);
            foreach (var line in bill.Lines.Where(l => l.Kind != ApBillLineKind.Account).OrderBy(l => l.LineNumber))
            {
                await EnsureOdcAsync(db, bill, vendor, line, ct);
            }

            await db.SaveChangesAsync(ct);
            odcIds = bill.Lines.Where(l => l.OdcEntryId != null).Select(l => l.OdcEntryId!.Value).ToList();
        }

        foreach (var odcId in odcIds)
        {
            try
            {
                problems.AddRange((await engine.PostOdcAsync(odcId, false, ct)).Problems);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException)
            {
                problems.Add(ex.Message);
            }
        }

        Guid? entryId = null;
        try
        {
            entryId = await PostApEntryAsync(billId, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException)
        {
            problems.Add(ex.Message);
        }

        await using (var db = await dbFactory.CreateAsync(ct))
        {
            var bill = await LoadAsync(db, billId, ct);
            bill.JournalEntryId = entryId ?? bill.JournalEntryId;
            if (problems.Count == 0)
            {
                bill.Status = ApBillStatus.Posted;
                bill.PostedAtUtc = UtcNow;
                bill.PostingProblem = null;
                audit.Record(db, PurchasingSupport.BillEntity, bill.Id, "Posted", after: new { bill.Amount, bill.RetainageAmount, bill.JournalEntryId });
            }
            else
            {
                bill.PostingProblem = string.Join(" ", problems.Distinct());
                if (bill.PostingProblem.Length > 1000)
                {
                    bill.PostingProblem = bill.PostingProblem[..1000];
                }

                logger.LogWarning("AP bill {BillId} couldn't post: {Problem}", bill.Id, bill.PostingProblem);
            }

            await db.SaveChangesAsync(ct);
        }

        if (problems.Count == 0 && useTax is not null)
        {
            await useTax.TryAccrueAsync(billId, ct); // Sales tax: use tax on taxable purchases the vendor didn't tax (never fails the posting).
        }
    }

    /// <summary>Approves the linked vendor ODC, or creates an approved one for the line, so the ODC engine posts it.</summary>
    private async Task EnsureOdcAsync(IWorkbenchDbContext db, ApBill bill, Vendor vendor, ApBillLine line, CancellationToken ct)
    {
        if (line.OdcEntryId is { } odcId)
        {
            var odc = await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.Id == odcId, ct) ?? throw WorkbenchNotFoundException.For("ODC", odcId);
            odc.VendorId ??= vendor.Id;
            odc.SubcontractId ??= line.SubcontractId;
            odc.InvoiceNumber ??= bill.InvoiceNumber;
            if (odc.Status != OdcStatus.Approved)
            {
                var before = odc.Status.ToString();
                odc.Status = OdcStatus.Approved;
                odc.SubmittedAtUtc ??= bill.SubmittedAtUtc;
                odc.ApprovedBy = line.DecidedBy;
                odc.ApprovedAtUtc = line.DecidedAtUtc ?? UtcNow;
                audit.Record(db, OdcWorkflow.OdcEntityType, odc.Id, "Approved", new { Status = before }, new { Status = "Approved", Bill = bill.Number });
            }

            return;
        }

        var created = new OdcEntry
        {
            ProjectId = line.ProjectId!.Value,
            WbsElementId = line.WbsElementId!.Value,
            Type = line.OdcType ?? (line.Kind == ApBillLineKind.Subcontract ? OdcType.Subcontract : OdcType.Other),
            IncurredOn = bill.InvoiceDate,
            Amount = line.Amount,
            Description = line.Description,
            Vendor = vendor.Name,
            VendorId = vendor.Id,
            SubcontractId = line.SubcontractId,
            InvoiceNumber = bill.InvoiceNumber,
            CurrencyCode = bill.CurrencyCode,
            TransactionAmount = bill.CurrencyCode is null ? null : line.TransactionAmount,
            ExchangeRate = bill.CurrencyCode is null ? null : bill.ExchangeRate,
            Status = OdcStatus.Approved,
            SubmittedBy = bill.EnteredBy,
            CreatedAtUtc = UtcNow,
            SubmittedAtUtc = bill.SubmittedAtUtc,
            ApprovedBy = line.DecidedBy,
            ApprovedAtUtc = line.DecidedAtUtc ?? UtcNow,
        };
        db.Set<OdcEntry>().Add(created);
        line.OdcEntryId = created.Id;
        audit.Record(db, OdcWorkflow.OdcEntityType, created.Id, "Created", after: new { created.Amount, created.Type, Bill = bill.Number, Status = "Approved" });
    }

    /// <summary>
    /// The AP entry: account lines (debit the account, credit AP) and retainage on subcontract lines (debit AP, credit AP
    /// retainage, moving the held part out of the payable). Returns the entry (existing or new), or null when there's nothing to post.
    /// </summary>
    private async Task<Guid?> PostApEntryAsync(Guid billId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var existing = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.Source == JournalSource.AP && e.SourceId == billId && e.Status == JournalStatus.Posted).Select(e => (Guid?)e.Id).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            return existing;
        }

        var bill = await LoadAsync(db, billId, ct);
        var accountLines = bill.Lines.Where(l => l.Kind == ApBillLineKind.Account).OrderBy(l => l.LineNumber).ToList();
        if (accountLines.Count == 0 && bill.RetainageAmount == 0)
        {
            return null;
        }

        var vendor = await PurchasingSupport.FindVendorAsync(db, bill.VendorId, ct);
        var payables = await PurchasingSupport.PayablesAccountAsync(db, ct);
        var entry = new JournalEntry
        {
            CompanyId = bill.CompanyId,
            Date = bill.InvoiceDate,
            Source = JournalSource.AP,
            SourceId = bill.Id,
            Description = $"AP bill {bill.Number}: {vendor.Name} invoice {bill.InvoiceNumber}",
            CreatedBy = ledger.Actor,
            CreatedAtUtc = ledger.UtcNow,
        };

        void Line(Guid account, decimal debit, decimal credit, string memo, Guid? org = null, decimal? transaction = null) => entry.Lines.Add(new JournalLine
        {
            JournalEntryId = entry.Id,
            LineNumber = entry.Lines.Count + 1,
            AccountId = account,
            OrgUnitId = org,
            Debit = debit,
            Credit = credit,
            Memo = memo.Length > 500 ? memo[..500] : memo,
            TransactionCurrency = transaction is null ? null : bill.CurrencyCode,
            TransactionAmount = transaction is null ? null : Math.Abs(transaction.Value),
            ExchangeRate = transaction is null ? null : bill.ExchangeRate,
        });
        decimal? Tx(decimal amount) => bill.IsForeignCurrency ? amount : null;

        foreach (var line in accountLines)
        {
            Line(line.AccountId!.Value, line.Amount, 0, line.Description, line.OrgUnitId);
        }

        var accountTotal = accountLines.Sum(l => l.Amount);
        if (accountTotal > 0)
        {
            Line(payables.Id, 0, accountTotal, $"{vendor.Code} {bill.InvoiceNumber}", transaction: Tx(accountLines.Sum(l => l.TransactionAmount ?? 0)));
        }

        if (bill.RetainageAmount > 0)
        {
            var retainage = await PurchasingSupport.EnsureRetainageAccountAsync(db, options.Value, UtcNow, ct);
            Line(payables.Id, bill.RetainageAmount, 0, $"Retainage withheld: {vendor.Code} {bill.InvoiceNumber}", transaction: Tx(bill.TransactionRetainage));
            Line(retainage.Id, 0, bill.RetainageAmount, $"Retainage withheld: {vendor.Code} {bill.InvoiceNumber}", transaction: Tx(bill.TransactionRetainage));
        }

        // A new retainage account has to exist before the ledger validates the lines against it.
        await db.SaveChangesAsync(ct);
        entry.FiscalPeriodId = (await GeneralLedger.PeriodForAsync(db, entry.Date, ct)).Id;
        db.Set<JournalEntry>().Add(entry);
        await ledger.PostAsync(db, entry, ct);
        await db.SaveChangesAsync(ct);
        return entry.Id;
    }

    // ---- Entry and validation ----

    private async Task ApplyAsync(IWorkbenchDbContext db, ApBill bill, SaveBillRequest r, CancellationToken ct)
    {
        var vendor = await PurchasingSupport.FindVendorAsync(db, r.VendorId, ct);
        PurchasingSupport.EnsureVendorCanTransact(vendor, "entering bills");
        var invoice = PurchasingSupport.Required(r.InvoiceNumber, "Invoice number", 64);
        var sameNumber = (await DuplicatesAsync(db, vendor.Id, invoice, r.InvoiceDate, null, bill.Id, ct)).FirstOrDefault(d => d.Reason == SameInvoiceNumber);
        if (sameNumber is not null)
        {
            throw new ArgumentException($"{vendor.Code} invoice {invoice} is already on bill {sameNumber.Number}.");
        }

        if (r.Lines is null || r.Lines.Count == 0)
        {
            throw new ArgumentException("A bill needs at least one line.");
        }

        if (r.Lines.Count > 200)
        {
            throw new ArgumentException("A bill can have at most 200 lines.");
        }

        var due = r.DueDate ?? r.InvoiceDate.AddDays(vendor.PaymentTermsDays);
        if (due < r.InvoiceDate)
        {
            throw new ArgumentException("The due date is before the invoice date.");
        }

        bill.VendorId = vendor.Id;
        bill.InvoiceNumber = invoice;
        bill.InvoiceDate = r.InvoiceDate;
        bill.DueDate = due;
        bill.Description = PurchasingSupport.Optional(r.Description, "Description", 500);

        // Multicurrency: lines are entered in the bill's currency and kept in the functional currency at the bill's rate.
        var currency = await CurrencySupport.ResolveAsync(db, r.CurrencyCode ?? vendor.CurrencyCode, r.InvoiceDate, r.ExchangeRate, ct);
        foreach (var input in r.Lines)
        {
            decimal? transactionAmount = null;
            var functionalAmount = input.Amount;
            if (currency.IsForeign)
            {
                if (input.PurchaseOrderLineId is not null || input.OdcEntryId is not null)
                {
                    throw new ArgumentException($"Line {bill.Lines.Count + 1}: purchase orders and project ODCs are in {await CurrencySupport.FunctionalAsync(db, ct)}, so a {currency.Code} bill can't bill them. Enter the bill in {await CurrencySupport.FunctionalAsync(db, ct)}.");
                }

                if (currency.RoundTransaction(input.Amount) != input.Amount)
                {
                    throw new ArgumentException($"Line {bill.Lines.Count + 1}: {currency.Code} amounts have {currency.Decimals} decimal place{(currency.Decimals == 1 ? "" : "s")}.");
                }

                transactionAmount = input.Amount;
                functionalAmount = currency.ToFunctional(input.Amount);
            }

            var line = new ApBillLine
            {
                BillId = bill.Id,
                LineNumber = bill.Lines.Count + 1,
                Kind = input.Kind.ToDomain(),
                Description = input.Description ?? "",
                Amount = functionalAmount,
                TransactionAmount = transactionAmount,
                WbsElementId = input.WbsElementId,
                OdcType = input.OdcType?.ToDomain(),
                SubcontractId = input.SubcontractId,
                OdcEntryId = input.OdcEntryId,
                AccountId = input.AccountId,
                OrgUnitId = input.OrgUnitId,
                PurchaseOrderLineId = input.PurchaseOrderLineId,
                Quantity = input.Quantity,
                UnitPrice = input.UnitPrice,
            };
            bill.Lines.Add(line);
            await ValidateLineAsync(db, bill, vendor, line, ct);
        }

        var linked = bill.Lines.Where(l => l.OdcEntryId != null).GroupBy(l => l.OdcEntryId).FirstOrDefault(g => g.Count() > 1);
        if (linked is not null)
        {
            throw new ArgumentException("The same ODC is linked to two lines.");
        }

        bill.Amount = bill.Lines.Sum(l => l.Amount);
        bill.RetainageAmount = bill.Lines.Sum(l => l.RetainageAmount);
        bill.CurrencyCode = currency.Code;
        bill.ExchangeRate = currency.Rate;
        bill.TransactionAmount = currency.IsForeign ? bill.Lines.Sum(l => l.TransactionAmount ?? 0) : 0;
        bill.TransactionRetainage = 0;
        if (currency.IsForeign && bill.RetainageAmount != 0)
        {
            var subIds = bill.Lines.Where(l => l.SubcontractId != null).Select(l => l.SubcontractId!.Value).Distinct().ToList();
            var percents = await db.Set<Subcontract>().AsNoTracking().Where(s => subIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.RetainagePercent, ct);
            bill.TransactionRetainage = bill.Lines.Where(l => l.SubcontractId is { } s && percents.ContainsKey(s))
                .Sum(l => currency.RoundTransaction((l.TransactionAmount ?? 0) * percents[l.SubcontractId!.Value] / 100m));
        }
    }

    /// <summary>Checks one line and fills in what it implies (project, WBS and amount of a linked ODC; retainage).</summary>
    private async Task ValidateLineAsync(IWorkbenchDbContext db, ApBill bill, Vendor vendor, ApBillLine line, CancellationToken ct)
    {
        var n = line.LineNumber;
        if (line.PurchaseOrderLineId is not null)
        {
            // Billed from a purchase order: the PO line decides the charging and price (procure-to-pay).
            await ThreeWayMatch.ApplyPoLineAsync(db, bill, line, ct);
        }
        else
        {
            line.Quantity = null;
            line.UnitPrice = null;
        }

        Subcontract? sub = null;
        if (line.Kind == ApBillLineKind.Subcontract)
        {
            sub = line.SubcontractId is { } sid
                ? await db.Set<Subcontract>().AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct) ?? throw WorkbenchNotFoundException.For("Subcontract", sid)
                : throw new ArgumentException($"Line {n}: choose a subcontract.");
            if (sub.VendorId != vendor.Id)
            {
                throw new ArgumentException($"Line {n}: subcontract {sub.Number} is with another vendor.");
            }

            if (sub.Status != SubcontractStatus.Active)
            {
                throw new ArgumentException($"Line {n}: subcontract {sub.Number} is {sub.Status}; only active subcontracts can be billed.");
            }

            line.OdcType = OdcType.Subcontract;
        }
        else
        {
            line.SubcontractId = null;
        }

        if (line.Kind == ApBillLineKind.Account)
        {
            line.ProjectId = null;
            line.WbsElementId = null;
            line.OdcType = null;
            line.OdcEntryId = null;
            var account = line.AccountId is { } aid
                ? await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid, ct) ?? throw WorkbenchNotFoundException.For("Account", aid)
                : throw new ArgumentException($"Line {n}: choose a GL account.");
            if (!account.IsActive || account.Type is not (AccountType.Expense or AccountType.Asset))
            {
                throw new ArgumentException($"Line {n}: {account.Number} {account.Name} must be an active expense or asset account.");
            }

            if (await db.Set<Account>().AnyAsync(a => a.ParentId == account.Id, ct))
            {
                throw new ArgumentException($"Line {n}: {account.Number} is a summary account; choose one of its detail accounts.");
            }

            if (line.OrgUnitId is not { } org || !await db.Set<OrgUnit>().AnyAsync(o => o.Id == org, ct))
            {
                throw new ArgumentException($"Line {n}: choose the organization the cost belongs to.");
            }
        }
        else
        {
            line.AccountId = null;
            line.OrgUnitId = null;
            if (line.OdcEntryId is { } odcId)
            {
                var odc = await db.Set<OdcEntry>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == odcId, ct) ?? throw WorkbenchNotFoundException.For("ODC", odcId);
                if (PostingEngine.IsEmployeeExpense(odc) || odc.Status == OdcStatus.Rejected || (odc.VendorId is { } v && v != vendor.Id))
                {
                    throw new ArgumentException($"Line {n}: \"{odc.Description}\" isn't an open vendor ODC of {vendor.Code}.");
                }

                if ((await LinkedOdcIdsAsync(db, bill.Id, ct)).Contains(odcId))
                {
                    throw new ArgumentException($"Line {n}: \"{odc.Description}\" is already on another bill.");
                }

                if (line.Amount != 0 && line.Amount != odc.Amount)
                {
                    throw new ArgumentException($"Line {n}: the linked ODC is {odc.Amount:N2}; the line must be the same amount.");
                }

                if (sub is not null && odc.SubcontractId is { } os && os != sub.Id)
                {
                    throw new ArgumentException($"Line {n}: the linked ODC is charged to another subcontract.");
                }

                line.Amount = odc.Amount;
                line.WbsElementId = odc.WbsElementId;
                line.OdcType = sub is null ? odc.Type : OdcType.Subcontract;
                if (string.IsNullOrWhiteSpace(line.Description))
                {
                    line.Description = odc.Description;
                }
            }
            else if (line.WbsElementId is null && sub?.ProjectId is { } subProject)
            {
                // A subcontract line without a WBS element goes to the first open direct charge code of the subcontract's project.
                line.WbsElementId = (await ChargeableLeavesAsync(db, subProject, ct)).FirstOrDefault()?.Id;
            }

            var wbs = line.WbsElementId is { } wid
                ? await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wid, ct) ?? throw WorkbenchNotFoundException.For("WBS element", wid)
                : throw new ArgumentException($"Line {n}: choose the project WBS element to charge.");
            var project = await db.Set<Project>().AsNoTracking().FirstAsync(p => p.Id == wbs.ProjectId, ct);
            if (line.OdcEntryId is null)
            {
                if (project.Status != ProjectStatus.Active || !wbs.IsOpen || !wbs.IsChargeable || await db.Set<WbsElement>().AnyAsync(w => w.ParentId == wbs.Id, ct))
                {
                    throw new ArgumentException($"Line {n}: {wbs.Code} isn't an open, chargeable lowest-level WBS element of an active project.");
                }
            }

            if (sub?.ProjectId is { } sp && sp != project.Id)
            {
                throw new ArgumentException($"Line {n}: subcontract {sub.Number} is on another project than {wbs.Code}.");
            }

            line.ProjectId = project.Id;
            line.OdcType ??= line.Kind == ApBillLineKind.ProjectCost ? throw new ArgumentException($"Line {n}: choose the ODC type.") : OdcType.Subcontract;
        }

        line.Description = PurchasingSupport.Required(line.Description, $"Line {n}: description", 500);
        line.Amount = PurchasingSupport.Money(line.Amount, $"Line {n}: amount");
        line.RetainageAmount = sub is null ? 0 : Financials.Money.Round(line.Amount * sub.RetainagePercent / 100m);
    }

    private static async Task<List<WbsElement>> ChargeableLeavesAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        var all = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == projectId).ToListAsync(ct);
        var parents = all.Where(w => w.ParentId != null).Select(w => w.ParentId!.Value).ToHashSet();
        return all.Where(w => w.IsOpen && w.IsChargeable && w.Kind == ChargeKind.Direct && !parents.Contains(w.Id)).OrderBy(w => w.Code, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Subcontract invoices can't push billed-to-date past the ceiling, and can't pass the funded value unless a Finance user
    /// gives a reason (recorded on the bill and in the audit trail).
    /// </summary>
    private async Task CheckSubcontractFundingAsync(IWorkbenchDbContext db, ApBill bill, string? overrideReason, CancellationToken ct)
    {
        bill.FundingOverrideBy = null;
        bill.FundingOverrideReason = null;
        var bySub = bill.Lines.Where(l => l.SubcontractId != null).GroupBy(l => l.SubcontractId!.Value).ToList();
        if (bySub.Count == 0)
        {
            return;
        }

        var ids = bySub.Select(g => g.Key).ToList();
        var subs = await db.Set<Subcontract>().AsNoTracking().Where(s => ids.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var others = await SubcontractTotals.LoadAsync(db, ids, ct, excludeBillId: bill.Id);
        var over = new List<string>();
        foreach (var group in bySub)
        {
            var sub = subs[group.Key];
            var total = (others.GetValueOrDefault(sub.Id)?.Billed ?? 0) + group.Sum(l => l.Amount);
            if (total > sub.CeilingValue)
            {
                throw new ArgumentException($"Subcontract {sub.Number}: billed to date would be {total:N2}, over the {sub.CeilingValue:N2} ceiling. Add a modification first.");
            }

            if (total > sub.FundedValue)
            {
                over.Add($"{sub.Number} by {total - sub.FundedValue:N2} (funded {sub.FundedValue:N2})");
            }
        }

        if (over.Count == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(overrideReason))
        {
            throw new ArgumentException($"This invoice exceeds the funded value of {string.Join("; ", over)}. Finance can override with a reason.");
        }

        if (!(await access.GetOrgRolesAsync(ct)).IsFinance())
        {
            throw new WorkbenchForbiddenException("Only Finance can submit a subcontract invoice over its funded value.");
        }

        bill.FundingOverrideBy = Me;
        bill.FundingOverrideReason = PurchasingSupport.Required(overrideReason, "The override reason", 1000);
    }

    private const string SameInvoiceNumber = "Same invoice number";

    private static async Task<IReadOnlyList<BillDuplicateDto>> DuplicatesAsync(
        IWorkbenchDbContext db, Guid vendorId, string? invoiceNumber, DateOnly invoiceDate, decimal? amount, Guid? excludeId, CancellationToken ct)
    {
        var number = invoiceNumber?.Trim().ToUpperInvariant() ?? "";
        var candidates = await db.Set<ApBill>().AsNoTracking()
            .Where(b => b.VendorId == vendorId && b.Status != ApBillStatus.Void && b.Id != excludeId)
            .ToListAsync(ct);
        var result = new List<BillDuplicateDto>();
        foreach (var b in candidates.OrderBy(b => b.Number))
        {
            var reason = number.Length > 0 && b.InvoiceNumber.Trim().ToUpperInvariant() == number ? SameInvoiceNumber
                : amount is { } a && a > 0 && b.Amount == a && b.InvoiceDate == invoiceDate ? "Same amount and invoice date"
                : null;
            if (reason is not null)
            {
                result.Add(new BillDuplicateDto(b.Id, b.Number, b.InvoiceNumber, b.InvoiceDate, b.Amount, b.Status.ToContract(), reason));
            }
        }

        return result;
    }

    /// <summary>ODCs linked from lines of live bills other than <paramref name="exceptBillId"/>.</summary>
    private static async Task<HashSet<Guid>> LinkedOdcIdsAsync(IWorkbenchDbContext db, Guid? exceptBillId, CancellationToken ct) =>
        (await db.Set<ApBillLine>().AsNoTracking().Where(l => l.OdcEntryId != null && l.BillId != exceptBillId)
            .Join(db.Set<ApBill>().Where(b => b.Status != ApBillStatus.Void), l => l.BillId, b => b.Id, (l, b) => l.OdcEntryId!.Value)
            .ToListAsync(ct)).ToHashSet();

    private static async Task<Dictionary<Guid, string>> LinkedOdcSubmittersAsync(IWorkbenchDbContext db, IEnumerable<ApBillLine> lines, CancellationToken ct)
    {
        var ids = lines.Where(l => l.OdcEntryId != null).Select(l => l.OdcEntryId!.Value).Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Set<OdcEntry>().AsNoTracking().Where(o => ids.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.SubmittedBy, ct);
    }

    /// <summary>
    /// The pending lines the current user may decide: those covered by their workflow tasks (a project task covers that
    /// project's PM-routed lines; a whole-bill task the Finance-routed ones), never a cost they entered themselves.
    /// </summary>
    private async Task<List<ApBillLine>> ApprovableLinesAsync(IWorkbenchDbContext db, ApBill bill, IReadOnlyDictionary<Guid, string> linkedSubmitters, CancellationToken ct)
    {
        if (bill.Status != ApBillStatus.PendingApproval || PurchasingSupport.SameEmail(bill.EnteredBy, Me))
        {
            return [];
        }

        var tasks = await _approvals.ActionableForAsync(db, ApprovalDocumentTypes.ApBill, bill.Id, Me, c => ApprovalSubjects.ApBillAsync(db, bill, c), ct);
        return LinesOf(bill, tasks, linkedSubmitters);
    }

    private List<ApBillLine> LinesOf(ApBill bill, IEnumerable<Domain.Approvals.ApprovalTask> tasks, IReadOnlyDictionary<Guid, string> linkedSubmitters)
    {
        var list = tasks.ToList();
        var projects = list.Where(t => t.ScopeKey?.StartsWith("project:", StringComparison.Ordinal) == true).Select(t => t.ScopeKey!).ToHashSet();
        var whole = list.Any(t => t.ScopeKey is null);
        return bill.Lines.Where(l => l.Status == ApLineStatus.Pending
                && !(l.OdcEntryId is { } o && PurchasingSupport.SameEmail(linkedSubmitters.GetValueOrDefault(o), Me))
                && (l.ApproverEmail is null ? whole : l.ProjectId is { } p && projects.Contains(ApprovalEngine.ProjectScope(p))))
            .ToList();
    }

    private static void EnsureEditable(ApBill bill)
    {
        if (bill.Status is not (ApBillStatus.Draft or ApBillStatus.Rejected))
        {
            throw new InvalidOperationException($"Bill {bill.Number} is {bill.Status} and can't be changed.");
        }
    }

    private static async Task<ApBill> LoadAsync(IWorkbenchDbContext db, Guid billId, CancellationToken ct) =>
        await db.Set<ApBill>().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, ct) ?? throw WorkbenchNotFoundException.For("Bill", billId);

    private static object Snapshot(ApBill b) => new
    {
        b.Number,
        b.VendorId,
        b.InvoiceNumber,
        b.InvoiceDate,
        b.DueDate,
        b.Amount,
        b.RetainageAmount,
        b.CurrencyCode,
        b.ExchangeRate,
        b.TransactionAmount,
        Lines = b.Lines.Select(l => new { l.LineNumber, Kind = l.Kind.ToString(), l.Amount, l.WbsElementId, l.SubcontractId, l.AccountId, l.OdcEntryId, l.PurchaseOrderLineId, l.Quantity }),
    };

    // ---- DTOs ----

    internal static async Task<IReadOnlyList<BillSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<ApBill> bills, CancellationToken ct)
    {
        var vendorIds = bills.Select(b => b.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        return bills.Select(b => Summary(b, vendors[b.VendorId])).ToList();
    }

    internal static BillSummaryDto Summary(ApBill b, Vendor v) => new(
        b.Id, b.Number, b.VendorId, v.Code, v.Name, b.InvoiceNumber, b.InvoiceDate, b.DueDate, b.Status.ToContract(),
        b.Amount, b.RetainageAmount, b.PaidAmount, b.EnteredBy, b.PostingProblem,
        b.CurrencyCode, b.CurrencyCode is null ? 1m : b.ExchangeRate, b.TransactionAmount, b.IsForeignCurrency ? b.TransactionBalance : 0m);

    private async Task<BillDto> DetailAsync(IWorkbenchDbContext db, Guid billId, CancellationToken ct)
    {
        var bill = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines).FirstOrDefaultAsync(b => b.Id == billId, ct) ?? throw WorkbenchNotFoundException.For("Bill", billId);
        var vendor = await PurchasingSupport.FindVendorAsync(db, bill.VendorId, ct);
        var lookups = await LineLookups.LoadAsync(db, bill.Lines, ct);
        var duplicates = bill.Status == ApBillStatus.Void ? [] : await DuplicatesAsync(db, bill.VendorId, bill.InvoiceNumber, bill.InvoiceDate, bill.Amount, bill.Id, ct);
        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => p.BillId == bill.Id)
            .Join(db.Set<ApPaymentRun>(), p => p.RunId, r => r.Id, (p, r) => new { p, r.Number }).ToListAsync(ct);
        var submitters = await LinkedOdcSubmittersAsync(db, bill.Lines, ct);
        var canDecide = access.IsAuthenticated && (await ApprovableLinesAsync(db, bill, submitters, ct)).Count > 0;
        return new BillDto(
            Summary(bill, vendor),
            bill.Description,
            bill.Lines.OrderBy(l => l.LineNumber).Select(lookups.ToDto).ToList(),
            duplicates,
            payments.OrderBy(x => x.p.PaidOn).Select(x => new BillPaymentDto(x.p.Id, x.p.RunId, x.Number, x.p.PaidOn, x.p.Amount, x.p.Method.ToContract(), x.p.Reference, x.p.Status.ToContract(),
                x.p.TransactionAmount, x.p.CurrencyCode is null ? 1m : x.p.ExchangeRate, x.p.RealizedGainLoss)).ToList(),
            bill.JournalEntryId,
            bill.FundingOverrideBy,
            bill.FundingOverrideReason,
            bill.RejectionReason,
            bill.VoidReason,
            bill.Status is ApBillStatus.Draft or ApBillStatus.Rejected,
            canDecide);
    }

    private sealed class LineLookups
    {
        private Dictionary<Guid, string> _projects = [];
        private Dictionary<Guid, string> _wbs = [];
        private Dictionary<Guid, string> _subs = [];
        private Dictionary<Guid, Account> _accounts = [];
        private Dictionary<Guid, string> _orgs = [];
        private Dictionary<Guid, string> _poNumbers = [];

        public static async Task<LineLookups> LoadAsync(IWorkbenchDbContext db, IEnumerable<ApBillLine> lines, CancellationToken ct)
        {
            var list = lines.ToList();
            List<Guid> Ids(Func<ApBillLine, Guid?> pick) => list.Select(pick).Where(i => i != null).Select(i => i!.Value).Distinct().ToList();
            var projectIds = Ids(l => l.ProjectId);
            var wbsIds = Ids(l => l.WbsElementId);
            var subIds = Ids(l => l.SubcontractId);
            var accountIds = Ids(l => l.AccountId);
            var orgIds = Ids(l => l.OrgUnitId);
            var poLineIds = Ids(l => l.PurchaseOrderLineId);
            return new LineLookups
            {
                _projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct),
                _wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct),
                _subs = await db.Set<Subcontract>().AsNoTracking().Where(s => subIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Number, ct),
                _accounts = await db.Set<Account>().AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct),
                _orgs = await db.Set<OrgUnit>().AsNoTracking().Where(o => orgIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.Code, ct),
                _poNumbers = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => poLineIds.Contains(l.Id))
                    .Join(db.Set<PurchaseOrder>(), l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l.Id, Number = o.Number + " line " + l.LineNumber })
                    .ToDictionaryAsync(x => x.Id, x => x.Number, ct),
            };
        }

        public BillLineDto ToDto(ApBillLine l)
        {
            var account = l.AccountId is { } a ? _accounts.GetValueOrDefault(a) : null;
            return new BillLineDto(
                l.Id, l.LineNumber, l.Kind.ToContract(), l.Description, l.Amount,
                l.ProjectId, l.ProjectId is { } p ? _projects.GetValueOrDefault(p) : null,
                l.WbsElementId, l.WbsElementId is { } w ? _wbs.GetValueOrDefault(w) : null,
                l.OdcType?.ToContract(), l.SubcontractId, l.SubcontractId is { } s ? _subs.GetValueOrDefault(s) : null,
                l.OdcEntryId, l.AccountId, account?.Number, account?.Name,
                l.OrgUnitId, l.OrgUnitId is { } o ? _orgs.GetValueOrDefault(o) : null,
                l.RetainageAmount, l.Status.ToContract(), l.ApproverEmail ?? PurchasingSupport.FinanceRoute, l.DecidedBy, l.DecidedAtUtc,
                l.PurchaseOrderLineId, l.PurchaseOrderLineId is { } po ? _poNumbers.GetValueOrDefault(po) : null, l.Quantity, l.UnitPrice,
                l.MatchProblem, l.MatchOverrideBy, l.MatchOverrideReason, l.TransactionAmount);
        }
    }
}
