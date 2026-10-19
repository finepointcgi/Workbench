using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.MyWork;

/// <summary>
/// Cross-sheet "assigned to me" query. It reads through <see cref="ISheetService"/> and <see cref="IItemService"/>
/// (never EF) so whatever visibility filtering those apply also applies here.
/// <para>
/// Performance: this is one sheet list, then a column lookup and a full item load per visible sheet that has a
/// Person column, filtered in memory. That's fine for tens of sheets with a few thousand items each. Beyond that,
/// push the assignee filter into the item query (an index over Person values, or an assignments table maintained on
/// item writes) and page the results.
/// </para>
/// </summary>
public sealed class MyWorkService(ISheetService sheets, IItemService items, ICurrentUser user, TimeProvider clock,
    Contracts.Tasks.ITaskAssignmentService? tasks = null) : IMyWorkService
{
    public async Task<MyWorkDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var email = user.Email;
        var found = new List<MyWorkItem>();

        // Items assigned through the Assignees field, by sheet (the service only returns sheets the user can see).
        var assigned = tasks is null ? []
            : (await tasks.ListMineAsync(includeDone: true, cancellationToken)).GroupBy(t => t.SheetId)
                .ToDictionary(g => g.Key, g => (IReadOnlySet<Guid>)g.Select(t => t.ItemId).ToHashSet());
        foreach (var summary in await sheets.ListAsync(cancellationToken))
        {
            if (summary.ItemCount == 0)
            {
                continue;
            }

            var sheet = await sheets.GetAsync(summary.Id, cancellationToken);
            var mine = assigned.GetValueOrDefault(summary.Id);
            if (sheet is null || (mine is null && !sheet.Columns.Any(c => c.Type == ColumnKind.Person)))
            {
                continue;
            }

            IReadOnlyList<ItemDto> sheetItems;
            try
            {
                sheetItems = await items.ListAsync(summary.Id, cancellationToken);
            }
            catch (WorkbenchNotFoundException)
            {
                continue; // deleted (or hidden) between the list and the load
            }

            found.AddRange(MyWorkCalculator.ItemsFor(sheet, sheetItems, email, today, mine));
        }

        return MyWorkCalculator.Summarize(email, today, found);
    }
}

public static class MyWorkRegistration
{
    public static IServiceCollection AddWorkbenchMyWork(this IServiceCollection services) =>
        services.AddScoped<IMyWorkService, MyWorkService>();
}
