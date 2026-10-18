using Microsoft.EntityFrameworkCore;
using Workbench.Application.ContractDetail;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Closeout;
using Workbench.Domain.Accounting;
using Workbench.Domain.Assets;
using Workbench.Domain.Billing;
using Workbench.Domain.Closeout;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Ceilings;

public sealed class CloseoutService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, AccessControl access, TimeProvider clock) : ICloseoutService
{
    internal const string Entity = "Closeout";

    private static readonly CloseoutItem[] ContractItems = Enum.GetValues<CloseoutItem>();

    private static readonly CloseoutItem[] ProjectItems =
        [CloseoutItem.AllCostsPosted, CloseoutItem.SubcontractsClosed, CloseoutItem.PropertyDispositioned, CloseoutItem.RecordsRetention];

    private static readonly InvoiceStatus[] BilledStatuses = [InvoiceStatus.Approved, InvoiceStatus.Sent, InvoiceStatus.PartiallyPaid, InvoiceStatus.Paid];

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<CloseoutDto> GetAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var target = await Target.LoadAsync(db, scope, id, cancellationToken);
        var record = await FindAsync(db, target, cancellationToken) ?? new CloseoutRecord { ContractId = target.ContractId, ProjectId = target.ProjectId };
        return await DtoAsync(db, target, record, cancellationToken);
    }

    public Task<CloseoutDto> MarkPhysicallyCompleteAsync(CloseoutScope scope, Guid id, PhysicallyCompleteRequest request, CancellationToken cancellationToken = default) =>
        ChangeAsync(scope, id, "PhysicallyComplete", null, (db, target, record, ct) =>
        {
            Expect(record, CloseoutStatus.Open, "marked physically complete");
            var on = request?.On ?? Today;
            if (on > Today)
            {
                throw new ArgumentException("Physical completion can't be in the future.");
            }

            record.Status = CloseoutStatus.PhysicallyComplete;
            record.PhysicallyCompleteOn = on;
            return Task.FromResult<object?>(new { On = on });
        }, cancellationToken);

    public Task<CloseoutDto> StartClosingAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(scope, id, "ClosingStarted", null, (db, target, record, ct) =>
        {
            Expect(record, CloseoutStatus.PhysicallyComplete, "moved to closing");
            record.Status = CloseoutStatus.Closing;
            return Task.FromResult<object?>(null);
        }, cancellationToken);

    public Task<CloseoutDto> CloseAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(scope, id, "Closed", null, async (db, target, record, ct) =>
        {
            Expect(record, CloseoutStatus.Closing, "closed");
            var items = await ItemsAsync(db, target, record, ct);
            var open = items.Where(i => !i.IsComplete).ToList();
            if (open.Count > 0)
            {
                throw new ArgumentException("The closeout checklist isn't complete: " + string.Join("; ", open.Select(i => $"{i.Label} ({i.Detail})")) + ".");
            }

            record.Status = CloseoutStatus.Closed;
            record.ClosedAtUtc = UtcNow;
            record.ClosedBy = access.Email;
            if (target.Contract is { } contract)
            {
                var tracked = await db.Set<Contract>().FirstAsync(c => c.Id == contract.Id, ct);
                record.StatusBeforeClose = tracked.Status.ToString();
                tracked.Status = ContractStatus.Closed;
            }
            else
            {
                var tracked = await db.Set<Project>().FirstAsync(p => p.Id == target.ProjectId, ct);
                record.StatusBeforeClose = tracked.Status.ToString();
                tracked.Status = ProjectStatus.Closed;
            }

            return new { Checklist = items.Select(i => new { Item = i.Check.ToString(), i.IsSatisfied, i.IsWaived }) };
        }, cancellationToken);

    public Task<CloseoutDto> ReopenAsync(CloseoutScope scope, Guid id, CloseoutReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = CeilingService.Required(request?.Reason, "A reason for reopening", 1000);
        return ChangeAsync(scope, id, "Reopened", reason, async (db, target, record, ct) =>
        {
            if (record.Status == CloseoutStatus.Open)
            {
                throw new ArgumentException("The closeout is already open.");
            }

            var was = record.Status;
            if (was == CloseoutStatus.Closed)
            {
                if (target.Contract is { } contract)
                {
                    var tracked = await db.Set<Contract>().FirstAsync(c => c.Id == contract.Id, ct);
                    tracked.Status = Enum.TryParse<ContractStatus>(record.StatusBeforeClose, out var s) && s != ContractStatus.Closed ? s : ContractStatus.Active;
                }
                else
                {
                    var tracked = await db.Set<Project>().FirstAsync(p => p.Id == target.ProjectId, ct);
                    tracked.Status = Enum.TryParse<ProjectStatus>(record.StatusBeforeClose, out var s) && s != ProjectStatus.Closed ? s : ProjectStatus.Active;
                }

                record.ReopenCount++;
            }

            record.Status = CloseoutStatus.Open;
            record.ClosedAtUtc = null;
            record.ClosedBy = null;
            return new { From = was.ToString() };
        }, cancellationToken);
    }

    public Task<CloseoutDto> SaveRecordsAsync(CloseoutScope scope, Guid id, SaveCloseoutRecordsRequest request, CancellationToken cancellationToken = default) =>
        ChangeAsync(scope, id, "RecordsUpdated", null, (db, target, record, ct) =>
        {
            ArgumentNullException.ThrowIfNull(request);
            NotClosed(record);
            if (target.Contract is null && (request.ReleaseOfClaimsOn is not null || request.AssignmentOfRefundsOn is not null))
            {
                throw new ArgumentException("Release of claims and assignment of refunds are recorded on the contract's closeout.");
            }

            if (request.RecordsRetentionUntil is { } until && until < Today)
            {
                throw new ArgumentException("The records retention date has to be today or later.");
            }

            if (request.ReleaseOfClaimsOn > Today || request.AssignmentOfRefundsOn > Today)
            {
                throw new ArgumentException("A release or assignment can't be dated in the future.");
            }

            record.ReleaseOfClaimsOn = request.ReleaseOfClaimsOn;
            record.ReleaseOfClaimsNote = CeilingService.Optional(request.ReleaseOfClaimsNote, 1000);
            record.AssignmentOfRefundsOn = request.AssignmentOfRefundsOn;
            record.AssignmentOfRefundsNote = CeilingService.Optional(request.AssignmentOfRefundsNote, 1000);
            record.RecordsRetentionUntil = request.RecordsRetentionUntil;
            return Task.FromResult<object?>(new { record.ReleaseOfClaimsOn, record.AssignmentOfRefundsOn, record.RecordsRetentionUntil });
        }, cancellationToken);

    public Task<CloseoutDto> WaiveAsync(CloseoutScope scope, Guid id, WaiveCloseoutItemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = CeilingService.Required(request.Reason, "A reason for waiving the item", 1000);
        var item = (CloseoutItem)(int)request.Check;
        return ChangeAsync(scope, id, "ItemWaived", reason, async (db, target, record, ct) =>
        {
            NotClosed(record);
            if (!ItemsFor(target).Contains(item))
            {
                throw new ArgumentException("That item isn't on this checklist.");
            }

            var existing = await db.Set<CloseoutWaiver>().FirstOrDefaultAsync(w => w.CloseoutId == record.Id && w.Item == item, ct);
            if (existing is null)
            {
                db.Set<CloseoutWaiver>().Add(new CloseoutWaiver { CloseoutId = record.Id, Item = item, Reason = reason, WaivedBy = access.Email, WaivedAtUtc = UtcNow });
            }
            else
            {
                (existing.Reason, existing.WaivedBy, existing.WaivedAtUtc) = (reason, access.Email, UtcNow);
            }

            return new { Item = item.ToString() };
        }, cancellationToken);
    }

    public Task<CloseoutDto> RemoveWaiverAsync(CloseoutScope scope, Guid id, CloseoutCheck check, CancellationToken cancellationToken = default) =>
        ChangeAsync(scope, id, "WaiverRemoved", null, async (db, target, record, ct) =>
        {
            NotClosed(record);
            var item = (CloseoutItem)(int)check;
            if (await db.Set<CloseoutWaiver>().FirstOrDefaultAsync(w => w.CloseoutId == record.Id && w.Item == item, ct) is { } waiver)
            {
                db.Set<CloseoutWaiver>().Remove(waiver);
            }

            return new { Item = item.ToString() };
        }, cancellationToken);

    public Task<CloseoutDto> MarkFinalInvoiceAsync(Guid contractId, MarkFinalInvoiceRequest request, CancellationToken cancellationToken = default) =>
        ChangeAsync(CloseoutScope.Contract, contractId, "FinalInvoiceMarked", null, async (db, target, record, ct) =>
        {
            ArgumentNullException.ThrowIfNull(request);
            NotClosed(record);
            if (request.InvoiceId is { } invoiceId)
            {
                var invoice = await db.Set<Invoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, ct)
                    ?? throw WorkbenchNotFoundException.For("Invoice", invoiceId);
                if (invoice.ContractId != contractId)
                {
                    throw new ArgumentException("That invoice is on another contract.");
                }

                if (!invoice.IsBilled || invoice.Type == InvoiceType.CreditMemo)
                {
                    throw new ArgumentException($"Invoice {invoice.Number} is {invoice.Status}: only an approved (or sent or paid) invoice can be the final invoice.");
                }
            }

            record.FinalInvoiceId = request.InvoiceId;
            return new { Invoice = request.InvoiceId };
        }, cancellationToken);

    public Task<CloseoutDto> RecordDeobligationAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        ChangeAsync(CloseoutScope.Contract, contractId, "DeobligationRecorded", null, async (db, target, record, ct) =>
        {
            NotClosed(record);
            var (funded, billed) = await FundedAndBilledAsync(db, target, ct);
            record.DeobligationAmount = funded - billed;
            record.DeobligationRecordedBy = access.Email;
            record.DeobligationRecordedAtUtc = UtcNow;
            return new { Funded = funded, Billed = billed, Deobligation = record.DeobligationAmount };
        }, cancellationToken);

    // ---- helpers ----

    private async Task<CloseoutDto> ChangeAsync(
        CloseoutScope scope, Guid id, string action, string? reason,
        Func<IWorkbenchDbContext, Target, CloseoutRecord, CancellationToken, Task<object?>> change, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var target = await Target.LoadAsync(db, scope, id, ct);
        var record = await db.Set<CloseoutRecord>().FirstOrDefaultAsync(c => target.ContractId != null ? c.ContractId == target.ContractId : c.ProjectId == target.ProjectId, ct);
        if (record is null)
        {
            record = new CloseoutRecord { ContractId = target.ContractId, ProjectId = target.ProjectId, CreatedAtUtc = UtcNow };
            db.Set<CloseoutRecord>().Add(record);
        }

        var before = record.Status.ToString();
        var after = await change(db, target, record, ct);
        record.UpdatedBy = access.Email;
        record.UpdatedAtUtc = UtcNow;
        audit.Record(db, Entity, record.Id, action, before: new { Status = before, Target = target.Label }, after: after, reason: reason);
        await db.SaveChangesAsync(ct);
        return await DtoAsync(db, target, record, ct);
    }

    private static void Expect(CloseoutRecord record, CloseoutStatus status, string what)
    {
        if (record.Status != status)
        {
            throw new ArgumentException($"The closeout is {Label(record.Status)}; only a closeout that is {Label(status)} can be {what}.");
        }
    }

    private static void NotClosed(CloseoutRecord record)
    {
        if (record.Status == CloseoutStatus.Closed)
        {
            throw new ArgumentException("The closeout is closed. Reopen it (with a reason) to change it.");
        }
    }

    private static string Label(CloseoutStatus s) => s switch
    {
        CloseoutStatus.PhysicallyComplete => "physically complete",
        _ => s.ToString().ToLowerInvariant(),
    };

    private static async Task<CloseoutRecord?> FindAsync(IWorkbenchDbContext db, Target target, CancellationToken ct) =>
        await db.Set<CloseoutRecord>().AsNoTracking()
            .FirstOrDefaultAsync(c => target.ContractId != null ? c.ContractId == target.ContractId : c.ProjectId == target.ProjectId, ct);

    private static IReadOnlyList<CloseoutItem> ItemsFor(Target target) => target.Contract is not null ? ContractItems : ProjectItems;

    private async Task<CloseoutDto> DtoAsync(IWorkbenchDbContext db, Target target, CloseoutRecord record, CancellationToken ct)
    {
        var items = await ItemsAsync(db, target, record, ct);
        var (funded, billed) = target.Contract is null ? (0m, 0m) : await FundedAndBilledAsync(db, target, ct);
        var invoices = target.Contract is null ? [] : await db.Set<Invoice>().AsNoTracking()
            .Where(i => i.ContractId == target.ContractId && BilledStatuses.Contains(i.Status) && i.Type != InvoiceType.CreditMemo)
            .ToListAsync(ct);
        var history = await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == Entity && a.EntityId == record.Id).ToListAsync(ct);
        var finance = (await access.GetOrgRolesAsync(ct)).IsFinance();
        return new CloseoutDto(
            target.Scope, target.Id, target.Code, target.Name, (CloseoutState)(int)record.Status, record.PhysicallyCompleteOn, items,
            record.FinalInvoiceId, invoices.FirstOrDefault(i => i.Id == record.FinalInvoiceId)?.Number,
            funded, billed, funded - billed, record.DeobligationAmount, record.DeobligationRecordedBy,
            record.ReleaseOfClaimsOn, record.ReleaseOfClaimsNote, record.AssignmentOfRefundsOn, record.AssignmentOfRefundsNote, record.RecordsRetentionUntil,
            record.ClosedAtUtc, record.ClosedBy, record.ReopenCount,
            invoices.OrderByDescending(i => i.PeriodTo).ThenByDescending(i => i.Sequence)
                .Select(i => new CloseoutInvoiceDto(i.Id, i.Number, i.Status.ToString(), i.PeriodTo, i.Total)).ToList(),
            history.OrderByDescending(h => h.OccurredAtUtc).Select(h => new CloseoutEventDto(h.Action, h.Actor, h.OccurredAtUtc, h.Reason)).ToList(),
            finance);
    }

    private async Task<List<CloseoutItemDto>> ItemsAsync(IWorkbenchDbContext db, Target target, CloseoutRecord record, CancellationToken ct)
    {
        var waivers = await db.Set<CloseoutWaiver>().AsNoTracking().Where(w => w.CloseoutId == record.Id).ToDictionaryAsync(w => w.Item, ct);
        var projectIds = target.ProjectIds;
        var wbsIds = await db.Set<WbsElement>().AsNoTracking().Where(w => projectIds.Contains(w.ProjectId)).Select(w => w.Id).ToListAsync(ct);
        var result = new List<CloseoutItemDto>();
        foreach (var item in ItemsFor(target))
        {
            var (ok, label, detail, link) = item switch
            {
                CloseoutItem.FinalRatesSettled => await FinalRatesAsync(db, target, Today, ct),
                CloseoutItem.AllCostsPosted => await CostsPostedAsync(db, wbsIds, ct),
                CloseoutItem.FinalInvoiceIssued => await FinalInvoiceAsync(db, record, ct),
                CloseoutItem.SubcontractsClosed => await SubcontractsAsync(db, target, ct),
                CloseoutItem.PropertyDispositioned => await PropertyAsync(db, target, ct),
                CloseoutItem.FundsDeobligated => await DeobligationAsync(db, target, record, ct),
                CloseoutItem.ReleaseOfClaims => (record.ReleaseOfClaimsOn is not null, "Release of claims recorded",
                    record.ReleaseOfClaimsOn is { } d ? $"Signed {d:yyyy-MM-dd}" + (record.ReleaseOfClaimsNote is { } n ? $": {n}" : "") : "Record the contractor's release of claims.", null),
                CloseoutItem.AssignmentOfRefunds => (record.AssignmentOfRefundsOn is not null, "Assignment of refunds recorded",
                    record.AssignmentOfRefundsOn is { } d2 ? $"Signed {d2:yyyy-MM-dd}" + (record.AssignmentOfRefundsNote is { } n2 ? $": {n2}" : "") : "Record the assignment of refunds, rebates and credits.", null),
                _ => (record.RecordsRetentionUntil is not null, "Records retention date set",
                    record.RecordsRetentionUntil is { } r ? $"Keep records until {r:yyyy-MM-dd}" : "Set how long the records are kept (usually 3 years after final payment).", null),
            };
            var waiver = waivers.GetValueOrDefault(item);
            result.Add(new CloseoutItemDto((CloseoutCheck)(int)item, label, ok, waiver is not null, waiver?.Reason, detail, link));
        }

        return result;
    }

    private static async Task<(bool, string, string, string?)> FinalRatesAsync(IWorkbenchDbContext db, Target target, DateOnly today, CancellationToken ct)
    {
        const string label = "Final indirect rates settled";
        const string link = "accounting/indirect-rates";
        var pools = await db.Set<IndirectPoolSetup>().AsNoTracking().Where(p => p.IsActive).Select(p => p.Pool).ToListAsync(ct);
        if (pools.Count == 0)
        {
            return (true, label, "No indirect pools are set up.", link);
        }

        var contract = target.Contract!;
        var end = contract.PeriodEnd < today ? contract.PeriodEnd : today;
        var years = await db.Set<FiscalYear>().AsNoTracking().Where(y => y.Start <= end && y.End >= contract.PeriodStart).OrderBy(y => y.Start).ToListAsync(ct);
        var actual = await db.Set<IndirectRate>().AsNoTracking().Where(r => r.Basis == IndirectRateBasis.Actual && r.SupersededAtUtc == null)
            .Select(r => new { r.FiscalYear, r.Pool }).ToListAsync(ct);
        var missing = years.Where(y => pools.Any(p => !actual.Any(a => a.FiscalYear == y.Year && a.Pool == p))).Select(y => $"FY{y.Year}").ToList();
        return missing.Count == 0
            ? (true, label, years.Count == 0 ? "No fiscal years in the period of performance." : $"Actual rates set for {string.Join(", ", years.Select(y => $"FY{y.Year}"))}.", link)
            : (false, label, $"Actual rates missing for {string.Join(", ", missing)}.", link);
    }

    private static async Task<(bool, string, string, string?)> CostsPostedAsync(IWorkbenchDbContext db, List<Guid> wbsIds, CancellationToken ct)
    {
        const string label = "All costs posted";
        var timesheets = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where wbsIds.Contains(e.WbsElementId) && e.Hours != 0 && t.Status != TimesheetStatus.Approved
                select t.Id)
            .Distinct().CountAsync(ct);
        var odcs = await db.Set<OdcEntry>().AsNoTracking()
            .CountAsync(o => wbsIds.Contains(o.WbsElementId) && (o.Status == OdcStatus.Draft || o.Status == OdcStatus.Submitted), ct);
        var bills = await (
                from l in db.Set<ApBillLine>().AsNoTracking()
                join b in db.Set<ApBill>().AsNoTracking() on l.BillId equals b.Id
                where l.WbsElementId != null && wbsIds.Contains(l.WbsElementId.Value)
                    && (b.Status == ApBillStatus.Draft || b.Status == ApBillStatus.PendingApproval || b.Status == ApBillStatus.Approved)
                select b.Id)
            .Distinct().CountAsync(ct);
        var committed = (await ProcurementSupport.CommitmentsByWbsAsync(db, wbsIds, ct)).Sum(c => c.Committed);
        var open = new List<string>();
        if (timesheets > 0)
        {
            open.Add($"{timesheets} timesheet(s) with hours not yet approved");
        }

        if (odcs > 0)
        {
            open.Add($"{odcs} ODC(s) or expense line(s) not yet approved");
        }

        if (bills > 0)
        {
            open.Add($"{bills} AP bill(s) not yet posted");
        }

        if (committed > 0)
        {
            open.Add($"{committed:C} of open purchase order commitments");
        }

        return open.Count == 0 ? (true, label, "No unapproved time, ODCs, unposted bills or open commitments.", null) : (false, label, string.Join(", ", open), null);
    }

    private static async Task<(bool, string, string, string?)> FinalInvoiceAsync(IWorkbenchDbContext db, CloseoutRecord record, CancellationToken ct)
    {
        const string label = "Final invoice issued";
        if (record.FinalInvoiceId is not { } id || await db.Set<Invoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == id, ct) is not { } invoice)
        {
            return (false, label, "Mark the contract's final invoice.", "billing/invoices");
        }

        return invoice.IsBilled
            ? (true, label, $"Invoice {invoice.Number} ({invoice.Status}) is the final invoice.", $"billing/invoices/{invoice.Id}")
            : (false, label, $"Invoice {invoice.Number} is {invoice.Status}.", $"billing/invoices/{invoice.Id}");
    }

    private static async Task<(bool, string, string, string?)> SubcontractsAsync(IWorkbenchDbContext db, Target target, CancellationToken ct)
    {
        const string label = "Subcontracts closed";
        var projectIds = target.ProjectIds;
        var open = await db.Set<Subcontract>().AsNoTracking()
            .Where(s => s.Status != SubcontractStatus.Closed
                && ((target.ContractId != null && s.PrimeContractId == target.ContractId) || (s.ProjectId != null && projectIds.Contains(s.ProjectId.Value))))
            .Select(s => s.Number).ToListAsync(ct);
        return open.Count == 0 ? (true, label, "No open subcontracts.", "purchasing/subcontracts")
            : (false, label, $"Still open: {string.Join(", ", open.Order(StringComparer.Ordinal))}.", "purchasing/subcontracts");
    }

    private static async Task<(bool, string, string, string?)> PropertyAsync(IWorkbenchDbContext db, Target target, CancellationToken ct)
    {
        const string label = "Government property dispositioned";
        var projectIds = target.ProjectIds;
        var open = await db.Set<FixedAsset>().AsNoTracking()
            .Where(a => (a.IsGovernmentFurnished || a.IsContractorAcquired) && a.Status == AssetStatus.InService
                && ((target.ContractId != null && a.ContractId == target.ContractId) || (a.ProjectId != null && projectIds.Contains(a.ProjectId.Value))))
            .Select(a => a.TagNumber).ToListAsync(ct);
        return open.Count == 0 ? (true, label, "No government-furnished or contractor-acquired property in service.", "assets")
            : (false, label, $"Still in service: {string.Join(", ", open.Order(StringComparer.Ordinal))} (dispose or transfer them).", "assets");
    }

    private static async Task<(bool, string, string, string?)> DeobligationAsync(IWorkbenchDbContext db, Target target, CloseoutRecord record, CancellationToken ct)
    {
        const string label = "Funds de-obligation calculated";
        var (funded, billed) = await FundedAndBilledAsync(db, target, ct);
        var now = funded - billed;
        if (record.DeobligationAmount is { } recorded)
        {
            return (true, label, $"{recorded:C} recorded by {record.DeobligationRecordedBy}" + (recorded != now ? $" (now {now:C}: funded {funded:C} − billed {billed:C})" : "."), null);
        }

        return (false, label, $"Funded {funded:C} − billed {billed:C} = {now:C} to de-obligate. Record it.", null);
    }

    /// <summary>Funded: the leaf CLINs' funding (or the contract's typed-in value without CLINs). Billed: approved, sent and paid invoices (credit memos net).</summary>
    private static async Task<(decimal Funded, decimal Billed)> FundedAndBilledAsync(IWorkbenchDbContext db, Target target, CancellationToken ct)
    {
        var contract = target.Contract!;
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contract.Id).ToListAsync(ct);
        var funded = clins.Count == 0 ? contract.FundedValue : clins.Where(c => !ClinRules.HasSlins(c, clins)).Sum(c => c.FundedValue);
        var billed = (await (
                from l in db.Set<InvoiceLine>().AsNoTracking()
                join i in db.Set<Invoice>().AsNoTracking() on l.InvoiceId equals i.Id
                where i.ContractId == contract.Id && BilledStatuses.Contains(i.Status)
                select l.Amount)
            .ToListAsync(ct)).Sum();
        return (funded, billed);
    }

    private sealed class Target
    {
        public required CloseoutScope Scope { get; init; }
        public required Guid Id { get; init; }
        public Contract? Contract { get; init; }
        public Project? Project { get; init; }
        public required IReadOnlyList<Guid> ProjectIds { get; init; }

        public Guid? ContractId => Contract?.Id;
        public Guid? ProjectId => Project?.Id;
        public string Code => Contract?.ContractNumber ?? Project!.Code;
        public string Name => Contract?.Name ?? Project!.Name;
        public string Label => Contract is not null ? $"Contract {Code}" : $"Project {Code}";

        public static async Task<Target> LoadAsync(IWorkbenchDbContext db, CloseoutScope scope, Guid id, CancellationToken ct)
        {
            if (scope == CloseoutScope.Contract)
            {
                var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Contract", id);
                var projects = await db.Set<Project>().AsNoTracking().Where(p => p.ContractId == id).Select(p => p.Id).ToListAsync(ct);
                return new Target { Scope = scope, Id = id, Contract = contract, ProjectIds = projects };
            }

            if (scope != CloseoutScope.Project)
            {
                throw new ArgumentException("Unknown closeout scope.");
            }

            var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Project", id);
            return new Target { Scope = scope, Id = id, Project = project, ProjectIds = [project.Id] };
        }
    }
}
