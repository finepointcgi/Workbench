using MudBlazor;
using Workbench.Contracts.Imports;

namespace Workbench.Ui.Setup.Import;

/// <summary>Labels and colors for the import screens.</summary>
public static class ImportFormat
{
    public static string Label(ImportSource source) => source switch
    {
        ImportSource.Costpoint => "Costpoint",
        ImportSource.SmartsheetApi => "Smartsheet (API)",
        ImportSource.SmartsheetExcel => "Smartsheet (.xlsx)",
        _ => source.ToString(),
    };

    public static string Label(ImportJobStatus status) => status switch
    {
        ImportJobStatus.DryRunDone => "Dry run done",
        ImportJobStatus.RolledBack => "Rolled back",
        _ => status.ToString(),
    };

    public static Color Color(ImportJobStatus status) => status switch
    {
        ImportJobStatus.Imported => MudBlazor.Color.Success,
        ImportJobStatus.Failed => MudBlazor.Color.Error,
        ImportJobStatus.RolledBack => MudBlazor.Color.Warning,
        ImportJobStatus.DryRunDone or ImportJobStatus.Validated => MudBlazor.Color.Info,
        _ => MudBlazor.Color.Default,
    };

    public static Color Color(ImportRowAction action) => action switch
    {
        ImportRowAction.Create => MudBlazor.Color.Success,
        ImportRowAction.Update => MudBlazor.Color.Info,
        _ => MudBlazor.Color.Default,
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };

    /// <summary>The wizard step a job resumes at (0 source … 6 import).</summary>
    public static int StepFor(ImportJobStatus status) => status switch
    {
        ImportJobStatus.Uploaded => 3,
        ImportJobStatus.Mapped or ImportJobStatus.Validated => 4,
        ImportJobStatus.DryRunDone => 5,
        _ => 6,
    };
}
