using System.Globalization;

namespace Workbench.Contracts.ProjectAccounting;

/// <summary>
/// WBS code numbering shared by the plan's automatic codes and the WBS editor's suggestions: a child's code is its parent's
/// code (the project code at the top) plus a two-digit position, e.g. <c>1001.02.03</c>.
/// </summary>
public static class WbsCodes
{
    public static string Code(string prefix, int position) => $"{prefix}.{position.ToString("00", CultureInfo.InvariantCulture)}";

    /// <summary>3 for <c>1001.02.03</c> under <c>1001.02</c>; null for a code that isn't directly under the prefix.</summary>
    public static int? Position(string prefix, string? code) =>
        code is not null && code.Length > prefix.Length + 1 && code.StartsWith(prefix + ".", StringComparison.Ordinal)
        && int.TryParse(code.AsSpan(prefix.Length + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>The next code under <paramref name="prefix"/> after the ones already used there.</summary>
    public static string Next(string prefix, IEnumerable<string> siblingCodes)
    {
        var used = siblingCodes.ToHashSet(StringComparer.Ordinal);
        var n = used.Select(c => Position(prefix, c) ?? 0).DefaultIfEmpty(0).Max() + 1;
        while (used.Contains(Code(prefix, n)))
        {
            n++;
        }

        return Code(prefix, n);
    }
}
