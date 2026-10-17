using Workbench.Contracts.Collaboration;
using Workbench.Domain.Collaboration;

namespace Workbench.Application.Collaboration;

/// <summary>Writes an activity entry and publishes the matching live change. Call it after the change is saved.</summary>
public sealed class ActivityRecorder(IWorkbenchDbContextFactory dbFactory, ISheetChangePublisher publisher, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task RecordAsync(Guid sheetId, Guid? itemId, string kind, string summary, SheetChangeKind change, Guid? columnId = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        // The change is already saved, so don't let a cancelled request skip its activity entry.
        await using (var db = await dbFactory.CreateAsync(CancellationToken.None))
        {
            db.Set<ActivityEvent>().Add(new ActivityEvent
            {
                SheetId = sheetId,
                ItemId = itemId,
                Actor = currentUser.Email,
                Kind = kind,
                Summary = summary.Length > 2_000 ? summary[..1_999] + "…" : summary,
                OccurredAtUtc = now,
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }

        await publisher.PublishAsync(new SheetChange(sheetId, change, itemId, columnId, currentUser.Email, now));
    }
}
