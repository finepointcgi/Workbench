using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

/// <summary>
/// A project's money terms: its <see cref="ProjectPlan"/> overrides where set, otherwise the contract's. With CLINs mapped
/// to the project's top WBS elements, the ceiling and funding are those CLINs' (the unit the government funds); otherwise
/// the contract's. The fee is the CLINs' (weighted by ceiling); T&amp;M and labor-hour work bills at the contract's rates.
/// </summary>
public static class ProjectTerms
{
    /// <summary>A CLIN a top WBS element is mapped to, with what it's worth and funded.</summary>
    public sealed record ClinTerms(Guid ClinId, string Number, decimal Ceiling, decimal Funded, decimal? FeeRate);

    /// <summary><see cref="Terms"/> are in force; <see cref="Derived"/> are what the contract alone gives (no overrides).</summary>
    public sealed record Loaded(ProjectTermsDto Terms, ProjectTermsDto Derived, ProjectPlan? Plan, IReadOnlyDictionary<Guid, ClinTerms> ClinOfTopElement);

    public static async Task<Loaded> LoadAsync(IWorkbenchDbContext db, Project project, CancellationToken ct)
    {
        var plan = await db.Set<ProjectPlan>().AsNoTracking().FirstOrDefaultAsync(p => p.ProjectId == project.Id, ct);
        var contract = project.ContractId is { } cid ? await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        var tops = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == project.Id && w.ParentId == null && w.ClinId != null)
            .Select(w => new { w.Id, ClinId = w.ClinId!.Value }).ToListAsync(ct);
        var clinIds = tops.Select(t => t.ClinId).Distinct().ToList();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => clinIds.Contains(c.Id))
            .Select(c => new ClinTerms(c.Id, c.Number, c.CeilingValue, c.FundedValue, c.FeeRate)).ToDictionaryAsync(c => c.ClinId, ct);
        var byTop = tops.Where(t => clins.ContainsKey(t.ClinId)).ToDictionary(t => t.Id, t => clins[t.ClinId]);
        var shared = contract is null ? [] : await db.Set<Project>().AsNoTracking()
            .Where(p => p.ContractId == contract.Id && p.Id != project.Id).OrderBy(p => p.Code).Select(p => p.Code).ToListAsync(ct);

        var derived = Derive(contract, clins.Values.ToList());
        var terms = new ProjectTermsDto(
            contract?.Id, contract?.ContractNumber,
            plan?.CeilingValue ?? derived.Ceiling, plan?.CeilingValue is null && derived.Ceiling is not null,
            plan?.FundedValue ?? derived.Funded, plan?.FundedValue is null && derived.Funded is not null,
            plan?.Pricing is { } p ? (ContractKind)(int)p : derived.Pricing, plan?.Pricing is null && derived.Pricing is not null,
            plan?.FeeRate ?? derived.FeeRate, plan?.FeeRate is null && derived.FeeFromContract,
            project.Start, project.Finish, shared);
        var contractOnly = new ProjectTermsDto(contract?.Id, contract?.ContractNumber, derived.Ceiling, derived.Ceiling is not null, derived.Funded, derived.Funded is not null,
            derived.Pricing, derived.Pricing is not null, derived.FeeRate, derived.FeeFromContract, project.Start, project.Finish, shared);
        return new Loaded(terms, contractOnly, plan, byTop);
    }

    /// <summary>What the contract says, before any project overrides (also what "New project from a contract" prefills).</summary>
    public static async Task<ProjectTermsDto> FromContractAsync(IWorkbenchDbContext db, Guid contractId, CancellationToken ct)
    {
        var contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contractId, ct)
            ?? throw Contracts.WorkbenchNotFoundException.For("Contract", contractId);
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => c.ContractId == contractId)
            .Select(c => new ClinTerms(c.Id, c.Number, c.CeilingValue, c.FundedValue, c.FeeRate)).ToListAsync(ct);
        var derived = Derive(contract, []);
        var fee = WeightedFee(clins);
        var shared = await db.Set<Project>().AsNoTracking().Where(p => p.ContractId == contractId).OrderBy(p => p.Code).Select(p => p.Code).ToListAsync(ct);
        return new ProjectTermsDto(contract.Id, contract.ContractNumber, derived.Ceiling, true, derived.Funded, true, derived.Pricing, true,
            fee ?? 0m, fee is not null, contract.PeriodStart, contract.PeriodEnd, shared);
    }

    /// <summary>
    /// The overrides to store for <paramref name="request"/>: a value equal to what the contract gives is stored as null,
    /// so a later contract change (a new funding action) still flows through.
    /// </summary>
    public static (decimal? Ceiling, decimal? Funded, ContractType? Pricing, decimal? FeeRate) Overrides(SaveProjectTermsRequest request, ProjectTermsDto fromContract)
    {
        if (request.Ceiling is < 0 || request.Funded is < 0)
        {
            throw new ArgumentException("The ceiling and funding can't be negative.");
        }

        if (request.FeeRate is < 0 or >= 1)
        {
            throw new ArgumentException("Enter the fee as a percentage below 100%.");
        }

        if (request.Ceiling is { } c && request.Funded is { } f && f > c)
        {
            throw new ArgumentException("Funding can't be more than the ceiling.");
        }

        return (
            request.Ceiling is { } ceiling && (!fromContract.CeilingFromContract || ceiling != fromContract.Ceiling) ? ceiling : null,
            request.Funded is { } funded && (!fromContract.FundedFromContract || funded != fromContract.Funded) ? funded : null,
            request.Pricing is { } pricing && (!fromContract.PricingFromContract || pricing != fromContract.Pricing) ? (ContractType)(int)pricing : null,
            request.FeeRate is { } fee && (!fromContract.FeeFromContract || fee != fromContract.FeeRate) ? fee : null);
    }

    private static (decimal? Ceiling, decimal? Funded, ContractKind? Pricing, decimal FeeRate, bool FeeFromContract) Derive(Contract? contract, IReadOnlyList<ClinTerms> mapped)
    {
        if (contract is null)
        {
            return (null, null, null, 0m, false);
        }

        var fee = WeightedFee(mapped);
        return mapped.Count > 0
            ? (mapped.Sum(c => c.Ceiling), mapped.Sum(c => c.Funded), (ContractKind)(int)contract.Type, fee ?? 0m, fee is not null)
            : (contract.CeilingValue, contract.FundedValue, (ContractKind)(int)contract.Type, 0m, false);
    }

    /// <summary>
    /// The fee across the CLINs, weighted by their ceilings; a CLIN without a fee rate (travel at cost, a fixed-price
    /// line) counts as no fee. Null when no CLIN has a fee rate.
    /// </summary>
    private static decimal? WeightedFee(IReadOnlyCollection<ClinTerms> clins)
    {
        if (!clins.Any(c => c.FeeRate is not null))
        {
            return null;
        }

        var weight = clins.Sum(c => c.Ceiling);
        return weight > 0
            ? Math.Round(clins.Sum(c => (c.FeeRate ?? 0m) * c.Ceiling) / weight, 6, MidpointRounding.AwayFromZero)
            : clins.Average(c => c.FeeRate ?? 0m);
    }
}
