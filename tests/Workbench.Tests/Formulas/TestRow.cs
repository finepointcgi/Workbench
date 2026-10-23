using Workbench.Domain.Formulas;

namespace Workbench.Tests.Formulas;

/// <summary>A hand-built row for evaluator tests.</summary>
internal sealed class TestRow : IFormulaContext
{
    public static readonly DateOnly DefaultToday = new(2026, 10, 14); // a Wednesday

    public Dictionary<string, FormulaValue> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<FormulaValue>> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public DateOnly Today { get; init; } = DefaultToday;

    /// <summary>Column ranges by "From:To" (case-insensitive), e.g. "Status:Status".</summary>
    public Dictionary<string, FormulaValue> Ranges { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cross-sheet references by name.</summary>
    public Dictionary<string, FormulaValue> References { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FormulaValue GetColumnRange(string from, string to) =>
        Ranges.TryGetValue($"{from}:{to}", out var value) ? value : FormulaValue.FromError(FormulaErrors.Ref);

    public FormulaValue GetSheetReference(string name) =>
        References.TryGetValue(name, out var value) ? value : FormulaValue.FromError(FormulaErrors.Ref);

    public TestRow WithRange(string column, params FormulaValue[] values)
    {
        Ranges[$"{column}:{column}"] = FormulaValue.FromList(values);
        return this;
    }

    public FormulaValue GetValue(string name) =>
        Values.TryGetValue(name, out var value) ? value : FormulaValue.FromError(FormulaErrors.Ref);

    public FormulaValue GetChildValues(string name) =>
        Children.TryGetValue(name, out var values) ? FormulaValue.FromList(values)
        : Values.ContainsKey(name) ? FormulaValue.FromList([])
        : FormulaValue.FromError(FormulaErrors.Ref);

    public TestRow With(string name, FormulaValue value)
    {
        Values[name] = value;
        return this;
    }

    public TestRow WithChildren(string name, params FormulaValue[] values)
    {
        Children[name] = values.ToList();
        Values.TryAdd(name, FormulaValue.Blank);
        return this;
    }

    /// <summary>Evaluates and formats as a stored cell string (null for blank).</summary>
    public string? Eval(string formula) => Formula.Parse(formula).Evaluate(this).ToCellString();

    public static string? Evaluate(string formula) => new TestRow().Eval(formula);

    public static FormulaValue N(double value) => FormulaValue.FromNumber(value);

    public static FormulaValue T(string value) => FormulaValue.FromText(value);

    public static FormulaValue D(int year, int month, int day) => FormulaValue.FromDate(new DateOnly(year, month, day));

    public static FormulaValue B(bool value) => FormulaValue.FromBoolean(value);
}
