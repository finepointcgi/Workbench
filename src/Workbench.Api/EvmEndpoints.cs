using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Evm;

namespace Workbench.Api;

internal static class EvmEndpoints
{
    public static void MapEvmEndpoints(this RouteGroupBuilder v1)
    {
        var evm = v1.MapGroup("").WithTags("Earned value");
        evm.MapGet("/projects/{id:guid}/evm/baseline", async (Guid id, IEvmService s, CancellationToken ct) =>
            await s.GetBaselineAsync(id, ct) is { } b ? Results.Ok(b) : Results.NoContent());
        evm.MapPost("/projects/{id:guid}/evm/baseline", (Guid id, CreateEvmBaselineRequest r, IEvmService s, CancellationToken ct) => s.CreateBaselineAsync(id, r, ct));
        evm.MapPut("/projects/{id:guid}/evm/thresholds", (Guid id, SetEvmThresholdsRequest r, IEvmService s, CancellationToken ct) => s.SetThresholdsAsync(id, r, ct));
        evm.MapPut("/evm/work-packages/{id:guid}", (Guid id, UpdateWorkPackageRequest r, IEvmService s, CancellationToken ct) => s.UpdateWorkPackageAsync(id, r, ct));
        evm.MapPut("/evm/work-packages/{id:guid}/progress", (Guid id, SetWorkPackageProgressRequest r, IEvmService s, CancellationToken ct) => s.SetProgressAsync(id, r, ct));
        evm.MapPut("/evm/milestones/{id:guid}/completion", (Guid id, SetMilestoneCompletionRequest r, IEvmService s, CancellationToken ct) => s.SetMilestoneCompletionAsync(id, r, ct));
        evm.MapGet("/projects/{id:guid}/evm/periods", (Guid id, IEvmService s, CancellationToken ct) => s.ListPeriodsAsync(id, ct));
        evm.MapGet("/projects/{id:guid}/evm/status", (Guid id, Guid? periodId, IEvmService s, CancellationToken ct) => s.GetStatusAsync(id, periodId, ct));
        evm.MapGet("/projects/{id:guid}/evm/series", (Guid id, IEvmService s, CancellationToken ct) => s.GetSeriesAsync(id, ct));
        evm.MapPost("/projects/{id:guid}/evm/periods/{periodId:guid}/close", (Guid id, Guid periodId, IEvmService s, CancellationToken ct) => s.ClosePeriodAsync(id, periodId, ct));
        evm.MapPost("/projects/{id:guid}/evm/periods/{periodId:guid}/reopen", (Guid id, Guid periodId, ReopenEvmPeriodRequest r, IEvmService s, CancellationToken ct) =>
            s.ReopenPeriodAsync(id, periodId, r, ct));
        evm.MapGet("/projects/{id:guid}/evm/variances", (Guid id, Guid? periodId, IEvmService s, CancellationToken ct) => s.ListVariancesAsync(id, periodId, ct));
        evm.MapPut("/projects/{id:guid}/evm/periods/{periodId:guid}/variances/{caId:guid}", (Guid id, Guid periodId, Guid caId, SaveVarianceRequest r, IEvmService s, CancellationToken ct) =>
            s.SaveVarianceAsync(id, periodId, caId, r, ct));

        evm.MapGet("/projects/{id:guid}/evm/change-requests", (Guid id, IEvmChangeRequestService s, CancellationToken ct) => s.ListAsync(id, ct));
        evm.MapPost("/projects/{id:guid}/evm/change-requests", (Guid id, CreateChangeRequestRequest r, IEvmChangeRequestService s, CancellationToken ct) => s.CreateAsync(id, r, ct));
        evm.MapGet("/evm/change-requests/{id:guid}", (Guid id, IEvmChangeRequestService s, CancellationToken ct) => s.GetAsync(id, ct));
        evm.MapPost("/evm/change-requests/{id:guid}/approve", (Guid id, DecideChangeRequestRequest r, IEvmChangeRequestService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        evm.MapPost("/evm/change-requests/{id:guid}/reject", (Guid id, DecideChangeRequestRequest r, IEvmChangeRequestService s, CancellationToken ct) => s.RejectAsync(id, r, ct));

        // JSON-wrapped for the desktop client; the .xlsx routes serve the file to browsers.
        evm.MapGet("/projects/{id:guid}/evm/export/format1", (Guid id, Guid? periodId, IEvmExportService s, CancellationToken ct) => s.ExportFormat1Async(id, periodId, ct));
        evm.MapGet("/projects/{id:guid}/evm/export/format5", (Guid id, Guid? periodId, IEvmExportService s, CancellationToken ct) => s.ExportFormat5Async(id, periodId, ct));
        evm.MapGet("/projects/{id:guid}/evm/format1.xlsx", async (Guid id, Guid? periodId, IEvmExportService s, CancellationToken ct) => File(await s.ExportFormat1Async(id, periodId, ct)));
        evm.MapGet("/projects/{id:guid}/evm/format5.xlsx", async (Guid id, Guid? periodId, IEvmExportService s, CancellationToken ct) => File(await s.ExportFormat5Async(id, periodId, ct)));
    }

    private static IResult File(EvmExportFile file) => Results.File(file.Content, file.ContentType, file.FileName);
}
