using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Approvals;
using Workbench.Application.Time;
using Workbench.Contracts.Automations;
using Workbench.Domain.Approvals;
using Workbench.Domain.Automations;
using Workbench.Domain.Integrations;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Automations;

/// <summary>
/// Runs what's waiting: reads new integration events into the queue (for automations triggered by them), then runs every
/// queued item, oldest first, each in its own scope. Items queued while it runs (chains) are run too, up to a cap.
/// Each item is claimed first (a concurrency token), so several hosts can process the queue.
/// </summary>
public sealed class AutomationProcessor(IWorkbenchDbContextFactory dbFactory, IServiceScopeFactory scopes, TimeProvider clock, ILogger<AutomationProcessor> logger)
{
    public const int MaxPerPass = 500;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await PumpEventsAsync(ct);
        var processed = 0;
        while (processed < MaxPerPass)
        {
            List<Guid> ids;
            await using (var db = await dbFactory.CreateAsync(ct))
            {
                ids = await db.Set<AutomationQueueItem>().AsNoTracking().Where(q => q.ProcessedAtUtc == null)
                    .OrderBy(q => q.CreatedAtUtc).Select(q => q.Id).Take(50).ToListAsync(ct);
            }

            if (ids.Count == 0)
            {
                break;
            }

            foreach (var id in ids)
            {
                if (await ClaimAsync(id, ct) is { } item)
                {
                    await RunAsync(item, ct);
                }

                processed++;
            }
        }

        return processed;
    }

    private async Task<AutomationQueueItem?> ClaimAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var item = await db.Set<AutomationQueueItem>().FirstOrDefaultAsync(q => q.Id == id && q.ProcessedAtUtc == null, ct);
        if (item is null)
        {
            return null;
        }

        item.ProcessedAtUtc = clock.GetUtcNow().UtcDateTime;
        item.Attempts++;
        try
        {
            await db.SaveChangesAsync(ct);
            return item;
        }
        catch (DbUpdateConcurrencyException)
        {
            return null; // Another host took it.
        }
    }

    private async Task RunAsync(AutomationQueueItem item, CancellationToken ct)
    {
        try
        {
            string? payload = null;
            if (item.SourceEventId is { } eventId)
            {
                await using var db = await dbFactory.CreateAsync(ct);
                payload = await db.Set<IntegrationOutboxEvent>().AsNoTracking().Where(e => e.Id == eventId).Select(e => e.PayloadJson).FirstOrDefaultAsync(ct);
            }

            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<AutomationExecutor>().RunAsync(new AutomationInvocation(
                item.AutomationId, Enum.Parse<AutomationTriggerKind>(item.TriggerKind), item.TriggerDetail, item.EntityType, item.RecordId, item.Actor,
                item.ChangesJson, item.Depth, item.ParentRunId, PrincipalOverride: item.PrincipalOverride, PayloadJson: payload), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Automation queue item {ItemId} failed", item.Id);
        }
    }

    /// <summary>Queues runs for integration events since the last pass (with a two-minute overlap; the dedup key absorbs repeats).</summary>
    public async Task<int> PumpEventsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var settings = await AutomationSettingsStore.GetOrCreateAsync(db, ct);
        var kind = nameof(AutomationTriggerKind.IntegrationEvent);
        var listeners = await db.Set<AutomationRecord>().AsNoTracking()
            .Where(a => a.IsEnabled && a.DeletedAtUtc == null && a.TriggerKind == kind && a.TriggerKeys != null)
            .Select(a => new { a.Id, a.TriggerKeys }).ToListAsync(ct);
        if (listeners.Count == 0 || settings.EventCursorUtc is null)
        {
            settings.EventCursorUtc = now;
            await db.SaveChangesAsync(ct);
            return 0;
        }

        var keys = listeners.Select(l => l.TriggerKeys!).Distinct().ToList();
        var from = settings.EventCursorUtc.Value.AddMinutes(-2);
        var events = await db.Set<IntegrationOutboxEvent>().AsNoTracking()
            .Where(e => e.OccurredAtUtc > from && keys.Contains(e.EventType))
            .OrderBy(e => e.OccurredAtUtc).Take(500).ToListAsync(ct);
        var dedup = events.SelectMany(e => listeners.Where(l => l.TriggerKeys == e.EventType).Select(l => $"event:{e.Id:N}:{l.Id:N}")).ToList();
        var existing = (await db.Set<AutomationQueueItem>().AsNoTracking().Where(q => dedup.Contains(q.DedupKey)).Select(q => q.DedupKey).ToListAsync(ct)).ToHashSet();
        var queued = 0;
        foreach (var e in events)
        {
            foreach (var listener in listeners.Where(l => l.TriggerKeys == e.EventType))
            {
                var key = $"event:{e.Id:N}:{listener.Id:N}";
                if (!existing.Add(key))
                {
                    continue;
                }

                db.Set<AutomationQueueItem>().Add(new AutomationQueueItem
                {
                    AutomationId = listener.Id,
                    TriggerKind = kind,
                    TriggerDetail = e.EventType,
                    EntityType = AutomationEntities.ForSubject(e.Subject)?.Key,
                    RecordId = AutomationEntities.ForSubject(e.Subject) is null ? null : e.SubjectId,
                    Actor = e.Actor,
                    SourceEventId = e.Id,
                    DedupKey = key,
                    CreatedAtUtc = now,
                });
                queued++;
            }
        }

        settings.EventCursorUtc = events.Count == 500 ? events[^1].OccurredAtUtc : now;
        await db.SaveChangesAsync(ct);
        return queued;
    }

    /// <summary>Removes processed queue items after 30 days and run log rows after a year (no-match runs after 30 days).</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(ct);
        var month = now.AddDays(-30);
        var year = now.AddDays(-365);
        var noMatch = nameof(AutomationRunStatus.ConditionsNotMet);
        db.Set<AutomationQueueItem>().RemoveRange(await db.Set<AutomationQueueItem>().Where(q => q.ProcessedAtUtc != null && q.ProcessedAtUtc < month).Take(500).ToListAsync(ct));
        db.Set<AutomationRun>().RemoveRange(await db.Set<AutomationRun>()
            .Where(r => r.StartedAtUtc < year || (r.Status == noMatch && r.StartedAtUtc < month)).Take(500).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
    }
}

