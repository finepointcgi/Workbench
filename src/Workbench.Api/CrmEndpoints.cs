using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Crm;

namespace Workbench.Api;

internal static class CrmEndpoints
{
    /// <summary>Request body for a CSV lead import.</summary>
    public sealed record CsvBody(string Csv);

    /// <summary>Request body for linking an account to a customer.</summary>
    public sealed record LinkCustomerBody(Guid? CustomerId);

    /// <summary>Request body for marking a milestone done or not.</summary>
    public sealed record DoneBody(bool Done);

    /// <summary>Request body for choosing the B&amp;P parent element.</summary>
    public sealed record BpParentBody(Guid? WbsElementId);
    public sealed record CompanyProfileBody(string? Profile);

    public static void MapCrmEndpoints(this RouteGroupBuilder v1)
    {
        var o = v1.MapGroup("/crm/opportunities").WithTags("Business development");
        o.MapPost("/search", (OpportunityFilter? f, IOpportunityService s, CancellationToken ct) => s.ListAsync(f, ct));
        o.MapGet("", (IOpportunityService s, CancellationToken ct) => s.ListAsync(null, ct));
        o.MapGet("/{id:guid}", (Guid id, IOpportunityService s, CancellationToken ct) => s.GetAsync(id, ct));
        o.MapPost("", (SaveOpportunityRequest r, IOpportunityService s, CancellationToken ct) => s.CreateAsync(r, ct));
        o.MapPut("/{id:guid}", (Guid id, SaveOpportunityRequest r, IOpportunityService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        o.MapPost("/{id:guid}/stage", (Guid id, ChangeStageRequest r, IOpportunityService s, CancellationToken ct) => s.ChangeStageAsync(id, r, ct));
        o.MapPost("/{id:guid}/outcome", (Guid id, RecordOutcomeRequest r, IOpportunityService s, CancellationToken ct) => s.RecordOutcomeAsync(id, r, ct));
        o.MapPost("/{id:guid}/reopen", (Guid id, ChangeStageRequest r, IOpportunityService s, CancellationToken ct) => s.ReopenAsync(id, r, ct));
        o.MapGet("/{id:guid}/history", (Guid id, IOpportunityService s, CancellationToken ct) => s.GetHistoryAsync(id, ct));
        o.MapGet("/{id:guid}/handoff-status", (Guid id, IOpportunityService s, CancellationToken ct) => s.GetHandoffStatusAsync(id, ct));
        o.MapPost("/{id:guid}/handoff", (Guid id, HandoffRequest r, IOpportunityService s, CancellationToken ct) => s.HandoffAsync(id, r, ct));
        o.MapPost("/{id:guid}/bp-charge-code", (Guid id, IOpportunityService s, CancellationToken ct) => s.EnsureBpChargeCodeAsync(id, ct));
        o.MapGet("/{id:guid}/gate-reviews", (Guid id, IOpportunityGateService s, CancellationToken ct) => s.ListReviewsAsync(id, ct));
        o.MapPost("/{id:guid}/gate-reviews", (Guid id, SubmitGateReviewRequest r, IOpportunityGateService s, CancellationToken ct) => s.SubmitAsync(id, r, ct));

        o.MapGet("/{id:guid}/proposal", (Guid id, IProposalService s, CancellationToken ct) => s.GetAsync(id, ct));
        o.MapPost("/{id:guid}/proposal/draft", (Guid id, IProposalService s, CancellationToken ct) => s.DraftAsync(id, ct));
        o.MapGet("/{id:guid}/proposal/export", async (Guid id, IProposalService s, CancellationToken ct) =>
        {
            var file = await s.ExportAsync(id, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        o.MapPost("/proposal-sections/{sectionId:guid}/redraft", (Guid sectionId, IProposalService s, CancellationToken ct) => s.RedraftSectionAsync(sectionId, ct));
        o.MapPut("/proposal-sections/{sectionId:guid}", (Guid sectionId, SaveProposalSectionRequest r, IProposalService s, CancellationToken ct) => s.SaveSectionAsync(sectionId, r, ct));
        o.MapGet("/{id:guid}/requirements", (Guid id, IRequirementService s, CancellationToken ct) => s.GetAsync(id, ct));
        o.MapPost("/{id:guid}/requirements/shred", (Guid id, ShredRequest r, IRequirementService s, CancellationToken ct) => s.ShredAsync(id, r, ct));
        o.MapPost("/{id:guid}/requirements", (Guid id, SaveRequirementRequest r, IRequirementService s, CancellationToken ct) => s.AddAsync(id, r, ct));
        o.MapGet("/{id:guid}/requirements/export", async (Guid id, IRequirementService s, CancellationToken ct) =>
        {
            var file = await s.ExportAsync(id, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
        o.MapPut("/requirements/{requirementId:guid}", (Guid requirementId, SaveRequirementRequest r, IRequirementService s, CancellationToken ct) => s.UpdateAsync(requirementId, r, ct));
        o.MapDelete("/requirements/{requirementId:guid}", async (Guid requirementId, IRequirementService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(requirementId, ct);
            return Results.NoContent();
        });
        o.MapGet("/{id:guid}/capture-plan", (Guid id, ICapturePlanService s, CancellationToken ct) => s.GetAsync(id, ct));
        o.MapPost("/{id:guid}/capture-plan/items", (Guid id, SavePlanItemRequest r, ICapturePlanService s, CancellationToken ct) => s.AddItemAsync(id, r, ct));
        o.MapPost("/{id:guid}/milestones", (Guid id, SaveMilestoneRequest r, ICapturePlanService s, CancellationToken ct) => s.AddMilestoneAsync(id, r, ct));
        o.MapPost("/{id:guid}/milestones/standard", (Guid id, ICapturePlanService s, CancellationToken ct) => s.AddStandardScheduleAsync(id, ct));
        o.MapPut("/{id:guid}/pwin-assessment", (Guid id, SavePwinAssessmentRequest r, ICapturePlanService s, CancellationToken ct) => s.SaveAssessmentAsync(id, r, ct));
        var pi = v1.MapGroup("/crm/capture-plan-items").WithTags("Business development");
        pi.MapPut("/{id:guid}", (Guid id, SavePlanItemRequest r, ICapturePlanService s, CancellationToken ct) => s.UpdateItemAsync(id, r, ct));
        pi.MapDelete("/{id:guid}", async (Guid id, ICapturePlanService s, CancellationToken ct) =>
        {
            await s.DeleteItemAsync(id, ct);
            return Results.NoContent();
        });
        var ms = v1.MapGroup("/crm/milestones").WithTags("Business development");
        ms.MapPut("/{id:guid}", (Guid id, SaveMilestoneRequest r, ICapturePlanService s, CancellationToken ct) => s.UpdateMilestoneAsync(id, r, ct));
        ms.MapPost("/{id:guid}/done", (Guid id, DoneBody r, ICapturePlanService s, CancellationToken ct) => s.SetMilestoneDoneAsync(id, r.Done, ct));
        ms.MapDelete("/{id:guid}", async (Guid id, ICapturePlanService s, CancellationToken ct) =>
        {
            await s.DeleteMilestoneAsync(id, ct);
            return Results.NoContent();
        });

        var g = v1.MapGroup("/crm/gates").WithTags("Business development");
        g.MapGet("", (bool? includeInactive, IOpportunityGateService s, CancellationToken ct) => s.ListGatesAsync(includeInactive ?? false, ct));
        g.MapPost("", (SaveGateDefinitionRequest r, IOpportunityGateService s, CancellationToken ct) => s.CreateGateAsync(r, ct));
        g.MapPut("/{id:guid}", (Guid id, SaveGateDefinitionRequest r, IOpportunityGateService s, CancellationToken ct) => s.UpdateGateAsync(id, r, ct));
        var gr = v1.MapGroup("/crm/gate-reviews").WithTags("Business development");
        gr.MapGet("/waiting", (IOpportunityGateService s, CancellationToken ct) => s.ListWaitingOnMeAsync(ct));
        gr.MapPost("/{id:guid}/approve", (Guid id, DecideGateRequest r, IOpportunityGateService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        gr.MapPost("/{id:guid}/reject", (Guid id, DecideGateRequest r, IOpportunityGateService s, CancellationToken ct) => s.RejectAsync(id, r, ct));
        gr.MapPost("/{id:guid}/cancel", (Guid id, IOpportunityGateService s, CancellationToken ct) => s.CancelAsync(id, ct));

        var a = v1.MapGroup("/crm/accounts").WithTags("Business development");
        a.MapGet("", (CrmAccountKind? kind, string? search, ICrmAccountService s, CancellationToken ct) => s.ListAccountsAsync(kind, search, ct));
        a.MapGet("/{id:guid}", (Guid id, ICrmAccountService s, CancellationToken ct) => s.GetAccountAsync(id, ct));
        a.MapPost("", (SaveCrmAccountRequest r, ICrmAccountService s, CancellationToken ct) => s.CreateAccountAsync(r, ct));
        a.MapPut("/{id:guid}", (Guid id, SaveCrmAccountRequest r, ICrmAccountService s, CancellationToken ct) => s.UpdateAccountAsync(id, r, ct));
        a.MapPut("/{id:guid}/customer", (Guid id, LinkCustomerBody r, ICrmAccountService s, CancellationToken ct) => s.LinkCustomerAsync(id, r.CustomerId, ct));
        var c = v1.MapGroup("/crm/contacts").WithTags("Business development");
        c.MapGet("", (Guid? accountId, string? search, ICrmAccountService s, CancellationToken ct) => s.ListContactsAsync(accountId, search, ct));
        c.MapPost("", (SaveCrmContactRequest r, ICrmAccountService s, CancellationToken ct) => s.CreateContactAsync(r, ct));
        c.MapPut("/{id:guid}", (Guid id, SaveCrmContactRequest r, ICrmAccountService s, CancellationToken ct) => s.UpdateContactAsync(id, r, ct));
        var act = v1.MapGroup("/crm/activities").WithTags("Business development");
        act.MapPost("/search", (CrmActivityFilter f, ICrmAccountService s, CancellationToken ct) => s.ListActivitiesAsync(f, ct));
        act.MapPost("", (SaveCrmActivityRequest r, ICrmAccountService s, CancellationToken ct) => s.LogActivityAsync(r, ct));
        act.MapPost("/{id:guid}/done", (Guid id, bool done, ICrmAccountService s, CancellationToken ct) => s.SetTaskDoneAsync(id, done, ct));

        var l = v1.MapGroup("/crm/leads").WithTags("Business development");
        l.MapGet("", (LeadStatus? status, bool? all, IOpportunityLeadService s, CancellationToken ct) => s.ListLeadsAsync(all == true ? null : status ?? LeadStatus.New, ct));
        l.MapPost("/score", (bool? all, IOpportunityLeadService s, CancellationToken ct) => s.ScoreLeadsAsync(all == true, ct));
        l.MapPost("/import", (CsvBody r, IOpportunityLeadService s, CancellationToken ct) => s.ImportCsvAsync(r.Csv, ct));
        l.MapPost("/{id:guid}/accept", (Guid id, AcceptLeadRequest r, IOpportunityLeadService s, CancellationToken ct) => s.AcceptAsync(id, r, ct));
        l.MapPost("/{id:guid}/dismiss", (Guid id, IOpportunityLeadService s, CancellationToken ct) => s.DismissAsync(id, ct));
        var ss = v1.MapGroup("/crm/lead-searches").WithTags("Business development");
        ss.MapGet("", (IOpportunityLeadService s, CancellationToken ct) => s.ListSearchesAsync(ct));
        ss.MapPost("", (SaveLeadSearchRequest r, IOpportunityLeadService s, CancellationToken ct) => s.CreateSearchAsync(r, ct));
        ss.MapPut("/{id:guid}", (Guid id, SaveLeadSearchRequest r, IOpportunityLeadService s, CancellationToken ct) => s.UpdateSearchAsync(id, r, ct));
        ss.MapDelete("/{id:guid}", async (Guid id, IOpportunityLeadService s, CancellationToken ct) =>
        {
            await s.DeleteSearchAsync(id, ct);
            return Results.NoContent();
        });
        ss.MapPost("/{id:guid}/run", (Guid id, IOpportunityLeadService s, CancellationToken ct) => s.RunSearchAsync(id, ct));

        var p = v1.MapGroup("/crm/pipeline").WithTags("Business development");
        p.MapGet("/board", (bool? includeOutcomes, IPipelineService s, CancellationToken ct) => s.GetBoardAsync(includeOutcomes ?? false, ct));
        p.MapGet("/staffing-demand", (DateOnly from, int? weeks, decimal? revenuePerHour, IPipelineService s, CancellationToken ct) =>
            s.GetStaffingDemandAsync(from, weeks ?? 12, revenuePerHour ?? 150m, ct));
        p.MapGet("/forecast", (IPipelineService s, CancellationToken ct) => s.GetForecastAsync(ct));
        p.MapGet("/highlights", (int? windowDays, IPipelineService s, CancellationToken ct) => s.GetHighlightsAsync(windowDays ?? 30, ct));
        p.MapGet("/win-loss", (DateOnly? from, DateOnly? to, IPipelineService s, CancellationToken ct) => s.GetWinLossAsync(from, to, ct));
        p.MapGet("/bp-spend", (Guid? opportunityId, IPipelineService s, CancellationToken ct) => s.GetBpSpendAsync(opportunityId, ct));
        p.MapGet("/settings", (IPipelineService s, CancellationToken ct) => s.GetSettingsAsync(ct));
        p.MapPut("/settings/company-profile", (CompanyProfileBody r, IPipelineService s, CancellationToken ct) => s.SetCompanyProfileAsync(r.Profile, ct));
        p.MapPut("/settings/bp-parent", (BpParentBody r, IPipelineService s, CancellationToken ct) => s.SetBpParentAsync(r.WbsElementId, ct));
        p.MapGet("/settings/bp-parent-options", (IPipelineService s, CancellationToken ct) => s.ListBpParentOptionsAsync(ct));
    }
}
