using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Migration;
using Workbench.Contracts.Payroll;

namespace Workbench.Api;

internal static class MigrationEndpoints
{
    public static void MapMigrationEndpoints(this RouteGroupBuilder v1)
    {
        var migration = v1.MapGroup("/migration").WithTags("Migration");
        migration.MapGet("/checklist", (IMigrationService s, CancellationToken ct) => s.GetChecklistAsync(ct));
        migration.MapPut("/settings", (SaveMigrationSettingsRequest r, IMigrationService s, CancellationToken ct) => s.SaveSettingsAsync(r, ct));
        migration.MapPut("/steps/{key}", (string key, SetMigrationStepRequest r, IMigrationService s, CancellationToken ct) => s.SetStepAsync(key, r, ct));

        var recon = migration.MapGroup("/reconciliation");
        recon.MapGet("/reports", (IReconciliationService s, CancellationToken ct) => s.ListReportsAsync(ct));
        recon.MapGet("/mappings", (ReconReportKind? kind, IReconciliationService s, CancellationToken ct) => s.ListMappingsAsync(kind, ct));
        recon.MapPut("/mappings", (ReconMappingDto r, IReconciliationService s, CancellationToken ct) => s.SaveMappingAsync(r, ct));
        recon.MapDelete("/mappings/{id:guid}", async (Guid id, IReconciliationService s, CancellationToken ct) =>
        {
            await s.DeleteMappingAsync(id, ct);
            return Results.NoContent();
        });
        recon.MapPost("/preview", (PreviewReconFileRequest r, IReconciliationService s, CancellationToken ct) => s.PreviewFileAsync(r, ct));
        recon.MapPost("/runs", (RunReconciliationRequest r, IReconciliationService s, CancellationToken ct) => s.RunAsync(r, ct));
        recon.MapGet("/runs", (Guid? periodId, IReconciliationService s, CancellationToken ct) => s.ListRunsAsync(periodId, ct));
        recon.MapGet("/runs/{id:guid}", (Guid id, IReconciliationService s, CancellationToken ct) => s.GetRunAsync(id, ct));
        recon.MapGet("/runs/{id:guid}/drill", (Guid id, string key, IReconciliationService s, CancellationToken ct) => s.DrillDownAsync(id, key, ct));
        recon.MapPost("/runs/{id:guid}/sign-off", (Guid id, SignOffReconciliationRequest r, IReconciliationService s, CancellationToken ct) => s.SignOffAsync(id, r, ct));
        recon.MapGet("/runs/{id:guid}/export", (Guid id, IReconciliationService s, CancellationToken ct) => s.ExportAsync(id, ct));
        recon.MapGet("/status/{periodId:guid}", (Guid periodId, IReconciliationService s, CancellationToken ct) => s.GetStatusAsync(periodId, ct));

        var layouts = v1.MapGroup("/payroll/layouts").WithTags("Payroll");
        layouts.MapGet("/{provider}", (string provider, IPayrollLayoutService s, CancellationToken ct) => s.GetAsync(provider, ct));
        layouts.MapGet("/{provider}/default", (string provider, IPayrollLayoutService s, CancellationToken ct) => s.GetDefaultAsync(provider, ct));
        layouts.MapPut("/{provider}", (string provider, SavePayrollLayoutRequest r, IPayrollLayoutService s, CancellationToken ct) => s.SaveAsync(provider, r, ct));
        layouts.MapDelete("/{provider}", (string provider, IPayrollLayoutService s, CancellationToken ct) => s.ResetAsync(provider, ct));
        layouts.MapPost("/{provider}/test-register", (string provider, TestRegisterSampleRequest r, IPayrollLayoutService s, CancellationToken ct) => s.TestRegisterAsync(provider, r, ct));
        layouts.MapPost("/{provider}/test-hours", (string provider, TestHoursTemplateRequest r, IPayrollLayoutService s, CancellationToken ct) => s.TestHoursTemplateAsync(provider, r, ct));
    }
}
