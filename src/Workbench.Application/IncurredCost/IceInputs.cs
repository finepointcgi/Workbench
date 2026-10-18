using System.Globalization;
using Workbench.Domain.IncurredCost;

namespace Workbench.Application.IncurredCost;

/// <summary>
/// The values typed into a package, looked up by schedule, row and column while it's built. Every lookup also registers
/// the cell as editable, so saving can refuse anything that isn't an input of the built package.
/// </summary>
internal sealed class IceInputs
{
    private readonly Dictionary<(string Schedule, string Row, string Column), string> _values;

    private IceInputs(Dictionary<(string, string, string), string> values) => _values = values;

    public HashSet<(string Schedule, string Row, string Column)> Editable { get; } = [];

    /// <summary>Numeric editable cells (the rest are text).</summary>
    public HashSet<(string Schedule, string Row, string Column)> Numeric { get; } = [];

    public static IceInputs Empty => new([]);

    public static IceInputs From(IEnumerable<IncurredCostInput> inputs) =>
        new(inputs.Where(i => !string.IsNullOrWhiteSpace(i.Value)).ToDictionary(i => (i.Schedule, i.RowKey, i.Column), i => i.Value!.Trim()));

    public string? Text(string schedule, string row, string column, string? fallback = null)
    {
        Editable.Add((schedule, row, column));
        return _values.TryGetValue((schedule, row, column), out var v) ? v : fallback;
    }

    public decimal Number(string schedule, string row, string column, decimal fallback = 0)
    {
        Editable.Add((schedule, row, column));
        Numeric.Add((schedule, row, column));
        return _values.TryGetValue((schedule, row, column), out var v) && TryParse(v, out var n) ? n : fallback;
    }

    /// <summary>Amounts as typed: invariant or en-US digits, optional $ and thousands separators, (parentheses) for negatives.</summary>
    public static bool TryParse(string value, out decimal result)
    {
        var s = value.Trim().Replace("$", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        var negative = s.StartsWith('(') && s.EndsWith(')');
        if (negative)
        {
            s = s[1..^1];
        }

        if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out result))
        {
            result = decimal.Round(negative ? -result : result, 2, MidpointRounding.AwayFromZero);
            return true;
        }

        return false;
    }
}
