using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Hr;

namespace Workbench.Api;

internal static class HrEndpoints
{
    public static void MapHrEndpoints(this RouteGroupBuilder v1)
    {
        var hr = v1.MapGroup("/hr").WithTags("HR");
        hr.MapGet("/access", (IHrAccessService s, CancellationToken ct) => s.GetAsync(ct));

        // Personnel
        hr.MapGet("/employees", (string? search, bool? includeTerminated, Guid? orgUnitId, IHrPersonnelService s, CancellationToken ct) =>
            s.ListAsync(new EmployeeQuery(search, includeTerminated ?? false, orgUnitId), ct));
        hr.MapGet("/employees/me", (IHrPersonnelService s, CancellationToken ct) => s.GetMineAsync(ct));
        hr.MapGet("/employees/{id:guid}", (Guid id, IHrPersonnelService s, CancellationToken ct) => s.GetAsync(id, ct));
        hr.MapPut("/employees/{id:guid}/job", (Guid id, SaveJobInfoRequest r, IHrPersonnelService s, CancellationToken ct) => s.SaveJobAsync(id, r, ct));
        hr.MapPut("/employees/{id:guid}/personal", (Guid id, SavePersonalInfoRequest r, IHrPersonnelService s, CancellationToken ct) => s.SavePersonalAsync(id, r, ct));
        hr.MapPut("/employees/{id:guid}/emergency-contacts", (Guid id, List<SaveEmergencyContact> r, IHrPersonnelService s, CancellationToken ct) =>
            s.SaveEmergencyContactsAsync(id, r, ct));
        hr.MapGet("/employees/{id:guid}/eeo", (Guid id, IHrPersonnelService s, CancellationToken ct) => s.GetEeoAsync(id, ct));
        hr.MapPut("/employees/{id:guid}/eeo", (Guid id, SaveEeoRequest r, IHrPersonnelService s, CancellationToken ct) => s.SaveEeoAsync(id, r, ct));
        hr.MapPost("/employees/{id:guid}/credentials", (Guid id, SaveCredentialRequest r, IHrPersonnelService s, CancellationToken ct) => s.SaveCredentialAsync(id, null, r, ct));
        hr.MapPut("/employees/{id:guid}/credentials/{credentialId:guid}", (Guid id, Guid credentialId, SaveCredentialRequest r, IHrPersonnelService s, CancellationToken ct) =>
            s.SaveCredentialAsync(id, credentialId, r, ct));
        hr.MapDelete("/credentials/{credentialId:guid}", async (Guid credentialId, IHrPersonnelService s, CancellationToken ct) =>
        {
            await s.DeleteCredentialAsync(credentialId, ct);
            return Results.NoContent();
        });
        hr.MapGet("/credentials/expiring", (int? days, IHrPersonnelService s, CancellationToken ct) => s.ListExpiringCredentialsAsync(days ?? 60, ct));

        // Multipart upload with a "file" field plus title, category and employeeVisible form fields.
        hr.MapPost("/employees/{id:guid}/documents", async (Guid id, HttpRequest http, IHrPersonnelService s, CancellationToken ct) =>
        {
            if (!http.HasFormContentType)
            {
                return MissingFile();
            }

            var form = await http.ReadFormAsync(ct);
            if (form.Files.GetFile("file") is not { } file)
            {
                return MissingFile();
            }

            if (file.Length > AttachmentRules.MaxSizeBytes)
            {
                throw new ArgumentException($"Files can be at most {AttachmentRules.MaxSizeBytes / (1024 * 1024)} MB.");
            }

            await using var stream = file.OpenReadStream();
            return Results.Ok(await s.UploadDocumentAsync(id, form["title"].ToString(), form["category"].ToString(),
                string.Equals(form["employeeVisible"].ToString(), "true", StringComparison.OrdinalIgnoreCase), file.FileName, stream, ct));
        }).DisableAntiforgery();
        hr.MapGet("/documents/{documentId:guid}", async (Guid documentId, IHrPersonnelService s, CancellationToken ct) =>
        {
            var doc = await s.OpenDocumentAsync(documentId, ct);

            // Always a download, never rendered inline, so an uploaded file can't run script on this origin.
            return Results.File(doc.Content, doc.ContentType, doc.FileName);
        });

        hr.MapGet("/requests", (bool? pendingOnly, IHrPersonnelService s, CancellationToken ct) => s.ListChangeRequestsAsync(pendingOnly ?? true, ct));
        hr.MapPost("/requests", (SubmitHrChangeRequest r, IHrPersonnelService s, CancellationToken ct) => s.RequestChangeAsync(r, ct));
        hr.MapPost("/requests/{id:guid}/approve", (Guid id, HrDecisionRequest r, IHrPersonnelService s, CancellationToken ct) => s.ApproveChangeAsync(id, r, ct));
        hr.MapPost("/requests/{id:guid}/reject", (Guid id, HrDecisionRequest r, IHrPersonnelService s, CancellationToken ct) => s.RejectChangeAsync(id, r, ct));
        hr.MapPost("/requests/{id:guid}/cancel", (Guid id, IHrPersonnelService s, CancellationToken ct) => s.CancelChangeAsync(id, ct));
        hr.MapGet("/org-chart", (IHrPersonnelService s, CancellationToken ct) => s.GetOrgChartAsync(ct));

        // Positions
        hr.MapGet("/positions", (bool? includeClosed, IPositionService s, CancellationToken ct) => s.ListAsync(includeClosed ?? false, ct));
        hr.MapGet("/positions/{id:guid}", (Guid id, IPositionService s, CancellationToken ct) => s.GetAsync(id, ct));
        hr.MapPost("/positions", (SavePositionRequest r, IPositionService s, CancellationToken ct) => s.CreateAsync(r, ct));
        hr.MapPut("/positions/{id:guid}", (Guid id, SavePositionRequest r, IPositionService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));

        // Compensation
        hr.MapGet("/compensation/pending", (ICompensationService s, CancellationToken ct) => s.ListPendingAsync(ct));
        hr.MapGet("/compensation/employees/{id:guid}", (Guid id, ICompensationService s, CancellationToken ct) => s.GetHistoryAsync(id, ct));
        hr.MapPost("/compensation/employees/{id:guid}", (Guid id, ProposeCompensationRequest r, ICompensationService s, CancellationToken ct) => s.ProposeAsync(id, r, ct));
        hr.MapPost("/compensation/{id:guid}/approve", (Guid id, CompensationDecisionRequest r, ICompensationService s, CancellationToken ct) => s.ApproveAsync(id, r, ct));
        hr.MapPost("/compensation/{id:guid}/reject", (Guid id, CompensationDecisionRequest r, ICompensationService s, CancellationToken ct) => s.RejectAsync(id, r, ct));
        hr.MapPost("/compensation/{id:guid}/cancel", (Guid id, ICompensationService s, CancellationToken ct) => s.CancelAsync(id, ct));
        hr.MapGet("/merit-cycles", (ICompensationService s, CancellationToken ct) => s.ListCyclesAsync(ct));
        hr.MapGet("/merit-cycles/{id:guid}", (Guid id, ICompensationService s, CancellationToken ct) => s.GetCycleAsync(id, ct));
        hr.MapPost("/merit-cycles", (SaveMeritCycleRequest r, ICompensationService s, CancellationToken ct) => s.CreateCycleAsync(r, ct));
        hr.MapPost("/merit-cycles/{id:guid}/proposals", (Guid id, AddMeritProposalsRequest r, ICompensationService s, CancellationToken ct) => s.AddProposalsAsync(id, r, ct));
        hr.MapPost("/merit-cycles/{id:guid}/submit", (Guid id, ICompensationService s, CancellationToken ct) => s.SubmitCycleAsync(id, ct));

        // Benefits
        hr.MapGet("/benefit-plans", (bool? includeInactive, IBenefitService s, CancellationToken ct) => s.ListPlansAsync(includeInactive ?? false, ct));
        hr.MapPost("/benefit-plans", (SaveBenefitPlanRequest r, IBenefitService s, CancellationToken ct) => s.SavePlanAsync(null, r, ct));
        hr.MapPut("/benefit-plans/{id:guid}", (Guid id, SaveBenefitPlanRequest r, IBenefitService s, CancellationToken ct) => s.SavePlanAsync(id, r, ct));
        hr.MapPut("/benefit-plans/{id:guid}/account", (Guid id, SetPlanAccountRequest r, IBenefitService s, CancellationToken ct) => s.SetPlanAccountAsync(id, r.ExpenseAccountId, ct));
        hr.MapGet("/benefit-accounts", (IBenefitService s, CancellationToken ct) => s.ListExpenseAccountsAsync(ct));
        hr.MapGet("/enrollment-windows", (IBenefitService s, CancellationToken ct) => s.ListWindowsAsync(ct));
        hr.MapPost("/enrollment-windows", (SaveOpenEnrollmentWindowRequest r, IBenefitService s, CancellationToken ct) => s.SaveWindowAsync(null, r, ct));
        hr.MapPut("/enrollment-windows/{id:guid}", (Guid id, SaveOpenEnrollmentWindowRequest r, IBenefitService s, CancellationToken ct) => s.SaveWindowAsync(id, r, ct));
        hr.MapGet("/benefits/me", (IBenefitService s, CancellationToken ct) => s.GetMineAsync(ct));
        hr.MapGet("/benefits/employees/{id:guid}", (Guid id, IBenefitService s, CancellationToken ct) => s.GetForEmployeeAsync(id, ct));
        hr.MapPost("/benefits/employees/{id:guid}/dependents", (Guid id, SaveDependentRequest r, IBenefitService s, CancellationToken ct) => s.SaveDependentAsync(id, null, r, ct));
        hr.MapPut("/benefits/employees/{id:guid}/dependents/{dependentId:guid}", (Guid id, Guid dependentId, SaveDependentRequest r, IBenefitService s, CancellationToken ct) =>
            s.SaveDependentAsync(id, dependentId, r, ct));
        hr.MapPost("/benefits/employees/{id:guid}/elections", (Guid id, ElectBenefitRequest r, IBenefitService s, CancellationToken ct) => s.ElectAsync(id, r, ct));
        hr.MapPost("/benefits/employees/{id:guid}/life-events", (Guid id, ReportLifeEventRequest r, IBenefitService s, CancellationToken ct) => s.ReportLifeEventAsync(id, r, ct));
        hr.MapGet("/life-events/pending", (IBenefitService s, CancellationToken ct) => s.ListPendingLifeEventsAsync(ct));
        hr.MapPost("/life-events/{id:guid}/decide", (Guid id, DecideLifeEventRequest r, IBenefitService s, CancellationToken ct) => s.DecideLifeEventAsync(id, r, ct));
        hr.MapGet("/deductions/periods", (IBenefitDeductionService s, CancellationToken ct) => s.ListPeriodsAsync(ct));
        hr.MapGet("/deductions/{payGroupId:guid}/{payPeriodId:guid}", (Guid payGroupId, Guid payPeriodId, IBenefitDeductionService s, CancellationToken ct) =>
            s.PreviewAsync(payGroupId, payPeriodId, ct));
        hr.MapPost("/deductions/{payGroupId:guid}/{payPeriodId:guid}/export", (Guid payGroupId, Guid payPeriodId, IBenefitDeductionService s, CancellationToken ct) =>
            s.ExportAsync(payGroupId, payPeriodId, ct));

        // Onboarding and offboarding
        hr.MapGet("/checklist-templates", (IOnboardingService s, CancellationToken ct) => s.ListTemplatesAsync(ct));
        hr.MapPost("/checklist-templates", (SaveChecklistTemplateRequest r, IOnboardingService s, CancellationToken ct) => s.SaveTemplateAsync(null, r, ct));
        hr.MapPut("/checklist-templates/{id:guid}", (Guid id, SaveChecklistTemplateRequest r, IOnboardingService s, CancellationToken ct) => s.SaveTemplateAsync(id, r, ct));
        hr.MapGet("/checklists", (bool? openOnly, IOnboardingService s, CancellationToken ct) => s.ListAsync(openOnly ?? true, ct));
        hr.MapGet("/checklists/mine", (IOnboardingService s, CancellationToken ct) => s.ListMyTasksAsync(ct));
        hr.MapGet("/checklists/{id:guid}", (Guid id, IOnboardingService s, CancellationToken ct) => s.GetAsync(id, ct));
        hr.MapPost("/checklists/onboarding", (StartOnboardingRequest r, IOnboardingService s, CancellationToken ct) => s.StartOnboardingAsync(r, ct));
        hr.MapPost("/checklists/offboarding", (StartOffboardingRequest r, IOnboardingService s, CancellationToken ct) => s.StartOffboardingAsync(r, ct));
        hr.MapPost("/checklist-tasks/{id:guid}/complete", (Guid id, CompleteChecklistTaskRequest r, IOnboardingService s, CancellationToken ct) => s.CompleteTaskAsync(id, r, ct));
        hr.MapPost("/checklist-tasks/{id:guid}/skip", (Guid id, CompleteChecklistTaskRequest r, IOnboardingService s, CancellationToken ct) => s.SkipTaskAsync(id, r, ct));

        // Reports
        hr.MapGet("/reports/headcount", (DateOnly asOf, IHrReportService s, CancellationToken ct) => s.GetHeadcountAsync(asOf, ct));
        hr.MapGet("/reports/turnover", (DateOnly from, DateOnly to, IHrReportService s, CancellationToken ct) => s.GetTurnoverAsync(from, to, ct));
        hr.MapGet("/reports/eeo1", (DateOnly asOf, IHrReportService s, CancellationToken ct) => s.GetEeo1Async(asOf, ct));
        hr.MapPost("/reports/eeo1/export", (DateOnly asOf, IHrReportService s, CancellationToken ct) => s.ExportEeo1Async(asOf, ct));
        hr.MapGet("/reports/vets4212", (DateOnly periodEnd, IHrReportService s, CancellationToken ct) => s.GetVets4212Async(periodEnd, ct));
        hr.MapPost("/reports/vets4212/export", (DateOnly periodEnd, IHrReportService s, CancellationToken ct) => s.ExportVets4212Async(periodEnd, ct));
        hr.MapGet("/reports/benefits", (DateOnly asOf, IHrReportService s, CancellationToken ct) => s.GetBenefitSummaryAsync(asOf, ct));
        hr.MapPost("/reports/benefits/export", (DateOnly asOf, IHrReportService s, CancellationToken ct) => s.ExportBenefitSummaryAsync(asOf, ct));
    }

    private static IResult MissingFile() =>
        Results.Problem("Send the file as multipart/form-data in a field named \"file\".", statusCode: StatusCodes.Status400BadRequest);
}
