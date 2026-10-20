namespace Workbench.Contracts.Integrations;

/// <summary>An area of the REST API a key can be given read or write access to, by the first path segment after <c>/api/v1/</c>.</summary>
public sealed record ApiKeyAreaDto(string Key, string Label, string Description, IReadOnlyList<string> PathSegments);

/// <summary>
/// API key scopes: <c>{area}:read</c> (GET and HEAD) or <c>{area}:write</c> (every method, reads included). Scopes only narrow
/// what a key may call; the key's organization roles and the permission decorators still decide what it may see and do.
/// Paths outside every area (user and sign-in administration, integrations, imports, the host, the assistant) are never open to keys.
/// A new top-level API path stays closed to keys until it is added to an area here.
/// </summary>
public static class ApiKeyScopes
{
    public const string Read = "read";
    public const string Write = "write";

    public static IReadOnlyList<ApiKeyAreaDto> Areas { get; } =
    [
        new("sheets", "Sheets, dashboards and tasks", "Sheets, items, columns, comments, workspaces, dashboards and assigned work.",
            ["sheets", "workspaces", "dashboards", "my-work", "my-tasks", "my-todo"]),
        new("projects", "Projects and contracts", "Projects, WBS, contracts, CLINs, funding, ceilings, closeout, resourcing and earned value.",
            ["projects", "project-hub", "contracts", "wbs", "assignments", "budgets", "clins", "modifications", "closeout", "resourcing", "evm", "ceilings", "ceiling-overrides", "rate-ceilings"]),
        new("time", "Time and expenses", "Timesheets, ODCs, expense reports, leave, per diem and total time.",
            ["time", "expense-reports", "odc-approvals", "odcs", "leave", "per-diem", "total-time", "travel", "cards"]),
        new("billing", "Billing and revenue", "Invoices, receipts, receivables and revenue recognition.", ["billing", "revenue"]),
        new("purchasing", "Purchasing and payables", "Vendors, requisitions, purchase orders, receiving, AP bills and payments.", ["purchasing", "edi"]),
        new("accounting", "Accounting", "Chart of accounts, journal, fiscal calendar, period close, cash, assets, currencies, companies, budgets and incurred cost.",
            ["accounting", "fiscal", "audit", "cash", "assets", "currencies", "companies", "planning", "incurred-cost", "indirect-rates", "sales-tax"]),
        new("people", "People and payroll", "HR records and payroll. Pay rates stay masked unless the key's roles may reveal them.", ["employees", "hr", "payroll"]),
        new("crm", "Business development and pricing", "Accounts, opportunities, pipeline and proposal pricing.", ["crm", "pricing"]),
        new("materials", "Materials and manufacturing", "Parts, inventory, BOMs, work orders and MRP.", ["materials", "quality", "engineering-changes"]),
        new("reports", "Reports", "Standard reports, the report writer and saved reports.", ["reports"]),
        new("setup", "Setup and approvals", "Organization setup lists (customers, labor categories, org units), approval workflows and the approval inbox.",
            ["setup", "customers", "org-units", "labor-categories", "approvals", "approval-workflows", "approval-delegations"]),
    ];

    public static string Scope(string area, string access) => $"{area}:{access}";

    /// <summary>The area serving <paramref name="segment"/>, or null when keys may never call it.</summary>
    public static ApiKeyAreaDto? AreaFor(string segment) =>
        Areas.FirstOrDefault(a => a.PathSegments.Contains(segment, StringComparer.OrdinalIgnoreCase));

    /// <summary>Normalizes and validates scopes (unknown areas or access levels throw); write implies read, so read is dropped.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> scopes)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in scopes.Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0))
        {
            var colon = raw.IndexOf(':');
            var (area, access) = colon <= 0 ? (raw, "") : (raw[..colon], raw[(colon + 1)..]);
            if (Areas.All(a => a.Key != area) || access is not (Read or Write))
            {
                throw new ArgumentException($"'{raw}' isn't a scope. Use an area followed by :read or :write, such as time:read.");
            }

            if (access == Write || !result.ContainsKey(area))
            {
                result[area] = access;
            }
        }

        return Areas.Where(a => result.ContainsKey(a.Key)).Select(a => Scope(a.Key, result[a.Key])).ToList();
    }

    public static bool IsReadMethod(string method) =>
        string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a key with <paramref name="scopes"/> may make this request. <paramref name="path"/> is the request path
    /// (<c>/api/v1/time/...</c>). Returns the reason when refused.
    /// </summary>
    public static (bool Allowed, string? Reason) Check(IEnumerable<string> scopes, string path, string method)
    {
        var segment = FirstSegment(path);
        var area = segment is null ? null : AreaFor(segment);
        if (area is null)
        {
            return (false, $"API keys can't call /{segment ?? ""}.");
        }

        var granted = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (granted.Contains(Scope(area.Key, Write)) || (IsReadMethod(method) && granted.Contains(Scope(area.Key, Read))))
        {
            return (true, null);
        }

        return (false, $"This key needs the {Scope(area.Key, IsReadMethod(method) ? Read : Write)} scope.");
    }

    /// <summary>The first segment after <c>/api/v1/</c>, lower-case; null when the path isn't under the API.</summary>
    public static string? FirstSegment(string path)
    {
        const string Root = "/api/v1/";
        if (!path.StartsWith(Root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var rest = path[Root.Length..];
        var slash = rest.IndexOf('/');
        var segment = (slash < 0 ? rest : rest[..slash]).ToLowerInvariant();
        return segment.Length == 0 ? null : segment;
    }
}
