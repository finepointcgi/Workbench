using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Api;

internal static class ProjectAccountingEndpoints
{
    public static void MapProjectAccountingEndpoints(this RouteGroupBuilder v1)
    {
        var people = v1.MapGroup("").WithTags("People");
        people.MapGet("/employees", (bool? includeInactive, IPeopleService s, CancellationToken ct) => s.ListEmployeesAsync(includeInactive ?? false, ct));
        people.MapGet("/employees/me", async (IPeopleService s, CancellationToken ct) =>
            await s.GetCurrentEmployeeAsync(ct) is { } e ? Results.Ok(e) : Results.NotFound());
        people.MapGet("/employees/{id:guid}", async (Guid id, IPeopleService s, CancellationToken ct) =>
            await s.GetEmployeeAsync(id, ct) is { } e ? Results.Ok(e) : Results.NotFound());
        people.MapPost("/employees", async (SaveEmployeeRequest r, IPeopleService s, CancellationToken ct) =>
        {
            var e = await s.CreateEmployeeAsync(r, ct);
            return Results.Created($"/api/v1/employees/{e.Id}", e);
        });
        people.MapPut("/employees/{id:guid}", (Guid id, SaveEmployeeRequest r, IPeopleService s, CancellationToken ct) => s.UpdateEmployeeAsync(id, r, ct));
        people.MapGet("/employees/{id:guid}/cost-rates", (Guid id, IPeopleService s, CancellationToken ct) => s.ListCostRatesAsync(id, ct));
        people.MapPost("/employees/{id:guid}/cost-rates", (Guid id, AddCostRateRequest r, IPeopleService s, CancellationToken ct) => s.AddCostRateAsync(id, r, ct));
        people.MapGet("/org-units", (IPeopleService s, CancellationToken ct) => s.ListOrgUnitsAsync(ct));
        people.MapPost("/org-units", (CreateOrgUnitRequest r, IPeopleService s, CancellationToken ct) => s.CreateOrgUnitAsync(r, ct));
        people.MapGet("/labor-categories", (IPeopleService s, CancellationToken ct) => s.ListLaborCategoriesAsync(ct));
        people.MapPost("/labor-categories", (SaveLaborCategoryRequest r, IPeopleService s, CancellationToken ct) => s.CreateLaborCategoryAsync(r, ct));
        people.MapPut("/labor-categories/{id:guid}", (Guid id, SaveLaborCategoryRequest r, IPeopleService s, CancellationToken ct) => s.UpdateLaborCategoryAsync(id, r, ct));

        var contracts = v1.MapGroup("").WithTags("Contracts");
        contracts.MapGet("/customers", (IContractService s, CancellationToken ct) => s.ListCustomersAsync(ct));
        contracts.MapPost("/customers", (SaveCustomerRequest r, IContractService s, CancellationToken ct) => s.CreateCustomerAsync(r, ct));
        contracts.MapGet("/contracts", (IContractService s, CancellationToken ct) => s.ListContractsAsync(ct));
        contracts.MapGet("/contracts/{id:guid}", async (Guid id, IContractService s, CancellationToken ct) =>
            await s.GetContractAsync(id, ct) is { } c ? Results.Ok(c) : Results.NotFound());
        contracts.MapPost("/contracts", (SaveContractRequest r, IContractService s, CancellationToken ct) => s.CreateContractAsync(r, ct));
        contracts.MapPut("/contracts/{id:guid}", (Guid id, SaveContractRequest r, IContractService s, CancellationToken ct) => s.UpdateContractAsync(id, r, ct));
        contracts.MapGet("/contracts/{id:guid}/bill-rates", (Guid id, IContractService s, CancellationToken ct) => s.ListBillRatesAsync(id, ct));
        contracts.MapPost("/contracts/{id:guid}/bill-rates", (Guid id, AddBillRateRequest r, IContractService s, CancellationToken ct) => s.AddBillRateAsync(id, r, ct));

        var projects = v1.MapGroup("").WithTags("Projects");
        projects.MapGet("/projects", (IProjectService s, CancellationToken ct) => s.ListProjectsAsync(ct));
        projects.MapGet("/projects/{id:guid}", async (Guid id, IProjectService s, CancellationToken ct) =>
            await s.GetProjectAsync(id, ct) is { } p ? Results.Ok(p) : Results.NotFound());
        projects.MapGet("/projects/by-sheet/{sheetId:guid}", async (Guid sheetId, IProjectService s, CancellationToken ct) =>
            await s.GetProjectForSheetAsync(sheetId, ct) is { } p ? Results.Ok(p) : Results.NotFound());
        projects.MapPost("/projects", (SaveProjectRequest r, IProjectService s, CancellationToken ct) => s.CreateProjectAsync(r, ct));
        projects.MapPut("/projects/{id:guid}", (Guid id, SaveProjectRequest r, IProjectService s, CancellationToken ct) => s.UpdateProjectAsync(id, r, ct));
        projects.MapGet("/projects/{id:guid}/wbs", (Guid id, IProjectService s, CancellationToken ct) => s.ListWbsAsync(id, ct));
        projects.MapPost("/projects/{id:guid}/wbs", (Guid id, SaveWbsElementRequest r, IProjectService s, CancellationToken ct) => s.CreateWbsElementAsync(id, r, ct));
        projects.MapPut("/wbs/{id:guid}", (Guid id, SaveWbsElementRequest r, IProjectService s, CancellationToken ct) => s.UpdateWbsElementAsync(id, r, ct));
        projects.MapGet("/wbs/{id:guid}/assignments", (Guid id, IProjectService s, CancellationToken ct) => s.ListAssignmentsAsync(id, ct));
        projects.MapPost("/wbs/{id:guid}/assignments", (Guid id, AddChargeAssignmentRequest r, IProjectService s, CancellationToken ct) => s.AddAssignmentAsync(id, r, ct));
        projects.MapDelete("/assignments/{id:guid}", async (Guid id, IProjectService s, CancellationToken ct) =>
        {
            await s.RemoveAssignmentAsync(id, ct);
            return Results.NoContent();
        });
        projects.MapGet("/employees/{id:guid}/charge-codes", (Guid id, DateOnly date, IProjectService s, CancellationToken ct) => s.ListChargeCodesAsync(id, date, ct));

        var calendar = v1.MapGroup("/fiscal").WithTags("Fiscal calendar");
        calendar.MapGet("/periods", (int? year, IFiscalCalendarService s, CancellationToken ct) => s.ListPeriodsAsync(year, ct));
        calendar.MapGet("/periods/for-date", async (DateOnly date, IFiscalCalendarService s, CancellationToken ct) =>
            await s.GetPeriodForDateAsync(date, ct) is { } p ? Results.Ok(p) : Results.NotFound());
        calendar.MapPost("/years", (CreateFiscalYearRequest r, IFiscalCalendarService s, CancellationToken ct) => s.CreateFiscalYearAsync(r.FiscalYear, r.Start, ct));
        calendar.MapPut("/periods/{id:guid}/status", (Guid id, SetPeriodStatusRequest r, IFiscalCalendarService s, CancellationToken ct) => s.SetPeriodStatusAsync(id, r.Status, r.Reason, ct));

        v1.MapGet("/audit", (string? entityType, Guid? entityId, int? limit, IAuditLogService s, CancellationToken ct) =>
            s.ListAsync(entityType, entityId, limit ?? 200, ct)).WithTags("Audit");
    }
}

internal sealed record CreateFiscalYearRequest(int FiscalYear, DateOnly Start);

internal sealed record SetPeriodStatusRequest(FiscalPeriodState Status, string Reason);
