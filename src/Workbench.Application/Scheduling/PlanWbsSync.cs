using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Sheets;
using Workbench.Domain;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Scheduling;

/// <summary>
/// On a project's plan, the plan rows are the WBS: every row (except milestones) has a WBS element made and kept in step
/// with it, so planning is building the WBS and there's no separate "make charge codes" step. Run by
/// <see cref="PlanReflow"/> after every change to the plan, in the same unit of work.
/// <list type="bullet">
/// <item>Name, parent and order follow the row. Rows without subtasks are charge codes; summary rows aren't.</item>
/// <item>The code follows the row's automatic number, except once time is posted to it: then it keeps its code (and
/// numbering works around it), its rows can't be deleted (<see cref="PlanWbsItemService"/>), and a summary over it
/// stays chargeable.</item>
/// <item>Kind (direct, indirect, leave), open/closed, CLIN mapping and charge rules stay as Finance sets them in Setup.</item>
/// </list>
/// </summary>
internal static class PlanWbsSync
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly AsyncLocal<int> Paused = new();

    /// <summary>
    /// Holds the sync while tasks are made from WBS elements that already exist (Setup's "WBS to plan"), which links
    /// them itself; otherwise each new task would get a second element.
    /// </summary>
    public static IDisposable Pause()
    {
        Paused.Value++;
        return new Resume();
    }

    private sealed class Resume : IDisposable
    {
        public void Dispose() => Paused.Value--;
    }

    /// <summary>The project's WBS elements with time on any timesheet (whose codes never change).</summary>
    public static async Task<HashSet<Guid>> PostedAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> elementIds, CancellationToken ct)
    {
        var ids = elementIds.ToList();
        return ids.Count == 0 ? [] : (await db.Set<TimeEntry>().AsNoTracking().Where(e => ids.Contains(e.WbsElementId)).Select(e => e.WbsElementId).Distinct().ToListAsync(ct)).ToHashSet();
    }

    /// <summary>Brings the project's WBS in line with the plan's rows. True when anything changed (the caller saves).</summary>
    public static async Task<bool> ApplyAsync(IWorkbenchDbContext db, Project project, IReadOnlyCollection<Item> items, CancellationToken ct)
    {
        if (Paused.Value > 0)
        {
            return false;
        }

        var elements = await db.Set<WbsElement>().Where(w => w.ProjectId == project.Id).ToListAsync(ct);
        var itemIds = items.Select(i => i.Id).ToHashSet();
        var byItem = elements.Where(e => e.ItemId is { } i && itemIds.Contains(i)).GroupBy(e => e.ItemId!.Value).ToDictionary(g => g.Key, g => g.First());
        var posted = await PostedAsync(db, elements.Select(e => e.Id).ToList(), ct);
        var outline = new PlanOutline(items);
        var actor = SheetRowStamps.CurrentAuthor ?? "plan";
        var now = DateTime.UtcNow;
        var changed = false;

        // Codes in use anywhere (they're unique across the organization), less the ones about to move.
        var stable = await db.Set<WbsElement>().AsNoTracking().Select(w => new { w.Id, w.Code }).ToListAsync(ct);
        var recodes = new List<(WbsElement Element, string Code)>();
        var created = new List<WbsElement>();

        foreach (var item in outline.Ordered)
        {
            var code = item.CostpointWbsId?.Trim();
            if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrEmpty(code))
            {
                continue;
            }

            var existing = byItem.GetValueOrDefault(item.Id);
            if (existing is null && item.IsMilestone)
            {
                continue; // a milestone is a point in time, not work anyone charges to
            }

            Guid? parentId = null;
            for (var p = outline.ParentOf(item); p is { } pid; p = outline.ParentOf(outline.Find(pid)!))
            {
                if (byItem.TryGetValue(pid, out var parentElement))
                {
                    parentId = parentElement.Id;
                    break;
                }
            }

            var leaf = !outline.IsSummary(item.Id);
            var name = item.Title.Trim()[..Math.Min(item.Title.Trim().Length, 200)];
            var order = outline.ChildrenOf(item.ParentId).ToList().IndexOf(item);
            if (existing is null)
            {
                var parent = parentId is { } pe ? elements.Concat(created).First(e => e.Id == pe) : null;
                var element = new WbsElement
                {
                    ProjectId = project.Id,
                    ParentId = parentId,
                    Code = code,
                    Name = name,
                    Kind = parent?.Kind ?? (project.ContractId is null ? ChargeKind.Indirect : ChargeKind.Direct),
                    IsChargeable = leaf,
                    ItemId = item.Id,
                    Order = Math.Max(0, order),
                    ClinId = null,
                };
                created.Add(element);
                byItem[item.Id] = element;
                changed = true;
                continue;
            }

            var before = Snapshot(existing);
            var touched = false;
            if (existing.Name != name)
            {
                existing.Name = name;
                touched = true;
            }

            if (existing.ParentId != parentId && parentId != existing.Id)
            {
                existing.ParentId = parentId;
                touched = true;
            }

            if (order >= 0 && existing.Order != order)
            {
                existing.Order = order;
                touched = true;
            }

            var chargeable = leaf || posted.Contains(existing.Id);
            if (existing.IsChargeable != chargeable && (leaf || !posted.Contains(existing.Id)))
            {
                existing.IsChargeable = chargeable;
                touched = true;
            }

            if (existing.Code != code && !posted.Contains(existing.Id))
            {
                recodes.Add((existing, code));
            }

            if (touched)
            {
                Audit(db, existing, "UpdatedFromPlan", before, actor, now);
                changed = true;
            }
        }

        // New codes may swap with each other (a row moved up), and codes are unique across the organization: new
        // elements take free codes, and the moving ones are parked on a temporary code, saved, then given their own.
        var moving = recodes.Select(r => r.Element.Id).ToHashSet();
        var takenByOthers = stable.Where(s => !moving.Contains(s.Id)).Select(s => s.Code).ToHashSet(StringComparer.Ordinal);
        recodes = recodes.Where(r => !takenByOthers.Contains(r.Code)).ToList();
        var used = takenByOthers.Concat(recodes.Select(r => r.Code)).ToHashSet(StringComparer.Ordinal);
        foreach (var element in created)
        {
            if (!used.Add(element.Code))
            {
                // A code someone typed that another charge code already has: the next free one under the same parent.
                var prefix = element.Code.Contains('.', StringComparison.Ordinal) ? element.Code[..element.Code.LastIndexOf('.')] : project.Code;
                element.Code = WbsCodes.Next(prefix, used.Where(u => u.StartsWith(prefix + ".", StringComparison.Ordinal)));
                used.Add(element.Code);
                if (items.FirstOrDefault(i => i.Id == element.ItemId) is { } item)
                {
                    item.CostpointWbsId = element.Code;
                    item.WbsCodeIsAuto = false;
                }
            }

            db.Set<WbsElement>().Add(element);
            Audit(db, element, "CreatedFromPlan", null, actor, now);
        }

        if (recodes.Count > 0)
        {
            var befores = recodes.ToDictionary(r => r.Element.Id, r => Snapshot(r.Element));
            foreach (var (element, _) in recodes)
            {
                element.Code = $"~{element.Id:N}";
            }

            await db.SaveChangesAsync(ct);
            foreach (var (element, code) in recodes)
            {
                var before = befores[element.Id];
                element.Code = code;
                Audit(db, element, "RecodedFromPlan", before, actor, now);
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Before plan rows are deleted (with their subtasks, <paramref name="doomed"/>): a row whose charge code has time on it
    /// can't go (nothing is changed). Other rows' codes are deleted when nothing refers to them, else closed and kept.
    /// </summary>
    public static async Task BeforeRowsDeletedAsync(IWorkbenchDbContext db, Guid sheetId, IReadOnlySet<Guid> doomed, CancellationToken ct)
    {
        if (await PlanReflow.ProjectForAsync(db, sheetId, ct) is null)
        {
            return;
        }

        var ids = doomed.ToList();
        var elements = await db.Set<WbsElement>().Where(w => w.ItemId != null && ids.Contains(w.ItemId.Value)).ToListAsync(ct);
        if (elements.Count == 0)
        {
            return;
        }

        var posted = await PostedAsync(db, elements.Select(e => e.Id).ToList(), ct);
        if (elements.FirstOrDefault(e => posted.Contains(e.Id)) is { } charged)
        {
            throw new ArgumentException(
                $"{charged.Code} {charged.Name} has time charged to it, so it stays on the plan. Close its charge code under Setup instead of deleting the row.");
        }

        var referenced = await ReferencedAsync(db, elements.Select(e => e.Id).ToList(), ct);
        var actor = SheetRowStamps.CurrentAuthor ?? "plan";
        var now = DateTime.UtcNow;
        foreach (var element in elements)
        {
            var before = Snapshot(element);
            if (referenced.Contains(element.Id))
            {
                element.ItemId = null;
                element.IsOpen = false;
                element.IsChargeable = false;
                Audit(db, element, "ClosedFromPlan", before, actor, now);
            }
            else
            {
                db.Set<WbsElement>().Remove(element);
                Audit(db, element, "DeletedFromPlan", before, actor, now);
            }
        }
    }

    /// <summary>
    /// The elements something else refers to (staffing, budgets, charge rules, ceilings, ODCs and every other table with a
    /// key to a WBS element), read from the model so a new table is covered too. Subtasks among them don't count.
    /// </summary>
    private static async Task<HashSet<Guid>> ReferencedAsync(IWorkbenchDbContext db, List<Guid> ids, CancellationToken ct)
    {
        if (db is not DbContext context || context.Model.FindEntityType(typeof(WbsElement)) is not { } type)
        {
            return ids.ToHashSet(); // can't tell: keep them all
        }

        var used = new HashSet<Guid>();
        var method = typeof(PlanWbsSync).GetMethod(nameof(ReferencingAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var fk in type.GetReferencingForeignKeys())
        {
            var dependent = fk.DeclaringEntityType;
            if (fk.Properties.Count != 1 || dependent.IsOwned() || dependent.HasSharedClrType)
            {
                continue;
            }

            var property = fk.Properties[0].Name;
            var found = await (Task<List<Guid>>)method.MakeGenericMethod(dependent.ClrType).Invoke(null, [context, property, ids, ct])!;
            if (dependent.ClrType == typeof(WbsElement) && property == nameof(WbsElement.ParentId))
            {
                // A subtask being deleted with it doesn't keep it; one staying does.
                var children = await context.Set<WbsElement>().AsNoTracking().Where(w => w.ParentId != null && ids.Contains(w.ParentId.Value) && !ids.Contains(w.Id))
                    .Select(w => w.ParentId!.Value).ToListAsync(ct);
                used.UnionWith(children);
                continue;
            }

            used.UnionWith(found);
        }

        return used;
    }

    private static async Task<List<Guid>> ReferencingAsync<T>(DbContext context, string property, List<Guid> ids, CancellationToken ct)
        where T : class =>
        (await context.Set<T>().AsNoTracking().Where(e => EF.Property<Guid?>(e, property) != null && ids.Contains(EF.Property<Guid?>(e, property)!.Value))
            .Select(e => EF.Property<Guid?>(e, property)).ToListAsync(ct)).OfType<Guid>().ToList();

    private static object Snapshot(WbsElement e) => new { e.Code, e.Name, e.ParentId, e.IsChargeable, e.Order };

    private static void Audit(IWorkbenchDbContext db, WbsElement element, string action, object? before, string actor, DateTime now) =>
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            EntityType = nameof(WbsElement),
            EntityId = element.Id,
            Action = action,
            BeforeJson = before is null ? null : JsonSerializer.Serialize(before, Json),
            AfterJson = JsonSerializer.Serialize(Snapshot(element), Json),
            Actor = actor,
            OccurredAtUtc = now,
        });
}
