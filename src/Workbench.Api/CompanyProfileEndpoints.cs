using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Company;

namespace Workbench.Api;

/// <summary>The company type and government-features switch (<c>/api/v1/company-profile</c>): read by everyone signed in, changed by Admin.</summary>
internal static class CompanyProfileEndpoints
{
    internal static void MapCompanyProfileEndpoints(this RouteGroupBuilder v1)
    {
        var profile = v1.MapGroup("/company-profile");
        profile.MapGet("", (ICompanyProfileService s, CancellationToken ct) => s.GetAsync(ct));
        profile.MapPut("", (UpdateCompanyProfileRequest r, ICompanyProfileService s, CancellationToken ct) => s.UpdateAsync(r, ct));
    }
}
