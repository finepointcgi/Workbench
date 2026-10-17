namespace Workbench.Domain.Formulas;

/// <summary>A parsed formula with the column names it reads.</summary>
public sealed class Formula
{
    /// <summary>Item fields every formula can reference, in addition to the sheet's columns.</summary>
    public static IReadOnlyList<string> BuiltInReferences { get; } = ["Title", "Start", "Finish"];

    private Formula(string text, FormulaNode root, IReadOnlySet<string> references, IReadOnlySet<string> childReferences)
    {
        Text = text;
        Root = root;
        References = references;
        ChildReferences = childReferences;
    }

    public string Text { get; }

    public FormulaNode Root { get; }

    /// <summary>Names read from the current row (<c>[Name]</c>), case-insensitive.</summary>
    public IReadOnlySet<string> References { get; }

    /// <summary>Names read from the item's children (<c>CHILDREN([Name])</c>), case-insensitive.</summary>
    public IReadOnlySet<string> ChildReferences { get; }

    /// <summary>Parses formula text; throws <see cref="FormulaParseException"/> on syntax errors.</summary>
    public static Formula Parse(string text)
    {
        var root = FormulaParser.Parse(text);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var childReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(root, references, childReferences);
        return new Formula(text, root, references, childReferences);
    }

    public FormulaValue Evaluate(IFormulaContext context) => FormulaEvaluator.Evaluate(Root, context);

    /// <summary>
    /// Checks syntax, function names, argument counts and column references, and returns the first problem
    /// as a user-facing message, or null when the formula is valid. Circular references aren't checked here;
    /// they show as #CIRCULAR! when the sheet is calculated.
    /// </summary>
    public static string? Validate(string text, IEnumerable<string> columnNames)
    {
        Formula formula;
        try
        {
            formula = Parse(text);
        }
        catch (FormulaParseException ex)
        {
            return ex.Message;
        }

        if (FindFunctionProblem(formula.Root) is { } problem)
        {
            return problem;
        }

        var known = new HashSet<string>(columnNames, StringComparer.OrdinalIgnoreCase);
        known.UnionWith(BuiltInReferences);
        var unknown = formula.References.Concat(formula.ChildReferences).FirstOrDefault(n => !known.Contains(n));
        return unknown is null ? null : $"There is no column named [{unknown}].";
    }

    private static string? FindFunctionProblem(FormulaNode node) => node switch
    {
        FunctionNode f when !FormulaFunctions.IsKnown(f.Name) =>
            $"Unknown function {f.Name} at position {f.Position + 1}. Available functions: {string.Join(", ", FormulaFunctions.Names.Order())}.",
        FunctionNode f when FormulaFunctions.CheckArity(f.Name, f.Arguments.Count) is { } message => message,
        FunctionNode f => f.Arguments.Select(FindFunctionProblem).FirstOrDefault(p => p is not null),
        UnaryNode u => FindFunctionProblem(u.Operand),
        BinaryNode b => FindFunctionProblem(b.Left) ?? FindFunctionProblem(b.Right),
        _ => null,
    };

    private static void Collect(FormulaNode node, HashSet<string> references, HashSet<string> childReferences)
    {
        switch (node)
        {
            case ReferenceNode r:
                references.Add(r.Name);
                break;
            case FunctionNode { Name: FormulaFunctions.Children, Arguments: [ReferenceNode child] }:
                childReferences.Add(child.Name);
                break;
            case FunctionNode f:
                foreach (var argument in f.Arguments)
                {
                    Collect(argument, references, childReferences);
                }

                break;
            case UnaryNode u:
                Collect(u.Operand, references, childReferences);
                break;
            case BinaryNode b:
                Collect(b.Left, references, childReferences);
                Collect(b.Right, references, childReferences);
                break;
        }
    }
}
