using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Receipts;
using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Receipts;
using Workbench.Domain.Files;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Identity;

/// <summary>
/// Receipt library. Everyone signed in has their own library and sees only it (the service lists, changes and deletes the
/// caller's own files). A file someone else uploaded can be opened only through a line it's attached to that the caller
/// can see: the expense report's employee, supervisor and approvers (<see cref="IExpenseReportService.GetReportAsync"/>),
/// and Finance, administrators and the project's manager (<see cref="IOdcService.GetAsync"/>). Anything else is "not found".
/// </summary>
public static class ReceiptPermissionRegistration
{
    public static IServiceCollection AddWorkbenchReceiptPermissions(this IServiceCollection services)
    {
        services.Decorate<IReceiptLibraryService, PermissionCheckingReceiptLibraryService>();
        return services;
    }
}

public sealed class PermissionCheckingReceiptLibraryService(
    IReceiptLibraryService inner,
    AccessControl access,
    IWorkbenchDbContextFactory dbFactory,
    IOdcService odcs,
    IExpenseReportService reports) : IReceiptLibraryService
{
    public Task<IReadOnlyList<ReceiptDto>> ListMineAsync(ReceiptQuery? query = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListMineAsync(query, cancellationToken);
    }

    public async Task<ReceiptDto> GetAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await RequireViewAsync(fileId, cancellationToken);
        return await inner.GetAsync(fileId, cancellationToken);
    }

    public Task<ReceiptReadingDto> UploadAsync(string fileName, Stream content, Guid? expenseReportId = null, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.UploadAsync(fileName, content, expenseReportId, cancellationToken);
    }

    public async Task<ReceiptReadingDto> ReadAsync(Guid fileId, Guid? expenseReportId = null, CancellationToken cancellationToken = default)
    {
        await RequireViewAsync(fileId, cancellationToken);
        return await inner.ReadAsync(fileId, expenseReportId, cancellationToken);
    }

    public Task<LodgingRateCheckDto> CheckLodgingAsync(LodgingRateCheckRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.CheckLodgingAsync(request, cancellationToken);
    }

    public Task<ReceiptDto> UpdateAsync(Guid fileId, UpdateReceiptRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn(); // The service changes only the caller's own files.
        return inner.UpdateAsync(fileId, request, cancellationToken);
    }

    public Task DeleteAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn(); // The service deletes only the caller's own files.
        return inner.DeleteAsync(fileId, cancellationToken);
    }

    public async Task<ReceiptContent> OpenAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        await RequireViewAsync(fileId, cancellationToken);
        return await inner.OpenAsync(fileId, cancellationToken);
    }

    public string GetContentUrl(Guid fileId) => inner.GetContentUrl(fileId);

    private async Task RequireViewAsync(Guid fileId, CancellationToken ct)
    {
        access.RequireSignedIn();
        var me = AccessControl.Normalize(access.Email);
        List<(Guid LineId, Guid? ReportId)> lines;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            var document = await db.Set<ReceiptDocument>().AsNoTracking().FirstOrDefaultAsync(d => d.FileId == fileId, ct);
            if (document is { RemovedAtUtc: null } && document.OwnerEmail == me)
            {
                return;
            }

            if (document is null && await db.Set<StoredFile>().AsNoTracking()
                    .AnyAsync(f => f.Id == fileId && f.OwnerType == ReceiptLibrary.LineOwnerType && f.UploadedBy.ToLower() == me, ct))
            {
                return;
            }

            lines = (await db.Set<OdcEntry>().AsNoTracking().Where(o => o.ReceiptAttachmentId == fileId)
                .Select(o => new { o.Id, o.ExpenseReportId }).ToListAsync(ct))
                .Select(o => (o.Id, o.ExpenseReportId)).ToList();
        }

        foreach (var reportId in lines.Where(l => l.ReportId is not null).Select(l => l.ReportId!.Value).Distinct())
        {
            if (await CanAsync(() => reports.GetReportAsync(reportId, ct)))
            {
                return;
            }
        }

        foreach (var (lineId, _) in lines)
        {
            if (await CanAsync(async () => await odcs.GetAsync(lineId, ct) ?? throw WorkbenchNotFoundException.For("ODC", lineId)))
            {
                return;
            }
        }

        throw WorkbenchNotFoundException.For("Receipt", fileId);
    }

    private static async Task<bool> CanAsync<T>(Func<Task<T>> read)
    {
        try
        {
            await read();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WorkbenchNotFoundException)
        {
            return false;
        }
    }
}
