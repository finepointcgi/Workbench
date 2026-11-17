using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// The billing model of commercial agreements (contracts without CLINs): the plan, fixed-fee milestones, and the retainer and
/// recurring periods that are due. It never writes invoices itself: drafts come from <see cref="InvoiceService"/>, so
/// every invoice goes through the same approval, GL posting and aging as any other.
/// </summary>
public sealed class AgreementBillingService(
    IWorkbenchDbContextFactory dbFactory, InvoiceService invoices, AuditRecorder audit, TimeProvider clock,
    INotificationPublisher? notifications = null) : IAgreementBillingService
{
    public const string DraftsKind = "invoice.drafts_ready";

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<AgreementBillingPlanDto?> GetPlanAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await db.Set<AgreementBillingPlan>().AsNoTracking().FirstOrDefaultAsync(p => p.ContractId == contractId, cancellationToken);
        return plan is null ? null : (await DtosAsync(db, [plan], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<AgreementBillingPlanDto>> ListPlansAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DtosAsync(db, await db.Set<AgreementBillingPlan>().AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<AgreementBillingPlanDto> SavePlanAsync(Guid contractId, SaveAgreementBillingPlanRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Contract", contractId);
        if (await db.Set<Clin>().AnyAsync(c => c.ContractId == contractId, cancellationToken))
        {
            throw new ArgumentException($"Contract {contract.ContractNumber} has CLINs, so it bills the government way. A commercial billing model is for agreements without CLINs.");
        }

        var model = request.Model.ToDomain();
        var description = InvoiceService.Clean(request.Description, 200);
        decimal? included = null;
        decimal? overage = null;
        var policy = UnusedHoursPolicy.Off;
        if (model is AgreementBillingModel.Retainer or AgreementBillingModel.Recurring)
        {
            if (request.StartDate is null)
            {
                throw new ArgumentException("Choose the date the first period starts.");
            }

            if (request.Amount <= 0)
            {
                throw new ArgumentException("The amount per period must be positive.");
            }

            if (request.EndDate is { } end && end < request.StartDate)
            {
                throw new ArgumentException("The end date is before the start date.");
            }

            if (model == AgreementBillingModel.Retainer)
            {
                policy = request.UnusedHours.ToDomain();
                if (policy != UnusedHoursPolicy.Off)
                {
                    if (request.IncludedHours is not > 0)
                    {
                        throw new ArgumentException("Enter the hours included in each period to track unused hours.");
                    }

                    included = request.IncludedHours;
                    overage = request.OverageRate is > 0 ? request.OverageRate : null;
                }
            }
        }

        var plan = await db.Set<AgreementBillingPlan>().FirstOrDefaultAsync(p => p.ContractId == contractId, cancellationToken);
        var live = await db.Set<Invoice>().AsNoTracking().Where(i => i.ContractId == contractId && i.Status != InvoiceStatus.Void).ToListAsync(cancellationToken);
        if (plan is not null)
        {
            if (plan.Model != model && live.Count > 0)
            {
                throw new ArgumentException("This agreement already has invoices, so its billing model can't change. Void them first, or set up a new agreement.");
            }

            if ((plan.StartDate != request.StartDate || plan.Cadence != request.Cadence.ToDomain())
                && live.Any(i => i.Type is InvoiceType.Retainer or InvoiceType.Recurring))
            {
                throw new ArgumentException("Periods are already billed, so the start date and cadence can't change. The amount and end date can.");
            }
        }

        var before = plan is null ? null : new { plan.Model, plan.Cadence, plan.StartDate, plan.EndDate, plan.Amount };
        if (plan is null)
        {
            plan = new AgreementBillingPlan { ContractId = contractId };
            db.Set<AgreementBillingPlan>().Add(plan);
        }

        plan.Model = model;
        plan.Cadence = request.Cadence.ToDomain();
        plan.StartDate = request.StartDate ?? default;
        plan.EndDate = request.EndDate;
        plan.Amount = model is AgreementBillingModel.Retainer or AgreementBillingModel.Recurring ? request.Amount : 0m;
        plan.Description = description;
        plan.InvoiceOnComplete = request.InvoiceOnComplete;
        plan.IncludedHours = included;
        plan.UnusedHours = policy;
        plan.OverageRate = overage;
        plan.AutoGenerate = request.AutoGenerate;
        plan.UpdatedBy = audit.Actor;
        plan.UpdatedAtUtc = UtcNow;
        audit.Record(db, nameof(AgreementBillingPlan), contractId, before is null ? "Created" : "Updated", before,
            new { plan.Model, plan.Cadence, plan.StartDate, plan.EndDate, plan.Amount });
        await db.SaveChangesAsync(cancellationToken);
        return (await DtosAsync(db, [plan], cancellationToken))[0];
    }

    public async Task RemovePlanAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var plan = await db.Set<AgreementBillingPlan>().FirstOrDefaultAsync(p => p.ContractId == contractId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Billing plan", contractId);
        if (await db.Set<Invoice>().AnyAsync(i => i.ContractId == contractId && i.Status != InvoiceStatus.Void, cancellationToken))
        {
            throw new InvalidOperationException("This agreement has invoices, so its billing plan can't be removed.");
        }

        db.Set<AgreementMilestone>().RemoveRange(await db.Set<AgreementMilestone>().Where(m => m.ContractId == contractId).ToListAsync(cancellationToken));
        db.Set<AgreementBillingPlan>().Remove(plan);
        audit.Record(db, nameof(AgreementBillingPlan), contractId, "Removed", before: new { plan.Model });
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Milestones ----

    public async Task<IReadOnlyList<AgreementMilestoneDto>> ListMilestonesAsync(Guid contractId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        return await MilestoneDtosAsync(db, await db.Set<AgreementMilestone>().AsNoTracking().Where(m => m.ContractId == contractId).ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<AgreementMilestoneDto> CreateMilestoneAsync(Guid contractId, SaveAgreementMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await RequireFixedFeeAsync(db, contractId, cancellationToken);
        Validate(request);
        var order = await db.Set<AgreementMilestone>().Where(m => m.ContractId == contractId).MaxAsync(m => (int?)m.Order, cancellationToken) ?? -1;
        var milestone = new AgreementMilestone
        {
            ContractId = contractId,
            Description = request.Description.Trim(),
            Amount = request.Amount,
            TargetDate = request.TargetDate,
            Order = order + 1,
        };
        db.Set<AgreementMilestone>().Add(milestone);
        audit.Record(db, nameof(AgreementMilestone), milestone.Id, "Created", after: new { milestone.Description, milestone.Amount, milestone.TargetDate });
        await db.SaveChangesAsync(cancellationToken);
        return (await MilestoneDtosAsync(db, [milestone], cancellationToken))[0];
    }

    public async Task<AgreementMilestoneDto> UpdateMilestoneAsync(Guid milestoneId, SaveAgreementMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadUnbilledAsync(db, milestoneId, cancellationToken);
        Validate(request);
        var before = new { milestone.Description, milestone.Amount, milestone.TargetDate };
        milestone.Description = request.Description.Trim();
        milestone.Amount = request.Amount;
        milestone.TargetDate = request.TargetDate;
        audit.Record(db, nameof(AgreementMilestone), milestone.Id, "Updated", before, new { milestone.Description, milestone.Amount, milestone.TargetDate });
        await db.SaveChangesAsync(cancellationToken);
        return (await MilestoneDtosAsync(db, [milestone], cancellationToken))[0];
    }

    public async Task<AgreementMilestoneCompletionDto> CompleteMilestoneAsync(Guid milestoneId, CompleteAgreementMilestoneRequest request, CancellationToken cancellationToken = default)
    {
        Guid contractId;
        bool draft;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var milestone = await LoadUnbilledAsync(db, milestoneId, cancellationToken);
            if (milestone.CompletedDate is not null)
            {
                throw new InvalidOperationException("This milestone is already complete.");
            }

            milestone.CompletedDate = request.CompletedDate;
            milestone.CompletedBy = audit.Actor;
            contractId = milestone.ContractId;
            draft = request.CreateDraftInvoice
                ?? (await db.Set<AgreementBillingPlan>().AsNoTracking().Where(p => p.ContractId == contractId).Select(p => (bool?)p.InvoiceOnComplete).FirstOrDefaultAsync(cancellationToken) ?? false);
            audit.Record(db, nameof(AgreementMilestone), milestone.Id, "Completed", after: new { request.CompletedDate });
            await db.SaveChangesAsync(cancellationToken);
        }

        InvoiceDto? created = null;
        if (draft)
        {
            // The completion is saved; a draft that can't be made (say the period is closed) leaves the milestone billable from Billing.
            try
            {
                created = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(contractId, InvoiceKind.FixedPriceMilestone, request.CompletedDate, request.CompletedDate), cancellationToken);
            }
            catch (ArgumentException)
            {
            }
        }

        await using var read = await dbFactory.CreateAsync(cancellationToken);
        var saved = await read.Set<AgreementMilestone>().AsNoTracking().FirstAsync(m => m.Id == milestoneId, cancellationToken);
        return new AgreementMilestoneCompletionDto((await MilestoneDtosAsync(read, [saved], cancellationToken))[0], created);
    }

    public async Task<AgreementMilestoneDto> ReopenMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadUnbilledAsync(db, milestoneId, cancellationToken);
        var before = new { milestone.CompletedDate };
        milestone.CompletedDate = null;
        milestone.CompletedBy = null;
        audit.Record(db, nameof(AgreementMilestone), milestone.Id, "Reopened", before);
        await db.SaveChangesAsync(cancellationToken);
        return (await MilestoneDtosAsync(db, [milestone], cancellationToken))[0];
    }

    public async Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var milestone = await LoadUnbilledAsync(db, milestoneId, cancellationToken);
        db.Set<AgreementMilestone>().Remove(milestone);
        audit.Record(db, nameof(AgreementMilestone), milestone.Id, "Deleted", before: new { milestone.Description, milestone.Amount });
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Retainers and recurring charges ----

    public async Task<IReadOnlyList<DueAgreementPeriodDto>> ListDuePeriodsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await DueAsync(db, asOf ?? Today, null, autoOnly: false, cancellationToken)).Select(d => d.Dto).ToList();
    }

    public async Task<GenerateDueInvoicesResultDto> GenerateDueDraftsAsync(DateOnly? asOf = null, Guid? contractId = null, CancellationToken cancellationToken = default) =>
        await GenerateAsync(asOf ?? Today, contractId, autoOnly: false, cancellationToken);

    /// <summary>The daily run: drafts for periods that have started, on plans that generate automatically. Finance is told, nothing is approved.</summary>
    public async Task<int> RunAutoAsync(CancellationToken cancellationToken = default)
    {
        var result = await GenerateAsync(Today, null, autoOnly: true, cancellationToken);
        if (result.Created > 0 && notifications is not null)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var finance = await Identity.RoleStore.EmailsWithPermissionAsync(db, Permission.BillingInvoicesManage, cancellationToken);
            if (finance.Count > 0)
            {
                await notifications.PublishAsync(finance, DraftsKind,
                    $"{result.Created} recurring invoice draft{(result.Created == 1 ? "" : "s")} ready to review",
                    string.Join(", ", result.InvoiceNumbers.Take(8)) + (result.InvoiceNumbers.Count > 8 ? ", ..." : ""),
                    "billing/invoices");
            }
        }

        return result.Created;
    }

    private async Task<GenerateDueInvoicesResultDto> GenerateAsync(DateOnly asOf, Guid? contractId, bool autoOnly, CancellationToken ct)
    {
        List<(DueAgreementPeriodDto Dto, bool Skip)> due;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            due = await DueAsync(db, asOf, contractId, autoOnly, ct);
        }

        var numbers = new List<string>();
        var skipped = new List<string>();
        foreach (var (period, skip) in due)
        {
            if (skip)
            {
                continue;
            }

            try
            {
                var invoice = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(period.ContractId, period.Type, period.PeriodFrom, period.PeriodTo), ct);
                numbers.Add(invoice.Summary.Number);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                skipped.Add($"{period.ContractNumber} {AgreementPeriods.Describe(period.PeriodFrom, period.PeriodTo)}: {ex.Message}");
            }
        }

        return new GenerateDueInvoicesResultDto(numbers.Count, numbers, skipped);
    }

    private async Task<List<(DueAgreementPeriodDto Dto, bool Skip)>> DueAsync(IWorkbenchDbContext db, DateOnly asOf, Guid? contractId, bool autoOnly, CancellationToken ct)
    {
        var plans = await db.Set<AgreementBillingPlan>().AsNoTracking()
            .Where(p => (p.Model == AgreementBillingModel.Retainer || p.Model == AgreementBillingModel.Recurring) && (contractId == null || p.ContractId == contractId))
            .ToListAsync(ct);
        if (autoOnly)
        {
            plans = plans.Where(p => p.AutoGenerate).ToList();
        }

        var ids = plans.Select(p => p.ContractId).ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var existing = (await db.Set<Invoice>().AsNoTracking().Where(i => ids.Contains(i.ContractId) && i.Status != InvoiceStatus.Void).ToListAsync(ct))
            .Where(i => i.Type is InvoiceType.Retainer or InvoiceType.Recurring).ToList();
        var result = new List<(DueAgreementPeriodDto, bool)>();
        foreach (var plan in plans.OrderBy(p => contracts[p.ContractId].ContractNumber, StringComparer.Ordinal))
        {
            var contract = contracts[plan.ContractId];
            if (contract.Status == ContractStatus.Closed)
            {
                continue;
            }

            var type = plan.Model == AgreementBillingModel.Retainer ? InvoiceType.Retainer : InvoiceType.Recurring;
            foreach (var (index, from, to) in AgreementPeriods.Started(plan, asOf))
            {
                var mine = existing.Where(i => i.ContractId == plan.ContractId && i.Type == type && i.PeriodFrom == from).ToList();
                if (mine.Any(i => i.Status != InvoiceStatus.Draft))
                {
                    continue;
                }

                var hasDraft = mine.Count > 0;
                result.Add((new DueAgreementPeriodDto(plan.ContractId, contract.ContractNumber, customers.GetValueOrDefault(contract.CustomerId) ?? "", type.ToContract(),
                    from, to, AgreementPeriods.AmountFor(plan, index), plan.Description, hasDraft), hasDraft));
            }
        }

        return result;
    }

    // ---- helpers ----

    private static void Validate(SaveAgreementMilestoneRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Trim().Length > 500)
        {
            throw new ArgumentException("A milestone needs a description of up to 500 characters.");
        }

        if (request.Amount <= 0)
        {
            throw new ArgumentException("A milestone's amount must be positive.");
        }
    }

    private static async Task RequireFixedFeeAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct)
    {
        if (!await db.Set<Contract>().AnyAsync(c => c.Id == contractId, ct))
        {
            throw WorkbenchNotFoundException.For("Contract", contractId);
        }

        if (!await db.Set<AgreementBillingPlan>().AnyAsync(p => p.ContractId == contractId && p.Model == AgreementBillingModel.FixedFeeMilestones, ct))
        {
            throw new ArgumentException("Milestones belong to an agreement billed as a fixed fee with milestones. Set its billing model first.");
        }
    }

    private static async Task<AgreementMilestone> LoadUnbilledAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var milestone = await db.Set<AgreementMilestone>().FirstOrDefaultAsync(m => m.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Milestone", id);
        return milestone.InvoiceId is null
            ? milestone
            : throw new InvalidOperationException("This milestone is on an invoice. Void or delete that invoice first.");
    }

    private static async Task<List<AgreementMilestoneDto>> MilestoneDtosAsync(IWorkbenchDbContext db, IReadOnlyList<AgreementMilestone> milestones, CancellationToken ct)
    {
        var invoiceIds = milestones.Where(m => m.InvoiceId is not null).Select(m => m.InvoiceId!.Value).Distinct().ToList();
        var numbers = await db.Set<Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        return milestones.OrderBy(m => m.Order).ThenBy(m => m.TargetDate)
            .Select(m => new AgreementMilestoneDto(m.Id, m.ContractId, m.Description, m.Amount, m.TargetDate, m.CompletedDate, m.CompletedBy,
                m.InvoiceId, m.InvoiceId is { } i ? numbers.GetValueOrDefault(i) : null, m.Order))
            .ToList();
    }

    private async Task<List<AgreementBillingPlanDto>> DtosAsync(IWorkbenchDbContext db, IReadOnlyList<AgreementBillingPlan> plans, CancellationToken ct)
    {
        var ids = plans.Select(p => p.ContractId).ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var customers = await db.Set<Customer>().AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var invoiceRows = (await db.Set<Invoice>().AsNoTracking().Where(i => ids.Contains(i.ContractId) && i.Status != InvoiceStatus.Void).ToListAsync(ct))
            .Where(i => i.Type != InvoiceType.CreditMemo).ToList();
        return plans.OrderBy(p => contracts[p.ContractId].ContractNumber, StringComparer.Ordinal).Select(plan =>
        {
            var contract = contracts[plan.ContractId];
            var mine = invoiceRows.Where(i => i.ContractId == plan.ContractId).ToList();
            var type = plan.Model == AgreementBillingModel.Retainer ? InvoiceType.Retainer : InvoiceType.Recurring;
            var periodic = plan.Model is AgreementBillingModel.Retainer or AgreementBillingModel.Recurring;
            var billedPeriods = mine.Where(i => i.Type == type).Select(i => i.PeriodFrom).ToHashSet();
            DateOnly? nextFrom = null;
            DateOnly? nextTo = null;
            if (periodic)
            {
                foreach (var (_, from, to) in AgreementPeriods.Started(plan, plan.EndDate ?? Today.AddYears(10)))
                {
                    if (!billedPeriods.Contains(from))
                    {
                        (nextFrom, nextTo) = (from, to);
                        break;
                    }
                }
            }

            return new AgreementBillingPlanDto(plan.ContractId, contract.ContractNumber, contract.Name, customers.GetValueOrDefault(contract.CustomerId) ?? "",
                plan.Model.ToContract(), plan.Cadence.ToContract(), plan.StartDate, plan.EndDate, plan.Amount, plan.Description, plan.InvoiceOnComplete,
                plan.IncludedHours, plan.UnusedHours.ToContract(), plan.OverageRate, plan.AutoGenerate, nextFrom, nextTo,
                periodic ? billedPeriods.Count : mine.Count(i => i.Status != InvoiceStatus.Draft),
                mine.Where(i => i.IsBilled).Sum(i => i.Total), Summary(plan));
        }).ToList();
    }

    public static string Summary(AgreementBillingPlan plan) => plan.Model switch
    {
        AgreementBillingModel.TimeAndMaterials => "Time and materials: approved hours at the contract's rate card, plus expenses at cost.",
        AgreementBillingModel.FixedFeeMilestones => "Fixed fee: each milestone bills its amount when it's marked complete.",
        AgreementBillingModel.Retainer => $"Retainer: {plan.Amount:C} each {AgreementPeriods.Label(plan.Cadence)} from {plan.StartDate:yyyy-MM-dd}"
            + (plan.EndDate is { } end ? $" to {end:yyyy-MM-dd}" : "")
            + (plan.UnusedHours != UnusedHoursPolicy.Off && plan.IncludedHours is { } h ? $", {h:0.##} hours included ({plan.UnusedHours.ToString().ToLowerInvariant()})" : ""),
        _ => $"Recurring: {plan.Amount:C} each {AgreementPeriods.Label(plan.Cadence)} from {plan.StartDate:yyyy-MM-dd}" + (plan.EndDate is { } e ? $" to {e:yyyy-MM-dd}" : ""),
    };
}

