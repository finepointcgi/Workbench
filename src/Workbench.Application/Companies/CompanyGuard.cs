using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Domain;
using Workbench.Domain.Companies;

namespace Workbench.Application.Companies;

/// <summary>
/// The checks behind the company-access decorators. A record in a company the user can't see (not in their company list,
/// or not the company they're working in) is not found, as for sheets; lists drop such records. With one company, no
/// access list and no selection, every check passes without a query.
/// </summary>
public sealed class CompanyGuard(IWorkbenchDbContextFactory dbFactory, CompanyScope scope)
{
    public Task<CompanyScope.View> ViewAsync(CancellationToken ct) => scope.GetAsync(ct);

    /// <summary>Throws not found unless the record's company is visible. A missing record is left to the service.</summary>
    public async Task EnsureAsync<T>(Guid id, string kind, CancellationToken ct)
        where T : Entity, ICompanyOwned
    {
        if (await scope.VisibleCompanyIdsAsync(ct) is not { } visible)
        {
            return;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var company = await db.Set<T>().AsNoTracking().Where(x => x.Id == id).Select(x => (Guid?)x.CompanyId).FirstOrDefaultAsync(ct);
        if (company is { } c && !visible.Contains(c))
        {
            throw WorkbenchNotFoundException.For(kind, id);
        }
    }

    /// <summary>True when the record's company is visible (or the record doesn't exist).</summary>
    public async Task<bool> IsVisibleAsync<T>(Guid id, CancellationToken ct)
        where T : Entity, ICompanyOwned
    {
        try
        {
            await EnsureAsync<T>(id, typeof(T).Name, ct);
            return true;
        }
        catch (WorkbenchNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Throws not found unless every record is visible.</summary>
    public async Task EnsureAllAsync<T>(IEnumerable<Guid> ids, string kind, CancellationToken ct)
        where T : Entity, ICompanyOwned
    {
        if (await scope.VisibleCompanyIdsAsync(ct) is not { } visible)
        {
            return;
        }

        var list = ids.Distinct().ToList();
        await using var db = await dbFactory.CreateAsync(ct);
        var hidden = await db.Set<T>().AsNoTracking().Where(x => list.Contains(x.Id)).Select(x => new { x.Id, x.CompanyId }).ToListAsync(ct);
        if (hidden.FirstOrDefault(x => !visible.Contains(x.CompanyId)) is { } h)
        {
            throw WorkbenchNotFoundException.For(kind, h.Id);
        }
    }

    /// <summary>Drops the items whose record is in a company the user can't see.</summary>
    public async Task<IReadOnlyList<TDto>> FilterAsync<T, TDto>(IReadOnlyList<TDto> items, Func<TDto, Guid> id, CancellationToken ct)
        where T : Entity, ICompanyOwned
    {
        if (items.Count == 0 || await scope.VisibleCompanyIdsAsync(ct) is not { } visible)
        {
            return items;
        }

        var ids = items.Select(id).Distinct().ToList();
        await using var db = await dbFactory.CreateAsync(ct);
        var companies = await db.Set<T>().AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.CompanyId, ct);
        return items.Where(i => !companies.TryGetValue(id(i), out var c) || visible.Contains(c)).ToList();
    }

    /// <summary>
    /// The company for a record just created through a service that doesn't take one: the company the user is working in.
    /// No-op unless that's a company other than the record's.
    /// </summary>
    public async Task StampNewAsync<T>(Guid id, CancellationToken ct)
        where T : Entity, ICompanyOwned
    {
        var view = await scope.GetAsync(ct);
        if (!view.IsMulticompany)
        {
            return;
        }

        await SetCompanyAsync<T>(id, view.ForNewRecords, ct);
    }

    public async Task SetCompanyAsync<T>(Guid id, Guid companyId, CancellationToken ct)
        where T : Entity, ICompanyOwned
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var record = await db.Set<T>().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (record is not null && record.CompanyId != companyId)
        {
            record.CompanyId = companyId;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>The single company of some records (refusing a mix); null when there are none.</summary>
    public async Task<Guid?> SingleCompanyAsync<T>(IEnumerable<Guid> ids, string message, CancellationToken ct)
        where T : Entity, ICompanyOwned
    {
        var list = ids.Distinct().ToList();
        await using var db = await dbFactory.CreateAsync(ct);
        var companies = await db.Set<T>().AsNoTracking().Where(x => list.Contains(x.Id)).Select(x => x.CompanyId).Distinct().ToListAsync(ct);
        return companies.Count switch
        {
            0 => null,
            1 => companies[0],
            _ => throw new ArgumentException(message),
        };
    }
}
