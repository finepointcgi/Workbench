using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tasks;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Tasks;

namespace Workbench.Application.Tasks;

/// <summary>
/// Task assignment: an item's Assignees (directory users), notifications and activity when they change, an assignee's
/// status update, "my tasks" and a project's workload. Permission checks are in <c>PermissionCheckingTaskAssignmentService</c>;
/// item changes go through <see cref="IItemService"/> (permission-checked, recorded, automated), so an assignee's status
/// update is allowed by the item decorator's narrow assignee rule.
/// </summary>
public sealed class TaskAssignmentService(
    IWorkbenchDbContextFactory dbFactory,
    IItemService items,
    ICurrentUser currentUser,
    AccessControl access,
    ActivityRecorder activity,
    INotificationPublisher notifications,
    TimeProvider clock) : ITaskAssignmentService
{
    private string Me => AccessControl.Normalize(currentUser.Email);

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<SheetTasksDto> GetSheetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var rows = await AssigneesOfSheetAsync(db, sheetId, cancellationToken);
        var names = await NamesAsync(db, rows.Select(r => r.Email), cancellationToken);
        var map = rows.GroupBy(r => r.ItemId).ToDictionary(
            g => g.Key,
            g => (IReadOnlyList<TaskAssigneeDto>)g.OrderBy(r => names.GetValueOrDefault(r.Email) ?? r.Email, StringComparer.OrdinalIgnoreCase).Select(r => ToDto(r, names)).ToList());
        var status = TaskRules.StatusColumn(columns);
        var percent = TaskRules.PercentColumn(columns);
        return new SheetTasksDto(sheetId, Me, false, map, status?.ToDto(), percent?.ToDto());
    }

    public async Task<IReadOnlyList<TaskAssigneeDto>> SetAssigneesAsync(Guid sheetId, Guid itemId, SetAssigneesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var emails = (request.Emails ?? []).Select(AccessControl.Normalize).Where(e => e.Length > 0).Distinct().ToList();
        if (emails.Count > 50)
        {
            throw new ArgumentException("An item can have at most 50 assignees.");
        }

        Item item;
        string sheetName;
        List<string> added, removed;
        Dictionary<string, string> names;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.SheetId == sheetId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Item", itemId);
            sheetName = await db.Sheets.Where(s => s.Id == sheetId).Select(s => s.Name).FirstAsync(cancellationToken);
            var users = await db.Set<AppUser>().AsNoTracking().Where(u => emails.Contains(u.Email) && u.IsActive).Select(u => u.Email).ToListAsync(cancellationToken);
            if (emails.FirstOrDefault(e => !users.Contains(e)) is { } unknown)
            {
                throw new ArgumentException($"{unknown} isn't an active Workbench user.");
            }

            var current = await db.Set<ItemAssignee>().Where(a => a.ItemId == itemId).ToListAsync(cancellationToken);
            removed = current.Where(a => !emails.Contains(a.Email)).Select(a => a.Email).ToList();
            added = emails.Where(e => current.All(a => a.Email != e)).ToList();
            db.Set<ItemAssignee>().RemoveRange(current.Where(a => removed.Contains(a.Email)));
            var now = clock.GetUtcNow().UtcDateTime;
            var actor = Truncate(currentUser.Email, 512);
            db.Set<ItemAssignee>().AddRange(added.Select(e => new ItemAssignee { SheetId = sheetId, ItemId = itemId, Email = e, AssignedBy = actor, AssignedAtUtc = now }));

            // On a project's plan the assignees and the people with hours are one list: taking someone off takes their hours too.
            await Scheduling.PlanPeopleSync.TakeOffHoursAsync(db, itemId, removed, actor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            names = await NamesAsync(db, added.Concat(removed).Append(Me), cancellationToken);
        }

        // The due date is the item's Finish: changed through the item service, so it's checked, recorded and automated too.
        var due = item.Finish;
        if ((request.Due is { } newDue && newDue != item.Finish) || (request.ClearDue && item.Finish is not null))
        {
            due = request.ClearDue ? null : request.Due;
            var start = item.Start is { } s && due is { } f && s > f ? f : item.Start;
            await items.UpdateAsync(sheetId, itemId, new UpdateItemRequest(item.Title, start, due, item.CostpointWbsId, item.Values), cancellationToken);
        }

        if (added.Count > 0 || removed.Count > 0)
        {
            string Name(string e) => names.GetValueOrDefault(e) ?? e;
            var parts = new List<string>();
            if (added.Count > 0)
            {
                parts.Add($"Assigned {string.Join(", ", added.Select(Name).Order(StringComparer.OrdinalIgnoreCase))}");
            }

            if (removed.Count > 0)
            {
                parts.Add($"{(added.Count > 0 ? "unassigned" : "Unassigned")} {string.Join(", ", removed.Select(Name).Order(StringComparer.OrdinalIgnoreCase))}");
            }

            await activity.RecordAsync(sheetId, itemId, TaskActivityKinds.ItemAssigned, $"{string.Join("; ", parts)} on \"{item.Title}\"", SheetChangeKind.ItemUpdated);

            var me = Me;
            var actorName = names.GetValueOrDefault(me) ?? currentUser.Email;
            await notifications.PublishAsync(
                added.Where(e => e != me), NotificationKinds.Assignment,
                $"{actorName} assigned you \"{item.Title}\"",
                $"{sheetName} · {DueText(due)}",
                sheetId: sheetId, itemId: itemId);
        }

        return (await GetSheetAsync(sheetId, cancellationToken)).For(itemId);
    }

    public async Task<ItemDto> UpdateStatusAsync(Guid sheetId, Guid itemId, UpdateTaskStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Item item;
        List<Column> columns;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.SheetId == sheetId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Item", itemId);
            columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        }

        var values = new Dictionary<string, string?>(item.Values);
        if (request.Status is { } status)
        {
            var column = TaskRules.StatusColumn(columns) ?? throw new ArgumentException("This sheet has no Status column.");
            var option = column.Options.FirstOrDefault(o => string.Equals(o, status.Trim(), StringComparison.OrdinalIgnoreCase));
            if (status.Trim().Length > 0 && column.Options.Count > 0 && option is null)
            {
                throw new ArgumentException($"\"{status}\" isn't one of the Status options.");
            }

            values[column.Id.ToString()] = status.Trim().Length == 0 ? null : option ?? status.Trim();
        }

        if (request.PercentComplete is { } percent)
        {
            var column = TaskRules.PercentColumn(columns) ?? throw new ArgumentException("This sheet has no % Complete column.");
            values[column.Id.ToString()] = TaskRules.FormatPercent(percent);
        }

        return await items.UpdateAsync(sheetId, itemId, new UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId, values), cancellationToken);
    }

    public async Task<IReadOnlyList<MyTaskDto>> ListMineAsync(bool includeDone = false, CancellationToken cancellationToken = default)
    {
        var me = Me;
        var visible = await access.GetVisibleWorkspaceIdsAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<ItemAssignee>().AsNoTracking()
            .Where(a => a.Email == me)
            .Join(db.Items.AsNoTracking(), a => a.ItemId, i => i.Id, (a, i) => new { a, i })
            .Join(db.Sheets.AsNoTracking(), x => x.i.SheetId, s => s.Id, (x, s) => new { x.a, x.i, s.Name, s.WorkspaceId })
            .ToListAsync(cancellationToken);
        rows = rows.Where(r => visible is null || visible.Contains(r.WorkspaceId)).ToList();
        var sheetIds = rows.Select(r => r.i.SheetId).Distinct().ToList();
        var statusColumns = (await db.Columns.AsNoTracking().Where(c => sheetIds.Contains(c.SheetId)).ToListAsync(cancellationToken))
            .GroupBy(c => c.SheetId)
            .ToDictionary(g => g.Key, g => TaskRules.StatusColumn(g) is { IsRestricted: false } column ? column.Id.ToString() : null);
        return rows
            .Select(r =>
            {
                var status = statusColumns.GetValueOrDefault(r.i.SheetId) is { } key ? r.i.Values.GetValueOrDefault(key) : null;
                return new MyTaskDto(r.i.SheetId, r.Name, r.i.Id, r.i.Title, r.i.Finish, string.IsNullOrWhiteSpace(status) ? null : status,
                    TaskRules.IsDone(status), r.a.AssignedBy, r.a.AssignedAtUtc);
            })
            .Where(t => includeDone || !t.IsDone)
            .OrderBy(t => t.Due ?? DateOnly.MaxValue)
            .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ProjectWorkloadDto> GetProjectWorkloadAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var today = Today;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Project", projectId);
        if (project.SheetId is not { } sheetId || !await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            return new ProjectWorkloadDto(projectId, null, today, []);
        }

        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var sheetItems = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var assignees = (await AssigneesOfSheetAsync(db, sheetId, cancellationToken)).ToLookup(a => a.ItemId, a => a.Email);
        var people = columns.Where(c => c.Type == ColumnType.Person && !c.IsRestricted).Select(c => c.Id.ToString()).ToList();
        var status = TaskRules.StatusColumn(columns) is { IsRestricted: false } s ? s.Id.ToString() : null;

        var byPerson = new Dictionary<string, List<WorkloadTaskDto>>();
        foreach (var item in sheetItems)
        {
            var itemStatus = status is null ? null : item.Values.GetValueOrDefault(status);
            if (TaskRules.IsDone(itemStatus))
            {
                continue;
            }

            var who = assignees[item.Id].Concat(people.SelectMany(p => TaskRules.People(item.Values.GetValueOrDefault(p)))).Distinct();
            foreach (var email in who)
            {
                if (!byPerson.TryGetValue(email, out var list))
                {
                    byPerson[email] = list = [];
                }

                list.Add(new WorkloadTaskDto(item.Id, item.Title, item.Finish, string.IsNullOrWhiteSpace(itemStatus) ? null : itemStatus));
            }
        }

        var names = await NamesAsync(db, byPerson.Keys, cancellationToken);
        var weekEnd = MyWork.MyWorkCalculator.WeekEnd(today);
        var rows = byPerson
            .Select(kv =>
            {
                var tasks = kv.Value.OrderBy(t => t.Due ?? DateOnly.MaxValue).ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase).ToList();
                return new WorkloadRowDto(kv.Key, names.GetValueOrDefault(kv.Key) ?? kv.Key, tasks.Count,
                    tasks.Count(t => t.Due < today), tasks.Count(t => t.Due >= today && t.Due <= weekEnd),
                    tasks.Where(t => t.Due >= today).Select(t => t.Due).FirstOrDefault(), tasks);
            })
            .OrderByDescending(r => r.Open)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new ProjectWorkloadDto(projectId, sheetId, today, rows);
    }

    /// <summary>"due Fri, Oct 23", or "no due date".</summary>
    public static string DueText(DateOnly? due) =>
        due is { } d ? "due " + d.ToString("ddd, MMM d", CultureInfo.InvariantCulture) : "no due date";

    private static Task<List<ItemAssignee>> AssigneesOfSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken ct) =>
        // Joined with the items: the InMemory provider only cascades deletes to tracked rows.
        db.Set<ItemAssignee>().AsNoTracking().Where(a => a.SheetId == sheetId)
            .Join(db.Items.AsNoTracking().Where(i => i.SheetId == sheetId), a => a.ItemId, i => i.Id, (a, i) => a)
            .ToListAsync(ct);

    private static async Task<Dictionary<string, string>> NamesAsync(IWorkbenchDbContext db, IEnumerable<string> emails, CancellationToken ct)
    {
        var list = emails.Distinct().ToList();
        return await db.Set<AppUser>().AsNoTracking().Where(u => list.Contains(u.Email)).ToDictionaryAsync(u => u.Email, u => u.DisplayName, ct);
    }

    private static TaskAssigneeDto ToDto(ItemAssignee a, IReadOnlyDictionary<string, string> names) =>
        new(a.Email, names.GetValueOrDefault(a.Email) ?? a.Email, a.AssignedBy, a.AssignedAtUtc);

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}

public static class TaskRegistration
{
    /// <summary>Task assignment. Above AddWorkbenchIdentity, which adds its permission decorator.</summary>
    public static IServiceCollection AddWorkbenchTasks(this IServiceCollection services)
    {
        services.AddScoped<ITaskAssignmentService, TaskAssignmentService>();
        services.AddScoped<MyTodo.IMyTodoProvider, TaskTodoProvider>();
        return services;
    }
}
