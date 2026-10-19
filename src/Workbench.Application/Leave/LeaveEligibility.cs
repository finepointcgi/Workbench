using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Leave;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Leave;

/// <summary>
/// Which employees a leave policy applies to (<see cref="LeavePolicy.EligibleOrgUnitIds"/> and friends). A policy with no
/// criteria applies to everyone, so existing policies are unchanged. Accrual, balances and requests all ask this.
/// </summary>
internal sealed class LeaveEligibility
{
    private readonly Dictionary<Guid, Guid?> _parents;
    private readonly Dictionary<Guid, EmployeePayProfile> _profiles;

    private LeaveEligibility(Dictionary<Guid, Guid?> parents, Dictionary<Guid, EmployeePayProfile> profiles)
    {
        _parents = parents;
        _profiles = profiles;
    }

    public static async Task<LeaveEligibility> LoadAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> employeeIds, CancellationToken ct)
    {
        var parents = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.ParentId, ct);
        var profiles = (await db.Set<EmployeePayProfile>().AsNoTracking().ToListAsync(ct))
            .Where(p => employeeIds.Contains(p.EmployeeId)).ToDictionary(p => p.EmployeeId);
        return new LeaveEligibility(parents, profiles);
    }

    public static bool AppliesToEveryone(LeavePolicy p) =>
        p.EligibleOrgUnitIds is null && p.EligiblePayGroupIds is null && p.EligibleExempt is null && p.EligibleEmploymentTypes is null;

    public bool Applies(LeavePolicy policy, Employee employee)
    {
        if (AppliesToEveryone(policy))
        {
            return true;
        }

        if (policy.EligibleExempt is { } exempt && employee.IsExempt != exempt)
        {
            return false;
        }

        if (Ids(policy.EligibleOrgUnitIds) is { Count: > 0 } units && !InUnits(employee.OrgUnitId, units))
        {
            return false;
        }

        var profile = _profiles.GetValueOrDefault(employee.Id);
        if (Ids(policy.EligiblePayGroupIds) is { Count: > 0 } groups && (profile?.PayGroupId is not { } group || !groups.Contains(group)))
        {
            return false;
        }

        var type = profile?.EmploymentType ?? EmploymentType.FullTime;
        return Types(policy.EligibleEmploymentTypes) is not { Count: > 0 } types || types.Contains(type);
    }

    /// <summary>The unit or one of its parents is listed (a cycle in bad data stops the walk).</summary>
    private bool InUnits(Guid? unitId, HashSet<Guid> units)
    {
        var seen = new HashSet<Guid>();
        for (var id = unitId; id is { } u && seen.Add(u); id = _parents.GetValueOrDefault(u))
        {
            if (units.Contains(u))
            {
                return true;
            }
        }

        return false;
    }

    // ---- Storage (comma-separated on the policy) ----

    public static HashSet<Guid> Ids(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Guid.TryParse(x, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToHashSet();

    public static HashSet<EmploymentType> Types(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Enum.TryParse<EmploymentType>(x, out var t) ? t : (EmploymentType?)null).OfType<EmploymentType>().ToHashSet();

    public static LeaveEligibilityDto ToDto(LeavePolicy p) => new(
        Ids(p.EligibleOrgUnitIds).Order().ToList(),
        Ids(p.EligiblePayGroupIds).Order().ToList(),
        p.EligibleExempt,
        Types(p.EligibleEmploymentTypes).Order().Select(t => (EmploymentTypeKind)(int)t).ToList());

    /// <summary>Validates and stores the criteria (null or empty: everyone).</summary>
    public static async Task ApplyAsync(IWorkbenchDbContext db, LeavePolicy policy, LeaveEligibilityDto? eligibility, CancellationToken ct)
    {
        var e = eligibility ?? LeaveEligibilityDto.Everyone;
        var units = (e.OrgUnitIds ?? []).Distinct().ToList();
        var groups = (e.PayGroupIds ?? []).Distinct().ToList();
        var types = (e.EmploymentTypes ?? []).Distinct().ToList();
        if (units.Count > 0 && await db.Set<OrgUnit>().CountAsync(o => units.Contains(o.Id), ct) != units.Count)
        {
            throw new ArgumentException("One of the chosen org units doesn't exist.");
        }

        if (groups.Count > 0 && await db.Set<PayGroup>().CountAsync(g => groups.Contains(g.Id), ct) != groups.Count)
        {
            throw new ArgumentException("One of the chosen pay groups doesn't exist.");
        }

        if (types.Any(t => !Enum.IsDefined(t)))
        {
            throw new ArgumentException("Choose known employment types.");
        }

        if (units.Count > 100 || groups.Count > 100)
        {
            throw new ArgumentException("Choose at most 100 org units and 100 pay groups.");
        }

        policy.EligibleOrgUnitIds = units.Count == 0 ? null : string.Join(',', units.Order());
        policy.EligiblePayGroupIds = groups.Count == 0 ? null : string.Join(',', groups.Order());
        policy.EligibleExempt = e.Exempt;
        policy.EligibleEmploymentTypes = types.Count == 0 ? null : string.Join(',', types.Order().Select(t => ((EmploymentType)(int)t).ToString()));
    }
}
