using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Forms;
using Workbench.Contracts.Sheets;

namespace Workbench.Api;

/// <summary>Wave E: linked rows, cross-sheet references and formula help, templates and copies, cross-sheet reports, forms.</summary>
internal static class SheetExtrasEndpoints
{
    /// <summary>The body of a formula check.</summary>
    internal sealed record FormulaCheckRequest(string Formula);

    public static void MapSheetExtrasEndpoints(this RouteGroupBuilder v1)
    {
        var sheets = v1.MapGroup("/sheets").WithTags("Sheets");

        // Links and lookups
        sheets.MapGet("/{sheetId:guid}/links", (Guid sheetId, ISheetLinkService service, CancellationToken ct) => service.GetAsync(sheetId, ct));
        sheets.MapGet("/linkable", (ISheetLinkService service, CancellationToken ct) => service.ListSheetsAsync(ct));

        // Cross-sheet references and formula help
        sheets.MapGet("/{sheetId:guid}/references", (Guid sheetId, ISheetFormulaService service, CancellationToken ct) => service.ListReferencesAsync(sheetId, ct));
        sheets.MapPost("/{sheetId:guid}/references", async (Guid sheetId, CreateSheetReferenceRequest request, ISheetFormulaService service, CancellationToken ct) =>
        {
            var created = await service.CreateReferenceAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{sheetId}/references/{created.Id}", created);
        });
        sheets.MapDelete("/{sheetId:guid}/references/{referenceId:guid}", async (Guid sheetId, Guid referenceId, ISheetFormulaService service, CancellationToken ct) =>
        {
            await service.DeleteReferenceAsync(sheetId, referenceId, ct);
            return Results.NoContent();
        });
        sheets.MapPost("/{sheetId:guid}/formula-check", (Guid sheetId, FormulaCheckRequest request, ISheetFormulaService service, CancellationToken ct) =>
            service.CheckAsync(sheetId, request.Formula, ct));
        sheets.MapGet("/formula-functions", (ISheetFormulaService service, CancellationToken ct) => service.ListFunctionsAsync(ct));

        // Templates and copies
        var templates = sheets.MapGroup("/templates");
        templates.MapGet("/", (ISheetTemplateService service, CancellationToken ct) => service.ListAsync(ct));
        templates.MapPost("/create-sheet", async (CreateSheetFromTemplateRequest request, ISheetTemplateService service, CancellationToken ct) =>
        {
            var created = await service.CreateSheetAsync(request, ct);
            return Results.Created($"/api/v1/sheets/{created.Id}", created);
        });
        templates.MapDelete("/{templateId:guid}", async (Guid templateId, ISheetTemplateService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(templateId, ct);
            return Results.NoContent();
        });
        sheets.MapPost("/{sheetId:guid}/save-as-template", (Guid sheetId, SaveSheetTemplateRequest request, ISheetTemplateService service, CancellationToken ct) =>
            service.SaveAsTemplateAsync(sheetId, request, ct));
        sheets.MapPost("/{sheetId:guid}/copy", async (Guid sheetId, CopySheetRequest request, ISheetTemplateService service, CancellationToken ct) =>
        {
            var created = await service.CopyAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/{created.Id}", created);
        });

        // Cross-sheet reports
        var reports = sheets.MapGroup("/reports");
        reports.MapGet("/", (ISheetReportService service, CancellationToken ct) => service.ListAsync(ct));
        reports.MapGet("/{reportId:guid}", async (Guid reportId, ISheetReportService service, CancellationToken ct) =>
            await service.GetAsync(reportId, ct) is { } report ? Results.Ok(report) : Results.NotFound());
        reports.MapPost("/", async (SaveSheetReportRequest request, ISheetReportService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(request, ct);
            return Results.Created($"/api/v1/sheets/reports/{created.Id}", created);
        });
        reports.MapPut("/{reportId:guid}", (Guid reportId, SaveSheetReportRequest request, ISheetReportService service, CancellationToken ct) =>
            service.UpdateAsync(reportId, request, ct));
        reports.MapDelete("/{reportId:guid}", async (Guid reportId, ISheetReportService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(reportId, ct);
            return Results.NoContent();
        });
        reports.MapGet("/{reportId:guid}/run", (Guid reportId, ISheetReportService service, CancellationToken ct) => service.RunAsync(reportId, ct));
        reports.MapPost("/preview", (SaveSheetReportRequest request, ISheetReportService service, CancellationToken ct) => service.PreviewAsync(request, ct));

        // Forms (the public link is served by the web host at /f/{token}, not here)
        sheets.MapGet("/{sheetId:guid}/forms", (Guid sheetId, IFormService service, CancellationToken ct) => service.ListAsync(sheetId, ct));
        sheets.MapPost("/{sheetId:guid}/forms", async (Guid sheetId, SaveFormRequest request, IFormService service, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(sheetId, request, ct);
            return Results.Created($"/api/v1/sheets/forms/{created.Id}", created);
        });
        sheets.MapGet("/{sheetId:guid}/forms/can-publish", (Guid sheetId, IFormService service, CancellationToken ct) => service.CanPublishAsync(sheetId, ct));
        var forms = sheets.MapGroup("/forms");
        forms.MapPut("/{formId:guid}", (Guid formId, SaveFormRequest request, IFormService service, CancellationToken ct) => service.UpdateAsync(formId, request, ct));
        forms.MapPost("/{formId:guid}/reset-link", (Guid formId, IFormService service, CancellationToken ct) => service.ResetPublicLinkAsync(formId, ct));
        forms.MapDelete("/{formId:guid}", async (Guid formId, IFormService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(formId, ct);
            return Results.NoContent();
        });
        forms.MapGet("/{formId:guid}/fill", (Guid formId, IFormService service, CancellationToken ct) => service.GetForFillingAsync(formId, ct));
        forms.MapPost("/{formId:guid}/submit", (Guid formId, SubmitFormRequest request, IFormService service, CancellationToken ct) => service.SubmitAsync(formId, request, ct));
    }
}
