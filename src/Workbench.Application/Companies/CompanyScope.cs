using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Companies;
using Workbench.Domain.Identity;

namespace Workbench.Application.Companies;

/// <summary>
/// Which companies the current user may use (their company access list; empty = all) and which they're working in (the
/// app bar switcher). Nothing is cached: a Blazor circuit keeps its scope for hours, and access changes apply on the next
/// call. With one company there's nothing to limit, so every answer is "all companies" (null).
/// </summary>
public sealed class CompanyScope(IWorkbenchDbContextFactory dbFactory, AccessControl access, ICurrentUser currentUser)
{
    /// <summary>The user's view of the companies.</summary>
    /// <param name="Companies">Every company, active or not, by code.</param>
    /// <param name="Accessible">Companies the user may use, or null for all of them.</param>
    /// <param name="Selected">The company picked in the switcher, if it's still one they may use.</param>
    public sealed record View(IReadOnlyList<Company> Companies, IReadOnlySet<Guid>? Accessible, Guid? Selected)
    {
        public bool IsMulticompany => Companies.Count > 1;

        /// <summary>What lists and ledger reports show: the selected company, else the accessible ones (null = all).</summary>
        public IReadOnlySet<Guid>? Visible => Selected is { } s ? new HashSet<Guid> { s } : Accessible;

        public bool CanUse(Guid companyId) => Accessible is null || Accessible.Contains(companyId);

        /// <summary>The company new records go to: the selected one, else the default (or the user's first) company.</summary>
        public Guid ForNewRecords => Selected
            ?? (Accessible is null || Accessible.Contains(Company.DefaultId)
                ? Company.DefaultId
                : Companies.Where(c => Accessible.Contains(c.Id) && c.IsActive).Select(c => c.Id).FirstOrDefault(Company.DefaultId));
    }

    private bool Unrestricted =>
        !access.IsAuthenticated || access.IsSystemReader || (currentUser is SystemCurrentUser && access.Email == SystemCurrentUser.SystemEmail);

    public async Task<View> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var companies = (await db.Set<Company>().AsNoTracking().ToListAsync(ct)).OrderBy(c => c.Code, StringComparer.Ordinal).ToList();
        if (companies.Count <= 1 || Unrestricted)
        {
            return new View(companies, null, null);
        }

        var email = access.Email;
        var userId = await db.Set<AppUser>().AsNoTracking().Where(u => u.Email == email).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct);
        if (userId is null)
        {
            return new View(companies, null, null);
        }

        var allowed = await db.Set<UserCompanyAccess>().AsNoTracking().Where(a => a.UserId == userId).Select(a => a.CompanyId).ToListAsync(ct);
        IReadOnlySet<Guid>? accessible = allowed.Count == 0 ? null : allowed.ToHashSet();
        var selected = await db.Set<UserCompanySelection>().AsNoTracking().Where(s => s.UserId == userId).Select(s => s.CompanyId).FirstOrDefaultAsync(ct);
        if (selected is { } s && (!companies.Any(c => c.Id == s && c.IsActive) || accessible?.Contains(s) == false))
        {
            selected = null;
        }

        // Someone limited to a single company always works in it.
        if (selected is null && accessible is { Count: 1 })
        {
            selected = accessible.First();
        }

        return new View(companies, accessible, selected);
    }

    /// <summary>The companies reads are limited to, or null for all (one company, no limits and no selection).</summary>
    public async Task<IReadOnlySet<Guid>?> VisibleCompanyIdsAsync(CancellationToken ct = default) => (await GetAsync(ct)).Visible;
}