public static class AutomationSettingsStore
{
    public static async Task<AutomationSettings> GetOrCreateAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var settings = db.Set<AutomationSettings>().Local.FirstOrDefault() ?? await db.Set<AutomationSettings>().FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new AutomationSettings();
            db.Set<AutomationSettings>().Add(settings);
        }

        return settings;
    }
}

/// <summary>
/// Schedules, date fields and thresholds, checked by the background service every few minutes. Each firing is queued once:
/// schedules and date fields by a dedup key per day and record; thresholds fire when a record crosses and re-arm when it's back
/// (an <see cref="AutomationTriggerState"/> per automation and record).
/// </summary>
public sealed class AutomationScheduler(IWorkbenchDbContextFactory dbFactory, IServiceProvider services, TimeProvider clock, ILogger<AutomationScheduler> logger)
{
    public const int MaxRecords = 1000;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        if ((await db.Set<AutomationSettings>().AsNoTracking().FirstOrDefaultAsync(ct))?.PausedAll == true)
        {
            return 0;
        }

        var kinds = new[] { nameof(AutomationTriggerKind.Schedule), nameof(AutomationTriggerKind.DateField), nameof(AutomationTriggerKind.Threshold) };
        var automations = await db.Set<AutomationRecord>().AsNoTracking()
            .Where(a => a.IsEnabled && a.DeletedAtUtc == null && a.SheetId == null && kinds.Contains(a.TriggerKind)).ToListAsync(ct); // Sheets run their own (hourly).
        var queued = 0;
        foreach (var automation in automations)
        {
            try
            {
                var version = await db.Set<AutomationVersion>().AsNoTracking().FirstAsync(v => v.Id == automation.CurrentVersionId, ct);
                queued += await CheckAsync(db, automation, AutomationJson.Definition(version.DefinitionJson).Trigger, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                (db as DbContext)?.ChangeTracker.Clear();
                logger.LogError(ex, "Checking automation {AutomationId}'s schedule failed", automation.Id);
            }
        }

        return queued;
    }

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateTime LocalNow => services.GetService<TimekeepingClock>() is { } c ? c.LocalTime(UtcNow) : UtcNow;

