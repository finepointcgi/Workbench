using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;

namespace Workbench.Ui.Integrations;

/// <summary>Shared wording, chips and browser helpers for Admin › Integrations.</summary>
internal static class IntegrationFormat
{
    /// <summary>Request/response blocks: monospace, wrapped, scrollable, theme colors.</summary>
    public const string CodeStyle = "font-family: ui-monospace, Menlo, Consolas, monospace; font-size: 0.8rem; white-space: pre-wrap; word-break: break-all; max-height: 18rem; overflow: auto; background: var(--mud-palette-background-gray); color: var(--mud-palette-text-primary); padding: 8px; border-radius: 4px; margin: 4px 0;";

    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or HttpRequestException or Workbench.Contracts.WorkbenchNotFoundException;

    public static string When(DateTime? utc) => utc is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "—";

    public static string EventLabel(string key) =>
        key == "ping" ? "Test (ping)" : WebhookEventCatalog.All.FirstOrDefault(e => e.Key == key)?.Label ?? key;

    public static string Roles(OrgRoles roles) =>
        roles == OrgRoles.Employee ? "Employee only" : string.Join(", ", OrgRoleChecks.All.Where(r => roles.HasFlag(r)).Select(OrgRoleChecks.Label));

    public static string AreaLabel(string scope)
    {
        var parts = scope.Split(':');
        var area = ApiKeyScopes.Areas.FirstOrDefault(a => a.Key == parts[0])?.Label ?? parts[0];
        return parts.Length > 1 && parts[1] == ApiKeyScopes.Write ? $"{area} (read and write)" : $"{area} (read)";
    }

    public static (string Text, Color Color, string Icon) Delivery(DeliveryState state) => state switch
    {
        DeliveryState.Succeeded => ("Delivered", Color.Success, Icons.Material.Filled.CheckCircle),
        DeliveryState.Failed => ("Failed", Color.Error, Icons.Material.Filled.Error),
        DeliveryState.Cancelled => ("Cancelled", Color.Default, Icons.Material.Filled.Block),
        _ => ("Waiting to retry", Color.Warning, Icons.Material.Filled.Schedule),
    };

    public static (string Text, Color Color, string Icon) Run(ConnectorRunState state) => state switch
    {
        ConnectorRunState.Succeeded => ("Succeeded", Color.Success, Icons.Material.Filled.CheckCircle),
        ConnectorRunState.Failed => ("Failed", Color.Error, Icons.Material.Filled.Error),
        _ => ("Skipped", Color.Default, Icons.Material.Filled.SkipNext),
    };

    public static string Schedule(ConnectorDto c) => c.Schedule switch
    {
        ConnectorScheduleKind.Daily => $"Daily at {c.RunAtHourUtc:00}:00 UTC",
        ConnectorScheduleKind.Weekly => $"Weekly on {(DayOfWeek)Math.Clamp(c.RunOnDay, 0, 6)} at {c.RunAtHourUtc:00}:00 UTC",
        ConnectorScheduleKind.Monthly => $"Monthly on day {c.RunOnDay} at {c.RunAtHourUtc:00}:00 UTC",
        ConnectorScheduleKind.Hourly => "Every hour",
        _ => "Only when you run it",
    };

    public static async Task<bool> CopyAsync(IJSRuntime js, string text)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/integrations.js");
        return await module.InvokeAsync<bool>("copyText", text);
    }

    public static async Task DownloadAsync(IJSRuntime js, ConnectorFileDto file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/integrations.js");
        await module.InvokeVoidAsync("downloadBase64", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
    }
}
