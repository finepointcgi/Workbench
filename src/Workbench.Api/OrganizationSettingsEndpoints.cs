using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Time;

namespace Workbench.Api;

/// <summary>Organization settings (<c>/api/v1/setup/organization</c>): read by Finance and Admin, changed by Admin.</summary>
internal static class OrganizationSettingsEndpoints
{
    internal static void MapOrganizationSettingsEndpoints(this RouteGroupBuilder v1)
    {
        var org = v1.MapGroup("/setup/organization");
        org.MapGet("", (IOrganizationSettingsService s, CancellationToken ct) => s.GetAsync(ct));
        org.MapPost("/timesheet-cadence/preview", (ChangeTimesheetCadenceRequest request, IOrganizationSettingsService s, CancellationToken ct) =>
            s.PreviewTimesheetCadenceAsync(request, ct));
        org.MapPut("/timesheet-cadence", (ChangeTimesheetCadenceRequest request, IOrganizationSettingsService s, CancellationToken ct) =>
            s.ChangeTimesheetCadenceAsync(request, ct));
    }
}
