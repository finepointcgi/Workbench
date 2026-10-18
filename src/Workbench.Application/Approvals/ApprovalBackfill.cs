using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Workbench.Application.Purchasing;
using Workbench.Contracts.Approvals;
using Workbench.Domain.Approvals;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Approvals;

/// <summary>
/// Routes every document that is waiting for approval but has no pending workflow trip (submitted before the engine
/// existed, seeded or imported), so the inbox shows it. Idempotent: a document with a pending trip is left alone. Nobody
/// is notified. Approvals already given under the old fixed rules (a PM's requisition or bill lines) are carried over.
/// Invoices and manual journal entries aren't submitted, so there's nothing to route for them.
/// </summary>
public sealed class ApprovalBackfill(
    IWorkbenchDbContextFactory dbFactory,
    ApprovalEngine engine,
    IOptions<PurchasingOptions>? purchasing = null,
    ILogger<ApprovalBackfill>? logger = null,
    ApprovalBackfillGate? gate = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<ApprovalBackfill>.Instance;
    private readonly ApprovalBackfillGate _gate = gate ?? new ApprovalBackfillGate();

    /// <summary>Routes what's missing; returns how many documents got a trip. One run at a time per tenant.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        var semaphore = _gate.For(Tenancy.TenantScope.Key);
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await RunCoreAsync(cancellationToken);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<int> RunCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var routed = await db.Set<ApprovalInstance>().AsNoTracking()
            .Where(i => i.Status == ApprovalInstanceStatus.Pending)
            .Select(i => new { i.DocumentType, i.DocumentId }).ToListAsync(cancellationToken);
        var has = routed.Select(r => (r.DocumentType, r.DocumentId)).ToHashSet();
        bool Missing(string type, Guid id) => !has.Contains((type, id));
        var count = 0;

        var sheetIds = await db.Set<Timesheet>().AsNoTracking().Where(t => t.Status == TimesheetStatus.Submitted).Select(t => t.Id).ToListAsync(cancellationToken);
        foreach (var id in sheetIds.Where(id => Missing(ApprovalDocumentTypes.Timesheet, id)))
        {
            count += await RouteAsync(ApprovalDocumentTypes.Timesheet, id, async (ctx, ct) =>
                await ApprovalSubjects.TimesheetAsync(ctx, await ctx.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).FirstAsync(t => t.Id == id, ct), ct),
                null, cancellationToken);
        }

        var reportIds = await db.Set<ExpenseReport>().AsNoTracking().Where(r => r.Status == OdcStatus.Submitted).Select(r => r.Id).ToListAsync(cancellationToken);
        foreach (var id in reportIds.Where(id => Missing(ApprovalDocumentTypes.ExpenseReport, id)))
        {
            count += await RouteAsync(ApprovalDocumentTypes.ExpenseReport, id, async (ctx, ct) =>
            {
                var report = await ctx.Set<ExpenseReport>().AsNoTracking().FirstAsync(r => r.Id == id, ct);
                var lines = await ctx.Set<OdcEntry>().AsNoTracking().Where(o => o.ExpenseReportId == id).ToListAsync(ct);
                return await ApprovalSubjects.ExpenseReportAsync(ctx, report, lines, ct);
            }, null, cancellationToken);
        }

        var odcIds = await db.Set<OdcEntry>().AsNoTracking().Where(o => o.ExpenseReportId == null && o.Status == OdcStatus.Submitted).Select(o => o.Id).ToListAsync(cancellationToken);
        foreach (var id in odcIds.Where(id => Missing(ApprovalDocumentTypes.VendorOdc, id)))
        {
            count += await RouteAsync(ApprovalDocumentTypes.VendorOdc, id, async (ctx, ct) =>
                ApprovalSubjects.VendorOdc(await ctx.Set<OdcEntry>().AsNoTracking().FirstAsync(o => o.Id == id, ct)), null, cancellationToken);
        }

        var threshold = (purchasing?.Value ?? new PurchasingOptions()).RequisitionFinanceThreshold;
        var reqIds = await db.Set<PurchaseRequisition>().AsNoTracking().Where(r => r.Status == RequisitionStatus.PendingApproval).Select(r => r.Id).ToListAsync(cancellationToken);
        foreach (var id in reqIds.Where(id => Missing(ApprovalDocumentTypes.Requisition, id)))
        {
            PurchaseRequisition? req = null;
            count += await RouteAsync(ApprovalDocumentTypes.Requisition, id, async (ctx, ct) =>
            {
                req = await ctx.Set<PurchaseRequisition>().AsNoTracking().Include(r => r.Lines).FirstAsync(r => r.Id == id, ct);
                return ApprovalSubjects.Requisition(req, threshold);
            }, task =>
            {
                // A PM who already approved their lines under the fixed rules.
                var lines = req!.Lines.Where(l => l.ProjectId is { } p && task.ScopeKey == ApprovalEngine.ProjectScope(p) && l.ApproverEmail != null).ToList();
                return lines.Count > 0 && lines.All(l => l.ApprovedBy != null) ? lines[0].ApprovedBy : null;
            }, cancellationToken);
        }

        var billIds = await db.Set<ApBill>().AsNoTracking().Where(b => b.Status == ApBillStatus.PendingApproval).Select(b => b.Id).ToListAsync(cancellationToken);
        foreach (var id in billIds.Where(id => Missing(ApprovalDocumentTypes.ApBill, id)))
        {
            ApBill? bill = null;
            count += await RouteAsync(ApprovalDocumentTypes.ApBill, id, async (ctx, ct) =>
            {
                bill = await ctx.Set<ApBill>().AsNoTracking().Include(b => b.Lines).FirstAsync(b => b.Id == id, ct);
                return await ApprovalSubjects.ApBillAsync(ctx, bill, ct);
            }, task =>
            {
                // Lines already approved under the fixed rules: a PM's project lines, or Finance's account lines.
                var lines = bill!.Lines.Where(l => task.ScopeKey is null
                    ? l.ApproverEmail is null
                    : l.ApproverEmail is not null && l.ProjectId is { } p && task.ScopeKey == ApprovalEngine.ProjectScope(p)).ToList();
                return lines.Count > 0 && lines.All(l => l.Status == ApLineStatus.Approved) ? lines[0].DecidedBy ?? "system" : null;
            }, cancellationToken);
        }

        if (count > 0)
        {
            _logger.LogInformation("Routed {Count} pending documents through their approval workflows", count);
        }

        return count;
    }

    /// <summary>Starts one document's trip in its own unit of work; a document the workflow can't route is logged and skipped.</summary>
    private async Task<int> RouteAsync(string type, Guid id, Func<IWorkbenchDbContext, CancellationToken, Task<ApprovalSubject>> subject,
        Func<ApprovalTask, string?>? priorApprover, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        if (await db.Set<ApprovalInstance>().AnyAsync(i => i.DocumentType == type && i.DocumentId == id && i.Status == ApprovalInstanceStatus.Pending, ct))
        {
            return 0; // Routed meanwhile (a decision, or another backfill).
        }

        try
        {
            var route = await engine.StartAsync(db, await subject(db, ct), ct);
            if (priorApprover is not null)
            {
                CarryOver(db, route, priorApprover);
            }

            await db.SaveChangesAsync(ct);
            return 1;
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Couldn't route {Type} {Id} through its approval workflow: {Problem}", type, id, ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// Marks tasks already approved under the old rules and moves the trip on, but never completes it: the module still
    /// shows the document as waiting, so someone must still be able to decide it.
    /// </summary>
    private void CarryOver(IWorkbenchDbContext db, ApprovalRoute route, Func<ApprovalTask, string?> priorApprover)
    {
        var open = route.Tasks.Where(t => t.Status is ApprovalTaskStatus.Pending or ApprovalTaskStatus.Waiting).ToList();
        var done = open.Select(t => (Task: t, By: priorApprover(t))).Where(x => x.By is not null).ToList();
        if (done.Count == 0 || done.Count == open.Count)
        {
            return;
        }

        foreach (var (task, by) in done)
        {
            task.Status = ApprovalTaskStatus.Approved;
            task.DecidedBy = ApprovalEngine.Normalize(by);
            task.DecidedAtUtc = engine.UtcNow;
            task.Comment = "Approved before approval workflows";
            engine.AddAction(db, route.Instance, task, "Approved", task.DecidedBy, null, task.Comment);
        }

        if (!route.Tasks.Any(t => t.Status == ApprovalTaskStatus.Pending))
        {
            var next = route.Tasks.Where(t => t.Status == ApprovalTaskStatus.Waiting).Min(t => t.Sequence);
            route.Instance.CurrentSequence = next;
            foreach (var task in route.Tasks.Where(t => t.Status == ApprovalTaskStatus.Waiting && t.Sequence == next))
            {
                task.Status = ApprovalTaskStatus.Pending;
                task.ActivatedAtUtc = engine.UtcNow;
            }
        }
    }
}

/// <summary>Serializes backfills per tenant within this server (the inbox page and the nav badge load together).</summary>
public sealed class ApprovalBackfillGate
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public SemaphoreSlim For(string tenantKey) => _gates.GetOrAdd(tenantKey, _ => new SemaphoreSlim(1, 1));
}

/// <summary>Runs <see cref="ApprovalBackfill"/> once per tenant when the server starts.</summary>
public sealed class ApprovalBackfillBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, ILogger<ApprovalBackfillBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await tenants.ForEachTenantAsync(async ct =>
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ApprovalBackfill>().RunAsync(ct);
            }, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Routing documents that were pending before approval workflows failed; the inbox routes them when it's opened");
        }
    }
}
