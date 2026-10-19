using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Tasks;

namespace Workbench.Api;

internal static class TaskEndpoints
{
    public static void MapTaskEndpoints(this RouteGroupBuilder v1)
    {
        var sheet = v1.MapGroup("/sheets/{sheetId:guid}/tasks").WithTags("Tasks");
        sheet.MapGet("", (Guid sheetId, ITaskAssignmentService s, CancellationToken ct) => s.GetSheetAsync(sheetId, ct));
        sheet.MapPut("/{itemId:guid}/assignees", (Guid sheetId, Guid itemId, SetAssigneesRequest r, ITaskAssignmentService s, CancellationToken ct) =>
            s.SetAssigneesAsync(sheetId, itemId, r, ct));
        sheet.MapPost("/{itemId:guid}/status", (Guid sheetId, Guid itemId, UpdateTaskStatusRequest r, ITaskAssignmentService s, CancellationToken ct) =>
            s.UpdateStatusAsync(sheetId, itemId, r, ct));
        v1.MapGet("/my-tasks", (bool? includeDone, ITaskAssignmentService s, CancellationToken ct) => s.ListMineAsync(includeDone ?? false, ct)).WithTags("Tasks");
        v1.MapGet("/projects/{projectId:guid}/workload", (Guid projectId, ITaskAssignmentService s, CancellationToken ct) =>
            s.GetProjectWorkloadAsync(projectId, ct)).WithTags("Tasks");
    }
}
