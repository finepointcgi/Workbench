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
    /// <summary>
    /// The next employee number in the pattern the company already uses ("E1005" then "E1006"): the prefix and width of the highest
    /// numbered one, one higher. Empty when none follow a pattern, so the person types the first.
    /// </summary>
    public static string NextEmployeeNumber(IEnumerable<string> existing)
    {
        var parsed = existing
            .Select(n => System.Text.RegularExpressions.Regex.Match(n ?? "", @"^(\D*)(\d+)$"))
            .Where(m => m.Success && m.Groups[2].Value.Length <= 9)
            .Select(m => (Prefix: m.Groups[1].Value, Width: m.Groups[2].Value.Length, Number: long.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        if (parsed.Count == 0)
        {
            return "";
        }

        var top = parsed.OrderByDescending(p => p.Number).First();
        var used = parsed.Where(p => p.Prefix == top.Prefix).Select(p => p.Number).ToHashSet();
        var next = top.Number + 1;
        while (used.Contains(next))
        {
            next++;
        }

        return top.Prefix + next.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(top.Width, '0');
    }

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