    private async Task<int> CheckAsync(IWorkbenchDbContext db, AutomationRecord automation, AutomationTrigger trigger, CancellationToken ct)
    {
        var local = LocalNow;
        var today = DateOnly.FromDateTime(local);
        var entity = AutomationEntities.Find(trigger.EntityType);
        var hourReached = local.Hour >= (trigger.Hour ?? 7);
        var fires = new List<(Guid? RecordId, string Key, string Detail)>();
        switch (trigger.Kind)
        {
            case AutomationTriggerKind.Schedule:
                var due = hourReached && trigger.Frequency switch
                {
                    ScheduleFrequency.Weekly => local.DayOfWeek == (trigger.DayOfWeek ?? DayOfWeek.Monday),
                    ScheduleFrequency.Monthly => today.Day == Math.Min(trigger.DayOfMonth ?? 1, DateTime.DaysInMonth(today.Year, today.Month)),
                    _ => true,
                };
                if (!due)
                {
                    return 0;
                }

                var detail = $"{trigger.Frequency ?? ScheduleFrequency.Daily} schedule, {today:yyyy-MM-dd}";
                if (entity is null)
                {
                    fires.Add((null, $"schedule:{automation.Id:N}:{today:yyyyMMdd}", detail));
                }
                else
                {
                    foreach (var id in await IdsAsync(db, entity, null, ct))
                    {
                        fires.Add((id, $"schedule:{automation.Id:N}:{today:yyyyMMdd}:{id:N}", detail));
                    }
                }

                break;

            case AutomationTriggerKind.DateField when entity is not null && trigger.FieldKey is { } field && hourReached:
                var target = today.AddDays(-(trigger.OffsetDays ?? 0));
                foreach (var id in await IdsAsync(db, entity, (field, target), ct))
                {
                    fires.Add((id, $"date:{automation.Id:N}:{id:N}:{target:yyyyMMdd}", $"{field} is {target:yyyy-MM-dd}"));
                }

                break;

            case AutomationTriggerKind.Threshold:
                return await CheckThresholdAsync(db, automation, trigger, ct);
        }

        return await QueueAsync(db, automation, trigger.Kind, fires, ct);
    }

    private async Task<int> QueueAsync(IWorkbenchDbContext db, AutomationRecord automation, AutomationTriggerKind kind, IReadOnlyList<(Guid? RecordId, string Key, string Detail)> fires,
        CancellationToken ct)
    {
        if (fires.Count == 0)
        {
            await db.SaveChangesAsync(ct); // Thresholds may have re-armed.
            return 0;
        }

        var keys = fires.Select(f => f.Key).ToList();
        var existing = (await db.Set<AutomationQueueItem>().AsNoTracking().Where(q => keys.Contains(q.DedupKey)).Select(q => q.DedupKey).ToListAsync(ct)).ToHashSet();
        var trigger = AutomationEntities.Find(automation.EntityType)?.Key;
        var added = 0;
        foreach (var (recordId, key, detail) in fires.Where(f => !existing.Contains(f.Key)))
        {
            db.Set<AutomationQueueItem>().Add(new AutomationQueueItem
            {
                AutomationId = automation.Id, TriggerKind = kind.ToString(), TriggerDetail = AutomationExecutor.Clip(detail, 200), EntityType = trigger,
                RecordId = recordId, DedupKey = key, CreatedAtUtc = UtcNow,
            });
            added++;
        }

        await db.SaveChangesAsync(ct);
        return added;
    }

