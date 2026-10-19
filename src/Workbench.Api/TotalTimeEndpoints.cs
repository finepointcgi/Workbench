using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.TotalTime;

namespace Workbench.Api;

internal static class TotalTimeEndpoints
{
    public static void MapTotalTimeEndpoints(this RouteGroupBuilder v1)
    {
        var tta = v1.MapGroup("/total-time").WithTags("Total time accounting");
        tta.MapGet("/settings", (ITotalTimeService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        tta.MapPost("/settings", (SetLaborCostMethodRequest r, ITotalTimeService s, CancellationToken ct) => s.SetMethodAsync(r, ct));
        tta.MapGet("/timesheets/{id:guid}", (Guid id, ITotalTimeService s, CancellationToken ct) => s.GetTimesheetSummaryAsync(id, ct));
        tta.MapGet("/report", (DateOnly from, DateOnly to, ITotalTimeService s, CancellationToken ct) => s.GetReportAsync(from, to, ct));
    }
}
