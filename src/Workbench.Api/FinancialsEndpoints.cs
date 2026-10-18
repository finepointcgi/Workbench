using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Financials;

namespace Workbench.Api;

internal static class FinancialsEndpoints
{
    public static void MapFinancialsEndpoints(this RouteGroupBuilder v1)
    {
        var budgets = v1.MapGroup("").WithTags("Budgets");
        budgets.MapGet("/projects/{id:guid}/budgets", (Guid id, IBudgetService s, CancellationToken ct) => s.ListVersionsAsync(id, ct));
        budgets.MapPost("/projects/{id:guid}/budgets", async (Guid id, CreateBudgetVersionRequest r, IBudgetService s, CancellationToken ct) =>
        {
            var v = await s.CreateVersionAsync(id, r, ct);
            return Results.Created($"/api/v1/budgets/{v.Version.Id}", v);
        });
        budgets.MapGet("/budgets/{id:guid}", async (Guid id, IBudgetService s, CancellationToken ct) =>
            await s.GetVersionAsync(id, ct) is { } v ? Results.Ok(v) : Results.NotFound());
        budgets.MapPut("/budgets/{id:guid}/lines", (Guid id, List<SaveBudgetLineRequest> r, IBudgetService s, CancellationToken ct) => s.SaveLinesAsync(id, r, ct));
        budgets.MapPost("/budgets/{id:guid}/lock", (Guid id, LockBudgetRequest r, IBudgetService s, CancellationToken ct) => s.LockBaselineAsync(id, r.Reason, ct));
        budgets.MapPost("/budgets/{id:guid}/promote", (Guid id, IBudgetService s, CancellationToken ct) => s.PromoteToCurrentAsync(id, ct));
        budgets.MapPost("/budgets/{id:guid}/reprice", (Guid id, IBudgetService s, CancellationToken ct) => s.RepriceAsync(id, ct));

        var financials = v1.MapGroup("").WithTags("Financials");
        financials.MapGet("/projects/{id:guid}/financials/summary", (Guid id, Guid? budgetVersionId, Guid? forecastVersionId, IProjectFinancialsService s, CancellationToken ct) =>
            s.GetProjectSummaryAsync(id, budgetVersionId, forecastVersionId, ct));
        financials.MapGet("/projects/{id:guid}/financials/wbs", (Guid id, Guid? budgetVersionId, Guid? forecastVersionId, IProjectFinancialsService s, CancellationToken ct) =>
            s.GetWbsFinancialsAsync(id, budgetVersionId, forecastVersionId, ct));
        financials.MapGet("/projects/{id:guid}/financials/time-phased", (Guid id, Guid? budgetVersionId, IProjectFinancialsService s, CancellationToken ct) =>
            s.GetTimePhasedBurnAsync(id, budgetVersionId, ct));
        financials.MapGet("/contracts/funding", (IProjectFinancialsService s, CancellationToken ct) => s.ListContractFundingAsync(ct));
        financials.MapGet("/contracts/{id:guid}/funding", (Guid id, IProjectFinancialsService s, CancellationToken ct) => s.GetContractFundingAsync(id, ct));

        // JSON-wrapped so the desktop client can save the file itself; the .csv routes serve the file to browsers.
        financials.MapGet("/projects/{id:guid}/financials/export/wbs", (Guid id, Guid? budgetVersionId, Guid? forecastVersionId, IFinancialExportService s, CancellationToken ct) =>
            s.ExportWbsCsvAsync(id, budgetVersionId, forecastVersionId, ct));
        financials.MapGet("/projects/{id:guid}/financials/export/time-phased", (Guid id, Guid? budgetVersionId, IFinancialExportService s, CancellationToken ct) =>
            s.ExportTimePhasedCsvAsync(id, budgetVersionId, ct));
        financials.MapGet("/projects/{id:guid}/financials/wbs.csv", async (Guid id, Guid? budgetVersionId, Guid? forecastVersionId, IFinancialExportService s, CancellationToken ct) =>
            File(await s.ExportWbsCsvAsync(id, budgetVersionId, forecastVersionId, ct)));
        financials.MapGet("/projects/{id:guid}/financials/time-phased.csv", async (Guid id, Guid? budgetVersionId, IFinancialExportService s, CancellationToken ct) =>
            File(await s.ExportTimePhasedCsvAsync(id, budgetVersionId, ct)));
    }

    private static IResult File(FinancialExportFile file) =>
        Results.File(System.Text.Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
}
