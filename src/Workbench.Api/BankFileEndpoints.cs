using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.BankFiles;

namespace Workbench.Api;

internal static class BankFileEndpoints
{
    public static void MapBankFileEndpoints(this RouteGroupBuilder v1)
    {
        var vendors = v1.MapGroup("/vendor-bank").WithTags("Vendor bank details").AddEndpointFilter(MapConflicts);
        vendors.MapGet("/changes/pending", (IVendorBankService s, CancellationToken ct) => s.ListPendingAsync(ct));
        vendors.MapGet("/{vendorId:guid}", (Guid vendorId, IVendorBankService s, CancellationToken ct) => s.GetAsync(vendorId, ct));
        vendors.MapPost("/{vendorId:guid}/changes", (Guid vendorId, RequestVendorBankChangeRequest r, IVendorBankService s, CancellationToken ct) => s.RequestChangeAsync(vendorId, r, ct));
        vendors.MapPost("/{vendorId:guid}/reveal", (Guid vendorId, IVendorBankService s, CancellationToken ct) => s.RevealAsync(vendorId, ct));
        vendors.MapPost("/changes/{changeId:guid}/approve", (Guid changeId, DecideVendorBankChangeRequest r, IVendorBankService s, CancellationToken ct) => s.ApproveChangeAsync(changeId, r, ct));
        vendors.MapPost("/changes/{changeId:guid}/reject", (Guid changeId, DecideVendorBankChangeRequest r, IVendorBankService s, CancellationToken ct) => s.RejectChangeAsync(changeId, r, ct));
        vendors.MapPost("/changes/{changeId:guid}/cancel", (Guid changeId, IVendorBankService s, CancellationToken ct) => s.CancelChangeAsync(changeId, ct));

        var files = v1.MapGroup("/bank-files").WithTags("Bank payment files").AddEndpointFilter(MapConflicts);
        files.MapGet("/accounts", (IPaymentFileService s, CancellationToken ct) => s.ListBankAccountsAsync(ct));
        files.MapGet("/accounts/{bankAccountId:guid}", (Guid bankAccountId, IPaymentFileService s, CancellationToken ct) => s.GetSettingsAsync(bankAccountId, ct));
        files.MapPut("/accounts/{bankAccountId:guid}", (Guid bankAccountId, SaveBankFileSettingsRequest r, IPaymentFileService s, CancellationToken ct) => s.SaveSettingsAsync(bankAccountId, r, ct));
        files.MapGet("/layouts", (IPaymentFileService s, CancellationToken ct) => s.ListLayoutsAsync(ct));
        files.MapPost("/layouts", (SavePositivePayLayoutRequest r, IPaymentFileService s, CancellationToken ct) => s.SaveLayoutAsync(null, r, ct));
        files.MapPut("/layouts/{layoutId:guid}", (Guid layoutId, SavePositivePayLayoutRequest r, IPaymentFileService s, CancellationToken ct) => s.SaveLayoutAsync(layoutId, r, ct));
        files.MapGet("/runs/{runId:guid}", (Guid runId, IPaymentFileService s, CancellationToken ct) => s.GetRunFilesAsync(runId, ct));
        files.MapPost("/runs/{runId:guid}/files", (Guid runId, GeneratePaymentFileRequest r, IPaymentFileService s, CancellationToken ct) => s.GenerateAsync(runId, r, ct));
        files.MapPost("/runs/{runId:guid}/remittance", (Guid runId, IPaymentFileService s, CancellationToken ct) => s.SendRemittanceAsync(runId, ct));
    }

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
