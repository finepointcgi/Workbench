using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.JSInterop;
using Workbench.Contracts;
using Workbench.Contracts.Reports;

namespace Workbench.Ui.Reports;

/// <summary>Display, download and URL helpers for the report screens.</summary>
public static class ReportFormat
{
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A cell as shown on screen: money to the cent with negatives in parentheses, rates and percents as %.</summary>
    public static string Display(ReportColumnType type, string? value)
    {
        if (value is null)
        {
            return "";
        }

        var number = decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : (decimal?)null;
        return type switch
        {
            ReportColumnType.Money when number is { } m => m.ToString("#,##0.00;(#,##0.00);0.00", Usd),
            ReportColumnType.Hours when number is { } h => h.ToString("#,##0.00", Usd),
            ReportColumnType.Rate when number is { } r => r.ToString("0.00%", Usd),
            ReportColumnType.Percent when number is { } p => p.ToString("0.0%", Usd),
            ReportColumnType.Number when number is { } n => n.ToString("#,##0.####", Usd),
            ReportColumnType.Boolean => value == "true" ? "Yes" : value == "false" ? "No" : value,
            _ => value,
        };
    }

    public static bool IsNumeric(ReportColumnType type) =>
        type is ReportColumnType.Number or ReportColumnType.Money or ReportColumnType.Hours or ReportColumnType.Rate or ReportColumnType.Percent;

    public static bool IsNegative(ReportColumnType type, string? value) =>
        IsNumeric(type) && value is { Length: > 0 } && value[0] == '-';

    public static string Error(Exception ex) => ex switch
    {
        WorkbenchForbiddenException or UnauthorizedAccessException => ex.Message,
        WorkbenchNotFoundException => "That report doesn't exist or isn't shared with you.",
        ArgumentException => ex.Message,
        _ => "Something went wrong running the report. Try again.",
    };

    public static bool IsForbidden(Exception ex) => ex is UnauthorizedAccessException;

    public static async Task DownloadAsync(IJSRuntime js, ReportFileDto file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/reports.js");
        await module.InvokeVoidAsync("downloadBase64", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
    }

    public static async Task PrintAsync(IJSRuntime js)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/reports.js");
        await module.InvokeVoidAsync("printPage");
    }

    /// <summary>Encodes parameters or a definition for a URL (print views, links).</summary>
    public static string Encode<T>(T value) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static T? Decode<T>(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return default;
        }

        try
        {
            var text = value.Replace('-', '+').Replace('_', '/');
            text += new string('=', (4 - text.Length % 4) % 4);
            return JsonSerializer.Deserialize<T>(Encoding.UTF8.GetString(Convert.FromBase64String(text)), Json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return default;
        }
    }

    public static DateTime? ToDateTime(DateOnly? date) => date?.ToDateTime(TimeOnly.MinValue);

    public static DateOnly? ToDateOnly(DateTime? date) => date is { } d ? DateOnly.FromDateTime(d) : null;

    public static string Words(string pascal)
    {
        var text = new StringBuilder();
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]) && !char.IsUpper(pascal[i - 1]))
            {
                text.Append(' ').Append(char.ToLowerInvariant(pascal[i]));
            }
            else
            {
                text.Append(pascal[i]);
            }
        }

        return text.ToString();
    }
}
