using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Licensing;

namespace Workbench.Api;

/// <summary>The edition, its seats and features, and entering or removing a license key.</summary>
internal static class LicenseEndpoints
{
    public static void MapLicenseEndpoints(this RouteGroupBuilder v1)
    {
        var license = v1.MapGroup("/license").WithTags("License");
        license.MapGet("", (ILicenseService s, CancellationToken ct) => s.GetStatusAsync(ct));
        license.MapPut("", (ActivateLicenseRequest r, ILicenseService s, CancellationToken ct) => s.ActivateAsync(r, ct));
        license.MapDelete("", (ILicenseService s, CancellationToken ct) => s.RemoveAsync(ct));
    }
}
