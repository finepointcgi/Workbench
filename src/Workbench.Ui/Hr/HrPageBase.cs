using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts;

namespace Workbench.Ui.Hr;

/// <summary>Loading, error and busy handling shared by the HR pages.</summary>
public abstract class HrPageBase : ComponentBase
{
    [Inject] protected ISnackbar Snackbar { get; set; } = default!;

    [Inject] protected IJSRuntime JS { get; set; } = default!;

    protected bool Loading { get; set; } = true;

    protected bool Busy { get; set; }

    protected bool Forbidden { get; set; }

    protected string? Error { get; set; }

    /// <summary>Runs the page's first load; a refusal shows the friendly 403, other expected errors a warning.</summary>
    protected async Task LoadAsync(Func<Task> load)
    {
        try
        {
            Error = null;
            await load();
        }
        catch (Exception ex) when (HrFormat.IsExpected(ex))
        {
            (Error, Forbidden) = (ex.Message, ex is UnauthorizedAccessException);
        }

        Loading = false;
    }

    /// <summary>Runs an action, showing its error (or <paramref name="success"/>) in a snackbar. Returns whether it worked.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? success = null)
    {
        Busy = true;
        try
        {
            await action();
            if (success is not null)
            {
                Snackbar.Add(success, Severity.Success);
            }

            return true;
        }
        catch (Exception ex) when (HrFormat.IsExpected(ex))
        {
            Snackbar.Add(ex.Message, Severity.Error);
            return false;
        }
        finally
        {
            Busy = false;
        }
    }

    protected async Task DownloadAsync(string fileName, string contentType, string content)
    {
        await using var module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/financials.js");
        await module.InvokeVoidAsync("downloadText", fileName, contentType, content);
    }
}

internal static class HrFormat
{
    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException
            or NotSupportedException or WorkbenchNotFoundException;

    public static string Date(DateOnly? date) => date is { } d ? d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "";

    public static string Money(decimal? value) => value is { } v ? v.ToString("N2", CultureInfo.InvariantCulture) : "";

    public static string Whole(decimal? value) => value is { } v ? v.ToString("N0", CultureInfo.InvariantCulture) : "";

    public static string Percent(decimal? fraction) => fraction is { } f ? f.ToString("P1", CultureInfo.InvariantCulture) : "";

    public static DateOnly? ToDate(DateTime? value) => value is { } v ? DateOnly.FromDateTime(v) : null;

    public static DateTime? ToDateTime(DateOnly? value) => value?.ToDateTime(TimeOnly.MinValue);

    public static Color Color(Contracts.Hr.EmploymentStatus s) => s switch
    {
        Contracts.Hr.EmploymentStatus.Active => MudBlazor.Color.Success,
        Contracts.Hr.EmploymentStatus.Terminated => MudBlazor.Color.Default,
        _ => MudBlazor.Color.Info,
    };

    /// <summary>"Expired", "Expires in 12 days" or the date.</summary>
    public static string Expiry(int? days, DateOnly? on) => days switch
    {
        null => "No expiry",
        < 0 => $"Expired {Date(on)}",
        0 => "Expires today",
        <= 60 => $"Expires in {days} days ({Date(on)})",
        _ => Date(on),
    };
}
