using Microsoft.EntityFrameworkCore;
using Workbench.Costpoint;
using Workbench.Domain.Costpoint;

namespace Workbench.Application.Costpoint;

/// <summary>
/// Pulls everything from <see cref="ICostpointSource"/> and makes the <c>Cp*</c> mirror tables match it:
/// new rows are inserted, changed rows updated, rows the source no longer returns deleted. Running it twice
/// against the same data changes nothing. It only reads from Costpoint; nothing is ever written back.
/// Each call records a <see cref="CpSyncRun"/>. The mirror is replaced in one transaction, so a failed
/// sync leaves the previous data in place.
/// </summary>
public sealed class CostpointSyncService(IWorkbenchDbContextFactory dbFactory, ICostpointSource source, TimeProvider clock)
{
    /// <summary>First fiscal period of actuals requested. Pre-contract costs can precede a project's start date.</summary>
    public const string ActualsFromPeriod = "2000-01";

    // One sync at a time per process; a second caller waits and then runs its own pass.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string SourceName => source.Name;

    public bool Configured => source is not NullCostpointSource;

    public async Task<CpSyncRun> RunAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var run = new CpSyncRun { Source = source.Name, StartedAtUtc = Now(), Status = CpSyncStatus.Running };
            await using (var db = await dbFactory.CreateAsync(cancellationToken))
            {
                db.Set<CpSyncRun>().Add(run);
                await db.SaveChangesAsync(cancellationToken);
            }

            try
            {
                await PullAndUpsertAsync(run, cancellationToken);
                run.Status = CpSyncStatus.Succeeded;
            }
            catch (Exception ex)
            {
                run.Status = CpSyncStatus.Failed;
                run.Error = Truncate($"{ex.GetType().Name}: {ex.Message}");
                if (ex is OperationCanceledException)
                {
                    await FinishAsync(run, CancellationToken.None);
                    throw;
                }
            }

            if (source is ICostpointSourceDiagnostics diagnostics)
            {
                var warnings = diagnostics.TakeWarnings();
                run.Skipped = warnings.Count;
                if (warnings.Count > 0 && run.Error is null)
                {
                    run.Error = Truncate($"Skipped {warnings.Count} invalid row(s): " + string.Join(" | ", warnings.Take(10)));
                }
            }

