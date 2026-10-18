using System.Globalization;
using MudBlazor;
using Workbench.Contracts;
using Workbench.Contracts.PerDiem;

namespace Workbench.Ui.PerDiem;

public static class PerDiemFormat
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal value) => value.ToString("C2", Us);

    public static string Season(PerDiemRateDto r) =>
        r.SeasonStart.Month == 10 && r.SeasonStart.Day == 1 && r.SeasonEnd.Month == 9 && r.SeasonEnd.Day == 30
            ? "All year"
            : $"{r.SeasonStart:MMM d} – {r.SeasonEnd:MMM d}";

    public static int FiscalYearOf(DateOnly date) => date.Month >= 10 ? date.Year + 1 : date.Year;

    public static (string Text, Color Color) Status(PerDiemAmountStatus status) => status switch
    {
        PerDiemAmountStatus.RequiresJustification => ("Requires justification", Color.Warning),
        PerDiemAmountStatus.Justified => ("Justified", Color.Info),
        PerDiemAmountStatus.Unallowable => ("Unallowable", Color.Error),
        _ => ("Within per diem", Color.Success),
    };

    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or IOException
            or TaskCanceledException or WorkbenchNotFoundException;
}
