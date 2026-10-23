using Microsoft.EntityFrameworkCore;
using Workbench.Application.Automations;
using Workbench.Application.Notifications;
using Workbench.Application.Sheets;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;
using Workbench.Domain;

namespace Workbench.Application.Automation;

/// <summary>
/// Decorates <see cref="IItemService"/>: captures the item before and after each create or update, notifies people
/// newly assigned in a Person column (unless they made the change), and runs the sheet's automations
/// (<see cref="SheetAutomationRuntime"/>; rows added inside <see cref="SheetAutomationEvents.AddedByForm"/> also fire "added by a form").
/// <para>
/// Ordering: registered after <c>AddWorkbenchCollaboration</c>, so it wraps <c>ActivityRecordingItemService</c>:
/// <c>AutomationItemService → ActivityRecordingItemService → ItemService</c>. The person's change is saved and
/// recorded first; rule actions then run through this decorator again (passing itself to the engine), so they
/// are recorded too, as "Workbench Automation" naming the rule and its author, and can chain. A permission decorator should wrap this one
/// (register it later) so people are checked but rule actions, which act for the rule's author, are not.
/// </para>
/// </summary>
public sealed class AutomationItemService : IItemService
{
    private readonly IItemService inner;
    private readonly IWorkbenchDbContextFactory dbFactory;
    private readonly SheetAutomationRuntime engine;
    private readonly Notifier notifier;
    private readonly ICurrentUser currentUser;

    public AutomationItemService(IItemService inner, IWorkbenchDbContextFactory dbFactory, SheetAutomationRuntime engine, Notifier notifier, ICurrentUser currentUser,
        AutomationItemServiceAccessor? accessor = null)
    {
        (this.inner, this.dbFactory, this.engine, this.notifier, this.currentUser) = (inner, dbFactory, engine, notifier, currentUser);
        if (accessor is not null)
        {
            accessor.Instance ??= this;
        }
    }

    public Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        inner.ListAsync(sheetId, cancellationToken);

    public async Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default)
    {
        var created = await inner.CreateAsync(sheetId, request, cancellationToken);
        await NotifyAssignedAsync(sheetId, null, created);
        using (SheetAutomationRuntime.ForActor(currentUser.Email))
        {
            await engine.OnItemCreatedAsync(this, sheetId, created, SheetAutomationEvents.CurrentForm);
        }

        return created;
    }

    public async Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default)
    {
        ItemDto? before;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            before = (await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.SheetId == sheetId, cancellationToken))?.ToDto();
        }

        var updated = await inner.UpdateAsync(sheetId, itemId, request, cancellationToken);
        if (before is not null)
        {
            await NotifyAssignedAsync(sheetId, before, updated);
            using (SheetAutomationRuntime.ForActor(currentUser.Email))
            {
                await engine.OnItemUpdatedAsync(this, sheetId, before, updated);
            }
        }

        return updated;
    }

    public Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default) =>
        inner.MoveAsync(sheetId, itemId, request, cancellationToken);

    public Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(sheetId, itemId, cancellationToken);

    /// <summary>"You were assigned": an email newly in a Person column, unless that person made the change.</summary>
    private async Task NotifyAssignedAsync(Guid sheetId, ItemDto? before, ItemDto after)
    {
        List<Column> people;
        string sheetName;
        await using (var db = await dbFactory.CreateAsync(CancellationToken.None))
        {
            people = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId && c.Type == ColumnType.Person).ToListAsync();
            if (people.Count == 0)
            {
                return;
            }

            sheetName = await db.Sheets.Where(s => s.Id == sheetId).Select(s => s.Name).FirstAsync();
        }

        var actor = currentUser.Email;
        foreach (var column in people)
        {
            var key = column.Id.ToString();
            var previous = RuleLogic.People(before?.Values.GetValueOrDefault(key)).Select(Notifier.Normalize).ToHashSet();
            var assigned = RuleLogic.People(after.Values.GetValueOrDefault(key))
                .Select(Notifier.Normalize)
                .OfType<string>()
                .Where(e => !previous.Contains(e) && !string.Equals(e, actor, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (assigned.Count == 0)
            {
                continue;
            }

            var request = new NotificationRequest(
                NotificationKinds.Assignment,
                $"{actor} assigned you to \"{after.Title}\"",
                $"You're now {column.Name} on \"{after.Title}\" in {sheetName}.",
                sheetId,
                after.Id);
            if (SheetAutomationBatch.Current is { } batch)
            {
                // A batch (an import) sends one "assigned to N rows" notice per person and column at the end.
                batch.Notify(column.Id, $"Assigned to you as {column.Name}", sheetId, sheetName, assigned, request, after.Title);
                continue;
            }

            await notifier.NotifyAsync(assigned, request);
        }
    }
}
