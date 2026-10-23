using System.Text;

namespace Workbench.Domain.Formulas;

/// <summary>A parsed formula with the column names it reads.</summary>
public sealed class Formula
{
    /// <summary>Item fields every formula can reference, in addition to the sheet's columns.</summary>
    public static IReadOnlyList<string> BuiltInReferences { get; } = ["Title", "Start", "Finish"];

    private Formula(string text, FormulaNode root, IReadOnlySet<string> references, IReadOnlySet<string> childReferences,
        IReadOnlyList<(string From, string To)> ranges, IReadOnlySet<string> sheetReferences)
    {
        Text = text;
        Root = root;
        References = references;
        ChildReferences = childReferences;
        ColumnRanges = ranges;
        SheetReferences = sheetReferences;
    }

    public string Text { get; }

    public FormulaNode Root { get; }

    /// <summary>Names read from the current row (<c>[Name]</c>), case-insensitive.</summary>
    public IReadOnlySet<string> References { get; }

    /// <summary>Names read from the item's children (<c>CHILDREN([Name])</c>), case-insensitive.</summary>
    public IReadOnlySet<string> ChildReferences { get; }

    /// <summary>Column ranges read from every row (<c>[From]:[To]</c>).</summary>
    public IReadOnlyList<(string From, string To)> ColumnRanges { get; }

    /// <summary>Names of the cross-sheet references read (<c>{Name}</c>), case-insensitive.</summary>
    public IReadOnlySet<string> SheetReferences { get; }

    /// <summary>Every column name the formula mentions: on this row, in children, and at either end of a range.</summary>
    public IEnumerable<string> AllColumnNames =>
        References.Concat(ChildReferences).Concat(ColumnRanges.SelectMany(r => new[] { r.From, r.To }));

    /// <summary>Parses formula text; throws <see cref="FormulaParseException"/> on syntax errors.</summary>
    public static Formula Parse(string text)
    {
        var root = FormulaParser.Parse(text);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var childReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranges = new List<(string, string)>();
        var sheetReferences = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(root, references, childReferences, ranges, sheetReferences);
        return new Formula(text, root, references, childReferences, ranges, sheetReferences);
    }

    public FormulaValue Evaluate(IFormulaContext context) => FormulaEvaluator.Evaluate(Root, context);

    /// <summary>
    /// Checks syntax, function names, argument counts and column references, and returns the first problem
    /// as a user-facing message, or null when the formula is valid. Circular references aren't checked here;
    /// they show as #CIRCULAR! when the sheet is calculated.
    /// </summary>
    public static string? Validate(string text, IEnumerable<string> columnNames) => Validate(text, columnNames, null);

    /// <summary>
    /// Like <see cref="Validate(string, IEnumerable{string})"/>, also checking <c>{Name}</c> cross-sheet references against
    /// <paramref name="sheetReferenceNames"/> (null skips that check).
    /// </summary>
    public static string? Validate(string text, IEnumerable<string> columnNames, IEnumerable<string>? sheetReferenceNames)
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
        if (formula.AllColumnNames.FirstOrDefault(n => !known.Contains(n)) is { } unknown)
        {
            return $"There is no column named [{unknown}].{DidYouMean(unknown, known, n => $"[{n}]")}";
        }

        if (FindCellProblem(formula.Root, insideCriterion: false) is { } cellProblem)
        {
            return cellProblem;
        }

        if (sheetReferenceNames is not null)
        {
            var references = new HashSet<string>(sheetReferenceNames, StringComparer.OrdinalIgnoreCase);
            if (formula.SheetReferences.FirstOrDefault(n => !references.Contains(n)) is { } missing)
            {
                return $"There is no cross-sheet reference named {{{missing}}}.{DidYouMean(missing, references, n => $"{{{n}}}")} "
                    + "Use \"Reference another sheet\" to pick the sheet and columns, and give the reference that name.";
            }
        }

