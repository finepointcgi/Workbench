using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Company;
using Workbench.Contracts.Identity;
using Workbench.Domain.Company;

namespace Workbench.Application.OrgProfile;

/// <summary>
/// The company type and government-features switch. Anyone signed in reads it (menus and wording depend on it); changing
/// it needs Admin and is audited. Only wording and visibility follow it: data and APIs work the same either way.
/// </summary>
public sealed class CompanyProfileService(IWorkbenchDbContextFactory dbFactory, AccessControl access, AuditRecorder audit, TimeProvider clock) : ICompanyProfileService
{
    public const string AuditEntity = "OrganizationProfile";

    public async Task<CompanyProfileDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<OrganizationProfile>().AsNoTracking().OrderBy(p => p.Id).FirstOrDefaultAsync(cancellationToken);
        return Build(row, await access.HasAsync(Permission.AdminOrganization, cancellationToken));
    }

    /// <summary>The terms for the current company type, for server code that words notifications and to-dos.</summary>
    public async Task<ITerms> GetTermsAsync(CancellationToken cancellationToken = default) =>
        CompanyTerms.For((await GetAsync(cancellationToken)).Type);

    /// <summary>
    /// The terms without a permission check, for singletons and background work that word text on someone's behalf.
    /// Falls back to the government wording when the organization has no profile or it can't be read.
    /// </summary>
    public static async Task<ITerms> LoadTermsAsync(IWorkbenchDbContextFactory dbFactory, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var type = await db.Set<OrganizationProfile>().AsNoTracking().OrderBy(p => p.Id).Select(p => p.CompanyType).FirstOrDefaultAsync(cancellationToken);
            return type is not null && Enum.TryParse<CompanyType>(type, out var parsed) ? CompanyTerms.For(parsed) : CompanyTerms.Default;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Microsoft.EntityFrameworkCore.DbUpdateException)
        {
            return CompanyTerms.Default;
        }
    }

    public async Task<CompanyProfileDto> UpdateAsync(UpdateCompanyProfileRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminOrganization, "change organization settings", cancellationToken);
        if (!Enum.IsDefined(request.Type))
        {
            throw new ArgumentException("Choose a company type.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<OrganizationProfile>().OrderBy(p => p.Id).FirstOrDefaultAsync(cancellationToken);
        var before = row is null ? null : new { row.CompanyType, row.ShowGovernmentFeatures };
        if (row is null)
        {
            db.Set<OrganizationProfile>().Add(row = new OrganizationProfile());
        }

        row.CompanyType = request.Type.ToString();
        row.ShowGovernmentFeatures = request.ShowGovernmentFeatures;
        (row.UpdatedAtUtc, row.UpdatedBy) = (clock.GetUtcNow().UtcDateTime, audit.Actor);
        audit.Record(db, AuditEntity, row.Id, "CompanyProfileChanged", before, new { row.CompanyType, row.ShowGovernmentFeatures });
        await db.SaveChangesAsync(cancellationToken);
        return Build(row, true);
    }

    private static CompanyProfileDto Build(OrganizationProfile? row, bool canEdit)
    {
        var type = row is not null && Enum.TryParse<CompanyType>(row.CompanyType, out var parsed) ? parsed : CompanyType.GovernmentContractor;
        var overridden = row?.ShowGovernmentFeatures is not null;
        return new CompanyProfileDto(type, row?.ShowGovernmentFeatures ?? CompanyTerms.ShowsGovernmentFeaturesByDefault(type), overridden, row is not null, canEdit);
    }
}
