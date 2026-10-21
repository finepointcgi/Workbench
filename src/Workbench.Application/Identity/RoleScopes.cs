using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Identity;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Identity;

/// <summary>Permissions a role grants only within a scope (some companies, org units, projects or contracts).</summary>
public sealed record ScopedGrant(RoleScope Scope, PermissionSet Permissions);

/// <summary>
/// Where someone holds a permission: everywhere, nowhere, or within some companies, org units, projects and contracts
/// (the union of their scoped role assignments that grant it).
/// </summary>
public sealed record GrantScope(bool All, IReadOnlySet<Guid> Companies, IReadOnlySet<Guid> OrgUnits, IReadOnlySet<Guid> Projects, IReadOnlySet<Guid> Contracts)
{
    public static GrantScope Everywhere { get; } = new(true, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());

    public static GrantScope Nowhere { get; } = new(false, new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());

    public bool IsNowhere => !All && Companies.Count == 0 && OrgUnits.Count == 0 && Projects.Count == 0 && Contracts.Count == 0;

    /// <summary>True when the scope covers the record.</summary>
    public bool Covers(ResourceRef resource) =>
        All
        || resource.CompanyIds.Any(Companies.Contains)
        || resource.OrgUnitIds.Any(OrgUnits.Contains)
        || resource.ProjectIds.Any(Projects.Contains)
        || resource.ContractIds.Any(Contracts.Contains);
}

/// <summary>
/// What a record belongs to, for matching scoped grants. <see cref="OrgUnitIds"/> holds the record's org unit and every
/// unit above it, so a scope on a unit covers its sub-units.
/// </summary>
public sealed record ResourceRef(IReadOnlyCollection<Guid> CompanyIds, IReadOnlyCollection<Guid> OrgUnitIds, IReadOnlyCollection<Guid> ProjectIds, IReadOnlyCollection<Guid> ContractIds)
{
    public static ResourceRef Nothing { get; } = new([], [], [], []);
}

/// <summary>Scope matching: which records a scoped grant covers, and which grants cover a record.</summary>
public static class RoleScopes
{
    /// <summary>Where <paramref name="access"/> holds <paramref name="permission"/>.</summary>
    public static GrantScope ScopeOf(ResolvedAccess access, string permission)
    {
        if (access.Unscoped.Has(permission))
        {
            return GrantScope.Everywhere;
        }

        HashSet<Guid> companies = [], units = [], projects = [], contracts = [];
        foreach (var grant in access.ScopedGrants.Where(g => g.Permissions.Has(permission)))
        {
            var target = grant.Scope.Kind switch
            {
                RoleScopeKind.Companies => companies,
                RoleScopeKind.OrgUnits => units,
                RoleScopeKind.Projects => projects,
                RoleScopeKind.Contracts => contracts,
                _ => null,
            };
            target?.UnionWith(grant.Scope.Ids);
        }

        return new GrantScope(false, companies, units, projects, contracts);
    }

    /// <summary>A project: its company, contract, and its manager's org unit and the units above it.</summary>
    public static async Task<ResourceRef> ForProjectAsync(IWorkbenchDbContext db, Guid projectId, CancellationToken ct)
    {
        var project = await db.Set<Project>().AsNoTracking().Where(p => p.Id == projectId)
            .Select(p => new { p.CompanyId, p.ContractId, p.ProjectManagerId }).FirstOrDefaultAsync(ct);
        if (project is null)
        {
            return ResourceRef.Nothing;
        }

        var units = await UnitChainOfEmployeeAsync(db, project.ProjectManagerId, ct);
        return new ResourceRef([project.CompanyId], units, [projectId], project.ContractId is { } c ? [c] : []);
    }

