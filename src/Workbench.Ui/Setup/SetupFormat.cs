using System.Globalization;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Ui.Setup;

/// <summary>Display and conversion helpers shared by the setup screens.</summary>
public static class SetupFormat
{
    // Government contractors work in US dollars.
    private static readonly CultureInfo Usd = CultureInfo.GetCultureInfo("en-US");

    public const string Masked = "••••";

    public static string Money(decimal value) => value.ToString("C0", Usd);

    public static string Rate(decimal value) => value.ToString("C2", Usd);

    public static string Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    public static DateTime? ToDateTime(DateOnly? value) => value?.ToDateTime(TimeOnly.MinValue);

    public static DateOnly? ToDateOnly(DateTime? value) => value is { } d ? DateOnly.FromDateTime(d) : null;

    public static string Label(ContractKind kind) => kind switch
    {
        ContractKind.FirmFixedPrice => "FFP",
        ContractKind.TimeAndMaterials => "T&M",
        ContractKind.LaborHour => "LH",
        ContractKind.CostPlusFixedFee => "CPFF",
        ContractKind.CostPlusAwardFee => "CPAF",
        ContractKind.CostPlusIncentiveFee => "CPIF",
        _ => kind.ToString(),
    };

    /// <summary>The spelled-out type for pickers ("T&amp;M · Time and materials"), not the code name.</summary>
    public static string Name(ContractKind kind) => kind switch
    {
        ContractKind.FirmFixedPrice => "Firm fixed price",
        ContractKind.TimeAndMaterials => "Time and materials",
        ContractKind.LaborHour => "Labor hour",
        ContractKind.CostPlusFixedFee => "Cost plus fixed fee",
        ContractKind.CostPlusAwardFee => "Cost plus award fee",
        ContractKind.CostPlusIncentiveFee => "Cost plus incentive fee",
        _ => kind.ToString(),
    };

    public static string Label(ProjectState state) => state == ProjectState.OnHold ? "On hold" : state.ToString();

    /// <summary>The user-facing message for a failed save: the service's validation message, or a generic one.</summary>
    public static string Error(Exception ex) => ex switch
    {
        ArgumentException or InvalidOperationException or HttpRequestException => ex.Message,
        _ when ex.GetType().Name.Contains("NotFound", StringComparison.Ordinal) => ex.Message,
        _ => "Something went wrong. " + ex.Message,
    };

    /// <summary>Org units or WBS elements ordered depth-first, each with its depth.</summary>
    public static IReadOnlyList<(T Node, int Depth)> Flatten<T>(IEnumerable<T> nodes, Func<T, Guid> id, Func<T, Guid?> parent, Func<T, string> sortKey)
    {
        var all = nodes.ToList();
        var ids = all.Select(id).ToHashSet();
        var result = new List<(T, int)>();
        void Visit(Guid? parentId, int depth)
        {
            // Orphans (parent missing) show at the top level.
            foreach (var node in all.Where(n => parentId is null ? parent(n) is not { } p || !ids.Contains(p) : parent(n) == parentId)
                         .OrderBy(sortKey, StringComparer.Ordinal))
            {
                result.Add((node, depth));
                if (depth < 50)
                {
                    Visit(id(node), depth + 1);
                }
            }
        }

        Visit(null, 0);
        return result;
    }
}
