using System.Globalization;
using MudBlazor;
using Workbench.Contracts.Crm;

namespace Workbench.Ui.Crm;

/// <summary>How business development pages show money, stages and dates.</summary>
public static class CrmFormat
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal value) => value.ToString("C0", Us);

    /// <summary>$950K, $12.5M, $1.2B.</summary>
    public static string Compact(decimal value) => Math.Abs(value) switch
    {
        >= 1_000_000_000m => (value / 1_000_000_000m).ToString("$0.#", Us) + "B",
        >= 1_000_000m => (value / 1_000_000m).ToString("$0.#", Us) + "M",
        >= 1_000m => (value / 1_000m).ToString("$0", Us) + "K",
        _ => value.ToString("$0", Us),
    };

    public static string Stage(OpportunityStage stage) => OpportunityStages.Label(stage);

    public static Color StageColor(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Won => Color.Success,
        OpportunityStage.Lost => Color.Error,
        OpportunityStage.NoBid => Color.Default,
        OpportunityStage.Submitted => Color.Info,
        OpportunityStage.Proposal or OpportunityStage.Capture => Color.Primary,
        _ => Color.Default,
    };

    public static string StageIcon(OpportunityStage stage) => stage switch
    {
        OpportunityStage.Won => Icons.Material.Filled.EmojiEvents,
        OpportunityStage.Lost => Icons.Material.Filled.Close,
        OpportunityStage.NoBid => Icons.Material.Filled.Block,
        OpportunityStage.Submitted => Icons.Material.Filled.Send,
        _ => Icons.Material.Filled.Flag,
    };

    /// <summary>The stage's accent class (<c>wb-stage-proposal</c>), from theme.css.</summary>
    public static string StageClass(OpportunityStage stage) => $"wb-stage-{stage.ToString().ToLowerInvariant()}";

    /// <summary>"Due today", "Due in 5 days", "Due tomorrow", "3 days late".</summary>
    public static string DueIn(DateOnly due, DateOnly today, string verb = "Due")
    {
        var days = due.DayNumber - today.DayNumber;
        var text = days switch
        {
            0 => $"{verb} today",
            1 => $"{verb} tomorrow",
            > 1 => $"{verb} in {days} days",
            -1 => "1 day late",
            _ => $"{-days} days late",
        };
        text = text.Trim();
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>"Blake Ortiz" → "BO"; an email's name part when there's no display name.</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        var text = name.Contains('@') ? name[..name.IndexOf('@')].Replace('.', ' ') : name;
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant(),
            _ => $"{char.ToUpperInvariant(parts[0][0])}{char.ToUpperInvariant(parts[^1][0])}",
        };
    }

    public static string Date(DateOnly? date) => date is { } d ? d.ToString("MMM d, yyyy", Us) : "—";

    public static string Percent(int value) => $"{value}%";

    public static string Kind(OpportunityKind kind) => kind == OpportunityKind.Recompete ? "Recompete" : "New";

    public static string AccountKind(CrmAccountKind kind) => kind switch
    {
        CrmAccountKind.Office => "Government office",
        CrmAccountKind.Agency => "Government agency",
        _ => kind.ToString(),
    };

    public static string ContractKind(Workbench.Contracts.ProjectAccounting.ContractKind kind) => kind switch
    {
        Workbench.Contracts.ProjectAccounting.ContractKind.FirmFixedPrice => "Firm fixed price",
        Workbench.Contracts.ProjectAccounting.ContractKind.TimeAndMaterials => "Time and materials",
        Workbench.Contracts.ProjectAccounting.ContractKind.LaborHour => "Labor hour",
        Workbench.Contracts.ProjectAccounting.ContractKind.CostPlusFixedFee => "Cost plus fixed fee",
        Workbench.Contracts.ProjectAccounting.ContractKind.CostPlusAwardFee => "Cost plus award fee",
        _ => "Cost plus incentive fee",
    };

    /// <summary>The request that saves an opportunity as it is (edit pages change a copy of it).</summary>
    public static SaveOpportunityRequest ToRequest(OpportunityDto o) => new(
        o.Name, o.Description, o.SolicitationNumber, o.AgencyAccountId, o.AgencyAccountId is null ? o.AgencyName : null, o.NaicsCode, o.SetAside,
        o.ContractVehicle, o.ContractType, o.Kind, o.EstimatedValue, o.EstimatedAwardDate, o.PeriodOfPerformanceMonths, o.RfiDueOn, o.RfpReleaseOn,
        o.ProposalDueOn, o.PlaceOfPerformance, o.IncumbentAccountId, o.IncumbentAccountId is null ? o.IncumbentName : null, o.CaptureManagerEmail,
        o.ProposalManagerEmail, o.Pwin, o.Pgo,
        o.Teammates.Select(t => new SaveTeammate(t.AccountId, t.Name, t.Role, t.WorksharePercent, t.Notes)).ToList(),
        o.Competitors.Select(c => new SaveCompetitor(c.AccountId, c.Name, c.IsIncumbent, c.Notes)).ToList(),
        o.SourceUrl);

    public static DateTime? ToDateTime(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue);

    public static DateOnly? ToDateOnly(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    /// <summary>A failure the page shows as a message (the server refused or the input was invalid).</summary>
    public static bool IsExpected(Exception ex) =>
        ex is ArgumentException or UnauthorizedAccessException or Workbench.Contracts.WorkbenchNotFoundException or InvalidOperationException or HttpRequestException;
}
