using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Collaboration;

namespace Workbench.Application.Collaboration;

public sealed class ActivityService(IWorkbenchDbContextFactory dbFactory) : IActivityService
{
    public const int MaxLimit = 500;

    public async Task<IReadOnlyList<ActivityDto>> ListAsync(Guid sheetId, Guid? itemId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<ActivityEvent>().AsNoTracking().Where(a => a.SheetId == sheetId);
        if (itemId is { } id)
        {
            query = query.Where(a => a.ItemId == id);
        }

        return await query
            .OrderByDescending(a => a.OccurredAtUtc)
            .ThenByDescending(a => a.Id)
            .Take(Math.Clamp(limit, 1, MaxLimit))
            .Select(a => new ActivityDto(a.Id, a.SheetId, a.ItemId, a.Actor, a.Kind, a.Summary, a.OccurredAtUtc))
            .ToListAsync(cancellationToken);
    }
}
