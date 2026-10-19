using System.Globalization;
using Workbench.Contracts.Planning;
using Workbench.Ui.Accounting;

namespace Workbench.Ui.Planning;

/// <summary>Display helpers for recurring journals, budgets and planning.</summary>
public static class PlanningFormat
{
    public static string Amount(decimal value) => AccountingFormat.Amount(value);

    /// <summary>Whole dollars for wide grids and totals.</summary>
    public static string Whole(decimal value) => value.ToString("#,##0;(#,##0);0", CultureInfo.GetCultureInfo("en-US"));

    public static string Percent(decimal? value) => value is { } v ? v.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";

    /// <summary>Unfavourable variances in the error colour.</summary>
    public static string VarianceClass(decimal variance) => variance < 0 ? "mud-error-text" : "";

    public static string Label(RecurrenceKind value) => value switch
    {
        RecurrenceKind.Quarterly => "Quarterly",
        RecurrenceKind.Annually => "Yearly",
        _ => "Monthly",
    };

    public static string Label(RecurringDay value) => value == RecurringDay.FirstDay ? "First day of the month" : "Last day of the month";

    public static string Label(SpreadMethod value) => value switch
    {
        SpreadMethod.PriorYearPattern => "Like last year",
        SpreadMethod.Profile => "By percentages",
        _ => "Evenly",
    };

    public static string Label(SubmissionState value) => value switch
    {
        SubmissionState.NotStarted => "Not started",
        SubmissionState.Drafting => "Drafting",
        SubmissionState.Submitted => "Waiting for approval",
        SubmissionState.Approved => "Approved",
        SubmissionState.Returned => "Returned for changes",
        _ => value.ToString(),
    };

    public static MudBlazor.Color Color(SubmissionState value) => value switch
    {
        SubmissionState.Approved => MudBlazor.Color.Success,
        SubmissionState.Submitted => MudBlazor.Color.Info,
        SubmissionState.Returned => MudBlazor.Color.Warning,
        SubmissionState.Drafting => MudBlazor.Color.Primary,
        _ => MudBlazor.Color.Default,
    };

    public static string Label(BudgetVersionKind kind) => kind.ToString();

    public static string Schedule(RecurringTemplateDto t)
    {
        var end = t.Occurrences is { } n ? $", {n} times" : t.EndDate is { } d ? $", until {AccountingFormat.Date(d)}" : "";
        var day = t.PostingDay == RecurringDay.FirstDay ? "1st" : "month end";
        return $"{Label(t.Frequency)} on the {day} from {AccountingFormat.Date(t.StartDate)}{end}";
    }

    /// <summary>Parses a typed amount (commas, $ and parentheses allowed); null when it isn't a number in whole cents.</summary>
    public static decimal? Parse(string? text)
    {
        var t = (text ?? "").Replace("$", "").Replace(",", "").Trim();
        if (t.Length == 0)
        {
            return 0;
        }

        var negative = t.StartsWith('(') && t.EndsWith(')');
        if (negative)
        {
            t = t[1..^1];
        }

        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && decimal.Round(v, 2) == v ? (negative ? -v : v) : null;
    }

    /// <summary>An amount as typed into a grid cell: blank for zero, no thousands separators.</summary>
    public static string Cell(decimal value) => value == 0 ? "" : value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>One editable grid row: an account (and, in corporate versions, an org unit) with an amount per period.</summary>
public sealed record BudgetGridLine(Guid AccountId, Guid? OrgUnitId, string Account, string? Org, bool IsRevenue, decimal[] Amounts, decimal? PriorYear = null)
{
    public string Key => $"{AccountId}:{OrgUnitId}";

    public decimal Total => Amounts.Sum();
}
