using Workbench.Contracts.Imports;

namespace Workbench.Application.Identity;

/// <summary>
/// Imports are Finance's (Finance or Admin): they create people, rates, contracts and budgets in bulk. The services an
/// import calls still check their own permissions, so a sheet import also needs edit access to a workspace.
/// </summary>
public sealed class PermissionCheckingImportService(IImportService inner, AccessControl access) : IImportService
{
    private const string What = "import data";

    private Task RequireAsync(CancellationToken ct) => access.RequireFinanceAsync(What, ct);

    public async Task<IReadOnlyList<ImportKindDto>> ListKindsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListKindsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ImportJobDto>> ListJobsAsync(CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListJobsAsync(cancellationToken);
    }

    public async Task<ImportJobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.GetJobAsync(jobId, cancellationToken);
    }

    public async Task<ImportJobDetailDto> UploadAsync(UploadImportRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.UploadAsync(request, cancellationToken);
    }

    public async Task<ImportJobDetailDto> ConnectSmartsheetAsync(ConnectSmartsheetRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ConnectSmartsheetAsync(request, cancellationToken);
    }

    public async Task<ImportJobDetailDto> SelectSmartsheetSheetsAsync(Guid jobId, SelectSmartsheetSheetsRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SelectSmartsheetSheetsAsync(jobId, request, cancellationToken);
    }

    public async Task<ImportJobDetailDto> SaveMappingAsync(Guid jobId, SaveImportMappingRequest request, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.SaveMappingAsync(jobId, request, cancellationToken);
    }

    public async Task<ImportJobDetailDto> ValidateAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ValidateAsync(jobId, cancellationToken);
    }

    public async Task<ImportJobDetailDto> DryRunAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.DryRunAsync(jobId, cancellationToken);
    }

    public async Task<ImportJobDetailDto> ImportAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ImportAsync(jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<ImportRecordDto>> ListRecordsAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.ListRecordsAsync(jobId, cancellationToken);
    }

    public async Task<RollbackCheckDto> CheckRollbackAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.CheckRollbackAsync(jobId, cancellationToken);
    }

    public async Task<ImportJobDetailDto> RollBackAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        return await inner.RollBackAsync(jobId, cancellationToken);
    }

    public async Task DeleteJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(cancellationToken);
        await inner.DeleteJobAsync(jobId, cancellationToken);
    }
}