    private async Task<int> CheckThresholdAsync(IWorkbenchDbContext db, AutomationRecord automation, AutomationTrigger trigger, CancellationToken ct)
    {
        var over = new List<(Guid RecordId, string Key, string Detail)>();
        var percent = trigger.Percent ?? 0m;
        switch (trigger.Threshold)
        {
            case ThresholdKind.ContractFundingBelow:
                var contracts = await db.Set<Contract>().AsNoTracking().Where(c => c.Status != ContractStatus.Closed).Take(MaxRecords).ToListAsync(ct);
                var contractEntity = AutomationEntities.Require("contract");
                foreach (var contract in contracts)
                {
                    var remaining = await contractEntity.Computed.First(c => c.Key == "fundingRemainingPercent").Read(new AutomationRecordContext(db, contract, services, ct)) as decimal?;
                    if (remaining is { } r && r < percent)
                    {
                        over.Add((contract.Id, $"funding:{contract.Id:N}", $"Funding remaining {r:0.#}% (under {percent:0.#}%)"));
                    }
                }

                break;

            case ThresholdKind.ProjectBudgetOver:
                var projects = await db.Set<Project>().AsNoTracking().Where(p => p.Status != ProjectStatus.Closed).Take(MaxRecords).ToListAsync(ct);
                var projectEntity = AutomationEntities.Require("project");
                foreach (var project in projects)
                {
                    var used = await projectEntity.Computed.First(c => c.Key == "budgetUsedPercent").Read(new AutomationRecordContext(db, project, services, ct)) as decimal?;
                    if (used is { } u && u > percent)
                    {
                        over.Add((project.Id, $"budget:{project.Id:N}", $"Budget used {u:0.#}% (over {percent:0.#}%)"));
                    }
                }

                break;

            case ThresholdKind.TimesheetMissing:
                over.AddRange(await MissingTimesheetsAsync(db, trigger.Days ?? 1, ct));
                break;

            case ThresholdKind.ApprovalWaiting:
                var entity = AutomationEntities.Find(trigger.EntityType);
                var types = new List<string>();
                if (entity?.ApprovalDocumentType is { } type)
                {
                    types.Add(type);
                }
                else if (trigger.EntityType is null)
                {
                    types.AddRange(Contracts.Approvals.ApprovalDocumentTypes.All);
                }

                var now = UtcNow;
                var waiting = await db.Set<ApprovalTask>().AsNoTracking()
                    .Where(t => t.Status == ApprovalTaskStatus.Pending && t.ActivatedAtUtc != null)
                    .Join(db.Set<ApprovalInstance>().Where(i => i.Status == ApprovalInstanceStatus.Pending && types.Contains(i.DocumentType)),
                        t => t.InstanceId, i => i.Id, (t, i) => new { TaskId = t.Id, t.ActivatedAtUtc, i.DocumentId, t.StepName })
                    .Take(MaxRecords).ToListAsync(ct);
                foreach (var w in waiting)
                {
                    var days = BusinessDays.Between(w.ActivatedAtUtc!.Value, now);
                    if (days >= (trigger.Days ?? 2))
                    {
                        over.Add((w.DocumentId, $"approval:{w.TaskId:N}", $"{w.StepName} approval waiting {days} business day(s)"));
                    }
                }

                break;
        }

        // Fire for records newly over the threshold; re-arm records that are back under it.
        var states = await db.Set<AutomationTriggerState>().Where(s => s.AutomationId == automation.Id).ToListAsync(ct);
        var current = over.Select(o => o.Key).ToHashSet();
        var rearm = trigger.Threshold is ThresholdKind.ContractFundingBelow or ThresholdKind.ProjectBudgetOver;
        if (rearm)
        {
            db.Set<AutomationTriggerState>().RemoveRange(states.Where(s => !current.Contains(s.Key)));
        }

        var fired = states.Select(s => s.Key).ToHashSet();
        var fires = new List<(Guid? RecordId, string Key, string Detail)>();
        foreach (var fire in over.Where(o => !fired.Contains(o.Key)))
        {
            var state = new AutomationTriggerState { AutomationId = automation.Id, Key = fire.Key, FiredAtUtc = UtcNow };
            db.Set<AutomationTriggerState>().Add(state);
            fires.Add((fire.RecordId, $"threshold:{automation.Id:N}:{fire.Key}:{state.Id:N}", fire.Detail)); // Each crossing queues once.
        }

        return await QueueAsync(db, automation, AutomationTriggerKind.Threshold, fires, ct);
    }

    /// <summary>Employees whose timesheet for the last period that ended <paramref name="graceDays"/> or more days ago isn't submitted.</summary>
    private async Task<List<(Guid RecordId, string Key, string Detail)>> MissingTimesheetsAsync(IWorkbenchDbContext db, int graceDays, CancellationToken ct)
    {
        var result = new List<(Guid, string, string)>();
        var clockService = services.GetService<TimekeepingClock>();
        var today = DateOnly.FromDateTime(LocalNow);
        var probe = today.AddDays(-graceDays - 1);
        (DateOnly Start, DateOnly End) period = clockService is null
            ? TimekeepingClock.PeriodFor(probe, default(TimesheetCadence))
            : await clockService.PeriodForAsync(db, probe, ct);
        if (period.End > probe)
        {
            period = clockService is null
                ? TimekeepingClock.PeriodFor(period.Start.AddDays(-1), default(TimesheetCadence))
                : await clockService.PeriodForAsync(db, period.Start.AddDays(-1), ct);
        }

        var employees = (await db.Set<Employee>().AsNoTracking().Where(e => e.HireDate <= period.End).Take(MaxRecords * 5).ToListAsync(ct))
            .Where(e => e.IsActiveOn(period.End)).ToList();
        var done = (await db.Set<Timesheet>().AsNoTracking()
                .Where(t => t.PeriodStart <= period.End && t.PeriodEnd >= period.Start && (t.Status == TimesheetStatus.Submitted || t.Status == TimesheetStatus.Approved))
                .Select(t => t.EmployeeId).ToListAsync(ct))
            .ToHashSet();
        foreach (var employee in employees.Where(e => !done.Contains(e.Id)))
        {
            result.Add((employee.Id, $"timesheet:{employee.Id:N}:{period.Start:yyyyMMdd}", $"No timesheet submitted for {period.Start:MMM d}–{period.End:MMM d}"));
        }

        return result;
    }

