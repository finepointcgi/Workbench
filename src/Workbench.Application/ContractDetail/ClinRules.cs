using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Contracts;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ContractDetail;

/// <summary>CLIN numbering, the SLIN hierarchy and the roll-up of CLIN values to the contract.</summary>
internal static partial class ClinRules
{
    [GeneratedRegex("^[0-9]{4}$")]
    private static partial Regex ClinNumber();

    [GeneratedRegex("^[0-9]{4}[A-Z]{2}$")]
    private static partial Regex SlinNumber();

    /// <summary>Normalizes and checks a CLIN (<c>0001</c>) or SLIN (<c>0001AA</c>) number.</summary>
    public static string Normalize(string? number)
    {
        var n = (number ?? "").Trim().ToUpperInvariant();
        if (!ClinNumber().IsMatch(n) && !SlinNumber().IsMatch(n))
        {
            throw new ArgumentException($"'{number}' isn't a CLIN number: use four digits (0001) or, for a SLIN, four digits and two letters (0001AA).");
        }

        return n;
    }

    /// <summary>The parent CLIN number of a SLIN, or null for a CLIN.</summary>
    public static string? ParentOf(string number) => number.Length == 6 ? number[..4] : null;

    /// <summary>Unexercised options can't be charged, funded or billed.</summary>
    public static bool IsActive(this Clin clin) => !clin.IsOption || clin.IsExercised;

    public static ClinDto ToDto(this Clin c, IEnumerable<Clin> all) =>
        new(c.Id, c.ContractId, c.Number, ParentOf(c.Number), c.Description, (ClinPricingKind)(int)c.Pricing, c.IsOption, c.IsExercised,
            c.PeriodStart, c.PeriodEnd, c.CeilingValue, c.FundedValue, c.FeeRate, c.Order, all.Any(x => ParentOf(x.Number) == c.Number));

    /// <summary>Parents first, then their SLINs, by number.</summary>
    public static IEnumerable<Clin> InTreeOrder(IEnumerable<Clin> clins) =>
        clins.OrderBy(c => c.Number[..4], StringComparer.Ordinal).ThenBy(c => c.Number.Length).ThenBy(c => c.Number, StringComparer.Ordinal);

    public static bool HasSlins(Clin clin, IEnumerable<Clin> all) => all.Any(x => ParentOf(x.Number) == clin.Number);

    /// <summary>
    /// Re-derives the roll-ups on tracked entities: a CLIN with SLINs gets the sums of its SLINs; the contract's value is the
    /// sum of active (non-option or exercised) leaf ceilings and its funded value the sum of leaf funding. Contracts without
    /// CLINs keep their typed-in values. Audits the contract when its totals change.
    /// </summary>
    public static void SyncTotals(IWorkbenchDbContext db, AuditRecorder audit, Contract contract, IReadOnlyList<Clin> clins, string reason)
    {
        if (clins.Count == 0)
        {
            return;
        }

        foreach (var parent in clins.Where(c => ParentOf(c.Number) is null))
        {
            var slins = clins.Where(c => ParentOf(c.Number) == parent.Number).ToList();
            if (slins.Count > 0)
            {
                parent.CeilingValue = slins.Sum(s => s.CeilingValue);
                parent.FundedValue = slins.Sum(s => s.FundedValue);
            }
        }

        var leaves = clins.Where(c => !HasSlins(c, clins)).ToList();
        var parents = clins.ToDictionary(c => c.Number);
        bool Active(Clin c) => c.IsActive() && (ParentOf(c.Number) is not { } p || !parents.TryGetValue(p, out var parent) || parent.IsActive());
        var ceiling = leaves.Where(Active).Sum(c => c.CeilingValue);
        var funded = leaves.Sum(c => c.FundedValue);
        if (ceiling == contract.CeilingValue && funded == contract.FundedValue)
        {
            return;
        }

        var before = new { contract.CeilingValue, contract.FundedValue };
        (contract.CeilingValue, contract.FundedValue) = (ceiling, funded);
        audit.Record(db, nameof(Contract), contract.Id, "TotalsSynced", before, new { contract.CeilingValue, contract.FundedValue }, reason);
    }

    public static async Task<List<Clin>> LoadTrackedAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct) =>
        await db.Set<Clin>().Where(c => c.ContractId == contractId).ToListAsync(ct);
}