internal static class AgreementMapping
{
    public static AgreementBillingModel ToDomain(this AgreementModel m) => (AgreementBillingModel)(int)m;

    public static AgreementModel ToContract(this AgreementBillingModel m) => (AgreementModel)(int)m;

    public static BillingCadence ToDomain(this BillingCadenceKind c) => (BillingCadence)(int)c;

    public static BillingCadenceKind ToContract(this BillingCadence c) => (BillingCadenceKind)(int)c;

    public static UnusedHoursPolicy ToDomain(this UnusedHoursKind u) => (UnusedHoursPolicy)(int)u;

    public static UnusedHoursKind ToContract(this UnusedHoursPolicy u) => (UnusedHoursKind)(int)u;

    public static InvoiceLayout ToDomain(this InvoiceLayoutKind l) => (InvoiceLayout)(int)l;

    public static InvoiceLayoutKind ToContract(this InvoiceLayout l) => (InvoiceLayoutKind)(int)l;
}

/// <summary>Creates the draft invoices of recurring plans each day; the run is idempotent (a period with an invoice isn't repeated).</summary>
public sealed class AgreementBillingBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<AgreementBillingBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var created = await scope.ServiceProvider.GetRequiredService<AgreementBillingService>().RunAutoAsync(ct);
                    if (created > 0)
                    {
                        logger.LogInformation("Created {Count} recurring invoice drafts", created);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recurring billing run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
