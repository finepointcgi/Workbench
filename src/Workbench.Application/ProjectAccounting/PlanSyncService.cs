using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Sheets;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

/// <summary>
/// Keeps a project's WBS and its plan sheet in step. Sheet changes go through <see cref="ISheetService"/> and
/// <see cref="IItemService"/>, WBS changes through <see cref="IProjectService"/>; only the links
/// (<c>Project.SheetId</c>, <c>WbsElement.ItemId</c>) are written here, with an audit row each.
/// </summary>
public sealed class PlanSyncService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    ISheetService sheets,
    IItemService items,
    IProjectService projects) : IPlanSyncService
{
    public async Task<ProjectDto> CreatePlanSheetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // The tasks are made from the elements and linked here, so the plan's own WBS sync waits.
        using var paused = Scheduling.PlanWbsSync.Pause();
        var project = await GetProjectAsync(projectId, cancellationToken);
        if (project.SheetId is { } existing && await sheets.GetAsync(existing, cancellationToken) is not null)
        {
            throw new ArgumentException("This project already has a plan sheet.");
        }

        var wbs = await projects.ListWbsAsync(projectId, cancellationToken);
        var sheet = await sheets.CreateAsync(
            new CreateSheetRequest($"{project.Code} {project.Name}", $"Plan for project {project.Code}. Its tasks mirror the WBS.", SchedulingEnabled: true),
            cancellationToken);

        var links = new Dictionary<Guid, Guid>();
        async Task AddChildrenAsync(Guid? parentElementId, Guid? parentItemId)
        {
            foreach (var element in wbs.Where(w => w.ParentId == parentElementId).OrderBy(w => w.Order).ThenBy(w => w.Code, StringComparer.Ordinal))
            {
                var item = await CreateTaskAsync(sheet.Id, element, parentItemId, cancellationToken);
                links[element.Id] = item.Id;
                await AddChildrenAsync(element.Id, item.Id);
            }
        }

        await AddChildrenAsync(null, null);
        await SaveLinksAsync(projectId, sheet.Id, links, cancellationToken);
        return await GetProjectAsync(projectId, cancellationToken);
    }

    public async Task<PlanSyncReport> DiffAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(projectId, cancellationToken);
        return new PlanSyncReport(projectId, state.Project.SheetId, Diff(state));
    }

    public async Task<PlanSyncReport> ApplyAsync(Guid projectId, ApplyPlanSyncRequest request, CancellationToken cancellationToken = default)
    {
        var state = await LoadAsync(projectId, cancellationToken);
        if (state.Project.SheetId is not { } sheetId)
        {
            throw new ArgumentException("Create the project's plan sheet first.");
        }

        var toPlan = request.Direction == PlanSyncDirection.WbsToPlan;
        var selected = request.Keys is null ? null : request.Keys.ToHashSet(StringComparer.Ordinal);
        var differences = Diff(state)
            .Where(d => (selected is null || selected.Contains(d.Key)) && (toPlan ? d.CanApplyToPlan : d.CanApplyToWbs))
            .ToList();

        var elements = state.Wbs.ToDictionary(w => w.Id);
        var itemsById = state.Items.ToDictionary(i => i.Id);
        var links = new Dictionary<Guid, Guid>();
        Guid? ItemFor(Guid? elementId) =>
            elementId is { } id ? links.TryGetValue(id, out var linked) ? linked : elements[id].ItemId is { } i && itemsById.ContainsKey(i) ? i : null : null;

        using var paused = Scheduling.PlanWbsSync.Pause(); // links are saved here, after the tasks or elements exist
        if (toPlan)
        {
            // Codes sort parents before children, so a new task's parent task already exists.
            foreach (var d in differences.Where(d => d.Kind == PlanDifferenceKind.WbsWithoutTask).OrderBy(d => d.WbsCode, StringComparer.Ordinal))
            {
                var element = elements[d.WbsElementId!.Value];
                var item = await CreateTaskAsync(sheetId, element, ItemFor(element.ParentId), cancellationToken);
                links[element.Id] = item.Id;
            }

            foreach (var d in differences.Where(d => d.Kind is PlanDifferenceKind.Renamed or PlanDifferenceKind.CodeOutOfDate))
            {
                var element = elements[d.WbsElementId!.Value];
                var item = itemsById[d.ItemId!.Value];
                await items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(element.Name, item.Start, item.Finish, element.Code, item.Values), cancellationToken);
            }
        }
        else
        {
            foreach (var d in differences.Where(d => d.Kind == PlanDifferenceKind.Renamed))
            {
                var element = elements[d.WbsElementId!.Value];
                var item = itemsById[d.ItemId!.Value];
                await projects.UpdateWbsElementAsync(element.Id, ToSave(element) with { Name = item.Title }, cancellationToken);
                if (item.CostpointWbsId != element.Code)
                {
                    await items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, item.Finish, element.Code, item.Values), cancellationToken);
                }
            }

            // Parents first, so a new element's parent element already exists.
            var depth = state.Items.ToDictionary(i => i.Id, i => Depth(i, itemsById));
            var elementForItem = state.Wbs.Where(w => w.ItemId is { } i && itemsById.ContainsKey(i)).ToDictionary(w => w.ItemId!.Value, w => w.Id);
            var codes = state.Wbs.Select(w => w.Code).ToHashSet(StringComparer.Ordinal);
            foreach (var d in differences.Where(d => d.Kind == PlanDifferenceKind.TaskWithoutWbs).OrderBy(d => depth[d.ItemId!.Value]))
            {
                var item = itemsById[d.ItemId!.Value];
                Guid? parentElementId = item.ParentId is { } p && elementForItem.TryGetValue(p, out var pe) ? pe : null;
                var prefix = parentElementId is { } pid ? elements[pid].Code : state.Project.Code;
                // Smart default: a subtask takes its parent's kind; a top-level element is Direct (billable) on a contract, else Indirect.
                var kind = parentElementId is { } pk ? elements[pk].Kind : state.Project.ContractId is null ? ChargeCodeKind.Indirect : ChargeCodeKind.Direct;
                var element = await CreateElementAsync(projectId, parentElementId, prefix, item.Title, codes, cancellationToken, item.CostpointWbsId, kind);
                elements[element.Id] = element;
                elementForItem[item.Id] = element.Id;
                links[element.Id] = item.Id;
                await items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, item.Finish, element.Code, item.Values), cancellationToken);
            }
        }

        if (links.Count > 0)
        {
            await SaveLinksAsync(projectId, null, links, cancellationToken);
        }

        return await DiffAsync(projectId, cancellationToken);
    }

    private static List<PlanDifferenceDto> Diff(SyncState state)
    {
        if (state.Project.SheetId is null)
        {
            return [];
        }

        var itemsById = state.Items.ToDictionary(i => i.Id);
        var linked = new HashSet<Guid>();
        var differences = new List<PlanDifferenceDto>();
        foreach (var element in state.Wbs.OrderBy(w => w.Code, StringComparer.Ordinal))
        {
            if (element.ItemId is not { } itemId || !itemsById.TryGetValue(itemId, out var item) || !linked.Add(itemId))
            {
                differences.Add(new($"wbs:{element.Id}", PlanDifferenceKind.WbsWithoutTask, element.Id, element.Code, element.Name, null, null, true, false));
            }
            else if (!string.Equals(item.Title, element.Name, StringComparison.Ordinal))
            {
                differences.Add(new($"renamed:{element.Id}", PlanDifferenceKind.Renamed, element.Id, element.Code, element.Name, item.Id, item.Title, true, true));
            }
            else if (!string.Equals(item.CostpointWbsId, element.Code, StringComparison.Ordinal))
            {
                differences.Add(new($"code:{element.Id}", PlanDifferenceKind.CodeOutOfDate, element.Id, element.Code, element.Name, item.Id, item.Title, true, false));
            }
        }

        foreach (var item in state.Items.Where(i => !linked.Contains(i.Id)).OrderBy(i => Depth(i, itemsById)).ThenBy(i => i.Order))
        {
            differences.Add(new($"item:{item.Id}", PlanDifferenceKind.TaskWithoutWbs, null, null, null, item.Id, item.Title, false, true));
        }

        return differences;
    }

    private async Task<ItemDto> CreateTaskAsync(Guid sheetId, WbsElementDto element, Guid? parentItemId, CancellationToken cancellationToken)
    {
        var item = await items.CreateAsync(sheetId, new CreateItemRequest(element.Name, parentItemId), cancellationToken);
        return await items.UpdateAsync(sheetId, item.Id, new UpdateItemRequest(item.Title, item.Start, item.Finish, element.Code, item.Values), cancellationToken);
    }

    /// <summary>
    /// Creates an element with the task's own code when it's free and under <paramref name="prefix"/> (the plan numbers
    /// tasks automatically), else the next free two-digit code under it.
    /// </summary>
    private async Task<WbsElementDto> CreateElementAsync(
        Guid projectId, Guid? parentId, string prefix, string name, HashSet<string> codes, CancellationToken cancellationToken, string? preferred = null,
        ChargeCodeKind kind = ChargeCodeKind.Direct)
    {
        if (preferred is not null && preferred.StartsWith(prefix + ".", StringComparison.Ordinal) && codes.Add(preferred))
        {
            try
            {
                return await projects.CreateWbsElementAsync(projectId, new SaveWbsElementRequest(parentId, preferred, name, kind), cancellationToken);
            }
            catch (ArgumentException)
            {
                // Used by another project: fall through to the next free code.
            }
        }

        var next = codes
            .Where(c => c.StartsWith(prefix + ".", StringComparison.Ordinal))
            .Select(c => c[(prefix.Length + 1)..])
            .Select(rest => int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        // Codes are unique across the organization, so skip any another project already uses.
        for (var attempt = 0; ; attempt++, next++)
        {
            var code = $"{prefix}.{next.ToString("00", CultureInfo.InvariantCulture)}";
            if (!codes.Add(code))
            {
                continue;
            }

            try
            {
                return await projects.CreateWbsElementAsync(projectId, new SaveWbsElementRequest(parentId, code, name, kind), cancellationToken);
            }
            catch (ArgumentException) when (attempt < 50)
            {
            }
        }
    }

    private async Task SaveLinksAsync(Guid projectId, Guid? sheetId, IReadOnlyDictionary<Guid, Guid> links, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (sheetId is { } id)
        {
            var project = await db.Set<Project>().FirstAsync(p => p.Id == projectId, cancellationToken);
            var before = new { project.SheetId };
            project.SheetId = id;
            audit.Record(db, nameof(Project), project.Id, "PlanSheetLinked", before, new { project.SheetId });
        }

        var ids = links.Keys.ToList();
        foreach (var element in await db.Set<WbsElement>().Where(w => ids.Contains(w.Id)).ToListAsync(cancellationToken))
        {
            var before = element.ToDto();
            element.ItemId = links[element.Id];
            audit.Record(db, nameof(WbsElement), element.Id, "LinkedToPlan", before, element.ToDto());
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<SyncState> LoadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await GetProjectAsync(projectId, cancellationToken);
        var wbs = await projects.ListWbsAsync(projectId, cancellationToken);
        if (project.SheetId is { } sheetId && await sheets.GetAsync(sheetId, cancellationToken) is null)
        {
            // The sheet was deleted out from under the project: behave as if there's no plan yet.
            project = project with { SheetId = null };
        }

        var planItems = project.SheetId is { } id ? await items.ListAsync(id, cancellationToken) : [];
        return new SyncState(project, wbs, planItems);
    }

    private async Task<ProjectDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
        await projects.GetProjectAsync(projectId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Project", projectId);

    private static SaveWbsElementRequest ToSave(WbsElementDto w) =>
        new(w.ParentId, w.Code, w.Name, w.Kind, w.IsChargeable, w.IsOpen, w.RequiresAssignment);

    private static int Depth(ItemDto item, IReadOnlyDictionary<Guid, ItemDto> byId)
    {
        var depth = 0;
        for (var parent = item.ParentId; parent is { } p && byId.TryGetValue(p, out var next) && depth < 100; parent = next.ParentId)
        {
            depth++;
        }

        return depth;
    }

    private sealed record SyncState(ProjectDto Project, IReadOnlyList<WbsElementDto> Wbs, IReadOnlyList<ItemDto> Items);
}
