using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Companies;

namespace Workbench.Api;

internal static class CompanyEndpoints
{
    public static void MapCompanyEndpoints(this RouteGroupBuilder v1)
    {
        var companies = v1.MapGroup("/companies").WithTags("Companies");
        companies.MapGet("/context", (ICompanyService s, CancellationToken ct) => s.GetContextAsync(ct));
        companies.MapPut("/context", (SelectCompanyRequest r, ICompanyService s, CancellationToken ct) => s.SelectAsync(r, ct));
        companies.MapGet("", (bool? includeInactive, ICompanyService s, CancellationToken ct) => s.ListAsync(includeInactive ?? true, ct));
        companies.MapPost("", (SaveCompanyRequest r, ICompanyService s, CancellationToken ct) => s.CreateAsync(r, ct));
        companies.MapPut("/{id:guid}", (Guid id, SaveCompanyRequest r, ICompanyService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        companies.MapGet("/access", (ICompanyService s, CancellationToken ct) => s.ListUserAccessAsync(ct));
        companies.MapPut("/access/{userId:guid}", (Guid userId, SetUserCompanyAccessRequest r, ICompanyService s, CancellationToken ct) => s.SetUserAccessAsync(userId, r, ct));
        companies.MapGet("/records/{kind}", (CompanyRecordKind kind, ICompanyService s, CancellationToken ct) => s.ListRecordsAsync(kind, ct));
        companies.MapPost("/records/assign", (AssignCompanyRequest r, ICompanyService s, CancellationToken ct) => s.AssignAsync(r, ct));
        companies.MapGet("/periods/{periodId:guid}", (Guid periodId, ICompanyService s, CancellationToken ct) => s.ListPeriodsAsync(periodId, ct));
        companies.MapPut("/{id:guid}/periods/{periodId:guid}", (Guid id, Guid periodId, SetCompanyPeriodRequest r, ICompanyService s, CancellationToken ct) =>
            s.SetPeriodClosedAsync(id, periodId, r, ct));

        companies.MapGet("/intercompany", (IIntercompanyService s, CancellationToken ct) => s.ListAsync(ct));
        companies.MapPut("/intercompany", (SaveIntercompanyAccountRequest r, IIntercompanyService s, CancellationToken ct) => s.SaveAsync(r, ct));
        companies.MapGet("/intercompany/balances/{periodId:guid}", (Guid periodId, IIntercompanyService s, CancellationToken ct) => s.ListBalancesAsync(periodId, ct));

        companies.MapPost("/consolidation/{report}", (ConsolidatedReport report, ConsolidationRequest r, IConsolidationService s, CancellationToken ct) =>
            s.GetAsync(report, r, ct));
    }
}