    /// <summary>A contract: its company, its projects and their managers' org units.</summary>
    public static async Task<ResourceRef> ForContractAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct)
    {
        var company = await db.Set<Contract>().AsNoTracking().Where(c => c.Id == contractId).Select(c => (Guid?)c.CompanyId).FirstOrDefaultAsync(ct);
        var projects = await db.Set<Project>().AsNoTracking().Where(p => p.ContractId == contractId).Select(p => new { p.Id, p.ProjectManagerId }).ToListAsync(ct);
        var units = new HashSet<Guid>();
        foreach (var manager in projects.Select(p => p.ProjectManagerId).Distinct())
        {
            units.UnionWith(await UnitChainOfEmployeeAsync(db, manager, ct));
        }

        return new ResourceRef(company is { } c ? [c] : [], units, projects.Select(p => p.Id).ToList(), [contractId]);
    }

    /// <summary>An employee: their company and their org unit with the units above it.</summary>
    public static async Task<ResourceRef> ForEmployeeAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken ct)
    {
        var company = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == employeeId).Select(e => (Guid?)e.CompanyId).FirstOrDefaultAsync(ct);
        return new ResourceRef(company is { } c ? [c] : [], await UnitChainOfEmployeeAsync(db, employeeId, ct), [], []);
    }

    /// <summary>The projects a scope covers (null for everywhere).</summary>
    public static async Task<IReadOnlySet<Guid>?> ProjectIdsAsync(IWorkbenchDbContext db, GrantScope scope, CancellationToken ct)
    {
        if (scope.All)
        {
            return null;
        }

        var result = new HashSet<Guid>(scope.Projects);
        if (scope.IsNowhere)
        {
            return result;
        }

        var companies = scope.Companies.ToList();
        var contracts = scope.Contracts.ToList();
        var managers = (await EmployeeIdsInUnitsAsync(db, scope.OrgUnits, ct)).ToList();
        result.UnionWith(await db.Set<Project>().AsNoTracking()
            .Where(p => companies.Contains(p.CompanyId) || (p.ContractId != null && contracts.Contains(p.ContractId.Value))
                || (p.ProjectManagerId != null && managers.Contains(p.ProjectManagerId.Value)))
            .Select(p => p.Id).ToListAsync(ct));
        return result;
    }

    /// <summary>The contracts a scope covers (null for everywhere): named ones, ones in its companies, and those of its projects.</summary>
    public static async Task<IReadOnlySet<Guid>?> ContractIdsAsync(IWorkbenchDbContext db, GrantScope scope, CancellationToken ct)
    {
        if (scope.All)
        {
            return null;
        }

        var result = new HashSet<Guid>(scope.Contracts);
        var companies = scope.Companies.ToList();
        if (companies.Count > 0)
        {
            result.UnionWith(await db.Set<Contract>().AsNoTracking().Where(c => companies.Contains(c.CompanyId)).Select(c => c.Id).ToListAsync(ct));
        }

        var projects = (await ProjectIdsAsync(db, scope with { Contracts = new HashSet<Guid>() }, ct))!.ToList();
        if (projects.Count > 0)
        {
            result.UnionWith(await db.Set<Project>().AsNoTracking().Where(p => projects.Contains(p.Id) && p.ContractId != null).Select(p => p.ContractId!.Value).ToListAsync(ct));
        }

        return result;
    }

    /// <summary>The employees a scope covers (null for everywhere): those in its companies and org units (with sub-units).</summary>
    public static async Task<IReadOnlySet<Guid>?> EmployeeIdsAsync(IWorkbenchDbContext db, GrantScope scope, CancellationToken ct)
    {
        if (scope.All)
        {
            return null;
        }

        var result = new HashSet<Guid>(await EmployeeIdsInUnitsAsync(db, scope.OrgUnits, ct));
        var companies = scope.Companies.ToList();
        if (companies.Count > 0)
        {
            result.UnionWith(await db.Set<Employee>().AsNoTracking().Where(e => companies.Contains(e.CompanyId)).Select(e => e.Id).ToListAsync(ct));
        }

        return result;
    }

    /// <summary>
    /// The company someone is working in, which company-scoped grants follow: the one picked in the company switcher (if
    /// they may still use it), else their only company. Null when they work across several.
    /// </summary>
    public static async Task<Guid?> WorkingCompanyAsync(IWorkbenchDbContext db, Guid userId, CancellationToken ct)
    {
        var active = await db.Set<Company>().AsNoTracking().Where(c => c.IsActive).Select(c => c.Id).ToListAsync(ct);
        if (active.Count <= 1)
        {
            return active.Count == 1 ? active[0] : Company.DefaultId;
        }

        var allowed = await db.Set<UserCompanyAccess>().AsNoTracking().Where(a => a.UserId == userId).Select(a => a.CompanyId).ToListAsync(ct);
        var selected = await db.Set<UserCompanySelection>().AsNoTracking().Where(s => s.UserId == userId).Select(s => s.CompanyId).FirstOrDefaultAsync(ct);
        if (selected is { } s && active.Contains(s) && (allowed.Count == 0 || allowed.Contains(s)))
        {
            return s;
        }

        return allowed.Count == 1 ? allowed[0] : null;
    }

    /// <summary>"company MAIN", "org units ENG, OPS", "project 1001": the scope in words (empty for everywhere).</summary>
    public static async Task<string> DescribeAsync(IWorkbenchDbContext db, RoleScope? scope, CancellationToken ct)
    {
        if (scope is null || scope.IsEverywhere)
        {
            return "";
        }

        var ids = scope.Ids.ToList();
        List<string> names = scope.Kind switch
        {
            RoleScopeKind.Companies => await db.Set<Company>().AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => c.Code).ToListAsync(ct),
            RoleScopeKind.OrgUnits => await db.Set<OrgUnit>().AsNoTracking().Where(u => ids.Contains(u.Id)).Select(u => u.Code).ToListAsync(ct),
            RoleScopeKind.Projects => await db.Set<Project>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => p.Code).ToListAsync(ct),
            RoleScopeKind.Contracts => await db.Set<Contract>().AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => c.ContractNumber).ToListAsync(ct),
            _ => [],
        };
        var noun = scope.Kind switch
        {
            RoleScopeKind.Companies => names.Count == 1 ? "company" : "companies",
            RoleScopeKind.OrgUnits => names.Count == 1 ? "org unit" : "org units",
            RoleScopeKind.Projects => names.Count == 1 ? "project" : "projects",
            _ => names.Count == 1 ? "contract" : "contracts",
        };
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return $"{noun} {string.Join(", ", names.DefaultIfEmpty("(deleted)"))}";
    }

    /// <summary>Checks a scope's kind and ids name real records; returns it normalized. Throws <see cref="ArgumentException"/>.</summary>
    public static async Task<RoleScope> ValidateAsync(IWorkbenchDbContext db, RoleScope? scope, CancellationToken ct)
    {
        if (scope is null || scope.IsEverywhere)
        {
            return RoleScope.Everywhere;
        }

        if (!Enum.IsDefined(scope.Kind))
        {
            throw new ArgumentException("Unknown kind of scope.", nameof(scope));
        }

        var normalized = scope.Normalized;
        var ids = normalized.Ids.ToList();
        var found = normalized.Kind switch
        {
            RoleScopeKind.Companies => await db.Set<Company>().AsNoTracking().CountAsync(c => ids.Contains(c.Id), ct),
            RoleScopeKind.OrgUnits => await db.Set<OrgUnit>().AsNoTracking().CountAsync(u => ids.Contains(u.Id), ct),
            RoleScopeKind.Projects => await db.Set<Project>().AsNoTracking().CountAsync(p => ids.Contains(p.Id), ct),
            _ => await db.Set<Contract>().AsNoTracking().CountAsync(c => ids.Contains(c.Id), ct),
        };
        if (found != ids.Count)
        {
            throw new ArgumentException("A role is limited to something that doesn't exist. Pick again.", nameof(scope));
        }

        if (RoleStore.FormatIds(ids)!.Length > 4000)
        {
            throw new ArgumentException("A role can be limited to at most 100 things; use a wider scope.", nameof(scope));
        }

        return normalized;
    }

    private static async Task<IReadOnlyList<Guid>> UnitChainOfEmployeeAsync(IWorkbenchDbContext db, Guid? employeeId, CancellationToken ct)
    {
        if (employeeId is not { } id)
        {
            return [];
        }

        var unit = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == id).Select(e => e.OrgUnitId).FirstOrDefaultAsync(ct);
        if (unit is null)
        {
            return [];
        }

        var parents = await db.Set<OrgUnit>().AsNoTracking().Select(u => new { u.Id, u.ParentId }).ToDictionaryAsync(u => u.Id, u => u.ParentId, ct);
        var chain = new List<Guid>();
        for (Guid? current = unit; current is { } c && !chain.Contains(c); current = parents.GetValueOrDefault(c))
        {
            chain.Add(c);
        }

        return chain;
    }

    private static async Task<IReadOnlyList<Guid>> EmployeeIdsInUnitsAsync(IWorkbenchDbContext db, IReadOnlySet<Guid> units, CancellationToken ct)
    {
        if (units.Count == 0)
        {
            return [];
        }

        var all = await db.Set<OrgUnit>().AsNoTracking().Select(u => new { u.Id, u.ParentId }).ToListAsync(ct);
        var subtree = new HashSet<Guid>(units);
        bool added;
        do
        {
            added = false;
            foreach (var u in all.Where(u => u.ParentId is { } p && subtree.Contains(p) && !subtree.Contains(u.Id)))
            {
                subtree.Add(u.Id);
                added = true;
            }
        }
        while (added);

        var list = subtree.ToList();
        return await db.Set<Employee>().AsNoTracking().Where(e => e.OrgUnitId != null && list.Contains(e.OrgUnitId.Value)).Select(e => e.Id).ToListAsync(ct);
    }
}
