using MudBlazor;
using Workbench.Contracts;
using Workbench.Contracts.Resourcing;

namespace Workbench.Ui.Resourcing;

public static class ResourcingFormat
{
    public static string Hours(decimal hours) => hours.ToString(hours == Math.Round(hours) ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);

    public static string Percent(decimal? ratio) => ratio is { } r ? (r * 100).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + "%" : "—";

    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateOnly ThisWeek => WeekStart(DateOnly.FromDateTime(DateTime.Today));

    /// <summary>Cell background for a load status (translucent, so it reads in light and dark themes).</summary>
    public static string CellStyle(LoadStatus status) => status switch
    {
        LoadStatus.Over => "background: color-mix(in srgb, var(--mud-palette-error) 32%, transparent);",
        LoadStatus.Full => "background: color-mix(in srgb, var(--mud-palette-success) 28%, transparent);",
        LoadStatus.Under => "background: color-mix(in srgb, var(--mud-palette-info) 14%, transparent);",
        _ => "",
    };

    public static string Label(LoadStatus status) => status switch
    {
        LoadStatus.Over => "Over",
        LoadStatus.Full => "Full",
        LoadStatus.Under => "Under",
        _ => "No capacity",
    };

    public static (string Text, Color Color) Gap(StaffingGapStatus status) => status switch
    {
        StaffingGapStatus.Understaffed => ("Understaffed", Color.Warning),
        StaffingGapStatus.Overstaffed => ("Over budget", Color.Error),
        StaffingGapStatus.NotBudgeted => ("Not budgeted", Color.Error),
        _ => ("Balanced", Color.Success),
    };

    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
            or WorkbenchNotFoundException;
}
