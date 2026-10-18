using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

public sealed partial class InvoiceService(
    IWorkbenchDbContextFactory dbFactory,
    InvoiceGenerator generator,
    BillingLedger ledger,
    AuditRecorder audit,
    TimeProvider clock,
    InvoiceDocuments documents,
    UnbilledAccruals accruals,
    Security.SecurityEventLog? securityLog = null,
    Approvals.ApprovalEngine? approvals = null) : IInvoiceService
{
    public const string AuditEntityType = nameof(Invoice);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<InvoiceSummaryDto>> ListAsync(InvoiceQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<Invoice>().AsNoTracking().AsQueryable();
        if (query.ContractId is { } contractId)
        {
            q = q.Where(i => i.ContractId == contractId);
        }

        if (query.CustomerId is { } customerId)
        {
            q = q.Where(i => i.CustomerId == customerId);
        }

        if (query.Status is { } status)
        {
            var s = status.ToDomain();
            q = q.Where(i => i.Status == s);
        }

        if (query.Type is { } type)
        {
            var t = type.ToDomain();
            q = q.Where(i => i.Type == t);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(i => i.Number.Contains(term) || (i.Notes != null && i.Notes.Contains(term)));
        }

        var invoices = (await q.ToListAsync(cancellationToken))
            .Where(i => !query.OpenOnly || i.Balance != 0)
            .OrderByDescending(i => i.InvoiceDate).ThenByDescending(i => i.CreatedAtUtc)
            .Take(Math.Clamp(query.Take, 1, 2000))
            .ToList();
        var lookups = await BillingLookups.LoadAsync(db, cancellationToken);
        return invoices.Select(i => lookups.Summary(i, Today)).ToList();
    }

    public async Task<InvoiceDto> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, await LoadAsync(db, invoiceId, tracking: false, cancellationToken), cancellationToken);
    }

    public async Task<IReadOnlyList<BillingContractDto>> ListBillingContractsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var lookups = await BillingLookups.LoadAsync(db, cancellationToken);
        var billed = (await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).ToListAsync(cancellationToken)).Where(i => i.IsBilled).ToList();
        return lookups.Contracts.Values
            .OrderBy(c => c.ContractNumber, StringComparer.Ordinal)
            .Select(c =>
            {
                var clins = lookups.Clins.Values.Where(x => x.ContractId == c.Id).OrderBy(x => x.Order).ThenBy(x => x.Number, StringComparer.Ordinal).ToList();
                var dtos = clins.Select(x => ClinDto(x, billed)).ToList();
                var types = new[] { InvoiceType.TimeAndMaterials, InvoiceType.CostPlus, InvoiceType.FixedPriceMilestone, InvoiceType.TrueUp }
                    .Where(t => clins.Any(x => InvoiceGenerator.Bills(t, x.Pricing))).Select(t => t.ToContract()).ToList();
                return new BillingContractDto(c.Id, c.ContractNumber, c.Name, c.CustomerId, lookups.Customers.GetValueOrDefault(c.CustomerId)?.Name ?? "",
                    BillingMapping.Label(c.Type), c.PeriodStart, c.PeriodEnd, dtos, types);
            })
            .ToList();
    }

    public async Task<InvoicePreviewDto> PreviewAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ctx = await BillingContext.LoadAsync(db, request.ContractId, cancellationToken);
        var existing = await FindDraftAsync(db, request, tracking: false, cancellationToken);
        var gen = await generator.GenerateAsync(db, ctx, request.Type.ToDomain(), request.PeriodFrom, request.PeriodTo, request.ReleaseWithhold,
            existing?.Id, approvedOnly: false, cancellationToken);
        var manual = existing?.Lines.Where(l => !l.IsGenerated).ToList() ?? [];
        var prior = await InvoiceGenerator.PriorInvoicesAsync(db, ctx.Contract.Id, existing?.Id, approvedOnly: true, cancellationToken);
        var funding = InvoiceGenerator.CheckFunding(ctx,
            gen.Lines.Select(l => (l.ClinId, l.Amount)).Concat(manual.Select(l => (l.ClinId, l.Amount))), prior);
        var lookups = await BillingLookups.LoadAsync(db, cancellationToken);
        var lines = gen.Lines.Select((l, i) => lookups.Line(l, i + 1))
            .Concat(manual.Select((l, i) => lookups.Line(l) with { LineNumber = gen.Lines.Count + i + 1 }))
            .ToList();
        return new InvoicePreviewDto(request, ctx.Contract.ContractNumber, ctx.Customer.Name, lines, funding,
            lines.Sum(l => l.Amount), -lines.Where(l => l.Kind == InvoiceLineType.FeeWithhold).Sum(l => l.Amount), gen.RateNote,
            gen.Warnings.Concat(InvoiceGenerator.FundingWarnings(funding)).ToList(), existing?.Id);
    }

    public async Task<InvoiceDto> GenerateDraftAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ctx = await BillingContext.LoadAsync(db, request.ContractId, cancellationToken);
        var invoice = await FindDraftAsync(db, request, tracking: true, cancellationToken);
        var isNew = invoice is null;
        if (invoice is null)
        {
            var date = request.InvoiceDate ?? Today;
            var (sequence, number) = await NextNumberAsync(db, ctx, cancellationToken);
            invoice = new Invoice
            {
                ContractId = ctx.Contract.Id,
                CustomerId = ctx.Contract.CustomerId,
                Sequence = sequence,
                Number = number,
                Type = request.Type.ToDomain(),
                PeriodFrom = request.PeriodFrom,
                PeriodTo = request.PeriodTo,
                InvoiceDate = date,
                PaymentTermsDays = ctx.PaymentTermsDays,
                DueDate = date.AddDays(ctx.PaymentTermsDays),
                CreatedBy = audit.Actor,
                CreatedAtUtc = UtcNow,
            };
        }
        else if (request.InvoiceDate is { } date)
        {
            invoice.InvoiceDate = date;
            invoice.DueDate = date.AddDays(invoice.PaymentTermsDays);
        }

        invoice.ReleasesWithhold = request.ReleaseWithhold;
        if (request.Notes is not null)
        {
            invoice.Notes = Clean(request.Notes, 2000);
        }

        var gen = await generator.GenerateAsync(db, ctx, invoice.Type, invoice.PeriodFrom, invoice.PeriodTo, invoice.ReleasesWithhold,
            isNew ? null : invoice.Id, approvedOnly: false, cancellationToken);
        if (isNew && gen.Lines.Count == 0)
        {
            throw new ArgumentException($"There's nothing to bill on {ctx.Contract.ContractNumber} for {request.PeriodFrom:yyyy-MM-dd} to {request.PeriodTo:yyyy-MM-dd}."
                + (gen.Warnings.Count > 0 ? " " + string.Join(" ", gen.Warnings) : ""));
        }

        if (isNew)
        {
            db.Set<Invoice>().Add(invoice);
        }

        await ReplaceGeneratedLinesAsync(db, invoice, gen, cancellationToken);
        audit.Record(db, AuditEntityType, invoice.Id, isNew ? "Generated" : "Regenerated",
            after: new { invoice.Number, Type = invoice.Type.ToString(), invoice.PeriodFrom, invoice.PeriodTo, invoice.Total, Lines = invoice.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceDto> RegenerateDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadDraftAsync(db, invoiceId, cancellationToken);
        if (invoice.Type == InvoiceType.CreditMemo)
        {
            throw new InvalidOperationException("Credit memos have no generated lines.");
        }

        var ctx = await BillingContext.LoadAsync(db, invoice.ContractId, cancellationToken);
        var gen = await generator.GenerateAsync(db, ctx, invoice.Type, invoice.PeriodFrom, invoice.PeriodTo, invoice.ReleasesWithhold, invoice.Id, false, cancellationToken);
        await ReplaceGeneratedLinesAsync(db, invoice, gen, cancellationToken);
        audit.Record(db, AuditEntityType, invoice.Id, "Regenerated", after: new { invoice.Total, Lines = invoice.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceDto> UpdateDraftAsync(Guid invoiceId, UpdateInvoiceDraftRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadDraftAsync(db, invoiceId, cancellationToken);
        var before = new { invoice.InvoiceDate, invoice.Notes };
        invoice.InvoiceDate = request.InvoiceDate;
        invoice.DueDate = request.InvoiceDate.AddDays(invoice.PaymentTermsDays);
        invoice.Notes = Clean(request.Notes, 2000);
        invoice.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, invoice.Id, "Updated", before, new { invoice.InvoiceDate, invoice.Notes });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceDto> AddLineAsync(Guid invoiceId, AddInvoiceLineRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadDraftAsync(db, invoiceId, cancellationToken);
        var description = Clean(request.Description, 500) ?? throw new ArgumentException("A line needs a description.");
        if (request.Amount == 0)
        {
            throw new ArgumentException("A line needs an amount.");
        }

        if (request.Kind is not (InvoiceLineType.Adjustment or InvoiceLineType.Odc or InvoiceLineType.Milestone))
        {
            throw new ArgumentException("Manual lines are adjustments, ODCs or milestones; generated lines come from the billing run.");
        }

        if (request.ClinId is { } clinId && !await db.Set<Clin>().AnyAsync(c => c.Id == clinId && c.ContractId == invoice.ContractId, cancellationToken))
        {
            throw new ArgumentException("That CLIN isn't on this invoice's contract.");
        }

        var line = new InvoiceLine
        {
            InvoiceId = invoice.Id,
            LineNumber = invoice.Lines.Count + 1,
            ClinId = request.ClinId,
            Kind = request.Kind.ToDomain(),
            Description = description,
            Quantity = 1,
            Rate = request.Amount,
            Amount = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero),
            IsGenerated = false,
        };
        db.Set<InvoiceLine>().Add(line);
        invoice.Lines.Add(line);
        invoice.Recalculate();
        invoice.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, invoice.Id, "LineAdded", after: new { line.Description, line.Amount, line.ClinId });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceDto> RemoveLineAsync(Guid invoiceId, Guid lineId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadDraftAsync(db, invoiceId, cancellationToken);
        var line = invoice.Lines.FirstOrDefault(l => l.Id == lineId) ?? throw WorkbenchNotFoundException.For("Invoice line", lineId);
        await ReleaseMilestonesAsync(db, line.Sources, cancellationToken);
        db.Set<BilledSource>().RemoveRange(line.Sources);
        db.Set<InvoiceLine>().Remove(line);
        invoice.Lines.Remove(line);
        Renumber(invoice);
        invoice.Recalculate();
        invoice.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, invoice.Id, "LineRemoved", before: new { line.Description, line.Amount, Sources = line.Sources.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, invoice, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadDraftAsync(db, invoiceId, cancellationToken);
        await ReleaseMilestonesAsync(db, invoice.Lines.SelectMany(l => l.Sources), cancellationToken);
        db.Set<BilledSource>().RemoveRange(invoice.Lines.SelectMany(l => l.Sources));
        db.Set<InvoiceLine>().RemoveRange(invoice.Lines);
        db.Set<Invoice>().Remove(invoice);
        audit.Record(db, AuditEntityType, invoice.Id, "DraftDeleted", before: new { invoice.Number, invoice.Total });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<InvoiceDto> ApproveAsync(Guid invoiceId, ApproveInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadDraftAsync(db, invoiceId, cancellationToken);
        if (invoice.Lines.Count == 0 || invoice.Total == 0)
        {
            throw new InvalidOperationException("An invoice needs at least one line and a total other than zero.");
        }

        var ctx = await BillingContext.LoadAsync(db, invoice.ContractId, cancellationToken);
        var prior = await InvoiceGenerator.PriorInvoicesAsync(db, invoice.ContractId, invoice.Id, approvedOnly: true, cancellationToken);
        var funding = InvoiceGenerator.CheckFunding(ctx, invoice.Lines.Select(l => (l.ClinId, l.Amount)), prior);
        var overCeiling = funding.Where(f => f.Level == FundingCheckLevel.OverCeiling).ToList();
        if (overCeiling.Count > 0)
        {
            throw new InvalidOperationException(string.Join(" ", InvoiceGenerator.FundingWarnings(overCeiling)));
        }

        var overFunded = funding.Where(f => f.Level == FundingCheckLevel.OverFunded).ToList();
        var reason = Clean(request.FundingOverrideReason, 1000);
        if (overFunded.Count > 0 && reason is null)
        {
            throw new InvalidOperationException(string.Join(" ", InvoiceGenerator.FundingWarnings(overFunded)));
        }

        await EnsureSourcesUnbilledAsync(db, invoice, cancellationToken);

        Invoice? original = null;
        if (invoice.Type == InvoiceType.CreditMemo)
        {
            original = await db.Set<Invoice>().FirstOrDefaultAsync(i => i.Id == invoice.CreditOfInvoiceId, cancellationToken)
                ?? throw new InvalidOperationException("The credited invoice no longer exists.");
            if (!original.IsOpenReceivable || -invoice.Total > original.Balance)
            {
                throw new InvalidOperationException($"The credit ({-invoice.Total:C}) is more than invoice {original.Number}'s open balance ({original.Balance:C}).");
            }
        }
        else if (invoice.Total < 0)
        {
            throw new InvalidOperationException("The invoice total is negative; use a credit memo against the invoice instead.");
        }

        // The invoice workflow (Setup › Approval workflows; by default Finance, as before). Without the engine registered
        // (tests, seeding) the caller's permission check is the only gate.
        if (approvals is not null)
        {
            var check = await approvals.CheckAsync(db, Contracts.Approvals.ApprovalDocumentTypes.Invoice, invoice.Id, audit.Actor,
                _ => Task.FromResult(Approvals.ApprovalSubjects.Invoice(invoice)), cancellationToken);
            if (!check.CanDecide)
            {
                throw new WorkbenchForbiddenException("This invoice is waiting on another approver.");
            }

            var outcome = await approvals.DecideAsync(db, check, Contracts.Approvals.ApprovalDecisionKind.Approve, audit.Actor, null, cancellationToken);
            if (!outcome.IsComplete)
            {
                audit.Record(db, AuditEntityType, invoice.Id, "StepApproved", after: new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName).Distinct()) });
                await db.SaveChangesAsync(cancellationToken);
                return await DetailAsync(db, invoice, cancellationToken);
            }
        }

        var entry = await ledger.PostInvoiceAsync(db, invoice, ctx.Contract, ctx.Clins, cancellationToken);
        invoice.JournalEntryId = entry.Id;
        invoice.Status = InvoiceStatus.Approved;
        invoice.ApprovedBy = audit.Actor;
        invoice.ApprovedAtUtc = UtcNow;
        invoice.UpdatedAtUtc = UtcNow;
        invoice.FundingOverrideReason = overFunded.Count > 0 ? reason : null;
        if (original is not null)
        {
            original.AmountCredited += -invoice.Total;
            original.UpdatePaymentStatus();
        }

        audit.Record(db, AuditEntityType, invoice.Id, "Approved",
            new { Status = nameof(InvoiceStatus.Draft) },
            new { Status = invoice.Status.ToString(), invoice.Total, Journal = entry.Number, Funding = overFunded.Select(f => f.ClinNumber) },
            overFunded.Count > 0 ? $"Funding override: {reason}" : null);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceDto> MarkSentAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadAsync(db, invoiceId, tracking: true, cancellationToken);
        if (!invoice.IsBilled)
        {
            throw new InvalidOperationException(invoice.Status == InvoiceStatus.Draft ? "Approve the invoice before sending it." : "A void invoice can't be sent.");
        }

        invoice.SentAtUtc ??= UtcNow;
        invoice.SentBy ??= audit.Actor;
        invoice.UpdatePaymentStatus();
        audit.Record(db, AuditEntityType, invoice.Id, "Sent", after: new { Status = invoice.Status.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceDto> VoidAsync(Guid invoiceId, VoidInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        var reason = Clean(request.Reason, 1000) ?? throw new ArgumentException("A reason is required to void an invoice.");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadAsync(db, invoiceId, tracking: true, cancellationToken);
        if (invoice.Status == InvoiceStatus.Void)
        {
            throw new InvalidOperationException($"Invoice {invoice.Number} is already void.");
        }

        if (invoice.AmountPaid != 0)
        {
            throw new InvalidOperationException($"Invoice {invoice.Number} has payments applied. Void the payments first.");
        }

        var credits = await db.Set<Invoice>().AsNoTracking().Where(i => i.CreditOfInvoiceId == invoice.Id && i.Status != InvoiceStatus.Void).ToListAsync(cancellationToken);
        if (credits.Count > 0)
        {
            throw new InvalidOperationException($"Void credit memo {credits[0].Number} against this invoice first.");
        }

        var before = invoice.Status;
        var reversal = await ledger.ReverseAsync(db, invoice.JournalEntryId, Today, $"Void of {invoice.Number}: {reason}", cancellationToken);
        foreach (var source in invoice.Lines.SelectMany(l => l.Sources))
        {
            source.ReleasedAtUtc = UtcNow;
        }

        await ReleaseMilestonesAsync(db, invoice.Lines.SelectMany(l => l.Sources), cancellationToken);
        if (invoice.Type == InvoiceType.CreditMemo && invoice.IsBilled
            && await db.Set<Invoice>().FirstOrDefaultAsync(i => i.Id == invoice.CreditOfInvoiceId, cancellationToken) is { } original)
        {
            original.AmountCredited -= -invoice.Total;
            original.UpdatePaymentStatus();
        }

        invoice.Status = InvoiceStatus.Void;
        invoice.VoidedBy = audit.Actor;
        invoice.VoidedAtUtc = UtcNow;
        invoice.VoidReason = reason;
        invoice.UpdatedAtUtc = UtcNow;
        audit.Record(db, AuditEntityType, invoice.Id, "Voided", new { Status = before.ToString() },
            new { Status = nameof(InvoiceStatus.Void), Reversal = reversal?.Number, Released = invoice.Lines.Sum(l => l.Sources.Count) }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, invoice, cancellationToken);
    }

    public async Task<InvoiceDto> CreateCreditMemoAsync(Guid invoiceId, CreditMemoRequest request, CancellationToken cancellationToken = default)
    {
        var reason = Clean(request.Reason, 1000) ?? throw new ArgumentException("A reason is required for a credit memo.");
        if (request.Lines is null || request.Lines.Count == 0 || request.Lines.Any(l => l.Amount <= 0 || string.IsNullOrWhiteSpace(l.Description)))
        {
            throw new ArgumentException("Add at least one line with a description and a positive amount to credit.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var original = await LoadAsync(db, invoiceId, tracking: false, cancellationToken);
        if (!original.IsOpenReceivable)
        {
            throw new InvalidOperationException($"Invoice {original.Number} isn't open, so it can't be credited.");
        }

        var amount = request.Lines.Sum(l => l.Amount);
        if (amount > original.Balance)
        {
            throw new ArgumentException($"The credit ({amount:C}) is more than the open balance ({original.Balance:C}).");
        }

        var ctx = await BillingContext.LoadAsync(db, original.ContractId, cancellationToken);
        if (request.Lines.Any(l => l.ClinId is { } c && ctx.Clin(c) is null))
        {
            throw new ArgumentException("A credited CLIN isn't on the invoice's contract.");
        }

        var date = request.InvoiceDate ?? Today;
        var (sequence, number) = await NextNumberAsync(db, ctx, cancellationToken);
        var memo = new Invoice
        {
            ContractId = original.ContractId,
            CustomerId = original.CustomerId,
            Sequence = sequence,
            Number = number,
            Type = InvoiceType.CreditMemo,
            PeriodFrom = original.PeriodFrom,
            PeriodTo = original.PeriodTo,
            InvoiceDate = date,
            DueDate = date,
            PaymentTermsDays = 0,
            CreditOfInvoiceId = original.Id,
            Notes = $"Credit against invoice {original.Number}: {reason}",
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        foreach (var l in request.Lines)
        {
            memo.Lines.Add(new InvoiceLine
            {
                InvoiceId = memo.Id,
                LineNumber = memo.Lines.Count + 1,
                ClinId = l.ClinId,
                Kind = InvoiceLineKind.Adjustment,
                Description = Clean(l.Description, 500)!,
                Quantity = 1,
                Rate = -l.Amount,
                Amount = -Math.Round(l.Amount, 2, MidpointRounding.AwayFromZero),
                IsGenerated = false,
            });
        }

        memo.Recalculate();
        db.Set<Invoice>().Add(memo);
        audit.Record(db, AuditEntityType, memo.Id, "CreditMemoCreated", after: new { memo.Number, Credits = original.Number, memo.Total }, reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, memo, cancellationToken);
    }

    public async Task<InvoicePrintDto> GetPrintAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadAsync(db, invoiceId, tracking: false, cancellationToken);
        return await documents.PrintAsync(db, invoice, await DetailAsync(db, invoice, cancellationToken), cancellationToken);
    }

    public async Task<BillingExportFile> ExportWawfCsvAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invoice = await LoadAsync(db, invoiceId, tracking: false, cancellationToken);
        var print = await documents.PrintAsync(db, invoice, await DetailAsync(db, invoice, cancellationToken), cancellationToken);
        var file = InvoiceDocuments.WawfCsv(print, await BillingContext.LoadAsync(db, invoice.ContractId, cancellationToken));
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(Contracts.Security.SecurityEventTypes.ExportFinancialCsv, file.FileName, detail: $"WAWF invoice export, {print.Invoice.Lines.Count} lines", cancellationToken: cancellationToken);
        }

        return file;
    }

    public async Task<ContractBillingSummaryDto> GetContractSummaryAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ctx = await BillingContext.LoadAsync(db, contractId, cancellationToken);
        var invoices = await db.Set<Invoice>().AsNoTracking().Include(i => i.Lines).Where(i => i.ContractId == contractId).ToListAsync(cancellationToken);
        var billed = invoices.Where(i => i.IsBilled).ToList();
        var clins = ctx.Clins.Select(c => ClinDto(c, billed)).ToList();
        return new ContractBillingSummaryDto(
            contractId,
            ctx.Clins.Count > 0 ? ctx.Clins.Sum(c => c.FundedValue) : ctx.Contract.FundedValue,
            ctx.Clins.Count > 0 ? ctx.Clins.Sum(c => c.CeilingValue) : ctx.Contract.CeilingValue,
            billed.Sum(i => i.Total),
            billed.Sum(i => i.AmountPaid),
            billed.Sum(i => i.Balance),
            InvoiceGenerator.WithheldToDate(billed, null),
            invoices.Where(i => i.Status == InvoiceStatus.Draft).Sum(i => i.Total),
            billed.Count(i => i.Type != InvoiceType.CreditMemo),
            billed.Count == 0 ? null : billed.Max(i => i.InvoiceDate),
            clins);
    }

    public async Task<ContractBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return SettingsDto(await BillingContext.LoadAsync(db, contractId, cancellationToken));
    }

    public async Task<ContractBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveContractBillingSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var prefix = (request.InvoicePrefix ?? "").Trim().ToUpperInvariant();
        if (!PrefixPattern().IsMatch(prefix))
        {
            throw new ArgumentException("The invoice prefix is 1 to 16 letters, digits or dashes.");
        }

        if (request.FeeWithholdRate is < 0 or > 1 || request.MaterialHandlingRate is < 0 or > 1)
        {
            throw new ArgumentException("Rates are fractions between 0 and 1 (0.15 = 15%).");
        }

        if (request.FeeWithholdCap is < 0 || request.PaymentTermsDays is < 0 or > 365 || request.CustomerPaymentTermsDays is < 0 or > 365)
        {
            throw new ArgumentException("Payment terms are 0 to 365 days, and the withhold cap can't be negative.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ctx = await BillingContext.LoadAsync(db, contractId, cancellationToken);
        if (await db.Set<ContractBillingSettings>().AnyAsync(s => s.InvoicePrefix == prefix && s.ContractId != contractId, cancellationToken))
        {
            throw new ArgumentException($"Another contract already uses the invoice prefix {prefix}.");
        }

        var settings = await db.Set<ContractBillingSettings>().FirstOrDefaultAsync(s => s.ContractId == contractId, cancellationToken);
        var before = settings is null ? null : new { settings.InvoicePrefix, settings.FeeWithholdRate, settings.FeeWithholdCap, settings.MaterialHandlingRate, settings.PaymentTermsDays };
        if (settings is null)
        {
            settings = new ContractBillingSettings { ContractId = contractId, InvoicePrefix = prefix };
            db.Set<ContractBillingSettings>().Add(settings);
        }

        settings.InvoicePrefix = prefix;
        settings.FeeWithholdRate = request.FeeWithholdRate;
        settings.FeeWithholdCap = request.FeeWithholdCap;
        settings.MaterialHandlingRate = request.MaterialHandlingRate is 0 ? null : request.MaterialHandlingRate;
        settings.PaymentTermsDays = request.PaymentTermsDays;
        settings.PayingOffice = Clean(request.PayingOffice, 500);
        settings.AdminOffice = Clean(request.AdminOffice, 500);
        settings.CageCode = Clean(request.CageCode, 5);
        settings.UpdatedBy = audit.Actor;
        settings.UpdatedAtUtc = UtcNow;

        if (request.CustomerPaymentTermsDays is not null || request.CustomerBillingAddress is not null)
        {
            var profile = await db.Set<CustomerBillingProfile>().FirstOrDefaultAsync(p => p.CustomerId == ctx.Contract.CustomerId, cancellationToken);
            if (profile is null)
            {
                profile = new CustomerBillingProfile { CustomerId = ctx.Contract.CustomerId };
                db.Set<CustomerBillingProfile>().Add(profile);
            }

            profile.PaymentTermsDays = request.CustomerPaymentTermsDays ?? profile.PaymentTermsDays;
            profile.BillingAddress = request.CustomerBillingAddress is null ? profile.BillingAddress : Clean(request.CustomerBillingAddress, 1000);
            profile.UpdatedBy = audit.Actor;
            profile.UpdatedAtUtc = UtcNow;
        }

        audit.Record(db, nameof(ContractBillingSettings), contractId, "Saved", before,
            new { settings.InvoicePrefix, settings.FeeWithholdRate, settings.FeeWithholdCap, settings.MaterialHandlingRate, settings.PaymentTermsDays });
        await db.SaveChangesAsync(cancellationToken);
        return SettingsDto(await BillingContext.LoadAsync(db, contractId, cancellationToken));
    }

    public async Task<UnbilledAccrualDto> GetUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await accruals.GetAsync(db, fiscalPeriodId, cancellationToken);
    }

    public async Task<UnbilledAccrualDto> PostUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await accruals.PostAsync(db, fiscalPeriodId, cancellationToken);
    }

    // ---- helpers ----

    private static void Validate(GenerateInvoiceRequest request)
    {
        if (request.PeriodTo < request.PeriodFrom)
        {
            throw new ArgumentException("The billing period ends before it starts.");
        }

        if (request.PeriodTo.DayNumber - request.PeriodFrom.DayNumber > 400)
        {
            throw new ArgumentException("A billing period can be at most a year long.");
        }

        if (request.Type == InvoiceKind.CreditMemo)
        {
            throw new ArgumentException("Credit memos are created from an approved invoice.");
        }
    }

    private static async Task<Invoice?> FindDraftAsync(IWorkbenchDbContext db, GenerateInvoiceRequest request, bool tracking, CancellationToken ct)
    {
        var type = request.Type.ToDomain();
        var q = db.Set<Invoice>().Include(i => i.Lines).ThenInclude(l => l.Sources).AsQueryable();
        if (!tracking)
        {
            q = q.AsNoTracking();
        }

        return await q.FirstOrDefaultAsync(i => i.ContractId == request.ContractId && i.Type == type && i.Status == InvoiceStatus.Draft
            && i.PeriodFrom == request.PeriodFrom && i.PeriodTo == request.PeriodTo, ct);
    }

    private static async Task<Invoice> LoadAsync(IWorkbenchDbContext db, Guid invoiceId, bool tracking, CancellationToken ct)
    {
        var q = db.Set<Invoice>().Include(i => i.Lines).ThenInclude(l => l.Sources).AsQueryable();
        if (!tracking)
        {
            q = q.AsNoTracking();
        }

        var invoice = await q.FirstOrDefaultAsync(i => i.Id == invoiceId, ct) ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
        invoice.Lines.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));
        return invoice;
    }

    private static async Task<Invoice> LoadDraftAsync(IWorkbenchDbContext db, Guid invoiceId, CancellationToken ct)
    {
        var invoice = await LoadAsync(db, invoiceId, tracking: true, ct);
        return invoice.Status == InvoiceStatus.Draft
            ? invoice
            : throw new InvalidOperationException($"Invoice {invoice.Number} is {invoice.Status}; only drafts can be changed.");
    }

    private static async Task<(int Sequence, string Number)> NextNumberAsync(IWorkbenchDbContext db, BillingContext ctx, CancellationToken ct)
    {
        var prefix = ctx.Settings?.InvoicePrefix ?? DefaultPrefix(ctx.Contract.ContractNumber);
        var sequence = (await db.Set<Invoice>().Where(i => i.ContractId == ctx.Contract.Id).MaxAsync(i => (int?)i.Sequence, ct) ?? 0) + 1;
        while (await db.Set<Invoice>().AnyAsync(i => i.Number == $"{prefix}-{sequence:D4}", ct))
        {
            sequence++;
        }

        return (sequence, $"{prefix}-{sequence:D4}");
    }

    /// <summary>The contract number without punctuation, at most 16 characters (so the invoice number fits WAWF's 22).</summary>
    public static string DefaultPrefix(string contractNumber)
    {
        var compact = new string(contractNumber.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return compact.Length == 0 ? "INV" : compact[..Math.Min(16, compact.Length)];
    }

    private async Task ReplaceGeneratedLinesAsync(IWorkbenchDbContext db, Invoice invoice, Generation gen, CancellationToken ct)
    {
        var old = invoice.Lines.Where(l => l.IsGenerated).ToList();
        await ReleaseMilestonesAsync(db, old.SelectMany(l => l.Sources), ct);
        foreach (var line in old)
        {
            db.Set<BilledSource>().RemoveRange(line.Sources);
            db.Set<InvoiceLine>().Remove(line);
            invoice.Lines.Remove(line);
        }

        var manual = invoice.Lines.ToList();
        invoice.Lines.Clear();
        foreach (var d in gen.Lines)
        {
            var line = new InvoiceLine
            {
                InvoiceId = invoice.Id,
                ClinId = d.ClinId,
                Kind = d.Kind,
                Description = d.Description.Length > 500 ? d.Description[..500] : d.Description,
                ProjectId = d.ProjectId,
                WbsElementId = d.WbsElementId,
                LaborCategoryId = d.LaborCategoryId,
                OdcType = d.OdcType,
                Quantity = d.Quantity,
                Rate = d.Rate,
                Amount = d.Amount,
                IsGenerated = true,
            };
            foreach (var s in d.Sources)
            {
                s.InvoiceId = invoice.Id;
                s.InvoiceLineId = line.Id;
                line.Sources.Add(s);
            }

            // Client-generated keys: add explicitly rather than relying on change detection.
            invoice.Lines.Add(line);
            db.Set<InvoiceLine>().Add(line);
        }

        invoice.Lines.AddRange(manual);
        Renumber(invoice);

        var milestoneIds = gen.Lines.SelectMany(l => l.Sources).Where(s => s.Kind == BilledSourceKind.Milestone).Select(s => s.SourceId).ToList();
        foreach (var m in await db.Set<BillingMilestone>().Where(m => milestoneIds.Contains(m.Id)).ToListAsync(ct))
        {
            m.InvoiceId = invoice.Id;
        }

        invoice.RateNote = gen.RateNote;
        invoice.GenerationWarnings = gen.Warnings.Count == 0 ? null : string.Join("\n", gen.Warnings);
        if (invoice.GenerationWarnings is { Length: > 4000 } w)
        {
            invoice.GenerationWarnings = w[..4000];
        }

        invoice.Recalculate();
        invoice.UpdatedAtUtc = UtcNow;
    }

    private static void Renumber(Invoice invoice)
    {
        for (var i = 0; i < invoice.Lines.Count; i++)
        {
            invoice.Lines[i].LineNumber = i + 1;
        }
    }

    private static async Task ReleaseMilestonesAsync(IWorkbenchDbContext db, IEnumerable<BilledSource> sources, CancellationToken ct)
    {
        var ids = sources.Where(s => s.Kind == BilledSourceKind.Milestone).Select(s => s.SourceId).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        foreach (var m in await db.Set<BillingMilestone>().Where(m => ids.Contains(m.Id)).ToListAsync(ct))
        {
            m.InvoiceId = null;
        }
    }

    /// <summary>Refuses approval when another live invoice already bills one of this invoice's hours, ODCs or milestones.</summary>
    private static async Task EnsureSourcesUnbilledAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct)
    {
        var ids = invoice.Lines.SelectMany(l => l.Sources).Select(s => s.SourceId).ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var clash = await (
                from s in db.Set<BilledSource>().AsNoTracking()
                join i in db.Set<Invoice>().AsNoTracking() on s.InvoiceId equals i.Id
                where ids.Contains(s.SourceId) && s.InvoiceId != invoice.Id && s.ReleasedAtUtc == null && i.Status != InvoiceStatus.Void
                select i.Number)
            .FirstOrDefaultAsync(ct);
        if (clash is not null)
        {
            throw new InvalidOperationException($"Some hours or ODCs on this draft are already on invoice {clash}. Regenerate the draft.");
        }
    }

    private async Task<InvoiceDto> DetailAsync(IWorkbenchDbContext db, Invoice invoice, CancellationToken ct)
    {
        var lookups = await BillingLookups.LoadAsync(db, ct);
        var ctx = await BillingContext.LoadAsync(db, invoice.ContractId, ct);
        var prior = await InvoiceGenerator.PriorInvoicesAsync(db, invoice.ContractId, invoice.Id, approvedOnly: true, ct);
        var funding = invoice.Status == InvoiceStatus.Void
            ? []
            : InvoiceGenerator.CheckFunding(ctx, invoice.Lines.Select(l => (l.ClinId, l.Amount)), invoice.IsBilled
                ? prior.Where(p => p.ApprovedAtUtc <= invoice.ApprovedAtUtc)
                : prior);
        var journal = await BillingLookups.JournalNumbersAsync(db, [invoice.JournalEntryId], ct);
        string? creditOf = null;
        if (invoice.CreditOfInvoiceId is { } cid)
        {
            creditOf = await db.Set<Invoice>().AsNoTracking().Where(i => i.Id == cid).Select(i => i.Number).FirstOrDefaultAsync(ct);
        }

        var warnings = (invoice.GenerationWarnings ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (invoice.Status == InvoiceStatus.Draft)
        {
            warnings.AddRange(InvoiceGenerator.FundingWarnings(funding));
        }

        return new InvoiceDto(
            lookups.Summary(invoice, Today),
            invoice.Lines.OrderBy(l => l.LineNumber).Select(l => lookups.Line(l)).ToList(),
            funding,
            invoice.Withheld,
            invoice.RateNote,
            invoice.Notes,
            invoice.FundingOverrideReason,
            invoice.CreditOfInvoiceId,
            creditOf,
            invoice.JournalEntryId,
            invoice.JournalEntryId is { } j ? journal.GetValueOrDefault(j) : null,
            invoice.PaymentTermsDays,
            invoice.CreatedBy,
            invoice.CreatedAtUtc,
            invoice.ApprovedBy,
            invoice.ApprovedAtUtc,
            invoice.SentAtUtc,
            invoice.VoidedBy,
            invoice.VoidedAtUtc,
            invoice.VoidReason,
            warnings);
    }

    private static BillingClinDto ClinDto(Clin c, IEnumerable<Invoice> billed) =>
        new(c.Id, c.Number, c.Description, BillingMapping.Label(c.Pricing), c.IsExercised, c.FeeRate, c.FundedValue, c.CeilingValue,
            billed.SelectMany(i => i.Lines).Where(l => l.ClinId == c.Id).Sum(l => l.Amount));

    private static ContractBillingSettingsDto SettingsDto(BillingContext ctx) => new(
        ctx.Contract.Id,
        ctx.Settings?.InvoicePrefix ?? DefaultPrefix(ctx.Contract.ContractNumber),
        ctx.Settings?.FeeWithholdRate ?? 0,
        ctx.Settings?.FeeWithholdCap,
        ctx.Settings?.MaterialHandlingRate,
        ctx.Settings?.PaymentTermsDays,
        ctx.Settings?.PayingOffice,
        ctx.Settings?.AdminOffice,
        ctx.Settings?.CageCode,
        ctx.Profile?.PaymentTermsDays ?? 30,
        ctx.Profile?.BillingAddress);

    internal static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length > max ? throw new ArgumentException($"Keep it to {max} characters.") : trimmed;
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,15}$")]
    private static partial Regex PrefixPattern();
}
