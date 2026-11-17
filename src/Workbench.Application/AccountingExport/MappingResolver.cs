using Workbench.Contracts.AccountingExport;
using Workbench.Domain.AccountingExport;

namespace Workbench.Application.AccountingExport;

/// <summary>
/// Looks names up in a target's saved mappings while an export is built, and remembers what had none. Without a mapping a name
/// falls back to Workbench's own (account name, customer name...), which is only right when the destination happens to use the
/// same names, so the service blocks on it (accounts, customers, vendors and items) unless the user allows it. The Generic
/// target never needs mappings.
/// </summary>
public sealed class MappingResolver
{
    private readonly Dictionary<(AccountingMapKind, string), AccountingMapping> _rows = [];
    private readonly Dictionary<(AccountingMapKind, string), (string Name, int Uses)> _unmapped = [];

    public MappingResolver(IEnumerable<AccountingMapping> rows, AccountingTarget target)
    {
        Target = target;
        foreach (var row in rows)
        {
            if (Enum.TryParse<AccountingMapKind>(row.MapKind, out var kind))
            {
                _rows[(kind, row.SourceKey)] = row;
            }
        }
    }

    public AccountingTarget Target { get; }

    /// <summary>The kinds whose names must exist in the destination, so an unmapped one blocks the export.</summary>
    public static bool Blocks(AccountingMapKind kind) => kind is AccountingMapKind.Account or AccountingMapKind.Customer or AccountingMapKind.Vendor or AccountingMapKind.Item;

    public Resolved Resolve(AccountingMapKind kind, string key, string sourceName, string? fallbackCode = null)
    {
        if (_rows.TryGetValue((kind, key), out var row))
        {
            return new Resolved(row.DestinationName, string.IsNullOrWhiteSpace(row.DestinationCode) ? fallbackCode : row.DestinationCode, true);
        }

        if (Target != AccountingTarget.Generic)
        {
            _unmapped[(kind, key)] = (sourceName, _unmapped.TryGetValue((kind, key), out var seen) ? seen.Uses + 1 : 1);
        }

        return new Resolved(sourceName, fallbackCode, false);
    }

    public IReadOnlyList<AccountingUnmappedDto> Unmapped(bool blockingOnly = false) =>
        _unmapped.Where(u => !blockingOnly || Blocks(u.Key.Item1))
            .Select(u => new AccountingUnmappedDto(u.Key.Item1, u.Key.Item2, u.Value.Name, u.Value.Uses))
            .OrderBy(u => u.Kind).ThenBy(u => u.SourceKey, StringComparer.Ordinal).ToList();
}
