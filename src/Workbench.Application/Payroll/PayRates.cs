using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Payroll;

/// <summary>
/// Effective-dated pay rates (<see cref="EmployeePayRate"/>). A profile saved before rates had a history has no rows: it
/// reads as one rate effective from the hire date (<see cref="BackfillAsync"/> stores that row, and the first rate
/// change stores it too), so nothing changes for it.
/// </summary>
public static class PayRates
{
    public const string BackfillReason = "Rate on file before pay rate history";

    /// <summary>Each employee's rate history, oldest first (for the same date, the latest recorded last).</summary>
    public static async Task<Dictionary<Guid, List<EmployeePayRate>>> HistoryAsync(
        IWorkbenchDbContext db, IReadOnlyCollection<EmployeePayProfile> profiles, CancellationToken ct)
    {
        var ids = profiles.Select(p => p.EmployeeId).Distinct().ToList();
        var rows = (await db.Set<EmployeePayRate>().AsNoTracking().Where(r => ids.Contains(r.EmployeeId)).ToListAsync(ct))
            .ToLookup(r => r.EmployeeId);
        var missing = profiles.Where(p => !rows[p.EmployeeId].Any() && HasRate(p)).Select(p => p.EmployeeId).ToList();
        var hired = missing.Count == 0
            ? []
            : await db.Set<Employee>().AsNoTracking().Where(e => missing.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.HireDate, ct);
        return profiles.ToDictionary(p => p.EmployeeId, p => rows[p.EmployeeId].Any()
            ? Order(rows[p.EmployeeId])
            : HasRate(p) ? [FromProfile(p, hired.GetValueOrDefault(p.EmployeeId, p.EffectiveFrom))] : []);
    }

    public static async Task<List<EmployeePayRate>> HistoryAsync(IWorkbenchDbContext db, EmployeePayProfile profile, CancellationToken ct) =>
        (await HistoryAsync(db, [profile], ct))[profile.EmployeeId];

    /// <summary>The rate in effect on <paramref name="date"/>, or null when the first rate starts later.</summary>
    public static EmployeePayRate? InEffect(IReadOnlyList<EmployeePayRate> history, DateOnly date) =>
        history.LastOrDefault(r => r.EffectiveFrom <= date);

    /// <summary>Rates that start after <paramref name="from"/> and on or before <paramref name="to"/> (a change within a pay period).</summary>
    public static IEnumerable<EmployeePayRate> ChangesWithin(IReadOnlyList<EmployeePayRate> history, DateOnly from, DateOnly to) =>
        history.Where(r => r.EffectiveFrom > from && r.EffectiveFrom <= to);

    public static List<EmployeePayRate> Order(IEnumerable<EmployeePayRate> rows) =>
        rows.OrderBy(r => r.EffectiveFrom).ThenBy(r => r.CreatedAtUtc).ThenBy(r => r.Id).ToList();

    /// <summary>Stores the hire-date row for every profile with a rate but no history. Idempotent; saves when anything was added.</summary>
    public static async Task<int> BackfillAsync(IWorkbenchDbContext db, string actor, DateTime utcNow, CancellationToken ct, string reason = BackfillReason)
    {
        var withRows = await db.Set<EmployeePayRate>().AsNoTracking().Select(r => r.EmployeeId).Distinct().ToListAsync(ct);
        var profiles = (await db.Set<EmployeePayProfile>().AsNoTracking().ToListAsync(ct))
            .Where(p => HasRate(p) && !withRows.Contains(p.EmployeeId)).ToList();
        if (profiles.Count == 0)
        {
            return 0;
        }

        var ids = profiles.Select(p => p.EmployeeId).ToList();
        var hired = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.HireDate, ct);
        foreach (var p in profiles)
        {
            var row = FromProfile(p, hired.GetValueOrDefault(p.EmployeeId, p.EffectiveFrom));
            row.CreatedBy = actor;
            row.CreatedAtUtc = utcNow;
            row.Reason = reason;
            db.Set<EmployeePayRate>().Add(row);
        }

        await db.SaveChangesAsync(ct);
        return profiles.Count;
    }

    public static bool HasRate(EmployeePayProfile p) => (p.AnnualSalary ?? p.HourlyRate) is not null;

    /// <summary>Copies the latest row onto the profile's mirror fields.</summary>
    public static void Mirror(EmployeePayProfile profile, IReadOnlyList<EmployeePayRate> history)
    {
        if (history.Count == 0)
        {
            return;
        }

        var latest = history[^1];
        profile.PayType = latest.PayType;
        profile.AnnualSalary = latest.AnnualSalary;
        profile.HourlyRate = latest.HourlyRate;
        profile.EffectiveFrom = latest.EffectiveFrom;
    }

    private static EmployeePayRate FromProfile(EmployeePayProfile p, DateOnly hireDate) => new()
    {
        EmployeeId = p.EmployeeId,
        EffectiveFrom = hireDate,
        PayType = p.PayType,
        AnnualSalary = p.AnnualSalary,
        HourlyRate = p.HourlyRate,
        Reason = BackfillReason,
        CreatedBy = p.UpdatedBy ?? "system",
        CreatedAtUtc = p.UpdatedAtUtc,
    };
}