            await FinishAsync(run, CancellationToken.None);
            return run;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PullAndUpsertAsync(CpSyncRun run, CancellationToken cancellationToken)
    {
        var projects = DistinctBy(await source.GetProjectsAsync(cancellationToken), p => p.ProjectId);
        var laborCategories = DistinctBy(await source.GetLaborCategoriesAsync(cancellationToken), l => l.LaborCategoryId);
        var employees = DistinctBy(await source.GetEmployeesAsync(cancellationToken), e => e.EmployeeId);

        var toPeriod = Now().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        var wbs = new List<CpWbsRecord>();
        var budgets = new List<CpBudgetRecord>();
        var actuals = new List<CpActualRecord>();
        foreach (var project in projects)
        {
            wbs.AddRange(await source.GetWbsAsync(project.ProjectId, cancellationToken));
            budgets.AddRange(await source.GetBudgetsAsync(project.ProjectId, cancellationToken));
            actuals.AddRange(await source.GetActualsAsync(project.ProjectId, ActualsFromPeriod, toPeriod, cancellationToken));
        }

        // Several rows for the same key (e.g. a per-employee export) are summed.
        var budgetTotals = budgets
            .GroupBy(b => (b.ProjectId, WbsId: b.WbsId ?? "", Period: b.FiscalPeriod ?? CpBudget.TotalPeriod))
            .Select(g => (g.Key, Hours: g.Sum(b => b.Hours), Amount: g.Sum(b => b.Amount)))
            .ToList();
        var actualTotals = actuals
            .GroupBy(a => (a.ProjectId, a.WbsId, a.FiscalPeriod))
            .Select(g => (g.Key, Hours: g.Sum(a => a.Hours), Cost: g.Sum(a => a.Cost)))
            .ToList();
        var wbsDistinct = DistinctBy(wbs, w => Key(w.ProjectId, w.WbsId));

        run.Projects = projects.Count;
        run.LaborCategories = laborCategories.Count;
        run.Employees = employees.Count;
        run.WbsElements = wbsDistinct.Count;
        run.Budgets = budgetTotals.Count;
        run.Actuals = actualTotals.Count;

        var now = Now();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var counts = new Counts();

        await UpsertAsync(db, counts, projects, p => p.ProjectId, p => p.ProjectId,
            p => new CpProject { ProjectId = p.ProjectId, Name = p.Name },
            (e, p) => Apply(e, p, now), cancellationToken);
        await UpsertAsync(db, counts, wbsDistinct, w => Key(w.ProjectId, w.WbsId), w => Key(w.ProjectId, w.WbsId),
            w => new CpWbsElement { ProjectId = w.ProjectId, WbsId = w.WbsId, Name = w.Name },
            (e, w) => Apply(e, w, now), cancellationToken);
        await UpsertAsync(db, counts, laborCategories, l => l.LaborCategoryId, l => l.LaborCategoryId,
            l => new CpLaborCategory { LaborCategoryId = l.LaborCategoryId, Name = l.Name },
            (e, l) => Apply(e, l, now), cancellationToken);
        await UpsertAsync(db, counts, employees, e => e.EmployeeId, e => e.EmployeeId,
            e => new CpEmployee { EmployeeId = e.EmployeeId, Name = e.Name },
            (e, r) => Apply(e, r, now), cancellationToken);
        await UpsertAsync(db, counts, budgetTotals, b => Key(b.ProjectId, b.WbsId, b.FiscalPeriod), b => Key(b.Key.ProjectId, b.Key.WbsId, b.Key.Period),
            b => new CpBudget { ProjectId = b.Key.ProjectId, WbsId = b.Key.WbsId, FiscalPeriod = b.Key.Period },
            (e, b) => Apply(e, b.Hours, b.Amount, now), cancellationToken);
        await UpsertAsync(db, counts, actualTotals, a => Key(a.ProjectId, a.WbsId, a.FiscalPeriod), a => Key(a.Key.ProjectId, a.Key.WbsId, a.Key.FiscalPeriod),
            a => new CpActual { ProjectId = a.Key.ProjectId, WbsId = a.Key.WbsId, FiscalPeriod = a.Key.FiscalPeriod },
            (e, a) => Apply(e, a.Hours, a.Cost, now), cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        run.Inserted = counts.Inserted;
        run.Updated = counts.Updated;
        run.Deleted = counts.Deleted;
    }

    private static async Task UpsertAsync<TEntity, TRecord>(
        IWorkbenchDbContext db,
        Counts counts,
        IReadOnlyList<TRecord> incoming,
        Func<TEntity, string> entityKey,
        Func<TRecord, string> recordKey,
        Func<TRecord, TEntity> create,
        Func<TEntity, TRecord, bool> apply,
        CancellationToken cancellationToken)
        where TEntity : class
    {
        var set = db.Set<TEntity>();
        var existing = (await set.ToListAsync(cancellationToken)).ToDictionary(entityKey, StringComparer.Ordinal);
        foreach (var record in incoming)
        {
            if (existing.Remove(recordKey(record), out var entity))
            {
                if (apply(entity, record))
                {
                    counts.Updated++;
                }
            }
            else
            {
                entity = create(record);
                apply(entity, record);
                set.Add(entity);
                counts.Inserted++;
            }
        }

        set.RemoveRange(existing.Values);
        counts.Deleted += existing.Count;
    }

    private static bool Apply(CpProject e, CpProjectRecord r, DateTime now) =>
        Touch(e.Name != r.Name || e.OrganizationId != r.OrganizationId || e.Start != r.Start || e.End != r.End || e.Active != r.Active || e.SyncedAtUtc == default, () =>
        {
            e.Name = r.Name;
            e.OrganizationId = r.OrganizationId;
            e.Start = r.Start;
            e.End = r.End;
            e.Active = r.Active;
            e.SyncedAtUtc = now;
        });

    private static bool Apply(CpWbsElement e, CpWbsRecord r, DateTime now) =>
        Touch(e.Name != r.Name || e.ParentWbsId != r.ParentWbsId || e.Level != r.Level || e.ChargeAllowed != r.ChargeAllowed || e.SyncedAtUtc == default, () =>
        {
            e.Name = r.Name;
            e.ParentWbsId = r.ParentWbsId;
            e.Level = r.Level;
            e.ChargeAllowed = r.ChargeAllowed;
            e.SyncedAtUtc = now;
        });

    private static bool Apply(CpLaborCategory e, CpLaborCategoryRecord r, DateTime now) =>
        Touch(e.Name != r.Name || e.SyncedAtUtc == default, () =>
        {
            e.Name = r.Name;
            e.SyncedAtUtc = now;
        });

    private static bool Apply(CpEmployee e, CpEmployeeRecord r, DateTime now) =>
        Touch(e.Name != r.Name || e.Email != r.Email || e.OrganizationId != r.OrganizationId || e.LaborCategoryId != r.LaborCategoryId || e.Active != r.Active || e.SyncedAtUtc == default, () =>
        {
            e.Name = r.Name;
            e.Email = r.Email;
            e.OrganizationId = r.OrganizationId;
            e.LaborCategoryId = r.LaborCategoryId;
            e.Active = r.Active;
            e.SyncedAtUtc = now;
        });

    private static bool Apply(CpBudget e, decimal hours, decimal amount, DateTime now) =>
        Touch(e.Hours != hours || e.Amount != amount || e.SyncedAtUtc == default, () =>
        {
            e.Hours = hours;
            e.Amount = amount;
            e.SyncedAtUtc = now;
        });

    private static bool Apply(CpActual e, decimal hours, decimal cost, DateTime now) =>
        Touch(e.Hours != hours || e.Cost != cost || e.SyncedAtUtc == default, () =>
        {
            e.Hours = hours;
            e.Cost = cost;
            e.SyncedAtUtc = now;
        });

    private static bool Touch(bool changed, Action update)
    {
        if (changed)
        {
            update();
        }

        return changed;
    }

    private async Task FinishAsync(CpSyncRun run, CancellationToken cancellationToken)
    {
        run.FinishedAtUtc = Now();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        db.Set<CpSyncRun>().Update(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static List<T> DistinctBy<T>(IEnumerable<T> records, Func<T, string> key) =>
        records.GroupBy(key, StringComparer.Ordinal).Select(g => g.Last()).ToList();

    private static string Key(params string[] parts) => string.Join('\u001f', parts);

    private static string? Truncate(string? value) => value is { Length: > 4000 } ? value[..4000] : value;

    private DateTime Now() => clock.GetUtcNow().UtcDateTime;

    private sealed class Counts
    {
        public int Inserted { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
    }
}
