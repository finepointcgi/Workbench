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
        ImportSource.Unanet => "Unanet",
        _ => source.ToString(),
    };

    /// <summary>What an import's records are called in a sentence ("12 people").</summary>
    public static string Noun(ImportKind kind) => kind switch
    {
        ImportKind.OrgUnits => "organizations",
        ImportKind.LaborCategories => "labor categories",
        ImportKind.Employees => "people",
        ImportKind.CostRates => "cost rates",
        ImportKind.Customers => "customers",
        ImportKind.Contracts => "contracts",
        ImportKind.Clins => "contract lines",
        ImportKind.Projects => "projects and tasks",
        ImportKind.BillRates => "bill rates",
        ImportKind.Budgets => "budget versions",
        ImportKind.Assignments => "assignments",
        ImportKind.Timesheets => "timesheets",
        ImportKind.Vendors => "vendors",
        ImportKind.OpenApBills => "AP invoices",
        ImportKind.OpenArInvoices => "AR invoices",
        _ => "records",
    };

    /// <summary>The dry run or finished import in one plain sentence: what gets added, changed and left alone.</summary>
    public static string PlainWords(ImportKind kind, ImportCountsDto counts, bool imported, IReadOnlyList<ImportPlanRowDto>? plan = null)
    {
        var errors = counts.Errors == 0 ? "" : $" {counts.Errors} row{(counts.Errors == 1 ? "" : "s")} {(imported ? "had" : "have")} problems and {(imported ? "were" : "will be")} left out.";
        var listed = plan is not null && plan.Count == counts.Creates + counts.Updates + counts.Skips;
        string Things(ImportRowAction action, int total) => listed
            ? Join(plan!.Where(p => p.Action == action).GroupBy(p => p.EntityType).Select(g => $"{g.Count()} {Plural(g.Key, g.Count())}").ToList())
            : $"{total} {Noun(kind)}";
        var added = counts.Creates == 0 ? "nothing" : Things(ImportRowAction.Create, counts.Creates);
        var changed = counts.Updates == 0 ? "nothing" : Things(ImportRowAction.Update, counts.Updates);
        var left = counts.Skips == 0 ? "" : imported
            ? $" Left {counts.Skips} as they were."
            : $" Leave {counts.Skips} as they are.";
        return imported
            ? $"Added {added}; changed {changed}.{left}{errors}"
            : $"This import will add {added} and change {changed}.{left}{errors}";
    }

    private static string Join(IReadOnlyList<string> parts) => parts.Count switch
    {
        0 => "nothing",
        1 => parts[0],
        _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
    };

    private static string Plural(string type, int count)
    {
        var word = type.Length > 1 && char.IsUpper(type[1]) ? type : char.ToLowerInvariant(type[0]) + type[1..];
        if (count == 1)
        {
            return word;
        }

        return word.EndsWith('y') ? word[..^1] + "ies" : word + "s";
    }

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
