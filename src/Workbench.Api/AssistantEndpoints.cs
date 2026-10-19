using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Assistant;

namespace Workbench.Api;

internal static class AssistantEndpoints
{
    public static void MapAssistantEndpoints(this RouteGroupBuilder v1)
    {
        var assistant = v1.MapGroup("/assistant").WithTags("Assistant");
        assistant.MapGet("/status", (IAssistantService s, CancellationToken ct) => s.GetStatusAsync(ct));

        // Streams the events as a JSON array (one element per event, flushed as they come).
        assistant.MapPost("/ask", (AskAssistantRequest request, IAssistantService s, CancellationToken ct) => s.AskAsync(request, ct));

        assistant.MapGet("/settings", (IAssistantAdminService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        assistant.MapPut("/settings", (SaveAssistantSettingsRequest r, IAssistantAdminService s, CancellationToken ct) => s.SaveSettingsAsync(r, ct));
        assistant.MapGet("/usage", (int? year, int? month, IAssistantAdminService s, CancellationToken ct) => s.GetUsageAsync(year, month, ct));
        assistant.MapGet("/audit", (DateOnly? from, DateOnly? to, string? user, int? take, IAssistantAdminService s, CancellationToken ct) =>
            s.ListAuditAsync(new AssistantAuditQuery(from, to, user, take ?? 200), ct));

        assistant.MapGet("/projects/{id:guid}/narrative/periods", (Guid id, IProjectNarrativeService s, CancellationToken ct) => s.ListPeriodsAsync(id, ct));
        assistant.MapGet("/projects/{id:guid}/narrative", async (Guid id, Guid? periodId, IProjectNarrativeService s, CancellationToken ct) =>
            await s.GetAsync(id, periodId, ct) is { } n ? Results.Ok(n) : Results.NoContent());
        assistant.MapPost("/projects/{id:guid}/narrative/draft", (Guid id, Guid? periodId, IProjectNarrativeService s, CancellationToken ct) => s.DraftAsync(id, periodId, ct));
        assistant.MapPut("/projects/{id:guid}/narrative", (Guid id, SaveProjectNarrativeRequest r, IProjectNarrativeService s, CancellationToken ct) => s.SaveAsync(id, r, ct));

        assistant.MapPost("/timesheet-flags", (TimesheetIdsRequest r, ITimesheetFlagService s, CancellationToken ct) => s.ListForTimesheetsAsync(r.TimesheetIds, ct));
        assistant.MapGet("/timesheet-flags/{id:guid}", (Guid id, ITimesheetFlagService s, CancellationToken ct) => s.GetAsync(id, ct));
        assistant.MapGet("/timesheet-flags/report", (DateOnly from, DateOnly to, string? rule, ITimesheetFlagService s, CancellationToken ct) =>
            s.ReportAsync(new TimesheetFlagReportQuery(from, to, rule), ct));
        assistant.MapPost("/timesheet-flags/{id:guid}/explain/{rule}", (Guid id, string rule, ITimesheetFlagService s, CancellationToken ct) => s.ExplainAsync(id, rule, ct));
        assistant.MapGet("/timesheet-submissions", (DateOnly? date, ITimesheetSubmissionService s, CancellationToken ct) =>
            s.GetStatusAsync(date ?? DateOnly.FromDateTime(DateTime.UtcNow), ct));
    }
}

/// <summary>Body of POST /assistant/timesheet-flags.</summary>
public sealed record TimesheetIdsRequest(IReadOnlyList<Guid> TimesheetIds);
