using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Payroll;

namespace Workbench.Api;

internal static class PaylocityEndpoints
{
    // Under /payroll, so API keys reach them through the "people" area (employees, hr, payroll).
    public static void MapPaylocityEndpoints(this RouteGroupBuilder v1)
    {
        var p = v1.MapGroup("/payroll/paylocity").WithTags("Payroll").AddEndpointFilter(PayrollLeaveEndpoints.MapPayrollErrors);

        p.MapGet("/mapping", (IPaylocityService s, CancellationToken ct) => s.GetMappingAsync(ct));
        p.MapPut("/cost-centers", (IReadOnlyList<SaveCostCenterMapping> r, IPaylocityService s, CancellationToken ct) => s.SaveCostCentersAsync(r, ct));
        p.MapGet("/exports", (Guid? payGroupId, Guid? payPeriodId, IPaylocityService s, CancellationToken ct) => s.ListExportsAsync(payGroupId, payPeriodId, ct));
        p.MapGet("/exports/{id:guid}/file", (Guid id, IPaylocityService s, CancellationToken ct) => s.GetExportFileAsync(id, ct));
        p.MapGet("/roster/settings", (IPaylocityService s, CancellationToken ct) => s.GetRosterSettingsAsync(ct));
        p.MapPut("/roster/settings", (SavePaylocityRosterSettingsRequest r, IPaylocityService s, CancellationToken ct) => s.SaveRosterSettingsAsync(r, ct));
        p.MapPost("/roster/import", (PaylocityRosterRequest r, IPaylocityService s, CancellationToken ct) => s.ImportRosterAsync(r, ct));
    }
}