    /// <summary>Records of a type (newest first), or those whose date field equals a date.</summary>
    private static async Task<List<Guid>> IdsAsync(IWorkbenchDbContext db, AutomationEntity entity, (string Field, DateOnly Date)? onDate, CancellationToken ct)
    {
        var method = typeof(AutomationScheduler).GetMethod(nameof(IdsOfAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(entity.ClrType);
        PropertyInfoOrNull(entity, onDate?.Field);
        return await (Task<List<Guid>>)method.Invoke(null, [db, entity, onDate, ct])!;
    }

    private static void PropertyInfoOrNull(AutomationEntity entity, string? field)
    {
        if (field is not null && entity.Property(field) is null)
        {
            throw new ArgumentException($"{entity.Label} has no built-in date field \"{field}\".");
        }
    }

    private static async Task<List<Guid>> IdsOfAsync<T>(IWorkbenchDbContext db, AutomationEntity entity, (string Field, DateOnly Date)? onDate, CancellationToken ct)
        where T : Domain.Entity
    {
        IQueryable<T> query = db.Set<T>().AsNoTracking();
        if (onDate is { } filter)
        {
            var property = entity.Property(filter.Field)!;
            var parameter = Expression.Parameter(typeof(T), "e");
            var member = Expression.Property(parameter, property);
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            Expression body;
            if (type == typeof(DateOnly))
            {
                body = Expression.Equal(member, Expression.Constant(filter.Date, property.PropertyType));
            }
            else if (type == typeof(DateTime))
            {
                var start = filter.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
                body = Expression.AndAlso(
                    Expression.GreaterThanOrEqual(member, Expression.Constant(start, property.PropertyType)),
                    Expression.LessThan(member, Expression.Constant(start.AddDays(1), property.PropertyType)));
            }
            else
            {
                throw new ArgumentException($"{entity.Label} › {property.Name} isn't a date.");
            }

            query = query.Where(Expression.Lambda<Func<T, bool>>(body, parameter));
        }

        return await query.OrderByDescending(e => e.Id).Select(e => e.Id).Take(MaxRecords).ToListAsync(ct);
    }
}

/// <summary>
/// Runs automations for every tenant: the queue every few seconds (sooner after a change queued a run), schedules and
/// thresholds every five minutes, pruning hourly. Registered by <see cref="AutomationBuilderRegistration.AddWorkbenchAutomationBuilderHosting"/>.
/// </summary>
public sealed class AutomationBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, AutomationSignal signal, TimeProvider clock,
    Microsoft.Extensions.Configuration.IConfiguration configuration, ILogger<AutomationBackgroundService> logger) : BackgroundService
{
    /// <summary>Configuration switch (default true): runs automations on this host. Safe on several hosts (queue items are claimed).</summary>
    public const string EnabledKey = "Automations:BackgroundEnabled";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.Equals(configuration[EnabledKey], "false", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Automations don't run on this host ({Key} is false)", EnabledKey);
            return;
        }

        var lastSchedule = DateTime.MinValue;
        var lastPrune = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var schedule = now - lastSchedule >= TimeSpan.FromMinutes(5);
            var prune = now - lastPrune >= TimeSpan.FromHours(1);
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (schedule)
                    {
                        await scope.ServiceProvider.GetRequiredService<AutomationScheduler>().RunAsync(ct);
                    }

                    var processor = scope.ServiceProvider.GetRequiredService<AutomationProcessor>();
                    var ran = await processor.RunOnceAsync(ct);
                    if (prune)
                    {
                        await processor.PruneAsync(ct);
                    }

                    if (ran > 0)
                    {
                        logger.LogInformation("Ran {Count} automation triggers", ran);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Automation pass failed");
            }

            lastSchedule = schedule ? now : lastSchedule;
            lastPrune = prune ? now : lastPrune;
            try
            {
                if (await signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken); // Let the change that queued it commit.
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