        return null;
    }

    /// <summary>
    /// Rewrites references to the column <paramref name="oldName"/> (<c>[Old]</c>, <c>CHILDREN([Old])</c>,
    /// <c>[Old]:[Old]</c>, <c>[Old]@row</c>) to <paramref name="newName"/>, leaving everything else as typed.
    /// Names match ignoring case and surrounding spaces. Text that can't be tokenized is returned unchanged.
    /// </summary>
    public static string RenameColumn(string text, string oldName, string newName)
    {
        IReadOnlyList<Token> tokens;
        try
        {
            tokens = FormulaTokenizer.Tokenize(text);
        }
        catch (FormulaParseException)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        var cursor = 0;
        foreach (var token in tokens.Where(t => t.Kind == TokenKind.Reference && string.Equals(t.Text, oldName.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            builder.Append(text, cursor, token.Position - cursor).Append('[').Append(newName.Trim()).Append(']');
            cursor = token.Position + token.Length;
        }

        return cursor == 0 ? text : builder.Append(text, cursor, text.Length - cursor).ToString();
    }

    private static string? FindFunctionProblem(FormulaNode node) => node switch
    {
        FunctionNode f when !FormulaFunctions.IsKnown(f.Name) =>
            $"Unknown function {f.Name} at position {f.Position + 1}.{DidYouMean(f.Name, FormulaFunctions.Names, n => n)} The formula help lists every function.",
        FunctionNode f when FormulaFunctions.CheckArity(f.Name, f.Arguments.Count) is { } message => message,
        FunctionNode f => f.Arguments.Select(FindFunctionProblem).FirstOrDefault(p => p is not null),
        UnaryNode u => FindFunctionProblem(u.Operand),
        BinaryNode b => FindFunctionProblem(b.Left) ?? FindFunctionProblem(b.Right),
        _ => null,
    };

    /// <summary><c>@cell</c> only means something inside a criterion of COUNTIF(S), SUMIF(S), AVERAGEIF(S) or COLLECT.</summary>
    private static string? FindCellProblem(FormulaNode node, bool insideCriterion) => node switch
    {
        CurrentCellNode c when !insideCriterion =>
            $"@cell at position {c.Position + 1} only works inside a criterion, like COUNTIFS([Due]:[Due], @cell < TODAY()).",
        FunctionNode f => f.Arguments
            .Select((a, i) => FindCellProblem(a, insideCriterion || FormulaFunctions.IsCriterionArgument(f.Name, i)))
            .FirstOrDefault(p => p is not null),
        UnaryNode u => FindCellProblem(u.Operand, insideCriterion),
        BinaryNode b => FindCellProblem(b.Left, insideCriterion) ?? FindCellProblem(b.Right, insideCriterion),
        _ => null,
    };

    /// <summary>" Did you mean X?" for the closest known name, or "" when nothing is close.</summary>
    internal static string DidYouMean(string name, IEnumerable<string> known, Func<string, string> format)
    {
        var best = known
            .Select(k => (Name: k, Distance: Distance(name.ToUpperInvariant(), k.ToUpperInvariant())))
            .Where(k => k.Distance <= Math.Max(1, name.Length / 3))
            .OrderBy(k => k.Distance)
            .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return best.Name is null ? "" : $" Did you mean {format(best.Name)}?";
    }

    /// <summary>Edits between two names (insert, delete, change, or swap two neighbouring letters).</summary>
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    private static void Collect(FormulaNode node, HashSet<string> references, HashSet<string> childReferences,
        List<(string, string)> ranges, HashSet<string> sheetReferences)
    {
        switch (node)
        {
            case ReferenceNode r:
                references.Add(r.Name);
                break;
            case ColumnRangeNode r:
                ranges.Add((r.From, r.To));
                break;
            case SheetRangeNode r:
                sheetReferences.Add(r.Name);
                break;
            case FunctionNode { Name: FormulaFunctions.Children, Arguments: [ReferenceNode child] }:
                childReferences.Add(child.Name);
                break;
            case FunctionNode f:
                foreach (var argument in f.Arguments)
                {
                    Collect(argument, references, childReferences, ranges, sheetReferences);
                }

                break;
            case UnaryNode u:
                Collect(u.Operand, references, childReferences, ranges, sheetReferences);
                break;
            case BinaryNode b:
                Collect(b.Left, references, childReferences, ranges, sheetReferences);
                Collect(b.Right, references, childReferences, ranges, sheetReferences);
                break;
        }
    }
}
