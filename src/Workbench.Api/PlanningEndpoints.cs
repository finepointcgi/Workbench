using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Planning;

namespace Workbench.Api;

internal static class PlanningEndpoints
{
    public static void MapPlanningEndpoints(this RouteGroupBuilder v1)
    {
        var rj = v1.MapGroup("/accounting/recurring-journals").WithTags("Recurring journals").AddEndpointFilter(MapConflicts);
        rj.MapGet("", (bool? includeInactive, IRecurringJournalService s, CancellationToken ct) => s.ListTemplatesAsync(includeInactive ?? true, ct));
        rj.MapPost("", (SaveRecurringTemplateRequest r, IRecurringJournalService s, CancellationToken ct) => s.CreateTemplateAsync(r, ct));
        rj.MapGet("/history", (Guid? templateId, int? take, IRecurringJournalService s, CancellationToken ct) => s.ListHistoryAsync(templateId, take ?? 200, ct));
        rj.MapPost("/generate", (GenerateRecurringRequest r, IRecurringJournalService s, CancellationToken ct) => s.GenerateDueAsync(r, ct));
        rj.MapGet("/settings", (IRecurringJournalService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        rj.MapPut("/settings", (SaveRecurringSettingsRequest r, IRecurringJournalService s, CancellationToken ct) => s.SaveSettingsAsync(r, ct));
        rj.MapGet("/{id:guid}", (Guid id, IRecurringJournalService s, CancellationToken ct) => s.GetTemplateAsync(id, ct));
        rj.MapPut("/{id:guid}", (Guid id, SaveRecurringTemplateRequest r, IRecurringJournalService s, CancellationToken ct) => s.UpdateTemplateAsync(id, r, ct));
        rj.MapPost("/{id:guid}/active", (Guid id, bool value, IRecurringJournalService s, CancellationToken ct) => s.SetActiveAsync(id, value, ct));
        rj.MapGet("/{id:guid}/schedule", (Guid id, int? upcoming, IRecurringJournalService s, CancellationToken ct) => s.GetScheduleAsync(id, upcoming ?? 6, ct));

        var b = v1.MapGroup("/planning/budgets").WithTags("Budgets").AddEndpointFilter(MapConflicts);
        b.MapGet("", (int? fiscalYear, IGlBudgetService s, CancellationToken ct) => s.ListVersionsAsync(fiscalYear, ct));
        b.MapPost("", (CreateBudgetVersionRequest r, IGlBudgetService s, CancellationToken ct) => s.CreateVersionAsync(r, ct));
        b.MapGet("/{id:guid}/grid", (Guid id, Guid? orgUnitId, IGlBudgetService s, CancellationToken ct) => s.GetGridAsync(id, orgUnitId, ct));
        b.MapPut("/{id:guid}/cells", (Guid id, SaveBudgetCellsRequest r, IGlBudgetService s, CancellationToken ct) => s.SaveCellsAsync(id, r, ct));
        b.MapPost("/{id:guid}/spread", (Guid id, SpreadBudgetRequest r, IGlBudgetService s, CancellationToken ct) => s.SpreadAsync(id, r, ct));
        b.MapPost("/{id:guid}/import", (Guid id, ImportBudgetCsvRequest r, IGlBudgetService s, CancellationToken ct) => s.ImportCsvAsync(id, r, ct));
        b.MapPost("/{id:guid}/adopt", (Guid id, IGlBudgetService s, CancellationToken ct) => s.AdoptAsync(id, ct));

        var r = v1.MapGroup("/planning/reports").WithTags("Budgets").AddEndpointFilter(MapConflicts);
        r.MapGet("/income-statement", (Guid versionId, Guid periodId, Guid? orgUnitId, IBudgetReportService s, CancellationToken ct) =>
            s.GetIncomeStatementAsync(new BudgetVsActualQuery(versionId, periodId, orgUnitId), ct));
        r.MapGet("/by-org-unit", (Guid versionId, Guid periodId, Guid? orgUnitId, IBudgetReportService s, CancellationToken ct) =>
            s.GetByOrgUnitAsync(new BudgetVsActualQuery(versionId, periodId, orgUnitId), ct));
        r.MapGet("/by-pool", (Guid versionId, Guid periodId, Guid? orgUnitId, IBudgetReportService s, CancellationToken ct) =>
            s.GetByPoolAsync(new BudgetVsActualQuery(versionId, periodId, orgUnitId), ct));
        r.MapGet("/budgeted-rates/{fiscalYear:int}", (int fiscalYear, Guid? versionId, IBudgetReportService s, CancellationToken ct) => s.GetBudgetedRatesAsync(fiscalYear, versionId, ct));
        r.MapGet("/export", (BudgetReport report, Guid versionId, Guid periodId, Guid? orgUnitId, IBudgetReportService s, CancellationToken ct) =>
            s.ExportCsvAsync(report, new BudgetVsActualQuery(versionId, periodId, orgUnitId), ct));

        var c = v1.MapGroup("/planning/cycles").WithTags("Planning cycles").AddEndpointFilter(MapConflicts);
        c.MapGet("", (IPlanningCycleService s, CancellationToken ct) => s.ListCyclesAsync(ct));
        c.MapPost("", (OpenPlanningCycleRequest req, IPlanningCycleService s, CancellationToken ct) => s.OpenCycleAsync(req, ct));
        c.MapGet("/{id:guid}", (Guid id, IPlanningCycleService s, CancellationToken ct) => s.GetCycleAsync(id, ct));
        c.MapPost("/{id:guid}/assignments", (Guid id, CycleAssignmentInput req, IPlanningCycleService s, CancellationToken ct) => s.AssignOwnerAsync(id, req, ct));
        c.MapPost("/{id:guid}/adopt", (Guid id, IPlanningCycleService s, CancellationToken ct) => s.AdoptAsync(id, ct));

        var sub = v1.MapGroup("/planning/submissions").WithTags("Planning cycles").AddEndpointFilter(MapConflicts);
        sub.MapGet("/mine", (IPlanningCycleService s, CancellationToken ct) => s.ListMineAsync(ct));
        sub.MapGet("/{id:guid}", (Guid id, IPlanningCycleService s, CancellationToken ct) => s.GetSubmissionAsync(id, ct));
        sub.MapPut("/{id:guid}/cells", (Guid id, SaveSubmissionCellsRequest req, IPlanningCycleService s, CancellationToken ct) => s.SaveSubmissionCellsAsync(id, req, ct));
        sub.MapPost("/{id:guid}/spread", (Guid id, SpreadSubmissionRequest req, IPlanningCycleService s, CancellationToken ct) => s.SpreadSubmissionAsync(id, req, ct));
        sub.MapPost("/{id:guid}/submit", (Guid id, IPlanningCycleService s, CancellationToken ct) => s.SubmitAsync(id, ct));
        sub.MapPost("/{id:guid}/recall", (Guid id, IPlanningCycleService s, CancellationToken ct) => s.RecallAsync(id, ct));
        sub.MapPost("/{id:guid}/approve", (Guid id, DecideSubmissionRequest req, IPlanningCycleService s, CancellationToken ct) => s.ApproveAsync(id, req, ct));
        sub.MapPost("/{id:guid}/return", (Guid id, DecideSubmissionRequest req, IPlanningCycleService s, CancellationToken ct) => s.ReturnAsync(id, req, ct));
    }

    /// <summary>Wrong state (a locked version, a closed cycle, a budget waiting for approval) → 409.</summary>
    private static async ValueTask<object?> MapConflicts(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}
