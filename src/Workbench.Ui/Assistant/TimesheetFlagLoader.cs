using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Assistant;
using Workbench.Ui.Navigation;

namespace Workbench.Ui.Assistant;

/// <summary>Loads flags for a page's timesheets in one call; empty (never an error) when the service isn't there or refuses.</summary>
public static class TimesheetFlagLoader
{
    public static async Task<IReadOnlyDictionary<Guid, TimesheetFlagsDto>> LoadAsync(IServiceProvider services, IEnumerable<Guid> timesheetIds)
    {
        var ids = timesheetIds.Distinct().ToList();
        if (ids.Count == 0 || services.GetService<ITimesheetFlagService>() is not { } flags)
        {
            return new Dictionary<Guid, TimesheetFlagsDto>();
        }

        try
        {
            return (await flags.ListForTimesheetsAsync(ids)).ToDictionary(f => f.TimesheetId);
        }
        catch (Exception ex) when (NavAccess.IsExpected(ex))
        {
            return new Dictionary<Guid, TimesheetFlagsDto>();
        }
    }
}
