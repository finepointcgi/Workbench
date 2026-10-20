using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Accounting;
using Workbench.Application.Approvals;
using Workbench.Application.Financials;
using Workbench.Application.Identity;
using Workbench.Application.Materials;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Quality;
using Workbench.Domain.Accounting;
using Workbench.Domain.Approvals;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;
using Workbench.Domain.Quality;

namespace Workbench.Application.Quality;

/// <summary>
/// Nonconformance reports and the material review board. A proposed disposition goes through the "NCR disposition"
/// workflow; the last approval carries it out in the same unit of work: use as is (back to available), rework (a stock
/// rework work order kitted with the material), return to vendor (out of stock against the receiving clearing account,
/// with a debit memo for accounts payable) or scrap (out of stock, charged to an expense account or a project).
/// </summary>
public sealed class NonconformanceService(
    IWorkbenchDbContextFactory dbFactory,
    MaterialsLedger ledger,
    AuditRecorder audit,
    INotificationPublisher notifications,
    IOptions<QualityOptions> options,
    TimeProvider clock,
    ApprovalEngine? approvals = null) : INonconformanceService
{
    public const string NotificationKind = "quality.ncr";

    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<IReadOnlyList<NcrSummaryDto>> ListAsync(NcrQuery query, CancellationToken cancellationToken = default)
    {
        query ??= new NcrQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var status = query.Status?.ToDomain();
        var rows = await db.Set<Nonconformance>().AsNoTracking()
            .Where(n => (status == null || n.Status == status) && (query.VendorId == null || n.VendorId == query.VendorId) && (query.PartId == null || n.PartId == query.PartId)
                && (!query.OpenOnly || n.Status == NonconformanceStatus.Open || n.Status == NonconformanceStatus.PendingApproval))
            .OrderByDescending(n => n.Number).Take(Math.Clamp(query.Take, 1, 1000)).ToListAsync(cancellationToken);
        return await SummariesAsync(db, rows, cancellationToken);
    }

    public async Task<NcrDto> GetAsync(Guid ncrId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, ncrId, cancellationToken);
    }

    public async Task<NcrDto> CreateAsync(CreateNcrRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var quantity = MaterialRules.Quantity(request.Quantity, "Quantity");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var codes = await DefectCodeDefaults.ValidateAsync(db, request.DefectCodes, cancellationToken);
        var description = PurchasingSupport.Required(request.Description, "Description", 2000);
        var found = await db.Set<InventoryBalance>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BalanceId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Stock", request.BalanceId);
        if (found.Status != InventoryStockStatus.Available)
        {
            throw new ArgumentException(found.Status == InventoryStockStatus.InspectionHold
                ? "That stock is waiting for inspection; reject it there."
                : "That stock is already nonconforming.");
        }

        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(found.PartId, cancellationToken);
        var balance = db.Set<InventoryBalance>().Local.First(b => b.Id == request.BalanceId);
        var ncr = new Nonconformance
        {
            Number = await QualityRules.NextNumberAsync<Nonconformance>(db, n => n.Number, "NCR", cancellationToken),
            PartId = part.Id, Lot = balance.Lot, Serial = balance.Serial, Quantity = quantity, Source = NonconformanceSource.Stock, DefectCodes = codes,
            Description = description, CreatedBy = audit.Actor, CreatedAtUtc = UtcNow, UpdatedAtUtc = UtcNow,
        };
        ncr.Items.Add(new NonconformanceItem
        {
            NonconformanceId = ncr.Id, WarehouseId = balance.WarehouseId, BinId = balance.BinId, Lot = balance.Lot, Serial = balance.Serial,
            Ownership = balance.Ownership, OwnerProjectId = balance.OwnerProjectId, Quantity = quantity,
        });
        engine.ChangeStatus(part, balance, quantity, InventoryStockStatus.Nonconforming, Today, $"{ncr.Number}: nonconforming");
        db.Set<Nonconformance>().Add(ncr);
        await engine.FlushAsync(JournalSource.Inventory, ncr.Id, Today, $"{ncr.Number}", cancellationToken);
        audit.Record(db, QualityRules.NcrEntity, ncr.Id, "Opened", after: new { ncr.Number, ncr.Quantity, ncr.DefectCodes });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, ncr.Id, cancellationToken);
    }

    public async Task<NcrDto> ProposeDispositionAsync(Guid ncrId, ProposeDispositionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ncr = await LoadAsync(db, ncrId, cancellationToken);
        if (ncr.Status != NonconformanceStatus.Open)
        {
            throw new InvalidOperationException(ncr.Status == NonconformanceStatus.PendingApproval
                ? $"{ncr.Number} already has a disposition waiting for approval; withdraw it to change it."
                : $"{ncr.Number} is {ncr.Status.ToString().ToLowerInvariant()}.");
        }

        var part = await db.Set<Part>().AsNoTracking().FirstAsync(p => p.Id == ncr.PartId, cancellationToken);
        var disposition = request.Disposition.ToDomain();
        ncr.ScrapAccountId = null;
        ncr.ScrapWbsElementId = null;
        switch (disposition)
        {
            case NonconformanceDisposition.ReturnToVendor:
                if (ncr.VendorId is null || ncr.Items.Count == 0)
                {
                    throw new ArgumentException("Only material received from a vendor (and still in stock) can be returned to the vendor.");
                }

                break;
            case NonconformanceDisposition.Rework when ncr.Items.Count > 0 && ncr.Items.Any(i => i.Ownership == InventoryOwnership.GovernmentFurnished):
                throw new ArgumentException("Government-furnished material isn't reworked on a stock work order; return it to the government or use it as is.");
            case NonconformanceDisposition.Scrap:
                if (request.ScrapWbsElementId is { } wid)
                {
                    ncr.ScrapWbsElementId = (await StockEngine.ChargeableWbsAsync(db, wid, direct: true, cancellationToken)).Id;
                }
                else if (request.ScrapAccountId is { } aid)
                {
                    var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == aid, cancellationToken) ?? throw WorkbenchNotFoundException.For("Account", aid);
                    if (!account.IsActive || account.Type != AccountType.Expense || await db.Set<Account>().AnyAsync(a => a.ParentId == account.Id, cancellationToken))
                    {
                        throw new ArgumentException($"{account.Number} {account.Name} isn't an active expense detail account.");
                    }

                    ncr.ScrapAccountId = account.Id;
                }

                break;
        }

        ncr.Disposition = disposition;
        ncr.DispositionNote = PurchasingSupport.Optional(request.Note, "Note", 2000);
        ncr.ValueAtCost = ncr.Items.Any(i => i.Ownership != InventoryOwnership.GovernmentFurnished) || ncr.Items.Count == 0
            ? Money.Round(ncr.Quantity * part.CurrentUnitCost) : 0;
        ncr.SubmittedBy = audit.Actor;
        ncr.SubmittedAtUtc = UtcNow;
        ncr.DecidedBy = null;
        ncr.DecidedAtUtc = null;
        ncr.DecisionComment = null;
        ncr.Status = NonconformanceStatus.PendingApproval;
        ncr.UpdatedAtUtc = UtcNow;
        ncr.DispositionDate = request.Date;
        ncr.ReturnAuthorization = disposition == NonconformanceDisposition.ReturnToVendor ? QualityRules.Clip(request.ReturnAuthorization, 64) : null;
        audit.Record(db, QualityRules.NcrEntity, ncr.Id, "DispositionProposed", null, new { Disposition = disposition.ToString(), ncr.ValueAtCost, ncr.DispositionNote });
        var route = await _approvals.StartAsync(db, await SubjectAsync(db, ncr, part, cancellationToken), cancellationToken);
        IReadOnlyList<string> notify = route.NotifyEmails;
        if (route.Instance.Status == ApprovalInstanceStatus.Approved)
        {
            await ExecuteAsync(db, ncr, ncr.DispositionDate ?? Today, audit.Actor, "No approval needed", cancellationToken);
            notify = [];
        }

        await db.SaveChangesAsync(cancellationToken);
        if (notify.Count > 0)
        {
            await notifications.PublishAsync(notify, NotificationKind, $"Disposition to approve: {ncr.Number} {part.Display}",
                $"{Label(disposition)} · {ncr.Quantity:0.####} {part.UnitOfMeasure}", link: Link(ncr.Id));
        }

        return await DetailAsync(db, ncr.Id, cancellationToken);
    }

    public Task<NcrDto> ApproveAsync(Guid ncrId, DecideNcrRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(ncrId, approve: true, request?.Comment, cancellationToken);

    public Task<NcrDto> RejectAsync(Guid ncrId, DecideNcrRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(ncrId, approve: false, request?.Comment, cancellationToken);

    private async Task<NcrDto> DecideAsync(Guid ncrId, bool approve, string? comment, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var ncr = await LoadAsync(db, ncrId, ct);
        if (ncr.Status != NonconformanceStatus.PendingApproval)
        {
            throw new InvalidOperationException($"{ncr.Number} has no disposition waiting for approval.");
        }

        if (ncr.SubmittedBy == audit.Actor)
        {
            throw new WorkbenchForbiddenException("You can't approve a disposition you proposed.");
        }

        var part = await db.Set<Part>().AsNoTracking().FirstAsync(p => p.Id == ncr.PartId, ct);
        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.NcrDisposition, ncr.Id, audit.Actor, c => SubjectAsync(db, ncr, part, c), ct);
        if (!check.CanDecide)
        {
            throw new WorkbenchForbiddenException("This disposition isn't waiting on you.");
        }

        var note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (!approve && note is null)
        {
            throw new ArgumentException("Give a reason for sending the disposition back.");
        }

        var outcome = await _approvals.DecideAsync(db, check, approve ? ApprovalDecisionKind.Approve : ApprovalDecisionKind.Reject, audit.Actor, note, ct);
        if (approve && !outcome.IsComplete)
        {
            audit.Record(db, QualityRules.NcrEntity, ncr.Id, "StepApproved", null, new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName)) }, note);
            await db.SaveChangesAsync(ct);
            await notifications.PublishAsync(outcome.NotifyEmails, NotificationKind, $"Disposition to approve: {ncr.Number} {part.Display}",
                Label(ncr.Disposition!.Value), link: Link(ncr.Id));
            return await DetailAsync(db, ncr.Id, ct);
        }

        if (approve)
        {
            await ExecuteAsync(db, ncr, ncr.DispositionDate ?? Today, audit.Actor, note, ct);
        }
        else
        {
            ncr.Status = NonconformanceStatus.Open;
            ncr.DecidedBy = audit.Actor;
            ncr.DecidedAtUtc = UtcNow;
            ncr.DecisionComment = QualityRules.Clip(note, 1000);
            ncr.UpdatedAtUtc = UtcNow;
            audit.Record(db, QualityRules.NcrEntity, ncr.Id, "DispositionRejected", null, new { Disposition = ncr.Disposition?.ToString() }, note);
        }

        await db.SaveChangesAsync(ct);
        if (ncr.SubmittedBy is { } submitter && submitter != audit.Actor)
        {
            await notifications.PublishAsync(submitter, NotificationKind,
                $"{ncr.Number} disposition {(approve ? "approved" : "sent back")}: {Label(ncr.Disposition!.Value)}", approve ? null : note, link: Link(ncr.Id));
        }

        return await DetailAsync(db, ncr.Id, ct);
    }

    public async Task<NcrDto> WithdrawDispositionAsync(Guid ncrId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ncr = await LoadAsync(db, ncrId, cancellationToken);
        if (ncr.Status != NonconformanceStatus.PendingApproval)
        {
            throw new InvalidOperationException($"{ncr.Number} has no disposition waiting for approval.");
        }

        ncr.Status = NonconformanceStatus.Open;
        ncr.UpdatedAtUtc = UtcNow;
        await _approvals.CancelAsync(db, ApprovalDocumentTypes.NcrDisposition, ncr.Id, audit.Actor, "Withdrawn", cancellationToken);
        audit.Record(db, QualityRules.NcrEntity, ncr.Id, "DispositionWithdrawn", null, new { Disposition = ncr.Disposition?.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, ncr.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<NcrSummaryDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = (await _approvals.ActionableAsync(db, ApprovalDocumentTypes.NcrDisposition, audit.Actor, cancellationToken)).Select(x => x.Instance.DocumentId).ToHashSet();
        var rows = await db.Set<Nonconformance>().AsNoTracking().Where(n => n.Status == NonconformanceStatus.PendingApproval && ids.Contains(n.Id)).ToListAsync(cancellationToken);
        return await SummariesAsync(db, rows, cancellationToken);
    }

    /// <summary>Carries out the approved disposition (stock, GL, rework order, debit memo) and closes the NCR.</summary>
    private async Task ExecuteAsync(IWorkbenchDbContext db, Nonconformance ncr, DateOnly date, string actor, string? comment, CancellationToken ct)
    {
        if (await ledger.AccountsAsync(db, ct) is not null)
        {
            await GeneralLedger.OpenPeriodForAsync(db, date, ct);
        }

        var engine = await StockEngine.CreateAsync(db, ledger, ct);
        var part = await engine.LoadPartAsync(ncr.PartId, ct);
        var disposition = ncr.Disposition ?? throw new InvalidOperationException("No disposition.");
        var memo = $"{ncr.Number} {Label(disposition).ToLowerInvariant()}";
        var moved = new List<InventoryTransaction>();
        switch (disposition)
        {
            case NonconformanceDisposition.UseAsIs:
                foreach (var item in ncr.Items)
                {
                    moved.Add(engine.ChangeStatus(part, Nonconforming(engine, part, item), item.Quantity, InventoryStockStatus.Available, date, $"{memo}").In);
                }

                break;

            case NonconformanceDisposition.Scrap when ncr.Items.Count == 0:
                await ScrapOnWorkOrderAsync(db, engine, ncr, part, date, ct);
                break;

            case NonconformanceDisposition.Scrap:
                Guid? debit = engine.Accounts is { } a ? ncr.ScrapWbsElementId is not null ? a.DirectMaterial : ncr.ScrapAccountId ?? a.Adjustment : null;
                Guid? projectId = null;
                if (ncr.ScrapWbsElementId is { } wid)
                {
                    projectId = (await StockEngine.ChargeableWbsAsync(db, wid, direct: true, ct)).ProjectId;
                }

                foreach (var item in ncr.Items)
                {
                    var balance = Nonconforming(engine, part, item);
                    if (balance.Ownership != InventoryOwnership.General && projectId is { } p && balance.OwnerProjectId != p)
                    {
                        throw new ArgumentException("Contract and government-furnished stock can only be charged to the project it belongs to.");
                    }

                    var t = engine.Issue(part, balance, item.Quantity, InventoryTransactionType.InventoryScrap, date, debit, memo, projectId, ncr.ScrapWbsElementId);
                    t.Reason = $"{ncr.Number}: scrapped";
                    moved.Add(t);
                }

                break;

            case NonconformanceDisposition.ReturnToVendor:
                await ReturnToVendorAsync(db, engine, ncr, part, date, memo, moved, ct);
                break;

            case NonconformanceDisposition.Rework when ncr.Items.Count == 0:
                break; // Material still on its work order: reworked there (the note says how).

            case NonconformanceDisposition.Rework:
                await ReworkAsync(db, engine, ncr, part, date, memo, moved, ct);
                break;
        }

        var entry = await engine.FlushAsync(JournalSource.Inventory, ncr.Id, date, $"{ncr.Number} {part.Display}: {Label(disposition).ToLowerInvariant()}", ct);
        ncr.JournalEntryId = entry?.Id;
        ncr.Status = NonconformanceStatus.Closed;
        ncr.DecidedBy = actor;
        ncr.DecidedAtUtc = UtcNow;
        ncr.DecisionComment = QualityRules.Clip(comment, 1000);
        ncr.ClosedAtUtc = UtcNow;
        ncr.UpdatedAtUtc = UtcNow;
        audit.Record(db, QualityRules.NcrEntity, ncr.Id, "Dispositioned", new { Status = nameof(NonconformanceStatus.PendingApproval) },
            new { Disposition = disposition.ToString(), Movements = moved.Select(t => t.Number).ToList(), Journal = entry?.Number }, comment);
        Integrations.IntegrationOutbox.Raise(db, Domain.Integrations.IntegrationEventType.NcrDispositioned, new Domain.Integrations.NcrEventData(ncr.Id, ncr.Number,
            ncr.PartId, ncr.Lot, ncr.Serial, ncr.Quantity, disposition.ToString(), ncr.VendorId, ncr.WorkOrderId), actor, UtcNow);
    }

    private static InventoryBalance Nonconforming(StockEngine engine, Part part, NonconformanceItem item)
    {
        var rows = engine.Balances(part.Id).Where(b => b.Status == InventoryStockStatus.Nonconforming && b.Lot == item.Lot && b.Serial == item.Serial
            && b.Ownership == item.Ownership && b.OwnerProjectId == item.OwnerProjectId).ToList();
        var balance = rows.FirstOrDefault(b => b.BinId == item.BinId && b.Quantity >= item.Quantity) ?? rows.FirstOrDefault(b => b.Quantity >= item.Quantity);
        return balance ?? throw new InvalidOperationException($"The nonconforming {part.Display} isn't all in stock any more{(item.Lot.Length > 0 ? $" (lot {item.Lot})" : "")}{(item.Serial.Length > 0 ? $" (serial {item.Serial})" : "")}.");
    }

    /// <summary>
    /// Out of stock against the receiving clearing account at the PO price (the difference from book cost to purchase
    /// price variance, as the receipt did), and a debit memo for what the vendor owes back.
    /// </summary>
    private async Task ReturnToVendorAsync(IWorkbenchDbContext db, StockEngine engine, Nonconformance ncr, Part part, DateOnly date, string memo, List<InventoryTransaction> moved, CancellationToken ct)
    {
        var line = ncr.PurchaseOrderLineId is { } lid ? await db.Set<PurchaseOrderLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == lid, ct) : null;
        decimal book = 0;
        foreach (var item in ncr.Items)
        {
            var t = engine.Issue(part, Nonconforming(engine, part, item), item.Quantity, InventoryTransactionType.VendorReturn, date, engine.Accounts?.ReceivedNotInvoiced, memo);
            t.PurchaseOrderLineId = ncr.PurchaseOrderLineId;
            t.Reason = $"{ncr.Number}: returned to vendor";
            book += -t.Amount;
            moved.Add(t);
        }

        var unitPrice = line?.UnitPrice ?? (ncr.Quantity == 0 ? 0 : Math.Round(book / ncr.Quantity, 4));
        var amount = line is null ? book : Money.Round(ncr.Quantity * unitPrice);
        if (amount != book && engine.Accounts is { } a && ncr.Items.Any(i => i.Ownership != InventoryOwnership.GovernmentFurnished))
        {
            engine.AddLedger(new LedgerAmount(a.ReceivedNotInvoiced, amount - book, $"{part.Display} {memo} at the PO price"),
                new LedgerAmount(a.PriceVariance, book - amount, $"{part.Display} {memo}: price variance reversed"));
        }

        var debitMemo = new VendorDebitMemo
        {
            Number = await QualityRules.NextNumberAsync<VendorDebitMemo>(db, m => m.Number, "DM", ct),
            VendorId = ncr.VendorId!.Value, NonconformanceId = ncr.Id, PurchaseOrderLineId = ncr.PurchaseOrderLineId, PartId = part.Id, Quantity = ncr.Quantity,
            UnitPrice = unitPrice, Amount = amount, ReturnAuthorization = ncr.ReturnAuthorization,
            Date = date, CreatedBy = engine.Actor, CreatedAtUtc = UtcNow,
        };
        db.Set<VendorDebitMemo>().Add(debitMemo);
        ncr.DebitMemoId = debitMemo.Id;
        audit.Record(db, QualityRules.DebitMemoEntity, debitMemo.Id, "Created", after: new { debitMemo.Number, debitMemo.Amount, Ncr = ncr.Number });
    }

    /// <summary>A released stock work order to rework the material (the part is its only component, already kitted from the nonconforming stock).</summary>
    private async Task ReworkAsync(IWorkbenchDbContext db, StockEngine engine, Nonconformance ncr, Part part, DateOnly date, string memo, List<InventoryTransaction> moved, CancellationToken ct)
    {
        var first = ncr.Items[0];
        var wo = new WorkOrder
        {
            Number = await QualityRules.NextNumberAsync<WorkOrder>(db, w => w.Number, "WO", ct),
            PartId = part.Id, Quantity = ncr.Quantity, StartDate = date, DueDate = date.AddDays(Math.Max(1, part.LeadTimeDays)), Status = WorkOrderStatus.InProgress,
            WarehouseId = first.WarehouseId, BinId = first.BinId, Notes = QualityRules.Clip($"Rework for {ncr.Number}: {ncr.DispositionNote ?? ncr.Description}", 2000),
            CreatedBy = engine.Actor, CreatedAtUtc = UtcNow, ReleasedAtUtc = UtcNow,
        };
        var component = new WorkOrderComponent { WorkOrderId = wo.Id, Sequence = 10, PartId = part.Id, QuantityPer = 1, QuantityRequired = ncr.Quantity, QuantityIssued = ncr.Quantity };
        wo.Components.Add(component);
        db.Set<WorkOrder>().Add(wo);
        foreach (var item in ncr.Items)
        {
            var t = engine.Issue(part, Nonconforming(engine, part, item), item.Quantity, InventoryTransactionType.IssueToWorkOrder, date, engine.Accounts?.Wip, $"{memo} on {wo.Number}");
            t.WorkOrderId = wo.Id;
            wo.MaterialCost += -t.Amount;
            moved.Add(t);
        }

        ncr.ReworkWorkOrderId = wo.Id;
        audit.Record(db, MaterialRules.WorkOrderEntity, wo.Id, "Created", after: new { wo.Number, wo.Quantity, Rework = ncr.Number });
    }

    /// <summary>Rejected in process: the units still on the work order are scrapped there (WIP relieved).</summary>
    private static async Task ScrapOnWorkOrderAsync(IWorkbenchDbContext db, StockEngine engine, Nonconformance ncr, Part part, DateOnly date, CancellationToken ct)
    {
        var wo = ncr.WorkOrderId is { } id ? await db.Set<WorkOrder>().FirstOrDefaultAsync(w => w.Id == id, ct) : null;
        if (wo is null || !wo.IsOpen)
        {
            throw new InvalidOperationException("The work order isn't open any more; scrap the units from stock instead.");
        }

        if (ncr.Quantity > wo.QuantityOpen)
        {
            throw new ArgumentException($"Only {wo.QuantityOpen:0.####} is still open on {wo.Number}.");
        }

        ManufacturingService.ScrapCore(engine, wo, part, ncr.Quantity, date, $"{ncr.Number}: scrapped");
    }

    // ---- Debit memos ----

    public async Task<IReadOnlyList<DebitMemoDto>> ListDebitMemosAsync(Guid? vendorId = null, bool openOnly = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<VendorDebitMemo>().AsNoTracking()
            .Where(m => (vendorId == null || m.VendorId == vendorId) && (!openOnly || m.Status == DebitMemoStatus.Open)).ToListAsync(cancellationToken);
        return await DebitMemosAsync(db, rows.OrderByDescending(m => m.Number, StringComparer.Ordinal).ToList(), cancellationToken);
    }

    public async Task<DebitMemoDto> ApplyDebitMemoAsync(Guid debitMemoId, ApplyDebitMemoRequest request, CancellationToken cancellationToken = default)
    {
        var reference = PurchasingSupport.Required(request?.Reference, "Where it was applied (bill or payment)", 100);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var memo = await db.Set<VendorDebitMemo>().FirstOrDefaultAsync(m => m.Id == debitMemoId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Debit memo", debitMemoId);
        if (memo.Status != DebitMemoStatus.Open)
        {
            throw new InvalidOperationException($"{memo.Number} is already {memo.Status.ToString().ToLowerInvariant()}.");
        }

        memo.Status = DebitMemoStatus.Applied;
        memo.AppliedBy = audit.Actor;
        memo.AppliedAtUtc = UtcNow;
        memo.AppliedReference = reference;
        audit.Record(db, QualityRules.DebitMemoEntity, memo.Id, "Applied", new { Status = nameof(DebitMemoStatus.Open) }, new { memo.AppliedReference });
        await db.SaveChangesAsync(cancellationToken);
        return (await DebitMemosAsync(db, [memo], cancellationToken))[0];
    }

    // ---- Approval subject and mapping ----

    internal async Task<ApprovalSubject> SubjectAsync(IWorkbenchDbContext db, Nonconformance ncr, Part part, CancellationToken ct)
    {
        var submitter = ncr.SubmittedBy is { } s ? await EmployeeLinks.FindForUserAsync(db, s, ct) : null;
        var disposition = ncr.Disposition is { } d ? Label(d) : "Disposition";
        return new ApprovalSubject(ApprovalDocumentTypes.NcrDisposition, ncr.Id, $"{ncr.Number} {part.Display}: {disposition.ToLowerInvariant()}", ncr.SubmittedBy ?? "",
            Link(ncr.Id), ncr.ValueAtCost, $"{ncr.Quantity:0.####} {part.UnitOfMeasure} · {ncr.DefectCodes}", submitter?.Id,
            [new ApprovalCharge(null, ncr.ValueAtCost, submitter?.OrgUnitId, "Materials")], options.Value.NcrFinanceThreshold);
    }

    public static string Link(Guid ncrId) => $"materials/quality/ncrs/{ncrId}";

    public static string Label(NonconformanceDisposition d) => d switch
    {
        NonconformanceDisposition.UseAsIs => "Use as is",
        NonconformanceDisposition.ReturnToVendor => "Return to vendor",
        _ => d.ToString(),
    };

    private static async Task<Nonconformance> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<Nonconformance>().Include(n => n.Items).FirstOrDefaultAsync(n => n.Id == id, ct) ?? throw WorkbenchNotFoundException.For("NCR", id);

    internal static async Task<List<NcrSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<Nonconformance> rows, CancellationToken ct)
    {
        var partIds = rows.Select(n => n.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var vendorIds = rows.Where(n => n.VendorId != null).Select(n => n.VendorId!.Value).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        return rows.Select(n =>
        {
            var p = parts[n.PartId];
            return new NcrSummaryDto(n.Id, n.Number, n.Status.ToContract(), n.Source.ToContract(), p.Id, p.Display, p.Description, p.UnitOfMeasure, n.Lot, n.Serial, n.Quantity,
                QualityRules.Codes(n.DefectCodes), n.VendorId is { } v ? vendors.GetValueOrDefault(v) : null, n.Disposition?.ToContract(), n.ValueAtCost, n.CreatedAtUtc, n.ClosedAtUtc);
        }).ToList();
    }

    internal static async Task<List<DebitMemoDto>> DebitMemosAsync(IWorkbenchDbContext db, IReadOnlyList<VendorDebitMemo> rows, CancellationToken ct)
    {
        var vendorIds = rows.Select(m => m.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var ncrIds = rows.Select(m => m.NonconformanceId).Distinct().ToList();
        var ncrs = await db.Set<Nonconformance>().AsNoTracking().Where(n => ncrIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Number, ct);
        var partIds = rows.Select(m => m.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Display, ct);
        var lineIds = rows.Where(m => m.PurchaseOrderLineId != null).Select(m => m.PurchaseOrderLineId!.Value).Distinct().ToList();
        var orders = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .Join(db.Set<PurchaseOrder>().AsNoTracking(), l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l.Id, o.Number }).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        return rows.Select(m => new DebitMemoDto(m.Id, m.Number, m.VendorId, vendors.GetValueOrDefault(m.VendorId) ?? "", m.NonconformanceId, ncrs.GetValueOrDefault(m.NonconformanceId) ?? "",
            parts.GetValueOrDefault(m.PartId) ?? "", m.Quantity, m.UnitPrice, m.Amount, m.ReturnAuthorization, m.Status.ToContract(), m.Date,
            m.PurchaseOrderLineId is { } l ? orders.GetValueOrDefault(l) : null, m.AppliedBy, m.AppliedReference)).ToList();
    }

    private async Task<NcrDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var n = await db.Set<Nonconformance>().AsNoTracking().Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw WorkbenchNotFoundException.For("NCR", id);
        var summary = (await SummariesAsync(db, [n], ct))[0];
        var inspection = n.InspectionId is { } iid ? await db.Set<Inspection>().AsNoTracking().Where(i => i.Id == iid).Select(i => i.Number).FirstOrDefaultAsync(ct) : null;
        var po = n.PurchaseOrderLineId is { } lid
            ? await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => l.Id == lid).Join(db.Set<PurchaseOrder>(), l => l.PurchaseOrderId, o => o.Id, (l, o) => o.Number).FirstOrDefaultAsync(ct)
            : null;
        var woIds = new[] { n.WorkOrderId, n.ReworkWorkOrderId }.OfType<Guid>().ToList();
        var wos = await db.Set<WorkOrder>().AsNoTracking().Where(w => woIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Number, ct);
        var account = n.ScrapAccountId is { } aid ? await db.Set<Account>().AsNoTracking().Where(a => a.Id == aid).Select(a => a.Number + " " + a.Name).FirstOrDefaultAsync(ct) : null;
        var charge = n.ScrapWbsElementId is { } wid
            ? await db.Set<WbsElement>().AsNoTracking().Where(w => w.Id == wid).Join(db.Set<Project>(), w => w.ProjectId, p => p.Id, (w, p) => p.Code + " " + w.Code).FirstOrDefaultAsync(ct)
            : null;
        var memo = n.DebitMemoId is { } mid ? (await DebitMemosAsync(db, await db.Set<VendorDebitMemo>().AsNoTracking().Where(m => m.Id == mid).ToListAsync(ct), ct)).FirstOrDefault() : null;
        var journal = n.JournalEntryId is { } jid ? await db.Set<JournalEntry>().AsNoTracking().Where(j => j.Id == jid).Select(j => j.Number).FirstOrDefaultAsync(ct) : null;
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var binIds = n.Items.Select(i => i.BinId).Distinct().ToList();
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var cars = await db.Set<CorrectiveAction>().AsNoTracking().Where(c => c.NonconformanceId == n.Id).ToListAsync(ct);
        var canDecide = n.Status == NonconformanceStatus.PendingApproval && n.SubmittedBy != audit.Actor
            && await _approvals.CanDecideAsync(db, ApprovalDocumentTypes.NcrDisposition, n.Id, audit.Actor,
                async c => await SubjectAsync(db, n, await db.Set<Part>().AsNoTracking().FirstAsync(p => p.Id == n.PartId, c), c), ct);
        return new NcrDto(summary, n.Description, n.InspectionId, inspection, n.VendorId, po, n.WorkOrderId, n.WorkOrderId is { } w1 ? wos.GetValueOrDefault(w1) : null,
            n.DispositionNote, n.ScrapAccountId, account, n.ScrapWbsElementId, charge, n.SubmittedBy, n.SubmittedAtUtc, n.DecidedBy, n.DecidedAtUtc, n.DecisionComment,
            n.ReworkWorkOrderId, n.ReworkWorkOrderId is { } w2 ? wos.GetValueOrDefault(w2) : null, memo, journal,
            n.Items.Select(i => new NcrItemDto(i.Id, QualityRules.Location(warehouses.GetValueOrDefault(i.WarehouseId), bins.GetValueOrDefault(i.BinId)), i.Lot, i.Serial, i.Quantity)).ToList(),
            await QualityFileService.ListAsync(db, QualityFileOwner.Nonconformance, n.Id, ct),
            await CorrectiveActionService.SummariesAsync(db, cars, DateOnly.FromDateTime(UtcNow), ct),
            canDecide, n.Status == NonconformanceStatus.PendingApproval);
    }
}

/// <summary>Lets the approvals inbox decide NCR dispositions through the NCR service (so its checks and postings run).</summary>
public sealed class NcrDispositionApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.NcrDisposition;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken cancellationToken) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<INonconformanceService>(services)
            .ApproveAsync(documentId, new DecideNcrRequest(comment), cancellationToken);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken cancellationToken) =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<INonconformanceService>(services)
            .RejectAsync(documentId, new DecideNcrRequest(reason), cancellationToken);
}
