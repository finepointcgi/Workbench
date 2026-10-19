using Microsoft.JSInterop;
using MudBlazor;
using Workbench.Contracts.Imports;
using Workbench.Contracts.Migration;

namespace Workbench.Ui.Setup.Migration;

/// <summary>Labels and colors for the cut-over checklist and reconciliation screens.</summary>
public static class MigrationFormat
{
    public static (string Text, Color Color, string Icon) Status(MigrationStepStatus status) => status switch
    {
        MigrationStepStatus.Done => ("Done", Color.Success, Icons.Material.Filled.CheckCircle),
        MigrationStepStatus.Skipped => ("Not applicable", Color.Default, Icons.Material.Filled.DoNotDisturbOn),
        MigrationStepStatus.InProgress => ("In progress", Color.Info, Icons.Material.Filled.Autorenew),
        MigrationStepStatus.Failed => ("Failed", Color.Error, Icons.Material.Filled.Error),
        MigrationStepStatus.Blocked => ("Waiting on earlier steps", Color.Warning, Icons.Material.Filled.Lock),
        _ => ("Not started", Color.Default, Icons.Material.Filled.RadioButtonUnchecked),
    };

    public static string Job(ImportJobStatus? status) => status switch
    {
        ImportJobStatus.Imported => "Imported",
        ImportJobStatus.RolledBack => "Rolled back",
        ImportJobStatus.Failed => "Failed",
        ImportJobStatus.DryRunDone => "Dry run done",
        ImportJobStatus.Validated => "Validated",
        ImportJobStatus.Mapped => "Mapped",
        ImportJobStatus.Uploaded => "Uploaded",
        _ => "Not imported",
    };

    public static Color JobColor(ImportJobStatus? status) => status switch
    {
        ImportJobStatus.Imported => Color.Success,
        ImportJobStatus.Failed => Color.Error,
        ImportJobStatus.RolledBack => Color.Warning,
        null => Color.Default,
        _ => Color.Info,
    };

    public static (string Text, Color Color) Row(ReconRowStatus status) => status switch
    {
        ReconRowStatus.Matched => ("Matched", Color.Success),
        ReconRowStatus.Different => ("Different", Color.Error),
        ReconRowStatus.OnlyInCostpoint => ("Only in Costpoint", Color.Warning),
        _ => ("Only in Workbench", Color.Warning),
    };

    public static string Amount(decimal value, ReconValueFormat format) =>
        format == ReconValueFormat.Rate ? value.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) : value.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);

    public static string When(DateTime utc) => $"{utc.ToLocalTime():yyyy-MM-dd HH:mm}";

    public static async Task DownloadAsync(IJSRuntime js, ReconExportFile file)
    {
        await using var module = await js.InvokeAsync<IJSObjectReference>("import", "./_content/Workbench.Ui/reports.js");
        await module.InvokeVoidAsync("downloadBase64", file.FileName, file.ContentType, Convert.ToBase64String(file.Content));
    }
}
