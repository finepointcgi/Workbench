using System.Globalization;
using Microsoft.JSInterop;
using Workbench.Contracts;
using Workbench.Contracts.Payroll;

namespace Workbench.Ui.Payroll;

internal static class PayrollFormat
{
    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
            or NotSupportedException or WorkbenchNotFoundException;

    public static bool IsForbidden(Exception ex) => ex is UnauthorizedAccessException;

    public static string Error(Exception ex) => ex.Message;

    public static string Money(decimal value) => value.ToString("N2", CultureInfo.InvariantCulture);

    public static string Money(decimal? value) => value is { } v ? Money(v) : "";

    public static string Hours(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Date(DateOnly date) => date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    public static string Range(DateOnly start, DateOnly end) =>
        $"{start.ToString("MMM d", CultureInfo.InvariantCulture)} – {end.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)}";

    public static string Label(PayrollRunState s) => s switch
    {
        PayrollRunState.RegisterImported => "Register imported",
        _ => s.ToString(),
    };

    public static MudBlazor.Color Color(PayrollRunState s) => s switch
    {
        PayrollRunState.Posted => MudBlazor.Color.Success,
        PayrollRunState.Voided => MudBlazor.Color.Default,
        PayrollRunState.Draft => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Info,
    };

    public static string Label(PayFrequencyKind f) => f switch
    {
        PayFrequencyKind.SemiMonthly => "Semi-monthly",
        _ => f.ToString(),
    };

    public static string Label(EmploymentTypeKind t) => t switch
    {
        EmploymentTypeKind.FullTime => "Full-time",
        EmploymentTypeKind.PartTime => "Part-time",
        _ => t.ToString(),
    };

    public static async Task DownloadAsync(IJSRuntime js, string fileName, string contentType, string content)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/financials.js");
        await module.InvokeVoidAsync("downloadText", fileName, contentType, content);
    }
}
