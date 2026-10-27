using Workbench.Contracts.Time;

namespace Workbench.Ui.Time;

/// <summary>Helpers for the timesheet's "Fill from my staffing".</summary>
public static class TimeFill
{
    /// <summary>
    /// Trims the hours to fill so no charge code goes past the room left under its hours ceiling (in quarter hours, earliest days first).
    /// <paramref name="stopped"/> collects the codes that were cut short, by <paramref name="codeOf"/>.
    /// </summary>
    public static List<(Guid WbsElementId, DateOnly Date, decimal Hours)> CapAtCeilings(
        IEnumerable<(Guid WbsElementId, DateOnly Date, decimal Hours)> hours, IReadOnlyList<ChargeCeilingRoomDto> room, List<string> stopped, Func<Guid, string> codeOf)
    {
        var left = room.ToDictionary(r => r.WbsElementId, r => r.HoursLeft);
        var result = new List<(Guid, DateOnly, decimal)>();
        foreach (var (wbs, date, value) in hours.OrderBy(h => h.Date))
        {
            if (!left.TryGetValue(wbs, out var available))
            {
                result.Add((wbs, date, value));
                continue;
            }

            var take = Math.Min(value, Math.Max(0, Math.Floor(available * 4) / 4));
            left[wbs] = available - take;
            if (take < value && !stopped.Contains(codeOf(wbs)))
            {
                stopped.Add(codeOf(wbs));
            }

            if (take > 0)
            {
                result.Add((wbs, date, take));
            }
        }

        return result;
    }
}
