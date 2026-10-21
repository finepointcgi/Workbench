using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts.Customization;

namespace Workbench.Ui.Customization;

public static class CustomizationFormat
{
    public static string TypeName(CustomFieldType type) => type switch
    {
        CustomFieldType.Text => "Text",
        CustomFieldType.LongText => "Long text",
        CustomFieldType.Number => "Number",
        CustomFieldType.Currency => "Currency",
        CustomFieldType.Date => "Date",
        CustomFieldType.YesNo => "Yes / no",
        CustomFieldType.Picklist => "Picklist",
        CustomFieldType.MultiPicklist => "Multi-select picklist",
        CustomFieldType.Person => "Person",
        CustomFieldType.RecordLink => "Link to a record",
        CustomFieldType.Url => "Web address",
        _ => type.ToString(),
    };

    public static string TypeIcon(CustomFieldType type) => type switch
    {
        CustomFieldType.Text => Icons.Material.Filled.ShortText,
        CustomFieldType.LongText => Icons.Material.Filled.Notes,
        CustomFieldType.Number => Icons.Material.Filled.Numbers,
        CustomFieldType.Currency => Icons.Material.Filled.AttachMoney,
        CustomFieldType.Date => Icons.Material.Filled.Event,
        CustomFieldType.YesNo => Icons.Material.Filled.ToggleOn,
        CustomFieldType.Picklist => Icons.Material.Filled.ArrowDropDownCircle,
        CustomFieldType.MultiPicklist => Icons.Material.Filled.Checklist,
        CustomFieldType.Person => Icons.Material.Filled.Person,
        CustomFieldType.RecordLink => Icons.Material.Filled.Link,
        CustomFieldType.Url => Icons.Material.Filled.Language,
        _ => Icons.Material.Filled.Label,
    };

    public static async Task DownloadAsync(IJSRuntime js, CustomFieldExportDto file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/integrations.js");
        await module.InvokeVoidAsync("downloadBase64", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
    }
}

/// <summary>
/// Administrator labels for built-in statuses, loaded once by a page. Without the customization service (component
/// tests) every label falls back to the page's own text.
/// </summary>
public sealed class StatusLabels
{
    private readonly Dictionary<(string, string), string> _labels = [];

    public static StatusLabels Empty { get; } = new();

    public static async Task<StatusLabels> LoadAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var labels = new StatusLabels();
        if (services.GetService<ICustomListService>() is not { } lists)
        {
            return labels;
        }

        try
        {
            foreach (var set in await lists.ListStatusSetsAsync(cancellationToken))
            {
                foreach (var v in set.Values.Where(v => v.Label != v.DefaultLabel))
                {
                    labels._labels[(set.Key, v.Value)] = v.Label;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or UnauthorizedAccessException or InvalidOperationException)
        {
        }

        return labels;
    }

    /// <summary>The administrator's label, else <paramref name="fallback"/> (the page's built-in text).</summary>
    public string Label(string setKey, object value, string fallback) =>
        _labels.TryGetValue((setKey, value.ToString() ?? ""), out var label) ? label : fallback;
}
