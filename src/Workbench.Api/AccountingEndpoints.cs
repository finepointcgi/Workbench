using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Accounting;

namespace Workbench.Api;

internal static class AccountingEndpoints
{
    public static void MapAccountingEndpoints(this RouteGroupBuilder v1)
    {
        var gl = v1.MapGroup("/accounting").WithTags("Accounting").AddEndpointFilter(MapLedgerErrors);

        gl.MapGet("/accounts", (bool? includeInactive, IChartOfAccountsService s, CancellationToken ct) => s.ListAccountsAsync(includeInactive ?? true, ct));
        gl.MapPost("/accounts", (SaveAccountRequest r, IChartOfAccountsService s, CancellationToken ct) => s.CreateAccountAsync(r, ct));
        gl.MapPut("/accounts/{id:guid}", (Guid id, SaveAccountRequest r, IChartOfAccountsService s, CancellationToken ct) => s.UpdateAccountAsync(id, r, ct));
        gl.MapPost("/accounts/{id:guid}/active", (Guid id, bool isActive, IChartOfAccountsService s, CancellationToken ct) => s.SetActiveAsync(id, isActive, ct));
        gl.MapPost("/accounts/install-default", (IChartOfAccountsService s, CancellationToken ct) => s.InstallDefaultChartAsync(ct));

        gl.MapGet("/journal", (Guid? periodId, JournalEntryState? status, JournalEntrySource? source, string? search, int? take, IJournalService s, CancellationToken ct) =>
            s.ListAsync(new JournalQuery(periodId, status, source, search, take ?? 200), ct));
        gl.MapGet("/journal/{id:guid}", (Guid id, IJournalService s, CancellationToken ct) => s.GetAsync(id, ct));
        gl.MapPost("/journal", (SaveJournalEntryRequest r, IJournalService s, CancellationToken ct) => s.CreateDraftAsync(r, ct));
        gl.MapPost("/journal/post", (SaveJournalEntryRequest r, IJournalService s, CancellationToken ct) => s.CreateAndPostAsync(r, ct));
        gl.MapPut("/journal/{id:guid}", (Guid id, SaveJournalEntryRequest r, IJournalService s, CancellationToken ct) => s.UpdateDraftAsync(id, r, ct));
        gl.MapDelete("/journal/{id:guid}", async (Guid id, IJournalService s, CancellationToken ct) =>
        {
            await s.DeleteDraftAsync(id, ct);
            return Results.NoContent();
        });
        gl.MapPost("/journal/{id:guid}/post", (Guid id, IJournalService s, CancellationToken ct) => s.PostAsync(id, ct));
        gl.MapPost("/journal/{id:guid}/reverse", (Guid id, ReverseJournalEntryRequest r, IJournalService s, CancellationToken ct) => s.ReverseAsync(id, r, ct));

        gl.MapGet("/posting/setup", (IPostingService s, CancellationToken ct) => s.GetSetupAsync(ct));
        gl.MapPut("/posting/rules", (SavePostingRuleRequest r, IPostingService s, CancellationToken ct) => s.SaveRuleAsync(r, ct));
        gl.MapPut("/posting/pools/{wbsId:guid}", (Guid wbsId, SetWbsPoolRequest r, IPostingService s, CancellationToken ct) => s.SetWbsPoolAsync(wbsId, r, ct));
        gl.MapGet("/posting/unposted", (Guid? periodId, IPostingService s, CancellationToken ct) => s.ListUnpostedAsync(periodId, ct));
        gl.MapPost("/posting/backlog", (IPostingService s, CancellationToken ct) => s.PostBacklogAsync(ct));
        gl.MapPost("/posting/timesheets/{id:guid}", (Guid id, IPostingService s, CancellationToken ct) => s.PostTimesheetAsync(id, ct));
        gl.MapPost("/posting/odcs/{id:guid}", (Guid id, IPostingService s, CancellationToken ct) => s.PostOdcAsync(id, ct));

        gl.MapGet("/periods/{id:guid}/close", (Guid id, IPeriodCloseService s, CancellationToken ct) => s.GetChecklistAsync(id, ct));
        gl.MapGet("/periods/{id:guid}/close/companies/{companyId:guid}", (Guid id, Guid companyId, IPeriodCloseService s, CancellationToken ct) => s.GetCompanyChecklistAsync(id, companyId, ct));
        gl.MapPost("/periods/{id:guid}/close", (Guid id, ClosePeriodRequest r, IPeriodCloseService s, CancellationToken ct) => s.CloseAsync(id, r, ct));
        gl.MapPost("/periods/{id:guid}/reopen", (Guid id, ClosePeriodRequest r, IPeriodCloseService s, CancellationToken ct) => s.ReopenAsync(id, r, ct));

        gl.MapGet("/reports/trial-balance/{periodId:guid}", (Guid periodId, IAccountingReportService s, CancellationToken ct) => s.GetTrialBalanceAsync(periodId, ct));
        gl.MapGet("/reports/ledger", (Guid? accountId, DateOnly? from, DateOnly? to, Guid? orgUnitId, Guid? projectId, Guid? wbsElementId, int? take,
            IAccountingReportService s, CancellationToken ct) => s.GetLedgerDetailAsync(new LedgerQuery(accountId, from, to, orgUnitId, projectId, wbsElementId, take ?? 2000), ct));
        gl.MapGet("/reports/month-review/{periodId:guid}", (Guid periodId, IAccountingReportService s, CancellationToken ct) => s.GetMonthReviewAsync(periodId, ct));
        gl.MapGet("/reports/income-statement/{periodId:guid}", (Guid periodId, IAccountingReportService s, CancellationToken ct) => s.GetIncomeStatementAsync(periodId, ct));
        gl.MapGet("/reports/balance-sheet/{periodId:guid}", (Guid periodId, IAccountingReportService s, CancellationToken ct) => s.GetBalanceSheetAsync(periodId, ct));
        gl.MapGet("/reports/project-cost", (Guid? projectId, Guid? periodId, IAccountingReportService s, CancellationToken ct) => s.GetProjectCostLedgerAsync(projectId, periodId, ct));
        gl.MapGet("/reports/reconciliation", (Guid? projectId, IAccountingReportService s, CancellationToken ct) => s.GetReconciliationAsync(projectId, ct));

        // JSON-wrapped so the desktop client can save the file; the .csv route serves the file to browsers.
        gl.MapPost("/reports/export", (AccountingExportRequest r, IAccountingReportService s, CancellationToken ct) => s.ExportCsvAsync(r, ct));
        gl.MapGet("/reports/{report}.csv", async (AccountingReport report, Guid? periodId, Guid? projectId, Guid? accountId, DateOnly? from, DateOnly? to,
            Guid? orgUnitId, Guid? wbsElementId, IAccountingReportService s, CancellationToken ct) =>
        {
            var file = await s.ExportCsvAsync(new AccountingExportRequest(report, periodId, new LedgerQuery(accountId, from, to, orgUnitId, projectId, wbsElementId), projectId), ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
        });
    }

    /// <summary>Wrong state (already posted or reversed, close checklist failing) → 409.</summary>
    private static async ValueTask<object?> MapLedgerErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
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
